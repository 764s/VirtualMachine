using System.Runtime.CompilerServices;

namespace FFVM
{
    /// <summary>
    /// Top-level VM world: manages instance pool, snapshots, and per-frame tick.
    /// API: SaveState / LoadState / Tick 鈥?drives all VM instances deterministically.
    /// </summary>
    public class VMWorld
    {
        public InstancePool Pool;

        /// <summary>
        /// VOM5: world-scoped default bindings. Used when an instance is spawned
        /// without an explicit <see cref="HostBindings"/>, and as the fallback for
        /// transient instances (InvokeOnTransient: <c>InstanceId &lt; 0</c>).
        /// </summary>
        public HostBindings DefaultBindings { get; }

        /// <summary>
        /// Backwards-compatible passthrough to <c>DefaultBindings.Syscalls</c>.
        /// All existing <c>world.Syscalls.Register / Replace</c> call sites keep
        /// working unchanged. New code should set per-instance bindings via the
        /// <see cref="SpawnInstance(int, int, HostBindings)"/> overload.
        /// </summary>
        public SyscallTable Syscalls => DefaultBindings.Syscalls;
        public VMModuleTable Modules { get; }

        /// <summary>VOM3 Phase1: small pool reserved for synchronous host calls. Not in ActiveList, not snapshot-tracked.</summary>
        internal TransientInstancePool TransientPool { get; } = new TransientInstancePool();

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
            DefaultBindings = HostBindings.CreateDefault();
            Modules = new VMModuleTable();
            _snapshots = new SnapshotRingBuffer();
            _frameNumber = 0;
        }

        /// <summary>
        /// Create a new VM instance running the given module at entryIP.
        /// Returns instance ID or -1 if pool exhausted.
        /// </summary>
        /// <remarks>
        /// VOM5: legacy int-form. New instance is bound to <see cref="DefaultBindings"/>.
        /// Prefer the <see cref="SpawnInstance(int, int, HostBindings)"/> overload
        /// returning <see cref="VMInstance"/> for new code.
        /// </remarks>
        public int SpawnInstance(int moduleSlot, int entryIP)
        {
            int id = Pool.Allocate(moduleSlot, entryIP);
            if (id < 0) return id;

            // VOM5: bind to default for legacy int-form callers.
            Pool.Bindings[id] = DefaultBindings;

            // Lang-1.1b: Pre-allocate extended registers if the module's program requires them
            VMProgram program = Modules.Get(moduleSlot);
            if (program != null && program.RequiredExtendedRegisters > 0)
            {
                Pool.ExtendedRegs[id] = new Number[program.RequiredExtendedRegisters];
            }

            return id;
        }

