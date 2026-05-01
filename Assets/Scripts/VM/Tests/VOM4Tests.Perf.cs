using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFVM;
using FFVM.Compiler;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// VOM4 yield-call perf gates B09/B10. Warn-only (StandaloneRunner exit code
/// is not affected by perf misses); regressions surface as PASS-WARN.
/// </summary>
public static partial class VOM4Tests
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
            _passed++;
            TestHarness.Assert(true, $"{name} | {nsPerOp:F1} ns/op (gate ≤ {gateNs} ns)");
        }
        else
        {
            Debug.Log($"[PASS-WARN] {name} | {nsPerOp:F1} ns/op exceeds gate {gateNs} ns (warn-only)");
            _passed++;
            TestHarness.RecordPass();
        }
    }

    private static void RunPerfTests()
    {
        const int Warmup = 1000;
        const int Measure = 50_000;

        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(
            "func main(): int { return 0 } " +
            "func quick(): int { return 7 } " +
            "func once(): int { yield; return 7 }",
            "main",
            new Dictionary<string, int>());
        if (!result.Success)
            throw new Exception("VOM4 perf compile failed: " + string.Join("; ", result.Errors));

        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        var quickH = result.Program.ResolveMethod("quick");
        var onceH = result.Program.ResolveMethod("once");

        // B09: YieldCall + single Tick + ReadReturn + Release on a non-yielding fn.
        {
            double ns = MeasureNsPerOp(Warmup, Measure, () =>
            {
                var yh = VMEngine.YieldCall(world, 0, quickH, Arguments.Empty);
                world.Tick();
                Span<Number> retBuf = stackalloc Number[1];
                yh.ReadReturn(world, new ReturnSlot(retBuf));
                yh.Release(world);
            });
            AssertPerfWarn(ns, 100.0, "VOM4.B09_YieldCallNoYieldRoundtrip");
        }

        // B10: YieldCall + 2× TickOnce + ReadReturn + Release on a single-yield fn.
        {
            double ns = MeasureNsPerOp(Warmup, Measure, () =>
            {
                var yh = VMEngine.YieldCall(world, 0, onceH, Arguments.Empty);
                yh.TickOnce(world); // executes up to yield
                yh.TickOnce(world); // wait counter decrements
                yh.TickOnce(world); // resumes and completes
                Span<Number> retBuf = stackalloc Number[1];
                yh.ReadReturn(world, new ReturnSlot(retBuf));
                yh.Release(world);
            });
            AssertPerfWarn(ns, 230.0, "VOM4.B10_YieldCallSingleYieldRoundtrip");
        }
    }
}
