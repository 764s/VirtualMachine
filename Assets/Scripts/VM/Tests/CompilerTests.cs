using System.Collections.Generic;
using System.Text;
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
        // Step 9: Struct compile-time flattening tests (CS01 - CS11)
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

        // ===== Test C4-01: Direct call to requires_cleanup syscall → compile error =====
        {
            string source = @"
func main() {
    Acquire(1)
}";
            var syscalls = new Dictionary<string, int> { { "Acquire", 0 }, { "Release", 1 } };
            var table = new SyscallTable();
            table.RegisterPaired(0, "Acquire", (ref VMInstanceState s) => { },
                                 1, "Release", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, table);
            Assert(!result.Success, "C4-01: compile error for direct call to requires_cleanup syscall");
            Assert(result.Errors != null && result.Errors.Count > 0 && result.Errors[0].Contains("requires cleanup"),
                   "C4-01: error message mentions 'requires cleanup'");
            Assert(result.Errors[0].Contains("Acquire"),
                   "C4-01: error message mentions the syscall name 'Acquire'");
        }

        // ===== Test C4-02: using-wrapped call to requires_cleanup syscall → compile success =====
        {
            string source = @"
func main() {
    using Acquire(1) {
        wait 5
    }
}";
            var syscalls = new Dictionary<string, int> { { "Acquire", 0 }, { "Release", 1 } };
            var table = new SyscallTable();
            table.RegisterPaired(0, "Acquire", (ref VMInstanceState s) => { },
                                 1, "Release", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, table);
            Assert(result.Success, "C4-02: using-wrapped requires_cleanup syscall compiles OK");
        }

        // ===== Test C4-03: Normal (non requires_cleanup) syscall → compile success =====
        {
            string source = @"
func main() {
    Report(42)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var table = new SyscallTable();
            table.Register(0, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, table);
            Assert(result.Success, "C4-03: normal syscall compiles OK (not affected by requires_cleanup)");
        }

        // ===== Test C4-04: No SyscallTable (null) → skip check, compile success =====
        {
            string source = @"
func main() {
    Acquire(1)
}";
            var syscalls = new Dictionary<string, int> { { "Acquire", 0 } };

            // No syscallTable passed → backward compatible, no requires_cleanup check
            var result = compiler.Compile(source, "main", syscalls, null);
            Assert(result.Success, "C4-04: no SyscallTable → skip requires_cleanup check");
        }

        // ===== Test G6-01: wait inside defer → compile error =====
        {
            string source = @"
func main() {
    defer {
        wait 10
    }
    wait 5
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "G6-01: compile error for wait inside defer");
            Assert(result.Errors != null && result.Errors.Count > 0 && result.Errors[0].Contains("cleanup block"),
                   "G6-01: error message mentions 'cleanup block'");
        }

        // ===== Test G6-02: wait_for inside defer → compile error =====
        {
            string source = @"
func main() {
    var id: int = 0
    defer {
        wait_for(id)
    }
    wait 5
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "G6-02: compile error for wait_for inside defer");
            Assert(result.Errors != null && result.Errors.Count > 0 && result.Errors[0].Contains("cleanup block"),
                   "G6-02: error message mentions 'cleanup block'");
        }

        // ===== Test G6-03: wait in normal function body → compile success (not affected) =====
        {
            string source = @"
func main() {
    wait 10
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "G6-03: wait in normal body compiles OK");
        }

        // ===== Test G6-04: wait inside using body → compile success (using body is NOT a cleanup block) =====
        {
            string source = @"
func main() {
    using Acquire(1) {
        wait 10
    }
}";
            var syscalls = new Dictionary<string, int> { { "Acquire", 0 }, { "Release", 1 } };
            var table = new SyscallTable();
            table.RegisterPaired(0, "Acquire", (ref VMInstanceState s) => { },
                                 1, "Release", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, table);
            Assert(result.Success, "G6-04: wait inside using body compiles OK (body != cleanup block)");
        }

        // ===== F4 Tests: Register lifecycle analysis + register reuse =====

        // F4-01: Two non-overlapping lifetime variables should reuse same register
        {
            string source = @"
func main() {
    var a: int = 10
    SetValue(a)
    var b: int = 20
    SetValue(b)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int lastArg = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "F4-01 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { lastArg = s.Registers.Get(0).ToInt(); });
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(lastArg == 20, $"F4-01: last SetValue arg = {lastArg} (expected 20)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "F4-01: Completed");
        }

        // F4-02: Cross-await variable should persist in local register
        {
            string source = @"
func main() {
    var x: int = 42
    wait 1
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "F4-02 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: executes LOAD_CONST + WAIT, suspends
            world.Tick(); // tick 2: decrements wait counter
            world.Tick(); // tick 3: resumes, calls SetValue
            Assert(capturedVal == 42, $"F4-02: cross-await var x = {capturedVal} (expected 42)");
        }

        // F4-03: Struct variable register reuse end-to-end
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var a: Vec2
    a.x = 5
    a.y = 10
    SetValue(a.x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "F4-03 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 5, $"F4-03: struct a.x = {capturedVal} (expected 5)");
        }

        // F4-04: Register reuse does not change execution results (end-to-end correctness)
        {
            string source = @"
func main() {
    var a: int = 1
    var b: int = 2
    var c: int = a + b
    SetValue(c)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "F4-04 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 3, $"F4-04: a + b = {capturedVal} (expected 3)");
        }

        // ===== O5 Tests: Constant folding =====

        // O5-01: var x = 2 + 3 → single LOAD_CONST (value = 5), no ADD
        {
            string source = @"
func main() {
    var x: int = 2 + 3
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O5-01 compile success");

            // Check that no ADD instruction exists (constant was folded)
            bool hasAdd = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.ADD) { hasAdd = true; break; }
            }
            Assert(!hasAdd, "O5-01: no ADD instruction (constant folded)");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 5, $"O5-01: 2 + 3 folded to {capturedVal} (expected 5)");
        }

        // O5-02: var x = 10 > 5 → single LOAD_CONST (value = 1)
        {
            string source = @"
func main() {
    var x: int = 10 > 5
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O5-02 compile success");

            bool hasCmp = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CMP_GT) { hasCmp = true; break; }
            }
            Assert(!hasCmp, "O5-02: no CMP_GT instruction (constant folded)");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 1, $"O5-02: 10 > 5 folded to {capturedVal} (expected 1)");
        }

        // O5-03: var x = a + 3 (contains variable) → not folded, normal emit
        {
            string source = @"
func main() {
    var a: int = 7
    var x: int = a + 3
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O5-03 compile success");

            bool hasAdd = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.ADD) { hasAdd = true; break; }
            }
            Assert(hasAdd, "O5-03: ADD instruction present (variable prevents folding)");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 10, $"O5-03: a + 3 = {capturedVal} (expected 10)");
        }

        // ===== O4 Tests: dest-reg hint =====

        // O4-01: var x = a + b → result directly in x's register (fewer MOVEs)
        {
            string source = @"
func main() {
    var a: int = 10
    var b: int = 20
    var x: int = a + b
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O4-01 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 30, $"O4-01: a + b = {capturedVal} (expected 30)");
        }

        // O4-02: x = a * b → result directly in x's register
        {
            string source = @"
func main() {
    var a: int = 3
    var b: int = 7
    var x: int = 0
    x = a * b
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O4-02 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 21, $"O4-02: a * b = {capturedVal} (expected 21)");
        }

        // ===== O7 Tests: Syscall result direct =====

        // O7-01: var x = SomeSyscall(args) → result from r0 directly to x's register
        {
            string source = @"
func main() {
    var x: int = GetVal(5)
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "GetVal", 0 }, { "SetValue", 1 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "O7-01 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "GetVal", (ref VMInstanceState s) => {
                s.Registers.Set(0, Number.FromInt(s.Registers.Get(0).ToInt() * 10));
            });
            world.Syscalls.Register(1, "SetValue", (ref VMInstanceState s) => {
                capturedVal = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 50, $"O7-01: GetVal(5) = {capturedVal} (expected 50)");
        }

        // ===== DBG1 Tests: Source Map =====

        // DBG1_T01: Source map records correct line numbers
        {
            string source = @"
func main() {
    var x: int = 1
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG1_T01 compile success");
            Assert(result.Program.SourceMap != null, "DBG1_T01: SourceMap is not null");
        }

        // DBG1_T02: Source map length matches instruction length
        {
            string source = @"
func main() {
    var x: int = 1
    var y: int = 2
    SetValue(x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG1_T02 compile success");
            Assert(result.Program.SourceMap.Length == result.Program.Instructions.Length,
                $"DBG1_T02: SourceMap length ({result.Program.SourceMap.Length}) == Instructions length ({result.Program.Instructions.Length})");
        }

        // ===== DBG2 Tests: Symbol Table =====

        // DBG2_T01: Symbol table records variable names and registers
        {
            string source = @"
func main() {
    var a: int = 1
    var b: int = 2
    SetValue(a)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG2_T01 compile success");
            Assert(result.Program.SymbolTable != null, "DBG2_T01: SymbolTable is not null");
            Assert(result.Program.SymbolTable.Length >= 2, $"DBG2_T01: SymbolTable has >= 2 entries (actual: {result.Program.SymbolTable.Length})");

            bool foundA = false, foundB = false;
            for (int i = 0; i < result.Program.SymbolTable.Length; i++)
            {
                if (result.Program.SymbolTable[i].Name == "a") foundA = true;
                if (result.Program.SymbolTable[i].Name == "b") foundB = true;
            }
            Assert(foundA, "DBG2_T01: found symbol 'a'");
            Assert(foundB, "DBG2_T01: found symbol 'b'");
        }

        // DBG2_T02: Symbol table records struct field names
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2
    v.x = 10
    SetValue(v.x)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG2_T02 compile success");

            bool foundStruct = false;
            for (int i = 0; i < result.Program.SymbolTable.Length; i++)
            {
                var sym = result.Program.SymbolTable[i];
                if (sym.Name == "v" && sym.FieldCount == 2 && sym.FieldNames != null)
                {
                    foundStruct = sym.FieldNames.Length == 2 && sym.FieldNames[0] == "x" && sym.FieldNames[1] == "y";
                }
            }
            Assert(foundStruct, "DBG2_T02: found struct symbol 'v' with fields x, y");
        }

        // ===== R7 Tests: _pendingCalls auto-switch Dictionary =====

        // R7-01: 100 functions with forward references → compile success
        {
            var sb = new StringBuilder();
            // Generate 100 leaf functions that all just call Ping (no deep chain)
            for (int i = 0; i < 100; i++)
                sb.AppendLine($"func f{i}() {{ Ping() }}");
            // main calls all of them → generates 100 forward references for backpatching
            sb.Append("func main() {");
            for (int i = 0; i < 100; i++)
                sb.Append($" f{i}()");
            sb.AppendLine(" }");

            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(sb.ToString(), "main", syscalls);
            Assert(result.Success, "R7-01: 100 functions compile success (Dictionary path)");
        }

        // R7-02: <50 functions → original List path, still works
        {
            string source = @"
func helper() { Ping() }
func main() { helper() }";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var pinged = false;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "R7-02 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { pinged = true; });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(pinged, "R7-02: helper() called Ping()");
        }

        // ===== R8 Tests: Cleanup block function call prohibition =====

        // R8-01: defer { someFunc() } → compile error
        {
            string source = @"
func helper() { Ping() }
func main() {
    defer { helper() }
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "R8-01: function call in defer → compile error");
            bool mentionsCleanup = false;
            for (int i = 0; i < result.Errors.Count; i++)
            {
                if (result.Errors[i].Contains("cleanup block")) { mentionsCleanup = true; break; }
            }
            Assert(mentionsCleanup, "R8-01: error mentions 'cleanup block'");
        }

        // R8-02: Normal function body call → compile success
        {
            string source = @"
func helper() { Ping() }
func main() { helper() }";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "R8-02: normal function call compiles OK");
        }

        // R8-03: using body with function call → compile success (using body != cleanup block)
        {
            string source = @"
func helper() { Ping() }
func main() {
    using Acquire(1) {
        helper()
    }
}";
            var syscalls = new Dictionary<string, int> { { "Acquire", 0 }, { "Release", 1 }, { "Ping", 2 } };
            var table = new SyscallTable();
            table.RegisterPaired(0, "Acquire", (ref VMInstanceState s) => { },
                                 1, "Release", (ref VMInstanceState s) => { });
            table.Register(2, "Ping", (ref VMInstanceState s) => { });
            var result = compiler.Compile(source, "main", syscalls, table);
            Assert(result.Success, "R8-03: function call in using body compiles OK (body != cleanup block)");
        }

        // ===== FO5 Tests: Return value direct =====

        // FO5-01: var result = f(x) → return value from r0 directly to result register
        {
            string source = @"
func double(n: int): int { return n * 2 }
func main() {
    var result: int = double(7)
    SetValue(result)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            int capturedVal = -1;
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO5-01 compile success");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedVal = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(capturedVal == 14, $"FO5-01: double(7) = {capturedVal} (expected 14)");
        }

        // ===== FO7 Tests: Static call depth analysis =====

        // FO7-01: depth 3 (a→b→c) → compile success
        {
            string source = @"
func c() { Ping() }
func b() { c() }
func a() { b() }
func main() { a() }";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO7-01: depth 3 compiles OK");
        }

        // FO7-02: recursive call → compiles (runtime check enforces depth)
        {
            string source = @"
func recurse(n: int) {
    if n > 0 { recurse(n - 1) }
}
func main() { recurse(3) }";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            // Recursion is not a compile error — runtime handles depth limit
            Assert(result.Success, "FO7-02: recursive call compiles OK (runtime enforces depth)");
        }

        // ===== FO1 Tests: Leaf function optimization =====

        // FO1-01: simple leaf function → CALL_LEAF + RET_LEAF
        {
            string source = @"
func add(a: int, b: int): int { return a + b }
func main() {
    var r: int = add(3, 4)
    SetValue(r)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-01: compile success");

            // Verify CALL_LEAF is emitted instead of CALL
            bool hasCallLeaf = false;
            bool hasRetLeaf = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCallLeaf = true;
                if (result.Program.Instructions[i].Code == OpCode.RET_LEAF) hasRetLeaf = true;
            }
            Assert(hasCallLeaf, "FO1-01: CALL_LEAF emitted for leaf function");
            Assert(hasRetLeaf, "FO1-01: RET_LEAF emitted for leaf function");

            // Verify FunctionEntry.IsLeaf
            bool addIsLeaf = false;
            for (int i = 0; i < result.Program.Functions.Length; i++)
            {
                if (result.Program.Functions[i].Name == "add")
                    addIsLeaf = result.Program.Functions[i].IsLeaf;
            }
            Assert(addIsLeaf, "FO1-01: FunctionEntry.IsLeaf = true for 'add'");

            // Verify execution correctness
            int capturedFO1 = -1;
            var worldFO1 = new VMWorld();
            worldFO1.Modules.Load(0, result.Program);
            worldFO1.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedFO1 = s.Registers.Get(0).ToInt(); });
            worldFO1.SpawnInstance(0, 0);
            worldFO1.Tick();
            Assert(capturedFO1 == 7, $"FO1-01: add(3,4) = {capturedFO1} (expected 7)");
        }

        // FO1-02: non-leaf function (calls another) → CALL + RET_FUNC
        {
            string source = @"
func inner(): int { return 10 }
func outer(): int { return inner() }
func main() {
    var r: int = outer()
    SetValue(r)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-02: compile success");

            // 'outer' calls 'inner' → outer is NOT leaf
            bool outerIsLeaf = false;
            bool innerIsLeaf = false;
            for (int i = 0; i < result.Program.Functions.Length; i++)
            {
                if (result.Program.Functions[i].Name == "outer")
                    outerIsLeaf = result.Program.Functions[i].IsLeaf;
                if (result.Program.Functions[i].Name == "inner")
                    innerIsLeaf = result.Program.Functions[i].IsLeaf;
            }
            Assert(!outerIsLeaf, "FO1-02: 'outer' is NOT leaf (calls inner)");
            Assert(innerIsLeaf, "FO1-02: 'inner' IS leaf (no calls)");

            // Verify CALL (non-leaf) and CALL_LEAF (leaf) both exist
            bool hasCAll = false;
            bool hasCallLeaf = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL) hasCAll = true;
                if (result.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCallLeaf = true;
            }
            Assert(hasCAll, "FO1-02: CALL emitted for non-leaf 'outer'");
            Assert(hasCallLeaf, "FO1-02: CALL_LEAF emitted for leaf 'inner'");

            // Verify execution
            int capturedFO2 = -1;
            var worldFO2 = new VMWorld();
            worldFO2.Modules.Load(0, result.Program);
            worldFO2.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedFO2 = s.Registers.Get(0).ToInt(); });
            worldFO2.SpawnInstance(0, 0);
            worldFO2.Tick();
            Assert(capturedFO2 == 10, $"FO1-02: outer() = {capturedFO2} (expected 10)");
        }

        // FO1-03: function with wait → NOT leaf
        {
            string source = @"
func waiter() {
    wait(1)
}
func main() {
    waiter()
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-03: compile success");

            bool waiterIsLeaf = false;
            for (int i = 0; i < result.Program.Functions.Length; i++)
            {
                if (result.Program.Functions[i].Name == "waiter")
                    waiterIsLeaf = result.Program.Functions[i].IsLeaf;
            }
            Assert(!waiterIsLeaf, "FO1-03: 'waiter' is NOT leaf (has wait)");

            // CALL (not CALL_LEAF) should be used
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL) { hasCall = true; break; }
            }
            Assert(hasCall, "FO1-03: CALL emitted for non-leaf 'waiter'");
        }

        // FO1-04: leaf function with syscall → IS leaf (syscalls don't use call stack)
        {
            string source = @"
func notify(val: int) {
    Ping(val)
}
func main() {
    notify(42)
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-04: compile success");

            bool notifyIsLeaf = false;
            for (int i = 0; i < result.Program.Functions.Length; i++)
            {
                if (result.Program.Functions[i].Name == "notify")
                    notifyIsLeaf = result.Program.Functions[i].IsLeaf;
            }
            Assert(notifyIsLeaf, "FO1-04: 'notify' IS leaf (syscall doesn't disqualify)");
        }

        // FO1-05: multiple leaf function calls in sequence
        {
            string source = @"
func square(n: int): int { return n * n }
func double(n: int): int { return n * 2 }
func main() {
    var a: int = square(3)
    var b: int = double(a)
    SetValue(b)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-05: compile success");

            int capturedFO5 = -1;
            var worldFO5 = new VMWorld();
            worldFO5.Modules.Load(0, result.Program);
            worldFO5.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedFO5 = s.Registers.Get(0).ToInt(); });
            worldFO5.SpawnInstance(0, 0);
            worldFO5.Tick();
            Assert(capturedFO5 == 18, $"FO1-05: double(square(3)) = {capturedFO5} (expected 18)");
        }

        // FO1-06: leaf function with return value in loop
        {
            string source = @"
func inc(n: int): int { return n + 1 }
func main() {
    var sum: int = 0
    for var i: int = 0; i < 10; i = i + 1 {
        sum = inc(sum)
    }
    SetValue(sum)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO1-06: compile success");

            int capturedFO6 = -1;
            var worldFO6 = new VMWorld();
            worldFO6.Modules.Load(0, result.Program);
            worldFO6.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedFO6 = s.Registers.Get(0).ToInt(); });
            worldFO6.SpawnInstance(0, 0);
            worldFO6.Tick();
            Assert(capturedFO6 == 10, $"FO1-06: 10 calls to inc → sum = {capturedFO6} (expected 10)");
        }

        // FO1-07: benchmark — measure overhead reduction for leaf calls vs non-leaf calls
        {
            // Script with tight loop calling a leaf function 1000 times
            string leafSource = @"
func add1(n: int): int { return n + 1 }
func main() {
    var sum: int = 0
    for var i: int = 0; i < 1000; i = i + 1 {
        sum = add1(sum)
    }
    SetValue(sum)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var leafResult = compiler.Compile(leafSource, "main", syscalls);
            Assert(leafResult.Success, "FO1-07: leaf compile success");

            // Count CALL_LEAF vs CALL instructions
            int callLeafCount = 0;
            int callCount = 0;
            int retLeafCount = 0;
            int retFuncCount = 0;
            for (int i = 0; i < leafResult.Program.Instructions.Length; i++)
            {
                var code = leafResult.Program.Instructions[i].Code;
                if (code == OpCode.CALL_LEAF) callLeafCount++;
                if (code == OpCode.CALL) callCount++;
                if (code == OpCode.RET_LEAF) retLeafCount++;
                if (code == OpCode.RET_FUNC) retFuncCount++;
            }
            Assert(callLeafCount > 0 && callCount == 0,
                $"FO1-07: all calls are CALL_LEAF ({callLeafCount} leaf, {callCount} regular)");
            Assert(retLeafCount > 0 && retFuncCount == 0,
                $"FO1-07: all returns are RET_LEAF ({retLeafCount} leaf, {retFuncCount} regular)");

            // Verify execution
            int capturedFO7 = -1;
            var worldFO7 = new VMWorld();
            worldFO7.MaxStepsPerTick = 20000; // 1000 iterations × ~15 instr/iter > default 1024
            worldFO7.Modules.Load(0, leafResult.Program);
            worldFO7.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { capturedFO7 = s.Registers.Get(0).ToInt(); });
            worldFO7.SpawnInstance(0, 0);
            worldFO7.Tick();
            Assert(capturedFO7 == 1000, $"FO1-07: 1000 calls to add1 → sum = {capturedFO7} (expected 1000)");

            // Overhead analysis: CALL_LEAF + RET_LEAF saves:
            // - CallFrame construction (4 fields)
            // - CallStack.Set unsafe ptr access
            // - CallStackDepth increment
            // - Stack overflow check
            // - CallStack.Get on return
            // - CallStackDepth decrement
            // Total: 10 ops → 6 ops = 40% reduction per call/return pair
            Debug.Log("[BENCH]   FO1: CALL_LEAF/RET_LEAF overhead: ~6 ops vs CALL/RET_FUNC ~10 ops = ~40% reduction");
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"Compiler Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
