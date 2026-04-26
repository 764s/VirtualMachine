using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFVM;
using FFVM.Compiler;
using UnityEngine;
using Debug = UnityEngine.Debug;

/// <summary>
/// VOM11 Lazy Rent Reset — validates that <see cref="TransientInstancePool.Rent"/>
/// no longer pays the wholesale ~1 KB <c>= default;</c> memzero, while VMEngine.Call
/// / ReadOnlyCall / Batch entries still observe a clean control-field state on
/// every invocation (three-layer safety: explicit re-init + MVar safety belt +
/// optional Debug poison).
/// </summary>
public static class VOM11Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) { Debug.Log($"[PASS] {name}"); _passed++; }
        else { Debug.LogError($"[FAIL] {name}"); _failed++; }
    }

    public static void RunAll()
    {
        _passed = 0;
        _failed = 0;

        Test_T1_RegisterResidualNotObserved();
        Test_T2_ReadOnlyViolation_NextCallIsClean();
        Test_T3_BatchContinueOnError_NextRowIsClean();
        Test_T4_RentReturnRoundtripLatency();
        Test_T5_ZeroAllocOver100Calls();

        Debug.Log($"[VOM11Tests] {_passed} passed, {_failed} failed");
        if (_failed > 0)
            throw new Exception($"VOM11Tests failed: {_failed} assertion(s)");
    }

    private static (VMWorld world, VMProgram program) CompileSimple(string src, string entry)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(src, entry, new Dictionary<string, int>());
        if (!result.Success)
            throw new Exception("VOM11 compile failed: " + string.Join("; ", result.Errors));
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        return (world, result.Program);
    }

    /// <summary>
    /// T1: Two consecutive VMEngine.Call invocations against the same handle
    /// reuse the same transient slot (pool capacity stays at 1). Despite no
    /// wholesale memzero in Rent, the second call must observe correct return
    /// value — exercises the explicit control-field re-init path.
    /// </summary>
    private static void Test_T1_RegisterResidualNotObserved()
    {
        var (world, prog) = CompileSimple(
            "func answer(): int { return 42 } func main(): int { return 0 }", "main");
        var h = prog.ResolveMethod("answer");
        Span<Number> retBuf = stackalloc Number[1];

        VMEngine.Call(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf));
        Assert(retBuf[0].ToInt() == 42, "VOM11.T1a_FirstCall_Returns42");

        // Pollute return-buffer slot to ensure second call writes it fresh.
        retBuf[0] = new Number(99);

        VMEngine.Call(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf));
        Assert(retBuf[0].ToInt() == 42, "VOM11.T1b_SecondCall_Returns42_NoResidual");

        Assert(world.TransientPool.Capacity == 4, "VOM11.T1c_PoolNotGrown");
    }

    /// <summary>
    /// T2: After ReadOnly violation throws, the slot is returned (existing VOM3
    /// invariant). The very next VMEngine.Call against the same slot must
    /// succeed cleanly — control-field re-init must override stale ErrorFlag /
    /// IsAlive / StateFlags from the violating call.
    /// </summary>
    private static void Test_T2_ReadOnlyViolation_NextCallIsClean()
    {
        // Two functions in one module: violator (writes a global in @readonly) +
        // a clean function the follow-up call invokes.
        var compiler = new BytecodeCompiler();
        var syscalls = new Dictionary<string, int> { { "Mut", 0 } };
        var result = compiler.Compile(
            "@readonly func violator(): int { Mut() return 0 }\n" +
            "func cleanFn(): int { return 7 }\n" +
            "func main(): int { return 0 }",
            "main", syscalls);
        if (!result.Success)
            throw new Exception("T2 compile failed: " + string.Join("; ", result.Errors));
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);

        var hViol = result.Program.ResolveMethod("violator");
        var hClean = result.Program.ResolveMethod("cleanFn");

        Span<Number> retBuf = stackalloc Number[1];
        bool threw = false;
        try { VMEngine.ReadOnlyCall(world, 0, hViol, Arguments.Empty, new ReturnSlot(retBuf)); }
        catch (ReadOnlyViolationException) { threw = true; }
        Assert(threw, "VOM11.T2a_ViolatorRejected");

        // Follow-up call: same transient pool, slot reused. Must succeed cleanly.
        retBuf[0] = new Number(0);
        VMEngine.Call(world, 0, hClean, Arguments.Empty, new ReturnSlot(retBuf));
        Assert(retBuf[0].ToInt() == 7, "VOM11.T2b_FollowUpCall_Clean");
    }

    /// <summary>
    /// T3: Batch with continue-on-error sees one row fail; subsequent rows in
    /// the same batch (and a follow-up batch) must observe clean per-row state.
    /// </summary>
    private static void Test_T3_BatchContinueOnError_NextRowIsClean()
    {
        // Strategy mirrors VOM6.B03: @readonly fn calls non-readonly syscall on poison row.
        var compiler = new BytecodeCompiler();
        var syscalls = new Dictionary<string, int> { { "Mut", 0 } };
        var result = compiler.Compile(
            "@readonly func cond(x: int): int { if x == 0 { Mut() } return x + 100 }\n" +
            "func main(): int { return 0 }",
            "main", syscalls);
        if (!result.Success)
            throw new Exception("T3 compile failed: " + string.Join("; ", result.Errors));
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);
        var h = result.Program.ResolveMethod("cond");

        const int rows = 4;
        Span<Number> args = stackalloc Number[rows];   // 1 param per row
        Span<Number> rets = stackalloc Number[rows];   // 1 return per row
        Span<VMError> errs = stackalloc VMError[rows];
        args[0] = new Number(1);
        args[1] = new Number(0); // poison row
        args[2] = new Number(3);
        args[3] = new Number(4);

        var plan = new BatchPlan(h, rows, args, rets, errs);

        int failures = VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
        Assert(failures == 1, $"VOM11.T3a_OneFailureRecorded (got {failures})");
        Assert(rets[0].ToInt() == 101, "VOM11.T3b_Row0_OK");
        Assert(errs[1] == VMError.PanicReadOnlyViolation, "VOM11.T3c_Row1_ErrorRecorded");
        Assert(rets[2].ToInt() == 103, "VOM11.T3d_Row2_OK_AfterFailureRow");
        Assert(rets[3].ToInt() == 104, "VOM11.T3e_Row3_OK");
    }

    /// <summary>
    /// T4 (B08-equivalent): Rent + Return roundtrip latency. Post-VOM11, Rent is
    /// pop-from-stack only (no wholesale memzero) → expect ≤ 5 ns. This replaces
    /// the VOM3.P2_F3_TransientResetLatency micro-bench (which measured the
    /// `slot = default;` operation directly, no longer in the hot path).
    /// </summary>
    private static void Test_T4_RentReturnRoundtripLatency()
    {
        var world = new VMWorld();
        var pool = world.TransientPool;

        // T4 design (decision B per VOM11 plan): inner-loop amplification.
        // Single Rent+Return is ~5 ns, well below Stopwatch granularity (~100 ns).
        // We run 1000 Rent+Return pairs per outer iteration so each timed unit is
        // ~5 µs (well above noise floor), then divide back to per-op ns.
        const int Inner = 1000;
        const int Warmup = 100;
        const int Outer = 1000;

        for (int w = 0; w < Warmup; w++)
        {
            for (int i = 0; i < Inner; i++)
            {
                int id = pool.Rent();
                pool.Return(id);
            }
        }

        var sw = Stopwatch.StartNew();
        for (int o = 0; o < Outer; o++)
        {
            for (int i = 0; i < Inner; i++)
            {
                int id = pool.Rent();
                pool.Return(id);
            }
        }
        sw.Stop();
        long totalOps = (long)Outer * Inner;
        double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / totalOps;

        // Per VOM11 §四 (revised baseline): 8.8 → ≤ 5 ns gate (≥44% reduction).
        // Warn-only to align with VOM3 perf-policy (Stopwatch noise on warm runs).
        const double Gate = 5.0;
        if (nsPerOp <= Gate)
        {
            Debug.Log($"[PASS] VOM11.T4_RentReturnRoundtrip | {nsPerOp:F2} ns/op (gate ≤ {Gate} ns, {totalOps} ops)");
            _passed++;
        }
        else
        {
            Debug.Log($"[PASS-WARN] VOM11.T4_RentReturnRoundtrip | {nsPerOp:F2} ns/op exceeds gate {Gate} ns (warn-only, {totalOps} ops)");
            _passed++;
        }
    }

    /// <summary>
    /// T5: 100 consecutive VMEngine.Call invocations allocate 0 bytes
    /// (VOM6.P03 invariant maintained post-A.1).
    /// </summary>
    private static void Test_T5_ZeroAllocOver100Calls()
    {
        var (world, prog) = CompileSimple(
            "func answer(): int { return 42 } func main(): int { return 0 }", "main");
        var h = prog.ResolveMethod("answer");

        // Warmup
        Span<Number> warmupBuf = stackalloc Number[1];
        for (int i = 0; i < 32; i++)
            VMEngine.Call(world, 0, h, Arguments.Empty, new ReturnSlot(warmupBuf));

        long before = GC.GetAllocatedBytesForCurrentThread();
        Span<Number> retBuf = stackalloc Number[1];
        for (int i = 0; i < 100; i++)
            VMEngine.Call(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf));
        long after = GC.GetAllocatedBytesForCurrentThread();
        long delta = after - before;

        Assert(delta == 0, $"VOM11.T5_ZeroAllocOver100Calls (delta={delta})");
    }
}
