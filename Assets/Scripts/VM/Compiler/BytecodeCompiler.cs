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

        // Struct support: compile-time type table
        private Dictionary<string, StructDecl> _structTypes;    // typeName → struct declaration
        private Dictionary<string, string> _structVarTypes;     // varName → struct typeName

        // Multi-function support
        private Dictionary<string, int> _functionTable;  // funcName → entryIP (-1 = not yet compiled)
        private Dictionary<string, FuncDecl> _funcDecls; // funcName → AST for param count lookup
        private bool _isEntryFunction;                   // true when compiling the entry func
        private int _callerWindowSize;                   // localVarCount for current function

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

            // --- Pass 2: compile entry function first, then all other functions ---
            var functionEntries = new List<FunctionEntry>();

            _functionTable[entryDecl.Name] = 0;
            CompileFunction(entryDecl, isEntry: true);
            functionEntries.Add(new FunctionEntry(entryDecl.Name, 0, entryDecl.Parameters.Count, _callerWindowSize));

            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                if (f.Name == entryFunc) continue;

                int ip = CurrentIP();
                _functionTable[f.Name] = ip;
                CompileFunction(f, isEntry: false);
                functionEntries.Add(new FunctionEntry(f.Name, ip, f.Parameters.Count, _callerWindowSize));
            }

            // --- Backpatch forward references: CALL instructions whose target was -1 at emit time ---
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

            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

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
                    functionEntries.ToArray()
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
            _inCleanupBlock = false;

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
                Emit(OpCode.RET_FUNC);

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

            // Record window size: number of local variable registers used (above r16)
            _callerWindowSize = _nextVarReg - VarRegBase;
        }

        // ===== Variable management =====

        private int DeclareVar(string name)
        {
            if (_nextVarReg >= TempRegBase)
            {
                _errors.Add($"Too many local variables (max {TempRegBase - VarRegBase})");
                return VarRegBase;
            }
            int reg = _nextVarReg++;
            _variables[name] = reg;
            return reg;
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
                CompileStmt(block.Statements[i]);
                ResetTemps();
            }
        }

        private void CompileStmt(Stmt stmt)
        {
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
                int baseReg = _nextVarReg;
                if (_nextVarReg + structDecl.Fields.Count > TempRegBase)
                {
                    _errors.Add($"Too many local variables — struct '{stmt.Name}' needs {structDecl.Fields.Count} registers (max {TempRegBase - VarRegBase})");
                    return;
                }
                _variables[stmt.Name] = baseReg;
                _structVarTypes[stmt.Name] = stmt.TypeName;
                _nextVarReg += structDecl.Fields.Count;

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
                int valueReg = CompileExpr(stmt.Initializer);
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
            // Non-entry function: RET_FUNC (pop CallFrame, resume caller)
            Emit(_isEntryFunction ? OpCode.RETURN : OpCode.RET_FUNC);
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

        // ===== Expression compilation =====
        // Returns the register holding the result

        private int CompileExpr(Expr expr)
        {
            if (expr is IntLiteralExpr intLit)
            {
                int reg = AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromInt(intLit.Value)));
                return reg;
            }

            if (expr is NumberLiteralExpr numLit)
            {
                int reg = AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromFloat(numLit.Value)));
                return reg;
            }

            if (expr is BoolLiteralExpr boolLit)
            {
                int reg = AllocTemp();
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
                int dest = AllocTemp();
                Emit(BinOpCode(bin.Kind), dest, left, right);
                return dest;
            }

            if (expr is UnaryExpr un)
            {
                int operand = CompileExpr(un.Operand);
                int dest = AllocTemp();
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
                    int valueReg = CompileExpr(assign.Value);
                    if (valueReg != fieldReg)
                        Emit(OpCode.MOVE, fieldReg, valueReg);
                    return fieldReg;
                }

                // Scalar assignment (original path)
                int scalarValueReg = CompileExpr(assign.Value);
                if (assign.Target is IdentifierExpr scalarTarget)
                {
                    int scalarTargetReg = ResolveVar(scalarTarget.Name);
                    if (scalarValueReg != scalarTargetReg)
                        Emit(OpCode.MOVE, scalarTargetReg, scalarValueReg);
                    return scalarTargetReg;
                }
                _errors.Add($"Invalid assignment target (line {assign.Line})");
                return scalarValueReg;
            }

            if (expr is CallExpr call)
            {
                // User function call (returns value in r0)
                if (_functionTable != null && _functionTable.ContainsKey(call.FunctionName))
                    return CompileUserCallExpr(call);

                // Syscall call
                return CompileSyscallExpr(call);
            }

            _errors.Add($"Unknown expression type: {expr.GetType().Name}");
            return TempRegBase;
        }

        // ===== Syscall compilation =====

        /// <summary>
        /// Compile a syscall call as an expression (saves result to temp).
        /// Two-phase arg compilation to avoid register conflicts with nested calls.
        /// </summary>
        private int CompileSyscallExpr(CallExpr call)
        {
            if (!_syscalls.TryGetValue(call.FunctionName, out int slot))
            {
                _errors.Add($"Unknown function '{call.FunctionName}' (line {call.Line})");
                return TempRegBase;
            }

            // C4: requires_cleanup check — only 'using' wrapped calls are exempt (they don't go through this path)
            if (_syscallTable != null && _syscallTable.RequiresCleanup(slot))
            {
                _errors.Add($"Syscall '{call.FunctionName}' requires cleanup. Use 'using {call.FunctionName}(args) {{ ... }}' or wrap with 'defer'. (line {call.Line})");
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

            // Save result from r0 to temp (protect from future overwrites)
            int resultTemp = AllocTemp();
            Emit(OpCode.MOVE, resultTemp, 0);
            return resultTemp;
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
                _errors.Add($"Syscall '{call.FunctionName}' requires cleanup. Use 'using {call.FunctionName}(args) {{ ... }}' or wrap with 'defer'. (line {call.Line})");
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
        private int CompileUserCallExpr(CallExpr call)
        {
            EmitUserCall(call);

            // Save result from r0 to temp (protect from future overwrites)
            int resultTemp = AllocTemp();
            Emit(OpCode.MOVE, resultTemp, 0);
            return resultTemp;
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
            Emit(OpCode.CALL, entryIP, windowSize);

            // If target IP is still placeholder (-1), record for backpatch
            if (entryIP < 0)
            {
                _pendingCalls.Add(new PendingCall { InstructionIP = callIP, FunctionName = call.FunctionName });
            }
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
    }
}
