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

        RunBenchmark("B01_ArithLoop",      B01_Script, B01_CSharp,      10000);
        RunBenchmark("B02_Fibonacci",       B02_Script, B02_CSharp,      250);
        RunBenchmark("B03_NestedLoop",      B03_Script, B03_CSharp,      100);
        RunBenchmark("B04_Branching",       B04_Script, B04_CSharp,      10000);
        RunBenchmark("B05_Accumulator",     B05_Script, B05_CSharp,      50000);
        RunBenchmark("B06_FuncCall",        B06_Script, B06_CSharp,      5000);

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
    }

    // ========================================================================
    //  B01: Arithmetic Loop — sum + multiply + modulo + branch
    //  Same as V3 benchmark but in script form
    // ========================================================================

    static BenchCase B01_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var i: int = 0
    var limit: int = {n}
    var acc: int = 0
    var divisor: int = 3
    while (i < limit) {{
        acc = acc + i
        var temp: int = i * 1
        temp = temp - 1
        acc = acc + temp
        if (i % divisor == 0) {{
            Noop()
        }}
        i = i + 1
    }}
    Result(acc)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Noop", 0 }, { "Result", 1 } },
        RegisterSyscalls = (s, cb) =>
        {
            s.Register(0, "Noop", (ref VMInstanceState _) => { });
            s.Register(1, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt()));
        },
        MaxSteps = n * 50,
    };

    static int B01_CSharp(int n)
    {
        Number limit = Number.FromInt(n);
        Number step = Number.FromInt(1);
        Number divisor = Number.FromInt(3);
        Number i = Number.Zero;
        Number acc = Number.Zero;
        int sc = 0;
        while (i < limit)
        {
            acc = acc + i;
            Number temp = i * step;
            temp = temp - step;
            acc = acc + temp;
            if (i % divisor == Number.Zero) sc++;
            i = i + step;
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
        Number a = Number.Zero;
        Number b = Number.One;
        Number step = Number.One;
        Number i = Number.Zero;
        Number limit = Number.FromInt(n);
        while (i < limit)
        {
            Number temp = b;
            b = a + b;
            a = temp;
            i = i + step;
        }
        return a.ToInt();
    }

    // ========================================================================
    //  B03: Nested Loop — O(n^2) iterations with inner accumulator
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
            acc = acc + i * j
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
        Number limit = Number.FromInt(n);
        Number step = Number.One;
        Number acc = Number.Zero;
        Number i = Number.Zero;
        while (i < limit)
        {
            Number j = Number.Zero;
            while (j < limit)
            {
                acc = acc + i * j;
                j = j + step;
            }
            i = i + step;
        }
        return acc.ToInt();
    }

    // ========================================================================
    //  B04: Heavy Branching — if/else chain every iteration
    // ========================================================================

    static BenchCase B04_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var count: int = 0
    var i: int = 0
    while (i < {n}) {{
        var m: int = i % 4
        if (m == 0) {{
            count = count + 1
        }} else if (m == 1) {{
            count = count + 2
        }} else if (m == 2) {{
            count = count + 3
        }} else {{
            count = count + 4
        }}
        i = i + 1
    }}
    Result(count)
}}",
        Entry = "main",
        Syscalls = new Dictionary<string, int> { { "Result", 0 } },
        RegisterSyscalls = (s, cb) =>
            s.Register(0, "Result", (ref VMInstanceState st) => cb(st.Registers.Get(0).ToInt())),
        MaxSteps = n * 50,
    };

    static int B04_CSharp(int n)
    {
        Number limit = Number.FromInt(n);
        Number step = Number.One;
        Number four = Number.FromInt(4);
        Number count = Number.Zero;
        Number i = Number.Zero;
        Number n0 = Number.Zero, n1 = Number.One, n2 = Number.FromInt(2);
        Number n3 = Number.FromInt(3), n4 = Number.FromInt(4);
        while (i < limit)
        {
            Number m = i % four;
            if (m == n0) count = count + n1;
            else if (m == n1) count = count + n2;
            else if (m == n2) count = count + n3;
            else count = count + n4;
            i = i + step;
        }
        return count.ToInt();
    }

    // ========================================================================
    //  B05: Simple Accumulator — minimal overhead, pure ADD loop
    //  NOTE: B05 is intentionally minimal (only ADD + compare + jump).
    //  The VM/C# ratio is amplified because C# JIT optimizes the tight
    //  loop aggressively while the VM pays per-instruction dispatch overhead
    //  on every iteration.  This makes B05 a worst-case dispatch-overhead
    //  indicator rather than a typical workload benchmark.
    // ========================================================================

    static BenchCase B05_Script(int n) => new BenchCase
    {
        Script = $@"
func main() {{
    var sum: int = 0
    var i: int = 0
    while (i < {n}) {{
        sum = sum + i
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
        Number limit = Number.FromInt(n);
        Number step = Number.One;
        Number sum = Number.Zero;
        Number i = Number.Zero;
        while (i < limit)
        {
            sum = sum + i;
            i = i + step;
        }
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
        Number limit = Number.FromInt(n);
        Number step = Number.One;
        Number sum = Number.Zero;
        Number i = Number.Zero;
        while (i < limit)
        {
            sum = sum + B06_Helper(i);
            i = i + step;
        }
        return sum.ToInt();
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static Number B06_Helper(Number x) => x + Number.One;
}
