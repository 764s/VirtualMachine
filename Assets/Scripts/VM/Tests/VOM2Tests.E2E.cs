using System;
using FFVM;

public static partial class VOM2Tests
{
    private static void RunStaticReadOnlyCallTests()
    {
        // E2E01: add(a, b) — entry function, ends with RETURN
        {
            string src = @"
@readonly func add(a: int, b: int): int {
    return a + b
}";
            var (world, prog) = CompileEntry(src, "add");
            var handle = prog.ResolveMethod("add");
            Assert(handle.IsResolved, "VOM2.E2E01a_AddHandleResolved");
            Assert(handle.ParamCount == 2, "VOM2.E2E01b_AddParamCount2");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(3);
            argBuf[1] = Number.FromInt(4);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 7, "VOM2.E2E01c_AddResult");
        }

        // E2E02: mul(a, b)
        {
            string src = @"
@readonly func mul(a: int, b: int): int {
    return a * b
}";
            var (world, prog) = CompileEntry(src, "mul");
            var handle = prog.ResolveMethod("mul");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(6);
            argBuf[1] = Number.FromInt(7);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM2.E2E02_MulResult42");
        }

        // E2E03: zero-arg constant function
        {
            string src = @"
@readonly func meaning(): int {
    return 42
}";
            var (world, prog) = CompileEntry(src, "meaning");
            var handle = prog.ResolveMethod("meaning");
            Assert(handle.ParamCount == 0, "VOM2.E2E03a_ZeroArgParamCount");

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            VMEngine.StaticReadOnlyCall(world, 0, handle, Arguments.Empty, ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM2.E2E03b_MeaningResult42");
        }

        // E2E04: Repeated invocations on same world (transient instance lifecycle)
        {
            string src = @"
@readonly func sq(x: int): int {
    return x * x
}";
            var (world, prog) = CompileEntry(src, "sq");
            var handle = prog.ResolveMethod("sq");

            Span<Number> argBuf = stackalloc Number[1];
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            for (int i = 1; i <= 5; i++)
            {
                argBuf[0] = Number.FromInt(i);
                VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            }
            // Last result is 5*5 = 25
            Assert(ret.Get(0).ToInt() == 25, "VOM2.E2E04_RepeatedCallsLastResult");
        }

        // E2E05: branching control flow inside callee
        {
            string src = @"
@readonly func absv(x: int): int {
    if x < 0 {
        return 0 - x
    }
    return x
}";
            var (world, prog) = CompileEntry(src, "absv");
            var handle = prog.ResolveMethod("absv");

            Span<Number> argBuf = stackalloc Number[1];
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            argBuf[0] = Number.FromInt(-7);
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 7, "VOM2.E2E05a_AbsNegative");

            argBuf[0] = Number.FromInt(13);
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 13, "VOM2.E2E05b_AbsPositive");
        }
    }
}
