using System.Collections.Generic;
using FFVM.AST;

namespace FFVM.Compiler
{
    public class CompileResult
    {
        public VMProgram Program;
        public List<string> Errors;
        public bool Success => Errors == null || Errors.Count == 0;
    }

    /// <summary>
    /// Compiles a parsed AST (single function) into a VMProgram (bytecode + constants).
    ///
    /// Register layout:
    ///   r0..r15   — scratch zone: syscall arguments / return values
    ///   r16..r47  — local variables (32 slots)
    ///   r48..r63  — expression temporaries (16 slots)
    /// </summary>
    public class BytecodeCompiler
    {
        private const int VarRegBase = 16;
        private const int TempRegBase = 48;

        private List<Instruction> _instructions;
        private List<Number> _constants;
        private Dictionary<string, int> _variables;   // name → register
        private int _nextVarReg;
        private int _tempTop;
        private Dictionary<string, int> _syscalls;    // name → slot
        private SyscallTable _syscallTable;            // paired slot lookup (optional, for using)
        private List<string> _errors;

        // DBG1: Source Map — parallel to _instructions, records line number for each emitted instruction
        private List<int> _sourceLines;
        private int _currentLine;  // updated from AST nodes during compilation

        // DBG2: Symbol Table — collected during compilation
        private List<SymbolEntry> _symbolEntries;
        private string _currentFunctionName;  // current function being compiled

        // Struct support: compile-time type table
        private Dictionary<string, StructDecl> _structTypes;    // typeName → struct declaration
        private Dictionary<string, string> _structVarTypes;     // varName → struct typeName

        // Multi-function support
        private Dictionary<string, int> _functionTable;  // funcName → entryIP (-1 = not yet compiled)
        private Dictionary<string, FuncDecl> _funcDecls; // funcName → AST for param count lookup
        private bool _isEntryFunction;                   // true when compiling the entry func
        private bool _isLeafFunction;                    // FO1: true when compiling a leaf func
        private int _callerWindowSize;                   // localVarCount for current function

        // FO1: Leaf function analysis — funcName → isLeaf
        private Dictionary<string, bool> _leafFunctions;

        // Forward-reference backpatch: CALL instructions that reference not-yet-compiled functions
        private struct PendingCall
        {
            public int InstructionIP;   // IP of the CALL instruction to backpatch
            public string FunctionName; // target function name
        }
        private List<PendingCall> _pendingCalls;

        // Deferred cleanup blocks (emitted after main body)
        private const int NoReleaseSyscall = -1;
        private struct DeferredCleanup
        {
            public int PushCleanupIP;
            public BlockStmt Body;              // for defer: cleanup block body (null when using)
            public int ReleaseSyscallSlot;      // for using: release syscall slot (NoReleaseSyscall = use Body instead)
        }
        private List<DeferredCleanup> _deferredCleanups;

        // Cleanup block compilation state (G6: prohibit wait/wait_for inside cleanup blocks)
        private bool _inCleanupBlock;

        // F4: Register lifecycle analysis
        private struct LiveRange
        {
            public string Name;
            public int DefOrder;      // declaration order within function
            public int LastUseOrder;  // last reference order
            public bool CrossesAwait; // variable is live across wait/wait_for
            public int FieldCount;    // >0 for struct variables (consecutive registers)
        }
        private Dictionary<string, LiveRange> _liveRanges;  // per-function analysis result
        private List<int> _freeVarRegs;                     // free list for register reuse
        private int _maxVarRegUsed;                         // track max register for precise LocalRegCount
        private int _stmtOrder;                             // current statement order counter for release tracking

        /// <summary>
        /// Compile source text into a VMProgram.
        /// </summary>
        /// <param name="source">Script source code</param>
        /// <param name="entryFunc">Entry function name (typically "main")</param>
        /// <param name="syscalls">Syscall name → slot mapping</param>
        /// <param name="syscallTable">Optional SyscallTable for paired syscall lookup (required for 'using')</param>
        public CompileResult Compile(string source, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable = null)
        {
            var parser = new Parser();
            var module = parser.Parse(source, out var parseErrors);

            if (parseErrors != null && parseErrors.Count > 0)
                return new CompileResult { Errors = parseErrors };

            return CompileModule(module, entryFunc, syscalls, syscallTable);
        }

        /// <summary>
        /// Compile a pre-parsed module into a VMProgram.
        /// Two-pass: (1) scan all functions → build function table; (2) compile entry, then others.
        /// Forward-reference CALL instructions are backpatched after all functions are compiled.
        /// </summary>
        public CompileResult CompileModule(ModuleNode module, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable = null)
        {
            _instructions = new List<Instruction>();
            _constants = new List<Number>();
            _syscalls = syscalls ?? new Dictionary<string, int>();
            _syscallTable = syscallTable;
            _errors = new List<string>();
            _pendingCalls = new List<PendingCall>();
            _sourceLines = new List<int>();
            _currentLine = 0;
            _symbolEntries = new List<SymbolEntry>();

            // --- Build struct type table ---
            _structTypes = new Dictionary<string, StructDecl>();
            for (int i = 0; i < module.Structs.Count; i++)
            {
                var s = module.Structs[i];
                if (_structTypes.ContainsKey(s.Name))
                    _errors.Add($"Duplicate struct type '{s.Name}'");
                else
                    _structTypes[s.Name] = s;
            }

            // --- Pass 1: build function table (name → placeholder IP) ---
            _functionTable = new Dictionary<string, int>();
            _funcDecls = new Dictionary<string, FuncDecl>();
            FuncDecl entryDecl = null;

            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                _functionTable[f.Name] = -1; // placeholder
                _funcDecls[f.Name] = f;
                if (f.Name == entryFunc)
                    entryDecl = f;
            }

            if (entryDecl == null)
                return new CompileResult { Errors = new List<string> { $"Entry function '{entryFunc}' not found" } };

            // FO1: analyze leaf functions before compilation
            AnalyzeLeafFunctions(module, entryFunc);

            // --- Pass 2: compile entry function first, then all other functions ---
            var functionEntries = new List<FunctionEntry>();

            _functionTable[entryDecl.Name] = 0;
            CompileFunction(entryDecl, isEntry: true);
            functionEntries.Add(new FunctionEntry(entryDecl.Name, 0, entryDecl.Parameters.Count, _callerWindowSize, false));

            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                if (f.Name == entryFunc) continue;

                bool isLeaf = _leafFunctions.TryGetValue(f.Name, out bool lf) && lf;
                int ip = CurrentIP();
                _functionTable[f.Name] = ip;
                CompileFunction(f, isEntry: false);
                functionEntries.Add(new FunctionEntry(f.Name, ip, f.Parameters.Count, _callerWindowSize, isLeaf));
            }

            // --- Backpatch forward references: CALL instructions whose target was -1 at emit time ---
            // R7: when >50 pending calls, build Dictionary index for O(1) lookup per function
            if (_pendingCalls.Count > 50)
            {
                var pendingByName = new Dictionary<string, List<int>>();
                for (int i = 0; i < _pendingCalls.Count; i++)
                {
                    var pending = _pendingCalls[i];
                    if (!pendingByName.TryGetValue(pending.FunctionName, out var list))
                    {
                        list = new List<int>();
                        pendingByName[pending.FunctionName] = list;
                    }
                    list.Add(pending.InstructionIP);
                }
                foreach (var kv in pendingByName)
                {
                    if (_functionTable.TryGetValue(kv.Key, out int targetIP) && targetIP >= 0)
                    {
                        for (int j = 0; j < kv.Value.Count; j++)
                        {
                            var instr = _instructions[kv.Value[j]];
                            _instructions[kv.Value[j]] = new Instruction(instr.Code, targetIP, instr.B, instr.C);
                        }
                    }
                    else
                    {
                        _errors.Add($"Unresolved function '{kv.Key}'");
                    }
                }
            }
            else
            {
                for (int i = 0; i < _pendingCalls.Count; i++)
                {
                    var pending = _pendingCalls[i];
                    if (_functionTable.TryGetValue(pending.FunctionName, out int targetIP) && targetIP >= 0)
                    {
                        var instr = _instructions[pending.InstructionIP];
                        _instructions[pending.InstructionIP] = new Instruction(instr.Code, targetIP, instr.B, instr.C);
                    }
                    else
                    {
                        _errors.Add($"Unresolved function '{pending.FunctionName}'");
                    }
                }
            }

