using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFVM;
using FFVM.Compiler;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// VOM3 Phase2 perf gates F.T1-T3. Per user policy these are warning-only
/// (StandaloneRunner exit code is not affected by perf misses); regressions
/// surface as PASS-with-WARN log lines so trends remain visible.
/// </summary>
public static partial class VOM3Tests
{
    private static double MeasureNsPerOp(int warmup, int measure, Action op)
    {
        for (int i = 0; i < warmup; i++) op();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < measure; i++) op();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / measure;
    }

    private static void AssertPerfWarn(double nsPerOp, double gateNs, string name)
    {
        if (nsPerOp <= gateNs)
        {
            Debug.Log($"[PASS] {name} | {nsPerOp:F1} ns/op (gate ≤ {gateNs} ns)");
            _passed++;
        }
        else
        {
            Debug.Log($"[PASS-WARN] {name} | {nsPerOp:F1} ns/op exceeds gate {gateNs} ns (warn-only per Phase2 policy)");
            _passed++; // warn-only: do not fail
        }
    }

    private static void RunPhase2PerfTests()
    {
        const int Warmup = 1000;
        const int Measure = 100_000;

        // Shared state across F.T1/F.T2: a tiny @readonly fn returning 0 for the RO path,
        // and a tiny non-readonly fn for the plain Call path.
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(
            "@readonly func ro(): int { return 0 } func plain(): int { return 0 } func main(): int { return 0 }",
            "main",
            new Dictionary<string, int>());
        if (!result.Success)
            throw new Exception("Phase2 perf compile failed: " + string.Join("; ", result.Errors));

        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        var roHandle = result.Program.ResolveMethod("ro");
        var plainHandle = result.Program.ResolveMethod("plain");

        // F.T1: VMEngine.Call latency ≤ 51 ns.
        {
            double ns = MeasureNsPerOp(Warmup, Measure, () =>
            {
                Span<Number> retBuf = stackalloc Number[1];
                VMEngine.Call(world, 0, plainHandle, Arguments.Empty, new ReturnSlot(retBuf));
            });
            AssertPerfWarn(ns, 51.0, "VOM3.P2_F1_CallLatency");
        }

        // F.T2: VMEngine.ReadOnlyCall latency ≤ 41 ns.
        {
            double ns = MeasureNsPerOp(Warmup, Measure, () =>
            {
                Span<Number> retBuf = stackalloc Number[1];
                VMEngine.ReadOnlyCall(world, 0, roHandle, Arguments.Empty, new ReturnSlot(retBuf));
            });
            AssertPerfWarn(ns, 41.0, "VOM3.P2_F2_ReadOnlyCallLatency");
        }

        // F.T3: TransientInstancePool slot reset (`inst = default;`) ≤ 15 ns.
        // Direct micro-bench of the Reset path used by Rent: `inst = default;` on a
        // VMInstanceState-sized struct (~1 KB). Phase2 decision tree: if this exceeds
        // the gate we'd start Sub-task D (selective HWM-driven Reset).
        {
            VMInstanceState slot = default;
            double ns = MeasureNsPerOp(Warmup, Measure, () =>
            {
                slot = default;
            });
            // Avoid dead-code elimination
            if (slot.IP == int.MinValue) Debug.Log("unreachable sink");
            AssertPerfWarn(ns, 15.0, "VOM3.P2_F3_TransientResetLatency");
        }
    }
}
