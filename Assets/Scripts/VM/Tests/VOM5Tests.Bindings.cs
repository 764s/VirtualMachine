using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM5Tests
{
    private static void RunBindingsTests()
    {
        // B01: two instances, two distinct HostBindings, same syscall slot
        // dispatches to *each instance's* registered handler.
        {
            string src = @"
func main(): int { return my_call() }
";
            var sysMap = new Dictionary<string, int> { { "my_call", 0 } };
            var (world, _) = Compile(src, "main", sysMap);

            var bindA = new HostBindings(new SyscallTable());
            var bindB = new HostBindings(new SyscallTable());

            int observedA = -1, observedB = -1;
            bindA.Syscalls.Register(0, "my_call", (ref VMInstanceState s) =>
            {
                observedA = s.InstanceId;
                s.Registers.Set(0, Number.FromInt(111));
            });
            bindB.Syscalls.Register(0, "my_call", (ref VMInstanceState s) =>
            {
                observedB = s.InstanceId;
                s.Registers.Set(0, Number.FromInt(222));
            });

            var ia = world.SpawnInstance(0, 0, bindA);
            var ib = world.SpawnInstance(0, 0, bindB);
            Assert(ia.IsValid && ib.IsValid, "VOM5.B01a_BothSpawned");
            Assert(ia.InstanceId != ib.InstanceId, "VOM5.B01b_DistinctIds");

            // Run both to completion via world tick.
            for (int t = 0; t < 5 && (ia.IsAlive || ib.IsAlive); t++) world.Tick();

            Assert(ia.IsCompleted, "VOM5.B01c_ACompleted");
            Assert(ib.IsCompleted, "VOM5.B01d_BCompleted");
            Assert(observedA == ia.InstanceId, "VOM5.B01e_ASawSelf");
            Assert(observedB == ib.InstanceId, "VOM5.B01f_BSawSelf");
            // Each instance's r0 should hold its own binding's return value.
            Assert(world.Pool.Instances[ia.InstanceId].Registers.Get(0).ToInt() == 111,
                "VOM5.B01g_AReturnFromBindA");
            Assert(world.Pool.Instances[ib.InstanceId].Registers.Get(0).ToInt() == 222,
                "VOM5.B01h_BReturnFromBindB");
        }

        // B02: legacy world.Syscalls.Register still works for the default-bound path.
        {
            string src = @"
func main(): int { return add_ten(5) }
";
            var (world, _) = Compile(src, "main", new Dictionary<string, int> { { "add_ten", 0 } });
            world.Syscalls.Register(0, "add_ten", (ref VMInstanceState s) =>
            {
                int x = s.Registers.Get(0).ToInt();
                s.Registers.Set(0, Number.FromInt(x + 10));
            });

            var inst = world.SpawnInstance(0, 0, null); // null → DefaultBindings
            for (int t = 0; t < 5 && inst.IsAlive; t++) world.Tick();
            Assert(inst.IsCompleted, "VOM5.B02a_DefaultBindCompletes");
            Assert(world.Pool.Instances[inst.InstanceId].Registers.Get(0).ToInt() == 15,
                "VOM5.B02b_DefaultBindReturned15");
        }

        // B03: bindings persist across yield (per-instance state is not lost).
        {
            string src = @"
func main(): int { var a: int = tag(); yield; var b: int = tag(); return a + b }
";
            var (world, _) = Compile(src, "main", new Dictionary<string, int> { { "tag", 0 } });

            var bind = new HostBindings(new SyscallTable());
            int callCount = 0;
            bind.Syscalls.Register(0, "tag", (ref VMInstanceState s) =>
            {
                callCount++;
                s.Registers.Set(0, Number.FromInt(callCount * 100));
            });

            var inst = world.SpawnInstance(0, 0, bind);
            for (int t = 0; t < 10 && inst.IsAlive; t++) world.Tick();
            Assert(inst.IsCompleted, "VOM5.B03a_YieldedInstCompleted");
            Assert(callCount == 2, "VOM5.B03b_TagCalledTwice");
            Assert(world.Pool.Instances[inst.InstanceId].Registers.Get(0).ToInt() == 300,
                "VOM5.B03c_AccumulatedReturn");
        }
    }
}
