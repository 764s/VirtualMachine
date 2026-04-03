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

        public FunctionEntry(string name, int entryIP, int paramCount, int localRegCount, bool isLeaf = false)
        {
            Name = name;
            EntryIP = entryIP;
            ParamCount = paramCount;
            LocalRegCount = localRegCount;
            IsLeaf = isLeaf;
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
        public readonly int RequiredRegisters;
        public readonly FunctionEntry[] Functions;

        /// <summary>Logical instruction count (excluding the sentinel).</summary>
        public int InstructionCount => Instructions.Length - 1;

        /// <summary>DBG1: IP → source line number mapping. Parallel array to Instructions. Null in release builds.</summary>
        public readonly int[] SourceMap;

        /// <summary>DBG2: Variable symbol table for debugging. Null in release builds.</summary>
        public readonly SymbolEntry[] SymbolTable;

        public VMProgram(Instruction[] instructions, Number[] constants, int requiredRegisters,
            FunctionEntry[] functions = null, int[] sourceMap = null, SymbolEntry[] symbolTable = null)
        {
            // Append a sentinel instruction that triggers PanicOutOfBounds if IP
            // ever overruns the program.  This lets the hot dispatch loop skip
            // per-instruction bounds checks while remaining safe against compiler bugs.
            var sentinel = new Instruction[instructions.Length + 1];
            System.Array.Copy(instructions, sentinel, instructions.Length);
            sentinel[instructions.Length] = new Instruction(OpCode.SENTINEL);
            Instructions = sentinel;

            // Extend SourceMap to match Instructions.Length (sentinel maps to line -1)
            if (sourceMap != null)
            {
                var extMap = new int[sourceMap.Length + 1];
                System.Array.Copy(sourceMap, extMap, sourceMap.Length);
                extMap[sourceMap.Length] = -1;
                SourceMap = extMap;
            }
            else
            {
                SourceMap = null;
            }

            Constants = constants;
            RequiredRegisters = requiredRegisters;
            Functions = functions ?? System.Array.Empty<FunctionEntry>();
            SymbolTable = symbolTable;
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
