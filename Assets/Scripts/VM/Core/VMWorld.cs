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

        /// <summary>
        /// Optional script debugger. Null = no debugging (zero overhead).
        /// Set before Tick() to enable breakpoints, variable inspection, and call stack viewing.
        /// </summary>
        public ScriptDebugger Debugger;

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
        /// O9: Iterates only active instances via ActiveList instead of scanning all 128 slots.
        /// </summary>
        public void Tick()
        {
            _frameNumber++;

            // Reset debugger per-tick state (allows same-line breakpoints to re-trigger next tick)
            Debugger?.ResetTickState();

            for (int i = 0; i < Pool.ActiveListCount; i++)
            {
                int id = Pool.ActiveList[i];
                ref VMInstanceState inst = ref Pool.Instances[id];
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

        /// <summary>
        /// Resolve a logical register index to a physical register index.
        /// r0-r15 (scratch zone) are absolute; r16+ are offset by RegisterBase.
        /// </summary>
        private static int Reg(int r, int regBase)
        {
            return r < 16 ? r : r + regBase;
        }

        private unsafe void ExecuteInstance(ref VMInstanceState inst)
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

            // Cache debugger reference for the duration of this execution burst
            var dbg = Debugger;
            var srcMap = (dbg != null) ? program.SourceMap : null;

            // FF5: saved r0 for preserving return value across non-entry function cleanup
            Number savedR0 = default;

            // O1: Pin registers once for the entire execution burst.
            // Previously each Get/Set call did its own fixed pin/unpin — the single
            // largest per-instruction overhead in the dispatch loop.
            fixed (Number* regs = &inst.Registers.R00)
            {
                while (steps < MaxStepsPerTick)
                {
                    if (inst.IP < 0 || inst.IP >= code.Length)
                    {
                        inst.ErrorFlag = VMError.PanicOutOfBounds;
                        return;
                    }

                    // --- Breakpoint check (zero overhead when Debugger is null) ---
                    if (srcMap != null)
                    {
                        if (dbg.CheckBreakpoint(inst.InstanceId, inst.IP, srcMap) && dbg.HaltOnBreakpoint)
                        {
                            // DAP mode: halt BEFORE executing the instruction so the user
                            // sees the breakpoint line as the current line in stackTrace.
                            return;
                        }
                    }

                    ref Instruction op = ref code[inst.IP];
                    int rb = inst.RegisterBase;
                    steps++;

                    switch (op.Code)
                    {
                        case OpCode.NOP:
                            inst.IP++;
                            break;

                        case OpCode.LOAD_CONST:
                            regs[Reg(op.A, rb)] = consts[op.B];
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

                        case OpCode.WAIT_FOR:
                            inst.WaitTargetInstanceId = regs[Reg(op.A, rb)].ToInt();
                            inst.IP++;
                            return; // Yield to next tick — Tick() checks WaitTargetInstanceId

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
                                // FF5: determine cleanup boundary for current scope
                                int cleanupBase = 0;
                                if (inst.CallStackDepth > 0)
                                    cleanupBase = inst.CallStack.Get(inst.CallStackDepth - 1).CleanupBase;

                                // Finished one cleanup block
                                if (inst.CleanupDepth > cleanupBase)
                                {
                                    // More cleanup blocks to run in this scope (LIFO)
                                    inst.CleanupDepth--;
                                    inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                                }
                                else if (inst.CallStackDepth > 0)
                                {
                                    // FF5: all function-scoped cleanups done — return to caller
                                    inst.CallStackDepth--;
                                    var frame = inst.CallStack.Get(inst.CallStackDepth);
                                    inst.StateFlags &= ~VMStateFlags.InCleanup;
                                    inst.IP = frame.ReturnIP;
                                    inst.RegisterBase = frame.RegisterBase;
                                    // Restore return value that cleanup may have clobbered
                                    regs[0] = savedR0;
                                    // If Killed, stop — let next Tick handle parent-scope cleanup
                                    if ((inst.StateFlags & VMStateFlags.Killed) != 0)
                                        return;
                                }
                                else
                                {
                                    // All cleanups done (entry function)
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

                        // --- Phase 2: Data Movement ---

                        case OpCode.MOVE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)];
                            inst.IP++;
                            break;

                        // --- Phase 2: Control Flow ---

                        case OpCode.JUMP:
                            inst.IP = op.A;
                            break;

                        case OpCode.JUMP_IF_ZERO:
                            if (regs[Reg(op.B, rb)] == Number.Zero)
                                inst.IP = op.A;
                            else
                                inst.IP++;
                            break;

                        case OpCode.JUMP_IF_NOT_ZERO:
                            if (regs[Reg(op.B, rb)] != Number.Zero)
                                inst.IP = op.A;
                            else
                                inst.IP++;
                            break;

                        // --- Phase 2: Arithmetic ---

                        case OpCode.ADD:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] + regs[Reg(op.C, rb)];
                            inst.IP++;
                            break;

                        case OpCode.SUB:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] - regs[Reg(op.C, rb)];
                            inst.IP++;
                            break;

                        case OpCode.MUL:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] * regs[Reg(op.C, rb)];
                            inst.IP++;
                            break;

                        case OpCode.DIV:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] / regs[Reg(op.C, rb)];
                            inst.IP++;
                            break;

                        case OpCode.MOD:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] % regs[Reg(op.C, rb)];
                            inst.IP++;
                            break;

                        // --- Phase 2: Comparison ---

                        case OpCode.CMP_EQ:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] == regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.CMP_NEQ:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] != regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.CMP_LT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] < regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.CMP_LTE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] <= regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.CMP_GT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] > regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.CMP_GTE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] >= regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        // --- Phase 2: Boolean / Unary ---

                        case OpCode.AND:
                            regs[Reg(op.A, rb)] =
                                (regs[Reg(op.B, rb)] != Number.Zero && regs[Reg(op.C, rb)] != Number.Zero)
                                    ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.OR:
                            regs[Reg(op.A, rb)] =
                                (regs[Reg(op.B, rb)] != Number.Zero || regs[Reg(op.C, rb)] != Number.Zero)
                                    ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.NOT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] == Number.Zero ? Number.One : Number.Zero;
                            inst.IP++;
                            break;

                        case OpCode.NEG:
                            regs[Reg(op.A, rb)] = -regs[Reg(op.B, rb)];
                            inst.IP++;
                            break;

                        // --- Phase 3: Function Calls ---

                        case OpCode.CALL:
                        {
                            if (inst.CallStackDepth >= VMConstants.MaxCallDepth)
                            {
                                inst.ErrorFlag = VMError.PanicStackOverflow;
                                return;
                            }
                            var frame = new CallFrame
                            {
                                ReturnIP = inst.IP + 1,
                                ReturnModuleSlot = inst.ModuleSlot,
                                RegisterBase = inst.RegisterBase,
                                CleanupBase = inst.CleanupDepth
                            };
                            inst.CallStack.Set(inst.CallStackDepth, frame);
                            inst.CallStackDepth++;
                            inst.RegisterBase += op.B; // B = callerWindowSize
                            inst.IP = op.A;            // A = target function entry IP
                            break;                     // don't IP++ — already jumped
                        }

                        case OpCode.RET_FUNC:
                        {
                            // FF5: check for pending function-scoped cleanups before returning
                            var frame = inst.CallStack.Get(inst.CallStackDepth - 1);
                            if (inst.CleanupDepth > frame.CleanupBase)
                            {
                                // Function has pending cleanups — execute them before returning.
                                // Keep CallStackDepth unchanged so RETURN can pop frame when done.
                                // Save r0 (return value) before cleanup blocks may clobber it.
                                savedR0 = regs[0];
                                inst.StateFlags |= VMStateFlags.InCleanup;
                                inst.CleanupDepth--;
                                inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                            }
                            else
                            {
                                // No pending cleanups — normal return
                                inst.CallStackDepth--;
                                inst.IP = frame.ReturnIP;
                                inst.RegisterBase = frame.RegisterBase;
                            }
                            break;                     // don't IP++ — either jumped to cleanup or ReturnIP is CALL+1
                        }

                        // --- FO1: Leaf function calls (skip CallFrame push/pop) ---

                        case OpCode.CALL_LEAF:
                        {
                            if (dbg != null)
                            {
                                // Debugger attached: promote to full CALL for call stack visibility
                                if (inst.CallStackDepth >= VMConstants.MaxCallDepth)
                                {
                                    inst.ErrorFlag = VMError.PanicStackOverflow;
                                    return;
                                }
                                var frame = new CallFrame
                                {
                                    ReturnIP = inst.IP + 1,
                                    ReturnModuleSlot = inst.ModuleSlot,
                                    RegisterBase = inst.RegisterBase,
                                    CleanupBase = inst.CleanupDepth
                                };
                                inst.CallStack.Set(inst.CallStackDepth, frame);
                                inst.CallStackDepth++;
                            }
                            else
                            {
                                // Fast path: save return info to instance fields, skip CallFrame
                                inst.LeafReturnIP = inst.IP + 1;
                                inst.LeafRegisterBase = inst.RegisterBase;
                            }
                            inst.RegisterBase += op.B;
                            inst.IP = op.A;
                            break;
                        }

                        case OpCode.RET_LEAF:
                        {
                            if (dbg != null)
                            {
                                // Debugger attached: promoted call used full CallFrame
                                inst.CallStackDepth--;
                                var frame = inst.CallStack.Get(inst.CallStackDepth);
                                inst.IP = frame.ReturnIP;
                                inst.RegisterBase = frame.RegisterBase;
                            }
                            else
                            {
                                // Fast path: restore from instance fields
                                inst.IP = inst.LeafReturnIP;
                                inst.RegisterBase = inst.LeafRegisterBase;
                            }
                            break;
                        }

                        default:
                            inst.ErrorFlag = VMError.PanicIllegalInstruction;
                            return;
                    }
                }
            } // end fixed

            // Hit step limit — treat as runaway
            inst.ErrorFlag = VMError.PanicStepLimitExceeded;
        }
    }
}
