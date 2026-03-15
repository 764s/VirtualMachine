namespace FFVM
{
    /// <summary>
    /// Top-level VM world: manages instance pool, snapshots, and per-frame tick.
    /// API: SaveState / LoadState / Tick — drives all VM instances deterministically.
    /// </summary>
    public class VMWorld
    {
        public InstancePool Pool;
        public SyscallTable Syscalls { get; }

        private readonly SnapshotRingBuffer _snapshots;
        private int _frameNumber;

        public int FrameNumber => _frameNumber;

        public VMWorld()
        {
            Pool.Init();
            Syscalls = new SyscallTable();
            _snapshots = new SnapshotRingBuffer();
            _frameNumber = 0;
        }

        /// <summary>
        /// Create a new VM instance running the given module at entryIP.
        /// Returns instance ID or -1 if pool exhausted.
        /// </summary>
        public int SpawnInstance(int moduleSlot, int entryIP)
        {
            return Pool.Allocate(moduleSlot, entryIP);
        }

        /// <summary>
        /// Destroy a VM instance.
        /// </summary>
        public void DestroyInstance(int instanceId)
        {
            Pool.Free(instanceId);
        }

        /// <summary>
        /// Save current state to snapshot ring buffer.
        /// </summary>
        public void SaveState()
        {
            _snapshots.SaveState(ref Pool, _frameNumber);
        }

        /// <summary>
        /// Rollback to a previous frame's state.
        /// </summary>
        public bool LoadState(int targetFrame)
        {
            if (_snapshots.LoadState(ref Pool, targetFrame))
            {
                _frameNumber = targetFrame;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Advance one frame. Ticks all alive instances:
        /// - Decrements wait counters
        /// - Checks wait_for targets
        /// - (Future: executes bytecode until yield/wait/completion)
        /// </summary>
        public void Tick()
        {
            _frameNumber++;

            for (int i = 0; i < VMConstants.MaxInstances; i++)
            {
                ref VMInstanceState inst = ref Pool.Instances[i];
                if (!inst.IsAlive || inst.ErrorFlag != VMError.None)
                    continue;

                // Handle wait counter
                if (inst.WaitCounter > 0)
                {
                    inst.WaitCounter--;
                    continue;
                }

                // Handle wait_for: check if target instance is still alive
                if (inst.WaitTargetInstanceId >= 0)
                {
                    ref VMInstanceState target = ref Pool.Instances[inst.WaitTargetInstanceId];
                    if (target.IsAlive)
                        continue; // Still waiting

                    inst.WaitTargetInstanceId = -1; // Target finished, resume
                }

                // TODO Phase 3.5+: Execute bytecode instructions here
                // For now this is a stub — the tree-walker is the Phase 2 executor
            }
        }
    }
}
