namespace FFVM
{
    /// <summary>
    /// Instance pool manages pre-allocated VM instances.
    /// Zero-allocation: creation = take free slot, destruction = return to free stack.
    /// Free stack order must be deterministic for rollback consistency.
    /// </summary>
    public struct InstancePool
    {
        public VMInstanceState[] Instances;

        // Free stack for deterministic allocation order
        public int[] FreeStack;
        public int FreeTop;

        // Tracking
        public int ActiveCount;

        public void Init()
        {
            Instances = new VMInstanceState[VMConstants.MaxInstances];
            FreeStack = new int[VMConstants.MaxFreeStack];
            FreeTop = VMConstants.MaxInstances;
            ActiveCount = 0;

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
            ActiveCount++;

            ref VMInstanceState inst = ref Instances[slot];
            inst = default;
            inst.InstanceId = slot;
            inst.ModuleSlot = moduleSlot;
            inst.IP = entryIP;
            inst.IsAlive = true;
            inst.StateFlags = VMStateFlags.Active;
            inst.WaitTargetInstanceId = -1;

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

            inst.IsAlive = false;
            inst.ErrorFlag = VMError.None;
            FreeStack[FreeTop] = instanceId;
            FreeTop++;
            ActiveCount--;
        }
    }
}
