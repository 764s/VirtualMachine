using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM6Tests
{
    private static void RunValidationTests()
    {
        // V01: Args length mismatch → BatchPlan ctor throws.
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (_, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            bool threw = false;
            try
            {
                Span<Number> args = stackalloc Number[3]; // expected 4*1=4
                Span<Number> rets = stackalloc Number[4];
                _ = new BatchPlan(h, 4, args, rets);
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM6.V01_ArgsLengthMismatchThrows");
        }

        // V02: Returns length mismatch → BatchPlan ctor throws.
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (_, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            bool threw = false;
            try
            {
                Span<Number> args = stackalloc Number[4];
                Span<Number> rets = stackalloc Number[2]; // expected 4*1=4
                _ = new BatchPlan(h, 4, args, rets);
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM6.V02_ReturnsLengthMismatchThrows");
        }

        // V03: Non-@readonly function under BatchKind.ReadOnlyCall → throws.
        {
            string src = @"
func plain(x: int): int { return x + 1 }
func main(): int { return 0 }
";
            var (world, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("plain");

            Span<Number> args = stackalloc Number[1];
            Span<Number> rets = stackalloc Number[1];
            args[0] = Number.FromInt(7);
            var plan = new BatchPlan(h, 1, args, rets);

            bool threw = false;
            try { VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall); }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM6.V03_NonReadOnlyUnderReadOnlyCallRejected");
        }

        // V04: With Errors sink, a row hitting a runtime violation records error;
        //       without sink, it throws.
        {
            string src = @"
@readonly func boom(): int { Mut() return 7 }
func main(): int { return 0 }
";
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var result = compiler.Compile(src, "main",
                new Dictionary<string, int> { { "Mut", 0 } });
            if (!result.Success) throw new Exception("V04 compile failed");
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);
            var h = result.Program.ResolveMethod("boom");

            // Sub-case A: no sink → throws on first failing row.
            {
                Span<Number> args = stackalloc Number[0];
                Span<Number> rets = stackalloc Number[1];
                var plan = new BatchPlan(h, 1, args, rets);

                bool threw = false;
                try { VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall); }
                catch (ReadOnlyViolationException) { threw = true; }
                catch (VMABIException) { threw = true; }
                Assert(threw, "VOM6.V04a_NoSinkThrowsOnFailure");
            }

            // Sub-case B: with sink → records error, fails count.
            {
                Span<Number> args = stackalloc Number[0];
                Span<Number> rets = stackalloc Number[1];
                Span<VMError> errs = stackalloc VMError[1];
                var plan = new BatchPlan(h, 1, args, rets, errs);

                int fails = VMEngine.Batch(world, 0, plan, BatchKind.ReadOnlyCall);
                Assert(fails == 1, "VOM6.V04b_SinkRecordsFailureCount");
                Assert(errs[0] == VMError.PanicReadOnlyViolation, "VOM6.V04c_SinkRecordsErrorCode");
            }
        }

        // V05: Errors span of wrong length (non-zero, non-Count) → ctor throws.
        {
            string src = @"
@readonly func sq(x: int): int { return x * x }
func main(): int { return 0 }
";
            var (_, prog) = Compile(src, "main");
            var h = prog.ResolveMethod("sq");

            bool threw = false;
            try
            {
                Span<Number> args = stackalloc Number[4];
                Span<Number> rets = stackalloc Number[4];
                Span<VMError> errs = stackalloc VMError[3]; // expected 0 or 4
                _ = new BatchPlan(h, 4, args, rets, errs);
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM6.V05_WrongErrorsLengthThrows");
        }
    }
}
