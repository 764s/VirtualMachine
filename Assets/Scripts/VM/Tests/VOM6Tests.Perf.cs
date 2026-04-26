using System;
using System.Diagnostics;
using FFVM;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static partial class VOM6Tests
{
    // Warn-only perf gates per VOM3 P2_F2 precedent.
    private const double GateReadOnlyNs = 35.0;
    private const double GateCallNs = 45.0;

    private static void RunPerfTests()
    {
        // P01: batch=64 ReadOnlyCall amortized per-row cost (warn-only ≤35ns).
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            const int N = 64;
            Span<Number> args = stackalloc Number[N];
            Span<Number> rets = stackalloc Number[N];
            for (int i = 0; i < N; i++) args[i] = Number.FromInt(i);

            var plan = new BatchPlan(h, N, args, rets);

            // warmup
            for (int w = 0; w < 32; w++) VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);

            const int Iters = 2000;
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < Iters; it++)
                VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
            sw.Stop();

            double nsPerRow = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (Iters * (double)N);
            Debug.Log($"[VOM6.P01] ReadOnly batch=64 per-row: {nsPerRow:F1} ns (gate {GateReadOnlyNs} ns warn-only)");
            if (nsPerRow > GateReadOnlyNs)
                Debug.Log($"[VOM6.P01] WARN: {nsPerRow:F1} ns exceeds gate {GateReadOnlyNs} ns");
            Assert(true, "VOM6.P01_ReadOnlyBatchPerf_Recorded");
        }

        // P02: batch=64 Call amortized per-row cost (warn-only ≤45ns).
        {
            string src = @"
func add(a: int, b: int): int { return a + b }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("add");

            const int N = 64;
            Span<Number> args = stackalloc Number[N * 2];
            Span<Number> rets = stackalloc Number[N];
            for (int i = 0; i < N; i++)
            {
                args[i * 2 + 0] = Number.FromInt(i);
                args[i * 2 + 1] = Number.FromInt(1);
            }
            var plan = new BatchPlan(h, N, args, rets);

            for (int w = 0; w < 32; w++) VMEngine.Batch(world, 0, plan, BatchKind.Call);

            const int Iters = 2000;
            var sw = Stopwatch.StartNew();
            for (int it = 0; it < Iters; it++)
                VMEngine.Batch(world, 0, plan, BatchKind.Call);
            sw.Stop();

            double nsPerRow = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / (Iters * (double)N);
            Debug.Log($"[VOM6.P02] Call batch=64 per-row: {nsPerRow:F1} ns (gate {GateCallNs} ns warn-only)");
            if (nsPerRow > GateCallNs)
                Debug.Log($"[VOM6.P02] WARN: {nsPerRow:F1} ns exceeds gate {GateCallNs} ns");
            Assert(true, "VOM6.P02_CallBatchPerf_Recorded");
        }

        // P03: 0 managed allocation across 100 batch invocations.
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            const int N = 16;
            Span<Number> args = stackalloc Number[N];
            Span<Number> rets = stackalloc Number[N];
            for (int i = 0; i < N; i++) args[i] = Number.FromInt(i);
            var plan = new BatchPlan(h, N, args, rets);

            // warmup to JIT and let any first-touch allocations settle.
            for (int w = 0; w < 16; w++) VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int it = 0; it < 100; it++)
                VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
            long after = GC.GetAllocatedBytesForCurrentThread();
            long delta = after - before;
            Debug.Log($"[VOM6.P03] alloc delta over 100 batches: {delta} bytes");
            Assert(delta == 0, "VOM6.P03_ZeroAllocOver100Batches");
        }
    }
}
