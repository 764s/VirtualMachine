using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM5Tests
{
    private static void RunCompatTests()
    {
        // C01: world.Syscalls is identity-equal to world.DefaultBindings.Syscalls.
        {
            var world = new VMWorld();
            Assert(ReferenceEquals(world.Syscalls, world.DefaultBindings.Syscalls),
                "VOM5.C01a_SyscallsPassthrough");
            Assert(world.DefaultBindings != null, "VOM5.C01b_DefaultBindingsNotNull");
        }

        // C02: legacy SpawnInstance(int,int) binds the new instance to DefaultBindings.
        {
            string src = @"
func main(): int { return ping() }
";
            var (world, _) = Compile(src, "main", new Dictionary<string, int> { { "ping", 0 } });
            world.Syscalls.Register(0, "ping", (ref VMInstanceState s) =>
            {
                s.Registers.Set(0, Number.FromInt(99));
            });

#pragma warning disable CS0618
            int id = world.SpawnInstance(0, 0);
#pragma warning restore CS0618
            Assert(id >= 0, "VOM5.C02a_LegacySpawnSucceeds");
            Assert(ReferenceEquals(world.Pool.Bindings[id], world.DefaultBindings),
                "VOM5.C02b_LegacyBoundToDefault");

            for (int t = 0; t < 5 && world.Pool.Instances[id].IsAlive; t++) world.Tick();
            Assert(world.Pool.Instances[id].Registers.Get(0).ToInt() == 99,
                "VOM5.C02c_LegacyDispatchedThroughDefault");
        }

        // C03: Free clears the binding entry (no GC root pinning).
        {
            var world = new VMWorld();
            var bind = HostBindings.CreateDefault();

            string src = @"func main(): int { return 0 }";
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var r = compiler.Compile(src, "main", new Dictionary<string, int>());
            world.Modules.Load(0, r.Program);

            var inst = world.SpawnInstance(0, 0, bind);
            Assert(ReferenceEquals(world.Pool.Bindings[inst.InstanceId], bind),
                "VOM5.C03a_BindingSet");

            world.Tick();
            world.DestroyInstance(inst.InstanceId);
            Assert(world.Pool.Bindings[inst.InstanceId] == null,
                "VOM5.C03b_BindingClearedOnFree");
        }
    }
}
