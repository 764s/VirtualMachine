using System;
using FFVM;
using UnityEngine;

public static partial class VOM4Tests
{
    private static void RunBasicTests()
    {
        // B01: YieldCall on non-yielding function — completes in single Tick.
        {
            string src = @"
func main(): int { return 0 }
func add(a: int, b: int): int { return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("add");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(7); argBuf[1] = Number.FromInt(35);
            var yh = VMEngine.YieldCall(world, 0, handle, new Arguments(argBuf));
            Assert(yh.IsValid(world), "VOM4.B01a_HandleValid");
            Assert(!yh.IsCompleted(world), "VOM4.B01b_NotCompletedBeforeTick");

            world.Tick();
            Assert(yh.IsCompleted(world), "VOM4.B01c_CompletedAfterTick");
            Assert(!yh.HasError(world), "VOM4.B01d_NoError");

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            yh.ReadReturn(world, ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM4.B01e_ReturnValue");

            yh.Release(world);
            Assert(!yh.IsValid(world), "VOM4.B01f_InvalidAfterRelease");
        }

        // B02: single yield → needs two TickOnce calls (yield = WAIT 1).
        {
            string src = @"
func main(): int { return 0 }
func two_step(): int {
    var x: int = 11
    yield
    return x + 31
}
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("two_step");
            var yh = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);

            yh.TickOnce(world); // executes up to yield → WAIT 1 set
            Assert(!yh.IsCompleted(world), "VOM4.B02a_NotCompletedAfterFirstTick");
            yh.TickOnce(world); // counts down WAIT
            yh.TickOnce(world); // resumes & runs to RET
            Assert(yh.IsCompleted(world), "VOM4.B02b_CompletedAfterThreeTicks");

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            yh.ReadReturn(world, ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM4.B02c_ReturnValue");
            yh.Release(world);
        }

        // B03: world.Tick() drives yielding instance to completion across frames.
        {
            string src = @"
func main(): int { return 0 }
func loop3(): int {
    var i: int = 0
    while i < 3 { i = i + 1; yield }
    return i
}
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("loop3");
            var yh = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);

            int ticks = 0;
            while (!yh.IsCompleted(world) && ticks < 20)
            {
                world.Tick();
                ticks++;
            }
            Assert(yh.IsCompleted(world), $"VOM4.B03a_CompletesViaWorldTick (ticks={ticks})");

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            yh.ReadReturn(world, ret);
            Assert(ret.Get(0).ToInt() == 3, "VOM4.B03b_LoopReturnValue");
            yh.Release(world);
        }

        // B04: multiple concurrent YieldCall — independent state.
        {
            string src = @"
func main(): int { return 0 }
func plus(a: int, b: int): int { yield; return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("plus");

            Span<Number> a1 = stackalloc Number[2]; a1[0] = Number.FromInt(1); a1[1] = Number.FromInt(2);
            Span<Number> a2 = stackalloc Number[2]; a2[0] = Number.FromInt(10); a2[1] = Number.FromInt(20);
            Span<Number> a3 = stackalloc Number[2]; a3[0] = Number.FromInt(100); a3[1] = Number.FromInt(200);

            var h1 = VMEngine.YieldCall(world, 0, handle, new Arguments(a1));
            var h2 = VMEngine.YieldCall(world, 0, handle, new Arguments(a2));
            var h3 = VMEngine.YieldCall(world, 0, handle, new Arguments(a3));

            Assert(h1.InstanceId != h2.InstanceId && h2.InstanceId != h3.InstanceId,
                "VOM4.B04a_DistinctInstances");

            for (int t = 0; t < 5; t++) world.Tick();

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            h1.ReadReturn(world, ret); Assert(ret.Get(0).ToInt() == 3, "VOM4.B04b_R1");
            h2.ReadReturn(world, ret); Assert(ret.Get(0).ToInt() == 30, "VOM4.B04c_R2");
            h3.ReadReturn(world, ret); Assert(ret.Get(0).ToInt() == 300, "VOM4.B04d_R3");

            h1.Release(world); h2.Release(world); h3.Release(world);
        }
    }
}
