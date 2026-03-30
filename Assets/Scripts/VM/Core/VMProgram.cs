namespace FFVM
{
    /// <summary>
    /// ROM: a compiled bytecode program. Read-only after construction.
    /// Not part of snapshot — shared across instances of the same module.
    /// </summary>
    public class VMProgram
    {
        public readonly Instruction[] Instructions;
        public readonly Number[] Constants;
        public readonly int RequiredRegisters;

        public VMProgram(Instruction[] instructions, Number[] constants, int requiredRegisters)
        {
            Instructions = instructions;
            Constants = constants;
            RequiredRegisters = requiredRegisters;
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
