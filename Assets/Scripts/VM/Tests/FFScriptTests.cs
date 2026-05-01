using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEditor;
using UnityEngine;

/// <summary>
/// FFScript compilation and execution verification.
/// Validates that skill_114feiyanxuanfengtui and skill_25shangpanbeijizhong
/// can be compiled and executed by the current VM pipeline.
/// </summary>
public static class FFScriptTests
{
    [MenuItem("TestVM/RunFFScriptTests")]
    public static void RunAll()
    {
        int passed = 0;
        int failed = 0;
        TestHarness.BeginSuite("FFScriptTests");

        void Assert(bool condition, string testName)
        {
            if (condition) passed++; else failed++;
            TestHarness.Assert(condition, testName);
        }

        var compiler = new BytecodeCompiler();

        // ====================================================================
        // S01: skill_114feiyanxuanfengtui — compilation
        // ====================================================================
        {
            string source = @"
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()
    }

    SpawnEffectSelf(7001, 60)

    var mutex1: int = 0
    var mutex2: int = 0
    var mutex3: int = 0

    var f: int = 0
    while f < 56 {

        if f == 9 {
            ApplySelfHorizKB(6.5, 29)
            ApplySelfVertKB(12, 9)
        }

        if f >= 9 && f < 13 && mutex1 == 0 {
            var t1: int = CheckAttackHit(2001)
            if t1 > 0 {
                var air1: int = HasTargetTag(t1, 1)
                if air1 == 0 {
                    ApplyDamage(t1, 5, 101)
                    ApplyHitstun(t1, 5, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t1, 5, 201)
                    ApplyHorizKB_Speed(t1, 4.998)
                    ApplyVertKB(t1, 16.998, 90)
                    ApplyHitstun(t1, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex1 = 1
                ApplySelfHitstun(0, 7, 0)
            }

            if mutex1 == 0 {
                var b1: int = CheckAttackBlocked(2001)
                if b1 > 0 {
                    SetEnergyCoeff(2)
                    ApplyDamage(b1, 0.3, 0)
                    ApplyHitstun(b1, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex1 = 1
                    ApplySelfHitstun(0, 7, 0)
                }
            }
        }

        if f >= 14 && f < 17 && mutex2 == 0 {
            var t2: int = CheckAttackHit(2002)
            if t2 > 0 {
                var air2: int = HasTargetTag(t2, 1)
                if air2 == 0 {
                    ApplyDamage(t2, 7, 102)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    ApplyHorizKB_Dist(t2, 1, 5)
                    SpawnEffectHit(3005, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t2, 7, 201)
                    ApplyHorizKB_Speed(t2, 4.998)
                    ApplyVertKB(t2, 16.998, 90)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex2 = 1
            }

            if mutex2 == 0 {
                var b2: int = CheckAttackBlocked(2002)
                if b2 > 0 {
                    ApplyDamage(b2, 0.42, 0)
                    ApplyHorizKB_Dist(b2, 0.8, 8)
                    ApplyHitstun(b2, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex2 = 1
                }
            }
        }

        if f >= 25 && f < 30 && mutex3 == 0 {
            var t3: int = CheckAttackHit(2003)
            if t3 > 0 {
                ApplyDamage(t3, 10, 201)
                ApplyHorizKB_Speed(t3, 7.998)
                ApplyVertKB(t3, 18, 120)
                ApplyHitstun(t3, 0, 12, 0, 1)
                ApplyCornerKBSelf(0, 1)
                SpawnEffectHit(3005, 60)
                SpawnEffectSelf(3002, 30)
                mutex3 = 1
            }

            if mutex3 == 0 {
                var b3: int = CheckAttackBlocked(2003)
                if b3 > 0 {
                    SetEnergyCoeff(3)
                    ApplyDamage(b3, 0.6, 0)
                    ApplyHorizKB_Dist(b3, 1, 5)
                    ApplyHitstun(b3, 0, 12, 0, 1)
                    ApplyCornerKBSelf(1.3, 1)
                    SpawnEffectHit(3004, 30)
                    mutex3 = 1
                }
            }
        }

        f = f + 1
        yield
    }
}
";
            var syscalls = new Dictionary<string, int>
            {
                { "BeginAction", 0 }, { "EndAction", 1 },
                { "SpawnEffectSelf", 2 }, { "SpawnEffectHit", 3 },
                { "CheckAttackHit", 4 }, { "CheckAttackBlocked", 5 },
                { "HasTargetTag", 6 }, { "ApplyDamage", 7 },
                { "ApplyHitstun", 8 }, { "ApplyHorizKB_Dist", 9 },
                { "ApplyHorizKB_Speed", 10 }, { "ApplyVertKB", 11 },
                { "ApplySelfHitstun", 12 }, { "ApplySelfHorizKB", 13 },
                { "ApplySelfVertKB", 14 }, { "ApplyCornerKBSelf", 15 },
                { "SetEnergyCoeff", 16 },
            };
            var result = compiler.Compile(source, "main", syscalls);

            if (!result.Success)
                foreach (var e in result.Errors) Debug.LogError($"  compile error: {e}");
            Assert(result.Success, "S01: skill_114feiyanxuanfengtui compiles");
        }

        // ====================================================================
        // S02: skill_114feiyanxuanfengtui — execution (no-hit path, 56 frames)
        // ====================================================================
        {
            string source = @"
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()
    }
    SpawnEffectSelf(7001, 60)
    var mutex1: int = 0
    var mutex2: int = 0
    var mutex3: int = 0
    var f: int = 0
    while f < 56 {
        if f == 9 {
            ApplySelfHorizKB(6.5, 29)
            ApplySelfVertKB(12, 9)
        }
        if f >= 9 && f < 13 && mutex1 == 0 {
            var t1: int = CheckAttackHit(2001)
            if t1 > 0 {
                var air1: int = HasTargetTag(t1, 1)
                if air1 == 0 {
                    ApplyDamage(t1, 5, 101)
                    ApplyHitstun(t1, 5, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t1, 5, 201)
                    ApplyHorizKB_Speed(t1, 4.998)
                    ApplyVertKB(t1, 16.998, 90)
                    ApplyHitstun(t1, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex1 = 1
                ApplySelfHitstun(0, 7, 0)
            }
            if mutex1 == 0 {
                var b1: int = CheckAttackBlocked(2001)
                if b1 > 0 {
                    SetEnergyCoeff(2)
                    ApplyDamage(b1, 0.3, 0)
                    ApplyHitstun(b1, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex1 = 1
                    ApplySelfHitstun(0, 7, 0)
                }
            }
        }
        if f >= 14 && f < 17 && mutex2 == 0 {
            var t2: int = CheckAttackHit(2002)
            if t2 > 0 {
                var air2: int = HasTargetTag(t2, 1)
                if air2 == 0 {
                    ApplyDamage(t2, 7, 102)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    ApplyHorizKB_Dist(t2, 1, 5)
                    SpawnEffectHit(3005, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t2, 7, 201)
                    ApplyHorizKB_Speed(t2, 4.998)
                    ApplyVertKB(t2, 16.998, 90)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex2 = 1
            }
            if mutex2 == 0 {
                var b2: int = CheckAttackBlocked(2002)
                if b2 > 0 {
                    ApplyDamage(b2, 0.42, 0)
                    ApplyHorizKB_Dist(b2, 0.8, 8)
                    ApplyHitstun(b2, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex2 = 1
                }
            }
        }
        if f >= 25 && f < 30 && mutex3 == 0 {
            var t3: int = CheckAttackHit(2003)
            if t3 > 0 {
                ApplyDamage(t3, 10, 201)
                ApplyHorizKB_Speed(t3, 7.998)
                ApplyVertKB(t3, 18, 120)
                ApplyHitstun(t3, 0, 12, 0, 1)
                ApplyCornerKBSelf(0, 1)
                SpawnEffectHit(3005, 60)
                SpawnEffectSelf(3002, 30)
                mutex3 = 1
            }
            if mutex3 == 0 {
                var b3: int = CheckAttackBlocked(2003)
                if b3 > 0 {
                    SetEnergyCoeff(3)
                    ApplyDamage(b3, 0.6, 0)
                    ApplyHorizKB_Dist(b3, 1, 5)
                    ApplyHitstun(b3, 0, 12, 0, 1)
                    ApplyCornerKBSelf(1.3, 1)
                    SpawnEffectHit(3004, 30)
                    mutex3 = 1
                }
            }
        }
        f = f + 1
        yield
    }
}
";
            var syscalls = new Dictionary<string, int>
            {
                { "BeginAction", 0 }, { "EndAction", 1 },
                { "SpawnEffectSelf", 2 }, { "SpawnEffectHit", 3 },
                { "CheckAttackHit", 4 }, { "CheckAttackBlocked", 5 },
                { "HasTargetTag", 6 }, { "ApplyDamage", 7 },
                { "ApplyHitstun", 8 }, { "ApplyHorizKB_Dist", 9 },
                { "ApplyHorizKB_Speed", 10 }, { "ApplyVertKB", 11 },
                { "ApplySelfHitstun", 12 }, { "ApplySelfHorizKB", 13 },
                { "ApplySelfVertKB", 14 }, { "ApplyCornerKBSelf", 15 },
                { "SetEnergyCoeff", 16 },
            };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "S02 compile");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);

            // All syscalls return 0 (no hit) — exercises the "空放" (no-hit) path
            world.Syscalls.Register(0, "BeginAction", (ref VMInstanceState s) =>
                { log.Add($"BeginAction({s.Registers.Get(0).ToInt()},{s.Registers.Get(1).ToInt()})"); });
            world.Syscalls.Register(1, "EndAction", (ref VMInstanceState s) =>
                { log.Add("EndAction"); });
            world.Syscalls.Register(2, "SpawnEffectSelf", (ref VMInstanceState s) =>
                { log.Add($"SpawnEffectSelf({s.Registers.Get(0).ToInt()},{s.Registers.Get(1).ToInt()})"); });
            world.Syscalls.Register(3, "SpawnEffectHit", (ref VMInstanceState s) => { });
            // CheckAttackHit always returns 0 (no hit)
            world.Syscalls.Register(4, "CheckAttackHit", (ref VMInstanceState s) =>
                { s.Registers.Set(0, Number.FromInt(0)); });
            // CheckAttackBlocked always returns 0 (no block)
            world.Syscalls.Register(5, "CheckAttackBlocked", (ref VMInstanceState s) =>
                { s.Registers.Set(0, Number.FromInt(0)); });
            world.Syscalls.Register(6, "HasTargetTag", (ref VMInstanceState s) =>
                { s.Registers.Set(0, Number.FromInt(0)); });
            world.Syscalls.Register(7, "ApplyDamage", (ref VMInstanceState s) => { });
            world.Syscalls.Register(8, "ApplyHitstun", (ref VMInstanceState s) => { });
            world.Syscalls.Register(9, "ApplyHorizKB_Dist", (ref VMInstanceState s) => { });
            world.Syscalls.Register(10, "ApplyHorizKB_Speed", (ref VMInstanceState s) => { });
            world.Syscalls.Register(11, "ApplyVertKB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(12, "ApplySelfHitstun", (ref VMInstanceState s) => { });
            world.Syscalls.Register(13, "ApplySelfHorizKB", (ref VMInstanceState s) =>
                { log.Add($"ApplySelfHorizKB"); });
            world.Syscalls.Register(14, "ApplySelfVertKB", (ref VMInstanceState s) =>
                { log.Add($"ApplySelfVertKB"); });
            world.Syscalls.Register(15, "ApplyCornerKBSelf", (ref VMInstanceState s) => { });
            world.Syscalls.Register(16, "SetEnergyCoeff", (ref VMInstanceState s) => { });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: BeginAction, SpawnEffectSelf, enter loop at f=0, yield
            world.Tick();
            Assert(log.Count >= 2 && log[0] == "BeginAction(114,56)"
                   && log[1] == "SpawnEffectSelf(7001,60)",
                   "S02: tick 1 → BeginAction + SpawnEffectSelf");

            // Each yield = WAIT 1 → needs 1 countdown tick + 1 resume tick = 2 ticks per iteration.
            // 56 iterations → 56*2 countdown+resume ticks, plus 1 final resume for loop exit.
            for (int t = 0; t < 150; t++)
            {
                if ((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0) break;
                world.Tick();
            }

            Assert(log.Contains("ApplySelfHorizKB"), "S02: self displacement triggered at frame 9");
            Assert(log.Contains("ApplySelfVertKB"), "S02: self vert displacement triggered at frame 9");
            Assert(log.Contains("EndAction"), "S02: EndAction via defer");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                   "S02: Completed after 56 frames");
        }

        // ====================================================================
        // S03: skill_25shangpanbeijizhong — compilation
        // ====================================================================
        {
            string source = @"
func main() {
    BeginAction(25, 30)
    defer {
        EndAction()
    }
    SpawnEffectSelf(4001, 300)
    var f: int = 0
    while f < 30 {
        f = f + 1
        yield
    }
}
";
            var syscalls = new Dictionary<string, int>
            {
                { "BeginAction", 0 }, { "EndAction", 1 }, { "SpawnEffectSelf", 2 },
            };
            var result = compiler.Compile(source, "main", syscalls);

            if (!result.Success)
                foreach (var e in result.Errors) Debug.LogError($"  compile error: {e}");
            Assert(result.Success, "S03: skill_25shangpanbeijizhong compiles");
        }

        // ====================================================================
        // S04: skill_25shangpanbeijizhong — execution (30 frames)
        // ====================================================================
        {
            string source = @"
func main() {
    BeginAction(25, 30)
    defer {
        EndAction()
    }
    SpawnEffectSelf(4001, 300)
    var f: int = 0
    while f < 30 {
        f = f + 1
        yield
    }
}
";
            var syscalls = new Dictionary<string, int>
            {
                { "BeginAction", 0 }, { "EndAction", 1 }, { "SpawnEffectSelf", 2 },
            };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "S04 compile");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "BeginAction", (ref VMInstanceState s) =>
                { log.Add($"BeginAction({s.Registers.Get(0).ToInt()},{s.Registers.Get(1).ToInt()})"); });
            world.Syscalls.Register(1, "EndAction", (ref VMInstanceState s) =>
                { log.Add("EndAction"); });
            world.Syscalls.Register(2, "SpawnEffectSelf", (ref VMInstanceState s) =>
                { log.Add($"SpawnEffectSelf({s.Registers.Get(0).ToInt()},{s.Registers.Get(1).ToInt()})"); });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: BeginAction + SpawnEffectSelf + enter loop + yield
            world.Tick();
            Assert(log.Count >= 2, "S04: tick 1 → BeginAction + SpawnEffectSelf");
            Assert(log[0] == "BeginAction(25,30)", "S04: BeginAction(25,30)");
            Assert(log[1] == "SpawnEffectSelf(4001,300)", "S04: SpawnEffectSelf(4001,300)");

            // Run until completed (yield=WAIT1 needs 2 ticks per iteration)
            for (int t = 0; t < 80; t++)
            {
                if ((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0) break;
                world.Tick();
            }

            Assert(log.Contains("EndAction"), "S04: EndAction via defer");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                   "S04: Completed after 30 frames");
        }

        // ====================================================================
        // S05: skill_114feiyanxuanfengtui — execution with hit at frame 10
        // ====================================================================
        {
            string source = @"
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()
    }
    SpawnEffectSelf(7001, 60)
    var mutex1: int = 0
    var mutex2: int = 0
    var mutex3: int = 0
    var f: int = 0
    while f < 56 {
        if f == 9 {
            ApplySelfHorizKB(6.5, 29)
            ApplySelfVertKB(12, 9)
        }
        if f >= 9 && f < 13 && mutex1 == 0 {
            var t1: int = CheckAttackHit(2001)
            if t1 > 0 {
                var air1: int = HasTargetTag(t1, 1)
                if air1 == 0 {
                    ApplyDamage(t1, 5, 101)
                    ApplyHitstun(t1, 5, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t1, 5, 201)
                    ApplyHorizKB_Speed(t1, 4.998)
                    ApplyVertKB(t1, 16.998, 90)
                    ApplyHitstun(t1, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex1 = 1
                ApplySelfHitstun(0, 7, 0)
            }
            if mutex1 == 0 {
                var b1: int = CheckAttackBlocked(2001)
                if b1 > 0 {
                    SetEnergyCoeff(2)
                    ApplyDamage(b1, 0.3, 0)
                    ApplyHitstun(b1, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex1 = 1
                    ApplySelfHitstun(0, 7, 0)
                }
            }
        }
        if f >= 14 && f < 17 && mutex2 == 0 {
            var t2: int = CheckAttackHit(2002)
            if t2 > 0 {
                var air2: int = HasTargetTag(t2, 1)
                if air2 == 0 {
                    ApplyDamage(t2, 7, 102)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    ApplyHorizKB_Dist(t2, 1, 5)
                    SpawnEffectHit(3005, 60)
                    SpawnEffectSelf(3002, 30)
                } else {
                    SetEnergyCoeff(2)
                    ApplyDamage(t2, 7, 201)
                    ApplyHorizKB_Speed(t2, 4.998)
                    ApplyVertKB(t2, 16.998, 90)
                    ApplyHitstun(t2, 0, 12, 0, 1)
                    SpawnEffectHit(3001, 60)
                    SpawnEffectSelf(3002, 30)
                    SpawnEffectSelf(3003, 120)
                }
                mutex2 = 1
            }
            if mutex2 == 0 {
                var b2: int = CheckAttackBlocked(2002)
                if b2 > 0 {
                    ApplyDamage(b2, 0.42, 0)
                    ApplyHorizKB_Dist(b2, 0.8, 8)
                    ApplyHitstun(b2, 0, 12, 0, 1)
                    SpawnEffectHit(3004, 30)
                    mutex2 = 1
                }
            }
        }
        if f >= 25 && f < 30 && mutex3 == 0 {
            var t3: int = CheckAttackHit(2003)
            if t3 > 0 {
                ApplyDamage(t3, 10, 201)
                ApplyHorizKB_Speed(t3, 7.998)
                ApplyVertKB(t3, 18, 120)
                ApplyHitstun(t3, 0, 12, 0, 1)
                ApplyCornerKBSelf(0, 1)
                SpawnEffectHit(3005, 60)
                SpawnEffectSelf(3002, 30)
                mutex3 = 1
            }
            if mutex3 == 0 {
                var b3: int = CheckAttackBlocked(2003)
                if b3 > 0 {
                    SetEnergyCoeff(3)
                    ApplyDamage(b3, 0.6, 0)
                    ApplyHorizKB_Dist(b3, 1, 5)
                    ApplyHitstun(b3, 0, 12, 0, 1)
                    ApplyCornerKBSelf(1.3, 1)
                    SpawnEffectHit(3004, 30)
                    mutex3 = 1
                }
            }
        }
        f = f + 1
        yield
    }
}
";
            var syscalls = new Dictionary<string, int>
            {
                { "BeginAction", 0 }, { "EndAction", 1 },
                { "SpawnEffectSelf", 2 }, { "SpawnEffectHit", 3 },
                { "CheckAttackHit", 4 }, { "CheckAttackBlocked", 5 },
                { "HasTargetTag", 6 }, { "ApplyDamage", 7 },
                { "ApplyHitstun", 8 }, { "ApplyHorizKB_Dist", 9 },
                { "ApplyHorizKB_Speed", 10 }, { "ApplyVertKB", 11 },
                { "ApplySelfHitstun", 12 }, { "ApplySelfHorizKB", 13 },
                { "ApplySelfVertKB", 14 }, { "ApplyCornerKBSelf", 15 },
                { "SetEnergyCoeff", 16 },
            };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "S05 compile");

            var hitLog = new List<string>();
            int simFrame = 0;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);

            world.Syscalls.Register(0, "BeginAction", (ref VMInstanceState s) => { });
            world.Syscalls.Register(1, "EndAction", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "SpawnEffectSelf", (ref VMInstanceState s) => { });
            world.Syscalls.Register(3, "SpawnEffectHit", (ref VMInstanceState s) => { });
            // Return target=42 on frame 10 for group 2001, else 0
            world.Syscalls.Register(4, "CheckAttackHit", (ref VMInstanceState s) =>
            {
                int grp = s.Registers.Get(0).ToInt();
                if (grp == 2001 && simFrame == 10)
                    s.Registers.Set(0, Number.FromInt(42));
                else
                    s.Registers.Set(0, Number.FromInt(0));
            });
            world.Syscalls.Register(5, "CheckAttackBlocked", (ref VMInstanceState s) =>
                { s.Registers.Set(0, Number.FromInt(0)); });
            // Target 42 is not airborne
            world.Syscalls.Register(6, "HasTargetTag", (ref VMInstanceState s) =>
                { s.Registers.Set(0, Number.FromInt(0)); });
            world.Syscalls.Register(7, "ApplyDamage", (ref VMInstanceState s) =>
            {
                hitLog.Add($"Damage(t={s.Registers.Get(0).ToInt()},c={s.Registers.Get(1).ToInt()},d={s.Registers.Get(2).ToInt()})");
            });
            world.Syscalls.Register(8, "ApplyHitstun", (ref VMInstanceState s) =>
            {
                hitLog.Add($"Hitstun(t={s.Registers.Get(0).ToInt()})");
            });
            world.Syscalls.Register(9, "ApplyHorizKB_Dist", (ref VMInstanceState s) => { });
            world.Syscalls.Register(10, "ApplyHorizKB_Speed", (ref VMInstanceState s) => { });
            world.Syscalls.Register(11, "ApplyVertKB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(12, "ApplySelfHitstun", (ref VMInstanceState s) =>
                { hitLog.Add("SelfHitstun"); });
            world.Syscalls.Register(13, "ApplySelfHorizKB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(14, "ApplySelfVertKB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(15, "ApplyCornerKBSelf", (ref VMInstanceState s) => { });
            world.Syscalls.Register(16, "SetEnergyCoeff", (ref VMInstanceState s) => { });

            int id = world.SpawnInstance(0, 0);

            // Run until completed, tracking simFrame for hit simulation.
            // Each loop iteration takes 2 ticks (yield=WAIT1: 1 countdown + 1 resume).
            // Script frame N executes at tick 2*N+1 (odd ticks).
            // We want a hit at script frame 10, so simFrame tracks the script's f variable.
            int tickCount = 0;
            int scriptFrame = -1;
            for (int t = 0; t < 150; t++)
            {
                // Odd ticks (1,3,5,...) are execution ticks; script frame = (tick-1)/2
                tickCount++;
                if (tickCount % 2 == 1)
                    scriptFrame = (tickCount - 1) / 2;
                simFrame = scriptFrame;
                world.Tick();
                if ((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0) break;
            }

            // Verify: hit at frame 10 should trigger ground-hit path
            Assert(hitLog.Contains("Damage(t=42,c=5,d=101)"),
                   "S05: ground hit damage at frame 10 → target 42, coeff 5, type 101");
            Assert(hitLog.Contains("Hitstun(t=42)"),
                   "S05: hitstun applied to target 42");
            Assert(hitLog.Contains("SelfHitstun"),
                   "S05: self hitstun on hit");
        }

        Debug.Log($"\n===== FFScriptTests: {passed} passed, {failed} failed =====");
        TestHarness.EndSuite();
    }
}