        /// <summary>
        /// VOM5: spawn a new VM instance bound to the given <see cref="HostBindings"/>
        /// (or <see cref="DefaultBindings"/> when null), returning a <see cref="VMInstance"/>
        /// fa莽ade with built-in stale-handle (Generation) detection.
        /// </summary>
        public VMInstance SpawnInstance(int moduleSlot, int entryIP, HostBindings bindings)
        {
            int id = Pool.Allocate(moduleSlot, entryIP);
            if (id < 0) return VMInstance.Invalid;

            Pool.Bindings[id] = bindings ?? DefaultBindings;

            VMProgram program = Modules.Get(moduleSlot);
            if (program != null && program.RequiredExtendedRegisters > 0)
            {
                Pool.ExtendedRegs[id] = new Number[program.RequiredExtendedRegisters];
            }

            return new VMInstance(this, id, Pool.Instances[id].Generation);
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
        /// spawn 鈫?TickInstance 鈫?check if completed (condition failed) or yielded (condition passed).
        /// </summary>
        public void TickInstance(int instanceId)
        {
            ref VMInstanceState inst = ref Pool.Instances[instanceId];
            // [VOM8a] Localize sub-struct refs so hot accesses bypass ref-property indirection.
            ref CPUData cpu = ref inst.Cpu;
            ref VMData vmd = ref inst.Data;
            if (!vmd.IsAlive || cpu.ErrorFlag != VMError.None)
                return;
            if ((cpu.StateFlags & VMStateFlags.Completed) != 0)
                return;

            // Handle killed 鈫?cleanup path
            if ((cpu.StateFlags & VMStateFlags.Killed) != 0 &&
                (cpu.StateFlags & VMStateFlags.InCleanup) == 0)
            {
                if (cpu.CleanupDepth > 0)
                {
                    cpu.StateFlags |= VMStateFlags.InCleanup;
                    cpu.CleanupDepth--;
                    cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                    ExecuteCleanupInstance(ref cpu, ref vmd);
                    return;
                }
                else
                {
                    cpu.StateFlags |= VMStateFlags.Completed;
                    return;
                }
            }

            // Wait counter
            if (vmd.WaitCounter > 0 && (cpu.StateFlags & VMStateFlags.Killed) == 0)
            {
                vmd.WaitCounter--;
                return;
            }

            // Wait-for target
            if (vmd.WaitTargetInstanceId >= 0 && (cpu.StateFlags & VMStateFlags.Killed) == 0)
            {
                ref VMInstanceState target = ref Pool.Instances[vmd.WaitTargetInstanceId];
                if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
                    return;
                vmd.WaitTargetInstanceId = -1;
            }

            ExecuteInstance(ref cpu, ref vmd);
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
                // [VOM8a] Localize sub-struct refs (loop-local, recreated each iteration).
                ref CPUData cpu = ref inst.Cpu;
                ref VMData vmd = ref inst.Data;
                if (!vmd.IsAlive || cpu.ErrorFlag != VMError.None)
                    continue;

                // 1. Already completed 鈫?skip
                if ((cpu.StateFlags & VMStateFlags.Completed) != 0)
                    continue;

                // 2. Killed but not yet in cleanup
                if ((cpu.StateFlags & VMStateFlags.Killed) != 0 &&
                    (cpu.StateFlags & VMStateFlags.InCleanup) == 0)
                {
                    if (cpu.CleanupDepth > 0)
                    {
                        cpu.StateFlags |= VMStateFlags.InCleanup;
                        cpu.CleanupDepth--;
                        cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                        ExecuteCleanupInstance(ref cpu, ref vmd);
                        continue;
                    }
                    else
                    {
                        cpu.StateFlags |= VMStateFlags.Completed;
                        continue;
                    }
                }

                // 3. Wait counter (only when not killed)
                if (vmd.WaitCounter > 0 && (cpu.StateFlags & VMStateFlags.Killed) == 0)
                {
                    vmd.WaitCounter--;
                    continue;
                }

                // 4. Handle wait_for: check if target instance is still alive
                if (vmd.WaitTargetInstanceId >= 0 && (cpu.StateFlags & VMStateFlags.Killed) == 0)
                {
                    ref VMInstanceState target = ref Pool.Instances[vmd.WaitTargetInstanceId];
                    if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
                        continue; // Still waiting

                    vmd.WaitTargetInstanceId = -1; // Target finished, resume
                }

                ExecuteInstance(ref cpu, ref vmd);
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

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void ExecuteCleanupInstance(ref CPUData cpu, ref VMData vmd)
        {
            while ((cpu.StateFlags & VMStateFlags.InCleanup) != 0
                && cpu.ErrorFlag == VMError.None
                && (cpu.StateFlags & VMStateFlags.Completed) == 0)
            {
                int savedMaxSteps = MaxStepsPerTick;
                MaxStepsPerTick = MaxCleanupSteps > 0 ? MaxCleanupSteps : 1;
                try
                {
                    ExecuteInstance(ref cpu, ref vmd);
                }
                finally
                {
                    MaxStepsPerTick = savedMaxSteps;
                }

                if (cpu.ErrorFlag == VMError.PanicStepLimitExceeded
                    && (cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                {
                    cpu.ErrorFlag = VMError.None;
                    SkipTimedOutCleanup(ref cpu);
                }
            }
        }

        private static void SkipTimedOutCleanup(ref CPUData cpu)
        {
            const int WAS_IN_CLEANUP = unchecked((int)0x80000000);
            const int CLEANUP_BASE_MASK = 0x7FFFFFFF;

            int cleanupBaseRaw = 0;
            if (cpu.CallStackDepth > 0)
                cleanupBaseRaw = cpu.CallStack.Get(cpu.CallStackDepth - 1).CleanupBase;
            int cleanupBase = cleanupBaseRaw & CLEANUP_BASE_MASK;

            if (cpu.CleanupDepth > cleanupBase)
            {
                cpu.CleanupDepth--;
                cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                return;
            }

            if (cpu.CallStackDepth > 0)
            {
                cpu.CallStackDepth--;
                var frame = cpu.CallStack.Get(cpu.CallStackDepth);
                if ((frame.CleanupBase & WAS_IN_CLEANUP) != 0)
                    cpu.StateFlags |= VMStateFlags.InCleanup;
                else
                    cpu.StateFlags &= ~VMStateFlags.InCleanup;
                cpu.IP = frame.ReturnIP;
                cpu.RegisterBase = frame.RegisterBase;
                cpu.Registers.Set(0, new Number(frame.SavedR0));
                if (cpu.IP < 0)
                    cpu.StateFlags |= VMStateFlags.Completed;
                return;
            }

            cpu.StateFlags &= ~VMStateFlags.InCleanup;
            cpu.StateFlags |= VMStateFlags.Completed;
        }

#if NET5_0_OR_GREATER && !FFVM_LEGACY_CSHARP
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
#endif
        internal unsafe void ExecuteInstance(ref CPUData cpu, ref VMData vmd)
        {
            // [VOM9 Phase 2] Signature switched from (ref VMInstanceState) to dual
            // (ref CPUData, ref VMData). Pool storage is still single-array
            // VMInstanceState[] in this phase; SoA split happens in Phase 3.
            // SYSCALL ABI is still (ref VMInstanceState) until Phase 4.
            VMProgram program = Modules.Get(vmd.ModuleSlot);
            if (program == null)
            {
                cpu.ErrorFlag = VMError.PanicModuleNotLoaded;
                return;
            }

            var code = program.Instructions;
            var consts = program.Constants;
            int steps = 0;
            int maxSteps = MaxStepsPerTick; // O15: cache to local 鈥?avoid field read every iteration

            // Cache debugger reference for the duration of this execution burst
            var dbg = Debugger;
            var srcMap = (dbg != null) ? program.SourceMap : null;

            // DC: WasInCleanup flag packed into high bit of CallFrame.CleanupBase.
            // Allows InCleanup state to be saved/restored across nested cleanup calls.
            const int WAS_IN_CLEANUP = unchecked((int)0x80000000);
            const int CLEANUP_BASE_MASK = 0x7FFFFFFF;

            // Lang-1.1b: Cache extended register array reference (null when not used).
            // Heap-allocated per-instance, accessed only via LOAD_XREG/STORE_XREG.
            // VOM3 Phase1: transient instances (InstanceId < 0) never use extended registers;
            // attempting LOAD_XREG / STORE_XREG with xregs == null trips PanicIllegalInstruction.
            Number[] xregs = vmd.InstanceId >= 0 ? Pool.ExtendedRegs[vmd.InstanceId] : null;

            // O1: Pin registers once for the entire execution burst.
            // Previously each Get/Set call did its own fixed pin/unpin 鈥?the single
            // largest per-instruction overhead in the dispatch loop.
            // B-蔚1: Pin code & consts arrays alongside registers 鈥?pointer arithmetic
            // skips CLR bounds check on every instruction fetch and constant load.
            fixed (long* rawRegs = cpu.Registers.Raw)
            fixed (Instruction* codeBase = code)
            fixed (Number* constBase = consts)
            {
                Number* regs = (Number*)rawRegs;
                int extendedA = 0;  // O8: high byte accumulator for EXTEND_AX prefix
                while (steps < maxSteps)
                {
                    // O15: boundary check removed 鈥?SENTINEL opcode at end of Instructions
                    // triggers PanicOutOfBounds via its switch-case, replacing the per-instruction
                    // if (cpu.IP < 0 || cpu.IP >= code.Length) guard.

                    // --- Breakpoint check (zero overhead when Debugger is null) ---
                    if (srcMap != null)
                    {
                        if (dbg.CheckBreakpoint(vmd.InstanceId, cpu.IP, srcMap, cpu.CallStackDepth, vmd.ModuleSlot) && dbg.HaltOnBreakpoint)
                        {
                            // DAP mode: halt BEFORE executing the instruction so the user
                            // sees the breakpoint line as the current line in stackTrace.
                            return;
                        }
                    }

                    ref Instruction op = ref codeBase[cpu.IP];
                    int rb = cpu.RegisterBase;

                    // O8: EXTEND_AX is a prefix 鈥?process atomically without counting as a step.
                    // extendedA merge is deferred to point-of-use in IP-reading cases only,
                    // so non-IP instructions (ADD, MUL, LOAD_CONST, ...) pay zero overhead.
                    if (op.Code == OpCode.EXTEND_AX)
                    {
                        extendedA = op.A << 8;
                        cpu.IP++;
                        continue;
                    }

                    steps++;

                    switch (op.Code)
                    {
                        case OpCode.NOP:
                            cpu.IP++;
                            break;

                        case OpCode.LOAD_CONST:
                            regs[Reg(op.A, rb)] = constBase[op.B];
                            cpu.IP++;
                            break;

                        // CFG1: wide constant pool access 鈥?16-bit constant index (B | C<<8).
                        // Zero overhead for modules with 鈮?56 constants (compiler emits LOAD_CONST).
                        // Only emitted when constant index 鈮?56.
                        case OpCode.LOAD_CONST_W:
                            regs[Reg(op.A, rb)] = constBase[op.B | (op.C << 8)];
                            cpu.IP++;
                            break;

                        case OpCode.SYSCALL:
                        {
                            // VOM5: per-instance binding lookup. Transient instances
                            // (InvokeOnTransient: InstanceId < 0) fall back to DefaultBindings.
                            var st = (vmd.InstanceId >= 0)
                                ? Pool.Bindings[vmd.InstanceId].Syscalls
                                : DefaultBindings.Syscalls;
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0 && !st.IsReadOnly(op.A))
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            // [VOM9 Phase 2] SYSCALL ABI is still (ref VMInstanceState).
                            // VMInstanceState is [StructLayout(Sequential)] with Cpu as the
                            // first field, and cpu/vmd here are refs into the same pool slot
                            // (Phase 2 keeps single-array Pool.Instances[]). Recover the
                            // wrapper ref via Unsafe.As on cpu — safe transition shim that
                            // disappears in Phase 4 when SyscallHandler takes VMInstanceView.
                            ref VMInstanceState inst = ref System.Runtime.CompilerServices.Unsafe.As<CPUData, VMInstanceState>(ref cpu);
                            cpu.StateFlags |= VMStateFlags.HostExecuting;
                            try
                            {
                                st.Invoke(op.A, ref inst);
                            }
                            finally
                            {
                                cpu.StateFlags &= ~VMStateFlags.HostExecuting;
                            }
                            if (cpu.ErrorFlag != VMError.None) return;
                            cpu.IP++;
                            break;
                        }

                        case OpCode.WAIT:
                            // VOM3 Phase2: yield not permitted inside ReadOnlyCall.
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            // DC: runtime guard 鈥?skip wait during cleanup execution.
                            // Functions called from cleanup blocks may contain WAIT but
                            // cleanup must complete synchronously within one Tick burst.
                            if ((cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                            {
                                cpu.IP++;
                                break; // treat as NOP
                            }
                            vmd.WaitCounter = op.A;
                            cpu.IP++;
                            return; // Yield to next tick

                        case OpCode.WAIT_FOR:
                            // VOM3 Phase2: yield not permitted inside ReadOnlyCall.
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            // DC: runtime guard 鈥?skip wait_for during cleanup execution.
                            if ((cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                            {
                                cpu.IP++;
                                break; // treat as NOP
                            }
                            vmd.WaitTargetInstanceId = regs[Reg(op.A, rb)].ToInt();
                            cpu.IP++;
                            return; // Yield to next tick 鈥?Tick() checks WaitTargetInstanceId

                        case OpCode.PUSH_CLEANUP:
                            if (cpu.CleanupDepth >= VMConstants.MaxCleanupDepth)
                            {
                                cpu.ErrorFlag = VMError.PanicStackOverflow;
                                return;
                            }
                            cpu.CleanupStack.Set(cpu.CleanupDepth, new CleanupFrame { CleanupEntryIP = op.A | extendedA });
                            extendedA = 0;
                            cpu.CleanupDepth++;
                            cpu.IP++;
                            break;

                        case OpCode.POP_CLEANUP:
                            if (cpu.CleanupDepth > 0) cpu.CleanupDepth--;
                            cpu.IP++;
                            break;

                        case OpCode.RETURN:
                            if ((cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                            {
                                // FF5: determine cleanup boundary for current scope
                                int cleanupBaseRaw2 = 0;
                                if (cpu.CallStackDepth > 0)
                                    cleanupBaseRaw2 = cpu.CallStack.Get(cpu.CallStackDepth - 1).CleanupBase;
                                int cleanupBase2 = cleanupBaseRaw2 & CLEANUP_BASE_MASK;

                                // Finished one cleanup block
                                if (cpu.CleanupDepth > cleanupBase2)
                                {
                                    // More cleanup blocks to run in this scope (LIFO)
                                    cpu.CleanupDepth--;
                                    cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                                    return;
                                }
                                else if (cpu.CallStackDepth > 0)
                                {
                                    // FF5: all function-scoped cleanups done 鈥?return to caller
                                    cpu.CallStackDepth--;
                                    var frame = cpu.CallStack.Get(cpu.CallStackDepth);
                                    // DC: restore InCleanup from WasInCleanup flag
                                    if ((frame.CleanupBase & WAS_IN_CLEANUP) != 0)
                                        cpu.StateFlags |= VMStateFlags.InCleanup;
                                    else
                                        cpu.StateFlags &= ~VMStateFlags.InCleanup;
                                    cpu.IP = frame.ReturnIP;
                                    cpu.RegisterBase = frame.RegisterBase;
                                    // Restore return value that cleanup may have clobbered
                                    regs[0] = *(Number*)&frame.SavedR0;
                                    // VOM3 Phase1: host-call sentinel 鈥?ReturnIP < 0 means caller is host.
                                    if (cpu.IP < 0)
                                    {
                                        cpu.StateFlags |= VMStateFlags.Completed;
                                        return;
                                    }
                                    // If Killed, stop 鈥?let next Tick handle parent-scope cleanup
                                    if ((cpu.StateFlags & VMStateFlags.Killed) != 0)
                                        return;
                                    return;
                                }
                                else
                                {
                                    // All cleanups done (entry function)
                                    cpu.StateFlags &= ~VMStateFlags.InCleanup;
                                    cpu.StateFlags |= VMStateFlags.Completed;
                                    return;
                                }
                            }
                            else
                            {
                                // Normal return 鈥?enter cleanup if any
                                if (cpu.CleanupDepth > 0)
                                {
                                    cpu.StateFlags |= VMStateFlags.InCleanup;
                                    cpu.CleanupDepth--;
                                    cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                                    ExecuteCleanupInstance(ref cpu, ref vmd);
                                    return;
                                }
                                else
                                {
                                    cpu.StateFlags |= VMStateFlags.Completed;
                                    return;
                                }
                            }
                            break;

                        // --- Phase 2: Data Movement ---

                        case OpCode.MOVE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)];
                            cpu.IP++;
                            break;

                        // --- SO1: Struct block copy ---
                        case OpCode.COPY_BLOCK:
                        {
                            int dst = Reg(op.A, rb);
                            int src = Reg(op.B, rb);
                            int count = op.C;
                            for (int ci = 0; ci < count; ci++)
                                regs[dst + ci] = regs[src + ci];
                            cpu.IP++;
                            break;
                        }

                        // --- Phase 2: Control Flow ---

                        case OpCode.JUMP:
                            cpu.IP = op.A | extendedA;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_ZERO:
                            if (regs[Reg(op.B, rb)] == Number.Zero)
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NOT_ZERO:
                            if (regs[Reg(op.B, rb)] != Number.Zero)
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        // --- Phase 2: Arithmetic ---

                        case OpCode.ADD:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] + regs[Reg(op.C, rb)];
                            cpu.IP++;
                            break;

                        case OpCode.SUB:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] - regs[Reg(op.C, rb)];
                            cpu.IP++;
                            break;

                        case OpCode.MUL:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] * regs[Reg(op.C, rb)];
                            cpu.IP++;
                            break;

                        case OpCode.DIV:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] / regs[Reg(op.C, rb)];
                            cpu.IP++;
                            break;

                        case OpCode.MOD:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] % regs[Reg(op.C, rb)];
                            cpu.IP++;
                            break;

                        // --- Phase 2: Comparison ---

                        case OpCode.CMP_EQ:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] == regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.CMP_NEQ:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] != regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.CMP_LT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] < regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.CMP_LTE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] <= regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.CMP_GT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] > regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.CMP_GTE:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] >= regs[Reg(op.C, rb)] ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        // --- Phase 2: Boolean / Unary ---

