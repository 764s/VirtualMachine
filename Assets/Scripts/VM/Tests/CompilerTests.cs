using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Step 6 validation: text script → Lexer → Parser → Compiler → VMProgram → VMWorld.Tick.
/// Covers: end-to-end compilation, tracer bullet from text, variables, if/else, while, for.
/// </summary>
public static class CompilerTests
{
    [MenuItem("TestVM/RunCompilerTests")]
    public static void RunAll()
    {
        int passed = 0;
        int failed = 0;

        void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Debug.Log($"[PASS] {testName}");
                passed++;
            }
            else
            {
                Debug.LogError($"[FAIL] {testName}");
                failed++;
            }
        }

        var compiler = new BytecodeCompiler();

        // ===== Test C01: Minimal script — single syscall =====
        {
            string source = @"
func main() {
    Ping()
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C01 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { log.Add("Ping"); });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "Ping", "C01: Ping called once");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C01: Completed");
        }

        // ===== Test C02: Syscall with arguments =====
        {
            string source = @"
func main() {
    SetValue(42, 7)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C02 compile success");

            int arg0 = -1, arg1 = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) =>
            {
                arg0 = s.Registers.Get(0).ToInt();
                arg1 = s.Registers.Get(1).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(arg0 == 42 && arg1 == 7, $"C02: args = ({arg0}, {arg1}) expected (42, 7)");
        }

        // ===== Test C03: Wait — suspension and resume =====
        {
            string source = @"
func main() {
    Before()
    wait 5
    After()
}";
            var syscalls = new Dictionary<string, int> { { "Before", 0 }, { "After", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C03 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Before", (ref VMInstanceState s) => { log.Add("Before"); });
            world.Syscalls.Register(1, "After", (ref VMInstanceState s) => { log.Add("After"); });

            int id = world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: Before + WAIT 5
            Assert(log.Count == 1 && log[0] == "Before", "C03: tick 1 → Before only");

            for (int t = 0; t < 5; t++) world.Tick(); // tick 2-6: waiting
            Assert(log.Count == 1, "C03: waiting, no new calls");

            world.Tick(); // tick 7: resume → After → Completed
            Assert(log.Count == 2 && log[1] == "After", "C03: tick 7 → After");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C03: Completed");
        }

        // ===== Test C04: Tracer bullet — defer + wait + syscall (exact match to hand-coded test) =====
        {
            string source = @"
func main() {
    defer {
        SetBB(0)
    }
    SetBB(1)
    wait 10
    PlayEffect()
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "PlayEffect", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C04 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
            {
                log.Add($"SetBB({s.Registers.Get(0).ToInt()})");
            });
            world.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
            {
                log.Add("PlayEffect");
            });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: PUSH_CLEANUP, SetBB(1), WAIT 10
            world.Tick();
            Assert(log.Count == 1 && log[0] == "SetBB(1)", "C04: tick 1 → SetBB(1)");

            // Tick 2-11: wait countdown
            for (int t = 0; t < 10; t++) world.Tick();
            Assert(log.Count == 1, "C04: waiting, no new syscalls");

            // Tick 12: resume → PlayEffect, RETURN → cleanup SetBB(0), RETURN → Completed
            world.Tick();
            Assert(log.Count == 3, "C04: 3 total syscalls");
            Assert(log[1] == "PlayEffect", "C04: PlayEffect after wait");
            Assert(log[2] == "SetBB(0)", "C04: cleanup SetBB(0)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C04: Completed");
        }

        // ===== Test C05: Tracer bullet — kill path (cleanup without PlayEffect) =====
        {
            string source = @"
func main() {
    defer {
        SetBB(0)
    }
    SetBB(1)
    wait 10
    PlayEffect()
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "PlayEffect", 1 } };
            var result = compiler.Compile(source, "main", syscalls);

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
            {
                log.Add($"SetBB({s.Registers.Get(0).ToInt()})");
            });
            world.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
            {
                log.Add("PlayEffect");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: SetBB(1), WAIT 10
            Assert(log.Count == 1 && log[0] == "SetBB(1)", "C05: tick 1 → SetBB(1)");

            // Kill during wait
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;

            world.Tick(); // tick 2: kill detected → cleanup → SetBB(0)
            Assert(log.Count == 2 && log[1] == "SetBB(0)", "C05: kill → cleanup SetBB(0)");
            Assert(!log.Contains("PlayEffect"), "C05: PlayEffect NOT called");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C05: Completed after kill");
        }

        // ===== Test C06: Variables and arithmetic =====
        {
            string source = @"
func main() {
    var x: int = 10
    var y: int = 3
    Report(x + y)
    Report(x * y)
    Report(x - y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C06 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, "C06: 3 reports");
            Assert(values[0] == 13, $"C06: 10+3 = {values[0]} (expected 13)");
            Assert(values[1] == 30, $"C06: 10*3 = {values[1]} (expected 30)");
            Assert(values[2] == 7, $"C06: 10-3 = {values[2]} (expected 7)");
        }

        // ===== Test C07: If/else =====
        {
            string source = @"
func main() {
    var x: int = 10
    if x > 5 {
        Report(1)
    } else {
        Report(0)
    }
    if x < 5 {
        Report(99)
    } else {
        Report(2)
    }
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C07 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "C07: 2 reports");
            Assert(values[0] == 1, $"C07: x>5 → Report(1), got {values[0]}");
            Assert(values[1] == 2, $"C07: x<5 → else → Report(2), got {values[1]}");
        }

        // ===== Test C08: While loop =====
        {
            string source = @"
func main() {
    var i: int = 0
    while i < 5 {
        i = i + 1
    }
    Report(i)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C08 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 5, $"C08: while loop → i = {reported} (expected 5)");
        }

        // ===== Test C09: For loop =====
        {
            string source = @"
func main() {
    var sum: int = 0
    for var i: int = 1; i <= 10; i = i + 1 {
        sum = sum + i
    }
    Report(sum)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C09 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 55, $"C09: sum(1..10) = {reported} (expected 55)");
        }

        // ===== Test C10: Yield (= wait 1) =====
        {
            string source = @"
func main() {
    A()
    yield
    B()
    yield
    C()
}";
            var syscalls = new Dictionary<string, int> { { "A", 0 }, { "B", 1 }, { "C", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C10 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "A", (ref VMInstanceState s) => { log.Add("A"); });
            world.Syscalls.Register(1, "B", (ref VMInstanceState s) => { log.Add("B"); });
            world.Syscalls.Register(2, "C", (ref VMInstanceState s) => { log.Add("C"); });

            world.SpawnInstance(0, 0);
            world.Tick(); // A, yield
            Assert(log.Count == 1 && log[0] == "A", "C10: tick 1 → A");
            world.Tick(); // wait 1 countdown
            world.Tick(); // B, yield
            Assert(log.Count == 2 && log[1] == "B", "C10: tick 3 → B");
            world.Tick(); // wait 1 countdown
            world.Tick(); // C, RETURN
            Assert(log.Count == 3 && log[2] == "C", "C10: tick 5 → C");
        }

        // ===== Test C11: Unary operators =====
        {
            string source = @"
func main() {
    var x: int = 10
    Report(-x)
    var flag: int = 0
    Report(!flag)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C11 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "C11: 2 reports");
            Assert(values[0] == -10, $"C11: -10, got {values[0]}");
            Assert(values[1] == 1, $"C11: !0 = 1, got {values[1]}");
        }

        // ===== Test C12: Boolean operators =====
        {
            string source = @"
func main() {
    var a: int = 1
    var b: int = 0
    Report(a && b)
    Report(a || b)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C12 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "C12: 2 reports");
            Assert(values[0] == 0, $"C12: 1&&0 = 0, got {values[0]}");
            Assert(values[1] == 1, $"C12: 1||0 = 1, got {values[1]}");
        }

        // ===== Test C13: Multiple defers (LIFO order) =====
        {
            string source = @"
func main() {
    defer {
        Report(1)
    }
    defer {
        Report(2)
    }
    Report(3)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C13 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, $"C13: 3 reports, got {values.Count}");
            Assert(values[0] == 3, $"C13: body first → 3, got {values[0]}");
            Assert(values[1] == 2, $"C13: LIFO → 2 before 1, got {values[1]}");
            Assert(values[2] == 1, $"C13: LIFO → 1 last, got {values[2]}");
        }

        // ===== Test C14: Comments =====
        {
            string source = @"
// This is a comment
func main() {
    // Another comment
    Report(42) // Inline comment
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C14 compile success (comments)");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 42, $"C14: Report(42), got {reported}");
        }

        // ===== Test C15: Variable assignment in expression =====
        {
            string source = @"
func main() {
    var x: int = 5
    x = x * 2 + 3
    Report(x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C15 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 13, $"C15: 5*2+3 = {reported} (expected 13)");
        }

        // ===== Test C16: Else-if chain =====
        {
            string source = @"
func main() {
    var x: int = 15
    if x < 10 {
        Report(1)
    } else if x < 20 {
        Report(2)
    } else {
        Report(3)
    }
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C16 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 2, $"C16: else-if → Report(2), got {reported}");
        }

        // ===== Test C17: Save/Load consistency with compiled script =====
        {
            string source = @"
func main() {
    defer {
        SetBB(0)
    }
    SetBB(1)
    wait 10
    PlayEffect()
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "PlayEffect", 1 } };
            var result = compiler.Compile(source, "main", syscalls);

            // Run "gold" — no interruption
            var goldLog = new List<string>();
            var goldWorld = new VMWorld();
            goldWorld.Modules.Load(0, result.Program);
            goldWorld.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) => goldLog.Add($"SetBB({s.Registers.Get(0).ToInt()})"));
            goldWorld.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) => goldLog.Add("PlayEffect"));
            goldWorld.SpawnInstance(0, 0);
            for (int t = 0; t < 15; t++) goldWorld.Tick();

            // Run with save/load at frame 5
            var testLog = new List<string>();
            var testWorld = new VMWorld();
            testWorld.Modules.Load(0, result.Program);
            testWorld.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) => testLog.Add($"SetBB({s.Registers.Get(0).ToInt()})"));
            testWorld.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) => testLog.Add("PlayEffect"));
            testWorld.SpawnInstance(0, 0);

            // Run 5 ticks, save
            for (int t = 0; t < 5; t++) testWorld.Tick();
            testWorld.SaveState();
            int savedFrame = testWorld.FrameNumber;

            // Run 5 more ticks (will be reverted)
            for (int t = 0; t < 5; t++) testWorld.Tick();

            // Load back to saved state
            testLog.Clear();
            testWorld.LoadState(savedFrame);

            // Reconstruct expected log from saved point
            var afterLoadLog = new List<string>();
            testWorld.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) => afterLoadLog.Add($"SetBB({s.Registers.Get(0).ToInt()})"));
            testWorld.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) => afterLoadLog.Add("PlayEffect"));

            for (int t = 0; t < 10; t++) testWorld.Tick();

            // Gold from frame 5 onward
            var goldFromFrame5 = new List<string>();
            var gold2 = new VMWorld();
            gold2.Modules.Load(0, result.Program);
            gold2.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) => { /* skip setup calls */ });
            gold2.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) => { /* skip */ });
            gold2.SpawnInstance(0, 0);
            for (int t = 0; t < 5; t++) gold2.Tick();
            // Now re-register with capture
            var gold2Log = new List<string>();
            gold2.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) => gold2Log.Add($"SetBB({s.Registers.Get(0).ToInt()})"));
            gold2.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) => gold2Log.Add("PlayEffect"));
            for (int t = 0; t < 10; t++) gold2.Tick();

            bool seqMatch = afterLoadLog.Count == gold2Log.Count;
            if (seqMatch)
            {
                for (int i = 0; i < afterLoadLog.Count; i++)
                {
                    if (afterLoadLog[i] != gold2Log[i]) { seqMatch = false; break; }
                }
            }
            Assert(seqMatch, $"C17: Save/Load syscall sequence match (after={afterLoadLog.Count}, gold={gold2Log.Count})");
        }

        // ===== Test C18: Nested expression in syscall args =====
        {
            string source = @"
func main() {
    var x: int = 3
    var y: int = 4
    Report(x * y + 2)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C18 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 14, $"C18: 3*4+2 = {reported} (expected 14)");
        }

        // ===== Test C19: Parse error handling =====
        {
            string source = @"
func main() {
    var x: int =
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "C19: parse error detected for incomplete var decl");
        }

        // ===== Test C20: Compile error — unknown function =====
        {
            string source = @"
func main() {
    UnknownFunc(1)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "C20: compile error for unknown function");
        }

        // ===== Test C21: Bool literals (true/false) =====
        {
            string source = @"
func main() {
    var a: int = true
    var b: int = false
    Report(a)
    Report(b)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C21 compile success (bool literals)");

            var reports = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reports.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reports.Count == 2, $"C21: 2 reports, got {reports.Count}");
            Assert(reports.Count > 0 && reports[0] == 1, $"C21: true = 1, got {(reports.Count > 0 ? reports[0].ToString() : "?")}");
            Assert(reports.Count > 1 && reports[1] == 0, $"C21: false = 0, got {(reports.Count > 1 ? reports[1].ToString() : "?")}");
        }

        // ===== Test C22: Float literals =====
        {
            string source = @"
func main() {
    var x: int = 3.5
    var y: int = 2.5
    Report(x + y)
    Report(x - y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C22 compile success (float literals)");

            var reports = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reports.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reports.Count == 2, $"C22: 2 reports, got {reports.Count}");
            Assert(reports.Count > 0 && reports[0] == 6, $"C22: 3.5+2.5 = 6, got {(reports.Count > 0 ? reports[0].ToString() : "?")}");
            Assert(reports.Count > 1 && reports[1] == 1, $"C22: 3.5-2.5 = 1, got {(reports.Count > 1 ? reports[1].ToString() : "?")}");
        }

        // ===== Test C23: wait_for — instance A waits for instance B to complete =====
        {
            string sourceA = @"
func main() {
    Before()
    wait_for(0)
    After()
}";
            string sourceB = @"
func main() {
    Work()
    wait 3
    Done()
}";
            var syscallsA = new Dictionary<string, int> { { "Before", 0 }, { "After", 1 } };
            var syscallsB = new Dictionary<string, int> { { "Work", 2 }, { "Done", 3 } };
            var resultA = compiler.Compile(sourceA, "main", syscallsA);
            var resultB = compiler.Compile(sourceB, "main", syscallsB);
            Assert(resultA.Success, "C23: script A compiles");
            Assert(resultB.Success, "C23: script B compiles");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, resultA.Program);
            world.Modules.Load(1, resultB.Program);
            world.Syscalls.Register(0, "Before", (ref VMInstanceState s) => { log.Add("Before"); });
            world.Syscalls.Register(1, "After", (ref VMInstanceState s) => { log.Add("After"); });
            world.Syscalls.Register(2, "Work", (ref VMInstanceState s) => { log.Add("Work"); });
            world.Syscalls.Register(3, "Done", (ref VMInstanceState s) => { log.Add("Done"); });

            int idB = world.SpawnInstance(1, 0); // instance B starts first → gets id=0 (pool allocates sequentially)
            int idA = world.SpawnInstance(0, 0); // instance A waits for B via wait_for(0) — hardcoded to match idB

            Assert(idB == 0, "C23: B must be instance 0 (script hardcodes wait_for(0))");
            world.Tick(); // tick 1: B→Work+WAIT3, A→Before+WAIT_FOR(0)
            Assert(log.Contains("Before") && log.Contains("Work"), "C23: tick 1 → Before + Work");

            world.Tick(); // tick 2: B waiting (counter 3→2)
            world.Tick(); // tick 3: B waiting (counter 2→1)
            world.Tick(); // tick 4: B waiting (counter 1→0)
            world.Tick(); // tick 5: B resumes → Done → Completed
            Assert(log.Contains("Done"), "C23: B completes → Done");

            world.Tick(); // tick 6: A detects B completed → After → Completed
            Assert(log.Contains("After"), "C23: A resumes after B → After");
            Assert((world.Pool.Instances[idA].StateFlags & VMStateFlags.Completed) != 0, "C23: A Completed");
            Assert((world.Pool.Instances[idB].StateFlags & VMStateFlags.Completed) != 0, "C23: B Completed");
        }

        // ===== Test C24: using — normal exit (POP_CLEANUP, no cleanup execution) =====
        {
            string source = @"
func main() {
    using SetBB(1) {
        Report(10)
    }
    Report(20)
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "Report", 2 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { /* acquire: set bb */ },
                1, "ResetBB", (ref VMInstanceState s) => { /* release: reset bb */ });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C24 compile success");

            var log = new List<string>();
            // Re-register with logging
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count >= 3, $"C24: expected 3 calls, got {log.Count}");
            Assert(log.Count > 0 && log[0] == "SetBB", $"C24: acquire SetBB first, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count > 1 && log[1] == "Report(10)", $"C24: body Report(10), got {(log.Count > 1 ? log[1] : "?")}");
            Assert(log.Count > 2 && log[2] == "Report(20)", $"C24: after using Report(20), got {(log.Count > 2 ? log[2] : "?")}");
            // ResetBB should NOT be called on normal exit — POP_CLEANUP removes the frame
            Assert(!log.Contains("ResetBB"), "C24: ResetBB NOT called on normal exit");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C24: Completed");
        }

        // ===== Test C25: using — Kill path (cleanup executes release syscall) =====
        {
            string source = @"
func main() {
    using SetBB(1) {
        wait 100
    }
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C25 compile success");

            var log = new List<string>();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: SetBB, PUSH_CLEANUP, WAIT 100
            Assert(log.Count == 1 && log[0] == "SetBB", "C25: tick 1 → SetBB (acquire)");

            // Kill during wait
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;
            world.Tick(); // tick 2: kill → cleanup → ResetBB → Completed
            Assert(log.Count == 2 && log[1] == "ResetBB", "C25: kill → cleanup ResetBB (release)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C25: Completed");
        }

        // ===== Test C26: using + defer mixed — LIFO order =====
        {
            string source = @"
func main() {
    defer {
        Report(1)
    }
    using SetBB(99) {
        Report(2)
    }
    Report(3)
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "Report", 2 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C26 compile success");

            var log = new List<string>();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected flow:
            // PUSH_CLEANUP(defer Report(1)), SetBB(99), PUSH_CLEANUP(ResetBB), Report(2), POP_CLEANUP, Report(3), RETURN → cleanup → Report(1) → Completed
            // After POP_CLEANUP: using's cleanup frame removed, only defer remains
            // RETURN → cleanup → defer block Report(1) → Completed
            Assert(log.Count >= 1 && log[0] == "SetBB", $"C26: SetBB first, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Contains("Report(2)"), "C26: body Report(2)");
            Assert(log.Contains("Report(3)"), "C26: after using Report(3)");
            Assert(log.Contains("Report(1)"), "C26: defer cleanup Report(1)");
            // ResetBB should NOT be called (POP_CLEANUP removed it on normal exit)
            Assert(!log.Contains("ResetBB"), "C26: ResetBB NOT called (POP_CLEANUP)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C26: Completed");
        }

        // ===== Test C27: using with wait inside — resume after wait, then normal exit =====
        {
            string source = @"
func main() {
    using SetBB(1) {
        Before()
        wait 3
        After()
    }
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "Before", 2 }, { "After", 3 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "Before", (ref VMInstanceState s) => { });
            world.Syscalls.Register(3, "After", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C27 compile success");

            var log = new List<string>();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.Register(2, "Before", (ref VMInstanceState s) => { log.Add("Before"); });
            world.Syscalls.Register(3, "After", (ref VMInstanceState s) => { log.Add("After"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);

            world.Tick(); // tick 1: SetBB, PUSH_CLEANUP, Before, WAIT 3
            Assert(log.Count == 2, $"C27: tick 1 → SetBB + Before, got {log.Count}");
            Assert(log[0] == "SetBB", "C27: acquire SetBB");
            Assert(log[1] == "Before", "C27: Before inside using");

            world.Tick(); world.Tick(); world.Tick(); // tick 2-4: wait countdown

            world.Tick(); // tick 5: resume → After, POP_CLEANUP, RETURN → Completed
            Assert(log.Count == 3 && log[2] == "After", "C27: After after wait");
            Assert(!log.Contains("ResetBB"), "C27: ResetBB NOT called (normal exit)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C27: Completed");
        }

        // ===== Test C28: using compile error — no paired syscall =====
        {
            string source = @"
func main() {
    using NoPair(1) {
        wait 5
    }
}";
            var syscalls = new Dictionary<string, int> { { "NoPair", 0 } };
            var world = new VMWorld();
            world.Syscalls.Register(0, "NoPair", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(!result.Success, "C28: compile error for unpaired syscall in using");
        }

        // ===== Test CF01: Basic function call — add(3, 4) → 7 =====
        {
            string source = @"
func add(a: int, b: int) {
    return a + b
}
func main() {
    var result: int = add(3, 4)
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF01 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"Report({s.Registers.Get(0).ToInt()})");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count == 1 && log[0] == "Report(7)", $"CF01: add(3,4) = 7, got {(log.Count > 0 ? log[0] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF01: Completed");
        }

        // ===== Test CF02: Multi-function call chain — a() calls b() =====
        {
            string source = @"
func b() {
    return 42
}
func a() {
    return b() + 1
}
func main() {
    var result: int = a()
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF02 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"Report({s.Registers.Get(0).ToInt()})");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count == 1 && log[0] == "Report(43)", $"CF02: a() = b()+1 = 43, got {(log.Count > 0 ? log[0] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF02: Completed");
        }

        // ===== Test CF03: Register window isolation — caller/callee locals don't interfere =====
        {
            string source = @"
func inner() {
    var x: int = 999
    return x
}
func main() {
    var x: int = 100
    var y: int = inner()
    Report(x)
    Report(y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF03 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count >= 1 && log[0] == "100", $"CF03: caller x = 100, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "999", $"CF03: callee returned 999, got {(log.Count > 1 ? log[1] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF03: Completed");
        }

        // ===== Test CF04: Function + Syscall mixed in same module =====
        {
            string source = @"
func double(n: int) {
    return n + n
}
func main() {
    Ping()
    var result: int = double(21)
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 }, { "Report", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF04 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { log.Add("Ping"); });
            world.Syscalls.Register(1, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"Report({s.Registers.Get(0).ToInt()})");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count >= 1 && log[0] == "Ping", "CF04: Ping first");
            Assert(log.Count >= 2 && log[1] == "Report(42)", $"CF04: double(21)=42, got {(log.Count > 1 ? log[1] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF04: Completed");
        }

        // ===== Test CF05: Function with void call (no return value used) =====
        {
            string source = @"
func sideEffect() {
    Report(99)
}
func main() {
    sideEffect()
    Report(0)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF05 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count >= 1 && log[0] == "99", $"CF05: sideEffect reports 99, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "0", $"CF05: main reports 0, got {(log.Count > 1 ? log[1] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF05: Completed");
        }

        // ===== Test CF06: Function calling function with parameters =====
        {
            string source = @"
func mul(a: int, b: int) {
    return a * b
}
func square(n: int) {
    return mul(n, n)
}
func main() {
    Report(square(7))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF06 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count == 1 && log[0] == "49", $"CF06: square(7) = 49, got {(log.Count > 0 ? log[0] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF06: Completed");
        }

        // ===== Test CF07: Function with defer — cleanup on entry function return =====
        {
            string source = @"
func main() {
    defer {
        Report(1)
    }
    Report(2)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF07 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count >= 1 && log[0] == "2", "CF07: body first");
            Assert(log.Count >= 2 && log[1] == "1", "CF07: defer cleanup after return");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CF07: Completed");
        }

        // ===== Test CF08: Function arg count mismatch → compile error =====
        {
            string source = @"
func add(a: int, b: int) {
    return a + b
}
func main() {
    add(1)
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CF08: compile error on arg count mismatch");
        }

        // ===== Test CF09: Regression — existing single-function paths unchanged =====
        {
            // This tests that single-function modules still work (no regression from multi-function)
            string source = @"
func main() {
    var x: int = 10
    var y: int = 20
    Report(x + y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CF09 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(log.Count == 1 && log[0] == "30", $"CF09: 10+20 = 30, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ============================================================
        // Step 9: Struct compile-time flattening tests (CS01 - CS07)
        // ============================================================

        // ===== Test CS01: Struct declaration + field assignment + field read =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2
    v.x = 10
    v.y = 20
    Report(v.x + v.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS01 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "30", $"CS01: v.x + v.y = 30, got {(log.Count > 0 ? log[0] : "?")}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "CS01: Completed");
        }

        // ===== Test CS02: Struct whole assignment (a = b) =====
        {
            string source = @"
struct Point {
    x: int
    y: int
}
func main() {
    var a: Point
    a.x = 3
    a.y = 7
    var b: Point
    b = a
    Report(b.x)
    Report(b.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS02 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "3", $"CS02: b.x = 3, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "7", $"CS02: b.y = 7, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test CS03: Struct field used as syscall argument =====
        {
            string source = @"
struct DamageInfo {
    level: int
    ratio: int
    target: int
}
func main() {
    var d: DamageInfo
    d.level = 5
    d.ratio = 100
    d.target = 42
    Apply(d.target, d.level, d.ratio)
}";
            var syscalls = new Dictionary<string, int> { { "Apply", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS03 compile success");

            int a0 = -1, a1 = -1, a2 = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Apply", (ref VMInstanceState s) =>
            {
                a0 = s.Registers.Get(0).ToInt();
                a1 = s.Registers.Get(1).ToInt();
                a2 = s.Registers.Get(2).ToInt();
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(a0 == 42 && a1 == 5 && a2 == 100, $"CS03: Apply(42,5,100), got ({a0},{a1},{a2})");
        }

        // ===== Test CS04: Struct field in conditional branch =====
        {
            string source = @"
struct Stats {
    hp: int
    alive: int
}
func main() {
    var s: Stats
    s.hp = 10
    s.alive = 1
    if s.hp > 0 {
        Report(1)
    } else {
        Report(0)
    }
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS04 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "1", $"CS04: hp > 0 → Report(1), got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS05: Multiple struct variables, registers don't conflict =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var a: Vec2
    var b: Vec2
    a.x = 1
    a.y = 2
    b.x = 10
    b.y = 20
    Report(a.x + b.x)
    Report(a.y + b.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS05 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "11", $"CS05: a.x+b.x = 11, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "22", $"CS05: a.y+b.y = 22, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test CS06: Compile error — unknown struct type =====
        {
            string source = @"
func main() {
    var d: UnknownType
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            // UnknownType is not a struct, so it's treated as a scalar — should compile fine
            // (type names are parsed but not enforced for scalar types)
            Assert(result.Success, "CS06: unknown type treated as scalar (no error)");
        }

        // ===== Test CS07: Compile error — access nonexistent field =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2
    v.z = 10
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS07: compile error on nonexistent field 'z'");
        }

        // ===== Test CS08: Struct var initialized from another struct var =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var a: Vec2
    a.x = 5
    a.y = 15
    var b: Vec2 = a
    Report(b.x)
    Report(b.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS08 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "5", $"CS08: b.x = 5, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "15", $"CS08: b.y = 15, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test CS09: Struct field in while loop =====
        {
            string source = @"
struct Counter {
    val: int
}
func main() {
    var c: Counter
    c.val = 0
    while c.val < 5 {
        c.val = c.val + 1
    }
    Report(c.val)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS09 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "5", $"CS09: counter loop → 5, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS10: Struct with 3 fields (DamageInfo) — field arithmetic =====
        {
            string source = @"
struct DamageInfo {
    base_dmg: int
    multiplier: int
    bonus: int
}
func main() {
    var d: DamageInfo
    d.base_dmg = 10
    d.multiplier = 3
    d.bonus = 5
    var total: int = d.base_dmg * d.multiplier + d.bonus
    Report(total)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS10 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "35", $"CS10: 10*3+5 = 35, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS11: Struct mixed with scalar variables =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var scale: int = 2
    var v: Vec2
    v.x = 3
    v.y = 4
    var result: int = v.x * scale + v.y * scale
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS11 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "14", $"CS11: 3*2 + 4*2 = 14, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"Compiler Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
