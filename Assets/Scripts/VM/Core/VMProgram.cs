namespace FFVM
{
    /// <summary>
    /// DBG2: Symbol table entry for debugging.
    /// Maps variable name → register + struct field info + scope.
    /// </summary>
    public struct SymbolEntry
    {
        public readonly string Name;
        public readonly int Register;
        public readonly int FieldCount;           // >0 for struct variables
        public readonly string[] FieldNames;       // null for scalar variables
        public readonly string ScopeFunctionName;  // which function this variable belongs to

        public SymbolEntry(string name, int register, int fieldCount, string[] fieldNames, string scopeFunctionName)
        {
            Name = name;
            Register = register;
            FieldCount = fieldCount;
            FieldNames = fieldNames;
            ScopeFunctionName = scopeFunctionName;
        }
    }

    /// <summary>
    /// A compiled function's metadata in the function table.
    /// </summary>
    public struct FunctionEntry
    {
        public readonly string Name;
        public readonly int EntryIP;
        public readonly int ParamCount;
        public readonly int LocalRegCount; // window size: registers above r16 used by this function
        public readonly bool IsLeaf; // FO1: true if function contains no CallExpr/wait/wait_for
        /// <summary>VOM2 Phase2: function declared <c>@readonly</c> / <c>@static_readonly</c>; required by <c>VMEngine.StaticReadOnlyCall</c>.</summary>
        public readonly bool IsReadOnly;

        public FunctionEntry(string name, int entryIP, int paramCount, int localRegCount, bool isLeaf = false, bool isReadOnly = false)
        {
            Name = name;
            EntryIP = entryIP;
            ParamCount = paramCount;
            LocalRegCount = localRegCount;
            IsLeaf = isLeaf;
            IsReadOnly = isReadOnly;
        }
    }

    /// <summary>
    /// ROM: a compiled bytecode program. Read-only after construction.
    /// Not part of snapshot — shared across instances of the same module.
    /// </summary>
    public class VMProgram
    {
        public readonly Instruction[] Instructions;
        public readonly Number[] Constants;
        public readonly string[] StringConstants;
        public readonly int RequiredRegisters;
        public readonly FunctionEntry[] Functions;

        /// <summary>DBG1: IP → source line number mapping. Parallel array to Instructions. Null in release builds.</summary>
        public readonly int[] SourceMap;

        /// <summary>
        /// DAP-F Phase 2: IP → source file id mapping. Parallel array to <see cref="SourceMap"/>.
        /// Null when <see cref="SourceMap"/> is null. Each entry is an index into <see cref="SourceFiles"/>;
        /// 0 is always the main compilation unit. Sentinel slot carries -1.
        /// </summary>
        public readonly int[] SourceFileMap;

        /// <summary>
        /// DAP-F Phase 2: file id → source file path. Index 0 is the main compilation unit;
        /// subsequent indices correspond to included files (preprocessor <c>OriginFile</c>).
        /// Strings are stored as the compiler saw them; callers should normalise before comparison
        /// (e.g. via <see cref="System.IO.Path.GetFullPath(string)"/>).
        /// </summary>
        public readonly string[] SourceFiles;

        /// <summary>DBG2: Variable symbol table for debugging. Null in release builds.</summary>
        public readonly SymbolEntry[] SymbolTable;

        /// <summary>B-ζ3: Jump tables for SWITCH instruction dispatch.</summary>
        public readonly int[][] JumpTables;

        /// <summary>Lang-1.1b: Number of extended (heap-allocated) registers required. 0 = none.</summary>
        public readonly int RequiredExtendedRegisters;

        /// <summary>Lang-6: Export table for cross-instance access. Null if module has no @export declarations.</summary>
        public readonly ExportTable ExportTable;

        /// <summary>O15: Logical instruction count, excluding the trailing SENTINEL.</summary>
        public int InstructionCount => Instructions.Length - 1;

        /// <summary>
        /// VOM1: Monotonic version stamp. Incremented by <see cref="Invalidate"/> on hot-reload
        /// to invalidate previously resolved <see cref="MethodHandle"/> instances. Starts at 1.
        /// </summary>
        public int Version { get; private set; } = 1;

        /// <summary>VOM1: name → Functions[] index cache. Populated lazily by ResolveMethod.</summary>
        private System.Collections.Generic.Dictionary<string, int> _methodIndexCache;

        public VMProgram(Instruction[] instructions, Number[] constants, int requiredRegisters,
            FunctionEntry[] functions = null, int[] sourceMap = null, SymbolEntry[] symbolTable = null,
            string[] stringConstants = null, int[][] jumpTables = null, int requiredExtendedRegisters = 0,
            ExportTable exportTable = null, int[] sourceFileMap = null, string[] sourceFiles = null)
        {
            // O15: append SENTINEL — allows removing per-instruction boundary check in ExecuteInstance.
            var withSentinel = new Instruction[instructions.Length + 1];
            System.Array.Copy(instructions, withSentinel, instructions.Length);
            withSentinel[instructions.Length] = new Instruction(OpCode.SENTINEL);
            Instructions = withSentinel;

            Constants = constants;
            RequiredRegisters = requiredRegisters;
            Functions = functions ?? System.Array.Empty<FunctionEntry>();

            // O15: sync SourceMap — extend to cover SENTINEL with invalid line marker (-1).
            if (sourceMap != null)
            {
                var withSentinelMap = new int[sourceMap.Length + 1];
                System.Array.Copy(sourceMap, withSentinelMap, sourceMap.Length);
                withSentinelMap[sourceMap.Length] = -1;
                SourceMap = withSentinelMap;
            }
            else
            {
                SourceMap = null;
            }

            // DAP-F Phase 2: SourceFileMap parallels SourceMap and must be the same length
            // (including sentinel). When the caller does not provide one, default every IP to
            // file id 0 so consumers always have a valid array to read from.
            if (sourceMap != null)
            {
                var srcFileMap = new int[sourceMap.Length + 1];
                if (sourceFileMap != null)
                {
                    int copyLen = System.Math.Min(sourceFileMap.Length, sourceMap.Length);
                    System.Array.Copy(sourceFileMap, srcFileMap, copyLen);
                }
                srcFileMap[sourceMap.Length] = -1;
                SourceFileMap = srcFileMap;
            }
            else
            {
                SourceFileMap = null;
            }

            SourceFiles = sourceFiles;

            SymbolTable = symbolTable;
            StringConstants = stringConstants ?? System.Array.Empty<string>();
            JumpTables = jumpTables ?? System.Array.Empty<int[]>();
            RequiredExtendedRegisters = requiredExtendedRegisters;
            ExportTable = exportTable;
        }

        /// <summary>
        /// DAP-F Phase 2: resolve a client-supplied source path to a <see cref="SourceFiles"/> index.
        /// Comparison is path-normalised + case-insensitive (matches <c>DapServer.PathsEqual</c>).
        /// Returns -1 if the path is empty, the program has no debug info, or no file matches.
        /// </summary>
        public int TryFindFileId(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath) || SourceFiles == null) return -1;
            string target;
            try { target = System.IO.Path.GetFullPath(sourcePath); }
            catch { target = sourcePath; }
            for (int i = 0; i < SourceFiles.Length; i++)
            {
                string candidate = SourceFiles[i];
                if (string.IsNullOrEmpty(candidate)) continue;
                string normalized;
                try { normalized = System.IO.Path.GetFullPath(candidate); }
                catch { normalized = candidate; }
                if (string.Equals(normalized, target, System.StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Look up a function by name. Returns true if found.
        /// </summary>
        public bool TryGetFunction(string name, out FunctionEntry entry)
        {
            for (int i = 0; i < Functions.Length; i++)
            {
                if (Functions[i].Name == name)
                {
                    entry = Functions[i];
                    return true;
                }
            }
            entry = default;
            return false;
        }

        /// <summary>
        /// VOM1: Resolve a function by name to a cached <see cref="MethodHandle"/>.
        /// First call builds the dictionary cache (O(N)); subsequent calls are O(1).
        /// Returns <see cref="MethodHandle.Invalid"/> if no function matches.
        /// </summary>
        public MethodHandle ResolveMethod(string name)
        {
            if (string.IsNullOrEmpty(name)) return MethodHandle.Invalid;

            if (_methodIndexCache == null)
            {
                var cache = new System.Collections.Generic.Dictionary<string, int>(Functions.Length);
                for (int i = 0; i < Functions.Length; i++)
                {
                    // Last-wins on duplicate names (matches TryGetFunction semantics: first-wins by index).
                    // We use first-wins to be consistent with Functions[] linear scan order.
                    if (!cache.ContainsKey(Functions[i].Name))
                    {
                        cache[Functions[i].Name] = i;
                    }
                }
                _methodIndexCache = cache;
            }

            if (!_methodIndexCache.TryGetValue(name, out var index))
            {
                return MethodHandle.Invalid;
            }

            ref readonly var fn = ref Functions[index];
            // ReturnCount is 1 for current FFS (single r0 return); reserved for FF4 multi-return.
            return new MethodHandle(index, Version, fn.EntryIP, fn.ParamCount, 1);
        }

        /// <summary>
        /// VOM1: Invalidate all previously issued <see cref="MethodHandle"/> instances.
        /// Call after hot-reload / module-replacement. Increments <see cref="Version"/>
        /// and clears the name cache; existing handles fail <see cref="MethodHandle.IsValid"/>.
        /// </summary>
        public void Invalidate()
        {
            unchecked { Version = Version + 1; }
            _methodIndexCache = null;
        }
    }

    /// <summary>
    /// Module table: maps moduleSlot → VMProgram.
    /// Fixed-size array, pre-allocated.
    /// </summary>
    public class VMModuleTable
    {
        private readonly VMProgram[] _modules = new VMProgram[VMConstants.MaxModules];

        public void Load(int slot, VMProgram program)
        {
            _modules[slot] = program;
        }

        public VMProgram Get(int slot)
        {
            if (slot < 0 || slot >= VMConstants.MaxModules) return null;
            return _modules[slot];
        }
    }
}