            // FO7: Static call depth analysis — check for excessive call depth or recursion
            AnalyzeCallDepth(module, entryFunc);

            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

            // O6: Peephole optimization pass — eliminate redundant instructions
            PeepholeOptimize(functionEntries);

            int maxRegs = VarRegBase; // minimum
            for (int i = 0; i < functionEntries.Count; i++)
            {
                int need = functionEntries[i].LocalRegCount + VarRegBase;
                if (need > maxRegs) maxRegs = need;
            }

            return new CompileResult
            {
                Program = new VMProgram(
                    _instructions.ToArray(),
                    _constants.ToArray(),
                    maxRegs,
                    functionEntries.ToArray(),
                    _sourceLines.ToArray(),
                    _symbolEntries.ToArray()
                ),
                Errors = _errors
            };
        }

        /// <summary>
        /// Compile a single function body into the instruction stream.
        /// Resets per-function state (variables, temps, deferred cleanups).
        /// </summary>
        private void CompileFunction(FuncDecl func, bool isEntry)
        {
            _variables = new Dictionary<string, int>();
            _structVarTypes = new Dictionary<string, string>();
            _nextVarReg = VarRegBase;
            _tempTop = TempRegBase;
            _deferredCleanups = new List<DeferredCleanup>();
            _isEntryFunction = isEntry;
            _isLeafFunction = !isEntry && _leafFunctions.TryGetValue(func.Name, out bool lf) && lf;
            _inCleanupBlock = false;
            _freeVarRegs = new List<int>();
            _maxVarRegUsed = VarRegBase - 1;
            _stmtOrder = 0;
            _currentFunctionName = func.Name;

            // F4: analyze variable lifetimes before compilation
            _liveRanges = AnalyzeVariableLifetimes(func);

            // Bind parameters: copy from scratch zone r0..rN into local registers r16+
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                int localReg = DeclareVar(func.Parameters[i].Name);
                // Emit MOVE to copy param from scratch r[i] to local r[localReg]
                if (localReg != i)
                    Emit(OpCode.MOVE, localReg, i);
            }

            // Compile function body
            CompileBlock(func.Body);

            // Emit terminator
            if (isEntry)
                Emit(OpCode.RETURN);
            else
                Emit(_isLeafFunction ? OpCode.RET_LEAF : OpCode.RET_FUNC);

            // Emit deferred cleanup blocks for this function
            for (int i = 0; i < _deferredCleanups.Count; i++)
            {
                int cleanupIP = _instructions.Count;
                Backpatch(_deferredCleanups[i].PushCleanupIP, cleanupIP);

                if (_deferredCleanups[i].ReleaseSyscallSlot >= 0)
                {
                    Emit(OpCode.SYSCALL, _deferredCleanups[i].ReleaseSyscallSlot, 0, 0);
                }
                else
                {
                    // G6: mark as inside cleanup block so wait/wait_for are rejected
                    bool prevInCleanup = _inCleanupBlock;
                    _inCleanupBlock = true;
                    CompileBlock(_deferredCleanups[i].Body);
                    _inCleanupBlock = prevInCleanup;
                }
                Emit(OpCode.RETURN);
            }

            // A.6: Record precise window size using max register actually allocated
            _callerWindowSize = (_maxVarRegUsed >= VarRegBase) ? (_maxVarRegUsed - VarRegBase + 1) : 0;
        }

        // ===== F4: Variable lifetime analysis =====

        /// <summary>
        /// Analyze variable lifetimes in a function AST.
        /// Returns a dictionary of variable name → LiveRange with declaration order, last use order, and await crossing info.
        /// </summary>
        private Dictionary<string, LiveRange> AnalyzeVariableLifetimes(FuncDecl func)
        {
            var ranges = new Dictionary<string, LiveRange>();
            int order = 0;
            bool seenAwait = false;
            var awaitOrder = -1; // order at which first await is seen
            var declaredBeforeAwait = new HashSet<string>();
            var usedAfterAwait = new HashSet<string>();

            // Track all variable declarations and usages through AST walk
            void WalkExpr(Expr expr)
            {
                if (expr == null) return;
                order++;

                if (expr is IdentifierExpr ident)
                {
                    if (ranges.ContainsKey(ident.Name))
                    {
                        var r = ranges[ident.Name];
                        r.LastUseOrder = order;
                        ranges[ident.Name] = r;
                    }
                    if (seenAwait && declaredBeforeAwait.Contains(ident.Name))
                        usedAfterAwait.Add(ident.Name);
                }
                else if (expr is FieldAccessExpr fa)
                {
                    if (fa.Target is IdentifierExpr faIdent)
                    {
                        if (ranges.ContainsKey(faIdent.Name))
                        {
                            var r = ranges[faIdent.Name];
                            r.LastUseOrder = order;
                            ranges[faIdent.Name] = r;
                        }
                        if (seenAwait && declaredBeforeAwait.Contains(faIdent.Name))
                            usedAfterAwait.Add(faIdent.Name);
                    }
                }
                else if (expr is BinaryExpr bin)
                {
                    WalkExpr(bin.Left);
                    WalkExpr(bin.Right);
                }
                else if (expr is UnaryExpr un)
                {
                    WalkExpr(un.Operand);
                }
                else if (expr is AssignExpr assign)
                {
                    WalkExpr(assign.Target);
                    WalkExpr(assign.Value);
                }
                else if (expr is CallExpr call)
                {
                    for (int i = 0; i < call.Arguments.Count; i++)
                        WalkExpr(call.Arguments[i]);
                }
            }

            void WalkStmt(Stmt stmt)
            {
                if (stmt == null) return;

                if (stmt is VarDeclStmt varDecl)
                {
                    order++;
                    int fieldCount = 0;
                    if (_structTypes.TryGetValue(varDecl.TypeName, out var sd))
                        fieldCount = sd.Fields.Count;

                    ranges[varDecl.Name] = new LiveRange
                    {
                        Name = varDecl.Name,
                        DefOrder = order,
                        LastUseOrder = order,
                        CrossesAwait = false,
                        FieldCount = fieldCount
                    };
                    if (!seenAwait) declaredBeforeAwait.Add(varDecl.Name);
                    if (varDecl.Initializer != null)
                        WalkExpr(varDecl.Initializer);
                }
                else if (stmt is ExprStmt exprStmt)
                {
                    WalkExpr(exprStmt.Expression);
                }
                else if (stmt is IfStmt ifStmt)
                {
                    WalkExpr(ifStmt.Condition);
                    WalkStmt(ifStmt.ThenBranch);
                    if (ifStmt.ElseBranch != null) WalkStmt(ifStmt.ElseBranch);
                }
                else if (stmt is WhileStmt whileStmt)
                {
                    WalkExpr(whileStmt.Condition);
                    WalkStmt(whileStmt.Body);
                }
                else if (stmt is ForStmt forStmt)
                {
                    if (forStmt.Initializer != null) WalkStmt(forStmt.Initializer);
                    if (forStmt.Condition != null) WalkExpr(forStmt.Condition);
                    if (forStmt.Increment != null) WalkExpr(forStmt.Increment);
                    WalkStmt(forStmt.Body);
                }
                else if (stmt is BlockStmt block)
                {
                    for (int i = 0; i < block.Statements.Count; i++)
                        WalkStmt(block.Statements[i]);
                }
                else if (stmt is ReturnStmt retStmt)
                {
                    if (retStmt.Value != null) WalkExpr(retStmt.Value);
                }
                else if (stmt is WaitStmt || stmt is WaitForStmt || stmt is YieldStmt)
                {
                    seenAwait = true;
                    if (awaitOrder < 0) awaitOrder = order;
                    if (stmt is WaitForStmt wf) WalkExpr(wf.TargetInstanceId);
                }
                else if (stmt is DeferStmt deferStmt)
                {
                    WalkStmt(deferStmt.Body);
                }
                else if (stmt is UsingStmt usingStmt)
                {
                    for (int i = 0; i < usingStmt.Arguments.Count; i++)
                        WalkExpr(usingStmt.Arguments[i]);
                    WalkStmt(usingStmt.Body);
                }
            }

            // Walk parameters first (they are always defined at start)
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                order++;
                ranges[func.Parameters[i].Name] = new LiveRange
                {
                    Name = func.Parameters[i].Name,
                    DefOrder = order,
                    LastUseOrder = order,
                    CrossesAwait = false,
                    FieldCount = 0
                };
                declaredBeforeAwait.Add(func.Parameters[i].Name);
            }

            // Walk function body
            for (int i = 0; i < func.Body.Statements.Count; i++)
                WalkStmt(func.Body.Statements[i]);

            // Mark variables that cross awaits
            foreach (var name in usedAfterAwait)
            {
                if (ranges.ContainsKey(name))
                {
                    var r = ranges[name];
                    r.CrossesAwait = true;
                    ranges[name] = r;
                }
            }

            return ranges;
        }

        // ===== Variable management =====

        private int DeclareVar(string name)
        {
            // F4: try to reuse a freed register from the free list
            if (_freeVarRegs != null && _freeVarRegs.Count > 0)
            {
                int reg = _freeVarRegs[_freeVarRegs.Count - 1];
                _freeVarRegs.RemoveAt(_freeVarRegs.Count - 1);
                _variables[name] = reg;
                // DBG2: record symbol entry for scalar variable
                _symbolEntries.Add(new SymbolEntry(name, reg, 0, null, _currentFunctionName));
                return reg;
            }

            if (_nextVarReg >= TempRegBase)
            {
                _errors.Add($"Too many local variables (max {TempRegBase - VarRegBase})");
                return VarRegBase;
            }
            int newReg = _nextVarReg++;
            _variables[name] = newReg;
            if (newReg > _maxVarRegUsed) _maxVarRegUsed = newReg;
            // DBG2: record symbol entry for scalar variable
            _symbolEntries.Add(new SymbolEntry(name, newReg, 0, null, _currentFunctionName));
            return newReg;
        }

        /// <summary>
        /// Declare a struct variable, allocating consecutive registers.
        /// F4: tries to find a consecutive free block in the free list.
        /// </summary>
        private int DeclareStructVar(string name, int fieldCount)
        {
            // F4: try to find consecutive free registers in free list
            if (_freeVarRegs != null && _freeVarRegs.Count >= fieldCount)
            {
                _freeVarRegs.Sort();
                // Look for a consecutive run
                for (int i = 0; i <= _freeVarRegs.Count - fieldCount; i++)
                {
                    bool consecutive = true;
                    for (int j = 1; j < fieldCount; j++)
                    {
                        if (_freeVarRegs[i + j] != _freeVarRegs[i] + j)
                        {
                            consecutive = false;
                            break;
                        }
                    }
                    if (consecutive)
                    {
                        int baseReg = _freeVarRegs[i];
                        // Remove these registers from free list (reverse order to keep indices valid)
                        for (int j = fieldCount - 1; j >= 0; j--)
                            _freeVarRegs.RemoveAt(i + j);
                        _variables[name] = baseReg;
                        return baseReg;
                    }
                }
            }

            // Fall back to linear allocation
            if (_nextVarReg + fieldCount > TempRegBase)
            {
                _errors.Add($"Too many local variables — struct '{name}' needs {fieldCount} registers (max {TempRegBase - VarRegBase})");
                return VarRegBase;
            }
            int newBaseReg = _nextVarReg;
            _nextVarReg += fieldCount;
            _variables[name] = newBaseReg;
            if (_nextVarReg - 1 > _maxVarRegUsed) _maxVarRegUsed = _nextVarReg - 1;
            return newBaseReg;
        }

        /// <summary>
        /// F4: Release a variable's register(s) back to the free list for reuse.
        /// Only releases if the variable is not live across an await.
        /// </summary>
        private void TryReleaseVar(string name)
        {
            if (_liveRanges == null) return;
            if (!_liveRanges.TryGetValue(name, out var range)) return;
            // Don't release variables that cross awaits — they must persist
            if (range.CrossesAwait) return;
            if (!_variables.TryGetValue(name, out int reg)) return;

            int count = range.FieldCount > 0 ? range.FieldCount : 1;
            for (int i = 0; i < count; i++)
                _freeVarRegs.Add(reg + i);
        }

        private int ResolveVar(string name)
        {
            if (_variables.TryGetValue(name, out int reg))
                return reg;
            _errors.Add($"Undefined variable '{name}'");
            return VarRegBase;
        }

        /// <summary>
        /// Resolve a field access (e.g., d.level) to a register number: baseReg + fieldIndex.
        /// </summary>
        private int ResolveFieldAccess(FieldAccessExpr fa)
        {
            if (fa.Target is IdentifierExpr ident)
            {
                if (!_structVarTypes.TryGetValue(ident.Name, out var typeName))
                {
                    _errors.Add($"Variable '{ident.Name}' is not a struct (line {fa.Line})");
                    return VarRegBase;
                }
                if (!_structTypes.TryGetValue(typeName, out var sd))
                {
                    _errors.Add($"Unknown struct type '{typeName}' (line {fa.Line})");
                    return VarRegBase;
                }
                int baseReg = _variables[ident.Name];
                for (int i = 0; i < sd.Fields.Count; i++)
                {
                    if (sd.Fields[i].Name == fa.FieldName)
                        return baseReg + i;
                }
                _errors.Add($"Struct '{typeName}' has no field '{fa.FieldName}' (line {fa.Line})");
                return baseReg;
            }
            _errors.Add($"Unsupported field access target (line {fa.Line})");
            return VarRegBase;
        }

        // ===== Temp management =====

        private int AllocTemp()
        {
            if (_tempTop >= VMConstants.MaxRegisters)
            {
                _errors.Add("Expression too complex (out of temp registers)");
                return TempRegBase;
            }
            return _tempTop++;
        }

        private void ResetTemps()
        {
            _tempTop = TempRegBase;
        }

        // ===== Constant pool =====

        private int AddConst(Number value)
        {
            for (int i = 0; i < _constants.Count; i++)
            {
                if (_constants[i] == value)
                    return i;
            }
            int idx = _constants.Count;
            _constants.Add(value);
            return idx;
        }

        // ===== Instruction emission =====

        private int CurrentIP() => _instructions.Count;

        private void Emit(OpCode code, int a = 0, int b = 0, int c = 0)
        {
            _instructions.Add(new Instruction(code, a, b, c));
            // DBG1: record source line for this instruction
            _sourceLines.Add(_currentLine);
        }

        private int EmitJump(OpCode code, int testReg = 0)
        {
            int ip = _instructions.Count;
            Emit(code, 0, testReg); // A=target placeholder, B=testReg
            return ip;
        }

        private void Backpatch(int instrIP, int targetIP)
        {
            var instr = _instructions[instrIP];
            _instructions[instrIP] = new Instruction(instr.Code, targetIP, instr.B, instr.C);
        }

        // ===== Statement compilation =====

        private void CompileBlock(BlockStmt block)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                _stmtOrder++;
                CompileStmt(block.Statements[i]);
                ResetTemps();

                // F4: release variables whose lifetime has ended after this statement
                if (_liveRanges != null)
                {
                    // Check each live range: if lastUseOrder <= current stmtOrder, release it
                    // (We use a snapshot approach: collect candidates then release)
                    var toRelease = new List<string>();
                    foreach (var kv in _liveRanges)
                    {
                        if (kv.Value.LastUseOrder <= _stmtOrder && kv.Value.DefOrder < _stmtOrder
                            && _variables.ContainsKey(kv.Key) && !kv.Value.CrossesAwait)
                        {
                            // Check if already freed
                            int reg = _variables[kv.Key];
                            bool alreadyFreed = false;
                            for (int f = 0; f < _freeVarRegs.Count; f++)
                            {
                                if (_freeVarRegs[f] == reg) { alreadyFreed = true; break; }
                            }
                            if (!alreadyFreed)
                                toRelease.Add(kv.Key);
                        }
                    }
                    for (int r = 0; r < toRelease.Count; r++)
                        TryReleaseVar(toRelease[r]);
                }
            }
        }

        private void CompileStmt(Stmt stmt)
        {
            // DBG1: update current line from AST node
            if (stmt.Line > 0) _currentLine = stmt.Line;

            if (stmt is VarDeclStmt varDecl) { CompileVarDecl(varDecl); return; }
            if (stmt is IfStmt ifStmt)       { CompileIf(ifStmt); return; }
            if (stmt is WhileStmt whileStmt) { CompileWhile(whileStmt); return; }
            if (stmt is ForStmt forStmt)     { CompileFor(forStmt); return; }
            if (stmt is ReturnStmt retStmt)  { CompileReturn(retStmt); return; }
            if (stmt is WaitStmt waitStmt)   { CompileWait(waitStmt); return; }
            if (stmt is WaitForStmt waitForStmt) { CompileWaitFor(waitForStmt); return; }
            if (stmt is YieldStmt)           { Emit(OpCode.WAIT, 1); return; }
            if (stmt is DeferStmt deferStmt) { CompileDefer(deferStmt); return; }
            if (stmt is UsingStmt usingStmt) { CompileUsing(usingStmt); return; }
            if (stmt is BlockStmt block)     { CompileBlock(block); return; }
            if (stmt is ExprStmt exprStmt)   { CompileExprStmt(exprStmt); return; }
            _errors.Add($"Unknown statement type: {stmt.GetType().Name}");
        }

        private void CompileVarDecl(VarDeclStmt stmt)
        {
            // Check if this is a struct variable
            if (_structTypes.TryGetValue(stmt.TypeName, out var structDecl))
            {
                // F4: use DeclareStructVar for register reuse of consecutive slots
                int baseReg = DeclareStructVar(stmt.Name, structDecl.Fields.Count);
                _structVarTypes[stmt.Name] = stmt.TypeName;

                // DBG2: record symbol entry for struct variable with field names
                var fieldNames = new string[structDecl.Fields.Count];
                for (int fi = 0; fi < structDecl.Fields.Count; fi++)
                    fieldNames[fi] = structDecl.Fields[fi].Name;
                _symbolEntries.Add(new SymbolEntry(stmt.Name, baseReg, structDecl.Fields.Count, fieldNames, _currentFunctionName));

                // Initialize: if initializer is another struct var of same type, emit N × MOVE
                if (stmt.Initializer != null)
                {
                    if (stmt.Initializer is IdentifierExpr srcIdent &&
                        _structVarTypes.TryGetValue(srcIdent.Name, out var srcType) &&
                        srcType == stmt.TypeName)
                    {
                        int srcBase = _variables[srcIdent.Name];
                        for (int i = 0; i < structDecl.Fields.Count; i++)
                            Emit(OpCode.MOVE, baseReg + i, srcBase + i);
                    }
                    else
                    {
                        _errors.Add($"Struct variable '{stmt.Name}' can only be initialized from another struct of same type (line {stmt.Line})");
                    }
                }
                else
                {
                    // Default initialize all fields to 0
                    int ci = AddConst(Number.Zero);
                    for (int i = 0; i < structDecl.Fields.Count; i++)
                        Emit(OpCode.LOAD_CONST, baseReg + i, ci);
                }
                return;
            }

            // Scalar variable (original path)
            int reg = DeclareVar(stmt.Name);
            if (stmt.Initializer != null)
            {
                // O4: pass dest-reg hint so expression writes directly into var register
                int valueReg = CompileExpr(stmt.Initializer, destReg: reg);
                if (valueReg != reg)
                    Emit(OpCode.MOVE, reg, valueReg);
            }
            else
            {
                // Default initialize to 0
                int ci = AddConst(Number.Zero);
                Emit(OpCode.LOAD_CONST, reg, ci);
            }
        }

        private void CompileIf(IfStmt stmt)
        {
            int condReg = CompileExpr(stmt.Condition);
            int jumpElseIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            ResetTemps();

            CompileStmt(stmt.ThenBranch);

            if (stmt.ElseBranch != null)
            {
                int jumpEndIP = EmitJump(OpCode.JUMP);
                Backpatch(jumpElseIP, CurrentIP());
                CompileStmt(stmt.ElseBranch);
                Backpatch(jumpEndIP, CurrentIP());
            }
            else
            {
                Backpatch(jumpElseIP, CurrentIP());
            }
        }

        private void CompileWhile(WhileStmt stmt)
        {
            int loopStart = CurrentIP();
            int condReg = CompileExpr(stmt.Condition);
            int jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            ResetTemps();

            CompileStmt(stmt.Body);
            Emit(OpCode.JUMP, loopStart);
            Backpatch(jumpEndIP, CurrentIP());
        }

        private void CompileFor(ForStmt stmt)
        {
            if (stmt.Initializer != null)
            {
                CompileStmt(stmt.Initializer);
                ResetTemps();
            }

            int loopStart = CurrentIP();

            // Condition (if null, treat as always true → infinite loop)
            int jumpEndIP = -1;
            if (stmt.Condition != null)
            {
                int condReg = CompileExpr(stmt.Condition);
                jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
                ResetTemps();
            }

            CompileStmt(stmt.Body);

            if (stmt.Increment != null)
            {
                CompileExpr(stmt.Increment);
                ResetTemps();
            }

            Emit(OpCode.JUMP, loopStart);

            if (jumpEndIP >= 0)
                Backpatch(jumpEndIP, CurrentIP());
        }

        private void CompileReturn(ReturnStmt stmt)
        {
            if (stmt.Value != null)
            {
                int valueReg = CompileExpr(stmt.Value);
                // Return value convention: r0
                if (valueReg != 0)
                    Emit(OpCode.MOVE, 0, valueReg);
            }
            // Entry function: RETURN (triggers cleanup chain / Completed)
            // Non-entry function: RET_FUNC/RET_LEAF (pop CallFrame or restore from leaf fields)
            Emit(_isEntryFunction ? OpCode.RETURN : (_isLeafFunction ? OpCode.RET_LEAF : OpCode.RET_FUNC));
        }

        private void CompileWait(WaitStmt stmt)
        {
            // G6: prohibit wait inside cleanup blocks (defer/using release)
            if (_inCleanupBlock)
            {
                _errors.Add($"Cannot use 'wait' inside a cleanup block (defer/using) (line {stmt.Line})");
                return;
            }

            if (stmt.FrameCount is IntLiteralExpr intLit)
            {
                Emit(OpCode.WAIT, intLit.Value);
            }
            else if (stmt.FrameCount is NumberLiteralExpr numLit)
            {
                Emit(OpCode.WAIT, (int)numLit.Value);
            }
            else
            {
                _errors.Add($"wait argument must be a constant integer (line {stmt.Line})");
            }
        }

        private void CompileDefer(DeferStmt stmt)
        {
            int pushIP = CurrentIP();
            Emit(OpCode.PUSH_CLEANUP, 0); // placeholder for cleanup entry IP
            _deferredCleanups.Add(new DeferredCleanup { PushCleanupIP = pushIP, Body = stmt.Body, ReleaseSyscallSlot = NoReleaseSyscall });
        }

        private void CompileWaitFor(WaitForStmt stmt)
        {
            // G6: prohibit wait_for inside cleanup blocks (defer/using release)
            if (_inCleanupBlock)
            {
                _errors.Add($"Cannot use 'wait_for' inside a cleanup block (defer/using) (line {stmt.Line})");
                return;
            }

            int targetReg = CompileExpr(stmt.TargetInstanceId);
            Emit(OpCode.WAIT_FOR, targetReg);
        }

        private void CompileUsing(UsingStmt stmt)
        {
            // 1. Resolve acquire syscall slot
            if (!_syscalls.TryGetValue(stmt.SyscallName, out int acquireSlot))
            {
                _errors.Add($"Unknown syscall '{stmt.SyscallName}' in using statement (line {stmt.Line})");
                return;
            }

            // 2. Resolve release (paired) syscall slot
            int releaseSlot = _syscallTable != null ? _syscallTable.GetPairedSlot(acquireSlot) : -1;
            if (releaseSlot < 0)
            {
                _errors.Add($"Syscall '{stmt.SyscallName}' has no paired release syscall — cannot use in 'using' (line {stmt.Line})");
                return;
            }

            // 3. Compile arguments → emit acquire SYSCALL
            int[] argRegs = new int[stmt.Arguments.Count];
            for (int i = 0; i < stmt.Arguments.Count; i++)
                argRegs[i] = CompileExpr(stmt.Arguments[i]);

            for (int i = 0; i < stmt.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            Emit(OpCode.SYSCALL, acquireSlot, 0, stmt.Arguments.Count);

            // 4. PUSH_CLEANUP (placeholder → release block emitted at function tail)
            int pushIP = CurrentIP();
            Emit(OpCode.PUSH_CLEANUP, 0);
            _deferredCleanups.Add(new DeferredCleanup
            {
                PushCleanupIP = pushIP,
                Body = null,
                ReleaseSyscallSlot = releaseSlot
            });

            // 5. Compile body
            CompileBlock(stmt.Body);

            // 6. POP_CLEANUP — normal exit pops the cleanup frame (G2 fix: first time POP_CLEANUP is emitted)
            Emit(OpCode.POP_CLEANUP);
        }

        private void CompileExprStmt(ExprStmt stmt)
        {
            // Optimized path: void call (skip result save)
            if (stmt.Expression is CallExpr call)
            {
                // User function — void call
                if (_functionTable != null && _functionTable.ContainsKey(call.FunctionName))
                {
                    CompileUserCallVoid(call);
                    return;
                }
                // Syscall — void call
                if (_syscalls.ContainsKey(call.FunctionName))
                {
                    CompileSyscallVoid(call);
                    return;
                }
            }
            // Generic path: compile expression, discard result
            CompileExpr(stmt.Expression);
        }

        // ===== Constant folding (O5) =====

        /// <summary>
        /// Try to evaluate a constant expression at compile time.
        /// Returns true if the expression is a pure constant (literals + arithmetic/comparison/boolean).
        /// </summary>
        private bool TryFoldConstant(Expr expr, out Number value)
        {
            value = Number.Zero;

            if (expr is IntLiteralExpr intLit)
            {
                value = Number.FromInt(intLit.Value);
                return true;
            }
            if (expr is NumberLiteralExpr numLit)
            {
                value = Number.FromFloat(numLit.Value);
                return true;
            }
            if (expr is BoolLiteralExpr boolLit)
            {
                value = boolLit.Value ? Number.One : Number.Zero;
                return true;
            }
            if (expr is UnaryExpr un)
            {
                if (!TryFoldConstant(un.Operand, out Number operand))
                    return false;
                switch (un.Kind)
                {
                    case NodeKind.Negate: value = -operand; return true;
                    case NodeKind.Not:    value = operand == Number.Zero ? Number.One : Number.Zero; return true;
                    default: return false;
                }
            }
            if (expr is BinaryExpr bin)
            {
                if (!TryFoldConstant(bin.Left, out Number left) || !TryFoldConstant(bin.Right, out Number right))
                    return false;
                switch (bin.Kind)
                {
                    case NodeKind.Add: value = left + right; return true;
                    case NodeKind.Sub: value = left - right; return true;
                    case NodeKind.Mul: value = left * right; return true;
                    case NodeKind.Div: value = left / right; return true;
                    case NodeKind.Mod: value = left % right; return true;
                    case NodeKind.Eq:  value = left == right ? Number.One : Number.Zero; return true;
                    case NodeKind.Neq: value = left != right ? Number.One : Number.Zero; return true;
                    case NodeKind.Lt:  value = left < right ? Number.One : Number.Zero; return true;
                    case NodeKind.Lte: value = left <= right ? Number.One : Number.Zero; return true;
                    case NodeKind.Gt:  value = left > right ? Number.One : Number.Zero; return true;
                    case NodeKind.Gte: value = left >= right ? Number.One : Number.Zero; return true;
                    case NodeKind.And: value = (left != Number.Zero && right != Number.Zero) ? Number.One : Number.Zero; return true;
                    case NodeKind.Or:  value = (left != Number.Zero || right != Number.Zero) ? Number.One : Number.Zero; return true;
                    default: return false;
                }
            }
            return false;
        }

        // ===== Expression compilation =====
        // Returns the register holding the result

        private int CompileExpr(Expr expr, int destReg = -1)
        {
            // DBG1: update current line from expression AST node
            if (expr.Line > 0) _currentLine = expr.Line;

            // O5: constant folding — evaluate pure constant expressions at compile time
            if (expr is BinaryExpr || expr is UnaryExpr)
            {
                if (TryFoldConstant(expr, out Number foldedValue))
                {
                    int reg = destReg >= 0 ? destReg : AllocTemp();
                    Emit(OpCode.LOAD_CONST, reg, AddConst(foldedValue));
                    return reg;
                }
            }

            if (expr is IntLiteralExpr intLit)
            {
                int reg = destReg >= 0 ? destReg : AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromInt(intLit.Value)));
                return reg;
            }

            if (expr is NumberLiteralExpr numLit)
            {
                int reg = destReg >= 0 ? destReg : AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromFloat(numLit.Value)));
                return reg;
            }

            if (expr is BoolLiteralExpr boolLit)
            {
                int reg = destReg >= 0 ? destReg : AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(boolLit.Value ? Number.One : Number.Zero));
                return reg;
            }

            if (expr is IdentifierExpr ident)
            {
                return ResolveVar(ident.Name);
            }

            if (expr is FieldAccessExpr fieldAccess)
            {
                return ResolveFieldAccess(fieldAccess);
            }

            if (expr is BinaryExpr bin)
            {
                int left = CompileExpr(bin.Left);
                int right = CompileExpr(bin.Right);
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(BinOpCode(bin.Kind), dest, left, right);
                return dest;
            }

            if (expr is UnaryExpr un)
            {
                int operand = CompileExpr(un.Operand);
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(UnOpCode(un.Kind), dest, operand);
                return dest;
            }

            if (expr is AssignExpr assign)
            {
                // Struct whole assignment: a = b (both are struct variables of same type)
                if (assign.Target is IdentifierExpr targetIdent &&
                    _structVarTypes.TryGetValue(targetIdent.Name, out var targetStructType))
                {
                    if (assign.Value is IdentifierExpr srcIdent &&
                        _structVarTypes.TryGetValue(srcIdent.Name, out var srcStructType) &&
                        srcStructType == targetStructType)
                    {
                        var sd = _structTypes[targetStructType];
                        int destBase = _variables[targetIdent.Name];
                        int srcBase = _variables[srcIdent.Name];
                        for (int i = 0; i < sd.Fields.Count; i++)
                            Emit(OpCode.MOVE, destBase + i, srcBase + i);
                        return destBase;
                    }
                    _errors.Add($"Cannot assign non-struct value to struct variable '{targetIdent.Name}' (line {assign.Line})");
                    return ResolveVar(targetIdent.Name);
                }

                // Field assignment: d.field = expr
                if (assign.Target is FieldAccessExpr fieldTarget)
                {
                    int fieldReg = ResolveFieldAccess(fieldTarget);
                    // O4: pass dest-reg hint for field assignment
                    int valueReg = CompileExpr(assign.Value, destReg: fieldReg);
                    if (valueReg != fieldReg)
                        Emit(OpCode.MOVE, fieldReg, valueReg);
                    return fieldReg;
                }

                // Scalar assignment (original path)
                if (assign.Target is IdentifierExpr scalarTarget)
                {
                    int scalarTargetReg = ResolveVar(scalarTarget.Name);
                    // O4: pass dest-reg hint so expression writes directly into target register
                    int scalarValueReg = CompileExpr(assign.Value, destReg: scalarTargetReg);
                    if (scalarValueReg != scalarTargetReg)
                        Emit(OpCode.MOVE, scalarTargetReg, scalarValueReg);
                    return scalarTargetReg;
                }
                {
                    int scalarValueReg = CompileExpr(assign.Value);
                    _errors.Add($"Invalid assignment target (line {assign.Line})");
                    return scalarValueReg;
                }
            }

            if (expr is CallExpr call)
            {
                // User function call (returns value in r0)
                if (_functionTable != null && _functionTable.ContainsKey(call.FunctionName))
                    return CompileUserCallExpr(call, destReg);

                // Syscall call
                return CompileSyscallExpr(call, destReg);
            }

            _errors.Add($"Unknown expression type: {expr.GetType().Name}");
            return TempRegBase;
        }

        // ===== Syscall compilation =====

        /// <summary>
        /// Compile a syscall call as an expression (saves result to temp).
        /// Two-phase arg compilation to avoid register conflicts with nested calls.
        /// </summary>
        private int CompileSyscallExpr(CallExpr call, int destReg = -1)
        {
            if (!_syscalls.TryGetValue(call.FunctionName, out int slot))
            {
                _errors.Add($"Unknown function '{call.FunctionName}' (line {call.Line})");
                return TempRegBase;
            }

            // C4: requires_cleanup check — only 'using' wrapped calls are exempt (they don't go through this path)
            if (_syscallTable != null && _syscallTable.RequiresCleanup(slot))
            {
                _errors.Add($"Syscall '{call.FunctionName}' requires cleanup. Use 'using {call.FunctionName}(args) {{ ... }}'. (line {call.Line})");
                return TempRegBase;
            }

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to r0, r1, ... (safe: all sources are r16+/r48+)
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            Emit(OpCode.SYSCALL, slot, 0, call.Arguments.Count);

            // O7/FO5: save result from r0 directly to destReg if available
            int resultReg = destReg >= 0 ? destReg : AllocTemp();
            if (resultReg != 0)
                Emit(OpCode.MOVE, resultReg, 0);
            return resultReg;
        }

        /// <summary>
        /// Compile a void syscall call (no result save, used for expression statements).
        /// </summary>
        private void CompileSyscallVoid(CallExpr call)
        {
            int slot = _syscalls[call.FunctionName];

            // C4: requires_cleanup check — only 'using' wrapped calls are exempt (they don't go through this path)
            if (_syscallTable != null && _syscallTable.RequiresCleanup(slot))
            {
                _errors.Add($"Syscall '{call.FunctionName}' requires cleanup. Use 'using {call.FunctionName}(args) {{ ... }}'. (line {call.Line})");
                return;
            }

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to r0, r1, ...
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            Emit(OpCode.SYSCALL, slot, 0, call.Arguments.Count);
        }

        // ===== User function call compilation =====

        /// <summary>
        /// Emit arguments to scratch zone, then CALL user function.
        /// Returns temp register holding the return value (from r0).
        /// </summary>
        private int CompileUserCallExpr(CallExpr call, int destReg = -1)
        {
            EmitUserCall(call);

            // FO5: save result from r0 directly to destReg if available
            int resultReg = destReg >= 0 ? destReg : AllocTemp();
            if (resultReg != 0)
                Emit(OpCode.MOVE, resultReg, 0);
            return resultReg;
        }

        /// <summary>
        /// Void user function call (no result save).
        /// </summary>
        private void CompileUserCallVoid(CallExpr call)
        {
            EmitUserCall(call);
        }

        /// <summary>
        /// Core: compile args → scratch zone, emit CALL instruction.
        /// callerWindowSize = VarRegBase(16) + localVarCount for register window offset.
        /// </summary>
        private void EmitUserCall(CallExpr call)
        {
            // R8: prohibit function calls inside cleanup blocks (defer/using release)
            if (_inCleanupBlock)
            {
                _errors.Add($"Cannot call functions inside a cleanup block (defer/using) (line {call.Line})");
                return;
            }

            int entryIP = _functionTable[call.FunctionName];

            // Validate parameter count
            if (_funcDecls.TryGetValue(call.FunctionName, out var funcDecl))
            {
                if (call.Arguments.Count != funcDecl.Parameters.Count)
                {
                    _errors.Add($"Function '{call.FunctionName}' expects {funcDecl.Parameters.Count} arguments but got {call.Arguments.Count} (line {call.Line})");
                    return;
                }
            }

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to r0, r1, ... (scratch zone — shared, not windowed)
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            // callerWindowSize = number of local var registers currently in use
            // CALL will offset RegisterBase by this amount so callee's r16+ doesn't overlap caller's
            int windowSize = _nextVarReg - VarRegBase;
            if (windowSize < 1) windowSize = 1; // minimum 1 to prevent zero-offset stacking

            int callIP = CurrentIP();
            // FO1: emit CALL_LEAF for leaf function targets
            bool targetIsLeaf = _leafFunctions.TryGetValue(call.FunctionName, out bool tl) && tl;
            Emit(targetIsLeaf ? OpCode.CALL_LEAF : OpCode.CALL, entryIP, windowSize);

            // If target IP is still placeholder (-1), record for backpatch
            if (entryIP < 0)
            {
                _pendingCalls.Add(new PendingCall { InstructionIP = callIP, FunctionName = call.FunctionName });
            }
        }

        // ===== FO7: Static call depth analysis =====

        /// <summary>
        /// Build a call graph from the AST and compute max call depth.
        /// If max depth exceeds MaxCallDepth, report a compile error.
        /// Detects recursion (cycles) and marks as "dynamic depth — requires runtime check".
        /// </summary>
        private void AnalyzeCallDepth(ModuleNode module, string entryFunc)
        {
            // Build call graph: funcName → set of called function names
            var callGraph = new Dictionary<string, HashSet<string>>();
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var func = module.Functions[i];
                var callees = new HashSet<string>();
                CollectCallees(func.Body, callees);
                callGraph[func.Name] = callees;
            }

            // DFS to compute max depth from each function
            var visited = new Dictionary<string, int>(); // funcName → max depth from this node (-1 = in progress)

            int ComputeDepth(string funcName)
            {
                if (visited.TryGetValue(funcName, out int cached))
                {
                    if (cached == -1) return -1; // cycle detected (recursion)
                    return cached;
                }
                visited[funcName] = -1; // mark in progress

                if (!callGraph.TryGetValue(funcName, out var callees) || callees.Count == 0)
                {
                    visited[funcName] = 0; // leaf function
                    return 0;
                }

                int maxChildDepth = 0;
                bool hasRecursion = false;
                foreach (var callee in callees)
                {
                    // Only analyze known user functions (skip syscalls)
                    if (!callGraph.ContainsKey(callee)) continue;

                    int childDepth = ComputeDepth(callee);
                    if (childDepth == -1)
                    {
                        hasRecursion = true;
                        continue; // skip recursive edges for depth calculation
                    }
                    if (childDepth + 1 > maxChildDepth)
                        maxChildDepth = childDepth + 1;
                }

                if (hasRecursion)
                {
                    // Don't error — recursion is valid but requires runtime check
                    // Mark with non-overflowing depth so callers don't trigger static errors
                    visited[funcName] = maxChildDepth;
                    return maxChildDepth;
                }

                visited[funcName] = maxChildDepth;
                return maxChildDepth;
            }

            int entryDepth = callGraph.ContainsKey(entryFunc) ? ComputeDepth(entryFunc) : 0;
            // Note: recursive functions return their non-recursive child depth (ignoring back-edges).
            // This means recursion doesn't inflate the static depth — runtime MaxCallDepth check handles it.
            if (entryDepth > VMConstants.MaxCallDepth)
            {
                _errors.Add($"Static call depth from '{entryFunc}' is {entryDepth}, exceeding MaxCallDepth ({VMConstants.MaxCallDepth}). Reduce function nesting or increase limit.");
            }
        }

        /// <summary>
        /// Recursively collect all user function names called within a block statement.
        /// </summary>
        private void CollectCallees(Stmt stmt, HashSet<string> callees)
        {
            if (stmt == null) return;

            if (stmt is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                    CollectCallees(block.Statements[i], callees);
            }
            else if (stmt is ExprStmt exprStmt)
            {
                CollectCalleesExpr(exprStmt.Expression, callees);
            }
            else if (stmt is VarDeclStmt varDecl)
            {
                if (varDecl.Initializer != null)
                    CollectCalleesExpr(varDecl.Initializer, callees);
            }
            else if (stmt is IfStmt ifStmt)
            {
                CollectCalleesExpr(ifStmt.Condition, callees);
                CollectCallees(ifStmt.ThenBranch, callees);
                if (ifStmt.ElseBranch != null) CollectCallees(ifStmt.ElseBranch, callees);
            }
            else if (stmt is WhileStmt whileStmt)
            {
                CollectCalleesExpr(whileStmt.Condition, callees);
                CollectCallees(whileStmt.Body, callees);
            }
            else if (stmt is ForStmt forStmt)
            {
                if (forStmt.Initializer != null) CollectCallees(forStmt.Initializer, callees);
                if (forStmt.Condition != null) CollectCalleesExpr(forStmt.Condition, callees);
                if (forStmt.Increment != null) CollectCalleesExpr(forStmt.Increment, callees);
                CollectCallees(forStmt.Body, callees);
            }
            else if (stmt is ReturnStmt retStmt)
            {
                if (retStmt.Value != null) CollectCalleesExpr(retStmt.Value, callees);
            }
            else if (stmt is DeferStmt deferStmt)
            {
                CollectCallees(deferStmt.Body, callees);
            }
            else if (stmt is UsingStmt usingStmt)
            {
                for (int i = 0; i < usingStmt.Arguments.Count; i++)
                    CollectCalleesExpr(usingStmt.Arguments[i], callees);
                CollectCallees(usingStmt.Body, callees);
            }
            else if (stmt is WaitForStmt wf)
            {
                CollectCalleesExpr(wf.TargetInstanceId, callees);
            }
        }

        private void CollectCalleesExpr(Expr expr, HashSet<string> callees)
        {
            if (expr == null) return;

            if (expr is CallExpr call)
            {
                // Only record user function calls (those in _funcDecls)
                if (_funcDecls.ContainsKey(call.FunctionName))
                    callees.Add(call.FunctionName);
                for (int i = 0; i < call.Arguments.Count; i++)
                    CollectCalleesExpr(call.Arguments[i], callees);
            }
            else if (expr is BinaryExpr bin)
            {
                CollectCalleesExpr(bin.Left, callees);
                CollectCalleesExpr(bin.Right, callees);
            }
            else if (expr is UnaryExpr un)
            {
                CollectCalleesExpr(un.Operand, callees);
            }
            else if (expr is AssignExpr assign)
            {
                CollectCalleesExpr(assign.Target, callees);
                CollectCalleesExpr(assign.Value, callees);
            }
        }

        // ===== FO1: Leaf function analysis =====

        /// <summary>
        /// Analyze all functions and determine which are leaf functions.
        /// A function is leaf if its body contains no CallExpr, WaitStmt, WaitForStmt, or YieldStmt.
        /// Entry function is never treated as leaf (uses RETURN, not RET_FUNC/RET_LEAF).
        /// </summary>
        private void AnalyzeLeafFunctions(ModuleNode module, string entryFunc)
        {
            _leafFunctions = new Dictionary<string, bool>();
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var func = module.Functions[i];
                if (func.Name == entryFunc)
                {
                    _leafFunctions[func.Name] = false; // entry function is never leaf
                    continue;
                }
                _leafFunctions[func.Name] = !ContainsNonLeafNode(func.Body);
            }
        }

        /// <summary>
        /// Returns true if the statement subtree contains any node that disqualifies
        /// a function from being a leaf: CallExpr, WaitStmt, WaitForStmt, YieldStmt.
        /// </summary>
        private bool ContainsNonLeafNode(Stmt stmt)
        {
            if (stmt == null) return false;

            if (stmt is WaitStmt || stmt is WaitForStmt || stmt is YieldStmt)
                return true;

            if (stmt is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                    if (ContainsNonLeafNode(block.Statements[i])) return true;
            }
            else if (stmt is ExprStmt exprStmt)
            {
                if (ContainsNonLeafExpr(exprStmt.Expression)) return true;
            }
            else if (stmt is VarDeclStmt varDecl)
            {
                if (varDecl.Initializer != null && ContainsNonLeafExpr(varDecl.Initializer)) return true;
            }
            else if (stmt is IfStmt ifStmt)
            {
                if (ContainsNonLeafExpr(ifStmt.Condition)) return true;
                if (ContainsNonLeafNode(ifStmt.ThenBranch)) return true;
                if (ifStmt.ElseBranch != null && ContainsNonLeafNode(ifStmt.ElseBranch)) return true;
            }
            else if (stmt is WhileStmt whileStmt)
            {
                if (ContainsNonLeafExpr(whileStmt.Condition)) return true;
                if (ContainsNonLeafNode(whileStmt.Body)) return true;
            }
            else if (stmt is ForStmt forStmt)
            {
                if (forStmt.Initializer != null && ContainsNonLeafNode(forStmt.Initializer)) return true;
                if (forStmt.Condition != null && ContainsNonLeafExpr(forStmt.Condition)) return true;
                if (forStmt.Increment != null && ContainsNonLeafExpr(forStmt.Increment)) return true;
                if (ContainsNonLeafNode(forStmt.Body)) return true;
            }
            else if (stmt is ReturnStmt retStmt)
            {
                if (retStmt.Value != null && ContainsNonLeafExpr(retStmt.Value)) return true;
            }
            else if (stmt is DeferStmt deferStmt)
            {
                if (ContainsNonLeafNode(deferStmt.Body)) return true;
            }
            else if (stmt is UsingStmt usingStmt)
            {
                for (int i = 0; i < usingStmt.Arguments.Count; i++)
                    if (ContainsNonLeafExpr(usingStmt.Arguments[i])) return true;
                if (ContainsNonLeafNode(usingStmt.Body)) return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the expression subtree contains a CallExpr (user function call).
        /// SyscallExpr is not disqualifying — syscalls don't use the call stack.
        /// </summary>
        private static bool ContainsNonLeafExpr(Expr expr)
        {
            if (expr == null) return false;

            if (expr is CallExpr) return true;

            if (expr is BinaryExpr bin)
                return ContainsNonLeafExpr(bin.Left) || ContainsNonLeafExpr(bin.Right);

            if (expr is UnaryExpr un)
                return ContainsNonLeafExpr(un.Operand);

            if (expr is AssignExpr assign)
                return ContainsNonLeafExpr(assign.Target) || ContainsNonLeafExpr(assign.Value);

            if (expr is SyscallExpr sc)
            {
                for (int i = 0; i < sc.Arguments.Count; i++)
                    if (ContainsNonLeafExpr(sc.Arguments[i])) return true;
            }

            if (expr is FieldAccessExpr fa)
                return ContainsNonLeafExpr(fa.Target);

            return false;
        }

        // ===== OpCode mapping =====

        private OpCode BinOpCode(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Add: return OpCode.ADD;
                case NodeKind.Sub: return OpCode.SUB;
                case NodeKind.Mul: return OpCode.MUL;
                case NodeKind.Div: return OpCode.DIV;
                case NodeKind.Mod: return OpCode.MOD;
                case NodeKind.Eq:  return OpCode.CMP_EQ;
                case NodeKind.Neq: return OpCode.CMP_NEQ;
                case NodeKind.Lt:  return OpCode.CMP_LT;
                case NodeKind.Lte: return OpCode.CMP_LTE;
                case NodeKind.Gt:  return OpCode.CMP_GT;
                case NodeKind.Gte: return OpCode.CMP_GTE;
                case NodeKind.And: return OpCode.AND;
                case NodeKind.Or:  return OpCode.OR;
                default:
                    _errors.Add($"Unknown binary operator: {kind}");
                    return OpCode.NOP;
            }
        }

        private OpCode UnOpCode(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Negate: return OpCode.NEG;
                case NodeKind.Not:    return OpCode.NOT;
                default:
                    _errors.Add($"Unknown unary operator: {kind}");
                    return OpCode.NOP;
            }
        }

        // ===== O6: Peephole optimization pass =====

        /// <summary>
        /// Returns true if the opcode writes a computed result to operand A.
        /// Used by peephole P2 (dest-redirect) to identify instructions whose
        /// destination register can be safely redirected.
        /// </summary>
        private static bool IsResultProducer(OpCode code)
        {
            switch (code)
            {
                case OpCode.LOAD_CONST:
                case OpCode.ADD:  case OpCode.SUB: case OpCode.MUL: case OpCode.DIV: case OpCode.MOD:
                case OpCode.CMP_EQ: case OpCode.CMP_NEQ: case OpCode.CMP_LT:
                case OpCode.CMP_LTE: case OpCode.CMP_GT: case OpCode.CMP_GTE:
                case OpCode.AND: case OpCode.OR: case OpCode.NOT: case OpCode.NEG:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Returns true if the opcode uses operand A as an absolute IP target
        /// that must be remapped when instructions are deleted.
        /// </summary>
        private static bool HasJumpTargetInA(OpCode code)
        {
            switch (code)
            {
                case OpCode.JUMP:
                case OpCode.JUMP_IF_ZERO:
                case OpCode.JUMP_IF_NOT_ZERO:
                case OpCode.CALL:
                case OpCode.CALL_LEAF:
                case OpCode.PUSH_CLEANUP:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// O6 Peephole optimization: scan emitted instructions and eliminate redundant patterns.
        /// Runs after backpatching, before VMProgram construction.
        /// Patterns:
        ///   P1: MOVE rA, rA          → NOP  (self-move)
        ///   P2: OP rT,… ; MOVE rV,rT → OP rV,… (dest-redirect, eliminates MOVE)
        ///   P3: MOVE rA,rB ; MOVE rB,rA → delete second (back-copy)
        ///   P4: JUMP target where target==IP+1 → NOP (jump-to-next)
        /// After marking, compacts the instruction stream and rebases all jump targets.
        /// </summary>
        private void PeepholeOptimize(List<FunctionEntry> functionEntries)
        {
            int count = _instructions.Count;
            if (count == 0) return;

            // Phase 1: build set of IPs that are jump targets (cannot safely remove these)
            var jumpTargets = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                if (HasJumpTargetInA(_instructions[i].Code))
                    jumpTargets.Add(_instructions[i].A);
            }

            // Phase 2: pattern matching — mark eliminated instructions as NOP
            // Use a bool array to track which instructions are eliminated (turned to NOP).
            // We keep the original NOP instructions intact (only eliminate optimizer-marked ones).
            var eliminated = new bool[count];

            for (int i = 0; i < count; i++)
            {
                if (eliminated[i]) continue;
                var ins = _instructions[i];

                // P1: self-MOVE → eliminate
                if (ins.Code == OpCode.MOVE && ins.A == ins.B)
                {
                    eliminated[i] = true;
                    continue;
                }

                // P4: unconditional JUMP to next instruction → eliminate
                if (ins.Code == OpCode.JUMP && ins.A == i + 1)
                {
                    eliminated[i] = true;
                    continue;
                }

                if (i + 1 >= count || eliminated[i + 1] || jumpTargets.Contains(i + 1))
                    continue;

                var next = _instructions[i + 1];

                // P2: dest-redirect — OP rT,… ; MOVE rV,rT → OP rV,…
                // Safety: only redirect when original dest (ins.A) is a temp register (≥ TempRegBase).
                // Variable registers may be read later; redirecting away from them would break semantics.
                if (IsResultProducer(ins.Code) && next.Code == OpCode.MOVE
                    && next.B == ins.A && ins.A >= TempRegBase)
                {
                    _instructions[i] = new Instruction(ins.Code, next.A, ins.B, ins.C);
                    eliminated[i + 1] = true;
                    continue;
                }

                // P3: back-copy — MOVE rA,rB ; MOVE rB,rA → delete second
                if (ins.Code == OpCode.MOVE && next.Code == OpCode.MOVE
                    && next.A == ins.B && next.B == ins.A)
                {
                    eliminated[i + 1] = true;
                    continue;
                }
            }

            // Phase 3: compact — remove eliminated instructions, rebuild jump targets
            // Build remap table: old IP → new IP
            int[] remap = new int[count + 1]; // +1 for potential end-of-program targets
            int newIP = 0;
            for (int i = 0; i < count; i++)
            {
                remap[i] = newIP;
                if (!eliminated[i]) newIP++;
            }
            remap[count] = newIP;

            // Check if any instructions were eliminated
            if (newIP == count) return; // nothing to compact

            // Build compacted instruction and source line lists
            var newInstructions = new List<Instruction>(newIP);
            var newSourceLines = new List<int>(newIP);
            for (int i = 0; i < count; i++)
            {
                if (eliminated[i]) continue;

                var ins = _instructions[i];
                // Rebase jump targets
                if (HasJumpTargetInA(ins.Code))
                    ins = new Instruction(ins.Code, remap[ins.A], ins.B, ins.C);

                newInstructions.Add(ins);
                newSourceLines.Add(_sourceLines[i]);
            }

            _instructions = newInstructions;
            _sourceLines = newSourceLines;

            // Rebase FunctionEntry IPs
            for (int i = 0; i < functionEntries.Count; i++)
            {
                var fe = functionEntries[i];
                int newEntryIP = remap[fe.EntryIP];
                functionEntries[i] = new FunctionEntry(fe.Name, newEntryIP, fe.ParamCount, fe.LocalRegCount);
            }
        }
    }
}
