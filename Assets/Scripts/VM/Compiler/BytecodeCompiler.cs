using System.Collections.Generic;
using FFVM.AST;

namespace FFVM.Compiler
{
    public class CompileResult
    {
        public VMProgram Program;
        public List<string> Errors;
        /// <summary>
        /// Lang-8: Non-fatal warning messages (compile succeeds but with diagnostics).
        /// </summary>
        public List<string> Warnings;
        public bool Success => Errors == null || Errors.Count == 0;
        /// <summary>
        /// Lang-9 P3: Module inline info for cross-module inline.
        /// Contains function ASTs and module variable mappings needed by callers
        /// to inline this module's exported functions.
        /// </summary>
        public ModuleInlineInfo InlineInfo;
    }

    /// <summary>
    /// Compiles a parsed AST (single function) into a VMProgram (bytecode + constants).
    ///
    /// Register layout (derived from VMConstants.MaxRegisters):
    ///   r0..ScratchZoneSize-1                — scratch zone: syscall arguments / return values (absolute)
    ///   ScratchZoneSize..TempRegBase-1        — local variables (LocalVarSlots slots, windowed)
    ///   TempRegBase..ModuleVarRegBase-1       — expression temporaries (TempSlots slots, windowed+remapped)
    ///   ModuleVarRegBase..MaxRegisters-1      — module variables (ModuleVarSlots slots, absolute via LOAD_MVAR/STORE_MVAR)
    ///   MaxRegisters+                          — extended registers (heap-allocated, via LOAD_XREG/STORE_XREG, Lang-1.1b)
    /// </summary>
    public class BytecodeCompiler
    {
        private const int VarRegBase = VMConstants.ScratchZoneSize;
        private const int TempRegBase = VMConstants.TempRegBase;
        private const int ModuleVarRegBase = VMConstants.ModuleVarRegBase;

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
        private List<string> _warnings;  // Lang-8: non-fatal diagnostics

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

        // Lang-1: Module variable support
        private Dictionary<string, int> _moduleVarRegisters;     // module var name → absolute register (>= MaxRegisters = extended)
        private Dictionary<string, Number> _moduleConstValues;   // module const name → folded value
        private HashSet<string> _moduleConstVarNames;            // module const names that required a register (non-foldable)
        private Dictionary<int, Number> _moduleVarInitValues;    // module var register → init value (for EmitModuleVarInit)
        private int _nextExtendedReg;                            // Lang-1.1b: next extended register index (0-based)
        // Lang-11: Module-level struct variable support
        private Dictionary<string, string> _moduleStructVarTypes; // module struct var/const name → struct type name

        // Lang-8: Service bindings for unified svc.member syntax
        private Dictionary<string, ServiceBinding> _serviceBindings;  // varName → binding

        // Lang-9: Inline expansion configuration and state
        private const int InlineThreshold = 16;   // max estimated instruction count for inlinable function
        private const int InlineDepthMax = 3;     // max nested inline depth
        private int _inlineDepth;                  // current inline nesting depth
        private HashSet<string> _inlineStack;      // recursion guard: functions currently being inlined
        // Lang-9 P2: multi-return exit label support
        private List<int> _inlineExitJumps;        // forward jump IPs to backpatch to inline exit point
        private int _inlineDestReg;                // destination register for inline return values
        // Lang-9 P3: cross-module inline context
        private int _xInlineSvcReg;                // service instance register during cross-module inline (-1 = not active)
        private Dictionary<string, int> _xInlineVars;  // callee exported var name → export var index (for XLOAD/XSTORE_MVAR)
        // Lang-9 P4: deep chain inline — callee function lookup during cross-module inline
        private ModuleInlineInfo _xInlineInfo;     // active cross-module inline info (null = not in cross-module inline)

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
        // Lang-9 P2: track which callees were inlined per function (for FO6 window analysis)
        private Dictionary<string, HashSet<string>> _inlinedCalleesPerFunc;

        /// <summary>
        /// Compile source text into a VMProgram.
        /// </summary>
        /// <param name="source">Script source code</param>
        /// <param name="entryFunc">Entry function name (typically "main")</param>
        /// <param name="syscalls">Syscall name → slot mapping</param>
        /// <param name="syscallTable">Optional SyscallTable for paired syscall lookup (required for 'using')</param>
        public CompileResult Compile(string source, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable = null)
        {
            return Compile(source, entryFunc, syscalls, syscallTable, null, null);
        }

        /// <summary>
        /// Compile source text with include support.
        /// When a fileResolver is provided, include directives are resolved via the Preprocessor.
        /// </summary>
        /// <param name="source">Script source code</param>
        /// <param name="entryFunc">Entry function name (typically "main")</param>
        /// <param name="syscalls">Syscall name → slot mapping</param>
        /// <param name="syscallTable">Optional SyscallTable for paired syscall lookup (required for 'using')</param>
        /// <param name="fileResolver">Optional file resolver for include directives</param>
        /// <param name="filePath">Logical path of the main file (for include cycle detection and diagnostics)</param>
        public CompileResult Compile(string source, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable, IFileResolver fileResolver, string filePath)
        {
            return Compile(source, entryFunc, syscalls, syscallTable, fileResolver, filePath, null);
        }

        /// <summary>
        /// Lang-8: Compile source text with include support and service bindings.
        /// Service bindings enable the svc.member unified syntax by providing target ExportTables.
        /// </summary>
        public CompileResult Compile(string source, string entryFunc, Dictionary<string, int> syscalls,
            SyscallTable syscallTable, IFileResolver fileResolver, string filePath,
            ServiceBinding[] serviceBindings)
        {
            ModuleNode module;
            List<string> parseErrors;

            if (fileResolver != null)
            {
                var preprocessor = new Preprocessor(fileResolver);
                module = preprocessor.Resolve(source, filePath ?? "main", out parseErrors);
            }
            else
            {
                var parser = new Parser();
                module = parser.Parse(source, out parseErrors);
            }

            if (parseErrors != null && parseErrors.Count > 0)
                return new CompileResult { Errors = parseErrors };

            return CompileModule(module, entryFunc, syscalls, syscallTable, serviceBindings);
        }

        /// <summary>
        /// Compile a pre-parsed module into a VMProgram.
        /// Two-pass: (1) scan all functions → build function table; (2) compile entry, then others.
        /// Forward-reference CALL instructions are backpatched after all functions are compiled.
        /// </summary>
        public CompileResult CompileModule(ModuleNode module, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable = null)
        {
            return CompileModule(module, entryFunc, syscalls, syscallTable, null);
        }

        /// <summary>
        /// Lang-8: Compile a pre-parsed module with optional service bindings.
        /// </summary>
        public CompileResult CompileModule(ModuleNode module, string entryFunc, Dictionary<string, int> syscalls, SyscallTable syscallTable, ServiceBinding[] serviceBindings)
        {
            _instructions = new List<Instruction>();
            _wideA = new List<int>();
            _constants = new List<Number>();
            _stringConstants = new List<string>();
            _jumpTables = new List<int[]>();
            _syscalls = syscalls ?? new Dictionary<string, int>();
            _syscallTable = syscallTable;
            _errors = new List<string>();
            _warnings = new List<string>();
            _pendingCalls = new List<PendingCall>();
            _inlineDepth = 0;
            _inlineStack = null;
            _inlineExitJumps = null;
            _inlineDestReg = -1;
            _xInlineSvcReg = -1;
            _xInlineVars = null;
            _xInlineInfo = null;
            _inlinedCalleesPerFunc = new Dictionary<string, HashSet<string>>();
            _sourceLines = new List<int>();
            _currentLine = 0;
            _symbolEntries = new List<SymbolEntry>();

            // Lang-8: Initialize service bindings for svc.member unified syntax
            _serviceBindings = new Dictionary<string, ServiceBinding>();
            if (serviceBindings != null)
            {
                for (int i = 0; i < serviceBindings.Length; i++)
                {
                    var sb = serviceBindings[i];
                    if (sb != null && sb.VarName != null && sb.Exports != null)
                        _serviceBindings[sb.VarName] = sb;
                }
            }

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

            // Lang-1: Process module-level var/const declarations
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

            // Lang-6: Y1-Plus — validate @export functions don't yield/wait and don't use defer/using
            ValidateExportedFunctions(module);

            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

            // Lang-6: Build export table from @export declarations
            ExportTable exportTable = BuildExportTable(module, functionEntries);

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

            // Lang-9 P3: Build ModuleInlineInfo for cross-module inline support
            var inlineInfo = BuildModuleInlineInfo(module);

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
                    _jumpTables.Count > 0 ? _jumpTables.ToArray() : null,
                    _nextExtendedReg,
                    exportTable
                ),
                Errors = _errors,
                Warnings = _warnings.Count > 0 ? _warnings : null,
                InlineInfo = inlineInfo
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

            // Lang-1: Pre-populate scope with module variables and constants
            if (_moduleVarRegisters != null)
            {
                foreach (var kv in _moduleVarRegisters)
                    _variables[kv.Key] = kv.Value;
            }
            if (_moduleConstValues != null)
            {
                foreach (var kv in _moduleConstValues)
                    _constValues[kv.Key] = kv.Value;
            }
            // Lang-11: Pre-populate struct type info for module struct vars
            if (_moduleStructVarTypes != null)
            {
                foreach (var kv in _moduleStructVarTypes)
                    _structVarTypes[kv.Key] = kv.Value;
            }
            // Module consts with registers (non-foldable) are already in _moduleVarRegisters
            // and marked in _moduleConstVarNames for assignment prevention

            // F4: analyze variable lifetimes before compilation
            _liveRanges = AnalyzeVariableLifetimes(func);

