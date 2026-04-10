using System.Runtime.CompilerServices;

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

        // Lang-6/7: Cross-instance call state
        private XCallFrame[] _xcallStack = new XCallFrame[4];
        private int _xcallDepth;

        /// <summary>Lang-7: Runtime configuration (XCALL depth policy, etc.).</summary>
        public VMConfig Config { get; }

        /// <summary>Lang-6: Callback invoked when XCALL nesting depth exceeds MaxXCallDepth (Warn policy only).</summary>
        public System.Action<int, int> OnXCallDepthWarning;

        /// <summary>Max instructions executed per instance per Tick to prevent infinite loops.</summary>
        public int MaxStepsPerTick = 1024;

        /// <summary>Max instructions per cleanup block. Exceeding skips the block but continues remaining cleanup.</summary>
        public int MaxCleanupSteps = 256;

        public int FrameNumber => _frameNumber;

        public VMWorld() : this(null) { }

        public VMWorld(VMConfig config)
        {
            Config = config ?? new VMConfig();
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
            int id = Pool.Allocate(moduleSlot, entryIP);
            if (id < 0) return id;

            // Lang-1.1b: Pre-allocate extended registers if the module's program requires them
            VMProgram program = Modules.Get(moduleSlot);
            if (program != null && program.RequiredExtendedRegisters > 0)
            {
                Pool.ExtendedRegs[id] = new Number[program.RequiredExtendedRegisters];
            }

            return id;
        }

        /// <summary>
        /// Destroy a VM instance.
        /// </summary>
        public void DestroyInstance(int instanceId)
        {
            Pool.Free(instanceId);
        }

        /// <summary>
        /// Execute a single instance for one tick (condition probe use-case).
        /// Does NOT advance the global frame number or affect other instances.
        /// Used by the host to probe a script's activation condition:
        /// spawn → TickInstance → check if completed (condition failed) or yielded (condition passed).
        /// </summary>
        public void TickInstance(int instanceId)
        {
            ref VMInstanceState inst = ref Pool.Instances[instanceId];
            if (!inst.IsAlive || inst.ErrorFlag != VMError.None)
                return;
            if ((inst.StateFlags & VMStateFlags.Completed) != 0)
                return;

            // Handle killed → cleanup path
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
                    return;
                }
            }

            // Wait counter
            if (inst.WaitCounter > 0 && (inst.StateFlags & VMStateFlags.Killed) == 0)
            {
                inst.WaitCounter--;
                return;
            }

            // Wait-for target
            if (inst.WaitTargetInstanceId >= 0 && (inst.StateFlags & VMStateFlags.Killed) == 0)
            {
                ref VMInstanceState target = ref Pool.Instances[inst.WaitTargetInstanceId];
                if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
                    return;
                inst.WaitTargetInstanceId = -1;
            }

            ExecuteInstance(ref inst);
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
        /// r0..ScratchZoneSize-1 (scratch zone) are absolute; rest are offset by RegisterBase.
        /// Module variables use dedicated LOAD_MVAR/STORE_MVAR opcodes (absolute addressing).
        /// </summary>
        private static int Reg(int r, int regBase)
        {
            return r < VMConstants.ScratchZoneSize ? r : r + regBase;
        }

