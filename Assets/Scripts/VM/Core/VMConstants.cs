namespace FFVM
{
    public static class VMConstants
    {
        // Instance limits
        //
        // 内存预算参考 (VMSlot = 8 bytes):
        //   单实例 RAM ≈ MaxRegisters × 8 = 512 bytes (仅寄存器部分)
        //   全实例 RAM ≈ MaxInstances × ~740 bytes ≈ 92 KB
        //   快照环总量 ≈ 全实例 RAM × SnapshotRingSize ≈ 740 KB (预分配)
        //   O10: 实际快照拷贝量 ≈ ActiveCount × ~740B (典型 3-10 实例 ≈ 2-7 KB)
        //   调参时以此为锚点评估内存压力
        //
        public const int MaxInstances = 128;
        public const int MaxRegisters = 64;
        public const int ModuleVarSlots = (MaxRegisters / 64) * 8;   // 8 slots per 64 registers
        public const int ModuleVarRegBase = MaxRegisters - ModuleVarSlots; // r56 when MaxRegisters=64
        public const int MaxCallDepth = 16;
        public const int MaxCleanupDepth = 8;

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