            // Lang-1: Entry function preamble — emit module variable initialization
            if (isEntry)
                EmitModuleVarInit();

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
        /// Lang-1: Process module-level var/const declarations.
        /// Allocates absolute registers r56..r63 for module variables.
        /// Foldable consts go to _moduleConstValues (no register).
        /// Non-foldable consts get a register and are tracked in _moduleConstVarNames.
        /// </summary>
        private void ProcessModuleVariables(ModuleNode module)
        {
            _moduleVarRegisters = new Dictionary<string, int>();
            _moduleConstValues = new Dictionary<string, Number>();
            _moduleConstVarNames = new HashSet<string>();
            _moduleVarInitValues = new Dictionary<int, Number>();
            _moduleStructVarTypes = new Dictionary<string, string>();
            _nextExtendedReg = 0;

            if (module.ModuleVariables.Count == 0) return;

            int nextModuleReg = VMConstants.ModuleVarRegBase;
            // Temporary _constValues used for cascading const folding during module variable processing
            _constValues = new Dictionary<string, Number>();

            for (int i = 0; i < module.ModuleVariables.Count; i++)
            {
                var decl = module.ModuleVariables[i];

                // Check for duplicate module variable names
                if (_moduleVarRegisters.ContainsKey(decl.Name) || _moduleConstValues.ContainsKey(decl.Name))
                {
                    _errors.Add($"Duplicate module variable '{decl.Name}' (line {decl.Line})");
                    continue;
                }

                // Lang-11: Check if this is a struct-typed module variable
                if (decl.TypeName != null && _structTypes.ContainsKey(decl.TypeName))
                {
                    ProcessModuleStructVar(decl, ref nextModuleReg);
                    continue;
                }

                if (decl.IsConst)
                {
                    // Try to fold to compile-time constant
                    if (decl.Initializer == null)
                    {
                        _errors.Add($"Module 'const' requires an initializer (line {decl.Line})");
                        continue;
                    }

                    if (TryFoldConstant(decl.Initializer, out Number constVal))
                    {
                        _moduleConstValues[decl.Name] = constVal;
                        _constValues[decl.Name] = constVal; // make available for subsequent folding

                        // Lang-12: @export const needs a register for ExportTable mvarSlot
                        // so cross-instance reads (XLOAD_MVAR / GetVarDefault) work
                        if (decl.IsExported)
                        {
                            int ecReg;
                            if (nextModuleReg < VMConstants.MaxRegisters)
                            {
                                ecReg = nextModuleReg++;
                            }
                            else
                            {
                                ecReg = VMConstants.MaxRegisters + _nextExtendedReg++;
                            }
                            _moduleVarRegisters[decl.Name] = ecReg;
                            _moduleVarInitValues[ecReg] = constVal;

                            _symbolEntries.Add(new SymbolEntry(decl.Name, ecReg, 0, null, "<module>"));
                        }

                        continue;
                    }

                    _errors.Add($"Module 'const' initializer must be a compile-time constant (line {decl.Line})");
                    continue;
                }

                // Module var — allocate register (fixed or extended)
                int reg;
                if (nextModuleReg < VMConstants.MaxRegisters)
                {
                    // Fixed module var slot available
                    reg = nextModuleReg++;
                }
                else
                {
                    // Lang-1.1b: Overflow to extended registers
                    reg = VMConstants.MaxRegisters + _nextExtendedReg++;
                }
                _moduleVarRegisters[decl.Name] = reg;

                // Try to fold initializer to a constant value for emit
                if (decl.Initializer != null)
                {
                    if (TryFoldConstant(decl.Initializer, out Number initVal))
                    {
                        _moduleVarInitValues[reg] = initVal;
                    }
                    else
                    {
                        _errors.Add($"Module variable '{decl.Name}' initializer must be a compile-time constant (line {decl.Line})");
                    }
                }
                // else: no initializer → default zero (no entry in _moduleVarInitValues)

                // DBG2: record symbol entry for module variable
                _symbolEntries.Add(new SymbolEntry(decl.Name, reg, 0, null, "<module>"));
            }

            // Clean up temporary _constValues used during folding
            _constValues = null;
        }

        /// <summary>
        /// Lang-11: Process a struct-typed module variable or const.
        /// Allocates N consecutive module var registers for the flattened struct fields.
        /// Struct literal initializer: all scalar fields must be compile-time constants.
        /// </summary>
        private void ProcessModuleStructVar(VarDeclStmt decl, ref int nextModuleReg)
        {
            var flatInfo = _flatStructInfo[decl.TypeName];
            int flatCount = flatInfo.FlatFieldCount;

            // Validate initializer
            if (decl.IsConst && decl.Initializer == null)
            {
                _errors.Add($"Module 'const' struct requires an initializer (line {decl.Line})");
                return;
            }
            if (decl.Initializer != null && !(decl.Initializer is StructLiteralExpr))
            {
                _errors.Add($"Module struct variable '{decl.Name}' initializer must be a struct literal (line {decl.Line})");
                return;
            }

            // @export is not supported for struct module vars (single-slot ExportVarEntry limitation)
            if (decl.IsExported)
            {
                _errors.Add($"@export is not supported for struct module variables ('{decl.Name}'). Use separate @export scalar variables for each field. (line {decl.Line})");
                return;
            }

            // Allocate N consecutive module var registers
            int baseReg;
            if (nextModuleReg + flatCount <= VMConstants.MaxRegisters)
            {
                baseReg = nextModuleReg;
                nextModuleReg += flatCount;
            }
            else
            {
                // Overflow to extended registers
                baseReg = VMConstants.MaxRegisters + _nextExtendedReg;
                _nextExtendedReg += flatCount;
            }

            _moduleVarRegisters[decl.Name] = baseReg;
            _moduleStructVarTypes[decl.Name] = decl.TypeName;

            if (decl.IsConst)
                _moduleConstVarNames.Add(decl.Name);

            // Fold struct literal field values into _moduleVarInitValues
            if (decl.Initializer is StructLiteralExpr structLit)
            {
                if (!TryFoldStructLiteral(structLit, decl.TypeName, baseReg))
                {
                    _errors.Add($"Module struct '{decl.Name}' initializer: all field values must be compile-time constants (line {decl.Line})");
                }
            }
            // else: var without initializer → default zero (registers are zero-initialized on spawn)

            // DBG2: record symbol entry for struct module variable with flattened field names
            var fieldNames = new string[flatCount];
            for (int fi = 0; fi < flatCount; fi++)
                fieldNames[fi] = flatInfo.FlatFields[fi].DotPath;
            _symbolEntries.Add(new SymbolEntry(decl.Name, baseReg, flatCount, fieldNames, "<module>"));
        }

        /// <summary>
        /// Lang-11: Recursively fold a struct literal's fields into _moduleVarInitValues.
        /// All scalar field values must be compile-time constants.
        /// Returns true if all fields were successfully folded.
        /// </summary>
        private bool TryFoldStructLiteral(StructLiteralExpr literal, string typeName, int baseReg)
        {
            if (!_structTypes.TryGetValue(typeName, out var structDecl))
                return false;

            if (literal.TypeName != typeName)
                return false;

            if (literal.Fields.Count != structDecl.Fields.Count)
                return false;

            int offset = 0;
            for (int i = 0; i < literal.Fields.Count; i++)
            {
                var (fieldName, valueExpr) = literal.Fields[i];
                var expectedField = structDecl.Fields[i];

                if (fieldName != expectedField.Name)
                    return false;

                if (_structTypes.ContainsKey(expectedField.TypeName))
                {
                    // Nested struct field — must be a struct literal
                    if (!(valueExpr is StructLiteralExpr nestedLit))
                        return false;
                    int nestedFlatCount = _flatStructInfo[expectedField.TypeName].FlatFieldCount;
                    if (!TryFoldStructLiteral(nestedLit, expectedField.TypeName, baseReg + offset))
                        return false;
                    offset += nestedFlatCount;
                }
                else
                {
                    // Scalar field — must fold to compile-time constant
                    if (!TryFoldConstant(valueExpr, out Number fieldVal))
                        return false;
                    _moduleVarInitValues[baseReg + offset] = fieldVal;
                    offset++;
                }
            }
            return true;
        }

        /// <summary>
        /// Lang-1: Emit module variable initialization code as entry function preamble.
        /// Emits LOAD_CONST to temp + STORE_MVAR/STORE_XREG for each module var with an initializer.
        /// Vars without initializer default to zero (registers/extended pool are zero-initialized on spawn).
        /// </summary>
        private void EmitModuleVarInit()
        {
            if (_moduleVarInitValues == null || _moduleVarInitValues.Count == 0) return;

            foreach (var kv in _moduleVarInitValues)
            {
                int reg = kv.Key;
                Number val = kv.Value;
                int ci = AddConst(val);
                // Load constant into a temp, then store to module var (fixed or extended)
                int temp = AllocTemp();
                Emit(OpCode.LOAD_CONST, temp, ci);
                EmitStoreModuleVar(reg, temp);
                ResetTemps();  // Lang-1.1b: reset per-var to support >8 module vars
            }
        }

        /// <summary>
        /// Lang-1.1b: Emit a load from a module variable register to a destination register.
        /// Routes to LOAD_MVAR (fixed) or LOAD_XREG (extended) based on register number.
        /// </summary>
        private void EmitLoadModuleVar(int destReg, int moduleVarReg)
        {
            if (moduleVarReg >= VMConstants.MaxRegisters)
            {
                int xidx = moduleVarReg - VMConstants.MaxRegisters;
                Emit(OpCode.LOAD_XREG, destReg, xidx & 0xFF, xidx >> 8);
            }
            else
            {
                Emit(OpCode.LOAD_MVAR, destReg, moduleVarReg - ModuleVarRegBase);
            }
        }

