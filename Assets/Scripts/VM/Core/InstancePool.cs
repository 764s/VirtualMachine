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

            // [VOM-Tail D5] Regression guard: after VOM10 the [ModuleVarRegBase..MaxRegisters)
            // window of the register file MUST never be written by any opcode (module variables
            // moved to Data.MVars). Verify on every Free under DEBUG_VM. Zero overhead in Release.
            AssertModuleVarRegRangeIsZero(ref inst, "InstancePool.Free");

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

        /// <summary>
        /// [VOM-Tail D5] Single assertion entry: verifies the module-variable region of the
        /// register file (<c>[ModuleVarRegBase..MaxRegisters)</c>) was not touched during the
        /// instance's lifetime. After VOM10 all four MVar opcodes (LOAD_MVAR / STORE_MVAR /
        /// XLOAD_MVAR / XSTORE_MVAR) read/write <c>Data.MVars</c> exclusively; any non-zero
        /// value here indicates a regression. <see cref="System.Diagnostics.ConditionalAttribute"/>
        /// removes both call sites and method body in Release builds (no DEBUG_VM define).
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG_VM")]
        internal static unsafe void AssertModuleVarRegRangeIsZero(ref VMInstanceState inst, string callsite)
        {
            // Coexist with VOM11 A.4 DEBUG_VM_POISON: the poison sentinel
            // 0xDEADBEEFDEADBEEF is written by TransientInstancePool.Rent before any opcode
            // executes, so it represents "untouched", not a regression.
            const long PoisonSentinel = unchecked((long)0xDEADBEEFDEADBEEFUL);
            fixed (long* raw = inst.Cpu.Registers.Raw)
            {
                for (int i = VMConstants.ModuleVarRegBase; i < VMConstants.MaxRegisters; i++)
                {
                    long v = raw[i];
                    if (v != 0L && v != PoisonSentinel)
                    {
                        throw new System.InvalidOperationException(
                            $"D5 regression @ {callsite}: Registers.Raw[{i}] = 0x{v:X16} " +
                            $"(expected 0 or poison; ModuleVarRegBase={VMConstants.ModuleVarRegBase}). " +
                            "Some opcode wrote into the legacy module-variable register window. " +
                            "All MVar writes must target Data.MVars after VOM10.");
                    }
                }
            }
        }
    }
}