#if !FFVM_LEGACY_CSHARP
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
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
            int maxSteps = MaxStepsPerTick; // O15: cache to local — avoid field read every iteration
            int cleanupSteps = 0;
            int maxCleanupSteps = MaxCleanupSteps; // C5: cache to local

            // Cache debugger reference for the duration of this execution burst
            var dbg = Debugger;
            var srcMap = (dbg != null) ? program.SourceMap : null;

            // DC: WasInCleanup flag packed into high bit of CallFrame.CleanupBase.
            // Allows InCleanup state to be saved/restored across nested cleanup calls.
            const int WAS_IN_CLEANUP = unchecked((int)0x80000000);
            const int CLEANUP_BASE_MASK = 0x7FFFFFFF;

            // Lang-1.1b: Cache extended register array reference (null when not used).
            // Heap-allocated per-instance, accessed only via LOAD_XREG/STORE_XREG.
            Number[] xregs = Pool.ExtendedRegs[inst.InstanceId];

            // O1: Pin registers once for the entire execution burst.
            // Previously each Get/Set call did its own fixed pin/unpin — the single
            // largest per-instruction overhead in the dispatch loop.
            // B-ε1: Pin code & consts arrays alongside registers — pointer arithmetic
            // skips CLR bounds check on every instruction fetch and constant load.
            fixed (long* rawRegs = inst.Registers.Raw)
            fixed (Instruction* codeBase = code)
            fixed (Number* constBase = consts)
            {
                Number* regs = (Number*)rawRegs;
                int extendedA = 0;  // O8: high byte accumulator for EXTEND_AX prefix
                while (steps < maxSteps)
                {
                    // O15: boundary check removed — SENTINEL opcode at end of Instructions
                    // triggers PanicOutOfBounds via its switch-case, replacing the per-instruction
                    // if (inst.IP < 0 || inst.IP >= code.Length) guard.

                    // --- Breakpoint check (zero overhead when Debugger is null) ---
                    if (srcMap != null)
                    {
                        if (dbg.CheckBreakpoint(inst.InstanceId, inst.IP, srcMap, inst.CallStackDepth) && dbg.HaltOnBreakpoint)
                        {
                            // DAP mode: halt BEFORE executing the instruction so the user
                            // sees the breakpoint line as the current line in stackTrace.
                            return;
                        }
                    }

                    ref Instruction op = ref codeBase[inst.IP];
                    int rb = inst.RegisterBase;

                    // O8: EXTEND_AX is a prefix — process atomically without counting as a step.
                    // extendedA merge is deferred to point-of-use in IP-reading cases only,
                    // so non-IP instructions (ADD, MUL, LOAD_CONST, ...) pay zero overhead.
                    if (op.Code == OpCode.EXTEND_AX)
                    {
                        extendedA = op.A << 8;
                        inst.IP++;
                        continue;
                    }

                    steps++;

                    // C5: Cleanup timeout protection — per-block step budget
                    if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                    {
                        cleanupSteps++;
                        if (cleanupSteps > maxCleanupSteps)
                        {
                            // Skip current cleanup block, advance to next
                            cleanupSteps = 0;
                            int cleanupBaseRaw = 0;
                            if (inst.CallStackDepth > 0)
                                cleanupBaseRaw = inst.CallStack.Get(inst.CallStackDepth - 1).CleanupBase;
                            int cleanupBase = cleanupBaseRaw & CLEANUP_BASE_MASK;

                            if (inst.CleanupDepth > cleanupBase)
                            {
                                inst.CleanupDepth--;
                                inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                            }
                            else if (inst.CallStackDepth > 0)
                            {
                                inst.CallStackDepth--;
                                var frame = inst.CallStack.Get(inst.CallStackDepth);
                                // DC: restore InCleanup from WasInCleanup flag
                                if ((frame.CleanupBase & WAS_IN_CLEANUP) != 0)
                                    inst.StateFlags |= VMStateFlags.InCleanup;
                                else
                                    inst.StateFlags &= ~VMStateFlags.InCleanup;
                                inst.IP = frame.ReturnIP;
                                inst.RegisterBase = frame.RegisterBase;
                                regs[0] = *(Number*)&frame.SavedR0;
                                if ((inst.StateFlags & VMStateFlags.Killed) != 0)
                                    return;
                            }
                            else
                            {
                                inst.StateFlags &= ~VMStateFlags.InCleanup;
                                inst.StateFlags |= VMStateFlags.Completed;
                                return;
                            }
                            continue;
                        }
                    }

                    switch (op.Code)
                    {
                        case OpCode.NOP:
                            inst.IP++;
                            break;

                        case OpCode.LOAD_CONST:
                            regs[Reg(op.A, rb)] = constBase[op.B];
                            inst.IP++;
                            break;

                        case OpCode.SYSCALL:
                            Syscalls.Invoke(op.A, ref inst);
                            if (inst.ErrorFlag != VMError.None) return;
                            inst.IP++;
                            break;

                        case OpCode.WAIT:
                            // DC: runtime guard — skip wait during cleanup execution.
                            // Functions called from cleanup blocks may contain WAIT but
                            // cleanup must complete synchronously within one Tick burst.
                            if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                            {
                                inst.IP++;
                                break; // treat as NOP
                            }
                            inst.WaitCounter = op.A;
                            inst.IP++;
                            return; // Yield to next tick

                        case OpCode.WAIT_FOR:
                            // DC: runtime guard — skip wait_for during cleanup execution.
                            if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                            {
                                inst.IP++;
                                break; // treat as NOP
                            }
                            inst.WaitTargetInstanceId = regs[Reg(op.A, rb)].ToInt();
                            inst.IP++;
                            return; // Yield to next tick — Tick() checks WaitTargetInstanceId

                        case OpCode.PUSH_CLEANUP:
                            if (inst.CleanupDepth >= VMConstants.MaxCleanupDepth)
                            {
                                inst.ErrorFlag = VMError.PanicStackOverflow;
                                return;
                            }
                            inst.CleanupStack.Set(inst.CleanupDepth, new CleanupFrame { CleanupEntryIP = op.A | extendedA });
                            extendedA = 0;
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
                                int cleanupBaseRaw2 = 0;
                                if (inst.CallStackDepth > 0)
                                    cleanupBaseRaw2 = inst.CallStack.Get(inst.CallStackDepth - 1).CleanupBase;
                                int cleanupBase2 = cleanupBaseRaw2 & CLEANUP_BASE_MASK;

                                // Finished one cleanup block
                                if (inst.CleanupDepth > cleanupBase2)
                                {
                                    // More cleanup blocks to run in this scope (LIFO)
                                    inst.CleanupDepth--;
                                    inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                                    cleanupSteps = 0; // C5: reset per-block budget
                                }
                                else if (inst.CallStackDepth > 0)
                                {
                                    // FF5: all function-scoped cleanups done — return to caller
                                    inst.CallStackDepth--;
                                    var frame = inst.CallStack.Get(inst.CallStackDepth);
                                    // DC: restore InCleanup from WasInCleanup flag
                                    if ((frame.CleanupBase & WAS_IN_CLEANUP) != 0)
                                        inst.StateFlags |= VMStateFlags.InCleanup;
                                    else
                                        inst.StateFlags &= ~VMStateFlags.InCleanup;
                                    inst.IP = frame.ReturnIP;
                                    inst.RegisterBase = frame.RegisterBase;
                                    // Restore return value that cleanup may have clobbered
                                    regs[0] = *(Number*)&frame.SavedR0;
                                    cleanupSteps = 0; // C5: reset for potential parent-scope cleanup
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
                                    cleanupSteps = 0; // C5: reset per-block budget
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

                        // --- SO1: Struct block copy ---
                        case OpCode.COPY_BLOCK:
                        {
                            int dst = Reg(op.A, rb);
                            int src = Reg(op.B, rb);
                            int count = op.C;
                            for (int ci = 0; ci < count; ci++)
                                regs[dst + ci] = regs[src + ci];
                            inst.IP++;
                            break;
                        }

                        // --- Phase 2: Control Flow ---

                        case OpCode.JUMP:
                            inst.IP = op.A | extendedA;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_ZERO:
                            if (regs[Reg(op.B, rb)] == Number.Zero)
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NOT_ZERO:
                            if (regs[Reg(op.B, rb)] != Number.Zero)
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
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
                            // DC: pack WasInCleanup flag into high bit of CleanupBase
                            int cbValue = inst.CleanupDepth;
                            if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                                cbValue |= WAS_IN_CLEANUP;
                            var frame = new CallFrame
                            {
                                ReturnIP = inst.IP + 1,
                                ReturnModuleSlot = inst.ModuleSlot,
                                RegisterBase = inst.RegisterBase,
                                CleanupBase = cbValue
                            };
                            inst.CallStack.Set(inst.CallStackDepth, frame);
                            inst.CallStackDepth++;
                            inst.RegisterBase += op.B; // B = callerWindowSize
                            inst.IP = op.A | extendedA;            // A = target function entry IP
                            extendedA = 0;
                            break;                     // don't IP++ — already jumped
                        }

                        case OpCode.RET_FUNC:
                        {
                            // FF5: check for pending function-scoped cleanups before returning
                            var frame = inst.CallStack.Get(inst.CallStackDepth - 1);
                            if (inst.CleanupDepth > (frame.CleanupBase & CLEANUP_BASE_MASK))
                            {
                                // Function has pending cleanups — execute them before returning.
                                // Keep CallStackDepth unchanged so RETURN can pop frame when done.
                                // DC: save r0 per-frame (supports nested cleanup from defer-called functions).
                                frame.SavedR0 = ((long*)regs)[0];
                                inst.CallStack.Set(inst.CallStackDepth - 1, frame);
                                inst.StateFlags |= VMStateFlags.InCleanup;
                                inst.CleanupDepth--;
                                inst.IP = inst.CleanupStack.Get(inst.CleanupDepth).CleanupEntryIP;
                                cleanupSteps = 0; // C5: reset per-block budget
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
                                // DC: pack WasInCleanup flag (same as CALL)
                                int cbValueLeaf = inst.CleanupDepth;
                                if ((inst.StateFlags & VMStateFlags.InCleanup) != 0)
                                    cbValueLeaf |= WAS_IN_CLEANUP;
                                var frame = new CallFrame
                                {
                                    ReturnIP = inst.IP + 1,
                                    ReturnModuleSlot = inst.ModuleSlot,
                                    RegisterBase = inst.RegisterBase,
                                    CleanupBase = cbValueLeaf
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
                            inst.IP = op.A | extendedA;
                            extendedA = 0;
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

                        // --- P5: fused compare-and-branch (B-ε2) ---

                        case OpCode.JUMP_IF_EQ:
                            if (regs[Reg(op.B, rb)] == regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NEQ:
                            if (regs[Reg(op.B, rb)] != regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LT:
                            if (regs[Reg(op.B, rb)] < regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LTE:
                            if (regs[Reg(op.B, rb)] <= regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GT:
                            if (regs[Reg(op.B, rb)] > regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GTE:
                            if (regs[Reg(op.B, rb)] >= regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        // --- B-ε4: FORLOOP super-instruction ---

                        case OpCode.FORLOOP:
                        {
                            int cr = Reg(op.B, rb);
                            regs[cr] = regs[cr] + Number.One;
                            if (regs[cr] < regs[Reg(op.C, rb)])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;
                        }

                        // --- B-ζ2: fused constant-compare-and-branch ---

                        case OpCode.JUMP_IF_EQ_K:
                            if (regs[Reg(op.B, rb)] == constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NEQ_K:
                            if (regs[Reg(op.B, rb)] != constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LT_K:
                            if (regs[Reg(op.B, rb)] < constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LTE_K:
                            if (regs[Reg(op.B, rb)] <= constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GT_K:
                            if (regs[Reg(op.B, rb)] > constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GTE_K:
                            if (regs[Reg(op.B, rb)] >= constBase[op.C])
                                inst.IP = op.A | extendedA;
                            else
                                inst.IP++;
                            extendedA = 0;
                            break;

                        // --- B-ζ3: SWITCH jump table dispatch ---

                        case OpCode.SWITCH:
                        {
                            int val = regs[Reg(op.B, rb)].ToInt();
                            int[] table = program.JumpTables[op.C];
                            if (val >= 0 && val < table.Length)
                                inst.IP = table[val];
                            else
                                inst.IP = op.A | extendedA; // default
                            extendedA = 0;
                            break;
                        }

                        // --- Lang-1: module variable access (absolute addressing) ---

                        case OpCode.LOAD_MVAR:
                            regs[Reg(op.A, rb)] = regs[VMConstants.ModuleVarRegBase + op.B];
                            inst.IP++;
                            break;

                        case OpCode.STORE_MVAR:
                            regs[VMConstants.ModuleVarRegBase + op.A] = regs[Reg(op.B, rb)];
                            inst.IP++;
                            break;

                        // --- Lang-1.1b: extended register access (heap-allocated) ---

                        case OpCode.LOAD_XREG:
                            if (xregs == null) { inst.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                            regs[Reg(op.A, rb)] = xregs[op.B | (op.C << 8)];
                            inst.IP++;
                            break;

                        case OpCode.STORE_XREG:
                            if (xregs == null) { inst.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                            xregs[op.A | (op.C << 8)] = regs[Reg(op.B, rb)];
                            inst.IP++;
                            break;

                        // --- Lang-6: cross-instance member access (XIMA) ---

                        case OpCode.XCALL:
                        {
                            // A=destReg, B=instanceId_reg, C=exportFuncIndex
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int funcIdx = op.C;
                            if (funcIdx < 0 || funcIdx >= targetProgram.ExportTable.Functions.Length)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            var exportFunc = targetProgram.ExportTable.Functions[funcIdx];
                            if (exportFunc.FuncTableIndex < 0 || exportFunc.FuncTableIndex >= targetProgram.Functions.Length)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }
                            var targetFunc = targetProgram.Functions[exportFunc.FuncTableIndex];

                            // Nested depth check (Lang-7: uses VMConfig)
                            _xcallDepth++;
                            if (Config.XCallPolicy == XCallDepthPolicy.Warn && _xcallDepth > Config.MaxXCallDepth)
                            {
                                OnXCallDepthWarning?.Invoke(_xcallDepth, Config.MaxXCallDepth);
                            }

                            // Grow stack if needed (Warn mode allows exceeding)
                            if (_xcallDepth > _xcallStack.Length)
                            {
                                var newStack = new XCallFrame[_xcallStack.Length * 2];
                                System.Array.Copy(_xcallStack, newStack, _xcallStack.Length);
                                _xcallStack = newStack;
                            }

                            // Save caller state
                            int destReg = Reg(op.A, rb);
                            _xcallStack[_xcallDepth - 1] = new XCallFrame
                            {
                                CallerInstanceId = inst.InstanceId,
                                CallerIP = inst.IP + 1,
                                CallerModuleSlot = inst.ModuleSlot,
                                CallerRegisterBase = inst.RegisterBase,
                                CallerCallStackDepth = inst.CallStackDepth,
                                CallerCleanupDepth = inst.CleanupDepth,
                                DestReg = destReg
                            };

                            // Copy parameters: caller scratch r0..r(paramCount-1) → target scratch r0..r(paramCount-1)
                            int paramCount = exportFunc.ParamCount;
                            fixed (long* targetRaw = targetInst.Registers.Raw)
                            {
                                Number* targetRegs = (Number*)targetRaw;
                                for (int p = 0; p < paramCount; p++)
                                    targetRegs[p] = regs[p];
                            }

                            // Execute target function synchronously (recursive)
                            targetInst.IP = targetFunc.EntryIP;
                            targetInst.RegisterBase = 0;
                            targetInst.CallStackDepth = 0;
                            targetInst.CleanupDepth = 0;

                            ExecuteInstance(ref targetInst);

                            // Restore caller — re-pin caller registers (may have moved if same instance)
                            // Read return value from target r0 BEFORE restoring caller
                            Number returnVal;
                            fixed (long* targetRaw2 = Pool.Instances[targetId].Registers.Raw)
                            {
                                returnVal = ((Number*)targetRaw2)[0];
                            }

                            // Restore caller state
                            var xcFrame = _xcallStack[_xcallDepth - 1];
                            _xcallDepth--;

                            // Check for errors in the target instance
                            if (Pool.Instances[targetId].ErrorFlag != VMError.None)
                            {
                                inst.ErrorFlag = Pool.Instances[targetId].ErrorFlag;
                                return;
                            }

                            // Write return value to caller's dest register
                            // Need to re-reference regs since we're back in caller context
                            regs[xcFrame.DestReg] = returnVal;
                            inst.IP = xcFrame.CallerIP;
                            inst.RegisterBase = xcFrame.CallerRegisterBase;
                            inst.CallStackDepth = xcFrame.CallerCallStackDepth;
                            inst.CleanupDepth = (byte)xcFrame.CallerCleanupDepth;
                            rb = inst.RegisterBase;
                            break;
                        }

                        case OpCode.XLOAD_MVAR:
                        {
                            // A=destReg, B=instanceId_reg, C=exportVarIndex
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int varIdx = op.C;
                            if (varIdx < 0 || varIdx >= targetProgram.ExportTable.Variables.Length)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int mvarSlot = targetProgram.ExportTable.Variables[varIdx].MvarSlot;

                            if (mvarSlot < VMConstants.ModuleVarSlots)
                            {
                                // Fixed register path
                                fixed (long* targetRaw = targetInst.Registers.Raw)
                                {
                                    Number* targetRegs = (Number*)targetRaw;
                                    regs[Reg(op.A, rb)] = targetRegs[VMConstants.ModuleVarRegBase + mvarSlot];
                                }
                            }
                            else
                            {
                                // Extended register path
                                Number[] txregs = Pool.ExtendedRegs[targetId];
                                if (txregs == null) { inst.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                                regs[Reg(op.A, rb)] = txregs[mvarSlot - VMConstants.ModuleVarSlots];
                            }
                            inst.IP++;
                            break;
                        }

                        case OpCode.XSTORE_MVAR:
                        {
                            // A=exportVarIndex, B=instanceId_reg, C=srcReg
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                inst.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int varIdx = op.A;
                            if (varIdx < 0 || varIdx >= targetProgram.ExportTable.Variables.Length)
                            {
                                inst.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            // Lang-12: reject writes to read-only exported consts
                            ref var exportVar = ref targetProgram.ExportTable.Variables[varIdx];
                            if (!exportVar.Writable)
                            {
                                inst.ErrorFlag = VMError.PanicIllegalInstruction;
                                return;
                            }

                            int mvarSlot = exportVar.MvarSlot;

                            if (mvarSlot < VMConstants.ModuleVarSlots)
                            {
                                // Fixed register path
                                fixed (long* targetRaw = targetInst.Registers.Raw)
                                {
                                    Number* targetRegs = (Number*)targetRaw;
                                    targetRegs[VMConstants.ModuleVarRegBase + mvarSlot] = regs[Reg(op.C, rb)];
                                }
                            }
                            else
                            {
                                // Extended register path
                                Number[] txregs = Pool.ExtendedRegs[targetId];
                                if (txregs == null) { inst.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                                txregs[mvarSlot - VMConstants.ModuleVarSlots] = regs[Reg(op.C, rb)];
                            }
                            inst.IP++;
                            break;
                        }

                        // --- O15: sentinel (end-of-program guard) ---

                        case OpCode.SENTINEL:
                            inst.ErrorFlag = VMError.PanicOutOfBounds;
                            return;

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
