namespace FFVM
{
    /// <summary>
    /// Instance pool manages pre-allocated VM instances.
    /// Zero-allocation: creation = take free slot, destruction = return to free stack.
    /// Free stack order must be deterministic for rollback consistency.
    /// O9: ActiveList tracks live instance IDs for Tick() — avoids scanning all 128 slots.
    /// </summary>
    public struct InstancePool
    {
        public VMInstanceState[] Instances;

        // Free stack for deterministic allocation order
        public int[] FreeStack;
        public int FreeTop;

        // O9: Active instance list — Tick() iterates only these
        public int[] ActiveList;
        public int ActiveListCount;

        // Lang-1.1b: Per-instance extended register pools (heap-allocated).
        // Null entry = instance does not use extended registers.
        public Number[][] ExtendedRegs;

        // VOM5: Per-instance host service binding (heap-allocated, ref type).
        // SYSCALL dispatch reads Bindings[id] at runtime to resolve callbacks.
        // Free() clears entries so dead slots don't pin GC roots.
        public HostBindings[] Bindings;

        // Legacy alias: always equals ActiveListCount (kept for snapshot compat)
        public int ActiveCount { get => ActiveListCount; set => ActiveListCount = value; }

        public void Init()
        {
            Instances = new VMInstanceState[VMConstants.MaxInstances];
            FreeStack = new int[VMConstants.MaxFreeStack];
            ActiveList = new int[VMConstants.MaxInstances];
            ExtendedRegs = new Number[VMConstants.MaxInstances][];
            Bindings = new HostBindings[VMConstants.MaxInstances];
            FreeTop = VMConstants.MaxInstances;
            ActiveListCount = 0;

            // Fill free stack: 0 at top (first to be allocated)
            for (int i = 0; i < VMConstants.MaxInstances; i++)
            {
                FreeStack[i] = VMConstants.MaxInstances - 1 - i;
            }
        }

        /// <summary>
        /// Allocate a new instance. Returns instance ID or -1 if pool exhausted.
        /// </summary>
        public int Allocate(int moduleSlot, int entryIP)
        {
            if (FreeTop <= 0)
                return -1;

            FreeTop--;
            int slot = FreeStack[FreeTop];

            ref VMInstanceState inst = ref Instances[slot];
            // VOM4: bump generation BEFORE clearing struct so we capture old value;
            // then we re-apply incremented value after `inst = default`.
            int newGen = inst.Generation + 1;
            inst = default;
            inst.Generation = newGen;
            inst.InstanceId = slot;
            inst.ModuleSlot = moduleSlot;
            inst.IP = entryIP;
            inst.IsAlive = true;
            inst.StateFlags = VMStateFlags.Active;
            inst.WaitTargetInstanceId = -1;

            // Lang-1.1b: Clear extended registers (pre-allocation handled by SpawnInstance)
            ExtendedRegs[slot] = null;

            // O9: Append to active list (also updates ActiveCount via alias)
            inst.ActiveListIndex = ActiveListCount;
            ActiveList[ActiveListCount] = slot;
            ActiveListCount++;

            return slot;
        }

        /// <summary>
        /// Release an instance back to the pool.
        /// </summary>
        public void Free(int instanceId)
        {
            if (instanceId < 0 || instanceId >= VMConstants.MaxInstances)
                return;

            ref VMInstanceState inst = ref Instances[instanceId];
            if (!inst.IsAlive)
                return;

            // O9: Swap-remove from active list (also updates ActiveCount via alias)
            int idx = inst.ActiveListIndex;
            int last = ActiveListCount - 1;
            if (idx != last)
            {
                int movedId = ActiveList[last];
                ActiveList[idx] = movedId;
                Instances[movedId].ActiveListIndex = idx;
            }
            ActiveListCount--;
            inst.ActiveListIndex = -1;

            inst.IsAlive = false;
            inst.ErrorFlag = VMError.None;
            // VOM5: clear binding ref so dead slot doesn't pin GC roots
            Bindings[instanceId] = null;
            FreeStack[FreeTop] = instanceId;
            FreeTop++;
        }
    }
}
