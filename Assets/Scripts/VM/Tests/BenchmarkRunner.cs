using System;
using System.Collections.Generic;
using System.Diagnostics;
using FFVM;
using FFVM.Compiler;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Automated VM vs C# benchmark runner.
/// Each benchmark defines IDENTICAL logic in both FFVM script and C# code,
/// runs both with Stopwatch, and emits structured output for collection.
///
/// Output format (machine-parseable):
///   [BENCHMARK] name | vm_us | cs_us | ratio | iters | instrs
///
/// Run from Unity: TestVM → RunBenchmarks
/// Run from CLI:   dotnet run --project StandaloneRunner -- --bench
/// </summary>
public static class BenchmarkRunner
{
    // ── configuration ──────────────────────────────────────────────
    const int WarmupRuns = 100;
    const int MeasureRuns = 200;

    static void Log(string msg) => UnityEngine.Debug.Log(msg);
    static void LogError(string msg) => UnityEngine.Debug.LogError(msg);

    // ── entry point ────────────────────────────────────────────────
    [MenuItem("TestVM/RunBenchmarks")]
    public static void RunAll()
    {
        Log("[BENCHMARK_START]");
        Log($"[BENCHMARK_ENV] runtime={Environment.Version} os={Environment.OSVersion} " +
            $"cores={Environment.ProcessorCount} warmup={WarmupRuns} runs={MeasureRuns}");

        _currentRawFn = B01_CSharpRaw; RunBenchmark("B01_ArithLoop",      B01_Script, B01_CSharp,      10000);
        _currentRawFn = B02_CSharpRaw; RunBenchmark("B02_Fibonacci",       B02_Script, B02_CSharp,      46);
        _currentRawFn = B03_CSharpRaw; RunBenchmark("B03_NestedLoop",      B03_Script, B03_CSharp,      100);
        _currentRawFn = B04_CSharpRaw; RunBenchmark("B04_Branching",       B04_Script, B04_CSharp,      10000);
        _currentRawFn = B05_CSharpRaw; RunBenchmark("B05_Accumulator",     B05_Script, B05_CSharp,      50000);
        _currentRawFn = B06_CSharpRaw; RunBenchmark("B06_FuncCall",        B06_Script, B06_CSharp,      5000);

        Log("[BENCHMARK_END]");
    }

    // ── harness ────────────────────────────────────────────────────

    struct BenchCase
    {
        public string Script;
        public string Entry;
        public Dictionary<string, int> Syscalls;
        public Action<SyscallTable, Action<int>> RegisterSyscalls;
        public int MaxSteps;
    }

    delegate BenchCase ScriptFactory(int scale);
    delegate int CSharpFunc(int scale);
    delegate double CSharpRawFunc(int scale);

    static CSharpRawFunc _currentRawFn;

