using System;
using FFVM;
using UnityEngine;

public static partial class VOM3Tests
{
    private static void RunNonEntryTests()
    {
        // N01: ReadOnlyCall on a non-entry function (RET_FUNC sentinel path).
        {
            string src = @"
func main(): int { return 0 }
@readonly func add(a: int, b: int): int { return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("add");
            Assert(handle.IsResolved, "VOM3.N01a_NonEntryHandleResolved");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(11); argBuf[1] = Number.FromInt(31);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            VMEngine.ReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM3.N01b_NonEntryReadOnlyCallResult");
        }

        // N02: zero-arg non-entry function.
        {
            string src = @"
func main(): int { return 0 }
@readonly func answer(): int { return 42 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("answer");
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            VMEngine.ReadOnlyCall(world, 0, handle, Arguments.Empty, ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM3.N02_NonEntryZeroArg");
        }

        // N03: branching control flow inside non-entry function.
        {
            string src = @"
func main(): int { return 0 }
@readonly func absv(x: int): int { if x < 0 { return 0 - x } return x }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("absv");

            Span<Number> argBuf = stackalloc Number[1];
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            argBuf[0] = Number.FromInt(-13);
            VMEngine.ReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 13, "VOM3.N03a_NonEntryBranchNeg");
            argBuf[0] = Number.FromInt(7);
            VMEngine.ReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 7, "VOM3.N03b_NonEntryBranchPos");
        }

        // N04: chained calls — non-entry calls another non-entry.
        {
            string src = @"
func main(): int { return 0 }
@readonly func square(x: int): int { return x * x }
@readonly func sum_of_squares(a: int, b: int): int { return square(a) + square(b) }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("sum_of_squares");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(3); argBuf[1] = Number.FromInt(4);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            VMEngine.ReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            // 3*3 + 4*4 = 25
            Assert(ret.Get(0).ToInt() == 25, "VOM3.N04_NonEntryChainedCalls");
        }

        // N05: StaticReadOnlyCall (alias) still works on non-entry function.
        {
            string src = @"
func main(): int { return 0 }
@readonly func neg(x: int): int { return 0 - x }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("neg");
            Span<Number> argBuf = stackalloc Number[1];
            argBuf[0] = Number.FromInt(99);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == -99, "VOM3.N05_StaticReadOnlyAliasNonEntry");
        }
    }
}
