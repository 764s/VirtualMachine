using System;

namespace FFVM
{
    /// <summary>
    /// VOM5: lightweight, owning-by-id façade over a VM instance.
    /// Combines a <see cref="VMWorld"/> reference with a slot id and a
    /// generation token so stale handles are detectable in O(1).
    ///
    /// Semantics:
    /// - <see cref="IsValid"/>: handle's generation matches the live slot.
    /// - <see cref="IsAlive"/>: slot is allocated AND not killed/completed.
    /// - <see cref="IsCompleted"/>: slot ran to completion under this handle.
    ///
    /// Reentrancy: <see cref="Tick"/> respects the same <c>HostExecuting</c>
    /// guard as <see cref="VMWorld.TickInstance"/>.
    /// </summary>
    public readonly struct VMInstance : IEquatable<VMInstance>
    {
        public readonly VMWorld World;
        public readonly int InstanceId;
        public readonly int Generation;

        public static readonly VMInstance Invalid = new VMInstance(null, -1, 0);

        public VMInstance(VMWorld world, int instanceId, int generation)
        {
            World = world;
            InstanceId = instanceId;
            Generation = generation;
        }

        /// <summary>Handle still references the same slot incarnation.</summary>
        public bool IsValid
        {
            get
            {
                if (World == null || InstanceId < 0) return false;
                if (InstanceId >= VMConstants.MaxInstances) return false;
                return World.Pool.Instances[InstanceId].Generation == Generation;
            }
        }

        /// <summary>Handle is valid AND the underlying slot is still running.</summary>
        public bool IsAlive
        {
            get
            {
                if (!IsValid) return false;
                ref readonly var s = ref World.Pool.Instances[InstanceId];
                if (!s.IsAlive) return false;
                if ((s.StateFlags & VMStateFlags.Killed) != 0) return false;
                if ((s.StateFlags & VMStateFlags.Completed) != 0) return false;
                return true;
            }
        }

        /// <summary>Handle is valid AND the underlying slot has completed normally.</summary>
        public bool IsCompleted
        {
            get
            {
                if (!IsValid) return false;
                ref readonly var s = ref World.Pool.Instances[InstanceId];
                return (s.StateFlags & VMStateFlags.Completed) != 0;
            }
        }

        /// <summary>Per-instance host bindings, or null if the handle is stale.</summary>
        public HostBindings Bindings
        {
            get
            {
                if (!IsValid) return null;
                return World.Pool.Bindings[InstanceId];
            }
        }

        /// <summary>
        /// Advance one VM tick. No-op if handle is stale or slot already terminated.
        /// Throws <see cref="YieldReentrancyException"/> if the host is already
        /// executing this slot (matches VMEngine.YieldCall guard).
        /// </summary>
        public void Tick()
        {
            if (!IsValid) return;
            ref var s = ref World.Pool.Instances[InstanceId];
            if ((s.StateFlags & VMStateFlags.HostExecuting) != 0)
                throw new YieldReentrancyException(
                    $"VMInstance.Tick: instance {InstanceId} is currently executing host-side; reentrancy is forbidden.");
            World.TickInstance(InstanceId);
        }

        /// <summary>Mark the slot for termination on the next tick. No-op if stale.</summary>
        public void Kill()
        {
            if (!IsValid) return;
            ref var s = ref World.Pool.Instances[InstanceId];
            s.StateFlags |= VMStateFlags.Killed;
        }

        public bool Equals(VMInstance other) =>
            ReferenceEquals(World, other.World) &&
            InstanceId == other.InstanceId &&
            Generation == other.Generation;

        public override bool Equals(object obj) => obj is VMInstance v && Equals(v);
        public override int GetHashCode() =>
            unchecked(((World?.GetHashCode() ?? 0) * 397) ^ (InstanceId * 31) ^ Generation);

        public static bool operator ==(VMInstance a, VMInstance b) => a.Equals(b);
        public static bool operator !=(VMInstance a, VMInstance b) => !a.Equals(b);
    }
}
