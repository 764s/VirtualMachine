using System;
using System.Collections.Generic;
using FFVM;
using UnityEngine;

public static partial class VOM5Tests
{
    private static void RunSnapshotTests()
    {
        // S01: SaveState / LoadState restores the per-instance binding ref.
        {
            string src = @"
func main(): int { yield; return sig() }
";
            var (world, _) = Compile(src, "main", new Dictionary<string, int> { { "sig", 0 } });

            var bind = new HostBindings(new SyscallTable());
            int observed = 0;
            bind.Syscalls.Register(0, "sig", (ref VMInstanceState s) =>
            {
                observed++;
                s.Registers.Set(0, Number.FromInt(777));
            });

            var inst = world.SpawnInstance(0, 0, bind);
            world.SaveState();          // frame 0 — binding present in snapshot
            int savedFrame = world.FrameNumber;

            world.Tick();                // executes to yield
            // Mutate post-snapshot: clear the slot binding to simulate corruption
            world.Pool.Bindings[inst.InstanceId] = null;

            bool ok = world.LoadState(savedFrame);
            Assert(ok, "VOM5.S01a_LoadStateOk");
            Assert(ReferenceEquals(world.Pool.Bindings[inst.InstanceId], bind),
                "VOM5.S01b_BindingRestored");

            // Drive to completion → must dispatch through restored binding.
            for (int t = 0; t < 10 && world.Pool.Instances[inst.InstanceId].IsAlive; t++) world.Tick();
            Assert(observed >= 1, "VOM5.S01c_DispatchedAfterRestore");
            Assert(world.Pool.Instances[inst.InstanceId].Registers.Get(0).ToInt() == 777,
                "VOM5.S01d_ReturnFromRestoredBinding");
        }

        // S02: LoadState clears bindings on slots that were not active in the snapshot.
        {
            string src = @"func main(): int { return 0 }";
            var (world, _) = Compile(src, "main");

            world.SaveState();           // frame 0 — empty world
            int snapFrame = world.FrameNumber;

            // After snapshot: spawn an instance.
            var inst = world.SpawnInstance(0, 0, HostBindings.CreateDefault());
            int slot = inst.InstanceId;
            Assert(world.Pool.Bindings[slot] != null, "VOM5.S02a_BindingPresentBeforeRollback");

            bool ok = world.LoadState(snapFrame);
            Assert(ok, "VOM5.S02b_RolledBack");
            Assert(world.Pool.Bindings[slot] == null,
                "VOM5.S02c_BindingClearedOnRollback");
        }
    }
}
