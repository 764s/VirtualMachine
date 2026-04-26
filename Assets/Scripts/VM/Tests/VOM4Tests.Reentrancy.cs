using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM4Tests
{
    private static void RunReentrancyTests()
    {
        // R01: A syscall captures the executing instance's YieldHandle and
        // attempts Release on it — must throw YieldReentrancyException because
        // HostExecuting is set on inst during syscall callback.
        {
            string src = @"
func main(): int { return 0 }
func tries_self(): int { ReleaseSelf(); return 99 }
";
            var sysMap = new Dictionary<string, int> { { "ReleaseSelf", 0 } };
            var (world, prog) = Compile(src, "main", sysMap);
            var handle = prog.ResolveMethod("tries_self");

            // Capture handle inside callback; the test framework reuses it.
            YieldHandle selfHandle = YieldHandle.Invalid;
            bool releaseThrew = false;
            bool tickOnceThrew = false;

            world.Syscalls.Register(0, "ReleaseSelf", (ref VMInstanceState s) =>
            {
                try { selfHandle.Release(world); }
                catch (YieldReentrancyException) { releaseThrew = true; }
                try { selfHandle.TickOnce(world); }
                catch (YieldReentrancyException) { tickOnceThrew = true; }
            });

            selfHandle = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);
            world.Tick();

            Assert(releaseThrew, "VOM4.R01a_ReleaseRejectedDuringSyscall");
            Assert(tickOnceThrew, "VOM4.R01b_TickOnceRejectedDuringSyscall");
            Assert(selfHandle.IsCompleted(world), "VOM4.R01c_RanToCompletionDespiteFailedSyscallCalls");
            selfHandle.Release(world);
        }

        // R02: A syscall on instance A tries to TickOnce another (foreign) handle B.
        // If B is NOT host-executing, that should succeed (cross-instance OK).
        {
            string src = @"
func main(): int { return 0 }
func owner(): int { TickForeign(); return 1 }
func target(): int { return 2 }
";
            var sysMap = new Dictionary<string, int> { { "TickForeign", 0 } };
            var (world, prog) = Compile(src, "main", sysMap);
            var ownerH = prog.ResolveMethod("owner");
            var targetH = prog.ResolveMethod("target");

            YieldHandle foreign = YieldHandle.Invalid;
            bool foreignTickedOk = false;

            world.Syscalls.Register(0, "TickForeign", (ref VMInstanceState s) =>
            {
                try
                {
                    foreign.TickOnce(world);
                    foreignTickedOk = foreign.IsCompleted(world);
                }
                catch (Exception) { foreignTickedOk = false; }
            });

            // Spawn target FIRST so its handle is callable from owner's syscall.
            foreign = VMEngine.YieldCall(world, 0, targetH, Arguments.Empty);
            var ownerHandle = VMEngine.YieldCall(world, 0, ownerH, Arguments.Empty);
            world.Tick();

            Assert(foreignTickedOk, "VOM4.R02_CrossInstanceTickOnceAllowed");
            ownerHandle.Release(world);
            foreign.Release(world);
        }

        // R03: Plain VMEngine.Call from inside a syscall on the same instance is
        // also rejected — InvokeOnTransient on a different (transient) slot is
        // OK, but calling YieldHandle methods on the running instance is not.
        // (Already covered by R01; this asserts ReadReturn also rejects.)
        {
            string src = @"
func main(): int { return 0 }
func tries_read(): int { ReadFromSelf(); return 7 }
";
            var sysMap = new Dictionary<string, int> { { "ReadFromSelf", 0 } };
            var (world, prog) = Compile(src, "main", sysMap);
            var handle = prog.ResolveMethod("tries_read");

            YieldHandle selfH = YieldHandle.Invalid;
            bool readThrew = false;

            world.Syscalls.Register(0, "ReadFromSelf", (ref VMInstanceState s) =>
            {
                try
                {
                    Span<Number> buf = stackalloc Number[1];
                    selfH.ReadReturn(world, new ReturnSlot(buf));
                }
                catch (YieldReentrancyException) { readThrew = true; }
            });

            selfH = VMEngine.YieldCall(world, 0, handle, Arguments.Empty);
            world.Tick();
            Assert(readThrew, "VOM4.R03_ReadReturnRejectedDuringSyscall");
            selfH.Release(world);
        }
    }
}
