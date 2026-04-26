using System;
using FFVM;
using UnityEngine;

public static partial class VOM3Tests
{
    private static void RunCallTests()
    {
        // C01: VMEngine.Call invokes a non-readonly function and returns its result.
        {
            string src = @"
func main(): int { return 0 }
func add(a: int, b: int): int { return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("add");

            Span<Number> argBuf = stackalloc Number[2];
            argBuf[0] = Number.FromInt(20); argBuf[1] = Number.FromInt(22);
            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);
            VMEngine.Call(world, 0, handle, new Arguments(argBuf), ret);
            Assert(ret.Get(0).ToInt() == 42, "VOM3.C01_CallNonReadOnly");
        }

        // C02: VMEngine.Call permits module-var writes (unlike ReadOnlyCall which is
        // compile-time rejected). Cross-call persistence requires module storage to be
        // externalized from VMInstanceState — that lands in VOM3 Phase2; here we only
        // verify a single call that writes an mvar and reads it back observes the write
        // within the same instance.
        {
            string src = @"
var counter: int = 0
func main(): int { return 0 }
func bump_and_read(): int {
    counter = counter + 5
    return counter
}
";
            var (world, prog) = Compile(src, "main");
            var bump = prog.ResolveMethod("bump_and_read");

            Span<Number> retBuf = stackalloc Number[1];
            var ret = new ReturnSlot(retBuf);

            VMEngine.Call(world, 0, bump, Arguments.Empty, ret);
            Assert(ret.Get(0).ToInt() == 5, "VOM3.C02_CallPermitsMVarWriteWithinInstance");
        }

        // C03: ReadOnlyCall rejects a non-@readonly function.
        {
            string src = @"
func main(): int { return 0 }
func bump_no_ro(): int { return 1 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("bump_no_ro");
            Span<Number> retBuf = stackalloc Number[1];
            bool threw = false;
            try
            {
                VMEngine.ReadOnlyCall(world, 0, handle, Arguments.Empty, new ReturnSlot(retBuf));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM3.C03_ReadOnlyCallRejectsNonReadOnly");
        }

        // C04: Call argument-count validation.
        {
            string src = @"
func main(): int { return 0 }
func two(a: int, b: int): int { return a + b }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("two");

            Span<Number> argBuf = stackalloc Number[1]; // wrong count
            argBuf[0] = Number.FromInt(1);
            Span<Number> retBuf = stackalloc Number[1];
            bool threw = false;
            try
            {
                VMEngine.Call(world, 0, handle, new Arguments(argBuf), new ReturnSlot(retBuf));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM3.C04_CallArgCountMismatchRejected");
        }

        // C05: Call return-slot capacity validation.
        {
            string src = @"
func main(): int { return 0 }
func one(): int { return 1 }
";
            var (world, prog) = Compile(src, "main");
            var handle = prog.ResolveMethod("one");
            Span<Number> retBuf = stackalloc Number[0]; // capacity 0 < required 1
            bool threw = false;
            try
            {
                VMEngine.Call(world, 0, handle, Arguments.Empty, new ReturnSlot(retBuf));
            }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM3.C05_CallReturnSlotTooSmallRejected");
        }
    }
}