                        case OpCode.AND:
                            regs[Reg(op.A, rb)] =
                                (regs[Reg(op.B, rb)] != Number.Zero && regs[Reg(op.C, rb)] != Number.Zero)
                                    ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.OR:
                            regs[Reg(op.A, rb)] =
                                (regs[Reg(op.B, rb)] != Number.Zero || regs[Reg(op.C, rb)] != Number.Zero)
                                    ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.NOT:
                            regs[Reg(op.A, rb)] = regs[Reg(op.B, rb)] == Number.Zero ? Number.One : Number.Zero;
                            cpu.IP++;
                            break;

                        case OpCode.NEG:
                            regs[Reg(op.A, rb)] = -regs[Reg(op.B, rb)];
                            cpu.IP++;
                            break;

                        // --- Lang-14: Bitwise ---

                        case OpCode.BIT_AND:
                            regs[Reg(op.A, rb)] = Number.BitAnd(regs[Reg(op.B, rb)], regs[Reg(op.C, rb)]);
                            cpu.IP++;
                            break;

                        case OpCode.BIT_OR:
                            regs[Reg(op.A, rb)] = Number.BitOr(regs[Reg(op.B, rb)], regs[Reg(op.C, rb)]);
                            cpu.IP++;
                            break;

                        case OpCode.BIT_XOR:
                            regs[Reg(op.A, rb)] = Number.BitXor(regs[Reg(op.B, rb)], regs[Reg(op.C, rb)]);
                            cpu.IP++;
                            break;

