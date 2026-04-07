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
    ///   r0..r15   — scratch zone: syscall arguments / return values (absolute)
    ///   r16..r47  — local variables (32 slots, windowed by RegisterBase)
    ///   r48..r55  — expression temporaries (8 slots, windowed by RegisterBase)
    ///   r56..r63  — module variables (8 slots, absolute — Lang-1)
    /// </summary>
    public class BytecodeCompiler
    {
        private const int VarRegBase = 16;
        private const int TempRegBase = 48;
        private const int ModuleVarRegBase = VMConstants.ModuleVarRegBase; // 56 — Lang-1

        private List<Instruction> _instructions;
        private List<int> _wideA;  // O8: full int A values parallel to _instructions (byte A may truncate for IP > 255)
        private List<Number> _constants;
        private Dictionary<string, int> _variables;   // name → register
        private Dictionary<string, Number> _constValues;  // B-ε3: name → compile-time constant value
        private int _nextVarReg;
        private int _forLoopId;                            // B-ε4: unique suffix for hidden limit vars
        private Dictionary<int, int> _hoistedConstants;     // B-ζ1: LICM — constIndex → hoisted register
        private int _licmId;                               // B-ζ1: unique suffix for hoisted constant vars
        private List<int[]> _jumpTables;                   // B-ζ3: SWITCH jump tables (tableIdx → IP array)
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

        // SN1: Nested struct — flattened struct info (computed once at compile start)
        private struct FlatFieldEntry
        {
            public string DotPath;   // e.g. "inner.x" (dot-separated from struct root)
            public int Offset;       // register offset from struct base
        }
        private struct FlatStructInfo
        {
            public int FlatFieldCount;          // total scalar registers after recursive flattening
            public FlatFieldEntry[] FlatFields;  // ordered flat field entries
        }
        private Dictionary<string, FlatStructInfo> _flatStructInfo;  // typeName → flattened info

        // Multi-function support
        private Dictionary<string, int> _functionTable;  // funcName → entryIP (-1 = not yet compiled)
        private Dictionary<string, FuncDecl> _funcDecls; // funcName → AST for param count lookup
        private bool _isEntryFunction;                   // true when compiling the entry func
        private bool _isLeafFunction;                    // FO1: true when compiling a leaf func
        private int _callerWindowSize;                   // localVarCount for current function

        // STR1: String constant pool (ROM)
        private List<string> _stringConstants;

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
        private int _maxTempUsed;                           // FO6: peak temp register used per function
        private int _stmtOrder;                             // current statement order counter for release tracking

        // Lang-1: Module-level variable support
        private Dictionary<string, int> _moduleVariables;       // name → absolute register (r56-r63)
        private Dictionary<string, Number> _moduleConstValues;  // name → compile-time constant value
        private Dictionary<string, string> _moduleStructVarTypes; // name → struct typeName (for module-level struct vars)
        private int _nextModuleVarReg;                          // next available module var register

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
            _wideA = new List<int>();
            _constants = new List<Number>();
            _stringConstants = new List<string>();
            _jumpTables = new List<int[]>();
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

            // SN1: Build flattened struct info (recursive expansion + cycle detection)
            BuildFlatStructInfo();
            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

            // --- Lang-1: Process module-level variables ---
            ProcessModuleVariables(module);
            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

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
            // FO6: after each function, remap temps to pack right after locals and patch CALL window sizes.
            var functionEntries = new List<FunctionEntry>();

            int funcStartIP = 0;
            _functionTable[entryDecl.Name] = 0;
            CompileFunction(entryDecl, isEntry: true);
            int funcEndIP = CurrentIP();
            int entryWindow = ComputeAndRemapFunctionWindow(funcStartIP, funcEndIP);
            functionEntries.Add(new FunctionEntry(entryDecl.Name, 0, entryDecl.Parameters.Count, entryWindow, false));

            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                if (f.Name == entryFunc) continue;

                bool isLeaf = _leafFunctions.TryGetValue(f.Name, out bool lf) && lf;
                funcStartIP = CurrentIP();
                _functionTable[f.Name] = funcStartIP;
                CompileFunction(f, isEntry: false);
                funcEndIP = CurrentIP();
                int window = ComputeAndRemapFunctionWindow(funcStartIP, funcEndIP);
                functionEntries.Add(new FunctionEntry(f.Name, funcStartIP, f.Parameters.Count, window, isLeaf));
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
                            _wideA[kv.Value[j]] = targetIP;
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
                        _wideA[pending.InstructionIP] = targetIP;
                    }
                    else
                    {
                        _errors.Add($"Unresolved function '{pending.FunctionName}'");
                    }
                }
            }

            // FO7: Static call depth analysis — check for excessive call depth or recursion
            // FO6: also validates cumulative register window doesn't overflow
            AnalyzeCallDepth(module, entryFunc, functionEntries);

            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

            // O6: Peephole optimization pass — eliminate redundant instructions
            PeepholeOptimize(functionEntries);

            // O8: Wide expansion pass — insert EXTEND_AX for instructions with IP > 255
            ExpandWideJumps(functionEntries);

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
                    _symbolEntries.ToArray(),
                    _stringConstants.Count > 0 ? _stringConstants.ToArray() : null,
                    _jumpTables.Count > 0 ? _jumpTables.ToArray() : null
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
            _constValues = new Dictionary<string, Number>();
            _structVarTypes = new Dictionary<string, string>();
            _nextVarReg = VarRegBase;
            _forLoopId = 0;
            _tempTop = TempRegBase;
            _deferredCleanups = new List<DeferredCleanup>();
            _isEntryFunction = isEntry;
            _isLeafFunction = !isEntry && _leafFunctions.TryGetValue(func.Name, out bool lf) && lf;
            _inCleanupBlock = false;
            _freeVarRegs = new List<int>();
            _maxVarRegUsed = VarRegBase - 1;
            _maxTempUsed = TempRegBase - 1;  // FO6: no temps used yet
            _stmtOrder = 0;
            _currentFunctionName = func.Name;

            // Reset source line to the function declaration line so that
            // parameter-binding MOVEs (emitted before the body) map to the
            // correct source location instead of carrying the previous
            // function's last line.
            _currentLine = func.Line;

            // Lang-1: Pre-populate module-level variables and consts into function scope
            if (_moduleVariables != null)
            {
                foreach (var kv in _moduleVariables)
                    _variables[kv.Key] = kv.Value;
            }
            if (_moduleConstValues != null)
            {
                foreach (var kv in _moduleConstValues)
                    _constValues[kv.Key] = kv.Value;
            }
            if (_moduleStructVarTypes != null)
            {
                foreach (var kv in _moduleStructVarTypes)
                    _structVarTypes[kv.Key] = kv.Value;
            }

            // F4: analyze variable lifetimes before compilation
            _liveRanges = AnalyzeVariableLifetimes(func);

            // Bind parameters: copy from scratch zone r0..rN into local registers r16+
            // S4/SN1: struct parameters use flattened field count for nested struct support
            {
                int scratchReg = 0;
                for (int i = 0; i < func.Parameters.Count; i++)
                {
                    var param = func.Parameters[i];
                    if (_structTypes.ContainsKey(param.TypeName))
                    {
                        // Struct parameter: allocate consecutive locals, copy multi-reg from scratch
                        int flatCount = _flatStructInfo[param.TypeName].FlatFieldCount;
                        int baseReg = DeclareStructVar(param.Name, flatCount);
                        _structVarTypes[param.Name] = param.TypeName;
                        EmitStructCopy(baseReg, scratchReg, flatCount);
                        scratchReg += flatCount;
                    }
                    else
                    {
                        // Scalar parameter (original behavior)
                        int localReg = DeclareVar(param.Name);
                        if (localReg != scratchReg)
                            Emit(OpCode.MOVE, localReg, scratchReg);
                        scratchReg++;
                    }
                }
            }

            // Lang-1: Emit module variable initialization in entry function
            if (isEntry)
                EmitModuleVarInit();

            // Compile function body
            CompileBlock(func.Body);

            // Emit terminator
            if (isEntry)
                Emit(OpCode.RETURN);
            else
                Emit(_isLeafFunction ? OpCode.RET_LEAF : OpCode.RET_FUNC);

            // C6: Emit deferred cleanup blocks — merge adjacent PUSH_CLEANUP groups
            EmitDeferredCleanups();

            // A.6: Record precise window size using max register actually allocated
            _callerWindowSize = (_maxVarRegUsed >= VarRegBase) ? (_maxVarRegUsed - VarRegBase + 1) : 0;
        }

        /// <summary>
        /// C6: Emit deferred cleanup blocks with adjacent PUSH_CLEANUP merging.
        /// Adjacent PUSH_CLEANUP instructions (consecutive IPs) are merged into a single
        /// compound cleanup block, reducing cleanup stack depth and instruction count.
        /// </summary>
        private void EmitDeferredCleanups()
        {
            if (_deferredCleanups.Count == 0) return;

            // Build groups of adjacent PUSH_CLEANUP instructions.
            // Two cleanups are "adjacent" if their PushCleanupIP values are consecutive
            // AND neither contains a ReturnStmt in its defer body (which would break
            // compound cleanup semantics by prematurely exiting the merged block).
            var groups = new List<(int Start, int End)>(); // [Start, End) ranges into _deferredCleanups
            int groupStart = 0;
            for (int i = 1; i <= _deferredCleanups.Count; i++)
            {
                bool canMerge = i < _deferredCleanups.Count
                    && _deferredCleanups[i].PushCleanupIP == _deferredCleanups[i - 1].PushCleanupIP + 1
                    && !DeferBodyContainsReturn(_deferredCleanups[i])
                    && !DeferBodyContainsReturn(_deferredCleanups[i - 1]);
                if (!canMerge)
                {
                    groups.Add((groupStart, i));
                    groupStart = i;
                }
            }

            // Emit each group
            foreach (var (start, end) in groups)
            {
                int groupSize = end - start;
                if (groupSize == 1)
                {
                    // Single cleanup — emit as before (no merge)
                    EmitSingleCleanup(start);
                }
                else
                {
                    // C6: merged group — emit compound cleanup block in LIFO order
                    // NOP-ify all PUSH_CLEANUP except the last in the group
                    for (int i = start; i < end - 1; i++)
                    {
                        _instructions[_deferredCleanups[i].PushCleanupIP] = new Instruction(OpCode.MOVE, 0, 0, 0);
                        _wideA[_deferredCleanups[i].PushCleanupIP] = 0;
                    }

                    // Backpatch last PUSH_CLEANUP to point to compound block start
                    int compoundIP = _instructions.Count;
                    Backpatch(_deferredCleanups[end - 1].PushCleanupIP, compoundIP);

                    // Emit cleanup blocks in REVERSE order (LIFO: last defer first)
                    for (int i = end - 1; i >= start; i--)
                    {
                        EmitCleanupBody(i);
                        if (i > start)
                        {
                            // No RETURN between merged blocks — fall through
                        }
                        else
                        {
                            // Final block in compound gets RETURN
                            Emit(OpCode.RETURN);
                        }
                    }
                }
            }
        }

        /// <summary>Emit a single (non-merged) cleanup block.</summary>
        private void EmitSingleCleanup(int index)
        {
            int cleanupIP = _instructions.Count;
            Backpatch(_deferredCleanups[index].PushCleanupIP, cleanupIP);
            EmitCleanupBody(index);
            Emit(OpCode.RETURN);
        }

        /// <summary>Emit the body of a cleanup block (without RETURN).</summary>
        private void EmitCleanupBody(int index)
        {
            if (_deferredCleanups[index].ReleaseSyscallSlot >= 0)
            {
                Emit(OpCode.SYSCALL, _deferredCleanups[index].ReleaseSyscallSlot, 0, 0);
            }
            else
            {
                bool prevInCleanup = _inCleanupBlock;
                _inCleanupBlock = true;
                CompileBlock(_deferredCleanups[index].Body);
                _inCleanupBlock = prevInCleanup;
            }
        }

        /// <summary>C6 safety: check if a DeferredCleanup's defer body contains ReturnStmt.</summary>
        private static bool DeferBodyContainsReturn(DeferredCleanup dc)
        {
            return dc.Body != null && ContainsReturn(dc.Body);
        }

        /// <summary>C6 safety: check if a block contains ReturnStmt (unsafe to merge).</summary>
        private static bool ContainsReturn(BlockStmt block)
        {
            foreach (var stmt in block.Statements)
            {
                if (ContainsReturnStmt(stmt)) return true;
            }
            return false;
        }

        private static bool ContainsReturnStmt(Stmt stmt)
        {
            if (stmt is ReturnStmt) return true;
            if (stmt is IfStmt ifStmt)
            {
                if (ContainsReturnStmt(ifStmt.ThenBranch)) return true;
                if (ifStmt.ElseBranch != null && ContainsReturnStmt(ifStmt.ElseBranch)) return true;
            }
            if (stmt is WhileStmt whileStmt && ContainsReturnStmt(whileStmt.Body)) return true;
            if (stmt is BlockStmt nested && ContainsReturn(nested)) return true;
            return false;
        }

        // ===== FO6: Adaptive register window — pack temps after locals =====

        /// <summary>
        /// FO6: Compute the total window size (locals + temps) for a compiled function,
        /// remap temp registers in instructions to pack right after locals, and patch
        /// all CALL/CALL_LEAF window sizes to use the function-level total.
        /// Returns the function's total window size.
        /// </summary>
        private int ComputeAndRemapFunctionWindow(int startIP, int endIP)
        {
            int localCount = _callerWindowSize; // locals-only window (already computed)
            int numTemps = (_maxTempUsed >= TempRegBase) ? (_maxTempUsed - TempRegBase + 1) : 0;
            int totalWindow = localCount + numTemps;
            if (totalWindow < 1) totalWindow = 1; // minimum 1 to prevent zero-offset stacking

            int tempRemapBase = (_maxVarRegUsed >= VarRegBase) ? (_maxVarRegUsed + 1) : VarRegBase;
            int shift = tempRemapBase - TempRegBase; // typically negative: moves temps closer to locals

            for (int ip = startIP; ip < endIP; ip++)
            {
                var instr = _instructions[ip];

                // Patch CALL/CALL_LEAF window sizes to use function-level total
                if (instr.Code == OpCode.CALL || instr.Code == OpCode.CALL_LEAF)
                {
                    _instructions[ip] = new Instruction(instr.Code, _wideA[ip], totalWindow, instr.C);
                    continue;
                }

                if (numTemps == 0 || shift == 0) continue; // no temps to remap

                // Remap temp register operands
                int a = instr.A, b = instr.B, c = instr.C;
                bool changed = false;

                byte mask = GetRegisterMask(instr.Code);
                // Lang-1: only remap temps (TempRegBase..ModuleVarRegBase-1), not module vars (r56+)
                if ((mask & 1) != 0 && a >= TempRegBase && a < ModuleVarRegBase) { a += shift; changed = true; }
                if ((mask & 2) != 0 && b >= TempRegBase && b < ModuleVarRegBase) { b += shift; changed = true; }
                if ((mask & 4) != 0 && c >= TempRegBase && c < ModuleVarRegBase) { c += shift; changed = true; }

                if (changed)
                    _instructions[ip] = new Instruction(instr.Code, a, b, c);
            }

            return totalWindow;
        }

        /// <summary>
        /// FO6: Returns a bitmask indicating which instruction operands are register references.
        /// Bit 0 = A is register, Bit 1 = B is register, Bit 2 = C is register.
        /// </summary>
        private static byte GetRegisterMask(OpCode code)
        {
            switch (code)
            {
                // A = register
                case OpCode.LOAD_CONST: return 1;    // A=destReg, B=constIndex
                case OpCode.WAIT_FOR:   return 1;    // A=srcReg

                // A and B = registers
                case OpCode.MOVE: return 3;           // A=dest, B=src
                case OpCode.NOT:  return 3;           // A=dest, B=src
                case OpCode.NEG:  return 3;           // A=dest, B=src
                case OpCode.COPY_BLOCK: return 3;     // A=dest, B=src, C=count (not a register)

                // B = register
                case OpCode.JUMP_IF_ZERO:     return 2;  // A=targetIP, B=testReg
                case OpCode.JUMP_IF_NOT_ZERO: return 2;  // A=targetIP, B=testReg

                // B = register, C = constIndex (B-ζ2 fused constant-compare-and-branch)
                case OpCode.JUMP_IF_EQ_K:  case OpCode.JUMP_IF_NEQ_K:
                case OpCode.JUMP_IF_LT_K:  case OpCode.JUMP_IF_LTE_K:
                case OpCode.JUMP_IF_GT_K:  case OpCode.JUMP_IF_GTE_K:
                    return 2; // A=targetIP, B=reg, C=constIndex

                // B-ζ3: SWITCH — B=testReg
                case OpCode.SWITCH: return 2; // A=defaultIP, B=testReg, C=jumpTableIdx

                // B, C = registers (P5 fused compare-and-branch)
                case OpCode.JUMP_IF_EQ:  case OpCode.JUMP_IF_NEQ:
                case OpCode.JUMP_IF_LT:  case OpCode.JUMP_IF_LTE:
                case OpCode.JUMP_IF_GT:  case OpCode.JUMP_IF_GTE:
                case OpCode.FORLOOP:
                    return 6; // A=targetIP, B=lhsReg/counterReg, C=rhsReg/limitReg

                // A, B, C = registers
                case OpCode.ADD: case OpCode.SUB:
                case OpCode.MUL: case OpCode.DIV: case OpCode.MOD:
                case OpCode.CMP_EQ:  case OpCode.CMP_NEQ:
                case OpCode.CMP_LT:  case OpCode.CMP_LTE:
                case OpCode.CMP_GT:  case OpCode.CMP_GTE:
                case OpCode.AND: case OpCode.OR:
                    return 7; // A=dest, B=lhs, C=rhs

                // No register operands: NOP, SYSCALL(slot,start,count), WAIT, PUSH_CLEANUP,
                // POP_CLEANUP, RETURN, JUMP, CALL, CALL_LEAF, RET_FUNC, RET_LEAF, SENTINEL
                default: return 0;
            }
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
                else if (expr is StructLiteralExpr structLit)
                {
                    for (int i = 0; i < structLit.Fields.Count; i++)
                        WalkExpr(structLit.Fields[i].Value);
                }
            }

            void WalkStmt(Stmt stmt)
            {
                if (stmt == null) return;

                if (stmt is VarDeclStmt varDecl)
                {
                    order++;
                    int fieldCount = 0;
                    // SN1: use flattened field count for nested struct support
                    int fc = GetFlatFieldCount(varDecl.TypeName);
                    if (fc > 0) fieldCount = fc;

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
                // S4/SN1: struct parameters need correct FieldCount (flattened) for register release
                int fieldCount = 0;
                int fc = GetFlatFieldCount(func.Parameters[i].TypeName);
                if (fc > 0) fieldCount = fc;
                ranges[func.Parameters[i].Name] = new LiveRange
                {
                    Name = func.Parameters[i].Name,
                    DefOrder = order,
                    LastUseOrder = order,
                    CrossesAwait = false,
                    FieldCount = fieldCount
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

        // ===== SN1: Nested struct flattening =====

        /// <summary>
        /// Build _flatStructInfo for all struct types by recursively expanding nested struct fields.
        /// Detects circular references via a visiting set.
        /// </summary>
        private void BuildFlatStructInfo()
        {
            _flatStructInfo = new Dictionary<string, FlatStructInfo>();
            var visiting = new HashSet<string>(); // cycle detection

            foreach (var kv in _structTypes)
            {
                if (!_flatStructInfo.ContainsKey(kv.Key))
                    FlattenStruct(kv.Key, visiting);
            }
        }

        private FlatStructInfo FlattenStruct(string typeName, HashSet<string> visiting)
        {
            if (_flatStructInfo.TryGetValue(typeName, out var cached))
                return cached;

            if (!visiting.Add(typeName))
            {
                _errors.Add($"Circular struct reference detected: '{typeName}'");
                var empty = new FlatStructInfo { FlatFieldCount = 0, FlatFields = System.Array.Empty<FlatFieldEntry>() };
                _flatStructInfo[typeName] = empty;
                return empty;
            }

            var sd = _structTypes[typeName];
            var flatFields = new List<FlatFieldEntry>();
            int offset = 0;

            for (int i = 0; i < sd.Fields.Count; i++)
            {
                var field = sd.Fields[i];
                if (_structTypes.ContainsKey(field.TypeName))
                {
                    // Nested struct field — recursively flatten
                    var inner = FlattenStruct(field.TypeName, visiting);
                    for (int j = 0; j < inner.FlatFields.Length; j++)
                    {
                        flatFields.Add(new FlatFieldEntry
                        {
                            DotPath = field.Name + "." + inner.FlatFields[j].DotPath,
                            Offset = offset + inner.FlatFields[j].Offset
                        });
                    }
                    offset += inner.FlatFieldCount;
                }
                else
                {
                    // Scalar field
                    flatFields.Add(new FlatFieldEntry { DotPath = field.Name, Offset = offset });
                    offset++;
                }
            }

            visiting.Remove(typeName);

            var info = new FlatStructInfo
            {
                FlatFieldCount = offset,
                FlatFields = flatFields.ToArray()
            };
            _flatStructInfo[typeName] = info;
            return info;
        }

        /// <summary>
        /// Get the flattened field count for a struct type. Returns -1 if not a struct.
        /// </summary>
        private int GetFlatFieldCount(string typeName)
        {
            if (_flatStructInfo != null && _flatStructInfo.TryGetValue(typeName, out var info))
                return info.FlatFieldCount;
            return -1;
        }

        /// <summary>
        /// Resolve a dot-path (e.g. "inner.x") to a register offset within a flattened struct.
        /// Also supports sub-struct prefix paths (e.g. "min" in Rect → offset of "min.x").
        /// Returns -1 if not found.
        /// </summary>
        private int ResolveFlatFieldOffset(string typeName, string dotPath)
        {
            if (_flatStructInfo == null || !_flatStructInfo.TryGetValue(typeName, out var info))
                return -1;
            // First: exact match (scalar leaf field)
            for (int i = 0; i < info.FlatFields.Length; i++)
            {
                if (info.FlatFields[i].DotPath == dotPath)
                    return info.FlatFields[i].Offset;
            }
            // Second: prefix match (sub-struct field — return offset of first child)
            string prefix = dotPath + ".";
            for (int i = 0; i < info.FlatFields.Length; i++)
            {
                if (info.FlatFields[i].DotPath.StartsWith(prefix))
                    return info.FlatFields[i].Offset;
            }
            return -1;
        }

        /// <summary>
        /// Get the flattened field count for a sub-field within a struct (for sub-struct assignment).
        /// E.g. for Rect.min where min is Vec2, returns 2.
        /// Returns -1 if the field is a scalar.
        /// </summary>
        private int GetSubFieldFlatCount(string typeName, string fieldName)
        {
            if (!_structTypes.TryGetValue(typeName, out var sd))
                return -1;
            for (int i = 0; i < sd.Fields.Count; i++)
            {
                if (sd.Fields[i].Name == fieldName)
                {
                    int fc = GetFlatFieldCount(sd.Fields[i].TypeName);
                    return fc > 0 ? fc : -1;
                }
            }
            return -1;
        }

        /// <summary>
        /// Get the struct type name of a specific field. Returns null if scalar.
        /// </summary>
        private string GetFieldStructType(string parentTypeName, string fieldName)
        {
            if (!_structTypes.TryGetValue(parentTypeName, out var sd))
                return null;
            for (int i = 0; i < sd.Fields.Count; i++)
            {
                if (sd.Fields[i].Name == fieldName && _structTypes.ContainsKey(sd.Fields[i].TypeName))
                    return sd.Fields[i].TypeName;
            }
            return null;
        }

        /// <summary>
        /// SN1: Resolve a dot-path (e.g. "inner.x") to the type name of the final field.
        /// Returns struct type name if the leaf is a struct, the scalar type name if scalar,
        /// or null if the path is invalid.
        /// </summary>
        private string ResolveFieldChainType(string rootTypeName, string dotPath)
        {
            string currentType = rootTypeName;
            string[] parts = dotPath.Split('.');
            for (int i = 0; i < parts.Length; i++)
            {
                if (!_structTypes.TryGetValue(currentType, out var sd))
                    return null;
                bool found = false;
                for (int j = 0; j < sd.Fields.Count; j++)
                {
                    if (sd.Fields[j].Name == parts[i])
                    {
                        currentType = sd.Fields[j].TypeName;
                        found = true;
                        break;
                    }
                }
                if (!found) return null;
            }
            return currentType;
        }

        // ===== Lang-1: Module variable processing =====

        /// <summary>
        /// Process module-level variable declarations: allocate registers r56-r63 for vars,
        /// fold constants for consts. Called once before compiling any function.
        /// </summary>
        private void ProcessModuleVariables(ModuleNode module)
        {
            _moduleVariables = new Dictionary<string, int>();
            _moduleConstValues = new Dictionary<string, Number>();
            _moduleStructVarTypes = new Dictionary<string, string>();
            _moduleVarDecls = new List<VarDeclStmt>();
            _nextModuleVarReg = ModuleVarRegBase;

            for (int i = 0; i < module.ModuleVariables.Count; i++)
            {
                var mv = module.ModuleVariables[i];

                // Module-level const — fold to compile-time value, no register
                if (mv.IsConst)
                {
                    if (mv.Initializer == null)
                    {
                        _errors.Add($"Module-level 'const' requires an initializer (line {mv.Line})");
                        continue;
                    }
                    if (TryFoldConstant(mv.Initializer, out Number constVal))
                    {
                        if (_moduleConstValues.ContainsKey(mv.Name) || _moduleVariables.ContainsKey(mv.Name))
                        {
                            _errors.Add($"Duplicate module-level declaration '{mv.Name}' (line {mv.Line})");
                            continue;
                        }
                        _moduleConstValues[mv.Name] = constVal;
                    }
                    else
                    {
                        _errors.Add($"Module-level 'const' initializer must be a compile-time constant (line {mv.Line})");
                    }
                    continue;
                }

                // Check for duplicate
                if (_moduleVariables.ContainsKey(mv.Name) || _moduleConstValues.ContainsKey(mv.Name))
                {
                    _errors.Add($"Duplicate module-level declaration '{mv.Name}' (line {mv.Line})");
                    continue;
                }

                // Module-level struct variable
                if (_structTypes.ContainsKey(mv.TypeName))
                {
                    var flatInfo = _flatStructInfo[mv.TypeName];
                    int flatCount = flatInfo.FlatFieldCount;
                    if (_nextModuleVarReg + flatCount > VMConstants.MaxRegisters)
                    {
                        _errors.Add($"Too many module variables — struct '{mv.Name}' needs {flatCount} registers (max {VMConstants.MaxRegisters - ModuleVarRegBase})");
                        continue;
                    }
                    int baseReg = _nextModuleVarReg;
                    _nextModuleVarReg += flatCount;
                    _moduleVariables[mv.Name] = baseReg;
                    _moduleStructVarTypes[mv.Name] = mv.TypeName;
                    _moduleVarDecls.Add(mv);

                    // DBG2: symbol entry for module-level struct variable
                    var fieldNames = new string[flatCount];
                    for (int fi = 0; fi < flatCount; fi++)
                        fieldNames[fi] = flatInfo.FlatFields[fi].DotPath;
                    _symbolEntries.Add(new SymbolEntry(mv.Name, baseReg, flatCount, fieldNames, null));
                    continue;
                }

                // Module-level scalar variable
                if (_nextModuleVarReg >= VMConstants.MaxRegisters)
                {
                    _errors.Add($"Too many module variables (max {VMConstants.MaxRegisters - ModuleVarRegBase})");
                    continue;
                }
                int reg = _nextModuleVarReg++;
                _moduleVariables[mv.Name] = reg;
                _moduleVarDecls.Add(mv);

                // DBG2: symbol entry for module-level scalar variable (scope = null = module)
                _symbolEntries.Add(new SymbolEntry(mv.Name, reg, 0, null, null));
            }
        }

        /// <summary>
        /// Emit module variable initialization code at the start of the entry function.
        /// Module consts don't need initialization (compile-time folded).
        /// Module vars get explicit initializer or default zero.
        /// </summary>
        private void EmitModuleVarInit()
        {
            if (_moduleVarDecls == null || _moduleVarDecls.Count == 0) return;

            for (int i = 0; i < _moduleVarDecls.Count; i++)
            {
                var mv = _moduleVarDecls[i];
                _currentLine = mv.Line;
                int reg = _moduleVariables[mv.Name];

                // Struct module variable
                if (_moduleStructVarTypes != null && _moduleStructVarTypes.ContainsKey(mv.Name))
                {
                    string typeName = _moduleStructVarTypes[mv.Name];
                    var flatInfo = _flatStructInfo[typeName];
                    int flatCount = flatInfo.FlatFieldCount;

                    if (mv.Initializer != null)
                    {
                        if (mv.Initializer is StructLiteralExpr literal)
                        {
                            CompileStructLiteral(literal, typeName, reg, mv.Line);
                        }
                        else if (mv.Initializer is IdentifierExpr srcIdent &&
                                 _moduleStructVarTypes.TryGetValue(srcIdent.Name, out var srcType) &&
                                 srcType == typeName)
                        {
                            int srcBase = _moduleVariables[srcIdent.Name];
                            EmitStructCopy(reg, srcBase, flatCount);
                        }
                        else
                        {
                            _errors.Add($"Module struct variable '{mv.Name}' can only be initialized from struct literal or same-type struct (line {mv.Line})");
                        }
                    }
                    else
                    {
                        // Default initialize all fields to 0
                        int ci = AddConst(Number.Zero);
                        for (int f = 0; f < flatCount; f++)
                            Emit(OpCode.LOAD_CONST, reg + f, ci);
                    }
                    continue;
                }

                // Scalar module variable
                if (mv.Initializer != null)
                {
                    int valueReg = CompileExpr(mv.Initializer, destReg: reg);
                    if (valueReg != reg)
                        Emit(OpCode.MOVE, reg, valueReg);
                }
                else
                {
                    // Default initialize to 0
                    EmitLoadConst(AddConst(Number.Zero), reg);
                }
            }
        }

        // Storage for module variable AST nodes (set during ProcessModuleVariables, used by EmitModuleVarInit)
        private List<VarDeclStmt> _moduleVarDecls;

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
        /// Resolve a field access (e.g., d.level or d.inner.x) to a register number.
        /// SN1: supports recursive field chains via dot-path lookup in flat struct info.
        /// </summary>
        private int ResolveFieldAccess(FieldAccessExpr fa)
        {
            // Collect the field chain: e.g. a.inner.x → varName="a", dotPath="inner.x"
            string varName;
            string dotPath;
            CollectFieldChain(fa, out varName, out dotPath);

            if (varName == null)
            {
                _errors.Add($"Unsupported field access target (line {fa.Line})");
                return VarRegBase;
            }

            if (!_structVarTypes.TryGetValue(varName, out var typeName))
            {
                _errors.Add($"Variable '{varName}' is not a struct (line {fa.Line})");
                return VarRegBase;
            }

            int baseReg = _variables[varName];
            int offset = ResolveFlatFieldOffset(typeName, dotPath);
            if (offset < 0)
            {
                _errors.Add($"Struct '{typeName}' has no field '{dotPath}' (line {fa.Line})");
                return baseReg;
            }
            return baseReg + offset;
        }

        /// <summary>
        /// Collect field access chain into (varName, dotPath).
        /// e.g. FieldAccess(FieldAccess(Ident("a"), "inner"), "x") → ("a", "inner.x")
        /// </summary>
        private void CollectFieldChain(FieldAccessExpr fa, out string varName, out string dotPath)
        {
            if (fa.Target is IdentifierExpr ident)
            {
                varName = ident.Name;
                dotPath = fa.FieldName;
                return;
            }
            if (fa.Target is FieldAccessExpr parentFa)
            {
                CollectFieldChain(parentFa, out varName, out string parentDotPath);
                dotPath = parentDotPath + "." + fa.FieldName;
                return;
            }
            varName = null;
            dotPath = null;
        }

        // ===== Temp management =====

        private int AllocTemp()
        {
            if (_tempTop >= ModuleVarRegBase)
            {
                _errors.Add("Expression too complex (out of temp registers)");
                return TempRegBase;
            }
            int reg = _tempTop++;
            if (reg > _maxTempUsed) _maxTempUsed = reg;  // FO6: track peak temp
            return reg;
        }

        private void ResetTemps()
        {
            _tempTop = TempRegBase;
        }

        // ===== Constant pool =====

        /// <summary>
        /// B-ζ1: Emit a LOAD_CONST or reuse a hoisted register.
        /// If the constant was hoisted (LICM), returns the hoisted register directly (no instruction emitted)
        /// or emits a MOVE if a specific destReg is requested.
        /// </summary>
        private int EmitLoadConst(int constIndex, int destReg)
        {
            if (_hoistedConstants != null && _hoistedConstants.TryGetValue(constIndex, out int hoistedReg))
            {
                if (destReg < 0) return hoistedReg; // no specific dest → use hoisted register directly
                if (destReg == hoistedReg) return destReg;
                Emit(OpCode.MOVE, destReg, hoistedReg);
                return destReg;
            }
            int reg = destReg >= 0 ? destReg : AllocTemp();
            Emit(OpCode.LOAD_CONST, reg, constIndex);
            return reg;
        }

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

        private int AddStringConst(string value)
        {
            for (int i = 0; i < _stringConstants.Count; i++)
            {
                if (_stringConstants[i] == value)
                    return i;
            }
            int idx = _stringConstants.Count;
            _stringConstants.Add(value);
            return idx;
        }

        private static bool ContainsStringLiteral(Expr expr)
        {
            if (expr is StringLiteralExpr) return true;
            if (expr is BinaryExpr b) return ContainsStringLiteral(b.Left) || ContainsStringLiteral(b.Right);
            if (expr is UnaryExpr u) return ContainsStringLiteral(u.Operand);
            return false;
        }

        // ===== Instruction emission =====

        private int CurrentIP() => _instructions.Count;

        private void Emit(OpCode code, int a = 0, int b = 0, int c = 0)
        {
            _instructions.Add(new Instruction(code, a, b, c));
            _wideA.Add(a);  // O8: preserve full int A value
            // DBG1: record source line for this instruction
            _sourceLines.Add(_currentLine);
        }

        /// <summary>
        /// SO1: Emit struct copy — COPY_BLOCK for count ≥ 3, N×MOVE for count ≤ 2.
        /// Self-copy (destBase == srcBase) is a no-op.
        /// </summary>
        private void EmitStructCopy(int destBase, int srcBase, int count)
        {
            if (destBase == srcBase) return;
            if (count >= 3)
            {
                Emit(OpCode.COPY_BLOCK, destBase, srcBase, count);
            }
            else
            {
                for (int i = 0; i < count; i++)
                    Emit(OpCode.MOVE, destBase + i, srcBase + i);
            }
        }

        /// <summary>
        /// SN2: Compile a struct literal expression into a target register range.
        /// Validates type match, field names, field count, and recursively handles nested literals.
        /// </summary>
        private void CompileStructLiteral(StructLiteralExpr literal, string expectedType, int baseReg, int errorLine)
        {
            if (literal.TypeName != expectedType)
            {
                _errors.Add($"Struct literal type '{literal.TypeName}' does not match expected type '{expectedType}' (line {errorLine})");
                return;
            }

            if (!_structTypes.TryGetValue(literal.TypeName, out var structDecl))
            {
                _errors.Add($"Unknown struct type '{literal.TypeName}' in struct literal (line {literal.Line})");
                return;
            }

            if (literal.Fields.Count != structDecl.Fields.Count)
            {
                _errors.Add($"Struct literal for '{literal.TypeName}' has {literal.Fields.Count} fields, expected {structDecl.Fields.Count} (line {literal.Line})");
                return;
            }

            var flatInfo = _flatStructInfo[literal.TypeName];
            int offset = 0;

            for (int i = 0; i < literal.Fields.Count; i++)
            {
                var (fieldName, valueExpr) = literal.Fields[i];
                var expectedField = structDecl.Fields[i];

                if (fieldName != expectedField.Name)
                {
                    _errors.Add($"Field name mismatch in struct literal '{literal.TypeName}': expected '{expectedField.Name}', got '{fieldName}' (line {literal.Line})");
                    return;
                }

                if (_structTypes.ContainsKey(expectedField.TypeName))
                {
                    // Nested struct field — must be a struct literal or struct var
                    if (valueExpr is StructLiteralExpr nestedLiteral)
                    {
                        int nestedFlatCount = _flatStructInfo[expectedField.TypeName].FlatFieldCount;
                        CompileStructLiteral(nestedLiteral, expectedField.TypeName, baseReg + offset, literal.Line);
                        offset += nestedFlatCount;
                    }
                    else if (valueExpr is IdentifierExpr srcIdent &&
                             _structVarTypes.TryGetValue(srcIdent.Name, out var srcType) &&
                             srcType == expectedField.TypeName)
                    {
                        int nestedFlatCount = _flatStructInfo[expectedField.TypeName].FlatFieldCount;
                        int srcBase = _variables[srcIdent.Name];
                        EmitStructCopy(baseReg + offset, srcBase, nestedFlatCount);
                        offset += nestedFlatCount;
                    }
                    else
                    {
                        _errors.Add($"Field '{fieldName}' of struct literal '{literal.TypeName}' requires a '{expectedField.TypeName}' struct literal or variable (line {literal.Line})");
                        int nestedFlatCount = _flatStructInfo[expectedField.TypeName].FlatFieldCount;
                        offset += nestedFlatCount;
                    }
                }
                else
                {
                    // Scalar field — compile expression into target register
                    int valueReg = CompileExpr(valueExpr, destReg: baseReg + offset);
                    if (valueReg != baseReg + offset)
                        Emit(OpCode.MOVE, baseReg + offset, valueReg);
                    offset++;
                }
            }
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
            _wideA[instrIP] = targetIP;  // O8: update wide A value
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
                    {
                        TryReleaseVar(toRelease[r]);
                        // E001 fix: remove from liveRanges to prevent double-free.
                        // Without this, a released variable whose register was reused
                        // by another variable would pass the "alreadyFreed" check again
                        // (since the register is no longer in _freeVarRegs after reuse),
                        // causing the same register to be freed and reused a second time.
                        _liveRanges.Remove(toRelease[r]);
                    }
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
            // Lang-1: prevent local var/const from shadowing module-level declarations
            if (_moduleVariables != null && _moduleVariables.ContainsKey(stmt.Name))
            {
                _errors.Add($"Local variable '{stmt.Name}' shadows module-level variable (line {stmt.Line})");
                return;
            }
            if (_moduleConstValues != null && _moduleConstValues.ContainsKey(stmt.Name))
            {
                _errors.Add($"Local variable '{stmt.Name}' shadows module-level constant (line {stmt.Line})");
                return;
            }

            // Check if this is a struct variable
            if (_structTypes.TryGetValue(stmt.TypeName, out var structDecl))
            {
                // SN1: use flattened field count for nested struct support
                var flatInfo = _flatStructInfo[stmt.TypeName];
                int flatCount = flatInfo.FlatFieldCount;

                // F4: use DeclareStructVar for register reuse of consecutive slots
                int baseReg = DeclareStructVar(stmt.Name, flatCount);
                _structVarTypes[stmt.Name] = stmt.TypeName;

                // DBG2: record symbol entry for struct variable with flattened field names
                var fieldNames = new string[flatCount];
                for (int fi = 0; fi < flatCount; fi++)
                    fieldNames[fi] = flatInfo.FlatFields[fi].DotPath;
                _symbolEntries.Add(new SymbolEntry(stmt.Name, baseReg, flatCount, fieldNames, _currentFunctionName));

                // Initialize: if initializer is another struct var of same type, emit N × MOVE
                // SN2: or struct literal of same type
                if (stmt.Initializer != null)
                {
                    if (stmt.Initializer is IdentifierExpr srcIdent &&
                        _structVarTypes.TryGetValue(srcIdent.Name, out var srcType) &&
                        srcType == stmt.TypeName)
                    {
                        int srcBase = _variables[srcIdent.Name];
                        EmitStructCopy(baseReg, srcBase, flatCount);
                    }
                    else if (stmt.Initializer is StructLiteralExpr literal)
                    {
                        CompileStructLiteral(literal, stmt.TypeName, baseReg, stmt.Line);
                    }
                    else
                    {
                        _errors.Add($"Struct variable '{stmt.Name}' can only be initialized from another struct of same type or struct literal (line {stmt.Line})");
                    }
                }
                else
                {
                    // Default initialize all fields to 0
                    int ci = AddConst(Number.Zero);
                    for (int i = 0; i < flatCount; i++)
                        Emit(OpCode.LOAD_CONST, baseReg + i, ci);
                }
                return;
            }

            // Scalar variable (original path)

            // B-ε3: const — fold to compile-time constant, no register allocation
            if (stmt.IsConst)
            {
                if (stmt.Initializer == null)
                {
                    _errors.Add($"'const' requires an initializer (line {stmt.Line})");
                    return;
                }
                if (TryFoldConstant(stmt.Initializer, out Number constVal))
                {
                    _constValues[stmt.Name] = constVal;
                    return; // no register, no instruction emitted
                }
                _errors.Add($"'const' initializer must be a compile-time constant (line {stmt.Line})");
                return;
            }

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
                EmitLoadConst(AddConst(Number.Zero), reg);
            }
        }

        // ===== B-ζ1: LICM — Loop-Invariant Constant Motion =====

        private const int MaxHoistedPerLoop = 8;

        /// <summary>
        /// Walk an AST subtree and collect all Number constants that would generate LOAD_CONST.
        /// </summary>
        private void CollectLoopLiterals(ASTNode node, HashSet<Number> result)
        {
            if (node == null) return;
            // Constant-foldable expressions: collect folded value, don't recurse
            if ((node is BinaryExpr || node is UnaryExpr) && TryFoldConstant((Expr)node, out Number folded))
            {
                result.Add(folded);
                return;
            }
            if (node is IntLiteralExpr intLit) { result.Add(Number.FromInt(intLit.Value)); return; }
            if (node is NumberLiteralExpr numLit) { result.Add(Number.FromFloat(numLit.Value)); return; }
            if (node is BoolLiteralExpr boolLit) { result.Add(boolLit.Value ? Number.One : Number.Zero); return; }
            // const identifier — inline value
            if (node is IdentifierExpr ident && _constValues != null && _constValues.TryGetValue(ident.Name, out Number cv))
            {
                result.Add(cv);
                return;
            }
            // Recurse into children
            if (node is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++) CollectLoopLiterals(block.Statements[i], result);
            }
            else if (node is ExprStmt es) CollectLoopLiterals(es.Expression, result);
            else if (node is VarDeclStmt vd)
            {
                if (vd.Initializer != null) CollectLoopLiterals(vd.Initializer, result);
                else result.Add(Number.Zero); // default init
            }
            else if (node is IfStmt ifs)
            {
                CollectLoopLiterals(ifs.Condition, result);
                CollectLoopLiterals(ifs.ThenBranch, result);
                if (ifs.ElseBranch != null) CollectLoopLiterals(ifs.ElseBranch, result);
            }
            else if (node is WhileStmt ws) { CollectLoopLiterals(ws.Condition, result); CollectLoopLiterals(ws.Body, result); }
            else if (node is ForStmt fs)
            {
                if (fs.Initializer != null) CollectLoopLiterals(fs.Initializer, result);
                if (fs.Condition != null) CollectLoopLiterals(fs.Condition, result);
                if (fs.Increment != null) CollectLoopLiterals(fs.Increment, result);
                CollectLoopLiterals(fs.Body, result);
            }
            else if (node is ReturnStmt rs) { if (rs.Value != null) CollectLoopLiterals(rs.Value, result); }
            else if (node is BinaryExpr bin) { CollectLoopLiterals(bin.Left, result); CollectLoopLiterals(bin.Right, result); }
            else if (node is UnaryExpr un) { CollectLoopLiterals(un.Operand, result); }
            else if (node is AssignExpr ae) { CollectLoopLiterals(ae.Target, result); CollectLoopLiterals(ae.Value, result); }
            else if (node is CallExpr ce) { for (int i = 0; i < ce.Arguments.Count; i++) CollectLoopLiterals(ce.Arguments[i], result); }
            else if (node is FieldAccessExpr fa) { CollectLoopLiterals(fa.Target, result); }
            else if (node is StructLiteralExpr sl) { for (int i = 0; i < sl.Fields.Count; i++) CollectLoopLiterals(sl.Fields[i].Value, result); }
            else if (node is UsingStmt us2) { for (int i = 0; i < us2.Arguments.Count; i++) CollectLoopLiterals(us2.Arguments[i], result); CollectLoopLiterals(us2.Body, result); }
            else if (node is DeferStmt ds) { CollectLoopLiterals(ds.Body, result); }
        }

        /// <summary>
        /// Hoist loop-invariant constants: emit LOAD_CONST before the loop, populate _hoistedConstants.
        /// Returns the previous hoisted map for restoration after the loop.
        /// </summary>
        private Dictionary<int, int> BeginLoopHoist(params ASTNode[] bodyNodes)
        {
            var saved = _hoistedConstants;
            var literals = new HashSet<Number>();
            for (int i = 0; i < bodyNodes.Length; i++)
                CollectLoopLiterals(bodyNodes[i], literals);
            if (literals.Count == 0) return saved;

            // Inherit existing hoisted map
            _hoistedConstants = saved != null
                ? new Dictionary<int, int>(saved)
                : new Dictionary<int, int>();

            int hoisted = 0;
            foreach (var val in literals)
            {
                if (hoisted >= MaxHoistedPerLoop) break;
                int ci = AddConst(val);
                if (_hoistedConstants.ContainsKey(ci)) continue; // already hoisted by outer loop
                int reg = DeclareVar($"$lc{_licmId++}");
                Emit(OpCode.LOAD_CONST, reg, ci);
                _hoistedConstants[ci] = reg;
                hoisted++;
            }
            ResetTemps();
            return saved;
        }

        private void EndLoopHoist(Dictionary<int, int> saved)
        {
            _hoistedConstants = saved;
        }

        // ===== B-ζ2: CMP-immediate — fused constant-compare-and-jump =====

        /// <summary>
        /// Try to emit a fused JUMP_IF_*_K for a comparison condition with a constant operand.
        /// Emits a "skip-when-false" jump (same semantics as JUMP_IF_ZERO for the condition).
        /// Returns true if emitted, with jumpIP for backpatching.
        /// </summary>
        private bool TryEmitKJump(Expr condition, out int jumpIP)
        {
            jumpIP = -1;
            if (!(condition is BinaryExpr bin)) return false;

            // Must be a comparison operator
            NodeKind kind = bin.Kind;
            if (kind != NodeKind.Eq && kind != NodeKind.Neq &&
                kind != NodeKind.Lt && kind != NodeKind.Lte &&
                kind != NodeKind.Gt && kind != NodeKind.Gte)
                return false;

            // Try right operand as constant
            if (TryFoldConstant(bin.Right, out Number rightVal))
            {
                int regLeft = CompileExpr(bin.Left);
                int ci = AddConst(rightVal);
                jumpIP = _instructions.Count;
                Emit(InvertedKOp(kind), 0, regLeft, ci);
                return true;
            }

            // Try left operand as constant (swap sides + flip comparison)
            if (TryFoldConstant(bin.Left, out Number leftVal))
            {
                int regRight = CompileExpr(bin.Right);
                int ci = AddConst(leftVal);
                jumpIP = _instructions.Count;
                Emit(InvertedKOp(SwapCompare(kind)), 0, regRight, ci);
                return true;
            }

            return false;
        }

        /// <summary>Returns the _K opcode that jumps when the comparison is FALSE (inverted).</summary>
        private static OpCode InvertedKOp(NodeKind cmp)
        {
            switch (cmp)
            {
                case NodeKind.Eq:  return OpCode.JUMP_IF_NEQ_K;
                case NodeKind.Neq: return OpCode.JUMP_IF_EQ_K;
                case NodeKind.Lt:  return OpCode.JUMP_IF_GTE_K;
                case NodeKind.Lte: return OpCode.JUMP_IF_GT_K;
                case NodeKind.Gt:  return OpCode.JUMP_IF_LTE_K;
                case NodeKind.Gte: return OpCode.JUMP_IF_LT_K;
                default: return OpCode.NOP;
            }
        }

        /// <summary>Swap comparison direction: a &lt; b → b &gt; a (for constant-on-left).</summary>
        private static NodeKind SwapCompare(NodeKind cmp)
        {
            switch (cmp)
            {
                case NodeKind.Lt:  return NodeKind.Gt;
                case NodeKind.Lte: return NodeKind.Gte;
                case NodeKind.Gt:  return NodeKind.Lt;
                case NodeKind.Gte: return NodeKind.Lte;
                default: return cmp; // Eq, Neq are commutative
            }
        }

        // ===== B-ζ3: SWITCH jump table compilation =====

        /// <summary>
        /// Try to compile an if-else-if chain as a SWITCH jump table dispatch.
        /// Pattern: if (v == 0) { ... } else if (v == 1) { ... } else if (v == 2) { ... } else { ... }
        /// Requirements: same variable, == comparisons, consecutive integer constants starting from 0, ≥3 cases.
        /// </summary>
        private bool TryCompileSwitch(IfStmt stmt)
        {
            // 1. Walk the if-else-if chain and collect cases
            var caseBlocks = new List<(int constVal, Stmt body)>();
            string switchVar = null;
            Stmt defaultBlock = null;
            IfStmt current = stmt;

            while (current != null)
            {
                // Condition must be a BinaryExpr with Eq kind
                if (!(current.Condition is BinaryExpr bin) || bin.Kind != NodeKind.Eq)
                    return false;

                // One side must be an identifier, the other a foldable integer constant
                string varName;
                int constVal;

                if (bin.Left is IdentifierExpr leftId && TryFoldConstant(bin.Right, out Number rightVal))
                {
                    varName = leftId.Name;
                    constVal = rightVal.ToInt();
                    if (Number.FromInt(constVal) != rightVal) return false; // not an exact integer
                }
                else if (bin.Right is IdentifierExpr rightId && TryFoldConstant(bin.Left, out Number leftVal))
                {
                    varName = rightId.Name;
                    constVal = leftVal.ToInt();
                    if (Number.FromInt(constVal) != leftVal) return false;
                }
                else return false;

                // All branches must test the same variable
                if (switchVar == null) switchVar = varName;
                else if (switchVar != varName) return false;

                // Variable must be declared
                if (!_variables.ContainsKey(switchVar)) return false;

                caseBlocks.Add((constVal, current.ThenBranch));

                if (current.ElseBranch is IfStmt nextIf)
                    current = nextIf;
                else
                {
                    defaultBlock = current.ElseBranch; // may be null
                    current = null;
                }
            }

            // 2. Need ≥3 cases for SWITCH to be worthwhile
            if (caseBlocks.Count < 3) return false;

            // 3. Sort by constant value and check consecutive from 0
            caseBlocks.Sort((a, b) => a.constVal.CompareTo(b.constVal));
            if (caseBlocks[0].constVal != 0) return false;
            for (int i = 1; i < caseBlocks.Count; i++)
                if (caseBlocks[i].constVal != i) return false;

            // 4. Emit SWITCH instruction (placeholder for default IP)
            int testReg = _variables[switchVar];
            int tableSize = caseBlocks.Count;
            int jumpTableIdx = _jumpTables.Count;
            int[] jumpTable = new int[tableSize];
            _jumpTables.Add(jumpTable);

            int switchIP = CurrentIP();
            Emit(OpCode.SWITCH, 0, testReg, jumpTableIdx); // A=defaultIP placeholder
            ResetTemps();

            // 5. Compile each case block, record entry IPs
            var endJumps = new List<int>();
            for (int i = 0; i < tableSize; i++)
            {
                jumpTable[i] = CurrentIP();
                CompileStmt(caseBlocks[i].body);
                endJumps.Add(EmitJump(OpCode.JUMP));
                ResetTemps();
            }

            // 6. Default block
            int defaultIP = CurrentIP();
            if (defaultBlock != null)
                CompileStmt(defaultBlock);

            // 7. End of switch
            int endIP = CurrentIP();

            // 8. Backpatch: SWITCH.A = defaultIP, all end-of-case JUMPs → endIP
            _instructions[switchIP] = new Instruction(OpCode.SWITCH, defaultIP, testReg, jumpTableIdx);
            _wideA[switchIP] = defaultIP;
            for (int i = 0; i < endJumps.Count; i++)
                Backpatch(endJumps[i], endIP);

            return true;
        }

        private void CompileIf(IfStmt stmt)
        {
            // B-ζ3: try SWITCH jump table for if-else-if chains
            if (stmt.ElseBranch is IfStmt && TryCompileSwitch(stmt))
                return;

            // B-ε3: DCE — constant condition eliminates dead branch
            if (TryFoldConstant(stmt.Condition, out Number condVal))
            {
                if (condVal != Number.Zero)
                    CompileStmt(stmt.ThenBranch);  // condition is true
                else if (stmt.ElseBranch != null)
                    CompileStmt(stmt.ElseBranch);  // condition is false, compile else
                return;
            }

            // B-ζ2: try fused constant-compare-and-jump
            int jumpElseIP;
            if (TryEmitKJump(stmt.Condition, out jumpElseIP))
            {
                // emitted JUMP_IF_*_K directly
            }
            else
            {
                int condReg = CompileExpr(stmt.Condition);
                jumpElseIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            }
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
            // B-ε3: DCE — while(false) is dead code
            if (TryFoldConstant(stmt.Condition, out Number condVal) && condVal == Number.Zero)
                return; // entire loop eliminated

            // B-ζ1: LICM — hoist loop-invariant constants before the loop
            var savedHoist = BeginLoopHoist(stmt.Condition, stmt.Body);

            int loopStart = CurrentIP();
            // B-ζ2: try fused constant-compare-and-jump
            int jumpEndIP;
            if (TryEmitKJump(stmt.Condition, out jumpEndIP))
            {
                // emitted JUMP_IF_*_K directly
            }
            else
            {
                int condReg = CompileExpr(stmt.Condition);
                jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            }
            ResetTemps();

            CompileStmt(stmt.Body);
            Emit(OpCode.JUMP, loopStart);
            Backpatch(jumpEndIP, CurrentIP());

            EndLoopHoist(savedHoist);
        }

        private void CompileFor(ForStmt stmt)
        {
            // B-ε4: detect canonical for-loop pattern → emit FORLOOP super-instruction
            if (TryCompileForLoop(stmt))
                return;

            if (stmt.Initializer != null)
            {
                CompileStmt(stmt.Initializer);
                ResetTemps();
            }

            // B-ζ1: LICM — hoist loop-invariant constants (condition + body + increment)
            var savedHoist = BeginLoopHoist(stmt.Condition, stmt.Body, stmt.Increment);

            int loopStart = CurrentIP();

            // Condition (if null, treat as always true → infinite loop)
            int jumpEndIP = -1;
            if (stmt.Condition != null)
            {
                // B-ζ2: try fused constant-compare-and-jump
                if (TryEmitKJump(stmt.Condition, out jumpEndIP))
                {
                    // emitted JUMP_IF_*_K directly
                }
                else
                {
                    int condReg = CompileExpr(stmt.Condition);
                    jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
                }
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

            EndLoopHoist(savedHoist);
        }

        /// <summary>
        /// B-ε4: Try to compile a for-loop as a FORLOOP super-instruction.
        /// Pattern: for (var counter = INIT; counter &lt; LIMIT; counter = counter + 1) { body }
        /// Emits: init → LOAD_CONST limit → JUMP_IF_GTE exit → body → FORLOOP loopBody
        /// </summary>
        private bool TryCompileForLoop(ForStmt stmt)
        {
            // 1) Init must be a scalar VarDeclStmt (not const, not struct)
            if (!(stmt.Initializer is VarDeclStmt initDecl) || initDecl.IsConst)
                return false;
            string counterName = initDecl.Name;

            // 2) Condition must be: counter < LIMIT
            if (!(stmt.Condition is BinaryExpr cond) || cond.Kind != NodeKind.Lt)
                return false;
            if (!(cond.Left is IdentifierExpr condLeft) || condLeft.Name != counterName)
                return false;
            Expr limitExpr = cond.Right;

            // 3) Increment must be: counter = counter + 1
            if (!(stmt.Increment is AssignExpr incr))
                return false;
            if (!(incr.Target is IdentifierExpr incrTarget) || incrTarget.Name != counterName)
                return false;
            if (!(incr.Value is BinaryExpr incrBin) || incrBin.Kind != NodeKind.Add)
                return false;
            if (!(incrBin.Left is IdentifierExpr incrLeft) || incrLeft.Name != counterName)
                return false;
            bool stepIsOne = (incrBin.Right is IntLiteralExpr stepInt && stepInt.Value == 1)
                          || (incrBin.Right is NumberLiteralExpr stepNum && stepNum.Value == 1.0f);
            if (!stepIsOne)
                return false;

            // --- Pattern matched: emit FORLOOP ---

            // Compile init (declares counter var + loads initial value)
            CompileStmt(stmt.Initializer);
            ResetTemps();
            int counterReg = ResolveVar(counterName);

            // Get limit into a persistent register
            int limitReg;
            if (limitExpr is IdentifierExpr limitIdent
                && _variables.TryGetValue(limitIdent.Name, out int existingReg))
            {
                // Limit is an existing variable — use its register directly
                limitReg = existingReg;
            }
            else
            {
                // Allocate hidden variable for limit, compile expression into it
                limitReg = DeclareVar($"$fl{_forLoopId++}");
                CompileExpr(limitExpr, destReg: limitReg);
                ResetTemps();
            }

            // Initial check: if counter >= limit → skip loop entirely
            int exitIP = _instructions.Count;
            Emit(OpCode.JUMP_IF_GTE, 0, counterReg, limitReg); // A=placeholder
            ResetTemps();

            // B-ζ1: LICM — hoist loop-invariant constants before body
            var savedHoist = BeginLoopHoist(stmt.Body);

            // Loop body
            int loopBodyIP = CurrentIP();
            CompileStmt(stmt.Body);

            // FORLOOP: counter += 1; if counter < limit → goto loopBody
            Emit(OpCode.FORLOOP, loopBodyIP, counterReg, limitReg);

            EndLoopHoist(savedHoist);

            // Backpatch exit jump
            Backpatch(exitIP, CurrentIP());
            return true;
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
            // B-ε3: const identifier propagation
            if (expr is IdentifierExpr ident && _constValues != null && _constValues.TryGetValue(ident.Name, out value))
            {
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
                    return EmitLoadConst(AddConst(foldedValue), destReg);
                }
            }

            if (expr is IntLiteralExpr intLit)
            {
                return EmitLoadConst(AddConst(Number.FromInt(intLit.Value)), destReg);
            }

            if (expr is NumberLiteralExpr numLit)
            {
                return EmitLoadConst(AddConst(Number.FromFloat(numLit.Value)), destReg);
            }

            if (expr is BoolLiteralExpr boolLit)
            {
                return EmitLoadConst(AddConst(boolLit.Value ? Number.One : Number.Zero), destReg);
            }

            // STR1: String literal → store index into string constant pool as numeric constant
            if (expr is StringLiteralExpr strLit)
            {
                int strIdx = AddStringConst(strLit.Value);
                return EmitLoadConst(AddConst(Number.FromInt(strIdx)), destReg);
            }

            if (expr is IdentifierExpr ident)
            {
                // B-ε3: const propagation — inline constant value
                if (_constValues != null && _constValues.TryGetValue(ident.Name, out Number constVal))
                {
                    return EmitLoadConst(AddConst(constVal), destReg);
                }
                return ResolveVar(ident.Name);
            }

            if (expr is FieldAccessExpr fieldAccess)
            {
                return ResolveFieldAccess(fieldAccess);
            }

            if (expr is BinaryExpr bin)
            {
                if (ContainsStringLiteral(bin.Left) || ContainsStringLiteral(bin.Right))
                {
                    _errors.Add($"String literals cannot be used in arithmetic/comparison expressions (line {bin.Line})");
                    return destReg >= 0 ? destReg : AllocTemp();
                }
                int left = CompileExpr(bin.Left);
                int right = CompileExpr(bin.Right);
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(BinOpCode(bin.Kind), dest, left, right);
                return dest;
            }

            if (expr is UnaryExpr un)
            {
                if (ContainsStringLiteral(un.Operand))
                {
                    _errors.Add($"String literals cannot be used in unary expressions (line {un.Line})");
                    return destReg >= 0 ? destReg : AllocTemp();
                }
                int operand = CompileExpr(un.Operand);
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(UnOpCode(un.Kind), dest, operand);
                return dest;
            }

            if (expr is AssignExpr assign)
            {
                // Struct whole assignment: a = b (both are struct variables of same type)
                // SN2: or a = TypeName { ... } struct literal
                if (assign.Target is IdentifierExpr targetIdent &&
                    _structVarTypes.TryGetValue(targetIdent.Name, out var targetStructType))
                {
                    if (assign.Value is IdentifierExpr srcIdent &&
                        _structVarTypes.TryGetValue(srcIdent.Name, out var srcStructType) &&
                        srcStructType == targetStructType)
                    {
                        // SN1: use flat field count for nested struct whole-copy
                        int flatCount = _flatStructInfo[targetStructType].FlatFieldCount;
                        int destBase = _variables[targetIdent.Name];
                        int srcBase = _variables[srcIdent.Name];
                        EmitStructCopy(destBase, srcBase, flatCount);
                        return destBase;
                    }
                    if (assign.Value is StructLiteralExpr literal)
                    {
                        int destBase = _variables[targetIdent.Name];
                        CompileStructLiteral(literal, targetStructType, destBase, assign.Line);
                        return destBase;
                    }
                    _errors.Add($"Cannot assign non-struct value to struct variable '{targetIdent.Name}' (line {assign.Line})");
                    return ResolveVar(targetIdent.Name);
                }

                // Field assignment: d.field = expr  OR  d.inner = other.inner (sub-struct copy)
                if (assign.Target is FieldAccessExpr fieldTarget)
                {
                    // SN1: check if target field is a sub-struct → whole sub-struct copy
                    if (assign.Value is FieldAccessExpr srcFieldAccess)
                    {
                        string targetVar, targetDotPath, srcVar, srcDotPath;
                        CollectFieldChain(fieldTarget, out targetVar, out targetDotPath);
                        CollectFieldChain(srcFieldAccess, out srcVar, out srcDotPath);

                        if (targetVar != null && srcVar != null &&
                            _structVarTypes.TryGetValue(targetVar, out var tType) &&
                            _structVarTypes.TryGetValue(srcVar, out var sType))
                        {
                            // Check if both dot-paths resolve to the same struct field type
                            string targetFieldType = ResolveFieldChainType(tType, targetDotPath);
                            string srcFieldType = ResolveFieldChainType(sType, srcDotPath);
                            if (targetFieldType != null && srcFieldType != null &&
                                targetFieldType == srcFieldType &&
                                _structTypes.ContainsKey(targetFieldType))
                            {
                                int subCount = _flatStructInfo[targetFieldType].FlatFieldCount;
                                int tBaseReg = _variables[targetVar] + ResolveFlatFieldOffset(tType, targetDotPath);
                                int sBaseReg = _variables[srcVar] + ResolveFlatFieldOffset(sType, srcDotPath);
                                EmitStructCopy(tBaseReg, sBaseReg, subCount);
                                return tBaseReg;
                            }
                        }
                    }
                    // SN1: check if target field resolves to a sub-struct and value is an identifier (var copy)
                    if (assign.Value is IdentifierExpr subSrcIdent &&
                        _structVarTypes.TryGetValue(subSrcIdent.Name, out var subSrcType))
                    {
                        string tgtVar2, tgtDotPath2;
                        CollectFieldChain(fieldTarget, out tgtVar2, out tgtDotPath2);
                        if (tgtVar2 != null && _structVarTypes.TryGetValue(tgtVar2, out var tType2))
                        {
                            string targetFieldType2 = ResolveFieldChainType(tType2, tgtDotPath2);
                            if (targetFieldType2 != null && targetFieldType2 == subSrcType &&
                                _structTypes.ContainsKey(targetFieldType2))
                            {
                                int subCount = _flatStructInfo[targetFieldType2].FlatFieldCount;
                                int tBaseReg = _variables[tgtVar2] + ResolveFlatFieldOffset(tType2, tgtDotPath2);
                                int sBaseReg = _variables[subSrcIdent.Name];
                                EmitStructCopy(tBaseReg, sBaseReg, subCount);
                                return tBaseReg;
                            }
                        }
                    }

                    // Scalar field assignment (original path)
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
                    // B-ε3: prevent assignment to const
                    if (_constValues != null && _constValues.ContainsKey(scalarTarget.Name))
                    {
                        _errors.Add($"Cannot assign to 'const' variable '{scalarTarget.Name}' (line {assign.Line})");
                        return destReg >= 0 ? destReg : AllocTemp();
                    }
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
        /// S4: struct arguments expand to multiple consecutive scratch registers.
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

            // Validate parameter count (FF3: allow fewer args if remaining params have defaults)
            if (_funcDecls.TryGetValue(call.FunctionName, out var funcDecl))
            {
                int requiredCount = 0;
                for (int i = 0; i < funcDecl.Parameters.Count; i++)
                {
                    if (funcDecl.Parameters[i].DefaultValue == null)
                        requiredCount++;
                    else
                        break; // once defaults start, all remaining are optional
                }

                if (call.Arguments.Count < requiredCount || call.Arguments.Count > funcDecl.Parameters.Count)
                {
                    _errors.Add($"Function '{call.FunctionName}' expects {requiredCount}-{funcDecl.Parameters.Count} arguments but got {call.Arguments.Count} (line {call.Line})");
                    return;
                }

                // S4/R5/SN1: validate total scratch registers using flat field count
                int totalScratchRegs = 0;
                for (int i = 0; i < funcDecl.Parameters.Count; i++)
                {
                    int fc = GetFlatFieldCount(funcDecl.Parameters[i].TypeName);
                    totalScratchRegs += fc > 0 ? fc : 1;
                }
                if (totalScratchRegs > VarRegBase)
                {
                    _errors.Add($"Function '{call.FunctionName}' requires {totalScratchRegs} scratch registers for parameters (max {VarRegBase}) (line {call.Line})");
                    return;
                }
            }

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to scratch zone r0, r1, ... (shared, not windowed)
            // S4/SN1: struct arguments expand to N consecutive scratch registers (flat count)
            {
                int scratchReg = 0;
                for (int i = 0; i < call.Arguments.Count; i++)
                {
                    // Check if this argument is a struct variable
                    bool isStructArg = false;
                    if (call.Arguments[i] is IdentifierExpr argIdent &&
                        _structVarTypes.TryGetValue(argIdent.Name, out var argTypeName) &&
                        _flatStructInfo.TryGetValue(argTypeName, out var argFlatInfo))
                    {
                        isStructArg = true;
                        int srcBase = argRegs[i]; // base register of the struct
                        for (int j = 0; j < argFlatInfo.FlatFieldCount; j++)
                        {
                            if (srcBase + j != scratchReg + j)
                                Emit(OpCode.MOVE, scratchReg + j, srcBase + j);
                        }
                        scratchReg += argFlatInfo.FlatFieldCount;
                    }

                    if (!isStructArg)
                    {
                        if (argRegs[i] != scratchReg)
                            Emit(OpCode.MOVE, scratchReg, argRegs[i]);
                        scratchReg++;
                    }
                }

                // FF3: fill default values for omitted optional parameters
                if (funcDecl != null)
                {
                    for (int i = call.Arguments.Count; i < funcDecl.Parameters.Count; i++)
                    {
                        var def = funcDecl.Parameters[i].DefaultValue;
                        if (def != null)
                        {
                            int defReg = CompileExpr(def);
                            if (defReg != scratchReg)
                                Emit(OpCode.MOVE, scratchReg, defReg);
                        }
                        scratchReg++;
                    }
                }
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

        // ===== FO7: Static call depth analysis + FO6: register window overflow =====

        /// <summary>
        /// Build a call graph from the AST and compute max call depth.
        /// If max depth exceeds MaxCallDepth, report a compile error.
        /// Detects recursion (cycles) and marks as "dynamic depth — requires runtime check".
        /// FO6: also validates that cumulative register window sizes don't exceed available slots.
        /// </summary>
        private void AnalyzeCallDepth(ModuleNode module, string entryFunc, List<FunctionEntry> functionEntries)
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

            // FO6: validate cumulative register window doesn't overflow available slots
            var funcWindows = new Dictionary<string, int>();
            for (int i = 0; i < functionEntries.Count; i++)
                funcWindows[functionEntries[i].Name] = functionEntries[i].LocalRegCount;

            var windowVisited = new Dictionary<string, int>(); // funcName → max cumulative window (-1 = in progress)
            // Lang-1: windowed zone is r16..r55 (VarRegBase to ModuleVarRegBase-1)
            int availableSlots = ModuleVarRegBase - VarRegBase; // 56 - 16 = 40

            int ComputeMaxWindow(string funcName)
            {
                if (windowVisited.TryGetValue(funcName, out int cached))
                {
                    if (cached == -1) return 0; // cycle (recursion) — skip for static analysis
                    return cached;
                }
                windowVisited[funcName] = -1; // mark in progress

                int myWindow = funcWindows.TryGetValue(funcName, out int w) ? w : 0;

                if (!callGraph.TryGetValue(funcName, out var callees) || callees.Count == 0)
                {
                    windowVisited[funcName] = myWindow;
                    return myWindow;
                }

                int maxTotal = myWindow; // leaf case: just this function
                foreach (var callee in callees)
                {
                    if (!callGraph.ContainsKey(callee)) continue;
                    int calleeWindow = ComputeMaxWindow(callee);
                    int total = myWindow + calleeWindow;
                    if (total > maxTotal) maxTotal = total;
                }

                windowVisited[funcName] = maxTotal;
                return maxTotal;
            }

            int maxWindow = callGraph.ContainsKey(entryFunc) ? ComputeMaxWindow(entryFunc) : 0;
            if (maxWindow > availableSlots)
            {
                _errors.Add($"Static register window depth from '{entryFunc}' requires {maxWindow} registers, exceeding available {availableSlots} slots (windowed zone r{VarRegBase}..r{ModuleVarRegBase - 1}). Reduce local variable usage or function nesting depth.");
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
        /// FF5: functions with defer/using are also non-leaf (need CALL/RET_FUNC for CleanupBase).
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
                // FF5: functions with defer/using need full CALL/RET_FUNC path for cleanup chain
                if (ContainsDeferOrUsing(func.Body))
                {
                    _leafFunctions[func.Name] = false;
                    continue;
                }
                _leafFunctions[func.Name] = !ContainsNonLeafNode(func.Body);
            }
        }

        /// <summary>
        /// FF5: Returns true if the statement subtree contains a DeferStmt or UsingStmt.
        /// Functions with defer/using cannot use the leaf optimization because
        /// CALL_LEAF/RET_LEAF don't preserve CleanupBase for cleanup chain alignment.
        /// </summary>
        private bool ContainsDeferOrUsing(Stmt stmt)
        {
            if (stmt == null) return false;
            if (stmt is DeferStmt || stmt is UsingStmt) return true;

            if (stmt is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                    if (ContainsDeferOrUsing(block.Statements[i])) return true;
            }
            else if (stmt is IfStmt ifStmt)
            {
                if (ContainsDeferOrUsing(ifStmt.ThenBranch)) return true;
                if (ifStmt.ElseBranch != null && ContainsDeferOrUsing(ifStmt.ElseBranch)) return true;
            }
            else if (stmt is WhileStmt whileStmt)
            {
                if (ContainsDeferOrUsing(whileStmt.Body)) return true;
            }
            else if (stmt is ForStmt forStmt)
            {
                if (ContainsDeferOrUsing(forStmt.Body)) return true;
            }

            return false;
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
        /// Returns true if the expression subtree contains a CallExpr to a user function.
        /// SyscallExpr and calls to names not in _funcDecls are not disqualifying.
        /// </summary>
        private bool ContainsNonLeafExpr(Expr expr)
        {
            if (expr == null) return false;

            if (expr is CallExpr call)
            {
                // Only user function calls disqualify; syscalls don't use the call stack
                if (_funcDecls.ContainsKey(call.FunctionName))
                    return true;
                // Check arguments even for syscall-like calls
                for (int i = 0; i < call.Arguments.Count; i++)
                    if (ContainsNonLeafExpr(call.Arguments[i])) return true;
                return false;
            }

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

        /// <summary>P5: returns true if the opcode is a comparison (CMP_*).</summary>
        private static bool IsCmpOp(OpCode code)
        {
            return code >= OpCode.CMP_EQ && code <= OpCode.CMP_GTE;
        }

        /// <summary>
        /// P5: Check if a register is used as a source operand in any instruction
        /// from fromIP to the end of the containing function, BEFORE being overwritten.
        /// Returns true if the register is live (used as source before any write),
        /// meaning fusion is NOT safe.
        /// </summary>
        private bool IsRegUsedAsSourceAfter(int reg, int fromIP, int[] funcBounds, List<FunctionEntry> functionEntries, int totalCount)
        {
            // Determine function boundary for the instruction at fromIP-2 (the CMP position)
            int funcEnd = (fromIP >= 2) ? funcBounds[fromIP - 2] : totalCount;
            for (int j = fromIP; j < funcEnd; j++)
            {
                var instr = _instructions[j];
                byte mask = GetRegisterMask(instr.Code);

                // Check if reg appears as a source operand (B or C)
                if ((mask & 2) != 0 && instr.B == reg) return true;
                if ((mask & 4) != 0 && instr.C == reg) return true;
                if (instr.Code == OpCode.WAIT_FOR && instr.A == reg) return true;

                // Check if reg is overwritten (dest in A) — old value is dead
                // All instructions with mask bit 0 are dest-in-A, EXCEPT WAIT_FOR
                if ((mask & 1) != 0 && instr.A == reg && instr.Code != OpCode.WAIT_FOR)
                    return false; // overwritten before any read → safe to fuse
            }
            return false;
        }

        /// <summary>
        /// P5: Map a CMP_* opcode to the fused JUMP_IF_* opcode.
        /// When invertSense is true (JUMP_IF_ZERO), the comparison is inverted:
        ///   CMP_EQ  + JUMP_IF_ZERO → JUMP_IF_NEQ (jump when NOT equal)
        ///   CMP_LT  + JUMP_IF_ZERO → JUMP_IF_GTE (jump when NOT less-than)
        /// When invertSense is false (JUMP_IF_NOT_ZERO), the comparison is kept:
        ///   CMP_EQ  + JUMP_IF_NOT_ZERO → JUMP_IF_EQ
        /// </summary>
        private static OpCode FusedJumpFor(OpCode cmpOp, bool invertSense)
        {
            if (invertSense)
            {
                return cmpOp switch
                {
                    OpCode.CMP_EQ  => OpCode.JUMP_IF_NEQ,
                    OpCode.CMP_NEQ => OpCode.JUMP_IF_EQ,
                    OpCode.CMP_LT  => OpCode.JUMP_IF_GTE,
                    OpCode.CMP_LTE => OpCode.JUMP_IF_GT,
                    OpCode.CMP_GT  => OpCode.JUMP_IF_LTE,
                    OpCode.CMP_GTE => OpCode.JUMP_IF_LT,
                    _ => throw new System.InvalidOperationException($"Not a CMP opcode: {cmpOp}")
                };
            }
            return cmpOp switch
            {
                OpCode.CMP_EQ  => OpCode.JUMP_IF_EQ,
                OpCode.CMP_NEQ => OpCode.JUMP_IF_NEQ,
                OpCode.CMP_LT  => OpCode.JUMP_IF_LT,
                OpCode.CMP_LTE => OpCode.JUMP_IF_LTE,
                OpCode.CMP_GT  => OpCode.JUMP_IF_GT,
                OpCode.CMP_GTE => OpCode.JUMP_IF_GTE,
                _ => throw new System.InvalidOperationException($"Not a CMP opcode: {cmpOp}")
            };
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
                case OpCode.JUMP_IF_EQ:
                case OpCode.JUMP_IF_NEQ:
                case OpCode.JUMP_IF_LT:
                case OpCode.JUMP_IF_LTE:
                case OpCode.JUMP_IF_GT:
                case OpCode.JUMP_IF_GTE:
                case OpCode.JUMP_IF_EQ_K:
                case OpCode.JUMP_IF_NEQ_K:
                case OpCode.JUMP_IF_LT_K:
                case OpCode.JUMP_IF_LTE_K:
                case OpCode.JUMP_IF_GT_K:
                case OpCode.JUMP_IF_GTE_K:
                case OpCode.FORLOOP:
                case OpCode.CALL:
                case OpCode.CALL_LEAF:
                case OpCode.PUSH_CLEANUP:
                case OpCode.SWITCH:
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
        ///   P5: CMP_* rT,B,C ; JUMP_IF_ZERO/NOT_ZERO tgt,rT → JUMP_IF_* tgt,B,C (compare-and-branch fusion)
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
                    jumpTargets.Add(_wideA[i]);  // O8: use full int A value
                // B-ζ3: SWITCH jump table entries are also jump targets
                if (_instructions[i].Code == OpCode.SWITCH)
                {
                    int[] table = _jumpTables[_instructions[i].C];
                    for (int t = 0; t < table.Length; t++)
                        jumpTargets.Add(table[t]);
                }
            }

            // Phase 2: pattern matching — mark eliminated instructions as NOP
            // Use a bool array to track which instructions are eliminated (turned to NOP).
            // We keep the original NOP instructions intact (only eliminate optimizer-marked ones).
            var eliminated = new bool[count];

            // P5: precompute function boundaries for liveness scans.
            // funcBounds[ip] = end IP of the function containing instruction at ip.
            var funcBounds = new int[count];
            {
                int fi = 0;
                for (int ip = 0; ip < count; ip++)
                {
                    while (fi + 1 < functionEntries.Count && functionEntries[fi + 1].EntryIP <= ip)
                        fi++;
                    funcBounds[ip] = (fi + 1 < functionEntries.Count) ? functionEntries[fi + 1].EntryIP : count;
                }
            }

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
                if (ins.Code == OpCode.JUMP && _wideA[i] == i + 1)  // O8: use full int A
                {
                    eliminated[i] = true;
                    continue;
                }

                if (i + 1 >= count || eliminated[i + 1] || jumpTargets.Contains(i + 1))
                    continue;

                var next = _instructions[i + 1];

                // P2: dest-redirect — OP rT,… ; MOVE rV,rT → OP rV,…
                // Safety: only redirect when original dest (ins.A) is a temp register (≥ TempRegBase, < ModuleVarRegBase).
                // Variable registers and module variable registers may be read later;
                // redirecting away from them would break semantics.
                if (IsResultProducer(ins.Code) && next.Code == OpCode.MOVE
                    && next.B == ins.A && ins.A >= TempRegBase && ins.A < ModuleVarRegBase)
                {
                    _instructions[i] = new Instruction(ins.Code, next.A, ins.B, ins.C);
                    _wideA[i] = next.A;  // O8: register value, always byte-safe
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

                // P5: compare-and-branch fusion — CMP_* rT,B,C ; JUMP_IF_ZERO/NOT_ZERO tgt,rT
                // → JUMP_IF_* tgt,B,C (single fused instruction, eliminates CMP + saves 1 dispatch)
                // Note: after FO6 remap, temps are below TempRegBase, so we use a liveness scan
                // to verify the CMP dest register is dead after the pair.
                if (IsCmpOp(ins.Code)
                    && (next.Code == OpCode.JUMP_IF_ZERO || next.Code == OpCode.JUMP_IF_NOT_ZERO)
                    && next.B == ins.A
                    && !IsRegUsedAsSourceAfter(ins.A, i + 2, funcBounds, functionEntries, count))
                {
                    OpCode fused = FusedJumpFor(ins.Code, next.Code == OpCode.JUMP_IF_ZERO);
                    _instructions[i] = new Instruction(fused, _wideA[i + 1], ins.B, ins.C);
                    _wideA[i] = _wideA[i + 1];  // O8: propagate wide A from JUMP_IF target
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
            var newWideA = new List<int>(newIP);
            var newSourceLines = new List<int>(newIP);
            for (int i = 0; i < count; i++)
            {
                if (eliminated[i]) continue;

                var ins = _instructions[i];
                int wideAVal = _wideA[i];
                // Rebase jump targets
                if (HasJumpTargetInA(ins.Code))
                {
                    wideAVal = remap[wideAVal];  // O8: remap full int A
                    ins = new Instruction(ins.Code, wideAVal, ins.B, ins.C);
                }

                newInstructions.Add(ins);
                newWideA.Add(wideAVal);
                newSourceLines.Add(_sourceLines[i]);
            }

            _instructions = newInstructions;
            _wideA = newWideA;
            _sourceLines = newSourceLines;

            // Rebase FunctionEntry IPs
            for (int i = 0; i < functionEntries.Count; i++)
            {
                var fe = functionEntries[i];
                int newEntryIP = remap[fe.EntryIP];
                functionEntries[i] = new FunctionEntry(fe.Name, newEntryIP, fe.ParamCount, fe.LocalRegCount, fe.IsLeaf);
            }

            // B-ζ3: Rebase SWITCH jump table entries
            for (int t = 0; t < _jumpTables.Count; t++)
            {
                int[] table = _jumpTables[t];
                for (int j = 0; j < table.Length; j++)
                    table[j] = remap[table[j]];
            }
        }

        /// <summary>
        /// O8: Wide expansion pass — insert EXTEND_AX before instructions whose A operand (IP) exceeds 255.
        /// Runs after Peephole. Uses the same remap pattern as Peephole compaction (but in reverse: expansion).
        /// May iterate if EXTEND_AX insertion pushes more IPs beyond 255.
        /// </summary>
        private void ExpandWideJumps(List<FunctionEntry> functionEntries)
        {
            // Fast path: if total instruction count <= 255, no EXTEND_AX needed
            if (_instructions.Count <= 255) return;

            // Iterate: inserting EXTEND_AX increases IPs, which may push more targets over 255.
            // Each pass only inserts EXTEND_AX for instructions WITHOUT an existing prefix.
            for (int pass = 0; pass < 10; pass++)
            {
                int count = _instructions.Count;
                int extraCount = 0;
                var needsExtend = new bool[count];

                for (int i = 0; i < count; i++)
                {
                    if (!HasJumpTargetInA(_instructions[i].Code)) continue;
                    if (_wideA[i] <= 255) continue;
                    // Skip if already has EXTEND_AX prefix from a previous pass
                    if (i > 0 && _instructions[i - 1].Code == OpCode.EXTEND_AX) continue;
                    needsExtend[i] = true;
                    extraCount++;
                }
                if (extraCount == 0) break; // converged

                // Build remap: old IP -> new IP (EXTEND_AX insertion adds 1 slot before marked instruction)
                // remap[i] points to the EXTEND_AX prefix when present, so that jumps
                // targeting instruction i will first execute the EXTEND_AX, setting extendedA.
                int[] remap = new int[count + 1];
                int newIP = 0;
                for (int i = 0; i < count; i++)
                {
                    remap[i] = newIP;
                    if (needsExtend[i]) newIP++;
                    newIP++;
                }
                remap[count] = newIP;

                // Build expanded lists
                var newInstructions = new List<Instruction>(newIP);
                var newWideA = new List<int>(newIP);
                var newSourceLines = new List<int>(newIP);

                for (int i = 0; i < count; i++)
                {
                    var ins = _instructions[i];
                    int wideAVal = _wideA[i];

                    // Remap all IP-based A operands to new IP space
                    if (HasJumpTargetInA(ins.Code))
                    {
                        wideAVal = remap[wideAVal];
                        ins = new Instruction(ins.Code, wideAVal, ins.B, ins.C);
                    }

                    if (needsExtend[i])
                    {
                        // Insert EXTEND_AX before this instruction
                        newInstructions.Add(new Instruction(OpCode.EXTEND_AX, wideAVal >> 8));
                        newWideA.Add(wideAVal >> 8);
                        newSourceLines.Add(_sourceLines[i]);
                    }

                    newInstructions.Add(ins);
                    newWideA.Add(wideAVal);
                    newSourceLines.Add(_sourceLines[i]);
                }

                // Fix up existing EXTEND_AX instructions (from previous passes) whose
                // successor's target IP may have changed due to this pass's remapping
                for (int i = 0; i < newInstructions.Count - 1; i++)
                {
                    if (newInstructions[i].Code == OpCode.EXTEND_AX
                        && HasJumpTargetInA(newInstructions[i + 1].Code))
                    {
                        int hi = newWideA[i + 1] >> 8;
                        newInstructions[i] = new Instruction(OpCode.EXTEND_AX, hi);
                        newWideA[i] = hi;
                    }
                }

                _instructions = newInstructions;
                _wideA = newWideA;
                _sourceLines = newSourceLines;

                // Rebase FunctionEntry IPs
                for (int i = 0; i < functionEntries.Count; i++)
                {
                    var fe = functionEntries[i];
                    functionEntries[i] = new FunctionEntry(fe.Name, remap[fe.EntryIP], fe.ParamCount, fe.LocalRegCount, fe.IsLeaf);
                }

                // Rebase SWITCH jump table entries
                for (int t = 0; t < _jumpTables.Count; t++)
                {
                    int[] table = _jumpTables[t];
                    for (int j = 0; j < table.Length; j++)
                        table[j] = remap[table[j]];
                }
            }
        }
    }
}
