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
        public int ActiveCount;
        // O9: ActiveListCount for active instance list
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

        // Snapshots of all instance slots (including inactive — cheap to copy, simpler logic)
        public VMInstanceState[] InstanceSnapshots;

        // Free stack snapshot
        public int[] FreeStackData;

        // O9: Active list snapshot
        public int[] ActiveListData;

        public void Init()
        {
            InstanceSnapshots = new VMInstanceState[VMConstants.MaxInstances];
            FreeStackData = new int[VMConstants.MaxFreeStack];
            ActiveListData = new int[VMConstants.MaxInstances];
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
        /// </summary>
        public void SaveState(ref InstancePool pool, int frameNumber)
        {
            ref VMWorldSnapshot snap = ref _ring[_head];
            snap.FrameNumber = frameNumber;
            snap.ActiveInstanceCount = pool.ActiveCount;
            snap.FreeStackState.FreeTop = pool.FreeTop;
            snap.FreeStackState.ActiveCount = pool.ActiveCount;
            snap.FreeStackState.ActiveListCount = pool.ActiveListCount;

            // memcpy instances
            Array.Copy(pool.Instances, snap.InstanceSnapshots, VMConstants.MaxInstances);

            // memcpy free stack
            Array.Copy(pool.FreeStack, snap.FreeStackData, VMConstants.MaxFreeStack);

            // O9: memcpy active list
            Array.Copy(pool.ActiveList, snap.ActiveListData, VMConstants.MaxInstances);

            _head = (_head + 1) % VMConstants.SnapshotRingSize;
        }

        /// <summary>
        /// Load a snapshot back into the VM world. Returns false if frame not found.
        /// </summary>
        public bool LoadState(ref InstancePool pool, int frameNumber)
        {
            for (int i = 0; i < VMConstants.SnapshotRingSize; i++)
            {
                ref VMWorldSnapshot snap = ref _ring[i];
                if (snap.FrameNumber == frameNumber)
                {
                    pool.ActiveCount = snap.ActiveInstanceCount;
                    pool.FreeTop = snap.FreeStackState.FreeTop;
                    pool.ActiveListCount = snap.FreeStackState.ActiveListCount;

                    Array.Copy(snap.InstanceSnapshots, pool.Instances, VMConstants.MaxInstances);
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
