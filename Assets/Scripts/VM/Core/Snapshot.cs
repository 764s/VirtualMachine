using System;
using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Snapshot of Instance Pool's free stack state.
    /// Required for deterministic allocation order after rollback.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FreeStackSnapshot
    {
        // Mirror of InstancePool.FreeStack + FreeTop
        public int FreeTop;
        // O9: ActiveListCount for active instance list (replaces separate ActiveCount)
        public int ActiveListCount;
        // FreeStack array is snapshotted separately via Buffer.BlockCopy
    }

    /// <summary>
    /// Complete snapshot of one frame's VM world state.
    /// Pure blittable for memcpy. Pre-allocated in ring buffer.
    /// </summary>
    public struct VMWorldSnapshot
    {
        public int FrameNumber;
        public int ActiveInstanceCount;
        public FreeStackSnapshot FreeStackState;

        // Snapshots of instance slots (O10: only active entries are populated; inactive entries contain stale data)
        public VMInstanceState[] InstanceSnapshots;

        // Free stack snapshot
        public int[] FreeStackData;

        // O9: Active list snapshot
        public int[] ActiveListData;

        // Lang-1.1b: Extended register snapshots (per-instance, null = not used)
        public Number[][] ExtendedRegSnapshots;

        // VOM5: per-instance host bindings snapshot (shallow ref-copy; HostBindings
        // is itself an immutable reference once constructed). Inactive entries left
        // null; on LoadState we clear all slots first then restore active ones.
        public HostBindings[] BindingSnapshots;

        public void Init()
        {
            InstanceSnapshots = new VMInstanceState[VMConstants.MaxInstances];
            FreeStackData = new int[VMConstants.MaxFreeStack];
            ActiveListData = new int[VMConstants.MaxInstances];
            ExtendedRegSnapshots = new Number[VMConstants.MaxInstances][];
            BindingSnapshots = new HostBindings[VMConstants.MaxInstances];
        }
    }

    /// <summary>
    /// Ring buffer of VMWorldSnapshot for rollback.
    /// Pre-allocated at startup, zero runtime allocation.
    /// </summary>
    public class SnapshotRingBuffer
    {
        private readonly VMWorldSnapshot[] _ring;
        private int _head; // Next write position

        public SnapshotRingBuffer()
        {
            _ring = new VMWorldSnapshot[VMConstants.SnapshotRingSize];
            for (int i = 0; i < VMConstants.SnapshotRingSize; i++)
            {
                _ring[i].Init();
            }
            _head = 0;
        }

        /// <summary>
        /// Save current VM world state into the next ring slot.
        /// O10: Only copies active instances instead of all 128 slots.
        /// </summary>
        public void SaveState(ref InstancePool pool, int frameNumber)
        {
            ref VMWorldSnapshot snap = ref _ring[_head];
            snap.FrameNumber = frameNumber;
            snap.ActiveInstanceCount = pool.ActiveListCount;
            snap.FreeStackState.FreeTop = pool.FreeTop;
            snap.FreeStackState.ActiveListCount = pool.ActiveListCount;

            // O10: Copy only active instances (typically 3-10 out of 128)
            for (int i = 0; i < pool.ActiveListCount; i++)
            {
                int id = pool.ActiveList[i];
                snap.InstanceSnapshots[id] = pool.Instances[id];

                // VOM5: shallow ref-copy of per-instance bindings.
                snap.BindingSnapshots[id] = pool.Bindings[id];

                // Lang-1.1b: Deep-copy extended registers if allocated
                var xregs = pool.ExtendedRegs[id];
                if (xregs != null)
                {
                    if (snap.ExtendedRegSnapshots[id] == null || snap.ExtendedRegSnapshots[id].Length != xregs.Length)
                        snap.ExtendedRegSnapshots[id] = new Number[xregs.Length];
                    Array.Copy(xregs, snap.ExtendedRegSnapshots[id], xregs.Length);
                }
                else
                {
                    snap.ExtendedRegSnapshots[id] = null;
                }
            }

            // memcpy free stack (int[128] = 512 bytes — always full copy)
            Array.Copy(pool.FreeStack, snap.FreeStackData, VMConstants.MaxFreeStack);

            // O9: memcpy active list (int[128] = 512 bytes — always full copy)
            Array.Copy(pool.ActiveList, snap.ActiveListData, VMConstants.MaxInstances);

            _head = (_head + 1) % VMConstants.SnapshotRingSize;
        }

        /// <summary>
        /// Load a snapshot back into the VM world. Returns false if frame not found.
        /// O10: Only restores active instances. Clears IsAlive on all slots first
        /// to invalidate stale data from post-snapshot mutations.
        /// </summary>
        public bool LoadState(ref InstancePool pool, int frameNumber)
        {
            for (int i = 0; i < VMConstants.SnapshotRingSize; i++)
            {
                ref VMWorldSnapshot snap = ref _ring[i];
                if (snap.FrameNumber == frameNumber)
                {
                    // O10: Clear IsAlive on all slots to prevent stale instances
                    // from appearing alive after rollback (128 byte writes — trivial cost)
                    for (int j = 0; j < VMConstants.MaxInstances; j++)
                    {
                        pool.Instances[j].IsAlive = false;
                        // VOM5: clear bindings on all slots so dead slots don't keep
                        // post-snapshot bindings alive (also avoids GC root pinning).
                        pool.Bindings[j] = null;
                    }

                    pool.ActiveListCount = snap.FreeStackState.ActiveListCount;
                    pool.FreeTop = snap.FreeStackState.FreeTop;

                    // O10: Only restore active instances from snapshot
                    for (int j = 0; j < snap.FreeStackState.ActiveListCount; j++)
                    {
                        int id = snap.ActiveListData[j];
                        pool.Instances[id] = snap.InstanceSnapshots[id];

                        // VOM5: restore per-instance binding ref.
                        pool.Bindings[id] = snap.BindingSnapshots[id];

                        // Lang-1.1b: Restore extended registers
                        var xsnap = snap.ExtendedRegSnapshots[id];
                        if (xsnap != null)
                        {
                            if (pool.ExtendedRegs[id] == null || pool.ExtendedRegs[id].Length != xsnap.Length)
                                pool.ExtendedRegs[id] = new Number[xsnap.Length];
                            Array.Copy(xsnap, pool.ExtendedRegs[id], xsnap.Length);
                        }
                        else
                        {
                            pool.ExtendedRegs[id] = null;
                        }
                    }

                    Array.Copy(snap.FreeStackData, pool.FreeStack, VMConstants.MaxFreeStack);

                    // O9: restore active list
                    Array.Copy(snap.ActiveListData, pool.ActiveList, VMConstants.MaxInstances);

                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Get the most recent snapshot frame number.
        /// </summary>
        public int GetLatestFrame()
        {
            int prev = (_head - 1 + VMConstants.SnapshotRingSize) % VMConstants.SnapshotRingSize;
            return _ring[prev].FrameNumber;
        }
    }
}