                        case OpCode.BIT_NOT:
                            regs[Reg(op.A, rb)] = Number.BitNot(regs[Reg(op.B, rb)]);
                            cpu.IP++;
                            break;

                        case OpCode.SHL:
                            regs[Reg(op.A, rb)] = Number.Shl(regs[Reg(op.B, rb)], regs[Reg(op.C, rb)]);
                            cpu.IP++;
                            break;

                        case OpCode.SHR:
                            regs[Reg(op.A, rb)] = Number.Shr(regs[Reg(op.B, rb)], regs[Reg(op.C, rb)]);
                            cpu.IP++;
                            break;

                        // --- Phase 3: Function Calls ---

                        case OpCode.CALL:
                        {
                            if (cpu.CallStackDepth >= VMConstants.MaxCallDepth)
                            {
                                cpu.ErrorFlag = VMError.PanicStackOverflow;
                                return;
                            }
                            // DC: pack WasInCleanup flag into high bit of CleanupBase
                            int cbValue = cpu.CleanupDepth;
                            if ((cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                                cbValue |= WAS_IN_CLEANUP;
                            var frame = new CallFrame
                            {
                                ReturnIP = cpu.IP + 1,
                                ReturnModuleSlot = vmd.ModuleSlot,
                                RegisterBase = cpu.RegisterBase,
                                CleanupBase = cbValue
                            };
                            cpu.CallStack.Set(cpu.CallStackDepth, frame);
                            cpu.CallStackDepth++;
                            cpu.RegisterBase += op.B; // B = callerWindowSize
                            cpu.IP = op.A | extendedA;            // A = target function entry IP
                            extendedA = 0;
                            break;                     // don't IP++ 鈥?already jumped
                        }

                        case OpCode.RET_FUNC:
                        {
                            // FF5: check for pending function-scoped cleanups before returning
                            var frame = cpu.CallStack.Get(cpu.CallStackDepth - 1);
                            if (cpu.CleanupDepth > (frame.CleanupBase & CLEANUP_BASE_MASK))
                            {
                                // Function has pending cleanups 鈥?execute them before returning.
                                // Keep CallStackDepth unchanged so RETURN can pop frame when done.
                                // DC: save r0 per-frame (supports nested cleanup from defer-called functions).
                                // CallFrame is a value type; Set() writes the modified copy back to the stack.
                                frame.SavedR0 = ((long*)regs)[0];
                                cpu.CallStack.Set(cpu.CallStackDepth - 1, frame);
                                cpu.StateFlags |= VMStateFlags.InCleanup;
                                cpu.CleanupDepth--;
                                cpu.IP = cpu.CleanupStack.Get(cpu.CleanupDepth).CleanupEntryIP;
                                ExecuteCleanupInstance(ref cpu, ref vmd);
                                if (cpu.ErrorFlag != VMError.None
                                    || (cpu.StateFlags & (VMStateFlags.Completed | VMStateFlags.Killed | VMStateFlags.InCleanup)) != 0)
                                    return;
                                rb = cpu.RegisterBase;
                                break;
                            }
                            else
                            {
                                // No pending cleanups 鈥?normal return
                                cpu.CallStackDepth--;
                                cpu.IP = frame.ReturnIP;
                                cpu.RegisterBase = frame.RegisterBase;
                                // VOM3 Phase1: host-call sentinel 鈥?ReturnIP < 0 means caller is host. Halt now.
                                if (cpu.IP < 0)
                                {
                                    cpu.StateFlags |= VMStateFlags.Completed;
                                    return;
                                }
                            }
                            break;                     // don't IP++ 鈥?either jumped to cleanup or ReturnIP is CALL+1
                        }

                        // --- FO1: Leaf function calls (skip CallFrame push/pop) ---

                        case OpCode.CALL_LEAF:
                        {
                            if (dbg != null)
                            {
                                // Debugger attached: promote to full CALL for call stack visibility
                                if (cpu.CallStackDepth >= VMConstants.MaxCallDepth)
                                {
                                    cpu.ErrorFlag = VMError.PanicStackOverflow;
                                    return;
                                }
                                // DC: pack WasInCleanup flag (same as CALL)
                                int cbValueLeaf = cpu.CleanupDepth;
                                if ((cpu.StateFlags & VMStateFlags.InCleanup) != 0)
                                    cbValueLeaf |= WAS_IN_CLEANUP;
                                var frame = new CallFrame
                                {
                                    ReturnIP = cpu.IP + 1,
                                    ReturnModuleSlot = vmd.ModuleSlot,
                                    RegisterBase = cpu.RegisterBase,
                                    CleanupBase = cbValueLeaf
                                };
                                cpu.CallStack.Set(cpu.CallStackDepth, frame);
                                cpu.CallStackDepth++;
                            }
                            else
                            {
                                // Fast path: save return info to instance fields, skip CallFrame
                                cpu.LeafReturnIP = cpu.IP + 1;
                                cpu.LeafRegisterBase = cpu.RegisterBase;
                            }
                            cpu.RegisterBase += op.B;
                            cpu.IP = op.A | extendedA;
                            extendedA = 0;
                            break;
                        }

                        case OpCode.RET_LEAF:
                        {
                            if (dbg != null)
                            {
                                // Debugger attached: promoted call used full CallFrame
                                cpu.CallStackDepth--;
                                var frame = cpu.CallStack.Get(cpu.CallStackDepth);
                                cpu.IP = frame.ReturnIP;
                                cpu.RegisterBase = frame.RegisterBase;
                            }
                            else
                            {
                                // Fast path: restore from instance fields
                                cpu.IP = cpu.LeafReturnIP;
                                cpu.RegisterBase = cpu.LeafRegisterBase;
                            }
                            // VOM3 Phase1: host-call sentinel 鈥?ReturnIP < 0 means caller is host.
                            if (cpu.IP < 0)
                            {
                                cpu.StateFlags |= VMStateFlags.Completed;
                                return;
                            }
                            break;
                        }

                        // --- P5: fused compare-and-branch (B-蔚2) ---

                        case OpCode.JUMP_IF_EQ:
                            if (regs[Reg(op.B, rb)] == regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NEQ:
                            if (regs[Reg(op.B, rb)] != regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LT:
                            if (regs[Reg(op.B, rb)] < regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LTE:
                            if (regs[Reg(op.B, rb)] <= regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GT:
                            if (regs[Reg(op.B, rb)] > regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GTE:
                            if (regs[Reg(op.B, rb)] >= regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        // --- B-蔚4: FORLOOP super-instruction ---

                        case OpCode.FORLOOP:
                        {
                            int cr = Reg(op.B, rb);
                            regs[cr] = regs[cr] + Number.One;
                            if (regs[cr] < regs[Reg(op.C, rb)])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;
                        }

                        // --- B-味2: fused constant-compare-and-branch ---

                        case OpCode.JUMP_IF_EQ_K:
                            if (regs[Reg(op.B, rb)] == constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_NEQ_K:
                            if (regs[Reg(op.B, rb)] != constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LT_K:
                            if (regs[Reg(op.B, rb)] < constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_LTE_K:
                            if (regs[Reg(op.B, rb)] <= constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GT_K:
                            if (regs[Reg(op.B, rb)] > constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        case OpCode.JUMP_IF_GTE_K:
                            if (regs[Reg(op.B, rb)] >= constBase[op.C])
                                cpu.IP = op.A | extendedA;
                            else
                                cpu.IP++;
                            extendedA = 0;
                            break;

                        // --- B-味3: SWITCH jump table dispatch ---

                        case OpCode.SWITCH:
                        {
                            int val = regs[Reg(op.B, rb)].ToInt();
                            int[] table = program.JumpTables[op.C];
                            if (val >= 0 && val < table.Length)
                                cpu.IP = table[val];
                            else
                                cpu.IP = op.A | extendedA; // default
                            extendedA = 0;
                            break;
                        }

                        // --- Lang-1: module variable access (absolute addressing) ---

                        case OpCode.LOAD_MVAR:
                            // [VOM10] Read from vmd.MVars (own buffer) instead of legacy regs[ModuleVarRegBase+B]
                            fixed (long* rawMvars = vmd.MVars.Raw)
                            {
                                Number* mvars = (Number*)rawMvars;
                                regs[Reg(op.A, rb)] = mvars[op.B];
                            }
                            cpu.IP++;
                            break;

                        case OpCode.STORE_MVAR:
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            // [VOM10] Write to vmd.MVars (own buffer) instead of legacy regs[ModuleVarRegBase+A]
                            fixed (long* rawMvars = vmd.MVars.Raw)
                            {
                                Number* mvars = (Number*)rawMvars;
                                mvars[op.A] = regs[Reg(op.B, rb)];
                            }
                            cpu.IP++;
                            break;

                        // --- Lang-1.1b: extended register access (heap-allocated) ---

                        case OpCode.LOAD_XREG:
                            if (xregs == null) { cpu.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                            regs[Reg(op.A, rb)] = xregs[op.B | (op.C << 8)];
                            cpu.IP++;
                            break;

                        case OpCode.STORE_XREG:
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            if (xregs == null) { cpu.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                            xregs[op.A | (op.C << 8)] = regs[Reg(op.B, rb)];
                            cpu.IP++;
                            break;

                        // --- Lang-6: cross-instance member access (XIMA) ---

                        case OpCode.XCALL:
                        {
                            // A=destReg, B=instanceId_reg, C=exportFuncIndex
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int funcIdx = op.C;
                            if (funcIdx < 0 || funcIdx >= targetProgram.ExportTable.Functions.Length)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            var exportFunc = targetProgram.ExportTable.Functions[funcIdx];
                            if (exportFunc.FuncTableIndex < 0 || exportFunc.FuncTableIndex >= targetProgram.Functions.Length)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }
                            var targetFunc = targetProgram.Functions[exportFunc.FuncTableIndex];

                            // VOM3 Phase2: caller in ReadOnlyMode may only XCALL @readonly targets;
                            // propagate the flag to the callee so its body remains gated.
                            bool callerReadOnly = (cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0;
                            if (callerReadOnly && !targetFunc.IsReadOnly)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
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
                                CallerInstanceId = vmd.InstanceId,
                                CallerIP = cpu.IP + 1,
                                CallerModuleSlot = vmd.ModuleSlot,
                                CallerRegisterBase = cpu.RegisterBase,
                                CallerCallStackDepth = cpu.CallStackDepth,
                                CallerCleanupDepth = cpu.CleanupDepth,
                                DestReg = destReg
                            };

                            // Copy parameters: caller scratch r0..r(paramCount-1) 鈫?target scratch r0..r(paramCount-1)
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

                            // VOM3 Phase2: propagate ReadOnlyMode flag into callee for the duration
                            // of this nested ExecuteInstance; restore afterwards.
                            VMStateFlags savedTargetFlags = targetInst.StateFlags;
                            if (callerReadOnly) targetInst.StateFlags |= VMStateFlags.ReadOnlyMode;

                            ExecuteInstance(ref targetInst.Cpu, ref targetInst.Data);
                            if ((targetInst.StateFlags & VMStateFlags.InCleanup) != 0
                                && targetInst.ErrorFlag == VMError.None
                                && (targetInst.StateFlags & VMStateFlags.Completed) == 0)
                                ExecuteCleanupInstance(ref targetInst.Cpu, ref targetInst.Data);

                            // Restore target's flag set (clear ReadOnlyMode if we set it)
                            targetInst.StateFlags = savedTargetFlags;

                            // Restore caller 鈥?re-pin caller registers (may have moved if same instance)
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
                                cpu.ErrorFlag = Pool.Instances[targetId].ErrorFlag;
                                return;
                            }

                            // Write return value to caller's dest register
                            // Need to re-reference regs since we're back in caller context
                            regs[xcFrame.DestReg] = returnVal;
                            cpu.IP = xcFrame.CallerIP;
                            cpu.RegisterBase = xcFrame.CallerRegisterBase;
                            cpu.CallStackDepth = xcFrame.CallerCallStackDepth;
                            cpu.CleanupDepth = (byte)xcFrame.CallerCleanupDepth;
                            rb = cpu.RegisterBase;
                            break;
                        }

                        case OpCode.XLOAD_MVAR:
                        {
                            // A=destReg, B=instanceId_reg, C=exportVarIndex
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int varIdx = op.C;
                            if (varIdx < 0 || varIdx >= targetProgram.ExportTable.Variables.Length)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int mvarSlot = targetProgram.ExportTable.Variables[varIdx].MvarSlot;

                            if (mvarSlot < VMConstants.ModuleVarSlots)
                            {
                                // [VOM10] Fixed MVar buffer (Data.MVars own array, not register file)
                                fixed (long* targetMvarRaw = targetInst.Data.MVars.Raw)
                                {
                                    Number* targetMvars = (Number*)targetMvarRaw;
                                    regs[Reg(op.A, rb)] = targetMvars[mvarSlot];
                                }
                            }
                            else
                            {
                                // Extended register path
                                Number[] txregs = Pool.ExtendedRegs[targetId];
                                if (txregs == null) { cpu.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                                regs[Reg(op.A, rb)] = txregs[mvarSlot - VMConstants.ModuleVarSlots];
                            }
                            cpu.IP++;
                            break;
                        }

                        case OpCode.XSTORE_MVAR:
                        {
                            if ((cpu.StateFlags & VMStateFlags.ReadOnlyMode) != 0)
                            {
                                cpu.ErrorFlag = VMError.PanicReadOnlyViolation;
                                return;
                            }
                            // A=exportVarIndex, B=instanceId_reg, C=srcReg
                            int targetId = regs[Reg(op.B, rb)].ToInt();
                            if (targetId < 0 || targetId >= VMConstants.MaxInstances)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }
                            ref var targetInst = ref Pool.Instances[targetId];
                            if (!targetInst.IsAlive)
                            {
                                cpu.ErrorFlag = VMError.PanicInvalidInstanceId;
                                return;
                            }

                            VMProgram targetProgram = Modules.Get(targetInst.ModuleSlot);
                            if (targetProgram == null || targetProgram.ExportTable == null)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            int varIdx = op.A;
                            if (varIdx < 0 || varIdx >= targetProgram.ExportTable.Variables.Length)
                            {
                                cpu.ErrorFlag = VMError.PanicExportNotFound;
                                return;
                            }

                            // Lang-12: reject writes to read-only exported consts
                            ref var exportVar = ref targetProgram.ExportTable.Variables[varIdx];
                            if (!exportVar.Writable)
                            {
                                cpu.ErrorFlag = VMError.PanicIllegalInstruction;
                                return;
                            }

                            int mvarSlot = exportVar.MvarSlot;

                            if (mvarSlot < VMConstants.ModuleVarSlots)
                            {
                                // [VOM10] Fixed MVar buffer (Data.MVars own array, not register file)
                                fixed (long* targetMvarRaw = targetInst.Data.MVars.Raw)
                                {
                                    Number* targetMvars = (Number*)targetMvarRaw;
                                    targetMvars[mvarSlot] = regs[Reg(op.C, rb)];
                                }
                            }
                            else
                            {
                                // Extended register path
                                Number[] txregs = Pool.ExtendedRegs[targetId];
                                if (txregs == null) { cpu.ErrorFlag = VMError.PanicIllegalInstruction; return; }
                                txregs[mvarSlot - VMConstants.ModuleVarSlots] = regs[Reg(op.C, rb)];
                            }
                            cpu.IP++;
                            break;
                        }

                        // --- O15: sentinel (end-of-program guard) ---

                        case OpCode.SENTINEL:
                            cpu.ErrorFlag = VMError.PanicOutOfBounds;
                            return;

                        default:
                            cpu.ErrorFlag = VMError.PanicIllegalInstruction;
                            return;
                    }

                }
            } // end fixed

            // Hit step limit 鈥?treat as runaway
            cpu.ErrorFlag = VMError.PanicStepLimitExceeded;
        }
    }
}
