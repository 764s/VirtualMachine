namespace FFVM
{
    /// <summary>
    /// VOM2/VOM3: object-style call entry points for host code.
    ///
    /// VOM3 Phase1: all three call shapes route through a transient instance
    /// pool with a sentinel CallFrame, so non-entry functions are supported and
    /// the per-call cost no longer includes <c>SpawnInstance</c> / <c>DestroyInstance</c>.
    /// Reaching the &lt;= 31 ns micro-benchmark and 0-alloc verification is deferred
    /// to VOM3 Phase2 (CPUData externalization + Reset profile).
    /// </summary>
    public static class VMEngine
    {
        /// <summary>
        /// Synchronous unrestricted call. The callee may write module variables
        /// and observe other state. Must complete within the per-call step budget
        /// (no WAIT / yield / blocking SYSCALL).
        /// </summary>
        public static void Call(
            VMWorld world,
            int moduleSlot,
            MethodHandle handle,
            Arguments args,
            ReturnSlot ret)
            => InvokeOnTransient(world, moduleSlot, handle, args, ret, requireReadOnly: false, callName: "Call");

        /// <summary>
        /// Synchronous read-only call. The function MUST be declared
        /// <c>@readonly</c> / <c>@static_readonly</c>; the compiler statically rejects
        /// module-state writes inside such a function (VOM2 Phase2).
        /// </summary>
        public static void ReadOnlyCall(
            VMWorld world,
            int moduleSlot,
            MethodHandle handle,
            Arguments args,
            ReturnSlot ret)
            => InvokeOnTransient(world, moduleSlot, handle, args, ret, requireReadOnly: true, callName: "ReadOnlyCall");

        /// <summary>
        /// VOM2-era alias for <see cref="ReadOnlyCall"/>. Kept for source-compat;
        /// semantics unchanged.
        /// </summary>
        public static void StaticReadOnlyCall(
            VMWorld world,
            int moduleSlot,
            MethodHandle handle,
            Arguments args,
            ReturnSlot ret)
            => InvokeOnTransient(world, moduleSlot, handle, args, ret, requireReadOnly: true, callName: "StaticReadOnlyCall");

        /// <summary>
        /// VOM4: spawn a yieldable instance from the main <see cref="InstancePool"/>
        /// and return a <see cref="YieldHandle"/> WITHOUT executing any bytecode.
        ///
        /// The caller drives execution via <c>world.Tick()</c> or
        /// <see cref="YieldHandle.TickOnce"/>; reads the result via
        /// <see cref="YieldHandle.ReadReturn"/> after <see cref="YieldHandle.IsCompleted"/>;
        /// and releases the slot via <see cref="YieldHandle.Release"/>.
        ///
        /// Unlike <see cref="Call"/> / <see cref="ReadOnlyCall"/>, the function
        /// is permitted to <c>WAIT</c> / <c>WAIT_FOR</c> / yield — control
        /// returns to the host between ticks.
        /// </summary>
        public static YieldHandle YieldCall(
            VMWorld world,
            int moduleSlot,
            MethodHandle handle,
            Arguments args)
        {
            if (world == null)
                throw new VMABIException("VMEngine.YieldCall: world is null");
            if (!handle.IsResolved)
                throw new VMABIException("VMEngine.YieldCall: MethodHandle is unresolved");

            var program = world.Modules.Get(moduleSlot);
            if (program == null)
                throw new VMABIException($"VMEngine.YieldCall: module slot {moduleSlot} not loaded");
            if (!handle.IsValid(program))
                throw new VMABIException("VMEngine.YieldCall: MethodHandle is stale (program changed)");
            if (args.Count != handle.ParamCount)
                throw new VMABIException(
                    $"VMEngine.YieldCall: argument count {args.Count} != expected {handle.ParamCount}");

            int id = world.Pool.Allocate(moduleSlot, handle.EntryIP);
            if (id < 0)
                throw new VMABIException("VMEngine.YieldCall: instance pool exhausted");

            // VOM5: bind the new host-spawned instance to the world's default
            // HostBindings so SYSCALL dispatch resolves correctly. Callers that
            // need a custom binding should use VMWorld.SpawnInstance(int,int,HostBindings).
            world.Pool.Bindings[id] = world.DefaultBindings;

            ref VMInstanceState inst = ref world.Pool.Instances[id];

            // VOM4: leaf-return sentinel. RET_LEAF restores IP from inst.LeafReturnIP
            // without touching the call stack; we must seed it with -1 so a leaf
            // function (e.g. `add(a,b) { return a+b }` compiled with RET_LEAF) hits
            // the host-call sentinel branch and halts with Completed.
            inst.LeafReturnIP = -1;
            inst.LeafRegisterBase = 0;

            // Sentinel CallFrame (ReturnIP = -1). RET_FUNC / RET_LEAF / RETURN-cleanup-pop
            // detect IP < 0 after restoring the frame and halt with Completed.
            inst.CallStack.Set(0, new CallFrame
            {
                ReturnIP = -1,
                ReturnModuleSlot = moduleSlot,
                RegisterBase = 0,
                CleanupBase = 0,
                SavedR0 = 0,
            });
            inst.CallStackDepth = 1;

            // Copy arguments into r0..r(N-1).
            for (int i = 0; i < args.Count; i++)
                inst.Registers.Set(i, args[i]);

            return new YieldHandle(id, inst.Generation, moduleSlot, handle.ReturnCount);
        }

        private static void InvokeOnTransient(
            VMWorld world,
            int moduleSlot,
            MethodHandle handle,
            Arguments args,
            ReturnSlot ret,
            bool requireReadOnly,
            string callName)
        {
            if (world == null)
                throw new VMABIException($"VMEngine.{callName}: world is null");
            if (!handle.IsResolved)
                throw new VMABIException($"VMEngine.{callName}: MethodHandle is unresolved");

            var program = world.Modules.Get(moduleSlot);
            if (program == null)
                throw new VMABIException($"VMEngine.{callName}: module slot {moduleSlot} not loaded");
            if (!handle.IsValid(program))
                throw new VMABIException($"VMEngine.{callName}: MethodHandle is stale (program changed)");
            if (requireReadOnly && !program.Functions[handle.FunctionIndex].IsReadOnly)
                throw new VMABIException(
                    $"VMEngine.{callName}: function '{program.Functions[handle.FunctionIndex].Name}' is not declared @readonly");
            if (args.Count != handle.ParamCount)
                throw new VMABIException(
                    $"VMEngine.{callName}: argument count {args.Count} != expected {handle.ParamCount}");
            if (ret.Capacity < handle.ReturnCount)
                throw new VMABIException(
                    $"VMEngine.{callName}: return slot capacity {ret.Capacity} < required {handle.ReturnCount}");

            var pool = world.TransientPool;
            int id = pool.Rent();
            try
            {
                ref var inst = ref pool.Get(id);

                // Initialize the borrowed instance for a one-shot host call.
                inst.InstanceId = -1;                 // not in ActiveList; never exposed to scripts
                inst.ModuleSlot = moduleSlot;
                inst.IP = handle.EntryIP;
                inst.RegisterBase = 0;
                inst.IsAlive = true;
                inst.StateFlags = VMStateFlags.Active;
                if (requireReadOnly) inst.StateFlags |= VMStateFlags.ReadOnlyMode;
                inst.WaitTargetInstanceId = -1;
                inst.LeafReturnIP = -1;
                inst.LeafRegisterBase = 0;
                inst.ActiveListIndex = -1;
                inst.ErrorFlag = VMError.None;
                inst.CleanupDepth = 0;

                // VOM11 safety belt: lazy-Rent skips wholesale memzero, but module
                // variables (Data.MVars, ~64 bytes) are not in the explicit
                // control-field reset list above, and a transient slot may be reused
                // by a different module's function. Zero MVars here to guarantee
                // cross-call isolation. Cost ~1 ns vs ~9 ns for full default.
                inst.Data.MVars = default;

                // Sentinel CallFrame with ReturnIP = -1. RET_FUNC / RET_LEAF / RETURN-cleanup-pop
                // detect IP<0 after restoring frame and halt with Completed.
                inst.CallStack.Set(0, new CallFrame
                {
                    ReturnIP = -1,
                    ReturnModuleSlot = moduleSlot,
                    RegisterBase = 0,
                    CleanupBase = 0,
                    SavedR0 = 0,
                });
                inst.CallStackDepth = 1;

                // Copy arguments into r0..r(N-1) — the scratch zone where parameters land.
                for (int i = 0; i < args.Count; i++)
                    inst.Registers.Set(i, args[i]);

                world.ExecuteInstance(ref inst.Cpu, ref inst.Data);
                if ((inst.StateFlags & VMStateFlags.InCleanup) != 0
                    && inst.ErrorFlag == VMError.None
                    && (inst.StateFlags & VMStateFlags.Completed) == 0)
                    world.ExecuteCleanupInstance(ref inst.Cpu, ref inst.Data);

                if (inst.ErrorFlag == VMError.PanicReadOnlyViolation)
                {
                    string opName = "<unknown>";
                    if (inst.IP >= 0 && inst.IP < program.Instructions.Length)
                        opName = program.Instructions[inst.IP].Code.ToString();
                    string fnName = program.Functions[handle.FunctionIndex].Name;
                    throw new ReadOnlyViolationException(opName, inst.IP, fnName);
                }
                if (inst.ErrorFlag != VMError.None)
                    throw new VMABIException($"VMEngine.{callName}: instance error: {inst.ErrorFlag}");
                if ((inst.StateFlags & VMStateFlags.Completed) == 0)
                    throw new VMABIException(
                        $"VMEngine.{callName}: function did not complete in one tick (yield/wait disallowed)");

                for (int i = 0; i < handle.ReturnCount; i++)
                    ret.Set(i, inst.Registers.Get(i));
            }
            finally
            {
                pool.Return(id);
            }
        }

        /// <summary>
        /// VOM6: row-batched dispatch over a single <see cref="MethodHandle"/>.
        /// Shares one transient slot + one sentinel <see cref="CallFrame"/> across
        /// all rows; only mutable per-row state (IP, flags, leaf-return sentinel,
        /// cleanup depth, error flag, register file) is reset between iterations.
        ///
        /// Returns the number of rows that failed. If the caller supplied
        /// <see cref="BatchPlan.Errors"/>, per-row errors are written there and the
        /// loop continues; otherwise the first failure throws immediately.
        /// </summary>
        public static int Batch(VMWorld world, int moduleSlot, BatchPlan plan, BatchKind kind)
        {
            if (world == null)
                throw new VMABIException("VMEngine.Batch: world is null");
            var program = world.Modules.Get(moduleSlot);
            if (program == null)
                throw new VMABIException($"VMEngine.Batch: module slot {moduleSlot} not loaded");
            if (!plan.Handle.IsValid(program))
                throw new VMABIException("VMEngine.Batch: MethodHandle is stale (program changed)");

            bool requireReadOnly = (kind == BatchKind.ReadOnlyCall);
            if (requireReadOnly && !program.Functions[plan.Handle.FunctionIndex].IsReadOnly)
                throw new VMABIException(
                    $"VMEngine.Batch: function '{program.Functions[plan.Handle.FunctionIndex].Name}' is not declared @readonly");

            if (plan.Count == 0) return 0;

            int paramCount = plan.Handle.ParamCount;
            int returnCount = plan.Handle.ReturnCount;
            int entryIP = plan.Handle.EntryIP;

            var pool = world.TransientPool;
            int id = pool.Rent();
            int failures = 0;
            try
            {
                ref var inst = ref pool.Get(id);

                // Slot-invariant init (done once; loop body only resets per-row state).
                inst.InstanceId = -1;
                inst.ModuleSlot = moduleSlot;
                inst.RegisterBase = 0;
                inst.WaitTargetInstanceId = -1;
                inst.LeafRegisterBase = 0;
                inst.ActiveListIndex = -1;
                // VOM11 safety belt: lazy-Rent skips wholesale memzero; clear MVars
                // (~64 bytes) here so this batch never sees stale module variables
                // from a previous transient call against a different module.
                inst.Data.MVars = default;
                inst.CallStack.Set(0, new CallFrame
                {
                    ReturnIP = -1,
                    ReturnModuleSlot = moduleSlot,
                    RegisterBase = 0,
                    CleanupBase = 0,
                    SavedR0 = 0,
                });

                VMStateFlags baseFlags = VMStateFlags.Active;
                if (requireReadOnly) baseFlags |= VMStateFlags.ReadOnlyMode;

                for (int row = 0; row < plan.Count; row++)
                {
                    // Per-row reset (sentinel CallFrame already in place).
                    inst.IP = entryIP;
                    inst.IsAlive = true;
                    inst.StateFlags = baseFlags;
                    inst.LeafReturnIP = -1;
                    inst.CallStackDepth = 1;
                    inst.CleanupDepth = 0;
                    inst.ErrorFlag = VMError.None;

                    // Copy row args into r0..r(N-1).
                    var argRow = plan.ArgsAt(row);
                    for (int i = 0; i < paramCount; i++)
                        inst.Registers.Set(i, argRow[i]);

                    world.ExecuteInstance(ref inst.Cpu, ref inst.Data);
                    if ((inst.StateFlags & VMStateFlags.InCleanup) != 0
                        && inst.ErrorFlag == VMError.None
                        && (inst.StateFlags & VMStateFlags.Completed) == 0)
                        world.ExecuteCleanupInstance(ref inst.Cpu, ref inst.Data);

                    // Diagnose row outcome.
                    VMError rowErr = inst.ErrorFlag;
                    bool didNotComplete = (rowErr == VMError.None)
                        && ((inst.StateFlags & VMStateFlags.Completed) == 0);
                    if (didNotComplete)
                    {
                        // Yielded/blocked inside Batch — policy violation.
                        rowErr = VMError.PanicStepLimitExceeded;
                    }

                    if (rowErr != VMError.None)
                    {
                        failures++;
                        if (plan.HasErrorSink)
                        {
                            plan.Errors[row] = rowErr;
                            // Zero-fill returns so caller doesn't read stale data.
                            var retRowZ = plan.ReturnsAt(row);
                            for (int i = 0; i < returnCount; i++) retRowZ[i] = default;
                            continue;
                        }

                        if (rowErr == VMError.PanicReadOnlyViolation)
                        {
                            string opName = "<unknown>";
                            if (inst.IP >= 0 && inst.IP < program.Instructions.Length)
                                opName = program.Instructions[inst.IP].Code.ToString();
                            string fnName = program.Functions[plan.Handle.FunctionIndex].Name;
                            throw new ReadOnlyViolationException(opName, inst.IP, fnName);
                        }
                        if (didNotComplete)
                        {
                            throw new VMABIException(
                                $"VMEngine.Batch: row {row} did not complete in one tick (yield/wait disallowed)");
                        }
                        throw new VMABIException(
                            $"VMEngine.Batch: row {row} error: {rowErr}");
                    }

                    var retRow = plan.ReturnsAt(row);
                    for (int i = 0; i < returnCount; i++)
                        retRow[i] = inst.Registers.Get(i);
                }
            }
            finally
            {
                pool.Return(id);
            }
            return failures;
        }
    }
}
