namespace FFVM
{
    public static class VMConstants
    {
        // Instance limits
        //
        // 内存预算参考 (VMSlot = 8 bytes):
        //   单实例 RAM ≈ MaxRegisters × 8 bytes (仅寄存器部分)
        //   全实例 RAM ≈ MaxInstances × (MaxRegisters×8 + 调用栈 + Cleanup栈 + 字段开销) bytes
        //   快照环总量 ≈ 全实例 RAM × SnapshotRingSize (预分配)
        //   O10: 实际快照拷贝量 ≈ ActiveCount × 单实例 (典型 3-10 实例)
        //   调参时以此为锚点评估内存压力
        //
        //   当前配置 (MaxRegisters=64): 单实例寄存器 = 512B, 全实例 ≈ 92 KB, 快照环 ≈ 740 KB
        //
        public const int MaxInstances = 128;
        public const int MaxRegisters = 64;
        public const int MaxCallDepth = 16;
        public const int MaxCleanupDepth = 8;

        // Register layout (derived from MaxRegisters, all zones auto-adjust):
        //   r0..ScratchZoneSize-1          — scratch zone (syscall args/return, absolute)
        //   ScratchZoneSize..TempRegBase-1  — local variables (windowed)
        //   TempRegBase..ModuleVarRegBase-1 — expression temporaries (windowed+remapped)
        //   ModuleVarRegBase..MaxRegisters-1 — module variables (absolute via LOAD_MVAR/STORE_MVAR)
        //
        // Changing MaxRegisters (must be multiple of 64) auto-adjusts all derived constants.
        public const int ScratchZoneSize = 16;
        public const int ModuleVarSlots = (MaxRegisters / 64) * 8;
        public const int ModuleVarRegBase = MaxRegisters - ModuleVarSlots;
        public const int TempSlots = (MaxRegisters / 64) * 8;
        public const int TempRegBase = ModuleVarRegBase - TempSlots;
        public const int LocalVarSlots = TempRegBase - ScratchZoneSize;

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
