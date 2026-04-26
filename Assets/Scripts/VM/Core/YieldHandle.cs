using System;

namespace FFVM
{
    /// <summary>
    /// VOM4: thrown when host code attempts a reentrant operation (Release,
    /// TickOnce, ReadReturn) on an instance that is already inside
    /// <see cref="VMWorld.ExecuteInstance"/> (e.g. from within a syscall callback).
    /// </summary>
    public sealed class YieldReentrancyException : VMABIException
    {
        public YieldReentrancyException(string message) : base(message) { }
    }

    /// <summary>
    /// VOM4: opaque handle to a yieldable VM instance spawned by
    /// <see cref="VMEngine.YieldCall"/>.
    ///
    /// The handle stores the <see cref="VMInstanceState.Generation"/> at issue
    /// time, so a slot reused after <see cref="Release"/> invalidates older
    /// copies of the handle (ABA defense).
    ///
    /// Driving the instance:
    /// <list type="bullet">
    ///   <item><c>world.Tick()</c> — drives ALL alive instances (typical game loop)</item>
    ///   <item><see cref="TickOnce"/> — drives ONLY this instance once (escape hatch)</item>
    /// </list>
    ///
    /// Lifecycle: caller MUST call <see cref="Release"/> exactly once when done
    /// (whether <see cref="IsCompleted"/> or aborted), otherwise the slot leaks
    /// from the main InstancePool until the next world reset.
    /// </summary>
    public readonly struct YieldHandle : IEquatable<YieldHandle>
    {
        public readonly int InstanceId;
        public readonly int Generation;
        public readonly int ModuleSlot;
        public readonly int ReturnCount;

        public YieldHandle(int instanceId, int generation, int moduleSlot, int returnCount)
        {
            InstanceId = instanceId;
            Generation = generation;
            ModuleSlot = moduleSlot;
            ReturnCount = returnCount;
        }

        public static readonly YieldHandle Invalid = new YieldHandle(-1, 0, -1, 0);

        public bool IsValid(VMWorld world)
        {
            if (world == null || InstanceId < 0 || InstanceId >= VMConstants.MaxInstances)
                return false;
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            return inst.IsAlive && inst.Generation == Generation;
        }

        /// <summary>
        /// True iff the instance has reached a terminal state (RET to sentinel frame
        /// or panic). Stale handle returns false (caller should check IsValid first).
        /// </summary>
        public bool IsCompleted(VMWorld world)
        {
            if (!IsValid(world)) return false;
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            return (inst.StateFlags & VMStateFlags.Completed) != 0;
        }

        public bool HasError(VMWorld world)
        {
            if (!IsValid(world)) return false;
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            return inst.ErrorFlag != VMError.None;
        }

        public VMError GetError(VMWorld world)
        {
            if (!IsValid(world)) return VMError.None;
            return world.Pool.Instances[InstanceId].ErrorFlag;
        }

        /// <summary>
        /// Copy r0..ReturnCount-1 to the supplied <paramref name="ret"/> slot.
        /// Throws <see cref="VMABIException"/> if the call has not completed,
        /// the handle is stale, or capacity is insufficient.
        /// </summary>
        public void ReadReturn(VMWorld world, ReturnSlot ret)
        {
            CheckUsable(world, "ReadReturn");
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            if ((inst.StateFlags & VMStateFlags.Completed) == 0)
                throw new VMABIException("YieldHandle.ReadReturn: call has not completed yet");
            if (ret.Capacity < ReturnCount)
                throw new VMABIException(
                    $"YieldHandle.ReadReturn: return slot capacity {ret.Capacity} < required {ReturnCount}");
            for (int i = 0; i < ReturnCount; i++)
                ret.Set(i, inst.Registers.Get(i));
        }

        /// <summary>
        /// Drive this single instance for one tick. Mirrors <c>VMWorld.TickInstance</c>
        /// without advancing the global frame counter.
        /// Rejects if the instance is currently inside <see cref="VMWorld.ExecuteInstance"/>.
        /// </summary>
        public void TickOnce(VMWorld world)
        {
            CheckUsable(world, "TickOnce");
            world.TickInstance(InstanceId);
        }

        /// <summary>
        /// Free the instance back to the pool. Idempotent against stale handles
        /// (returns silently). Rejects if the instance is currently host-executing.
        /// </summary>
        public void Release(VMWorld world)
        {
            if (world == null) return;
            if (!IsValid(world)) return; // stale or already freed — no-op
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            if ((inst.StateFlags & VMStateFlags.HostExecuting) != 0)
                throw new YieldReentrancyException(
                    $"YieldHandle.Release: instance {InstanceId} is currently executing (reentrant call from syscall?)");
            world.DestroyInstance(InstanceId);
        }

        private void CheckUsable(VMWorld world, string op)
        {
            if (world == null)
                throw new VMABIException($"YieldHandle.{op}: world is null");
            if (!IsValid(world))
                throw new VMABIException($"YieldHandle.{op}: handle is stale or invalid");
            ref VMInstanceState inst = ref world.Pool.Instances[InstanceId];
            if ((inst.StateFlags & VMStateFlags.HostExecuting) != 0)
                throw new YieldReentrancyException(
                    $"YieldHandle.{op}: instance {InstanceId} is currently executing (reentrant call from syscall?)");
        }

        public bool Equals(YieldHandle other)
            => InstanceId == other.InstanceId && Generation == other.Generation;

        public override bool Equals(object obj) => obj is YieldHandle h && Equals(h);
        public override int GetHashCode() => (InstanceId * 397) ^ Generation;
        public override string ToString()
            => $"YieldHandle(id={InstanceId}, gen={Generation}, mod={ModuleSlot}, ret={ReturnCount})";
    }
}
