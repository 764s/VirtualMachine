using System;
using FFVM;
using UnityEngine;

public static partial class VOM5Tests
{
    private static void RunFacadeTests()
    {
        // F01: Invalid handle is the default; IsValid/IsAlive/IsCompleted all false.
        {
            VMInstance v = default;
            Assert(!v.IsValid, "VOM5.F01a_DefaultInvalid");
            Assert(!v.IsAlive, "VOM5.F01b_DefaultNotAlive");
            Assert(!v.IsCompleted, "VOM5.F01c_DefaultNotCompleted");
            Assert(!VMInstance.Invalid.IsValid, "VOM5.F01d_StaticInvalidIsInvalid");
        }

        // F02: Spawned handle goes Valid+Alive → Valid+Completed after world tick.
        {
            string src = @"func main(): int { return 9 }";
            var (world, _) = Compile(src, "main");
            var bind = HostBindings.CreateDefault();
            var inst = world.SpawnInstance(0, 0, bind);

            Assert(inst.IsValid, "VOM5.F02a_Valid");
            Assert(inst.IsAlive, "VOM5.F02b_Alive");
            Assert(!inst.IsCompleted, "VOM5.F02c_NotCompletedYet");
            Assert(ReferenceEquals(inst.Bindings, bind), "VOM5.F02d_BindingsAccessor");

            world.Tick();
            Assert(inst.IsValid, "VOM5.F02e_StillValidAfterTick");
            Assert(!inst.IsAlive, "VOM5.F02f_NotAliveAfterCompletion");
            Assert(inst.IsCompleted, "VOM5.F02g_CompletedAfterTick");
        }

        // F03: ABA defense — old handle becomes Invalid after slot reuse;
        //      new handle on the same slot is Valid.
        {
            string src = @"func main(): int { return 1 }";
            var (world, _) = Compile(src, "main");
            var i1 = world.SpawnInstance(0, 0, null);
            int slot = i1.InstanceId;
            int gen1 = i1.Generation;

            // Run + free
            world.Tick();
            world.DestroyInstance(slot);
            // Generation bumps on Allocate (not Free), so at this point the
            // handle's gen still matches the slot's gen — IsValid stays true,
            // but IsAlive becomes false because the slot is freed.
            Assert(!i1.IsAlive, "VOM5.F03a_NotAliveAfterFree");

            // Reuse same slot
            var i2 = world.SpawnInstance(0, 0, null);
            Assert(i2.IsValid, "VOM5.F03b_NewValid");
            Assert(i2.InstanceId == slot, "VOM5.F03c_SlotReused");
            Assert(i2.Generation != gen1, "VOM5.F03d_GenerationBumped");
            Assert(!i1.IsValid, "VOM5.F03e_OldStillInvalid");
        }

        // F04: VMInstance.Tick advances the slot just like world.TickInstance.
        {
            string src = @"func main(): int { yield; yield; return 7 }";
            var (world, _) = Compile(src, "main");
            var inst = world.SpawnInstance(0, 0, null);

            int safety = 0;
            while (inst.IsAlive && safety++ < 50) inst.Tick();
            Assert(inst.IsCompleted, "VOM5.F04a_TickDrivesToCompletion");
            Assert(safety < 50, "VOM5.F04b_NoInfiniteLoop");

            // Stale handle Tick is a no-op (does not throw)
            world.DestroyInstance(inst.InstanceId);
            inst.Tick();
            Assert(!inst.IsAlive, "VOM5.F04c_StaleTickNoCrash");
        }

        // F05: Equality is by (World, Id, Generation).
        {
            string src = @"func main(): int { return 0 }";
            var (world, _) = Compile(src, "main");
            var a = world.SpawnInstance(0, 0, null);
            var b = new VMInstance(world, a.InstanceId, a.Generation);
            Assert(a == b, "VOM5.F05a_StructuralEqual");
            Assert(a.GetHashCode() == b.GetHashCode(), "VOM5.F05b_HashEqual");

            var c = new VMInstance(world, a.InstanceId, a.Generation + 1);
            Assert(a != c, "VOM5.F05c_DifferentGenerationNotEqual");
        }
    }
}
