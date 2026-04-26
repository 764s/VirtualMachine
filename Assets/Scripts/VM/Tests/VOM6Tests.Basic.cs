using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM6Tests
{
    private static void RunBasicTests()
    {
        // B01: ReadOnly batch=4, distinct args → distinct returns; 0 failures.
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            const int N = 4;
            Span<Number> args = stackalloc Number[N * 1];
            Span<Number> rets = stackalloc Number[N * 1];
            for (int i = 0; i < N; i++) args[i] = Number.FromInt(i + 1); // 1,2,3,4

            var plan = new BatchPlan(h, N, args, rets);
            int fails = VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
            Assert(fails == 0, "VOM6.B01a_NoFailures");
            Assert(rets[0].ToInt() == 1, "VOM6.B01b_Row0");
            Assert(rets[1].ToInt() == 4, "VOM6.B01c_Row1");
            Assert(rets[2].ToInt() == 9, "VOM6.B01d_Row2");
            Assert(rets[3].ToInt() == 16, "VOM6.B01e_Row3");
        }

        // B02: Call (unrestricted) batch=3 on a 2-arg function — independent rows.
        {
            string src = @"
func add(a: int, b: int): int { return a + b }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("add");

            const int N = 3;
            Span<Number> args = stackalloc Number[N * 2];
            Span<Number> rets = stackalloc Number[N * 1];
            args[0] = Number.FromInt(10); args[1] = Number.FromInt(1);
            args[2] = Number.FromInt(20); args[3] = Number.FromInt(2);
            args[4] = Number.FromInt(30); args[5] = Number.FromInt(3);

            var plan = new BatchPlan(h, N, args, rets);
            int fails = VMEngine.Batch(world, 0, plan, BatchKind.Call);
            Assert(fails == 0, "VOM6.B02a_NoFailures");
            Assert(rets[0].ToInt() == 11, "VOM6.B02b_Row0");
            Assert(rets[1].ToInt() == 22, "VOM6.B02c_Row1");
            Assert(rets[2].ToInt() == 33, "VOM6.B02d_Row2");
        }

        // B03: continue-on-error — row 1 triggers ReadOnlyViolation via Mut(); rows 0,2 ok.
        // Strategy: @readonly fn conditionally calls a non-readonly syscall when x==0.
        {
            string src = @"
@readonly func cond(x: int): int { if x == 0 { Mut() } return x + 100 }
func main(): int { return 0 }
";
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var result = compiler.Compile(src, "main",
                new Dictionary<string, int> { { "Mut", 0 } });
            if (!result.Success) throw new Exception("B03 compile failed");
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);
            var h = result.Program.ResolveMethod("cond");

            const int N = 3;
            Span<Number> args = stackalloc Number[N];
            Span<Number> rets = stackalloc Number[N];
            Span<VMError> errs = stackalloc VMError[N];
            args[0] = Number.FromInt(1);
            args[1] = Number.FromInt(0); // boom
            args[2] = Number.FromInt(2);

            var plan = new BatchPlan(h, N, args, rets, errs);
            int fails = VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
            Assert(fails == 1, "VOM6.B03a_OneFailure");
            Assert(errs[0] == VMError.None, "VOM6.B03b_Row0Ok");
            Assert(errs[1] == VMError.PanicReadOnlyViolation, "VOM6.B03c_Row1Violation");
            Assert(errs[2] == VMError.None, "VOM6.B03d_Row2Ok");
            Assert(rets[0].ToInt() == 101, "VOM6.B03e_Row0Result");
            Assert(rets[1].ToInt() == 0, "VOM6.B03f_Row1Zeroed");
            Assert(rets[2].ToInt() == 102, "VOM6.B03g_Row2Result");
        }

        // B04: Count == 0 is a no-op (no Rent/Return cost; returns 0).
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            var plan = new BatchPlan(h, 0, ReadOnlySpan<Number>.Empty, Span<Number>.Empty);
            int fails = VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
            Assert(fails == 0, "VOM6.B04a_EmptyBatchNoOp");
        }
    }
}
