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
        public VMModuleTable Modules { get; }

        private readonly SnapshotRingBuffer _snapshots;
        private int _frameNumber;

        /// <summary>Max instructions executed per instance per Tick to prevent infinite loops.</summary>
        public int MaxStepsPerTick = 1024;

        public int FrameNumber => _frameNumber;

        public VMWorld()
        {
            Pool.Init();
            Syscalls = new SyscallTable();
            Modules = new VMModuleTable();
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
        /// Advance one frame. Ticks all alive instances through the bytecode interpreter.
        /// </summary>
        public void Tick()
        {
            _frameNumber++;

            for (int i = 0; i < VMConstants.MaxInstances; i++)
            {
                ref VMInstanceState inst = ref Pool.Instances[i];
                if (!inst.IsAlive || inst.ErrorFlag != VMError.None)
                    continue;

                // 1. Already completed → skip
                if ((inst.StateFlags & VMStateFlags.Completed) != 0)
                    continue;

                // 2. Killed but not yet in cleanup
                if ((inst.StateFlags & VMStateFlags.Killed) != 0 &&
                    (inst.StateFlags & VMStateFlags.InCleanup) == 0)
                {
                    if (inst.CleanupDepth > 0)
                    {
                        inst.StateFlags |= VMStateFlags.InCleanup;
                        inst.CleanupDepth--;
                        inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                    }
                    else
                    {
                        inst.StateFlags |= VMStateFlags.Completed;
                        continue;
                    }
                }

                // 3. Wait counter (only when not killed)
                if (inst.WaitCounter > 0 && (inst.StateFlags & VMStateFlags.Killed) == 0)
                {
                    inst.WaitCounter--;
                    continue;
                }

                // 4. Handle wait_for: check if target instance is still alive
                if (inst.WaitTargetInstanceId >= 0 && (inst.StateFlags & VMStateFlags.Killed) == 0)
                {
                    ref VMInstanceState target = ref Pool.Instances[inst.WaitTargetInstanceId];
                    if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
                        continue; // Still waiting

                    inst.WaitTargetInstanceId = -1; // Target finished, resume
                }

                ExecuteInstance(ref inst);
            }
        }

        private void ExecuteInstance(ref VMInstanceState inst)
        {
            VMProgram program = Modules.Get(inst.ModuleSlot);
            if (program == null)
            {
                inst.ErrorFlag = VMError.PanicModuleNotLoaded;
                return;
            }

            var code = program.Instructions;
            var consts = program.Constants;
            int steps = 0;

            while (steps < MaxStepsPerTick)
            {
                if (inst.IP < 0 || inst.IP >= code.Length)
                {
                    inst.ErrorFlag = VMError.PanicOutOfBounds;
                    return;
                }

                ref Instruction op = ref code[inst.IP];
                steps++;

                switch (op.Code)
                {
                    case OpCode.NOP:
                        inst.IP++;
                        break;

                    case OpCode.LOAD_CONST:
                        inst.Registers.Set(op.A, consts[op.B]);
                        inst.IP++;
                        break;

                    case OpCode.SYSCALL:
                        Syscalls.Invoke(op.A, ref inst);
                        if (inst.ErrorFlag != VMError.None) return;
                        inst.IP++;
                        break;

                    case OpCode.WAIT:
                        inst.WaitCounter = op.A;
                        inst.IP++;
                        return; // Yield to next tick

                    case OpCode.PUSH_CLEANUP:
                        if (inst.CleanupDepth >= VMConstants.MaxCleanupDepth)
                        {
                            inst.ErrorFlag = VMError.PanicStackOverflow;
                            return;
                        }
                        inst.CleanupStack.Set(inst.CleanupDepth, new CleanupFrame { CleanupEntryIP = op.A });
                        inst.CleanupDepth++;
                        inst.IP++;
                        break;

                    case OpCode.POP_CLEANUP:
                        if (inst.CleanupDepth > 0) inst.CleanupDepth--;
                        inst.IP++;
                        break;

                    case OpCode.RETURN:
                        if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                        {
                            // Finished one cleanup block
                            if (inst.CleanupDepth > 0)
                            {
                                // More cleanup blocks to run (LIFO)
                                inst.CleanupDepth--;
                                inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                            }
                            else
                            {
                                // All cleanups done
                                inst.StateFlags &= ~VMStateFlags.InCleanup;
                                inst.StateFlags |= VMStateFlags.Completed;
                                return;
                            }
                        }
                        else
                        {
                            // Normal return — enter cleanup if any
                            if (inst.CleanupDepth > 0)
                            {
                                inst.StateFlags |= VMStateFlags.InCleanup;
                                inst.CleanupDepth--;
                                inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                            }
                            else
                            {
                                inst.StateFlags |= VMStateFlags.Completed;
                                return;
                            }
                        }
                        break;

                    default:
                        inst.ErrorFlag = VMError.PanicIllegalInstruction;
                        return;
                }
            }

            // Hit step limit — treat as runaway
            inst.ErrorFlag = VMError.PanicIllegalInstruction;
        }
    }
}