    static void RunBenchmark(string name, ScriptFactory scriptFn, CSharpFunc csharpFn, int scale)
    {
        // ── compile & verify ──
        var bc = scriptFn(scale);
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(bc.Script, bc.Entry, bc.Syscalls);
        if (!result.Success)
        {
            LogError($"[BENCHMARK] {name} | COMPILE_ERROR | {string.Join("; ", result.Errors)}");
            return;
        }

        // verify via Result syscall
        int vmResult = 0;
        var world = new VMWorld();
        if (bc.MaxSteps > 0) world.MaxStepsPerTick = bc.MaxSteps;
        world.Modules.Load(0, result.Program);
        bc.RegisterSyscalls(world.Syscalls, v => vmResult = v);
        int iid = world.SpawnInstance(0, 0);
        world.Tick();
        int csResult = csharpFn(scale);
        world.DestroyInstance(iid);

        if (vmResult != csResult)
        {
            LogError($"[BENCHMARK] {name} | MISMATCH | vm={vmResult} cs={csResult}");
            return;
        }

        int instrCount = result.Program.InstructionCount;

        // ── warmup VM ──
        for (int w = 0; w < WarmupRuns; w++)
        {
            int wid = world.SpawnInstance(0, 0);
            world.Tick();
            world.DestroyInstance(wid);
        }

        // ── measure VM ──
        var sw = Stopwatch.StartNew();
        for (int r = 0; r < MeasureRuns; r++)
        {
            int mid = world.SpawnInstance(0, 0);
            world.Tick();
            world.DestroyInstance(mid);
        }
        sw.Stop();
        double vmUs = (sw.Elapsed.TotalMilliseconds / MeasureRuns) * 1000.0;

        // ── warmup C# ──
        for (int w = 0; w < WarmupRuns; w++)
            csharpFn(scale);

        // ── measure C# ──
        sw.Restart();
        for (int r = 0; r < MeasureRuns; r++)
            csharpFn(r == 0 ? scale : scale); // prevent dead-code elimination
        sw.Stop();
        double csUs = (sw.Elapsed.TotalMilliseconds / MeasureRuns) * 1000.0;

        double ratio = csUs > 0 ? vmUs / csUs : 0;

        Log($"[BENCHMARK] {name} | {vmUs:F1} | {csUs:F1} | {ratio:F2} | {scale} | {instrCount}");

        // ── C# raw baseline ──
        if (_currentRawFn != null)
        {
            var rawFn = _currentRawFn;
            for (int w = 0; w < WarmupRuns; w++)
                rawFn(scale);
            sw.Restart();
            for (int r = 0; r < MeasureRuns; r++)
                rawFn(r == 0 ? scale : scale);
            sw.Stop();
            double rawUs = (sw.Elapsed.TotalMilliseconds / MeasureRuns) * 1000.0;
            Log($"[BENCHMARK_RAW] {name} | {rawUs:F1}");
        }
    }

    // ========================================================================
    //  B01: ArithLoop — int loop, float arithmetic (add/mul/sub)
    // ========================================================================

