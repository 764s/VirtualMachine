using System;
using FFVM;
using UnityEngine;

public static partial class VOM4Tests
{
    private static void RunHandleTests()
    {
        // H01: Release invalidates handle.
        {
            string src = @"
func main(): int { return 0 }
func quick(): int { return 7 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("quick");
            var yh = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);
            world.Tick();
            yh.Release(world);
            Assert(!yh.IsValid(world), "VOM4.H01a_InvalidAfterRelease");
            Assert(!yh.IsCompleted(world), "VOM4.H01b_IsCompletedFalseAfterRelease");
        }

        // H02: Generation defeats ABA — Release + new YieldCall on same slot
        // produces a new handle; the old one stays invalid.
        {
            string src = @"
func main(): int { return 0 }
func quick(): int { return 7 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("quick");

            var yhOld = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);
            int oldId = yhOld.InstanceId; int oldGen = yhOld.Generation;
            world.Tick();
            yhOld.Release(world);

            var yhNew = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);
            Assert(yhNew.InstanceId == oldId, "VOM4.H02a_SameSlotReused");
            Assert(yhNew.Generation == oldGen + 1, "VOM4.H02b_GenerationBumped");
            Assert(!yhOld.IsValid(world), "VOM4.H02c_OldHandleStillInvalid");
            Assert(yhNew.IsValid(world), "VOM4.H02d_NewHandleValid");

            world.Tick();
            yhNew.Release(world);
        }

        // H03: Invalid (default) handle is rejected by all read paths.
        {
            string src = @"
func main(): int { return 0 }
";
            var (world, _) = Compile(src, "main");
            var yh = YieldHandle.Invalid;

            Assert(!yh.IsValid(world), "VOM4.H03a_DefaultIsInvalid");

            bool threw = false;
            try
            {
                Span<Number> retBuf = stackalloc Number[1];
                yh.ReadReturn(world, new ReturnSlot(retBuf));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM4.H03b_ReadReturnRejectsInvalid");

            threw = false;
            try { yh.TickOnce(world); }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM4.H03c_TickOnceRejectsInvalid");

            // Release on invalid handle is silent (idempotent).
            yh.Release(world);
            Assert(true, "VOM4.H03d_ReleaseIdempotentOnInvalid");
        }

        // H04: ReadReturn before completion throws.
        {
            string src = @"
func main(): int { return 0 }
func slow(): int { yield; return 42 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("slow");
            var yh = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);

            // Drive once — function should yield, NOT complete.
            yh.TickOnce(world);
            Assert(!yh.IsCompleted(world), "VOM4.H04a_NotCompletedYet");

            bool threw = false;
            try
            {
                Span<Number> retBuf = stackalloc Number[1];
                yh.ReadReturn(world, new ReturnSlot(retBuf));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM4.H04b_ReadReturnRejectsBeforeCompletion");

            yh.Release(world);
        }

        // H05: argument count mismatch → VMABIException, no slot leak.
        {
            string src = @"
func main(): int { return 0 }
func need_two(a: int, b: int): int { return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("need_two");
            int activeBefore = world.Pool.ActiveListCount;

            bool threw = false;
            try
            {
                Span<Number> wrong = stackalloc Number[1]; wrong[0] = Number.FromInt(5);
                VMEngine.YieldCall(world, 0, handle, new Arguments(wrong));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM4.H05a_ArgCountMismatchThrows");
            Assert(world.Pool.ActiveListCount == activeBefore, "VOM4.H05b_NoSlotLeakOnArgError");
        }
    }
}
