namespace FFVM
{
    /// <summary>
    /// A compiled function's metadata in the function table.
    /// </summary>
    public struct FunctionEntry
    {
        public readonly string Name;
        public readonly int EntryIP;
        public readonly int ParamCount;
        public readonly int LocalRegCount; // window size: registers above r16 used by this function

        public FunctionEntry(string name, int entryIP, int paramCount, int localRegCount)
        {
            Name = name;
            EntryIP = entryIP;
            ParamCount = paramCount;
            LocalRegCount = localRegCount;
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

        public VMProgram(Instruction[] instructions, Number[] constants, int requiredRegisters, FunctionEntry[] functions = null)
        {
            Instructions = instructions;
            Constants = constants;
            RequiredRegisters = requiredRegisters;
            Functions = functions ?? System.Array.Empty<FunctionEntry>();
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
