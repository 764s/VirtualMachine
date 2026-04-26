using System;
using FFVM;
using UnityEngine;

public static partial class VOM3Tests
{
    private static void RunPoolTests()
    {
        // P01: Rent/Return are inverse — repeated cycles do not leak slots.
        {
            var pool = new TransientInstancePool();
            int cap0 = pool.Capacity;
            for (int i = 0; i < 10000; i++)
            {
                int id = pool.Rent();
                pool.Return(id);
            }
            Assert(pool.Capacity == cap0, "VOM3.P01_RentReturnNoGrowth");
        }

        // P02: Pool grows when concurrent rents exceed initial capacity.
        {
            var pool = new TransientInstancePool();
            int cap0 = pool.Capacity;
            int n = cap0 + 5;
            int[] ids = new int[n];
            for (int i = 0; i < n; i++) ids[i] = pool.Rent();
            Assert(pool.Capacity >= n, "VOM3.P02a_PoolGrewToFitDemand");
            // All ids must be distinct.
            bool unique = true;
            for (int i = 0; i < n && unique; i++)
                for (int j = i + 1; j < n && unique; j++)
                    if (ids[i] == ids[j]) unique = false;
            Assert(unique, "VOM3.P02b_RentedIdsUnique");
            for (int i = 0; i < n; i++) pool.Return(ids[i]);
        }

        // P03: 0-alloc check on the steady-state Rent/Return path.
        // Note: this measures only the pool's own bookkeeping, not VMEngine.Call (which
        // currently still allocates per-call inside ExecuteInstance — perf gates land in
        // VOM3 Phase2). A regression here would mean we re-introduced per-call alloc on
        // the pool itself.
        {
            var pool = new TransientInstancePool();
            // Warm-up.
            for (int i = 0; i < 16; i++) { int id = pool.Rent(); pool.Return(id); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) { int id = pool.Rent(); pool.Return(id); }
            long after = GC.GetAllocatedBytesForCurrentThread();
            Assert(after - before == 0, $"VOM3.P03_PoolRentReturnZeroAlloc (delta={after - before})");
        }
    }
}