        /// <summary>
        /// Lang-1.1b: Emit a store to a module variable register from a source register.
        /// Routes to STORE_MVAR (fixed) or STORE_XREG (extended) based on register number.
        /// </summary>
        private void EmitStoreModuleVar(int moduleVarReg, int srcReg)
        {
            if (moduleVarReg >= VMConstants.MaxRegisters)
            {
                int xidx = moduleVarReg - VMConstants.MaxRegisters;
                Emit(OpCode.STORE_XREG, xidx & 0xFF, srcReg, xidx >> 8);
            }
            else
            {
                Emit(OpCode.STORE_MVAR, moduleVarReg - ModuleVarRegBase, srcReg);
            }
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
                if ((mask & 1) != 0 && a >= TempRegBase && a < VMConstants.ModuleVarRegBase) { a += shift; changed = true; }
                if ((mask & 2) != 0 && b >= TempRegBase && b < VMConstants.ModuleVarRegBase) { b += shift; changed = true; }
                if ((mask & 4) != 0 && c >= TempRegBase && c < VMConstants.ModuleVarRegBase) { c += shift; changed = true; }

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
                case OpCode.LOAD_MVAR:  return 1;    // A=destReg, B=mvarSlot
                case OpCode.LOAD_XREG: return 1;    // A=destReg, B=xidx_lo, C=xidx_hi

                // B = register (A is not a register)
                case OpCode.STORE_MVAR: return 2;    // A=mvarSlot, B=srcReg
                case OpCode.STORE_XREG: return 2;    // A=xidx_lo, B=srcReg, C=xidx_hi

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
                // Lang-8: XCALL/XLOAD_MVAR: A=destReg, B=instanceIdReg, C=index (not reg)
                case OpCode.XCALL:       return 3; // A=destReg, B=instanceIdReg
                case OpCode.XLOAD_MVAR:  return 3; // A=destReg, B=instanceIdReg
                // Lang-8: XSTORE_MVAR: A=varIndex (not reg), B=instanceIdReg, C=srcReg
                case OpCode.XSTORE_MVAR: return 6; // B=instanceIdReg, C=srcReg
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
                // Lang-8: MemberCallExpr — track target variable usage + recurse into arguments
                else if (expr is MemberCallExpr mc)
                {
                    // Track the service variable reference
                    if (ranges.ContainsKey(mc.TargetName))
                    {
                        var r = ranges[mc.TargetName];
                        r.LastUseOrder = order;
                        ranges[mc.TargetName] = r;
                    }
                    if (seenAwait && declaredBeforeAwait.Contains(mc.TargetName))
                        usedAfterAwait.Add(mc.TargetName);
                    for (int i = 0; i < mc.Arguments.Count; i++)
                        WalkExpr(mc.Arguments[i]);
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

        // ===== Variable management =====

        private int DeclareVar(string name)
        {
            // Lang-1: prevent local variable from shadowing module variable
            if (_moduleVarRegisters != null && _moduleVarRegisters.ContainsKey(name))
            {
                _errors.Add($"Cannot redeclare '{name}' as local variable (already exists as module variable)");
                return VarRegBase;
            }
            if (_moduleConstValues != null && _moduleConstValues.ContainsKey(name))
            {
                _errors.Add($"Cannot redeclare '{name}' as local variable (already exists as module constant)");
                return VarRegBase;
            }

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
            // Lang-1: don't release module variable registers — they're shared across functions
            if (IsModuleVarReg(reg)) return;

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
        /// Check if a register index belongs to the module variable region (r56-r63).
        /// </summary>
        private bool IsModuleVarReg(int reg)
        {
            return reg >= ModuleVarRegBase;
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
            if (_tempTop >= VMConstants.ModuleVarRegBase)
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
            bool destIsMVar = IsModuleVarReg(destBase);
            bool srcIsMVar = IsModuleVarReg(srcBase);

            if (!destIsMVar && !srcIsMVar)
            {
                // Original path — both are local/temp registers
                if (count >= 3)
                    Emit(OpCode.COPY_BLOCK, destBase, srcBase, count);
                else
                    for (int i = 0; i < count; i++)
                        Emit(OpCode.MOVE, destBase + i, srcBase + i);
                return;
            }

            // At least one side is a module var — use per-field EmitLoadModuleVar/EmitStoreModuleVar
            for (int i = 0; i < count; i++)
            {
                if (srcIsMVar && destIsMVar)
                {
                    int temp = AllocTemp();
                    EmitLoadModuleVar(temp, srcBase + i);
                    EmitStoreModuleVar(destBase + i, temp);
                }
                else if (srcIsMVar)
                {
                    EmitLoadModuleVar(destBase + i, srcBase + i);
                }
                else
                {
                    EmitStoreModuleVar(destBase + i, srcBase + i);
                }
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
                    int targetReg = baseReg + offset;
                    if (IsModuleVarReg(targetReg))
                    {
                        int valueReg = CompileExpr(valueExpr);
                        EmitStoreModuleVar(targetReg, valueReg);
                    }
                    else
                    {
                        int valueReg = CompileExpr(valueExpr, destReg: targetReg);
                        if (valueReg != targetReg)
                            Emit(OpCode.MOVE, targetReg, valueReg);
                    }
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
                // Lang-1: prevent local const from shadowing module variable/const
                if (_moduleVarRegisters != null && _moduleVarRegisters.ContainsKey(stmt.Name))
                {
                    _errors.Add($"Cannot redeclare '{stmt.Name}' as local constant (already exists as module variable) (line {stmt.Line})");
                    return;
                }
                if (_moduleConstValues != null && _moduleConstValues.ContainsKey(stmt.Name))
                {
                    _errors.Add($"Cannot redeclare '{stmt.Name}' as local constant (already exists as module constant) (line {stmt.Line})");
                    return;
                }

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
            else if (node is MemberCallExpr mc) { for (int i = 0; i < mc.Arguments.Count; i++) CollectLoopLiterals(mc.Arguments[i], result); }
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
            // Lang-9 P2: inside inline context, return compiles value to _inlineDestReg
            // and jumps to the exit point (no RET instruction emitted)
            if (_inlineExitJumps != null)
            {
                if (stmt.Value != null)
                {
                    int valueReg = CompileExpr(stmt.Value, _inlineDestReg);
                    if (valueReg != _inlineDestReg)
                        Emit(OpCode.MOVE, _inlineDestReg, valueReg);
                }
                // Emit forward jump to exit point (will be backpatched)
                _inlineExitJumps.Add(EmitJump(OpCode.JUMP));
                return;
            }

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
                // P4: callee function call during cross-module inline — void call
                if (_xInlineInfo != null && _xInlineInfo.FuncDecls.ContainsKey(call.FunctionName))
                {
                    TryInlineCalleeFunc(call, -1, out _);
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
                // Lang-9 P3: cross-module inline variable read → XLOAD_MVAR
                if (_xInlineVars != null && _xInlineVars.TryGetValue(ident.Name, out int xVarIdx))
                {
                    int dest = destReg >= 0 ? destReg : AllocTemp();
                    Emit(OpCode.XLOAD_MVAR, dest, _xInlineSvcReg, xVarIdx);
                    return dest;
                }
                int reg = ResolveVar(ident.Name);
                // Lang-11: module struct var → return base register directly (no single-field materialization)
                // Struct operations (field access, struct copy, function args) handle module var per-field
                if (IsModuleVarReg(reg) && _structVarTypes.ContainsKey(ident.Name))
                {
                    return reg;
                }
                // Lang-1: module var → emit LOAD_MVAR/LOAD_XREG to materialize value
                if (IsModuleVarReg(reg))
                {
                    int dest = destReg >= 0 ? destReg : AllocTemp();
                    EmitLoadModuleVar(dest, reg);
                    return dest;
                }
                return reg;
            }

            if (expr is FieldAccessExpr fieldAccess)
            {
                // Lang-8: check if target is a service binding → XLOAD_MVAR
                if (fieldAccess.Target is IdentifierExpr svcIdent &&
                    _serviceBindings != null && _serviceBindings.TryGetValue(svcIdent.Name, out var svcBinding))
                {
                    return CompileServiceVarRead(svcIdent.Name, fieldAccess.FieldName, svcBinding, destReg, fieldAccess.Line);
                }

                int reg = ResolveFieldAccess(fieldAccess);
                // Lang-1: module struct field → emit LOAD_MVAR/LOAD_XREG to materialize value
                if (IsModuleVarReg(reg))
                {
                    int dest = destReg >= 0 ? destReg : AllocTemp();
                    EmitLoadModuleVar(dest, reg);
                    return dest;
                }
                return reg;
            }

            // Lang-8: Cross-instance member function call (svc.func(args))
            if (expr is MemberCallExpr memberCall)
            {
                return CompileMemberCallExpr(memberCall, destReg);
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
                    // Lang-11: prevent assignment to const struct module variables
                    if (_moduleConstVarNames != null && _moduleConstVarNames.Contains(targetIdent.Name))
                    {
                        _errors.Add($"Cannot assign to 'const' struct '{targetIdent.Name}' (line {assign.Line})");
                        return destReg >= 0 ? destReg : AllocTemp();
                    }
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
                    // Lang-8: check if target is a service binding → XSTORE_MVAR
                    if (fieldTarget.Target is IdentifierExpr svcWriteIdent &&
                        _serviceBindings != null && _serviceBindings.TryGetValue(svcWriteIdent.Name, out var svcWriteBinding))
                    {
                        return CompileServiceVarWrite(svcWriteIdent.Name, fieldTarget.FieldName, svcWriteBinding, assign.Value, fieldTarget.Line);
                    }

                    // Lang-11: prevent field assignment on const struct module variables
                    {
                        string rootVar, rootDotPath;
                        CollectFieldChain(fieldTarget, out rootVar, out rootDotPath);
                        if (rootVar != null && _moduleConstVarNames != null && _moduleConstVarNames.Contains(rootVar))
                        {
                            _errors.Add($"Cannot assign to field of 'const' struct '{rootVar}' (line {assign.Line})");
                            return destReg >= 0 ? destReg : AllocTemp();
                        }
                    }

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
                    // Lang-1: module struct field → STORE_MVAR/STORE_XREG
                    if (IsModuleVarReg(fieldReg))
                    {
                        int valueReg = CompileExpr(assign.Value);
                        EmitStoreModuleVar(fieldReg, valueReg);
                        return valueReg;
                    }
                    // O4: pass dest-reg hint for field assignment
                    int fieldValueReg = CompileExpr(assign.Value, destReg: fieldReg);
                    if (fieldValueReg != fieldReg)
                        Emit(OpCode.MOVE, fieldReg, fieldValueReg);
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
                    // Lang-9 P3: cross-module inline variable write → XSTORE_MVAR
                    if (_xInlineVars != null && _xInlineVars.TryGetValue(scalarTarget.Name, out int xVarIdx))
                    {
                        int scalarValueReg = CompileExpr(assign.Value);
                        Emit(OpCode.XSTORE_MVAR, xVarIdx, _xInlineSvcReg, scalarValueReg);
                        return scalarValueReg;
                    }
                    int scalarTargetReg = ResolveVar(scalarTarget.Name);
                    // Lang-1: module var → STORE_MVAR/STORE_XREG
                    if (IsModuleVarReg(scalarTargetReg))
                    {
                        int scalarValueReg = CompileExpr(assign.Value);
                        EmitStoreModuleVar(scalarTargetReg, scalarValueReg);
                        return scalarValueReg;
                    }
                    // O4: pass dest-reg hint so expression writes directly into target register
                    int localValueReg = CompileExpr(assign.Value, destReg: scalarTargetReg);
                    if (localValueReg != scalarTargetReg)
                        Emit(OpCode.MOVE, scalarTargetReg, localValueReg);
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

                // P4: callee function call during cross-module inline
                if (_xInlineInfo != null && _xInlineInfo.FuncDecls.ContainsKey(call.FunctionName))
                {
                    if (TryInlineCalleeFunc(call, destReg, out int calleeReg))
                        return calleeReg;
                }

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
        /// Lang-9: tries inline expansion first; falls back to CALL if not inlinable.
        /// </summary>
        private int CompileUserCallExpr(CallExpr call, int destReg = -1)
        {
            // Lang-9: try inline expansion
            if (TryInlineCall(call, destReg, out int inlinedReg))
                return inlinedReg;

            EmitUserCall(call);

            // FO5: save result from r0 directly to destReg if available
            int resultReg = destReg >= 0 ? destReg : AllocTemp();
            if (resultReg != 0)
                Emit(OpCode.MOVE, resultReg, 0);
            return resultReg;
        }

        /// <summary>
        /// Void user function call (no result save).
        /// Lang-9: tries inline expansion first; falls back to CALL if not inlinable.
        /// </summary>
        private void CompileUserCallVoid(CallExpr call)
        {
            // Lang-9: try inline expansion (result discarded for void calls)
            if (TryInlineCall(call, -1, out _))
                return;

            EmitUserCall(call);
        }

        /// <summary>
        /// Core: compile args → scratch zone, emit CALL instruction.
        /// S4: struct arguments expand to multiple consecutive scratch registers.
        /// callerWindowSize = VarRegBase + localVarCount for register window offset.
        /// </summary>
        private void EmitUserCall(CallExpr call)
        {
            // DC: R8 blanket ban removed — user function calls are now allowed
            // inside cleanup blocks (defer/using). Runtime handles nested cleanup
            // via per-frame SavedR0 and WasInCleanup flag in CallFrame.CleanupBase.

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
                        // Lang-11: module struct var → use EmitLoadModuleVar per field
                        if (IsModuleVarReg(srcBase))
                        {
                            for (int j = 0; j < argFlatInfo.FlatFieldCount; j++)
                                EmitLoadModuleVar(scratchReg + j, srcBase + j);
                        }
                        else
                        {
                            for (int j = 0; j < argFlatInfo.FlatFieldCount; j++)
                            {
                                if (srcBase + j != scratchReg + j)
                                    Emit(OpCode.MOVE, scratchReg + j, srcBase + j);
                            }
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
            int availableSlots = VMConstants.ModuleVarRegBase - VarRegBase;

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
                // Lang-9 P2: get inlined callees for this function (their window is already in myWindow)
                HashSet<string> inlined = null;
                _inlinedCalleesPerFunc.TryGetValue(funcName, out inlined);
                foreach (var callee in callees)
                {
                    if (!callGraph.ContainsKey(callee)) continue;
                    // P2: skip inlined callees — their registers are already part of this function's window
                    if (inlined != null && inlined.Contains(callee)) continue;
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
                _errors.Add($"Static register window depth from '{entryFunc}' requires {maxWindow} registers, exceeding available {availableSlots} slots (MaxRegisters={VMConstants.MaxRegisters}). Reduce local variable usage or function nesting depth.");
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
            // Lang-8: MemberCallExpr — cross-instance call, not a local callee, but recurse into args
            else if (expr is MemberCallExpr mc)
            {
                for (int i = 0; i < mc.Arguments.Count; i++)
                    CollectCalleesExpr(mc.Arguments[i], callees);
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

        // ===== Lang-8: Service member access compilation =====

        /// <summary>
        /// Lang-8: Compile svc.func(args) → XCALL / XLOAD_MVAR (getter) / XSTORE_MVAR (setter).
        /// Routes based on export table degradation type.
        /// </summary>
        private int CompileMemberCallExpr(MemberCallExpr mc, int destReg)
        {
            // Check service binding exists
            if (_serviceBindings == null || !_serviceBindings.TryGetValue(mc.TargetName, out var binding))
            {
                _errors.Add($"'{mc.TargetName}' is not a service reference. Use service bindings to enable svc.member syntax. (line {mc.Line})");
                return destReg >= 0 ? destReg : AllocTemp();
            }

            // Look up function in export table
            int funcIdx = -1;
            ExportFuncEntry funcEntry = default;
            for (int i = 0; i < binding.Exports.Functions.Length; i++)
            {
                if (binding.Exports.Functions[i].Name == mc.MemberName)
                {
                    funcIdx = i;
                    funcEntry = binding.Exports.Functions[i];
                    break;
                }
            }

            if (funcIdx < 0)
            {
                _errors.Add($"Function '{mc.MemberName}' is not exported by service '{mc.TargetName}'. (line {mc.Line})");
                return destReg >= 0 ? destReg : AllocTemp();
            }

            // Resolve service instance variable register
            int svcReg = ResolveVar(mc.TargetName);
            if (IsModuleVarReg(svcReg))
            {
                // Module var → need to load into a temp first
                int tmpSvc = AllocTemp();
                EmitLoadModuleVar(tmpSvc, svcReg);
                svcReg = tmpSvc;
            }

            // Route based on degradation type
            if (funcEntry.Degradation == DegradationType.Getter)
            {
                // A1: Pure getter → XLOAD_MVAR (bypass XCALL, direct variable read)
                if (mc.Arguments.Count > 0)
                {
                    _errors.Add($"Getter '{mc.MemberName}' takes no arguments, but {mc.Arguments.Count} provided. (line {mc.Line})");
                }
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(OpCode.XLOAD_MVAR, dest, svcReg, funcEntry.DegradeMvarSlot);
                return dest;
            }
            else if (funcEntry.Degradation == DegradationType.Setter)
            {
                // A2: Pure setter → XSTORE_MVAR (bypass XCALL, direct variable write)
                if (mc.Arguments.Count != 1)
                {
                    _errors.Add($"Setter '{mc.MemberName}' takes exactly 1 argument, but {mc.Arguments.Count} provided. (line {mc.Line})");
                    return destReg >= 0 ? destReg : AllocTemp();
                }
                int argReg = CompileExpr(mc.Arguments[0]);
                Emit(OpCode.XSTORE_MVAR, funcEntry.DegradeMvarSlot, svcReg, argReg);
                return argReg;
            }
            else
            {
                // Lang-9 P3: Attempt cross-module inline before XCALL
                if (TryInlineMemberCall(mc, destReg, binding, out int inlinedReg))
                {
                    return inlinedReg;
                }

                // None → standard XCALL
                // Lang-8: warn if @inline hint but neither inlined nor degraded to direct variable access
                if (funcEntry.IsInlineHint)
                {
                    _warnings.Add($"@inline function '{mc.MemberName}' was neither inlined nor degraded to direct variable access. XCALL will be used. (line {mc.Line})");
                }

                // Validate argument count
                if (mc.Arguments.Count != funcEntry.ParamCount)
                {
                    _errors.Add($"Function '{mc.MemberName}' expects {funcEntry.ParamCount} arguments, but {mc.Arguments.Count} provided. (line {mc.Line})");
                    return destReg >= 0 ? destReg : AllocTemp();
                }

                // Phase 1: Compile arguments into temp registers
                // (two-phase approach: arguments may be IdentifierExpr that resolve to local
                // registers; CompileExpr ignores destReg for idents. Compiling all to temps first
                // then MOVEing to scratch zone avoids register conflicts.)
                int[] argRegs = new int[mc.Arguments.Count];
                for (int i = 0; i < mc.Arguments.Count; i++)
                    argRegs[i] = CompileExpr(mc.Arguments[i]);

                // Phase 2: Move args to scratch zone r0..r(n-1)
                for (int i = 0; i < mc.Arguments.Count; i++)
                {
                    if (argRegs[i] != i)
                        Emit(OpCode.MOVE, i, argRegs[i]);
                }

                // Emit XCALL: A=destReg, B=svcReg, C=exportFuncIndex
                int dest = destReg >= 0 ? destReg : AllocTemp();
                Emit(OpCode.XCALL, dest, svcReg, funcIdx);
                return dest;
            }
        }

        /// <summary>
        /// Lang-8: Compile svc.var read → XLOAD_MVAR.
        /// </summary>
        private int CompileServiceVarRead(string svcVarName, string memberName, ServiceBinding binding, int destReg, int line)
        {
            // Look up variable in export table
            int varIdx = -1;
            for (int i = 0; i < binding.Exports.Variables.Length; i++)
            {
                if (binding.Exports.Variables[i].Name == memberName)
                {
                    varIdx = i;
                    break;
                }
            }

            if (varIdx < 0)
            {
                // Not a variable — check if it's a function (user might have forgotten ())
                for (int i = 0; i < binding.Exports.Functions.Length; i++)
                {
                    if (binding.Exports.Functions[i].Name == memberName)
                    {
                        _errors.Add($"'{memberName}' is an exported function of '{svcVarName}'. Use '{svcVarName}.{memberName}()' to call it. (line {line})");
                        return destReg >= 0 ? destReg : AllocTemp();
                    }
                }
                _errors.Add($"'{memberName}' is not exported by service '{svcVarName}'. (line {line})");
                return destReg >= 0 ? destReg : AllocTemp();
            }

            // Resolve service instance variable register
            int svcReg = ResolveVar(svcVarName);
            if (IsModuleVarReg(svcReg))
            {
                int tmpSvc = AllocTemp();
                EmitLoadModuleVar(tmpSvc, svcReg);
                svcReg = tmpSvc;
            }

            int dest = destReg >= 0 ? destReg : AllocTemp();
            Emit(OpCode.XLOAD_MVAR, dest, svcReg, varIdx);
            return dest;
        }

        /// <summary>
        /// Lang-8: Compile svc.var = expr → XSTORE_MVAR.
        /// </summary>
        private int CompileServiceVarWrite(string svcVarName, string memberName, ServiceBinding binding, Expr value, int line)
        {
            // Look up variable in export table
            int varIdx = -1;
            ExportVarEntry varEntry = default;
            for (int i = 0; i < binding.Exports.Variables.Length; i++)
            {
                if (binding.Exports.Variables[i].Name == memberName)
                {
                    varIdx = i;
                    varEntry = binding.Exports.Variables[i];
                    break;
                }
            }

            if (varIdx < 0)
            {
                _errors.Add($"Variable '{memberName}' is not exported by service '{svcVarName}'. (line {line})");
                return AllocTemp();
            }

            if (!varEntry.Writable)
            {
                _errors.Add($"Cannot write to read-only exported variable '{svcVarName}.{memberName}'. (line {line})");
                return AllocTemp();
            }

            // Resolve service instance variable register
            int svcReg = ResolveVar(svcVarName);
            if (IsModuleVarReg(svcReg))
            {
                int tmpSvc = AllocTemp();
                EmitLoadModuleVar(tmpSvc, svcReg);
                svcReg = tmpSvc;
            }

            int valueReg = CompileExpr(value);
            Emit(OpCode.XSTORE_MVAR, varIdx, svcReg, valueReg);
            return valueReg;
        }

        /// <summary>
        /// Lang-6 Y1-Plus: Validate that @export functions don't yield/wait (directly or transitively)
        /// and don't contain defer/using (C-1 restriction).
        /// Uses yield-taint analysis: mark functions that directly yield, then propagate via call graph.
        /// </summary>
        private void ValidateExportedFunctions(ModuleNode module)
        {
            bool hasExported = false;
            for (int i = 0; i < module.Functions.Count; i++)
            {
                if (module.Functions[i].IsExported) { hasExported = true; break; }
            }
            if (!hasExported) return;

            // Step 1: Mark functions that directly contain yield/wait/wait_for
            var mayYield = new Dictionary<string, bool>();
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                mayYield[f.Name] = ContainsYieldOrWait(f.Body);
            }

            // Step 2: Build call graph and propagate yield taint (transitive closure)
            var callees = new Dictionary<string, List<string>>();
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                var calls = new List<string>();
                CollectCallees(f.Body, calls);
                callees[f.Name] = calls;
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int i = 0; i < module.Functions.Count; i++)
                {
                    var f = module.Functions[i];
                    if (mayYield[f.Name]) continue;
                    if (callees.TryGetValue(f.Name, out var cList))
                    {
                        for (int j = 0; j < cList.Count; j++)
                        {
                            if (mayYield.TryGetValue(cList[j], out bool cy) && cy)
                            {
                                mayYield[f.Name] = true;
                                changed = true;
                                break;
                            }
                        }
                    }
                }
            }

            // Step 3: Check @export functions
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                if (!f.IsExported) continue;

                if (mayYield.TryGetValue(f.Name, out bool yields) && yields)
                    _errors.Add($"@export function '{f.Name}' may yield (directly or via called functions). Service functions must complete synchronously. (line {f.Line})");

                // C-1: @export functions cannot use defer/using
                if (ContainsDeferOrUsing(f.Body))
                    _errors.Add($"@export function '{f.Name}' cannot use defer/using (C-1 restriction). (line {f.Line})");
            }
        }

        /// <summary>
        /// Returns true if the statement subtree directly contains a yield, wait, or wait_for statement.
        /// </summary>
        private bool ContainsYieldOrWait(Stmt stmt)
        {
            if (stmt == null) return false;
            if (stmt is WaitStmt || stmt is WaitForStmt || stmt is YieldStmt) return true;

            if (stmt is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                    if (ContainsYieldOrWait(block.Statements[i])) return true;
            }
            else if (stmt is IfStmt ifStmt)
            {
                if (ContainsYieldOrWait(ifStmt.ThenBranch)) return true;
                if (ifStmt.ElseBranch != null && ContainsYieldOrWait(ifStmt.ElseBranch)) return true;
            }
            else if (stmt is WhileStmt whileStmt)
            {
                if (ContainsYieldOrWait(whileStmt.Body)) return true;
            }
            else if (stmt is ForStmt forStmt)
            {
                if (ContainsYieldOrWait(forStmt.Body)) return true;
            }
            else if (stmt is DeferStmt deferStmt)
            {
                if (ContainsYieldOrWait(deferStmt.Body)) return true;
            }
            else if (stmt is UsingStmt usingStmt)
            {
                if (ContainsYieldOrWait(usingStmt.Body)) return true;
            }

            return false;
        }

        /// <summary>
        /// Collect user function names called from the statement subtree.
        /// </summary>
        private void CollectCallees(Stmt stmt, List<string> callees)
        {
            if (stmt == null) return;

            if (stmt is ExprStmt exprStmt)
            {
                CollectCalleesExpr(exprStmt.Expression, callees);
            }
            else if (stmt is VarDeclStmt varDecl)
            {
                if (varDecl.Initializer != null) CollectCalleesExpr(varDecl.Initializer, callees);
            }
            else if (stmt is ReturnStmt retStmt)
            {
                if (retStmt.Value != null) CollectCalleesExpr(retStmt.Value, callees);
            }
            else if (stmt is BlockStmt block)
            {
                for (int i = 0; i < block.Statements.Count; i++)
                    CollectCallees(block.Statements[i], callees);
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
        }

        /// <summary>
        /// Collect user function names from expression subtree.
        /// </summary>
        private void CollectCalleesExpr(Expr expr, List<string> callees)
        {
            if (expr == null) return;

            if (expr is CallExpr call)
            {
                if (_funcDecls != null && _funcDecls.ContainsKey(call.FunctionName))
                    callees.Add(call.FunctionName);
                for (int i = 0; i < call.Arguments.Count; i++)
                    CollectCalleesExpr(call.Arguments[i], callees);
            }
            else if (expr is BinaryExpr bin)
            {
                CollectCalleesExpr(bin.Left, callees);
                CollectCalleesExpr(bin.Right, callees);
            }
            else if (expr is UnaryExpr unary)
            {
                CollectCalleesExpr(unary.Operand, callees);
            }
            else if (expr is AssignExpr assign)
            {
                CollectCalleesExpr(assign.Value, callees);
            }
            // Lang-8: MemberCallExpr — cross-instance call, not a local callee, but recurse into args
            else if (expr is MemberCallExpr mc)
            {
                for (int i = 0; i < mc.Arguments.Count; i++)
                    CollectCalleesExpr(mc.Arguments[i], callees);
            }
        }

        /// <summary>
        /// Lang-7: A1/A2 auto-degradation detection result.
        /// </summary>
        private struct DegradationInfo
        {
            public DegradationType Type;
            public int MvarSlot;
        }

        /// <summary>
        /// Lang-7: Detect pure getter (A1) or pure setter (A2) pattern in an @export function.
        /// A1 pure getter: 0 params, body = single ReturnStmt whose value is an IdentifierExpr referencing a module variable.
        /// A2 pure setter: 1 param, body = single ExprStmt(AssignExpr) where target is module var IdentifierExpr and value is IdentifierExpr of the param.
        /// Returns DegradationType.None if no pattern detected (safe fallback to XCALL).
        /// </summary>
        private DegradationInfo DetectFuncDegradation(FuncDecl func)
        {
            var result = new DegradationInfo { Type = DegradationType.None, MvarSlot = -1 };

            if (func.Body == null || func.Body.Statements == null || func.Body.Statements.Count != 1)
                return result;

            var stmt = func.Body.Statements[0];

            // A1: Pure getter — func has 0 params, body is: return <moduleVar>
            if (func.Parameters.Count == 0 && stmt is ReturnStmt ret && ret.Value is IdentifierExpr retId)
            {
                int mvarSlot = GetModuleVarMvarSlot(retId.Name);
                if (mvarSlot >= 0)
                {
                    result.Type = DegradationType.Getter;
                    result.MvarSlot = mvarSlot;
                }
                return result;
            }

            // A2: Pure setter — func has 1 param, body is: <moduleVar> = <param>
            if (func.Parameters.Count == 1 && stmt is ExprStmt exprStmt && exprStmt.Expression is AssignExpr assign)
            {
                if (assign.Target is IdentifierExpr targetId && assign.Value is IdentifierExpr valueId)
                {
                    if (valueId.Name == func.Parameters[0].Name)
                    {
                        int mvarSlot = GetModuleVarMvarSlot(targetId.Name);
                        if (mvarSlot >= 0)
                        {
                            result.Type = DegradationType.Setter;
                            result.MvarSlot = mvarSlot;
                        }
                    }
                }
                return result;
            }

            return result;
        }

        /// <summary>
        /// Lang-7: Get mvar slot for a module variable by name. Returns -1 if not a module variable.
        /// </summary>
        private int GetModuleVarMvarSlot(string name)
        {
            if (_moduleVarRegisters == null || !_moduleVarRegisters.TryGetValue(name, out int reg))
                return -1;

            // Same mapping as BuildExportTable variable collection
            return reg >= VMConstants.MaxRegisters
                ? (reg - VMConstants.MaxRegisters) + VMConstants.ModuleVarSlots
                : reg - VMConstants.ModuleVarRegBase;
        }

        /// <summary>
        /// Lang-6: Build ExportTable from @export declarations. Returns null if no exports.
        /// </summary>
        private ExportTable BuildExportTable(ModuleNode module, List<FunctionEntry> functionEntries)
        {
            var exportVars = new List<ExportVarEntry>();
            var exportFuncs = new List<ExportFuncEntry>();

            // Collect exported variables (var and const)
            for (int i = 0; i < module.ModuleVariables.Count; i++)
            {
                var decl = module.ModuleVariables[i];
                if (!decl.IsExported) continue;

                if (decl.IsConst)
                {
                    // Lang-12: @export const — register was allocated in ProcessModuleVariables
                    if (_moduleVarRegisters.TryGetValue(decl.Name, out int reg) &&
                        _moduleConstValues.TryGetValue(decl.Name, out Number constVal))
                    {
                        int mvarSlot = reg >= VMConstants.MaxRegisters
                            ? (reg - VMConstants.MaxRegisters) + VMConstants.ModuleVarSlots
                            : reg - VMConstants.ModuleVarRegBase;
                        exportVars.Add(new ExportVarEntry(decl.Name, mvarSlot, false, constVal));
                    }
                }
                else
                {
                    // @export var — must have a register
                    if (_moduleVarRegisters.TryGetValue(decl.Name, out int reg))
                    {
                        int mvarSlot = reg >= VMConstants.MaxRegisters
                            ? (reg - VMConstants.MaxRegisters) + VMConstants.ModuleVarSlots
                            : reg - VMConstants.ModuleVarRegBase;
                        // Lang-10: store compile-time default value
                        Number defaultVal = Number.Zero;
                        if (_moduleVarInitValues.TryGetValue(reg, out Number initVal))
                            defaultVal = initVal;
                        exportVars.Add(new ExportVarEntry(decl.Name, mvarSlot, true, defaultVal));
                    }
                }
            }

            // Collect exported functions
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                if (!f.IsExported) continue;

                // Find the function's index in functionEntries
                int funcIdx = -1;
                for (int j = 0; j < functionEntries.Count; j++)
                {
                    if (functionEntries[j].Name == f.Name)
                    {
                        funcIdx = j;
                        break;
                    }
                }

                if (funcIdx >= 0)
                {
                    // Lang-7: A1/A2 auto-degradation detection
                    var degradation = DetectFuncDegradation(f);
                    // Lang-8: @inline hint
                    exportFuncs.Add(new ExportFuncEntry(f.Name, funcIdx, f.Parameters.Count,
                        degradation.Type, degradation.MvarSlot, f.IsInline));
                }
            }

            if (exportVars.Count == 0 && exportFuncs.Count == 0)
                return null;

            return new ExportTable(exportVars.ToArray(), exportFuncs.ToArray());
        }

        /// <summary>
        /// Lang-9 P3/P4: Build ModuleInlineInfo containing function ASTs, variable
        /// export indices, and module const values for cross-module inline support.
        /// P4: includes ALL function ASTs (not just exported) to enable deep chain inline.
        /// </summary>
        private ModuleInlineInfo BuildModuleInlineInfo(ModuleNode module)
        {
            // P4: Collect ALL function ASTs (exported + non-exported) for deep chain inline
            var funcDecls = new Dictionary<string, FuncDecl>();
            var allFuncNames = new HashSet<string>();
            for (int i = 0; i < module.Functions.Count; i++)
            {
                var f = module.Functions[i];
                allFuncNames.Add(f.Name);
                funcDecls[f.Name] = f;
            }

            // Build exported variable name → export var index mapping
            var varExportIndices = new Dictionary<string, int>();
            // This must match the order in BuildExportTable
            int exportIdx = 0;
            for (int i = 0; i < module.ModuleVariables.Count; i++)
            {
                var decl = module.ModuleVariables[i];
                if (!decl.IsExported) continue;
                if (decl.IsConst)
                {
                    if (_moduleVarRegisters.ContainsKey(decl.Name) && _moduleConstValues.ContainsKey(decl.Name))
                    {
                        varExportIndices[decl.Name] = exportIdx;
                        exportIdx++;
                    }
                }
                else
                {
                    if (_moduleVarRegisters.ContainsKey(decl.Name))
                    {
                        varExportIndices[decl.Name] = exportIdx;
                        exportIdx++;
                    }
                }
            }

            // Collect module const values for cross-module const propagation
            var constValues = new Dictionary<string, Number>();
            if (_moduleConstValues != null)
            {
                foreach (var kv in _moduleConstValues)
                    constValues[kv.Key] = kv.Value;
            }

            // Need at least one exported function for cross-module inline to be useful
            bool hasExported = false;
            for (int i = 0; i < module.Functions.Count; i++)
                if (module.Functions[i].IsExported) { hasExported = true; break; }
            if (!hasExported)
                return null;

            return new ModuleInlineInfo(funcDecls, varExportIndices, constValues, allFuncNames);
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

            // Lang-8: MemberCallExpr generates XCALL which is effectively a call
            if (expr is MemberCallExpr mc)
            {
                // XCALL-based: not a local function call but still non-leaf (recursive ExecuteInstance)
                // Check arguments
                for (int i = 0; i < mc.Arguments.Count; i++)
                    if (ContainsNonLeafExpr(mc.Arguments[i])) return true;
                return true; // XCALL itself disqualifies
            }

            return false;
        }

        // ===== Lang-9: Inline expansion (P1: module-internal trivial inline) =====

        /// <summary>
        /// Lang-9 P1: Check if a function can be inlined at the current call site.
        /// P1 constraints: single return as last statement, no branches/loops/yield/defer/using,
        /// Lang-9 P2: Check if a function is eligible for inline expansion.
        /// Rejects: yield/wait/defer/using, recursive calls, over threshold size, XCALL (MemberCallExpr).
        /// Allows: branches (if/else), loops (while/for), multi-statement bodies, multi-return,
        /// user function calls (non-recursive), struct parameters.
        /// </summary>
        private bool CanInline(string funcName)
        {
            if (_inlineDepth >= InlineDepthMax) return false;
            if (!_funcDecls.TryGetValue(funcName, out var func)) return false;
            // Never inline entry function
            if (_isEntryFunction && func.Name == _currentFunctionName) return false;
            // Recursion guard
            if (_inlineStack != null && _inlineStack.Contains(funcName)) return false;

            var stmts = func.Body.Statements;
            if (stmts.Count == 0) return false;

            // P2: check each statement for disqualifying nodes (yield/wait/defer/using/XCALL)
            for (int i = 0; i < stmts.Count; i++)
            {
                if (!IsInlineSafeStmt(stmts[i])) return false;
            }

            // Size check
            int estimate = EstimateBodySize(func.Body);
            if (estimate > InlineThreshold) return false;

            return true;
        }

        /// <summary>
        /// Lang-9 P2: Check if a statement is safe for inline expansion.
        /// Allows: VarDeclStmt, ExprStmt, ReturnStmt, IfStmt, WhileStmt, ForStmt, BlockStmt.
        /// Rejects: YieldStmt, WaitStmt, WaitForStmt, DeferStmt, UsingStmt.
        /// </summary>
        private bool IsInlineSafeStmt(Stmt stmt)
        {
            if (stmt is VarDeclStmt vd)
            {
                return vd.Initializer == null || IsInlineSafeExpr(vd.Initializer);
            }
            if (stmt is ExprStmt es) return IsInlineSafeExpr(es.Expression);
            if (stmt is ReturnStmt rs) return rs.Value == null || IsInlineSafeExpr(rs.Value);
            if (stmt is IfStmt ifS)
            {
                if (!IsInlineSafeExpr(ifS.Condition)) return false;
                if (!IsInlineSafeStmt(ifS.ThenBranch)) return false;
                if (ifS.ElseBranch != null && !IsInlineSafeStmt(ifS.ElseBranch)) return false;
                return true;
            }
            if (stmt is WhileStmt ws)
            {
                if (!IsInlineSafeExpr(ws.Condition)) return false;
                return IsInlineSafeStmt(ws.Body);
            }
            if (stmt is ForStmt fs)
            {
                if (fs.Initializer != null && !IsInlineSafeStmt(fs.Initializer)) return false;
                if (fs.Condition != null && !IsInlineSafeExpr(fs.Condition)) return false;
                if (fs.Increment != null && !IsInlineSafeExpr(fs.Increment)) return false;
                return IsInlineSafeStmt(fs.Body);
            }
            if (stmt is BlockStmt blk)
            {
                for (int i = 0; i < blk.Statements.Count; i++)
                    if (!IsInlineSafeStmt(blk.Statements[i])) return false;
                return true;
            }
            // YieldStmt, WaitStmt, WaitForStmt, DeferStmt, UsingStmt → not safe
            return false;
        }

        /// <summary>
        /// Lang-9 P2: Check if an expression is safe for inline expansion.
        /// Allows user function calls (recursion handled by _inlineStack guard).
        /// Only MemberCallExpr (XCALL) is disqualified.
        /// </summary>
        private bool IsInlineSafeExpr(Expr expr)
        {
            if (expr == null) return true;
            if (expr is CallExpr call)
            {
                // P2: user function calls are allowed (recursion guard in CanInline)
                for (int i = 0; i < call.Arguments.Count; i++)
                    if (!IsInlineSafeExpr(call.Arguments[i])) return false;
                return true;
            }
            if (expr is MemberCallExpr) return false; // XCALL disqualifies
            if (expr is BinaryExpr bin)
                return IsInlineSafeExpr(bin.Left) && IsInlineSafeExpr(bin.Right);
            if (expr is UnaryExpr un)
                return IsInlineSafeExpr(un.Operand);
            if (expr is AssignExpr assign)
                return IsInlineSafeExpr(assign.Target) && IsInlineSafeExpr(assign.Value);
            if (expr is SyscallExpr sc)
            {
                for (int i = 0; i < sc.Arguments.Count; i++)
                    if (!IsInlineSafeExpr(sc.Arguments[i])) return false;
                return true;
            }
            if (expr is FieldAccessExpr fa) return IsInlineSafeExpr(fa.Target);
            // Literals, identifiers — always safe
            return true;
        }

        /// <summary>
        /// Lang-9: Estimate instruction count for a function body (AST-level heuristic).
        /// Used to decide if a function fits within InlineThreshold.
        /// </summary>
        private int EstimateBodySize(BlockStmt body)
        {
            int total = 0;
            for (int i = 0; i < body.Statements.Count; i++)
                total += EstimateStmtSize(body.Statements[i]);
            return total;
        }

        private int EstimateStmtSize(Stmt stmt)
        {
            if (stmt is VarDeclStmt vd)
                return 1 + (vd.Initializer != null ? EstimateExprSize(vd.Initializer) : 0);
            if (stmt is ReturnStmt rs)
                return 1 + (rs.Value != null ? EstimateExprSize(rs.Value) : 0);
            if (stmt is ExprStmt es)
                return EstimateExprSize(es.Expression);
            if (stmt is BlockStmt blk)
            {
                int s = 0;
                for (int i = 0; i < blk.Statements.Count; i++) s += EstimateStmtSize(blk.Statements[i]);
                return s;
            }
            // P2: proper estimates for branches and loops
            if (stmt is IfStmt ifS)
            {
                int s = 2 + EstimateExprSize(ifS.Condition) + EstimateStmtSize(ifS.ThenBranch);
                if (ifS.ElseBranch != null) s += EstimateStmtSize(ifS.ElseBranch);
                return s;
            }
            if (stmt is WhileStmt ws)
                return 2 + EstimateExprSize(ws.Condition) + EstimateStmtSize(ws.Body);
            if (stmt is ForStmt fs)
            {
                int s = 2;
                if (fs.Initializer != null) s += EstimateStmtSize(fs.Initializer);
                if (fs.Condition != null) s += EstimateExprSize(fs.Condition);
                if (fs.Increment != null) s += EstimateExprSize(fs.Increment);
                s += EstimateStmtSize(fs.Body);
                return s;
            }
            // Yield/Wait/Defer/Using — should not appear in inlinable functions, return large number
            return InlineThreshold + 1;
        }

        private int EstimateExprSize(Expr expr)
        {
            if (expr == null) return 0;
            if (expr is IntLiteralExpr || expr is NumberLiteralExpr || expr is BoolLiteralExpr) return 1;
            if (expr is StringLiteralExpr) return 1;
            if (expr is IdentifierExpr) return 0; // just a register reference
            if (expr is BinaryExpr bin) return 1 + EstimateExprSize(bin.Left) + EstimateExprSize(bin.Right);
            if (expr is UnaryExpr un) return 1 + EstimateExprSize(un.Operand);
            if (expr is CallExpr call)
            {
                int s = 2; // SYSCALL + potential MOVE
                for (int i = 0; i < call.Arguments.Count; i++) s += EstimateExprSize(call.Arguments[i]);
                return s;
            }
            if (expr is SyscallExpr sc)
            {
                int s = 2;
                for (int i = 0; i < sc.Arguments.Count; i++) s += EstimateExprSize(sc.Arguments[i]);
                return s;
            }
            if (expr is AssignExpr assign) return 1 + EstimateExprSize(assign.Value);
            if (expr is FieldAccessExpr) return 0; // register offset
            return 1; // conservative
        }

        /// <summary>
        /// Lang-9 P2: Try to inline a user function call. Returns true if inlined successfully,
        /// in which case destReg holds the result. Returns false if not inlinable (caller should
        /// fall through to normal CALL emission).
        /// P2 supports: multi-statement bodies, branches, loops, multi-return (exit label pattern),
        /// user function calls, struct parameters, void functions.
        /// </summary>
        private bool TryInlineCall(CallExpr call, int destReg, out int resultReg)
        {
            resultReg = destReg >= 0 ? destReg : -1;

            // DC: R8 inlining guard removed — inlining is now allowed inside cleanup blocks.
            // Functions containing defer/using still can't be inlined (CanInline rejects DeferStmt).

            if (!CanInline(call.FunctionName))
            {
                // Lang-9: @inline diagnostics
                EmitInlineFailureDiagnostic(call.FunctionName, call.Line);
                return false;
            }

            var func = _funcDecls[call.FunctionName];

            // Validate argument count (same logic as EmitUserCall)
            int requiredCount = 0;
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                if (func.Parameters[i].DefaultValue == null) requiredCount++;
                else break;
            }
            if (call.Arguments.Count < requiredCount || call.Arguments.Count > func.Parameters.Count)
            {
                // Argument mismatch — don't inline, let normal path report the error
                return false;
            }

            // Phase 1: Compile all arguments into temp registers (before scope changes)
            int[] argRegs = new int[func.Parameters.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);
            // Fill defaults for omitted optional params
            for (int i = call.Arguments.Count; i < func.Parameters.Count; i++)
            {
                var def = func.Parameters[i].DefaultValue;
                argRegs[i] = def != null ? CompileExpr(def) : AllocTemp();
            }

            // Phase 2: Save and set up inline scope
            var savedVars = _variables;
            var savedConsts = _constValues;
            var savedStructVarTypes = _structVarTypes;
            var savedLiveRanges = _liveRanges;
            var savedStmtOrder = _stmtOrder;
            var savedInlineExitJumps = _inlineExitJumps;
            var savedInlineDestReg = _inlineDestReg;

            _variables = new Dictionary<string, int>(savedVars);      // copy parent scope
            _constValues = new Dictionary<string, Number>(savedConsts);
            _structVarTypes = new Dictionary<string, string>(savedStructVarTypes);
            _liveRanges = null; // disable F4 register release inside inline body
            _stmtOrder = 0;

            // Bind parameters: map param names → arg registers
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var param = func.Parameters[i];
                int argReg = argRegs[i];

                // P2: struct parameter support
                if (_structTypes.ContainsKey(param.TypeName) &&
                    _flatStructInfo.TryGetValue(param.TypeName, out var flatInfo))
                {
                    int flatCount = flatInfo.FlatFieldCount;
                    int baseReg = DeclareStructVar(param.Name, flatCount);
                    _structVarTypes[param.Name] = param.TypeName;
                    // Copy struct fields from arg to local
                    if (IsModuleVarReg(argReg))
                    {
                        for (int j = 0; j < flatCount; j++)
                            EmitLoadModuleVar(baseReg + j, argReg + j);
                    }
                    else
                    {
                        for (int j = 0; j < flatCount; j++)
                        {
                            if (baseReg + j != argReg + j)
                                Emit(OpCode.MOVE, baseReg + j, argReg + j);
                        }
                    }
                }
                else
                {
                    // Scalar parameter
                    if (argReg < VarRegBase || argReg >= TempRegBase)
                    {
                        // Scratch zone or temp — copy to a local var register for safety
                        int localReg = DeclareVar(param.Name);
                        if (localReg != argReg)
                            Emit(OpCode.MOVE, localReg, argReg);
                    }
                    else
                    {
                        // Already in var zone — just bind the name
                        _variables[param.Name] = argReg;
                    }
                }
            }

            // Phase 3: Push inline guard
            if (_inlineStack == null) _inlineStack = new HashSet<string>();
            _inlineStack.Add(call.FunctionName);
            _inlineDepth++;

            // Phase 4: Compile inline body with multi-return exit label pattern
            var stmts = func.Body.Statements;
            int actualDest = destReg >= 0 ? destReg : AllocTemp();

            // Set up P2 exit label context: ReturnStmt inside inline body will
            // compile value to _inlineDestReg and JUMP to the exit point
            _inlineExitJumps = new List<int>();
            _inlineDestReg = actualDest;

            // P2: save temp baseline — inline body statement resets must not
            // go below this point, to preserve actualDest and outer temps
            int inlineTempBaseline = _tempTop;

            for (int i = 0; i < stmts.Count; i++)
            {
                CompileStmt(stmts[i]);
                _tempTop = inlineTempBaseline; // reset only inline-local temps
            }

            // Backpatch all exit jumps to current IP (the instruction after inline body)
            int exitIP = CurrentIP();
            for (int i = 0; i < _inlineExitJumps.Count; i++)
                Backpatch(_inlineExitJumps[i], exitIP);

            // Phase 5: Restore scope
            _inlineDepth--;
            _inlineStack.Remove(call.FunctionName);

            _variables = savedVars;
            _constValues = savedConsts;
            _structVarTypes = savedStructVarTypes;
            _liveRanges = savedLiveRanges;
            _stmtOrder = savedStmtOrder;
            _inlineExitJumps = savedInlineExitJumps;
            _inlineDestReg = savedInlineDestReg;

            resultReg = actualDest;

            // P2: record inlined callee for FO6 window analysis adjustment
            if (!_inlinedCalleesPerFunc.TryGetValue(_currentFunctionName, out var inlinedSet))
            {
                inlinedSet = new HashSet<string>();
                _inlinedCalleesPerFunc[_currentFunctionName] = inlinedSet;
            }
            inlinedSet.Add(call.FunctionName);

            return true;
        }

        /// <summary>
        /// Lang-9: Emit diagnostic when a function cannot be inlined and is marked @inline.
        /// </summary>
        private void EmitInlineFailureDiagnostic(string funcName, int line)
        {
            if (!_funcDecls.TryGetValue(funcName, out var func)) return;
            if (!func.IsInline) return; // unmarked → silent fallback
            _warnings.Add($"@inline function '{funcName}' could not be inlined. CALL will be used. (line {line})");
        }

        // ===== Lang-9 P3: Cross-module inline expansion =====

        /// <summary>
        /// Lang-9 P3/P4: Check if a cross-module function (from ServiceBinding) can be inlined.
        /// P4: allows callee user function calls if the callee is itself cross-module inlinable
        /// (deep chain inline). Uses visited set to prevent infinite recursion.
        /// </summary>
        private bool CanInlineCrossModule(FuncDecl func, ServiceBinding binding)
        {
            return CanInlineCrossModule(func, binding.InlineInfo, null);
        }

        private bool CanInlineCrossModule(FuncDecl func, ModuleInlineInfo inlineInfo, HashSet<string> visited)
        {
            if (inlineInfo == null) return false;
            int chainDepth = visited != null ? visited.Count : 0;
            if (_inlineDepth + chainDepth >= InlineDepthMax) return false;
            // DC: R8 inlining guard removed — cross-module inlining allowed in cleanup blocks.
            // Recursion guard (compile-time stack)
            if (_inlineStack != null && _inlineStack.Contains(func.Name)) return false;
            // P4: visited set prevents mutual recursion during chain check
            if (visited != null && visited.Contains(func.Name)) return false;

            var stmts = func.Body.Statements;
            if (stmts.Count == 0) return false;

            // P4: add current function to visited set for recursive callee checks
            bool ownedVisited = false;
            if (visited == null) { visited = new HashSet<string>(); ownedVisited = true; }
            visited.Add(func.Name);

            // Check each statement for safety (P4: allows inlinable callee function calls)
            for (int i = 0; i < stmts.Count; i++)
            {
                if (!IsCrossModuleInlineSafeStmt(stmts[i], inlineInfo, visited))
                {
                    visited.Remove(func.Name);
                    return false;
                }
            }

            // Size check
            int estimate = EstimateBodySize(func.Body);
            if (estimate > InlineThreshold)
            {
                visited.Remove(func.Name);
                return false;
            }

            // Verify all variable references can be resolved in cross-module context
            var paramNames = new HashSet<string>();
            for (int i = 0; i < func.Parameters.Count; i++)
                paramNames.Add(func.Parameters[i].Name);
            if (!AllVarRefsResolvable(func.Body, paramNames, inlineInfo))
            {
                visited.Remove(func.Name);
                return false;
            }

            if (ownedVisited) visited.Clear();
            else visited.Remove(func.Name);

            return true;
        }

        /// <summary>
        /// Lang-9 P3/P4: Check if a statement is safe for cross-module inline.
        /// P4: passes visited set for recursive callee function chain checking.
        /// </summary>
        private bool IsCrossModuleInlineSafeStmt(Stmt stmt, ModuleInlineInfo inlineInfo, HashSet<string> visited)
        {
            if (stmt is VarDeclStmt vd)
                return vd.Initializer == null || IsCrossModuleInlineSafeExpr(vd.Initializer, inlineInfo, visited);
            if (stmt is ExprStmt es) return IsCrossModuleInlineSafeExpr(es.Expression, inlineInfo, visited);
            if (stmt is ReturnStmt rs) return rs.Value == null || IsCrossModuleInlineSafeExpr(rs.Value, inlineInfo, visited);
            if (stmt is IfStmt ifS)
            {
                if (!IsCrossModuleInlineSafeExpr(ifS.Condition, inlineInfo, visited)) return false;
                if (!IsCrossModuleInlineSafeStmt(ifS.ThenBranch, inlineInfo, visited)) return false;
                if (ifS.ElseBranch != null && !IsCrossModuleInlineSafeStmt(ifS.ElseBranch, inlineInfo, visited)) return false;
                return true;
            }
            if (stmt is WhileStmt ws)
            {
                if (!IsCrossModuleInlineSafeExpr(ws.Condition, inlineInfo, visited)) return false;
                return IsCrossModuleInlineSafeStmt(ws.Body, inlineInfo, visited);
            }
            if (stmt is ForStmt fs)
            {
                if (fs.Initializer != null && !IsCrossModuleInlineSafeStmt(fs.Initializer, inlineInfo, visited)) return false;
                if (fs.Condition != null && !IsCrossModuleInlineSafeExpr(fs.Condition, inlineInfo, visited)) return false;
                if (fs.Increment != null && !IsCrossModuleInlineSafeExpr(fs.Increment, inlineInfo, visited)) return false;
                return IsCrossModuleInlineSafeStmt(fs.Body, inlineInfo, visited);
            }
            if (stmt is BlockStmt blk)
            {
                for (int i = 0; i < blk.Statements.Count; i++)
                    if (!IsCrossModuleInlineSafeStmt(blk.Statements[i], inlineInfo, visited)) return false;
                return true;
            }
            // YieldStmt, WaitStmt, WaitForStmt, DeferStmt, UsingStmt → not safe
            return false;
        }

        /// <summary>
        /// Lang-9 P3/P4: Check if an expression is safe for cross-module inline.
        /// P4: callee user function calls are allowed if the callee is itself cross-module inlinable.
        /// Rejects: MemberCallExpr (XCALL), non-inlinable callee user function calls.
        /// Allows: syscall calls, inlinable callee function calls (P4).
        /// </summary>
        private bool IsCrossModuleInlineSafeExpr(Expr expr, ModuleInlineInfo inlineInfo, HashSet<string> visited)
        {
            if (expr == null) return true;
            if (expr is CallExpr call)
            {
                if (inlineInfo.AllFuncNames.Contains(call.FunctionName))
                {
                    // P4: callee user function — check if it's also cross-module inlinable
                    if (!inlineInfo.FuncDecls.TryGetValue(call.FunctionName, out var calleeFunc))
                        return false; // AST not available
                    if (!CanInlineCrossModule(calleeFunc, inlineInfo, visited))
                        return false;
                    for (int i = 0; i < call.Arguments.Count; i++)
                        if (!IsCrossModuleInlineSafeExpr(call.Arguments[i], inlineInfo, visited)) return false;
                    return true;
                }
                // Only allow if it's a known syscall in the caller
                if (!_syscalls.ContainsKey(call.FunctionName)) return false;
                for (int i = 0; i < call.Arguments.Count; i++)
                    if (!IsCrossModuleInlineSafeExpr(call.Arguments[i], inlineInfo, visited)) return false;
                return true;
            }
            if (expr is MemberCallExpr) return false; // XCALL disqualifies
            if (expr is BinaryExpr bin)
                return IsCrossModuleInlineSafeExpr(bin.Left, inlineInfo, visited) && IsCrossModuleInlineSafeExpr(bin.Right, inlineInfo, visited);
            if (expr is UnaryExpr un)
                return IsCrossModuleInlineSafeExpr(un.Operand, inlineInfo, visited);
            if (expr is AssignExpr assign)
                return IsCrossModuleInlineSafeExpr(assign.Target, inlineInfo, visited) && IsCrossModuleInlineSafeExpr(assign.Value, inlineInfo, visited);
            if (expr is SyscallExpr sc)
            {
                for (int i = 0; i < sc.Arguments.Count; i++)
                    if (!IsCrossModuleInlineSafeExpr(sc.Arguments[i], inlineInfo, visited)) return false;
                return true;
            }
            if (expr is FieldAccessExpr fa) return IsCrossModuleInlineSafeExpr(fa.Target, inlineInfo, visited);
            // Literals, identifiers — always safe
            return true;
        }

        /// <summary>
        /// Lang-9 P3: Verify all variable references in the function body can be resolved
        /// in the cross-module inline context (params, exported vars, consts, or locals declared in body).
        /// </summary>
        private bool AllVarRefsResolvable(BlockStmt body, HashSet<string> paramNames, ModuleInlineInfo inlineInfo)
        {
            // Collect local variable declarations in the body
            var localNames = new HashSet<string>(paramNames);
            CollectLocalDecls(body, localNames);

            // Check all identifier references
            return AllIdentRefsResolvable(body, localNames, inlineInfo);
        }

        private void CollectLocalDecls(Stmt stmt, HashSet<string> locals)
        {
            if (stmt is VarDeclStmt vd) { locals.Add(vd.Name); return; }
            if (stmt is BlockStmt blk) { for (int i = 0; i < blk.Statements.Count; i++) CollectLocalDecls(blk.Statements[i], locals); return; }
            if (stmt is IfStmt ifS)
            {
                CollectLocalDecls(ifS.ThenBranch, locals);
                if (ifS.ElseBranch != null) CollectLocalDecls(ifS.ElseBranch, locals);
                return;
            }
            if (stmt is WhileStmt ws) { CollectLocalDecls(ws.Body, locals); return; }
            if (stmt is ForStmt fs)
            {
                if (fs.Initializer != null) CollectLocalDecls(fs.Initializer, locals);
                CollectLocalDecls(fs.Body, locals);
            }
        }

        private bool AllIdentRefsResolvable(Stmt stmt, HashSet<string> knownNames, ModuleInlineInfo inlineInfo)
        {
            if (stmt is VarDeclStmt vd)
                return vd.Initializer == null || AllIdentRefsResolvableExpr(vd.Initializer, knownNames, inlineInfo);
            if (stmt is ExprStmt es) return AllIdentRefsResolvableExpr(es.Expression, knownNames, inlineInfo);
            if (stmt is ReturnStmt rs) return rs.Value == null || AllIdentRefsResolvableExpr(rs.Value, knownNames, inlineInfo);
            if (stmt is BlockStmt blk)
            {
                for (int i = 0; i < blk.Statements.Count; i++)
                    if (!AllIdentRefsResolvable(blk.Statements[i], knownNames, inlineInfo)) return false;
                return true;
            }
            if (stmt is IfStmt ifS)
            {
                if (!AllIdentRefsResolvableExpr(ifS.Condition, knownNames, inlineInfo)) return false;
                if (!AllIdentRefsResolvable(ifS.ThenBranch, knownNames, inlineInfo)) return false;
                if (ifS.ElseBranch != null && !AllIdentRefsResolvable(ifS.ElseBranch, knownNames, inlineInfo)) return false;
                return true;
            }
            if (stmt is WhileStmt ws)
            {
                if (!AllIdentRefsResolvableExpr(ws.Condition, knownNames, inlineInfo)) return false;
                return AllIdentRefsResolvable(ws.Body, knownNames, inlineInfo);
            }
            if (stmt is ForStmt fs)
            {
                if (fs.Initializer != null && !AllIdentRefsResolvable(fs.Initializer, knownNames, inlineInfo)) return false;
                if (fs.Condition != null && !AllIdentRefsResolvableExpr(fs.Condition, knownNames, inlineInfo)) return false;
                if (fs.Increment != null && !AllIdentRefsResolvableExpr(fs.Increment, knownNames, inlineInfo)) return false;
                return AllIdentRefsResolvable(fs.Body, knownNames, inlineInfo);
            }
            return true;
        }

        private bool AllIdentRefsResolvableExpr(Expr expr, HashSet<string> knownNames, ModuleInlineInfo inlineInfo)
        {
            if (expr == null) return true;
            if (expr is IdentifierExpr ident)
            {
                if (knownNames.Contains(ident.Name)) return true;
                if (inlineInfo.VarExportIndices.ContainsKey(ident.Name)) return true;
                if (inlineInfo.ConstValues.ContainsKey(ident.Name)) return true;
                return false; // unresolvable reference → reject inline
            }
            if (expr is BinaryExpr bin)
                return AllIdentRefsResolvableExpr(bin.Left, knownNames, inlineInfo) &&
                       AllIdentRefsResolvableExpr(bin.Right, knownNames, inlineInfo);
            if (expr is UnaryExpr un) return AllIdentRefsResolvableExpr(un.Operand, knownNames, inlineInfo);
            if (expr is CallExpr call)
            {
                for (int i = 0; i < call.Arguments.Count; i++)
                    if (!AllIdentRefsResolvableExpr(call.Arguments[i], knownNames, inlineInfo)) return false;
                return true;
            }
            if (expr is AssignExpr assign)
            {
                return AllIdentRefsResolvableExpr(assign.Target, knownNames, inlineInfo) &&
                       AllIdentRefsResolvableExpr(assign.Value, knownNames, inlineInfo);
            }
            if (expr is SyscallExpr sc)
            {
                for (int i = 0; i < sc.Arguments.Count; i++)
                    if (!AllIdentRefsResolvableExpr(sc.Arguments[i], knownNames, inlineInfo)) return false;
                return true;
            }
            if (expr is FieldAccessExpr fa) return AllIdentRefsResolvableExpr(fa.Target, knownNames, inlineInfo);
            // NumberLiteral, StringLiteral, BoolLiteral, etc. — always resolvable
            return true;
        }

        /// <summary>
        /// Lang-9 P3: Attempt to inline a cross-module member call (svc.func(args)).
        /// Called from CompileMemberCallExpr before falling through to XCALL.
        /// Returns true if inline succeeded (bytecode emitted), false if XCALL should be used.
        /// </summary>
        private bool TryInlineMemberCall(MemberCallExpr mc, int destReg, ServiceBinding binding, out int resultReg)
        {
            resultReg = destReg >= 0 ? destReg : -1;

            if (binding.InlineInfo == null) return false;
            if (!binding.InlineInfo.FuncDecls.TryGetValue(mc.MemberName, out var func)) return false;
            if (!CanInlineCrossModule(func, binding)) return false;

            // Validate argument count
            int requiredCount = 0;
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                if (func.Parameters[i].DefaultValue == null) requiredCount++;
                else break;
            }
            if (mc.Arguments.Count < requiredCount || mc.Arguments.Count > func.Parameters.Count)
                return false;

            // Resolve service instance register
            int svcReg = ResolveVar(mc.TargetName);
            if (IsModuleVarReg(svcReg))
            {
                int tmpSvc = AllocTemp();
                EmitLoadModuleVar(tmpSvc, svcReg);
                svcReg = tmpSvc;
            }

            // Phase 1: Compile all arguments into temp registers
            int[] argRegs = new int[func.Parameters.Count];
            for (int i = 0; i < mc.Arguments.Count; i++)
                argRegs[i] = CompileExpr(mc.Arguments[i]);
            for (int i = mc.Arguments.Count; i < func.Parameters.Count; i++)
            {
                var def = func.Parameters[i].DefaultValue;
                argRegs[i] = def != null ? CompileExpr(def) : AllocTemp();
            }

            // Phase 2: Save and set up inline scope
            var savedVars = _variables;
            var savedConsts = _constValues;
            var savedStructVarTypes = _structVarTypes;
            var savedLiveRanges = _liveRanges;
            var savedStmtOrder = _stmtOrder;
            var savedInlineExitJumps = _inlineExitJumps;
            var savedInlineDestReg = _inlineDestReg;
            var savedXInlineSvcReg = _xInlineSvcReg;
            var savedXInlineVars = _xInlineVars;
            var savedXInlineInfo = _xInlineInfo;

            _variables = new Dictionary<string, int>(savedVars);
            _constValues = new Dictionary<string, Number>(savedConsts);
            _structVarTypes = new Dictionary<string, string>(savedStructVarTypes);
            _liveRanges = null; // disable F4 inside inline body
            _stmtOrder = 0;

            // Populate cross-module const values
            if (binding.InlineInfo.ConstValues != null)
            {
                foreach (var kv in binding.InlineInfo.ConstValues)
                    _constValues[kv.Key] = kv.Value;
            }

            // Set up cross-module variable redirect context
            _xInlineSvcReg = svcReg;
            _xInlineVars = binding.InlineInfo.VarExportIndices;
            // P4: set cross-module inline info for callee function lookup
            _xInlineInfo = binding.InlineInfo;

            // Bind parameters: map param names → arg registers
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var param = func.Parameters[i];
                int argReg = argRegs[i];
                // P3: scalar parameters only (struct cross-module params deferred)
                if (argReg < VarRegBase || argReg >= TempRegBase)
                {
                    int localReg = DeclareVar(param.Name);
                    if (localReg != argReg)
                        Emit(OpCode.MOVE, localReg, argReg);
                }
                else
                {
                    _variables[param.Name] = argReg;
                }
            }

            // Phase 3: Push inline guard
            if (_inlineStack == null) _inlineStack = new HashSet<string>();
            _inlineStack.Add(func.Name);
            _inlineDepth++;

            // Phase 4: Compile inline body with multi-return exit label pattern
            var stmts = func.Body.Statements;
            int actualDest = destReg >= 0 ? destReg : AllocTemp();

            _inlineExitJumps = new List<int>();
            _inlineDestReg = actualDest;

            int inlineTempBaseline = _tempTop;

            for (int i = 0; i < stmts.Count; i++)
            {
                CompileStmt(stmts[i]);
                _tempTop = inlineTempBaseline;
            }

            // Backpatch all exit jumps
            int exitIP = CurrentIP();
            for (int i = 0; i < _inlineExitJumps.Count; i++)
                Backpatch(_inlineExitJumps[i], exitIP);

            // Phase 5: Restore scope
            _inlineDepth--;
            _inlineStack.Remove(func.Name);

            _variables = savedVars;
            _constValues = savedConsts;
            _structVarTypes = savedStructVarTypes;
            _liveRanges = savedLiveRanges;
            _stmtOrder = savedStmtOrder;
            _inlineExitJumps = savedInlineExitJumps;
            _inlineDestReg = savedInlineDestReg;
            _xInlineSvcReg = savedXInlineSvcReg;
            _xInlineVars = savedXInlineVars;
            _xInlineInfo = savedXInlineInfo;

            resultReg = actualDest;
            return true;
        }

        /// <summary>
        /// Lang-9 P4: Inline a callee's own function during cross-module inline.
        /// Called when the inlined cross-module body calls another function in the callee module.
        /// Uses the existing _xInlineSvcReg/_xInlineVars/_xInlineInfo context from TryInlineMemberCall.
        /// </summary>
        private bool TryInlineCalleeFunc(CallExpr call, int destReg, out int resultReg)
        {
            resultReg = destReg >= 0 ? destReg : -1;

            if (_xInlineInfo == null) return false;
            if (!_xInlineInfo.FuncDecls.TryGetValue(call.FunctionName, out var func)) return false;
            if (!CanInlineCrossModule(func, _xInlineInfo, null)) return false;

            // Validate argument count
            int requiredCount = 0;
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                if (func.Parameters[i].DefaultValue == null) requiredCount++;
                else break;
            }
            if (call.Arguments.Count < requiredCount || call.Arguments.Count > func.Parameters.Count)
                return false;

            // Phase 1: Compile all arguments into temp registers
            int[] argRegs = new int[func.Parameters.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);
            for (int i = call.Arguments.Count; i < func.Parameters.Count; i++)
            {
                var def = func.Parameters[i].DefaultValue;
                argRegs[i] = def != null ? CompileExpr(def) : AllocTemp();
            }

            // Phase 2: Save inline scope (keep _xInlineSvcReg/_xInlineVars/_xInlineInfo — same module)
            var savedVars = _variables;
            var savedConsts = _constValues;
            var savedStructVarTypes = _structVarTypes;
            var savedLiveRanges = _liveRanges;
            var savedStmtOrder = _stmtOrder;
            var savedInlineExitJumps = _inlineExitJumps;
            var savedInlineDestReg = _inlineDestReg;

            _variables = new Dictionary<string, int>(savedVars);
            _constValues = new Dictionary<string, Number>(savedConsts);
            _structVarTypes = new Dictionary<string, string>(savedStructVarTypes);
            _liveRanges = null;
            _stmtOrder = 0;

            // Populate cross-module const values (same module as parent)
            if (_xInlineInfo.ConstValues != null)
            {
                foreach (var kv in _xInlineInfo.ConstValues)
                    _constValues[kv.Key] = kv.Value;
            }

            // Bind parameters: map param names → arg registers
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                var param = func.Parameters[i];
                int argReg = argRegs[i];
                if (argReg < VarRegBase || argReg >= TempRegBase)
                {
                    int localReg = DeclareVar(param.Name);
                    if (localReg != argReg)
                        Emit(OpCode.MOVE, localReg, argReg);
                }
                else
                {
                    _variables[param.Name] = argReg;
                }
            }

            // Phase 3: Push inline guard
            if (_inlineStack == null) _inlineStack = new HashSet<string>();
            _inlineStack.Add(func.Name);
            _inlineDepth++;

            // Phase 4: Compile inline body with multi-return exit label pattern
            var stmts = func.Body.Statements;
            int actualDest = destReg >= 0 ? destReg : AllocTemp();

            _inlineExitJumps = new List<int>();
            _inlineDestReg = actualDest;

            int inlineTempBaseline = _tempTop;

            for (int i = 0; i < stmts.Count; i++)
            {
                CompileStmt(stmts[i]);
                _tempTop = inlineTempBaseline;
            }

            // Backpatch all exit jumps
            int exitIP = CurrentIP();
            for (int i = 0; i < _inlineExitJumps.Count; i++)
                Backpatch(_inlineExitJumps[i], exitIP);

            // Phase 5: Restore scope
            _inlineDepth--;
            _inlineStack.Remove(func.Name);

            _variables = savedVars;
            _constValues = savedConsts;
            _structVarTypes = savedStructVarTypes;
            _liveRanges = savedLiveRanges;
            _stmtOrder = savedStmtOrder;
            _inlineExitJumps = savedInlineExitJumps;
            _inlineDestReg = savedInlineDestReg;

            resultReg = actualDest;
            return true;
        }

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
                // Safety: only redirect when original dest (ins.A) is a temp register (≥ TempRegBase).
                // Variable registers may be read later; redirecting away from them would break semantics.
                // Lang-1: Module var registers (≥ ModuleVarRegBase) are persistent absolute and must NOT be redirected.
                if (IsResultProducer(ins.Code) && next.Code == OpCode.MOVE
                    && next.B == ins.A && ins.A >= TempRegBase && ins.A < VMConstants.ModuleVarRegBase)
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