    static BenchCase B01_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var i: int = 0
    var limit: int = {n}
    var acc: int = 0
    while (i < limit) {{
        var x: int = i + 0.5
        acc = acc + x
        var temp: int = x * 2.0
        temp = temp - 1.0
        acc = acc + temp
        i = i + 1
    }}
    Result(acc)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 30,
    };

    static int B01_CSharp(int n)
    {
        Number half = Number.Half;
        Number two = Number.FromInt(2);
        Number one = Number.One;
        Number acc = Number.Zero;
        for (int i = 0; i < n; i++)
        {
            Number x = Number.FromInt(i) + half;
            acc = acc + x;
            Number temp = x * two;
            temp = temp - one;
            acc = acc + temp;
        }
        return acc.ToInt();
    }

    // ========================================================================
    //  B02: Fibonacci — iterative fib(N): a=0, b=1, swap N times
    // ========================================================================

    static BenchCase B02_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var a: int = 0
    var b: int = 1
    var i: int = 0
    while (i < {n}) {{
        var temp: int = b
        b = a + b
        a = temp
        i = i + 1
    }}
    Result(a)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 20,
    };

    static int B02_CSharp(int n)
    {
        int a = 0, b = 1;
        for (int i = 0; i < n; i++)
        {
            int temp = b;
            b = a + b;
            a = temp;
        }
        return a;
    }

    // ========================================================================
    //  B03: NestedLoop — int loops, float multiply-accumulate
    // ========================================================================

    static BenchCase B03_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var acc: int = 0
    var i: int = 0
    while (i < {n}) {{
        var j: int = 0
        while (j < {n}) {{
            acc = acc + (i + 0.5) * (j + 0.5)
            j = j + 1
        }}
        i = i + 1
    }}
    Result(acc)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * n * 20,
    };

    static int B03_CSharp(int n)
    {
        Number half = Number.Half;
        Number acc = Number.Zero;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                acc = acc + (Number.FromInt(i) + half) * (Number.FromInt(j) + half);
        return acc.ToInt();
    }

    // ========================================================================
    //  B04: Branching — int loop+branch, float accumulate
    // ========================================================================

    static BenchCase B04_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var acc: int = 0
    var i: int = 0
    while (i < {n}) {{
        var x: int = i * 0.5
        var m: int = i % 4
        if (m == 0) {{
            acc = acc + x
        }} else if (m == 1) {{
            acc = acc + x * 2.0
        }} else if (m == 2) {{
            acc = acc + x * 0.5
        }} else {{
            acc = acc + x * 4.0
        }}
        i = i + 1
    }}
    Result(acc)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 50,
    };

    static int B04_CSharp(int n)
    {
        Number half = Number.Half;
        Number two = Number.FromInt(2);
        Number four = Number.FromInt(4);
        Number acc = Number.Zero;
        for (int i = 0; i < n; i++)
        {
            Number x = Number.FromInt(i) * half;
            int m = i % 4;
            if (m == 0) acc = acc + x;
            else if (m == 1) acc = acc + x * two;
            else if (m == 2) acc = acc + x * half;
            else acc = acc + x * four;
        }
        return acc.ToInt();
    }

    // ========================================================================
    //  B05: Accumulator — int loop, float sum (i * 0.5)
    // ========================================================================

    static BenchCase B05_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var sum: int = 0
    var i: int = 0
    while (i < {n}) {{
        sum = sum + i * 0.5
        i = i + 1
    }}
    Result(sum)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 10,
    };

    static int B05_CSharp(int n)
    {
        Number half = Number.Half;
        Number sum = Number.Zero;
        for (int i = 0; i < n; i++)
            sum = sum + Number.FromInt(i) * half;
        return sum.ToInt();
    }

    // ========================================================================
    //  B06: Function Call Intensive — measures CALL/RET overhead
    //  Calls a helper function on every loop iteration. The compiler may
    //  optimize the helper as a leaf function (CALL_LEAF/RET_LEAF).
    // ========================================================================

    static BenchCase B06_Script(int n) => new BenchCase
    {
        Script = $@"
func add_one(x: int): int {{
    return x + 1
}}

func main() {{
    var sum: int = 0
    var i: int = 0
    while (i < {n}) {{
        sum = sum + add_one(i)
        i = i + 1
    }}
    Result(sum)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 30,
    };

    static int B06_CSharp(int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
            sum += B06_RawHelper(i);
        return sum;
    }

    // ========================================================================
    //  C# Raw baselines — native int (loop) + double (computation)
    //  B02/B06: pure int. B01/B03/B04/B05: int loop + double arithmetic.
    // ========================================================================

    static double B01_CSharpRaw(int n)
    {
        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            double x = i + 0.5;
            acc += x;
            double temp = x * 2.0;
            temp -= 1.0;
            acc += temp;
        }
        return acc;
    }

    static double B02_CSharpRaw(int n)
    {
        int a = 0, b = 1;
        for (int i = 0; i < n; i++)
        {
            int temp = b;
            b = a + b;
            a = temp;
        }
        return a;
    }

    static double B03_CSharpRaw(int n)
    {
        double acc = 0.0;
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                acc += (i + 0.5) * (j + 0.5);
        return acc;
    }

    static double B04_CSharpRaw(int n)
    {
        double acc = 0.0;
        for (int i = 0; i < n; i++)
        {
            double x = i * 0.5;
            int m = i % 4;
            if (m == 0) acc += x;
            else if (m == 1) acc += x * 2.0;
            else if (m == 2) acc += x * 0.5;
            else acc += x * 4.0;
        }
        return acc;
    }

    static double B05_CSharpRaw(int n)
    {
        double sum = 0.0;
        for (int i = 0; i < n; i++)
            sum += i * 0.5;
        return sum;
    }

    static double B06_CSharpRaw(int n)
    {
        int sum = 0;
        for (int i = 0; i < n; i++)
            sum += B06_RawHelper(i);
        return sum;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static int B06_RawHelper(int x) => x + 1;
}
