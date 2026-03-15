namespace FFVM
{
    public static class VMConstants
    {
        // Instance limits
        public const int MaxInstances = 128;
        public const int MaxRegisters = 64;
        public const int MaxCallDepth = 16;

        // Module limits
        public const int MaxModules = 64;
        public const int MaxModuleSize = 64 * 1024; // 64KB per module

        // Syscall limits
        public const int MaxSyscalls = 256;

        // Snapshot
        public const int SnapshotRingSize = 8;

        // Link table
        public const int MaxLinkEntries = 1024;

        // Instance pool free stack
        public const int MaxFreeStack = MaxInstances;
    }
}
