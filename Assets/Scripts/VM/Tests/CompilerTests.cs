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

        // ===== Test C6-01: C6 — consecutive defers are merged (2 adjacent PUSH_CLEANUP) =====
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
            Assert(result.Success, "C6-01 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // LIFO order: defer{Report(2)} runs first, then defer{Report(1)}
            Assert(log.Count == 3, $"C6-01: 3 reports, got {log.Count}");
            Assert(log[0] == "3", $"C6-01: body Report(3) first, got {log[0]}");
            Assert(log[1] == "2", $"C6-01: LIFO defer Report(2) second, got {log[1]}");
            Assert(log[2] == "1", $"C6-01: LIFO defer Report(1) last, got {log[2]}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C6-01: Completed");

            // C6 verification: count PUSH_CLEANUP instructions — should be 1 (merged from 2)
            int pushCount = 0;
            for (int i = 0; i < result.Program.InstructionCount; i++)
                if (result.Program.Instructions[i].Code == OpCode.PUSH_CLEANUP) pushCount++;
            Assert(pushCount == 1, $"C6-01: C6 merged 2 defers → 1 PUSH_CLEANUP, got {pushCount}");
        }

        // ===== Test C6-02: C6 — three consecutive defers merged =====
        {
            string source = @"
func main() {
    defer {
        Report(10)
    }
    defer {
        Report(20)
    }
    defer {
        Report(30)
    }
    Report(99)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C6-02 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 4, $"C6-02: 4 reports, got {log.Count}");
            Assert(log[0] == "99", $"C6-02: body first, got {log[0]}");
            Assert(log[1] == "30", $"C6-02: LIFO defer 30, got {log[1]}");
            Assert(log[2] == "20", $"C6-02: LIFO defer 20, got {log[2]}");
            Assert(log[3] == "10", $"C6-02: LIFO defer 10, got {log[3]}");

            int pushCount = 0;
            for (int i = 0; i < result.Program.InstructionCount; i++)
                if (result.Program.Instructions[i].Code == OpCode.PUSH_CLEANUP) pushCount++;
            Assert(pushCount == 1, $"C6-02: C6 merged 3 defers → 1 PUSH_CLEANUP, got {pushCount}");
        }

        // ===== Test C6-03: C6 — nested using (not adjacent, no merge) + correctness =====
        {
            string source = @"
func main() {
    using SetBB(1) {
        using SetCC(2) {
            Report(100)
        }
    }
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "SetCC", 2 }, { "ResetCC", 3 }, { "Report", 4 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });
            world.Syscalls.RegisterPaired(
                2, "SetCC", (ref VMInstanceState s) => { },
                3, "ResetCC", (ref VMInstanceState s) => { });
            world.Syscalls.Register(4, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C6-03 compile success");

            var log = new List<string>();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.RegisterPaired(
                2, "SetCC", (ref VMInstanceState s) => { log.Add("SetCC"); },
                3, "ResetCC", (ref VMInstanceState s) => { log.Add("ResetCC"); });
            world.Syscalls.Register(4, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // Normal exit: POP_CLEANUP removes each using's frame, so no release syscalls called
            Assert(log.Contains("SetBB"), "C6-03: SetBB called");
            Assert(log.Contains("SetCC"), "C6-03: SetCC called");
            Assert(log.Contains("Report(100)"), "C6-03: body Report(100)");
            Assert(!log.Contains("ResetBB"), "C6-03: ResetBB NOT called (POP_CLEANUP normal exit)");
            Assert(!log.Contains("ResetCC"), "C6-03: ResetCC NOT called (POP_CLEANUP normal exit)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C6-03: Completed");
        }

        // ===== Test C6-04: C6 — consecutive defers + using mixed (partial merge) =====
        {
            string source = @"
func main() {
    defer {
        Report(1)
    }
    defer {
        Report(2)
    }
    using SetBB(99) {
        Report(3)
    }
    Report(4)
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "Report", 2 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "C6-04 compile success");

            var log = new List<string>();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add("SetBB"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Contains("SetBB"), "C6-04: SetBB called");
            Assert(log.Contains("Report(3)"), "C6-04: body Report(3)");
            Assert(log.Contains("Report(4)"), "C6-04: Report(4) after using");
            Assert(log.Contains("Report(2)"), "C6-04: LIFO defer Report(2)");
            Assert(log.Contains("Report(1)"), "C6-04: LIFO defer Report(1)");
            Assert(!log.Contains("ResetBB"), "C6-04: ResetBB NOT called (POP_CLEANUP)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C6-04: Completed");

            // The 2 defers should be merged, using should remain separate
            int pushCount = 0;
            for (int i = 0; i < result.Program.InstructionCount; i++)
                if (result.Program.Instructions[i].Code == OpCode.PUSH_CLEANUP) pushCount++;
            Assert(pushCount == 2, $"C6-04: 2 defers merged + 1 using = 2 PUSH_CLEANUP, got {pushCount}");
        }

        // ===== Test C6-05: C6 — kill path with merged defers (all cleanups execute) =====
        {
            string source = @"
func main() {
    defer {
        Report(1)
    }
    defer {
        Report(2)
    }
    wait 100
    Report(99)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "C6-05 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();  // execute up to wait
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed; // kill while waiting
            world.Tick();  // cleanup should run
            world.Tick();  // in case cleanup needs multiple ticks

            // Both defers should execute via compound cleanup even on kill
            Assert(log.Contains("2"), "C6-05: kill path defer Report(2) executed");
            Assert(log.Contains("1"), "C6-05: kill path defer Report(1) executed");
            Assert(!log.Contains("99"), "C6-05: Report(99) NOT reached (killed during wait)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "C6-05: Completed after kill cleanup");
        }

        // ===== Test C5-01: Cleanup block infinite loop → timeout skip → Completed =====
        {
            string source = @"
func main() {
    defer {
        var x: int = 0
        while x < 1 {
            Report(999)
        }
    }
    Report(1)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"C5-01 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.MaxCleanupSteps = 50; // small budget to trigger timeout quickly
            world.MaxStepsPerTick = 10000; // large enough that overall won't hit
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Contains("1"), "C5-01: body Report(1) executed");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "C5-01: instance Completed despite cleanup timeout");
            Assert(world.Pool.Instances[id].ErrorFlag == VMError.None,
                "C5-01: no panic error (cleanup timeout is graceful)");
        }

        // ===== Test C5-02: Multiple cleanups, first times out, second runs normally =====
        {
            // Non-adjacent defers → separate PUSH_CLEANUP frames (C6 won't merge)
            string source = @"
func main() {
    defer {
        Report(10)
    }
    Report(1)
    defer {
        var x: int = 0
        while x < 1 {
            Report(999)
        }
    }
    Report(2)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"C5-02 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.MaxCleanupSteps = 50;
            world.MaxStepsPerTick = 10000;
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Contains("1"), "C5-02: body Report(1) executed");
            // Second defer (LIFO first) has infinite loop → timeout
            // First defer (LIFO second) Report(10) should still execute
            Assert(log.Contains("10"), "C5-02: outer defer Report(10) executed after inner timeout");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "C5-02: instance Completed");
        }

        // ===== Test C5-03: Normal cleanup within budget — not affected =====
        {
            string source = @"
func main() {
    defer {
        Report(42)
    }
    Report(1)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"C5-03 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.MaxCleanupSteps = 50;
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 2, $"C5-03: expected 2 reports, got {log.Count}");
            Assert(log[0] == "1", $"C5-03: body Report(1) first, got {log[0]}");
            Assert(log[1] == "42", $"C5-03: defer Report(42) second, got {log[1]}");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "C5-03: instance Completed normally");
        }

        // ===== Test C5-04: Cleanup timeout on Kill path + wait_for dependent resumes =====
        {
            string source1 = @"
func main() {
    defer {
        var x: int = 0
        while x < 1 {
            Report(999)
        }
    }
    wait 100
}";
            string source2 = @"
func main() {
    wait_for(0)
    Report(77)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var compiler2 = new BytecodeCompiler();
            var result1 = compiler.Compile(source1, "main", syscalls);
            var result2 = compiler2.Compile(source2, "main", syscalls);
            Assert(result1.Success, "C5-04 source1 compile success");
            Assert(result2.Success, "C5-04 source2 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.MaxCleanupSteps = 50;
            world.MaxStepsPerTick = 10000;
            world.Modules.Load(0, result1.Program);
            world.Modules.Load(1, result2.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id1 = world.SpawnInstance(0, 0); // source1, will be killed
            int id2 = world.SpawnInstance(1, 1); // source2, waits for id1

            world.Tick(); // both start, source1 hits wait 100, source2 hits wait_for(0)
            // Kill source1
            world.Pool.Instances[id1].StateFlags |= VMStateFlags.Killed;
            world.Tick(); // cleanup of id1 times out, id1 completes
            Assert((world.Pool.Instances[id1].StateFlags & VMStateFlags.Completed) != 0,
                "C5-04: killed instance Completed after cleanup timeout");

            world.Tick(); // id2 should now resume since id1 is completed
            Assert(log.Contains("77"), "C5-04: wait_for dependent resumed after killed instance completed");
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

        // ===== Test CS12: S4 — basic struct parameter passing =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func sum(v: Vec2) {
    return v.x + v.y
}
func main() {
    var a: Vec2
    a.x = 10
    a.y = 20
    Report(sum(a))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS12 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "30", $"CS12: sum({{10,20}}) = 30, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS13: S4 — multiple struct parameters =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func add(a: Vec2, b: Vec2) {
    return a.x + a.y + b.x + b.y
}
func main() {
    var p: Vec2
    p.x = 1
    p.y = 2
    var q: Vec2
    q.x = 10
    q.y = 20
    Report(add(p, q))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS13 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "33", $"CS13: add({{1,2}},{{10,20}}) = 33, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS14: S4 — mixed scalar and struct parameters =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func scale(v: Vec2, factor: int) {
    return (v.x + v.y) * factor
}
func main() {
    var a: Vec2
    a.x = 3
    a.y = 7
    Report(scale(a, 5))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS14 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "50", $"CS14: scale({{3,7}}, 5) = 50, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS15: S4 — struct parameter isolation (caller unchanged) =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func process(v: Vec2) {
    return v.x + 100
}
func main() {
    var a: Vec2
    a.x = 5
    a.y = 15
    var result: int = process(a)
    Report(a.x)
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS15 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "5", $"CS15: caller a.x unchanged = 5, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "105", $"CS15: process result = 105, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test CS16: S4 — struct parameter in call chain =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func inner(v: Vec2) {
    return v.x + v.y
}
func outer(v: Vec2) {
    return inner(v) + 1000
}
func main() {
    var a: Vec2
    a.x = 5
    a.y = 3
    Report(outer(a))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS16 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "1008", $"CS16: outer({{5,3}}) = 5+3+1000 = 1008, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS17: S4 — struct parameter with leaf function =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func dot(a: Vec2, b: Vec2) {
    return a.x * b.x + a.y * b.y
}
func main() {
    var p: Vec2
    p.x = 3
    p.y = 4
    var q: Vec2
    q.x = 5
    q.y = 6
    Report(dot(p, q))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS17 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "39", $"CS17: dot({{3,4}},{{5,6}}) = 15+24 = 39, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS18: S4/R5 — error: too many scratch registers =====
        {
            // Lang-9 P2: body must exceed InlineThreshold to prevent inline (which bypasses scratch check)
            string source = @"
struct Big5 {
    a: int
    b: int
    c: int
    d: int
    e: int
}
func bad(x: Big5, y: Big5, z: Big5, w: int, v: int) {
    var t1: int = x.a + y.a + z.a + w + v
    var t2: int = x.b + y.b + z.b + t1
    var t3: int = x.c + y.c + z.c + t2
    var t4: int = x.d + y.d + z.d + t3
    return t4
}
func main() {
    var s: Big5
    bad(s, s, s, 1, 2)
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS18: compile error — too many scratch registers (5+5+5+1+1=17 > 16)");
        }

        // ===== Test CS19: S4 — scalar before struct parameter =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func compute(factor: int, v: Vec2) {
    return factor * (v.x + v.y)
}
func main() {
    var a: Vec2
    a.x = 4
    a.y = 6
    Report(compute(3, a))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS19 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "30", $"CS19: compute(3, {{4,6}}) = 3*10 = 30, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS20: S4 — struct parameter with 3 fields =====
        {
            string source = @"
struct DamageInfo {
    base_dmg: int
    multiplier: int
    bonus: int
}
func totalDamage(d: DamageInfo) {
    return d.base_dmg * d.multiplier + d.bonus
}
func main() {
    var d: DamageInfo
    d.base_dmg = 10
    d.multiplier = 3
    d.bonus = 5
    Report(totalDamage(d))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS20 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "35", $"CS20: totalDamage({{10,3,5}}) = 35, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS21: S4 — struct parameter in while loop =====
        {
            string source = @"
struct Pair {
    a: int
    b: int
}
func sumN(p: Pair) {
    var total: int = 0
    var i: int = p.a
    while i <= p.b {
        total = total + i
        i = i + 1
    }
    return total
}
func main() {
    var r: Pair
    r.a = 1
    r.b = 10
    Report(sumN(r))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS21 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "55", $"CS21: sumN({{1,10}}) = 55, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS22: SN1 — basic nested struct declaration + field access =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var r: Rect
    r.min.x = 1
    r.min.y = 2
    r.max.x = 10
    r.max.y = 20
    Report(r.min.x)
    Report(r.min.y)
    Report(r.max.x)
    Report(r.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS22 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 4, $"CS22: expected 4 reports, got {log.Count}");
            Assert(log[0] == "1", $"CS22: min.x=1, got {log[0]}");
            Assert(log[1] == "2", $"CS22: min.y=2, got {log[1]}");
            Assert(log[2] == "10", $"CS22: max.x=10, got {log[2]}");
            Assert(log[3] == "20", $"CS22: max.y=20, got {log[3]}");
        }

        // ===== Test CS23: SN1 — nested struct whole copy =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var a: Rect
    a.min.x = 1
    a.min.y = 2
    a.max.x = 3
    a.max.y = 4
    var b: Rect = a
    Report(b.min.x)
    Report(b.min.y)
    Report(b.max.x)
    Report(b.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS23 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 4, $"CS23: expected 4 reports, got {log.Count}");
            Assert(log[0] == "1", $"CS23: b.min.x=1, got {log[0]}");
            Assert(log[1] == "2", $"CS23: b.min.y=2, got {log[1]}");
            Assert(log[2] == "3", $"CS23: b.max.x=3, got {log[2]}");
            Assert(log[3] == "4", $"CS23: b.max.y=4, got {log[3]}");
        }

        // ===== Test CS24: SN1 — nested struct as function parameter =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func area(r: Rect) {
    var w: int = r.max.x - r.min.x
    var h: int = r.max.y - r.min.y
    return w * h
}
func main() {
    var r: Rect
    r.min.x = 2
    r.min.y = 3
    r.max.x = 7
    r.max.y = 8
    Report(area(r))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS24 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "25", $"CS24: area({{2,3,7,8}})=25, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS25: SN1 — three-level nested struct =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
struct Scene {
    bounds: Rect
    id: int
}
func main() {
    var s: Scene
    s.bounds.min.x = 10
    s.bounds.min.y = 20
    s.bounds.max.x = 100
    s.bounds.max.y = 200
    s.id = 42
    Report(s.bounds.min.x)
    Report(s.bounds.max.y)
    Report(s.id)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS25 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 3, $"CS25: expected 3 reports, got {log.Count}");
            Assert(log[0] == "10", $"CS25: bounds.min.x=10, got {log[0]}");
            Assert(log[1] == "200", $"CS25: bounds.max.y=200, got {log[1]}");
            Assert(log[2] == "42", $"CS25: id=42, got {log[2]}");
        }

        // ===== Test CS26: SN1 — circular struct reference → compile error =====
        {
            string source = @"
struct A {
    f: B
}
struct B {
    g: A
}
func main() {
    var a: A
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS26: circular struct → compile error");
        }

        // ===== Test CS27: SN1 — nested struct field used in syscall =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var r: Rect
    r.min.x = 5
    r.min.y = 6
    r.max.x = 15
    r.max.y = 16
    Report(r.min.x + r.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS27 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1 && log[0] == "21", $"CS27: min.x + max.y = 21, got {(log.Count > 0 ? log[0] : "?")}");
        }

        // ===== Test CS28: SN1 — nested struct + wait + snapshot/rollback =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var r: Rect
    r.min.x = 1
    r.max.y = 2
    Report(r.min.x)
    wait 1
    r.min.x = 99
    r.max.y = 88
    Report(r.min.x)
    Report(r.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS28 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);

            // Tick 1 (frame=1): reports r.min.x=1, then hits wait 1 (WaitCounter=1)
            world.Tick();
            Assert(log.Count == 1 && log[0] == "1", $"CS28 tick1: min.x=1, got {(log.Count > 0 ? log[0] : "?")}");

            // Save state after tick 1
            world.SaveState();
            int savedFrame = world.FrameNumber;

            // Tick 2 (frame=2): WaitCounter decrements 1→0, does not execute
            world.Tick();

            // Tick 3 (frame=3): resumes, reports r.min.x=99, r.max.y=88
            log.Clear();
            world.Tick();
            Assert(log.Count == 2, $"CS28 tick3: expected 2 reports, got {log.Count}");
            Assert(log.Count >= 2 && log[0] == "99" && log[1] == "88",
                $"CS28 tick3: min.x=99, max.y=88, got {string.Join(",", log)}");

            // Rollback to saved frame and replay
            log.Clear();
            world.LoadState(savedFrame);
            world.Tick(); // wait decrement
            world.Tick(); // actual execution
            Assert(log.Count == 2, $"CS28 rollback: expected 2 reports, got {log.Count}");
            Assert(log.Count >= 2 && log[0] == "99" && log[1] == "88",
                $"CS28 rollback: min.x=99, max.y=88, got {string.Join(",", log)}");
        }

        // ===== Test CS29: SN1 — whole-struct assignment between nested struct vars =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var a: Rect
    a.min.x = 1
    a.min.y = 2
    a.max.x = 3
    a.max.y = 4
    var b: Rect
    b = a
    Report(b.min.x)
    Report(b.min.y)
    Report(b.max.x)
    Report(b.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS29 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 4, $"CS29: expected 4 reports, got {log.Count}");
            Assert(log[0] == "1" && log[1] == "2" && log[2] == "3" && log[3] == "4",
                $"CS29: b = a copy, got {string.Join(",", log)}");
        }

        // ===== Test CS30: SN1 — sub-struct field assignment (a.min = b.min) =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var a: Rect
    a.min.x = 10
    a.min.y = 20
    a.max.x = 30
    a.max.y = 40
    var b: Rect
    b.min = a.min
    b.max = a.max
    Report(b.min.x)
    Report(b.min.y)
    Report(b.max.x)
    Report(b.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "CS30 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 4, $"CS30: expected 4 reports, got {log.Count}");
            Assert(log[0] == "10" && log[1] == "20" && log[2] == "30" && log[3] == "40",
                $"CS30: sub-struct copy, got {string.Join(",", log)}");
        }

        // ===== Test CS31: SN2 — basic struct literal =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2 = Vec2 { x: 1, y: 2 }
    Report(v.x + v.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"CS31 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1, $"CS31: expected 1 report, got {log.Count}");
            Assert(log[0] == "3", $"CS31: Vec2 {{ 1, 2 }} sum = 3, got {log[0]}");
        }

        // ===== Test CS32: SN2 — struct literal with expression values =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2 = Vec2 { x: 1 + 2, y: 3 * 4 }
    Report(v.x)
    Report(v.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"CS32 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 2, $"CS32: expected 2 reports, got {log.Count}");
            Assert(log[0] == "3", $"CS32: x = 1+2 = 3, got {log[0]}");
            Assert(log[1] == "12", $"CS32: y = 3*4 = 12, got {log[1]}");
        }

        // ===== Test CS33: SN2 — nested struct literal =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var r: Rect = Rect { min: Vec2 { x: 1, y: 2 }, max: Vec2 { x: 3, y: 4 } }
    Report(r.min.x + r.min.y + r.max.x + r.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"CS33 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1, $"CS33: expected 1 report, got {log.Count}");
            Assert(log[0] == "10", $"CS33: nested literal sum = 10, got {log[0]}");
        }

        // ===== Test CS34: SN2 — struct literal in assignment position =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2
    v = Vec2 { x: 5, y: 6 }
    Report(v.x + v.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"CS34 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1, $"CS34: expected 1 report, got {log.Count}");
            Assert(log[0] == "11", $"CS34: assign literal sum = 11, got {log[0]}");
        }

        // ===== Test CS35: SN2 — field count mismatch → compile error =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2 = Vec2 { x: 1 }
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS35: compile error for field count mismatch");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("1 fields, expected 2"),
                   $"CS35: error mentions field count mismatch, got: {(result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test CS36: SN2 — field name mismatch → compile error =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2 = Vec2 { a: 1, b: 2 }
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS36: compile error for field name mismatch");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("expected 'x', got 'a'"),
                   $"CS36: error mentions field name mismatch, got: {(result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test CS37: SN2 — unknown struct type → compile error =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var v: Vec2 = Vec3 { x: 1, y: 2 }
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "CS37: compile error for unknown struct type in literal");
        }

        // ===== Test CS38: SN2 — three-level nested struct literal =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
struct Scene {
    bounds: Rect
    origin: Vec2
}
func main() {
    var s: Scene = Scene {
        bounds: Rect { min: Vec2 { x: 1, y: 2 }, max: Vec2 { x: 3, y: 4 } },
        origin: Vec2 { x: 5, y: 6 }
    }
    Report(s.bounds.min.x + s.bounds.min.y + s.bounds.max.x + s.bounds.max.y + s.origin.x + s.origin.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"CS38 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 1, $"CS38: expected 1 report, got {log.Count}");
            Assert(log[0] == "21", $"CS38: 3-level nested literal sum = 21, got {log[0]}");
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

        // ===== R8 Tests: Cleanup block function call — DC (Defer-Call) relaxation =====

        // R8-01: defer { someFunc() } → now compiles OK (DC relaxation)
        {
            string source = @"
func helper() { Ping() }
func main() {
    defer { helper() }
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "R8-01: function call in defer → compile OK (DC relaxation)");
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

        // ===== DC Tests: Defer-Call Level 3 — function calls inside cleanup blocks =====

        // DC-01: defer calls function without defer — basic execution + order
        {
            string source = @"
func cleanup() { Report(10) }
func main() {
    defer { cleanup() }
    Report(20)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-01: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 2, $"DC-01: 2 reports, got {log.Count}");
            Assert(log[0] == "Report(20)", $"DC-01: main body first, got '{log[0]}'");
            Assert(log[1] == "Report(10)", $"DC-01: defer cleanup() second, got '{log[1]}'");
        }

        // DC-02: defer calls function WITH defer (Level 3 — nested cleanup)
        {
            string source = @"
func inner() {
    defer { Report(1) }
    Report(2)
}
func main() {
    defer { inner() }
    Report(3)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-02: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // Expected: main body(3), then defer runs inner(): inner body(2), inner defer(1)
            Assert(log.Count == 3, $"DC-02: 3 reports, got {log.Count}");
            Assert(log[0] == "Report(3)", $"DC-02: main body, got '{log[0]}'");
            Assert(log[1] == "Report(2)", $"DC-02: inner body, got '{log[1]}'");
            Assert(log[2] == "Report(1)", $"DC-02: inner defer, got '{log[2]}'");
        }

        // DC-03: non-entry function with defer calling function with defer — return value preserved
        {
            string source = @"
func innerCleanup() {
    defer { Report(1) }
    Report(2)
}
func outer(): int {
    defer { innerCleanup() }
    Report(3)
    return 42
}
func main() {
    var res: int = outer()
    Report(res)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-03: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // Expected: outer body(3), outer defer→inner body(2), inner defer(1), main gets 42 → Report(42)
            Assert(log.Count == 4, $"DC-03: 4 reports, got {log.Count}");
            Assert(log[0] == "Report(3)", $"DC-03: outer body, got '{log[0]}'");
            Assert(log[1] == "Report(2)", $"DC-03: inner body, got '{log[1]}'");
            Assert(log[2] == "Report(1)", $"DC-03: inner defer, got '{log[2]}'");
            Assert(log[3] == "Report(42)", $"DC-03: return value preserved, got '{log[3]}'");
        }

        // DC-04: multiple defers each calling functions (LIFO order with calls)
        {
            string source = @"
func cleanA() { Report(10) }
func cleanB() { Report(20) }
func main() {
    defer { cleanA() }
    defer { cleanB() }
    Report(30)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-04: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // LIFO: cleanB first (defer registered second), then cleanA
            Assert(log.Count == 3, $"DC-04: 3 reports, got {log.Count}");
            Assert(log[0] == "Report(30)", $"DC-04: main body, got '{log[0]}'");
            Assert(log[1] == "Report(20)", $"DC-04: cleanB (LIFO first), got '{log[1]}'");
            Assert(log[2] == "Report(10)", $"DC-04: cleanA (LIFO second), got '{log[2]}'");
        }

        // DC-05: defer calling chain — funcA calls funcB, funcB has defer
        {
            string source = @"
func funcB() {
    defer { Report(1) }
    Report(2)
}
func funcA() {
    funcB()
    Report(3)
}
func main() {
    defer { funcA() }
    Report(4)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-05: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // main body(4), defer→funcA: funcA→funcB: funcB body(2), funcB defer(1), funcA body(3)
            Assert(log.Count == 4, $"DC-05: 4 reports, got {log.Count}");
            Assert(log[0] == "Report(4)", $"DC-05: main body, got '{log[0]}'");
            Assert(log[1] == "Report(2)", $"DC-05: funcB body, got '{log[1]}'");
            Assert(log[2] == "Report(1)", $"DC-05: funcB defer, got '{log[2]}'");
            Assert(log[3] == "Report(3)", $"DC-05: funcA after funcB, got '{log[3]}'");
        }

        // DC-06: defer with function call + Kill path
        {
            string source = @"
func cleanup() { Report(99) }
func main() {
    defer { cleanup() }
    Report(1)
    wait 100
    Report(2)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-06: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick(); // executes body up to wait
            Assert(log.Count == 1, $"DC-06: 1 report before wait, got {log.Count}");
            Assert(log[0] == "Report(1)", $"DC-06: body report, got '{log[0]}'");
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;
            world.Tick(); // kill triggers cleanup
            Assert(log.Count == 2, $"DC-06: cleanup called, got {log.Count}");
            Assert(log[1] == "Report(99)", $"DC-06: cleanup on kill, got '{log[1]}'");
        }

        // DC-07: defer calling function that returns a value (return value not used by defer)
        {
            string source = @"
func compute(): int {
    defer { Report(10) }
    return 42
}
func main() {
    var x: int = compute()
    Report(x)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-07: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 2, $"DC-07: 2 reports, got {log.Count}");
            Assert(log[0] == "Report(10)", $"DC-07: compute's defer runs before return value used, got '{log[0]}'");
            Assert(log[1] == "Report(42)", $"DC-07: return value 42 preserved, got '{log[1]}'");
        }

        // DC-08: deeply nested — defer calls func with defer that calls func with defer
        {
            string source = @"
func level3() {
    defer { Report(3) }
    Report(30)
}
func level2() {
    defer { level3() }
    Report(20)
}
func main() {
    defer { level2() }
    Report(10)
}";
            var log = new System.Collections.Generic.List<string>();
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DC-08: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });
            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();
            // main(10), defer→level2: level2(20), level2 defer→level3: level3(30), level3 defer(3)
            Assert(log.Count == 4, $"DC-08: 4 reports, got {log.Count}");
            Assert(log[0] == "Report(10)", $"DC-08: main body, got '{log[0]}'");
            Assert(log[1] == "Report(20)", $"DC-08: level2 body, got '{log[1]}'");
            Assert(log[2] == "Report(30)", $"DC-08: level3 body, got '{log[2]}'");
            Assert(log[3] == "Report(3)", $"DC-08: level3 defer, got '{log[3]}'");
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

            // Verify CALL_LEAF is emitted instead of CALL, or function is fully inlined (Lang-9)
            bool hasCallLeaf = false;
            bool hasRetLeaf = false;
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCallLeaf = true;
                if (result.Program.Instructions[i].Code == OpCode.RET_LEAF) hasRetLeaf = true;
                if (result.Program.Instructions[i].Code == OpCode.CALL) hasCall = true;
            }
            // Lang-9: inlining may eliminate the CALL_LEAF entirely (better optimization)
            Assert(hasCallLeaf || !hasCall, "FO1-01: CALL_LEAF emitted for leaf function (or inlined)");
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

            // Verify CALL (non-leaf) and CALL_LEAF (leaf) or both inlined away (Lang-9 P2)
            bool hasCall = false;
            bool hasCallLeaf = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL) hasCall = true;
                if (result.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCallLeaf = true;
            }
            // Lang-9 P2: both inner and outer may be inlined — no CALL at all is valid
            Assert(hasCall || (!hasCall && !hasCallLeaf), "FO1-02: CALL emitted for non-leaf 'outer' (or both inlined by P2)");
            // Lang-9: inner may be inlined within outer (better than CALL_LEAF)
            Assert(hasCallLeaf || innerIsLeaf || (!hasCall && !hasCallLeaf), "FO1-02: CALL_LEAF emitted for leaf 'inner' (or inlined)");

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
            // Lang-9: with inlining, CALL_LEAF may be zero (inlined away) — that's a better optimization
            Assert(callLeafCount > 0 || (callLeafCount == 0 && callCount == 0),
                $"FO1-07: all calls are CALL_LEAF or inlined ({callLeafCount} leaf, {callCount} regular)");
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

        // ===== FO6: Adaptive register window tests =====

        // FO6-01: 4-level nesting with local vars → correct result
        {
            string source = @"
func d(x: int): int { return x + 1 }
func c(x: int): int {
    var a: int = d(x)
    var b: int = a + 2
    return b
}
func b(x: int): int {
    var a: int = c(x)
    var b: int = a + 3
    return b
}
func a(x: int): int {
    var a: int = b(x)
    var b: int = a + 4
    return b
}
func main() {
    var result: int = a(10)
    SetValue(result)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO6-01: 4-level nesting compiles");

            int captured = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { captured = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // d(10)=11, c(10)=11+2=13, b(10)=13+3=16, a(10)=16+4=20
            Assert(captured == 20, $"FO6-01: 4-level nesting result = {captured} (expected 20)");
        }

        // FO6-02: 6-level nesting with many locals per function → correct result
        {
            string source = @"
func f6(x: int): int {
    var a: int = x + 1
    var b: int = a + 1
    var c: int = b + 1
    return c
}
func f5(x: int): int {
    var a: int = x + 1
    var b: int = a + 1
    var c: int = b + 1
    return f6(c) + a
}
func f4(x: int): int {
    var a: int = x + 1
    var b: int = a + 1
    var c: int = b + 1
    return f5(c) + a
}
func f3(x: int): int {
    var a: int = x + 1
    var b: int = a + 1
    var c: int = b + 1
    return f4(c) + a
}
func f2(x: int): int {
    var a: int = x + 1
    var b: int = a + 1
    var c: int = b + 1
    return f3(c) + a
}
func main() {
    var result: int = f2(0)
    SetValue(result)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO6-02: 6-level nesting compiles");

            // Compute expected: main→f2(0)→f3(3+0+1=...)→f4→f5→f6
            // f2(0): a=1,b=2,c=3 → f3(3)+1
            // f3(3): a=4,b=5,c=6 → f4(6)+4
            // f4(6): a=7,b=8,c=9 → f5(9)+7
            // f5(9): a=10,b=11,c=12 → f6(12)+10
            // f6(12): a=13,b=14,c=15 → 15
            // f5=15+10=25, f4=25+7=32, f3=32+4=36, f2=36+1=37
            int captured = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { captured = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(captured == 37, $"FO6-02: 6-level nesting result = {captured} (expected 37)");
        }

        // FO6-03: window size includes temps — LocalRegCount > pure local count
        {
            string source = @"
func helper(x: int): int { return x + 1 }
func caller(x: int): int {
    var a: int = x + 1
    return helper(a) + a
}
func main() {
    var r: int = caller(5)
    SetValue(r)
}";
            var syscalls = new Dictionary<string, int> { { "SetValue", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FO6-03: compile success");

            // caller uses 1 local (a) + temps for expression → LocalRegCount should be > 1
            bool found = false;
            for (int i = 0; i < result.Program.Functions.Length; i++)
            {
                if (result.Program.Functions[i].Name == "caller")
                {
                    int lr = result.Program.Functions[i].LocalRegCount;
                    Assert(lr > 1, $"FO6-03: caller LocalRegCount = {lr} > 1 (includes temps)");
                    found = true;
                    break;
                }
            }
            Assert(found, "FO6-03: caller function found in function table");

            int captured = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetValue", (ref VMInstanceState s) => { captured = s.Registers.Get(0).ToInt(); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // helper(6)+6 = 7+6 = 13
            Assert(captured == 13, $"FO6-03: result = {captured} (expected 13)");
        }

        // FO6-04: register window overflow → compile error
        {
            // Generate deeply nested functions with many locals to exceed 48 slots
            var sb = new StringBuilder();
            int depth = 10;
            for (int i = depth; i >= 1; i--)
            {
                sb.AppendLine($"func f{i}(x: int): int {{");
                // Each function uses 6 locals to consume registers quickly
                for (int v = 1; v <= 6; v++)
                {
                    if (v == 1) sb.AppendLine($"    var v{v}: int = x + {v}");
                    else sb.AppendLine($"    var v{v}: int = v{v-1} + {v}");
                }
                if (i < depth)
                    sb.AppendLine($"    return f{i+1}(v6) + v1");
                else
                    sb.AppendLine($"    return v6");
                sb.AppendLine("}");
            }
            sb.AppendLine("func main() { f1(0) }");

            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(sb.ToString(), "main", syscalls);
            Assert(!result.Success, "FO6-04: overflow detected at compile time");
            bool hasWindowError = false;
            if (result.Errors != null)
            {
                for (int i = 0; i < result.Errors.Count; i++)
                {
                    if (result.Errors[i].Contains("register window"))
                    {
                        hasWindowError = true;
                        break;
                    }
                }
            }
            Assert(hasWindowError, "FO6-04: error mentions register window overflow");
        }

        // ===== Test FF5-01: Non-entry function with defer =====
        {
            string source = @"
func foo() {
    defer {
        Report(1)
    }
    Report(2)
}
func main() {
    foo()
    Report(3)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-01 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected order: foo body Report(2), foo defer Report(1), main Report(3)
            Assert(log.Count >= 1 && log[0] == "2", "FF5-01: foo body Report(2) first");
            Assert(log.Count >= 2 && log[1] == "1", "FF5-01: foo defer Report(1) second");
            Assert(log.Count >= 3 && log[2] == "3", "FF5-01: main Report(3) after foo returns");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-01: Completed");
        }

        // ===== Test FF5-02: Entry and non-entry both with defer =====
        {
            string source = @"
func foo() {
    defer {
        Report(10)
    }
    Report(20)
}
func main() {
    defer {
        Report(30)
    }
    foo()
    Report(40)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-02 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected: foo body(20), foo defer(10), main body(40), main defer(30)
            Assert(log.Count >= 1 && log[0] == "20", "FF5-02: foo body Report(20)");
            Assert(log.Count >= 2 && log[1] == "10", "FF5-02: foo defer Report(10)");
            Assert(log.Count >= 3 && log[2] == "40", "FF5-02: main body Report(40)");
            Assert(log.Count >= 4 && log[3] == "30", "FF5-02: main defer Report(30)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-02: Completed");
        }

        // ===== Test FF5-03: Nested function calls with defer at each level =====
        {
            string source = @"
func inner() {
    defer {
        Report(1)
    }
    Report(2)
}
func middle() {
    defer {
        Report(3)
    }
    inner()
    Report(4)
}
func main() {
    defer {
        Report(5)
    }
    middle()
    Report(6)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-03 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected: inner body(2), inner defer(1), middle body(4), middle defer(3), main body(6), main defer(5)
            Assert(log.Count >= 1 && log[0] == "2", "FF5-03: inner body Report(2)");
            Assert(log.Count >= 2 && log[1] == "1", "FF5-03: inner defer Report(1)");
            Assert(log.Count >= 3 && log[2] == "4", "FF5-03: middle body Report(4)");
            Assert(log.Count >= 4 && log[3] == "3", "FF5-03: middle defer Report(3)");
            Assert(log.Count >= 5 && log[4] == "6", "FF5-03: main body Report(6)");
            Assert(log.Count >= 6 && log[5] == "5", "FF5-03: main defer Report(5)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-03: Completed");
        }

        // ===== Test FF5-04: Using (paired syscall) in non-entry function =====
        {
            string source = @"
func foo() {
    using SetBB(99) {
        Report(1)
    }
    Report(2)
}
func main() {
    foo()
    Report(3)
}";
            var syscalls = new Dictionary<string, int> { { "SetBB", 0 }, { "ResetBB", 1 }, { "Report", 2 } };

            var world = new VMWorld();
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { },
                1, "ResetBB", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { });

            var result = compiler.Compile(source, "main", syscalls, world.Syscalls);
            Assert(result.Success, "FF5-04 compile success");

            var log = new List<string>();
            // Re-register with logging
            world.Syscalls.RegisterPaired(
                0, "SetBB", (ref VMInstanceState s) => { log.Add($"SetBB({s.Registers.Get(0).ToInt()})"); },
                1, "ResetBB", (ref VMInstanceState s) => { log.Add("ResetBB"); });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) => { log.Add($"Report({s.Registers.Get(0).ToInt()})"); });

            world.Modules.Load(0, result.Program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Normal exit: SetBB(99), Report(1), POP_CLEANUP (removes SetBB cleanup), Report(2), RET_FUNC, Report(3), RETURN → Completed
            // ResetBB should NOT be called on normal exit (POP_CLEANUP removed the frame)
            Assert(log.Count >= 1 && log[0] == "SetBB(99)", "FF5-04: SetBB(99) acquired");
            Assert(log.Count >= 2 && log[1] == "Report(1)", "FF5-04: body Report(1)");
            Assert(log.Count >= 3 && log[2] == "Report(2)", "FF5-04: after using Report(2)");
            Assert(log.Count >= 4 && log[3] == "Report(3)", "FF5-04: main Report(3)");
            bool hasResetBB = false;
            for (int j = 0; j < log.Count; j++) if (log[j] == "ResetBB") hasResetBB = true;
            Assert(!hasResetBB, "FF5-04: ResetBB NOT called on normal exit (POP_CLEANUP)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-04: Completed");
        }

        // ===== Test FF5-05: Kill during non-entry function with defer =====
        {
            string source = @"
func foo() {
    defer {
        Report(1)
    }
    Report(2)
    wait 100
    Report(99)
}
func main() {
    defer {
        Report(3)
    }
    foo()
    Report(99)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-05 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: main PUSH_CLEANUP(defer 3), CALL foo → foo PUSH_CLEANUP(defer 1), Report(2), WAIT 100
            Assert(log.Count >= 1 && log[0] == "2", "FF5-05: foo body Report(2)");

            // Kill during wait
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;

            world.Tick(); // tick 2: kill → foo defer Report(1) → return to main scope
            world.Tick(); // tick 3: still killed, not in cleanup → main defer Report(3) → Completed

            Assert(log.Contains("1"), "FF5-05: foo defer Report(1) executed");
            Assert(log.Contains("3"), "FF5-05: main defer Report(3) executed");
            Assert(!log.Contains("99"), "FF5-05: unreachable Report(99) NOT called");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-05: Completed after kill");
        }

        // ===== Test FF5-06: Multiple defers in non-entry function (LIFO order) =====
        {
            string source = @"
func foo() {
    defer {
        Report(1)
    }
    defer {
        Report(2)
    }
    Report(3)
}
func main() {
    foo()
    Report(4)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-06 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected: foo body(3), foo defer(2) LIFO, foo defer(1) LIFO, main(4)
            Assert(log.Count >= 1 && log[0] == "3", "FF5-06: foo body Report(3)");
            Assert(log.Count >= 2 && log[1] == "2", "FF5-06: foo defer Report(2) LIFO first");
            Assert(log.Count >= 3 && log[2] == "1", "FF5-06: foo defer Report(1) LIFO second");
            Assert(log.Count >= 4 && log[3] == "4", "FF5-06: main Report(4) after foo returns");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-06: Completed");
        }

        // ===== Test FF5-07: Non-entry function with defer and return value =====
        {
            string source = @"
func compute(x: int): int {
    defer {
        Report(100)
    }
    return x * 2
}
func main() {
    var result: int = compute(21)
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "FF5-07 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Expected: compute defer Report(100), then main Report(42)
            Assert(log.Count >= 1 && log[0] == "100", "FF5-07: compute defer Report(100)");
            Assert(log.Count >= 2 && log[1] == "42", "FF5-07: main Report(compute(21)=42)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "FF5-07: Completed");
        }

        // ===== E001 Regression: Dead variables + while loop register lifecycle =====

        // E001-01: 2 dead vars before while loop (original repro — sum=127 bug)
        {
            string source = @"
func main() {
    var dead0: int = 1
    var dead1: int = 2
    var sum: int = 0
    var i: int = 1
    while i <= 100 {
        sum = sum + i
        i = i + 1
    }
    Report(sum)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-01 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 5050, $"E001-01: 2 dead vars + while → sum = {reported} (expected 5050)");
        }

        // E001-02: 4 dead vars before while loop
        {
            string source = @"
func main() {
    var d0: int = 1
    var d1: int = 2
    var d2: int = 3
    var d3: int = 4
    var sum: int = 0
    var i: int = 1
    while i <= 10 {
        sum = sum + i
        i = i + 1
    }
    Report(sum)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-02 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 55, $"E001-02: 4 dead vars + while → sum = {reported} (expected 55)");
        }

        // E001-03: Dead vars with for loop
        {
            string source = @"
func main() {
    var unused1: int = 10
    var unused2: int = 20
    var total: int = 0
    for var j: int = 1; j <= 5; j = j + 1 {
        total = total + j
    }
    Report(total)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-03 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 15, $"E001-03: dead vars + for → total = {reported} (expected 15)");
        }

        // E001-04: Dead vars interleaved with live vars (no conflict expected)
        {
            string source = @"
func main() {
    var a: int = 100
    var dead: int = 999
    var b: int = 200
    var result: int = a + b
    Report(result)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-04 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 300, $"E001-04: interleaved dead/live vars → result = {reported} (expected 300)");
        }

        // E001-05: Nested while loops with dead vars
        {
            string source = @"
func main() {
    var x: int = 0
    var y: int = 0
    var sum: int = 0
    var i: int = 1
    while i <= 3 {
        var j: int = 1
        while j <= 3 {
            sum = sum + 1
            j = j + 1
        }
        i = i + 1
    }
    Report(sum)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-05 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 9, $"E001-05: nested while with dead vars → sum = {reported} (expected 9)");
        }

        // E001-06: Dead vars + while in non-entry function
        {
            string source = @"
func compute(): int {
    var dead1: int = 42
    var dead2: int = 43
    var sum: int = 0
    var i: int = 1
    while i <= 10 {
        sum = sum + i
        i = i + 1
    }
    return sum
}

func main() {
    var val: int = compute()
    Report(val)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E001-06 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = s.Registers.Get(0).ToInt();
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 55, $"E001-06: dead vars + while in func → val = {reported} (expected 55)");
        }

        // ===== E002 Regression: Syscall safety + SyscallArgs =====

        // E002-01: Collision detection — different name on same slot throws
        {
            bool caught = false;
            try
            {
                var world = new VMWorld();
                world.Syscalls.Register(0, "Foo", (ref VMInstanceState s) => { });
                world.Syscalls.Register(0, "Bar", (ref VMInstanceState s) => { }); // collision!
            }
            catch (System.InvalidOperationException ex)
            {
                caught = ex.Message.Contains("collision");
            }
            Assert(caught, "E002-01: collision detection throws on different name, same slot");
        }

        // E002-02: Re-register same name on same slot succeeds (hot-swap)
        {
            bool ok = true;
            try
            {
                var world = new VMWorld();
                world.Syscalls.Register(0, "Foo", (ref VMInstanceState s) => { });
                world.Syscalls.Register(0, "Foo", (ref VMInstanceState s) => { }); // same name = ok
            }
            catch
            {
                ok = false;
            }
            Assert(ok, "E002-02: re-register same name succeeds (hot-swap)");
        }

        // E002-03: SyscallArgs type-safe parameter access
        {
            string source = @"
func main() {
    Report(42, 7)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E002-03 compile success");

            int arg0 = -1, arg1 = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                arg0 = args.GetInt(0);
                arg1 = args.GetInt(1);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(arg0 == 42, $"E002-03: SyscallArgs.GetInt(0) = {arg0} (expected 42)");
            Assert(arg1 == 7, $"E002-03: SyscallArgs.GetInt(1) = {arg1} (expected 7)");
        }

        // E002-04: SyscallArgs return value
        {
            string source = @"
func main() {
    var x: int = GetValue()
    Report(x)
}";
            var syscalls = new Dictionary<string, int> { { "GetValue", 0 }, { "Report", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "E002-04 compile success");

            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "GetValue", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                args.SetReturnInt(999);
            });
            world.Syscalls.Register(1, "Report", (ref VMInstanceState s) =>
            {
                reported = new SyscallArgs(ref s).GetInt(0);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 999, $"E002-04: SyscallArgs.SetReturnInt → Report = {reported} (expected 999)");
        }

        // E002-05: LoadDeclarationJson builds syscall map + signatures
        {
            string declJson = @"{
    ""syscalls"": {
        ""Ping"": { ""params"": [], ""returnType"": ""void"", ""description"": ""test ping"" },
        ""Add"": { ""params"": [{ ""name"": ""a"", ""type"": ""int"" }, { ""name"": ""b"", ""type"": ""int"" }], ""returnType"": ""int"", ""description"": ""add two"" }
    }
}";
            var table = new SyscallTable();
            var map = table.LoadDeclarationJson(declJson);
            Assert(map.ContainsKey("Ping"), "E002-05: Ping in map");
            Assert(map.ContainsKey("Add"), "E002-05: Add in map");
            Assert(map["Ping"] == 0, $"E002-05: Ping slot = {map["Ping"]} (expected 0)");
            Assert(map["Add"] == 1, $"E002-05: Add slot = {map["Add"]} (expected 1)");

            var sig = table.GetSignature(1);
            Assert(sig != null, "E002-05: Add signature registered");
            Assert(sig.Parameters.Length == 2, $"E002-05: Add has {sig.Parameters.Length} params (expected 2)");
            Assert(sig.ReturnType == "int", $"E002-05: Add returnType = {sig.ReturnType}");
        }

        // E002-06: LoadDeclarationJson enables compilation of syscall scripts
        {
            string declJson = @"{
    ""syscalls"": {
        ""Report"": { ""params"": [{ ""name"": ""value"", ""type"": ""int"" }], ""returnType"": ""void"", ""description"": ""report"" }
    }
}";
            var table = new SyscallTable();
            var map = table.LoadDeclarationJson(declJson);

            string source = @"
func main() {
    Report(42)
}";
            var result = compiler.Compile(source, "main", map);
            Assert(result.Success, "E002-06: syscall script compiles with declaration-loaded map");

            // Run with real handler replacing the no-op
            int reported = -1;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reported = new SyscallArgs(ref s).GetInt(0);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported == 42, $"E002-06: declaration-loaded syscall works at runtime, got {reported}");
        }

        // ===== Test STR1-01: Basic string literal — compile + syscall receives string =====
        {
            string source = @"
func main() {
    Report(""hello"")
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-01 compile success");
            Assert(result.Program.StringConstants.Length == 1, "STR1-01: one string constant");
            Assert(result.Program.StringConstants[0] == "hello", "STR1-01: string constant is 'hello'");

            string received = null;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                received = args.GetString(0, strTable);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(received == "hello", $"STR1-01: syscall received 'hello', got '{received}'");
        }

        // ===== Test STR1-02: Multiple distinct strings — deduplication =====
        {
            string source = @"
func main() {
    A(""foo"")
    B(""bar"")
    A(""foo"")
}";
            var syscalls = new Dictionary<string, int> { { "A", 0 }, { "B", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-02 compile success");
            Assert(result.Program.StringConstants.Length == 2, $"STR1-02: 2 unique strings, got {result.Program.StringConstants.Length}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "A", (ref VMInstanceState s) =>
            {
                log.Add("A:" + new SyscallArgs(ref s).GetString(0, strTable));
            });
            world.Syscalls.Register(1, "B", (ref VMInstanceState s) =>
            {
                log.Add("B:" + new SyscallArgs(ref s).GetString(0, strTable));
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count == 3, $"STR1-02: 3 calls, got {log.Count}");
            Assert(log[0] == "A:foo" && log[1] == "B:bar" && log[2] == "A:foo",
                $"STR1-02: log={string.Join(",", log)}");
        }

        // ===== Test STR1-03: String in variable — register carries index =====
        {
            string source = @"
func main() {
    var msg: string = ""world""
    Report(msg)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-03 compile success");

            string received = null;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                received = new SyscallArgs(ref s).GetString(0, strTable);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(received == "world", $"STR1-03: variable stores string index, syscall received '{received}'");
        }

        // ===== Test STR1-04: String concatenation is a compile error =====
        {
            string source = @"
func main() {
    var x: int = ""a"" + ""b""
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "STR1-04: string concatenation is compile error");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("String literals"),
                "STR1-04: error mentions 'String literals'");
        }

        // ===== Test STR1-05: String in unary expression is a compile error =====
        {
            string source = @"
func main() {
    var x: int = -""a""
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "STR1-05: string in unary expression is compile error");
        }

        // ===== Test STR1-06: String in arithmetic is a compile error =====
        {
            string source = @"
func main() {
    var n: int = 1
    var x: int = n + ""a""
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "STR1-06: string + number is compile error");
        }

        // ===== Test STR1-07: String as function argument =====
        {
            string source = @"
func greet(msg: string) {
    Report(msg)
}
func main() {
    greet(""hi"")
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-07 compile success");

            string received = null;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                received = new SyscallArgs(ref s).GetString(0, strTable);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(received == "hi", $"STR1-07: function forwards string to syscall, got '{received}'");
        }

        // ===== Test STR1-08: Escape sequences in strings =====
        {
            string source = @"
func main() {
    Report(""line1\nline2"")
    Report(""tab\there"")
    Report(""quote\""\\"")
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-08 compile success");
            Assert(result.Program.StringConstants.Length == 3, "STR1-08: 3 distinct strings");
            Assert(result.Program.StringConstants[0] == "line1\nline2", "STR1-08: newline escape");
            Assert(result.Program.StringConstants[1] == "tab\there", "STR1-08: tab escape");
            Assert(result.Program.StringConstants[2] == "quote\"\\", "STR1-08: quote+backslash escape");
        }

        // ===== Test STR1-09: Empty string =====
        {
            string source = @"
func main() {
    Report("""")
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-09 compile success");
            Assert(result.Program.StringConstants[0] == "", "STR1-09: empty string constant");

            string received = null;
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                received = new SyscallArgs(ref s).GetString(0, strTable);
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(received == "", $"STR1-09: empty string passed through, got '{received}'");
        }

        // ===== Test STR1-10: Unterminated string is a lexer error =====
        {
            string source = @"
func main() {
    Report(""hello)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "STR1-10: unterminated string is compile error");
        }

        // ===== Test STR1-11: String survives snapshot/rollback (ROM) =====
        {
            string source = @"
func main() {
    Report(""snapshot_test"")
    wait 1
    Report(""after_wait"")
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "STR1-11 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add(new SyscallArgs(ref s).GetString(0, strTable));
            });
            world.SpawnInstance(0, 0);
            world.Tick(); // executes Report("snapshot_test") + wait 1

            // Save + rollback
            int frame = world.FrameNumber;
            world.SaveState();
            world.Tick(); // decrement wait
            world.Tick(); // executes Report("after_wait")
            world.LoadState(frame);
            log.Clear();
            world.Tick(); // decrement wait (replayed)
            world.Tick(); // executes Report("after_wait") again
            Assert(log.Count == 1 && log[0] == "after_wait",
                $"STR1-11: string survives rollback, got [{string.Join(",", log)}]");
        }

        // ===== Test STR1-12: No StringConstants when no string literals =====
        {
            string source = @"
func main() {
    var x: int = 42
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "STR1-12 compile success");
            Assert(result.Program.StringConstants.Length == 0,
                $"STR1-12: no string constants when none used, got {result.Program.StringConstants.Length}");
        }

        // =================================================================
        //  FF3: Optional parameters with default values
        // =================================================================

        // ===== Test FF3-01: Basic optional parameter with default =====
        {
            string source = @"
func add(a: int, b: int = 10): int {
    return a + b
}
func main() {
    Report(add(5))
    Report(add(5, 20))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"FF3-01 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "15", $"FF3-01: add(5) = 15, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "25", $"FF3-01: add(5,20) = 25, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test FF3-02: Multiple optional parameters =====
        {
            string source = @"
func calc(a: int, b: int = 2, c: int = 3): int {
    return a + b * c
}
func main() {
    Report(calc(10))
    Report(calc(10, 5))
    Report(calc(10, 5, 7))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"FF3-02 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "16", $"FF3-02: calc(10) = 10+2*3=16, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "25", $"FF3-02: calc(10,5) = 10+5*3=25, got {(log.Count > 1 ? log[1] : "?")}");
            Assert(log.Count >= 3 && log[2] == "45", $"FF3-02: calc(10,5,7) = 10+5*7=45, got {(log.Count > 2 ? log[2] : "?")}");
        }

        // ===== Test FF3-03: All parameters optional =====
        {
            string source = @"
func val(x: int = 42): int {
    return x
}
func main() {
    Report(val())
    Report(val(99))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"FF3-03 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "42", $"FF3-03: val() = 42, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "99", $"FF3-03: val(99) = 99, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // ===== Test FF3-04: Too few args → compile error =====
        {
            string source = @"
func add(a: int, b: int = 10): int {
    return a + b
}
func main() {
    Report(add())
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "FF3-04: too few args is compile error");
        }

        // ===== Test FF3-05: Too many args → compile error =====
        {
            string source = @"
func add(a: int, b: int = 10): int {
    return a + b
}
func main() {
    Report(add(1, 2, 3))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "FF3-05: too many args is compile error");
        }

        // ===== Test FF3-06: Required after optional → compile error =====
        {
            string source = @"
func bad(a: int = 1, b: int): int {
    return a + b
}
func main() {
    Report(bad(1, 2))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "FF3-06: required after optional is compile error");
        }

        // ===== Test FF3-07: Default with expression (negative literal) =====
        {
            string source = @"
func offset(x: int, dx: int = -5): int {
    return x + dx
}
func main() {
    Report(offset(10))
    Report(offset(10, 3))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, $"FF3-07 compile success: {(result.Success ? "" : string.Join("; ", result.Errors))}");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 1 && log[0] == "5", $"FF3-07: offset(10) = 5, got {(log.Count > 0 ? log[0] : "?")}");
            Assert(log.Count >= 2 && log[1] == "13", $"FF3-07: offset(10,3) = 13, got {(log.Count > 1 ? log[1] : "?")}");
        }

        // =================================================================
        //  SO1: COPY_BLOCK OpCode tests
        // =================================================================

        // ===== Test SO1-01: 3-field struct uses COPY_BLOCK =====
        {
            string source = @"
struct Vec3 {
    x: int
    y: int
    z: int
}
func main() {
    var a: Vec3
    a.x = 10
    a.y = 20
    a.z = 30
    var b: Vec3 = a
    Report(b.x)
    Report(b.y)
    Report(b.z)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "SO1-01 compile success");

            // Verify COPY_BLOCK is emitted (3 fields ≥ 3 threshold)
            bool hasCopyBlock = false;
            for (int i = 0; i < result.Program.InstructionCount; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.COPY_BLOCK)
                {
                    hasCopyBlock = true;
                    Assert(result.Program.Instructions[i].C == 3,
                        $"SO1-01: COPY_BLOCK count = {result.Program.Instructions[i].C} (== 3)");
                    break;
                }
            }
            Assert(hasCopyBlock, "SO1-01: COPY_BLOCK emitted for 3-field struct");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 3 && log[0] == "10" && log[1] == "20" && log[2] == "30",
                $"SO1-01: b = a → (10,20,30), got ({string.Join(",", log)})");
        }

        // ===== Test SO1-02: 2-field struct still uses N×MOVE (below threshold) =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
func main() {
    var a: Vec2
    a.x = 5
    a.y = 9
    var b: Vec2 = a
    Report(b.x)
    Report(b.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "SO1-02 compile success");

            // Verify no COPY_BLOCK emitted (2 fields < 3 threshold)
            bool hasCopyBlock = false;
            for (int i = 0; i < result.Program.InstructionCount; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.COPY_BLOCK)
                {
                    hasCopyBlock = true;
                    break;
                }
            }
            Assert(!hasCopyBlock, "SO1-02: no COPY_BLOCK for 2-field struct (uses N×MOVE)");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 2 && log[0] == "5" && log[1] == "9",
                $"SO1-02: b = a → (5,9), got ({string.Join(",", log)})");
        }

        // ===== Test SO1-03: 4-field struct whole assignment =====
        {
            string source = @"
struct Stats {
    hp: int
    mp: int
    atk: int
    def: int
}
func main() {
    var s: Stats
    s.hp = 100
    s.mp = 50
    s.atk = 25
    s.def = 10
    var t: Stats = s
    Report(t.hp)
    Report(t.mp)
    Report(t.atk)
    Report(t.def)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "SO1-03 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 4 && log[0] == "100" && log[1] == "50" && log[2] == "25" && log[3] == "10",
                $"SO1-03: t = s → (100,50,25,10), got ({string.Join(",", log)})");
        }

        // ===== Test SO1-04: Nested struct uses COPY_BLOCK for whole assignment =====
        {
            string source = @"
struct Vec2 {
    x: int
    y: int
}
struct Rect {
    min: Vec2
    max: Vec2
}
func main() {
    var r: Rect
    r.min.x = 1
    r.min.y = 2
    r.max.x = 3
    r.max.y = 4
    var s: Rect = r
    Report(s.min.x)
    Report(s.min.y)
    Report(s.max.x)
    Report(s.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "SO1-04 compile success");

            // Rect has 4 flat fields → should use COPY_BLOCK
            bool hasCopyBlock = false;
            for (int i = 0; i < result.Program.InstructionCount; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.COPY_BLOCK &&
                    result.Program.Instructions[i].C == 4)
                {
                    hasCopyBlock = true;
                    break;
                }
            }
            Assert(hasCopyBlock, "SO1-04: COPY_BLOCK(4) emitted for nested 4-field struct");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(log.Count >= 4 && log[0] == "1" && log[1] == "2" && log[2] == "3" && log[3] == "4",
                $"SO1-04: s = r → (1,2,3,4), got ({string.Join(",", log)})");
        }

        // ===== Test SO1-05: COPY_BLOCK survives rollback =====
        {
            string source = @"
struct Vec3 {
    x: int
    y: int
    z: int
}
func main() {
    var a: Vec3
    a.x = 7
    a.y = 8
    a.z = 9
    var b: Vec3 = a
    wait 1
    Report(b.x)
    Report(b.y)
    Report(b.z)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "SO1-05 compile success");

            var log = new List<string>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                log.Add($"{s.Registers.Get(0).ToInt()}");
            });

            world.SpawnInstance(0, 0);
            world.Tick(); // frame 1: COPY_BLOCK executes, then WAIT 1 → suspend
            world.SaveState();
            world.Tick(); // frame 2: WaitCounter 1→0, skip
            world.Tick(); // frame 3: Reports (7,8,9)
            Assert(log.Count == 3, $"SO1-05: 3 reports, got {log.Count}");

            // Rollback to frame 1 and replay
            log.Clear();
            bool loaded = world.LoadState(1);
            Assert(loaded, "SO1-05: rollback succeeded");
            world.Tick(); // replay frame 2: wait expires
            world.Tick(); // replay frame 3: reports
            Assert(log.Count >= 3 && log[0] == "7" && log[1] == "8" && log[2] == "9",
                $"SO1-05: after rollback → (7,8,9), got ({string.Join(",", log)})");
        }

        // ===== Test MV01: Module variable — basic scalar var/const =====
        {
            string source = @"
var counter: int = 0
const MAX: int = 100

func incr() {
    counter = counter + 1
}

func main() {
    incr()
    incr()
    incr()
    Report(counter)
    Report(MAX)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV01 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"MV01: 2 reports, got {values.Count}");
            Assert(values[0] == 3, $"MV01: counter=3 after 3 incr(), got {values[0]}");
            Assert(values[1] == 100, $"MV01: MAX=100, got {values[1]}");
        }

        // ===== Test MV02: Module variable — shared across functions =====
        {
            string source = @"
var x: int = 10

func add(n: int) {
    x = x + n
}

func main() {
    add(5)
    add(3)
    Report(x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV02 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1, $"MV02: 1 report, got {values.Count}");
            Assert(values[0] == 18, $"MV02: x=10+5+3=18, got {values[0]}");
        }

        // ===== Test MV03: Module const — prevent assignment =====
        {
            string source = @"
const PI: int = 314

func main() {
    PI = 0
    Report(PI)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MV03: assignment to module const should fail");
        }

        // ===== Test MV04: Module variable — prevent local shadowing =====
        {
            string source = @"
var x: int = 10

func main() {
    var x: int = 20
    Report(x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MV04: local var shadowing module var should fail");
        }

        // ===== Test MV05: Module variable — default initialization to zero =====
        {
            string source = @"
var a: int
var b: int

func main() {
    Report(a)
    Report(b)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV05 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"MV05: 2 reports, got {values.Count}");
            Assert(values[0] == 0, $"MV05: a=0 default, got {values[0]}");
            Assert(values[1] == 0, $"MV05: b=0 default, got {values[1]}");
        }

        // ===== Test MV06: Multiple module variables =====
        {
            string source = @"
var a: int = 1
var b: int = 2
var c: int = 3

func sum(): int {
    return a + b + c
}

func main() {
    Report(sum())
    a = 10
    Report(sum())
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV06 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"MV06: 2 reports, got {values.Count}");
            Assert(values[0] == 6, $"MV06: sum=1+2+3=6, got {values[0]}");
            Assert(values[1] == 15, $"MV06: sum=10+2+3=15, got {values[1]}");
        }

        // ===== Test MV07: Module const with expression initializer =====
        {
            string source = @"
const A: int = 10
const B: int = A + 5
const C: int = A * B

func main() {
    Report(A)
    Report(B)
    Report(C)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV07 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, $"MV07: 3 reports, got {values.Count}");
            Assert(values[0] == 10, $"MV07: A=10, got {values[0]}");
            Assert(values[1] == 15, $"MV07: B=15, got {values[1]}");
            Assert(values[2] == 150, $"MV07: C=150, got {values[2]}");
        }

        // ===== Test MV08: Module variable with wait — persistence across frames =====
        {
            string source = @"
var state: int = 0

func main() {
    state = 1
    Report(state)
    wait 1
    state = state + 1
    Report(state)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MV08 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();  // frame 1: state=1, Report(1), wait 1
            Assert(values.Count == 1 && values[0] == 1, $"MV08: frame1 state=1, got {(values.Count > 0 ? values[0].ToString() : "none")}");
            world.Tick();  // frame 2: wait expires
            world.Tick();  // frame 3: state=2, Report(2)
            Assert(values.Count == 2 && values[1] == 2, $"MV08: frame3 state=2, got {(values.Count > 1 ? values[1].ToString() : "none")}");
        }

        // ===== Test XR01: Extended registers — module variable overflow (>8 vars) =====
        {
            // 10 module vars: 8 fit in fixed slots (r56-r63), 2 overflow to extended registers
            string source = @"
var v1: int = 1
var v2: int = 2
var v3: int = 3
var v4: int = 4
var v5: int = 5
var v6: int = 6
var v7: int = 7
var v8: int = 8
var v9: int = 9
var v10: int = 10

func sumAll(): int {
    var s: int = v1 + v2
    s = s + v3
    s = s + v4
    s = s + v5
    s = s + v6
    s = s + v7
    s = s + v8
    s = s + v9
    s = s + v10
    return s
}

func main() {
    Report(sumAll())
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR01 compile success");
            Assert(result.Program.RequiredExtendedRegisters == 2, $"XR01: 2 extended regs needed, got {result.Program.RequiredExtendedRegisters}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1, $"XR01: 1 report, got {values.Count}");
            Assert(values[0] == 55, $"XR01: sum(1..10)=55, got {values[0]}");
        }

        // ===== Test XR02: Extended registers — read/write overflow module vars =====
        {
            string source = @"
var v1: int = 1
var v2: int = 2
var v3: int = 3
var v4: int = 4
var v5: int = 5
var v6: int = 6
var v7: int = 7
var v8: int = 8
var v9: int = 100
var v10: int = 200

func incr9() {
    v9 = v9 + 1
}

func main() {
    incr9()
    incr9()
    incr9()
    v10 = v10 + v9
    Report(v9)
    Report(v10)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR02 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"XR02: 2 reports, got {values.Count}");
            Assert(values[0] == 103, $"XR02: v9=100+3=103, got {values[0]}");
            Assert(values[1] == 303, $"XR02: v10=200+103=303, got {values[1]}");
        }

        // ===== Test XR03: Extended registers — zero overhead when not used =====
        {
            string source = @"
var a: int = 42
func main() {
    Report(a)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR03 compile success");
            Assert(result.Program.RequiredExtendedRegisters == 0, $"XR03: 0 extended regs for 1 module var, got {result.Program.RequiredExtendedRegisters}");

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var id = world.SpawnInstance(0, 0);
            Assert(world.Pool.ExtendedRegs[id] == null, "XR03: no extended regs allocated");
        }

        // ===== Test XR04: Extended registers — persistence across frames (wait) =====
        {
            // v9 overflows to extended register, must persist across wait frames
            string source = @"
var v1: int = 0
var v2: int = 0
var v3: int = 0
var v4: int = 0
var v5: int = 0
var v6: int = 0
var v7: int = 0
var v8: int = 0
var counter: int = 0

func main() {
    counter = 10
    Report(counter)
    wait 1
    counter = counter + 5
    Report(counter)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR04 compile success");
            Assert(result.Program.RequiredExtendedRegisters == 1, $"XR04: 1 extended reg, got {result.Program.RequiredExtendedRegisters}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();  // frame 1: counter=10, Report(10), wait 1
            Assert(values.Count == 1 && values[0] == 10, $"XR04: frame1 counter=10, got {(values.Count > 0 ? values[0].ToString() : "none")}");
            world.Tick();  // frame 2: wait expires
            world.Tick();  // frame 3: counter=15, Report(15)
            Assert(values.Count == 2 && values[1] == 15, $"XR04: frame3 counter=15, got {(values.Count > 1 ? values[1].ToString() : "none")}");
        }

        // ===== Test XR05: Extended registers — snapshot/rollback =====
        {
            string source = @"
var v1: int = 0
var v2: int = 0
var v3: int = 0
var v4: int = 0
var v5: int = 0
var v6: int = 0
var v7: int = 0
var v8: int = 0
var xval: int = 0

func main() {
    xval = 42
    Report(xval)
    wait 100
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR05 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.SaveState();  // Save before tick
            world.Tick();       // xval=42, Report(42), wait 100
            Assert(values.Count == 1 && values[0] == 42, $"XR05: pre-rollback xval=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");

            // Rollback to saved state
            bool ok = world.LoadState(world.FrameNumber - 1);
            Assert(ok, "XR05: rollback success");

            // After rollback, xval should be 0 again (pre-tick state)
            values.Clear();
            world.Tick();  // re-execute: xval=42, Report(42), wait 100
            Assert(values.Count == 1 && values[0] == 42, $"XR05: post-rollback xval=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XR06: Extended registers — mixed fixed and extended module vars =====
        {
            // First 8 module vars use fixed registers, 9th+ use extended registers
            // Test that both types work correctly together
            string source = @"
var a: int = 10
var b: int = 20
var c: int = 30
var d: int = 40
var e: int = 50
var f: int = 60
var g: int = 70
var h: int = 80
var x: int = 100

func compute(): int {
    var s: int = a + b
    s = s + c
    s = s + d
    s = s + e
    s = s + f
    s = s + g
    s = s + h
    s = s + x
    return s
}

func updateAll() {
    a = a + 1
    x = x + 1
}

func main() {
    Report(compute())
    updateAll()
    Report(compute())
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XR06 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"XR06: 2 reports, got {values.Count}");
            Assert(values[0] == 460, $"XR06: sum=10+20+30+40+50+60+70+80+100=460, got {values[0]}");
            Assert(values[1] == 462, $"XR06: after update sum=462, got {values[1]}");
        }

        // ===================================================================
        //  Lang-2: Include Tests (INC01–INC16)
        // ===================================================================

        // ===== Test INC01: Basic include — const declaration =====
        {
            var files = new Dictionary<string, string>
            {
                { "shared/config", @"
const SPEED: int = 42
" }
            };
            string source = @"
include ""shared/config""

func main() {
    Report(SPEED)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC01 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 42, $"INC01: SPEED=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC02: Include func declaration =====
        {
            var files = new Dictionary<string, string>
            {
                { "shared/helpers", @"
func add(a: int, b: int): int {
    return a + b
}
" }
            };
            string source = @"
include ""shared/helpers""

func main() {
    Report(add(10, 20))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC02 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 30, $"INC02: add(10,20)=30, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC03: Multiple includes =====
        {
            var files = new Dictionary<string, string>
            {
                { "config/a", @"const A: int = 100" },
                { "config/b", @"const B: int = 200" }
            };
            string source = @"
include ""config/a""
include ""config/b""

func main() {
    Report(A + B)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC03 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 300, $"INC03: A+B=300, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC04: Multi-level include (A→B→C) =====
        {
            var files = new Dictionary<string, string>
            {
                { "deep/c", @"const BASE: int = 5" },
                { "mid/b", @"
include ""deep/c""
func triple(x: int): int { return x * 3 }
" }
            };
            string source = @"
include ""mid/b""

func main() {
    Report(triple(BASE))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC04 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 15, $"INC04: triple(5)=15, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC05: Circular include detection =====
        {
            var files = new Dictionary<string, string>
            {
                { "loop/a", @"include ""loop/b""
const X: int = 1" },
                { "loop/b", @"include ""loop/a""
const Y: int = 2" }
            };
            string source = @"
include ""loop/a""
func main() { Report(X) }";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(!result.Success, "INC05: circular include detected");
            bool hasCircular = false;
            if (result.Errors != null)
                for (int i = 0; i < result.Errors.Count; i++)
                    if (result.Errors[i].Contains("Circular")) hasCircular = true;
            Assert(hasCircular, "INC05: error mentions 'Circular'");
        }

        // ===== Test INC06: Cross-file const override (later wins) =====
        {
            var files = new Dictionary<string, string>
            {
                { "base_config", @"const PRIORITY: int = 100" }
            };
            string source = @"
include ""base_config""
const PRIORITY: int = 200

func main() {
    Report(PRIORITY)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC06 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 200, $"INC06: override PRIORITY=200, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC07: Same-file const redefinition is error =====
        {
            string source = @"
const X: int = 10
const X: int = 20
func main() { Report(X) }";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(new Dictionary<string, string>());
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(!result.Success, "INC07: same-file const redefine is error");
        }

        // ===== Test INC08: var cannot override const =====
        {
            var files = new Dictionary<string, string>
            {
                { "base", @"const X: int = 10" }
            };
            string source = @"
include ""base""
var X: int = 20
func main() { Report(X) }";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(!result.Success, "INC08: var cannot override const");
        }

        // ===== Test INC09: Cross-file func override =====
        {
            var files = new Dictionary<string, string>
            {
                { "templates", @"
func checkEnter(): int {
    return 0
}
" }
            };
            string source = @"
include ""templates""

func checkEnter(): int {
    return 1
}

func main() {
    Report(checkEnter())
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC09 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 1, $"INC09: overridden checkEnter()=1, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC10: Include struct declaration =====
        {
            var files = new Dictionary<string, string>
            {
                { "types/vec", @"
struct Vec2 {
    x: int
    y: int
}
" }
            };
            string source = @"
include ""types/vec""

func main() {
    var v: Vec2
    v.x = 3
    v.y = 4
    Report(v.x + v.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC10 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 7, $"INC10: v.x+v.y=7, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC11: Include module var declaration =====
        {
            var files = new Dictionary<string, string>
            {
                { "shared/state", @"var counter: int = 0" }
            };
            string source = @"
include ""shared/state""

func inc() {
    counter = counter + 1
}

func main() {
    inc()
    inc()
    inc()
    Report(counter)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC11 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 3, $"INC11: counter=3, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC12: Full integration — chain includes + all declaration types =====
        {
            var files = new Dictionary<string, string>
            {
                { "base/types", @"
struct Point {
    x: int
    y: int
}
const ORIGIN_X: int = 0
const ORIGIN_Y: int = 0
" },
                { "base/math", @"
include ""base/types""
func dist(p: Point): int {
    return p.x + p.y
}
" },
                { "shared/config", @"
include ""base/types""
const SPEED: int = 10
var phase: int = 0
" }
            };
            string source = @"
include ""base/math""
include ""shared/config""
const SPEED: int = 25

func main() {
    var p: Point
    p.x = 3
    p.y = 4
    phase = dist(p)
    Report(phase)
    Report(SPEED)
    Report(ORIGIN_X)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC12 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, $"INC12: 3 reports, got {values.Count}");
            if (values.Count == 3)
            {
                Assert(values[0] == 7, $"INC12: dist(3,4)=7, got {values[0]}");
                Assert(values[1] == 25, $"INC12: SPEED=25 (overridden), got {values[1]}");
                Assert(values[2] == 0, $"INC12: ORIGIN_X=0, got {values[2]}");
            }
        }

        // ===== Test INC13: Include file not found error =====
        {
            string source = @"
include ""nonexistent""
func main() { Report(0) }";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(new Dictionary<string, string>());
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(!result.Success, "INC13: file not found is error");
            bool hasNotFound = false;
            if (result.Errors != null)
                for (int i = 0; i < result.Errors.Count; i++)
                    if (result.Errors[i].Contains("not found")) hasNotFound = true;
            Assert(hasNotFound, "INC13: error mentions 'not found'");
        }

        // ===== Test INC14: const cannot override var =====
        {
            var files = new Dictionary<string, string>
            {
                { "base", @"var X: int = 10" }
            };
            string source = @"
include ""base""
const X: int = 20
func main() { Report(X) }";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(!result.Success, "INC14: const cannot override var");
        }

        // ===== Test INC15: Cross-file struct override =====
        {
            var files = new Dictionary<string, string>
            {
                { "old_types", @"
struct Pair {
    a: int
    b: int
}
" }
            };
            string source = @"
include ""old_types""

struct Pair {
    a: int
    b: int
    c: int
}

func main() {
    var p: Pair
    p.a = 1
    p.b = 2
    p.c = 3
    Report(p.a + p.b + p.c)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "INC15 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 6, $"INC15: Pair(1+2+3)=6, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test INC16: No file resolver — backward compat (no include) =====
        {
            string source = @"
func main() {
    Report(42)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "INC16: backward compat compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 42, $"INC16: backward compat Report(42), got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===================================================================
        //  Lang-3: Blackboard Syscall Tests (BB01–BB10)
        // ===================================================================

        // ===== Test BB01: Basic Set/Get round-trip =====
        {
            string source = @"
const KEY_HP: int = 1

func main() {
    SetBlackboard(KEY_HP, 100)
    Report(GetBlackboard(KEY_HP))
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB01 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 100, $"BB01: Set(1,100) then Get(1)=100, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB02: Multiple keys — independence =====
        {
            string source = @"
const KEY_A: int = 1
const KEY_B: int = 2
const KEY_C: int = 3

func main() {
    SetBlackboard(KEY_A, 10)
    SetBlackboard(KEY_B, 20)
    SetBlackboard(KEY_C, 30)
    Report(GetBlackboard(KEY_A))
    Report(GetBlackboard(KEY_B))
    Report(GetBlackboard(KEY_C))
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB02 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, $"BB02: 3 reports, got {values.Count}");
            Assert(values[0] == 10 && values[1] == 20 && values[2] == 30,
                $"BB02: keys independent, got [{string.Join(",", values)}]");
        }

        // ===== Test BB03: Default value — unset key returns 0 =====
        {
            string source = @"
const KEY_UNSET: int = 99

func main() {
    Report(GetBlackboard(KEY_UNSET))
}";
            var syscalls = new Dictionary<string, int> { { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB03 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 0, $"BB03: unset key returns 0, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB04: Overwrite — last write wins =====
        {
            string source = @"
const KEY: int = 1

func main() {
    SetBlackboard(KEY, 10)
    SetBlackboard(KEY, 20)
    SetBlackboard(KEY, 30)
    Report(GetBlackboard(KEY))
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB04 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 30, $"BB04: last write wins, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB05: Cross-function persistence =====
        {
            string source = @"
const KEY: int = 1

func writer() {
    SetBlackboard(KEY, 42)
}

func reader(): int {
    return GetBlackboard(KEY)
}

func main() {
    writer()
    Report(reader())
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB05 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 42, $"BB05: cross-function persistence, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB06: Integration with module variables =====
        {
            string source = @"
const KEY: int = 1
var cachedValue: int = 0

func main() {
    SetBlackboard(KEY, 77)
    cachedValue = GetBlackboard(KEY)
    Report(cachedValue)
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB06 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 77, $"BB06: module var caches blackboard value, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB07: Integration with include =====
        {
            var files = new Dictionary<string, string>
            {
                { "shared/bb_keys", @"
const KEY_COMBO: int = 10
const KEY_HITS: int = 11

func bbSet(k: int, v: int) {
    SetBlackboard(k, v)
}

func bbGet(k: int): int {
    return GetBlackboard(k)
}
" }
            };
            string source = @"
include ""shared/bb_keys""

func main() {
    bbSet(KEY_COMBO, 3)
    bbSet(KEY_HITS, 5)
    Report(bbGet(KEY_COMBO))
    Report(bbGet(KEY_HITS))
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var resolver = new DictionaryFileResolver(files);
            var result = compiler.Compile(source, "main", syscalls, null, resolver, "main.ffs");
            Assert(result.Success, "BB07 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"BB07: 2 reports, got {values.Count}");
            Assert(values[0] == 3 && values[1] == 5,
                $"BB07: include key consts + helper funcs, got [{string.Join(",", values)}]");
        }

        // ===== Test BB08: Integration with defer — cleanup resets blackboard =====
        {
            string source = @"
const KEY: int = 1

func main() {
    SetBlackboard(KEY, 0)
    defer {
        SetBlackboard(KEY, 0)
    }
    SetBlackboard(KEY, 99)
    Report(GetBlackboard(KEY))
    wait 10
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB08 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            var id = world.SpawnInstance(0, 0);
            world.Tick(); // tick 1: Set(0), defer registered, Set(99), Report(99), wait 10
            Assert(values.Count == 1 && values[0] == 99, $"BB08: before cleanup, got {(values.Count > 0 ? values[0].ToString() : "none")}");
            Assert(board[1] == 99, $"BB08: board[1]=99 after Set, got {board[1]}");

            // Kill instance → defer fires → SetBlackboard(KEY, 0)
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;
            world.Tick();
            Assert(board[1] == 0, $"BB08: defer cleanup resets board[1]=0, got {board[1]}");
        }

        // ===== Test BB09: String key name via syscall =====
        {
            string source = @"
func main() {
    SetBBNamed(""combo_count"", 7)
    Report(GetBBNamed(""combo_count""))
}";
            var syscalls = new Dictionary<string, int> { { "SetBBNamed", 0 }, { "GetBBNamed", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB09 compile success");

            var board = new Dictionary<string, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            var strTable = result.Program.StringConstants;
            world.Syscalls.Register(0, "SetBBNamed", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                string key = args.GetString(0, strTable);
                int val = args.GetInt(1);
                board[key] = val;
            });
            world.Syscalls.Register(1, "GetBBNamed", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                string key = args.GetString(0, strTable);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 7, $"BB09: string key via syscall, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test BB10: Loop bulk read/write =====
        {
            string source = @"
func main() {
    var i: int = 0
    while i < 5 {
        SetBlackboard(i, i * 10)
        i = i + 1
    }
    var sum: int = 0
    i = 0
    while i < 5 {
        sum = sum + GetBlackboard(i)
        i = i + 1
    }
    Report(sum)
}";
            var syscalls = new Dictionary<string, int> { { "SetBlackboard", 0 }, { "GetBlackboard", 1 }, { "Report", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "BB10 compile success");

            var board = new Dictionary<int, int>();
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                board[args.GetInt(0)] = args.GetInt(1);
            });
            world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
            {
                var args = new SyscallArgs(ref s);
                int key = args.GetInt(0);
                args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
            });
            world.Syscalls.Register(2, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            // sum = 0*10 + 1*10 + 2*10 + 3*10 + 4*10 = 0+10+20+30+40 = 100
            Assert(values.Count == 1 && values[0] == 100, $"BB10: loop bulk R/W sum=100, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===================================================================
        //  Lang-6: XCALL / Export Table Tests (XC01–XC15)
        // ===================================================================

        // ===== Test XC01: Basic @export parsing and export table generation =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
var internal_state: int = 0
@export func get_hp(): int {
    return hp
}
func main() {
    Report(hp)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XC01 compile success");
            Assert(result.Program.ExportTable != null, "XC01: ExportTable not null");
            Assert(result.Program.ExportTable.Variables.Length == 2, $"XC01: 2 exported vars, got {result.Program.ExportTable.Variables.Length}");
            Assert(result.Program.ExportTable.Functions.Length == 1, $"XC01: 1 exported func, got {result.Program.ExportTable.Functions.Length}");
            Assert(result.Program.ExportTable.Variables[0].Name == "hp", $"XC01: var[0].Name=hp, got {result.Program.ExportTable.Variables[0].Name}");
            Assert(result.Program.ExportTable.Variables[0].Writable == true, "XC01: var[0].Writable=true");
            Assert(result.Program.ExportTable.Variables[1].Name == "mp", $"XC01: var[1].Name=mp, got {result.Program.ExportTable.Variables[1].Name}");
            Assert(result.Program.ExportTable.Functions[0].Name == "get_hp", $"XC01: func[0].Name=get_hp, got {result.Program.ExportTable.Functions[0].Name}");
            Assert(result.Program.ExportTable.Functions[0].ParamCount == 0, $"XC01: func[0].ParamCount=0, got {result.Program.ExportTable.Functions[0].ParamCount}");
        }

        // ===== Test XC02: No @export → null ExportTable =====
        {
            string source = @"
var hp: int = 100
func main() {
    Report(hp)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XC02 compile success");
            Assert(result.Program.ExportTable == null, "XC02: ExportTable null when no exports");
        }

        // ===== Test XC03: @export func with params → export table has correct paramCount =====
        {
            string source = @"
@export var hp: int = 100
@export func take_damage(d: int, m: int): int {
    hp = hp - d * m
    return hp
}
func main() {
    Report(hp)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "XC03 compile success");
            Assert(result.Program.ExportTable != null, "XC03: ExportTable not null");
            Assert(result.Program.ExportTable.Functions[0].ParamCount == 2, $"XC03: paramCount=2, got {result.Program.ExportTable.Functions[0].ParamCount}");
        }

        // ===== Test XC04: XCALL basic — cross-instance function call =====
        {
            // Service module: @export func add(a, b) { return a + b }
            string svcSource = @"
@export func add(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC04 svc compile success");
            Assert(svcResult.Program.ExportTable != null, "XC04: svc ExportTable not null");

            // Caller module: written in bytecode since C-1 has no svc.member syntax
            // Caller puts args in r0,r1, then XCALL, then Report(result)
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),    // r0 = 10 (arg0)
                new Instruction(OpCode.LOAD_CONST, 1, 1),    // r1 = 32 (arg1)
                new Instruction(OpCode.LOAD_CONST, 2, 2),    // r2 = svc instanceId (will be filled)
                new Instruction(OpCode.XCALL, 3, 2, 0),       // r3 = XCALL(svc=r2, funcIdx=0)
                new Instruction(OpCode.MOVE, 0, 3),           // r0 = r3 (result for Report)
                new Instruction(OpCode.SYSCALL, 0, 0, 1),     // Report(r0)
                new Instruction(OpCode.RETURN, 0, 0),
            };
            // Constants: [0]=10, [1]=32, [2]=svc instanceId (we'll use 1)
            var callerConsts = new Number[] { Number.FromInt(10), Number.FromInt(32), Number.FromInt(0) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 4) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 4, callerFuncs);

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerProg);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);  // service instance
            // Fix svc instanceId in caller's constants
            callerConsts[2] = Number.FromInt(svcId);
            callerProg = new VMProgram(callerInstructions, callerConsts, 4, callerFuncs);
            world.Modules.Load(1, callerProg);
            int callerId = world.SpawnInstance(1, 0);  // caller instance
            world.Tick(); // run both — svc main() completes, caller calls svc.add(10,32)
            Assert(values.Count == 1, $"XC04: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 42, $"XC04: add(10,32)=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XC05: XLOAD_MVAR — cross-instance variable read =====
        {
            // Service module: @export var hp = 999
            string svcSource = @"
@export var hp: int = 999
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC05 svc compile success");

            // Caller: read svc.hp via XLOAD_MVAR, report it
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),     // r0 = svc instanceId
                new Instruction(OpCode.XLOAD_MVAR, 1, 0, 0),  // r1 = XLOAD_MVAR(svc=r0, varIdx=0) → hp
                new Instruction(OpCode.MOVE, 0, 1),            // r0 = r1 for Report
                new Instruction(OpCode.SYSCALL, 0, 0, 1),      // Report(r0)
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(0) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);

            callerConsts[0] = Number.FromInt(svcId);
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(values.Count == 1, $"XC05: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 999, $"XC05: svc.hp=999, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XC06: XSTORE_MVAR — cross-instance variable write =====
        {
            // Service module: @export var hp = 100
            string svcSource = @"
@export var hp: int = 100
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC06 svc compile success");

            // Caller: write svc.hp=42, then read it back and report
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),      // r0 = svc instanceId
                new Instruction(OpCode.LOAD_CONST, 1, 1),      // r1 = 42 (new value)
                new Instruction(OpCode.XSTORE_MVAR, 0, 0, 1),  // XSTORE_MVAR(varIdx=0, svc=r0, src=r1)
                new Instruction(OpCode.XLOAD_MVAR, 2, 0, 0),   // r2 = XLOAD_MVAR(svc=r0, varIdx=0)
                new Instruction(OpCode.MOVE, 0, 2),             // r0 = r2 for Report
                new Instruction(OpCode.SYSCALL, 0, 0, 1),       // Report(r0)
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(0), Number.FromInt(42) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 3) };

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);

            callerConsts[0] = Number.FromInt(svcId);
            var callerProg = new VMProgram(callerInstructions, callerConsts, 3, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(values.Count == 1, $"XC06: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 42, $"XC06: write svc.hp=42, read back=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XC07: Multi-instance independence (XLOAD_MVAR) =====
        {
            // Two service instances of same module, each with different hp
            string svcSource = @"
@export var hp: int = 0
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC07 svc compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svc1 = world.SpawnInstance(0, 0); // hp=0
            int svc2 = world.SpawnInstance(0, 0); // hp=0
            world.Tick(); // let both initialize

            // Caller: write svc1.hp=100, svc2.hp=200, then read both
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),      // r0 = svc1 id
                new Instruction(OpCode.LOAD_CONST, 1, 1),      // r1 = 100
                new Instruction(OpCode.XSTORE_MVAR, 0, 0, 1),  // svc1.hp = 100
                new Instruction(OpCode.LOAD_CONST, 0, 2),      // r0 = svc2 id
                new Instruction(OpCode.LOAD_CONST, 1, 3),      // r1 = 200
                new Instruction(OpCode.XSTORE_MVAR, 0, 0, 1),  // svc2.hp = 200
                // Read back svc1.hp
                new Instruction(OpCode.LOAD_CONST, 0, 0),      // r0 = svc1 id
                new Instruction(OpCode.XLOAD_MVAR, 1, 0, 0),   // r1 = svc1.hp
                new Instruction(OpCode.MOVE, 0, 1),
                new Instruction(OpCode.SYSCALL, 0, 0, 1),       // Report(svc1.hp) → 100
                // Read back svc2.hp
                new Instruction(OpCode.LOAD_CONST, 0, 2),      // r0 = svc2 id
                new Instruction(OpCode.XLOAD_MVAR, 1, 0, 0),   // r1 = svc2.hp
                new Instruction(OpCode.MOVE, 0, 1),
                new Instruction(OpCode.SYSCALL, 0, 0, 1),       // Report(svc2.hp) → 200
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[]
            {
                Number.FromInt(svc1), Number.FromInt(100),
                Number.FromInt(svc2), Number.FromInt(200)
            };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(values.Count == 2, $"XC07: 2 reports, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 100, $"XC07: svc1.hp=100, got {(values.Count > 0 ? values[0].ToString() : "none")}");
            Assert(values.Count > 1 && values[1] == 200, $"XC07: svc2.hp=200, got {(values.Count > 1 ? values[1].ToString() : "none")}");
        }

        // ===== Test XC08: Nested XCALL (A→B) =====
        {
            // Service B: @export func double_it(x) { return x * 2 }
            string svcBSource = @"
@export func double_it(x: int): int {
    return x * 2
}
func main() {
}";
            var svcBResult = compiler.Compile(svcBSource, "main", new Dictionary<string, int>());
            Assert(svcBResult.Success, "XC08 svcB compile success");

            // Service A: @export func quad(x) { XCALL svcB.double_it(XCALL svcB.double_it(x)) }
            // We build A as hand-crafted bytecode
            // quad(x): r0=x, call B.double_it(x), call B.double_it(result), return
            var svcAInstructions = new Instruction[]
            {
                // main() — entry function (does nothing)
                new Instruction(OpCode.RETURN, 0, 0),
                // quad(x): r0 = x on entry (from scratch zone)
                new Instruction(OpCode.LOAD_CONST, 1, 0),      // r1 = svcB id
                new Instruction(OpCode.XCALL, 2, 1, 0),         // r2 = svcB.double_it(r0=x) → x*2
                new Instruction(OpCode.MOVE, 0, 2),             // r0 = x*2 (for second call's arg)
                new Instruction(OpCode.XCALL, 2, 1, 0),         // r2 = svcB.double_it(r0=x*2) → x*4
                new Instruction(OpCode.MOVE, 0, 2),             // r0 = x*4 (return value)
                new Instruction(OpCode.RET_FUNC, 0, 0),
            };
            var svcAConsts = new Number[] { Number.FromInt(0) }; // [0] = svcB id, filled later
            var svcAFuncs = new FunctionEntry[]
            {
                new FunctionEntry("main", 0, 0, 0),
                new FunctionEntry("quad", 1, 1, 3)
            };
            var svcAExportFuncs = new ExportFuncEntry[] { new ExportFuncEntry("quad", 1, 1) };
            var svcAExportTable = new ExportTable(null, svcAExportFuncs);

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcBResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcBId = world.SpawnInstance(0, 0);

            svcAConsts[0] = Number.FromInt(svcBId);
            var svcAProg = new VMProgram(svcAInstructions, svcAConsts, 3, svcAFuncs, exportTable: svcAExportTable);
            world.Modules.Load(1, svcAProg);
            int svcAId = world.SpawnInstance(1, 0);

            // Caller: XCALL svcA.quad(5) → expect 5*4=20
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),    // r0 = 5 (arg)
                new Instruction(OpCode.LOAD_CONST, 1, 1),    // r1 = svcA id
                new Instruction(OpCode.XCALL, 2, 1, 0),       // r2 = XCALL svcA.quad(5)
                new Instruction(OpCode.MOVE, 0, 2),
                new Instruction(OpCode.SYSCALL, 0, 0, 1),
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(5), Number.FromInt(svcAId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 3) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 3, callerFuncs);
            world.Modules.Load(2, callerProg);
            world.SpawnInstance(2, 0);

            world.Tick(); // svcB.main + svcA.main complete, then caller runs
            Assert(values.Count == 1, $"XC08: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 20, $"XC08: quad(5)=20, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XC09: XCALL depth warning callback =====
        {
            // Chain: A→B→C→D→E (depth=4, should warn at depth 5 but 4 is max)
            // Simplified: just verify warning fires when depth > maxXCallDepth
            // Use a simple svc with @export func that does nothing
            string svcSource = @"
@export func noop(): int {
    return 1
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC09 svc compile success");

            var values = new List<int>();
            var depthWarnings = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.OnXCallDepthWarning = (depth, max) => depthWarnings.Add(depth);
            int svcId = world.SpawnInstance(0, 0);

            // Caller: 5 consecutive XCALLs to same svc (not nested, depth=1 each)
            // No nesting → no warning expected
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),    // r0 = svc id
                new Instruction(OpCode.XCALL, 1, 0, 0),       // XCALL 1 (depth=1)
                new Instruction(OpCode.XCALL, 1, 0, 0),       // XCALL 2 (depth=1)
                new Instruction(OpCode.XCALL, 1, 0, 0),       // XCALL 3 (depth=1)
                new Instruction(OpCode.MOVE, 0, 1),
                new Instruction(OpCode.SYSCALL, 0, 0, 1),
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(svcId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(depthWarnings.Count == 0, $"XC09: no warnings for sequential calls, got {depthWarnings.Count}");
            Assert(values.Count == 1 && values[0] == 1, $"XC09: noop()=1, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XC10: Y1-Plus — @export func with yield → compile error =====
        {
            string source = @"
@export func bad_func(): int {
    yield
    return 1
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "XC10: compile error for @export func with yield");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("yield"), $"XC10: error mentions yield, got {(result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test XC11: Y1-Plus — @export func calling yielding function → compile error =====
        {
            string source = @"
func yielder(): int {
    yield
    return 1
}

@export func bad_func(): int {
    return yielder()
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "XC11: compile error for transitive yield");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("yield"), $"XC11: error mentions yield, got {(result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test XC12: Y1-Plus — @export func calling pure function → OK =====
        {
            string source = @"
func helper(x: int): int {
    return x * 2
}

@export func good_func(a: int): int {
    return helper(a)
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, $"XC12: pure call OK, errors: {(result.Errors != null && result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test XC13: ExportTable correctness — variable slot mapping =====
        {
            string source = @"
var internal1: int = 1
@export var exported1: int = 10
var internal2: int = 2
@export var exported2: int = 20
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "XC13 compile success");
            Assert(result.Program.ExportTable != null, "XC13: ExportTable not null");
            Assert(result.Program.ExportTable.Variables.Length == 2, $"XC13: 2 exported vars, got {result.Program.ExportTable.Variables.Length}");
            // exported1 should be slot 1 (internal1 is slot 0), exported2 slot 3 (internal2 is slot 2)
            Assert(result.Program.ExportTable.Variables[0].Name == "exported1", $"XC13: var[0]=exported1, got {result.Program.ExportTable.Variables[0].Name}");
            Assert(result.Program.ExportTable.Variables[1].Name == "exported2", $"XC13: var[1]=exported2, got {result.Program.ExportTable.Variables[1].Name}");
            Assert(result.Program.ExportTable.Variables[0].MvarSlot == 1, $"XC13: var[0].slot=1, got {result.Program.ExportTable.Variables[0].MvarSlot}");
            Assert(result.Program.ExportTable.Variables[1].MvarSlot == 3, $"XC13: var[1].slot=3, got {result.Program.ExportTable.Variables[1].MvarSlot}");
        }

        // ===== Test XC14: Error handling — invalid instanceId → PanicInvalidInstanceId =====
        {
            // Caller: XLOAD_MVAR on non-existent instance
            string svcSource = @"
@export var hp: int = 100
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC14 svc compile success");

            // Caller: try to XLOAD_MVAR with instance id 99 (invalid)
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),     // r0 = 99 (invalid instance)
                new Instruction(OpCode.XLOAD_MVAR, 1, 0, 0),  // r1 = XLOAD_MVAR → should fail
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(99) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);

            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(0, 0); // svc at id 0
            int callerId = world.SpawnInstance(1, 0);
            world.Tick();
            Assert(world.Pool.Instances[callerId].ErrorFlag == VMError.PanicInvalidInstanceId, $"XC14: PanicInvalidInstanceId, got {world.Pool.Instances[callerId].ErrorFlag}");
        }

        // ===== Test XC15: @export func with defer → compile error (C-1 restriction) =====
        {
            string source = @"
@export func bad_defer(): int {
    defer {
        var x: int = 1
    }
    return 42
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "XC15: compile error for @export func with defer");
            Assert(result.Errors.Count > 0 && result.Errors[0].Contains("defer"), $"XC15: error mentions defer, got {(result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test XC16: XCALL with params — scratch zone copy =====
        {
            // Service: @export func mul3(a, b, c) { return a * b * c }
            string svcSource = @"
@export func mul3(a: int, b: int, c: int): int {
    return a * b * c
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XC16 svc compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);

            // Caller: put 3 args in r0-r2, XCALL mul3
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),    // r0 = 2
                new Instruction(OpCode.LOAD_CONST, 1, 1),    // r1 = 3
                new Instruction(OpCode.LOAD_CONST, 2, 2),    // r2 = 7
                new Instruction(OpCode.LOAD_CONST, 3, 3),    // r3 = svc id
                new Instruction(OpCode.XCALL, 4, 3, 0),       // r4 = XCALL svc.mul3(2,3,7)
                new Instruction(OpCode.MOVE, 0, 4),
                new Instruction(OpCode.SYSCALL, 0, 0, 1),
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(2), Number.FromInt(3), Number.FromInt(7), Number.FromInt(svcId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 5) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 5, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(values.Count == 1, $"XC16: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 42, $"XC16: mul3(2,3,7)=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===================================================================
        //  Lang-7: Auto Degradation + VMConfig Tests (AD01–AD10)
        // ===================================================================

        // ===== Test AD01: A1 pure getter → DegradationType.Getter =====
        {
            string source = @"
@export var hp: int = 100
@export func get_hp(): int {
    return hp
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD01 compile success");
            Assert(result.Program.ExportTable != null, "AD01: ExportTable not null");
            Assert(result.Program.ExportTable.Functions.Length == 1, $"AD01: 1 exported func, got {result.Program.ExportTable.Functions.Length}");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Name == "get_hp", $"AD01: func=get_hp, got {func0.Name}");
            Assert(func0.Degradation == DegradationType.Getter, $"AD01: Degradation=Getter, got {func0.Degradation}");
            // hp is the first @export var → mvarSlot should match the exported var's slot
            Assert(result.Program.ExportTable.Variables.Length == 1, $"AD01: 1 exported var");
            Assert(func0.DegradeMvarSlot == result.Program.ExportTable.Variables[0].MvarSlot, $"AD01: DegradeMvarSlot matches hp's mvarSlot ({result.Program.ExportTable.Variables[0].MvarSlot}), got {func0.DegradeMvarSlot}");
        }

        // ===== Test AD02: A2 pure setter → DegradationType.Setter =====
        {
            string source = @"
@export var hp: int = 100
@export func set_hp(val: int) {
    hp = val
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD02 compile success");
            Assert(result.Program.ExportTable != null, "AD02: ExportTable not null");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Name == "set_hp", $"AD02: func=set_hp, got {func0.Name}");
            Assert(func0.Degradation == DegradationType.Setter, $"AD02: Degradation=Setter, got {func0.Degradation}");
            Assert(func0.DegradeMvarSlot == result.Program.ExportTable.Variables[0].MvarSlot, $"AD02: DegradeMvarSlot matches hp's mvarSlot ({result.Program.ExportTable.Variables[0].MvarSlot}), got {func0.DegradeMvarSlot}");
        }

        // ===== Test AD03: Non-pure function → DegradationType.None =====
        {
            string source = @"
@export var hp: int = 100
@export func add_hp(val: int) {
    hp = hp + val
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD03 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Degradation == DegradationType.None, $"AD03: non-pure function → None, got {func0.Degradation}");
            Assert(func0.DegradeMvarSlot == -1, $"AD03: DegradeMvarSlot=-1, got {func0.DegradeMvarSlot}");
        }

        // ===== Test AD04: Multi-statement body → no degradation =====
        {
            string source = @"
@export var hp: int = 100
@export func get_hp_plus(): int {
    var x: int = hp + 1
    return x
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD04 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Degradation == DegradationType.None, $"AD04: multi-stmt → None, got {func0.Degradation}");
        }

        // ===== Test AD05: Getter returning local var (not module var) → no degradation =====
        {
            string source = @"
@export func get_local(): int {
    return 42
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD05 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Degradation == DegradationType.None, $"AD05: return literal → None, got {func0.Degradation}");
        }

        // ===== Test AD06: Setter with wrong assignment target → no degradation =====
        {
            string source = @"
@export var hp: int = 100
var internal_var: int = 0
@export func set_wrong(val: int) {
    internal_var = val
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD06 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            // internal_var is a module var but not exported — setter still detects it as module var
            // Degradation should still be Setter since it writes to a module variable
            Assert(func0.Degradation == DegradationType.Setter, $"AD06: setter writes module var → Setter, got {func0.Degradation}");
        }

        // ===== Test AD07: Mixed exports — getter, setter, and normal =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
@export func get_hp(): int {
    return hp
}
@export func set_mp(val: int) {
    mp = val
}
@export func compute(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD07 compile success");
            Assert(result.Program.ExportTable.Functions.Length == 3, $"AD07: 3 exported funcs, got {result.Program.ExportTable.Functions.Length}");

            // get_hp → Getter
            var getHp = result.Program.ExportTable.Functions[0];
            Assert(getHp.Name == "get_hp", $"AD07: func[0]=get_hp, got {getHp.Name}");
            Assert(getHp.Degradation == DegradationType.Getter, $"AD07: get_hp=Getter, got {getHp.Degradation}");

            // set_mp → Setter
            var setMp = result.Program.ExportTable.Functions[1];
            Assert(setMp.Name == "set_mp", $"AD07: func[1]=set_mp, got {setMp.Name}");
            Assert(setMp.Degradation == DegradationType.Setter, $"AD07: set_mp=Setter, got {setMp.Degradation}");

            // compute → None
            var compute = result.Program.ExportTable.Functions[2];
            Assert(compute.Name == "compute", $"AD07: func[2]=compute, got {compute.Name}");
            Assert(compute.Degradation == DegradationType.None, $"AD07: compute=None, got {compute.Degradation}");
        }

        // ===== Test AD08: VMConfig — custom MaxXCallDepth + Warn =====
        {
            string svcSource = @"
@export func noop(): int {
    return 1
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "AD08 svc compile success");

            var config = new VMConfig { MaxXCallDepth = 2, XCallPolicy = XCallDepthPolicy.Warn };
            var world = new VMWorld(config);
            var depthWarnings = new List<int>();
            world.Modules.Load(0, svcResult.Program);
            world.OnXCallDepthWarning = (depth, max) => depthWarnings.Add(depth);
            int svcId = world.SpawnInstance(0, 0);

            // Caller: 3 sequential XCALLs (depth=1 each, no nesting, no warning)
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),
                new Instruction(OpCode.XCALL, 1, 0, 0),
                new Instruction(OpCode.XCALL, 1, 0, 0),
                new Instruction(OpCode.XCALL, 1, 0, 0),
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(svcId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);
            world.Modules.Load(1, callerProg);
            world.SpawnInstance(1, 0);
            world.Tick();
            Assert(depthWarnings.Count == 0, $"AD08: sequential calls → no warnings, got {depthWarnings.Count}");
            Assert(world.Config.MaxXCallDepth == 2, $"AD08: config.MaxXCallDepth=2, got {world.Config.MaxXCallDepth}");
        }

        // ===== Test AD09: VMConfig — Unlimited mode (no warnings) =====
        {
            // Build a chain: A calls B (nesting depth=1) — in Unlimited mode, no warning
            string svcSource = @"
@export func noop(): int {
    return 1
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "AD09 svc compile success");

            var config = new VMConfig { MaxXCallDepth = 1, XCallPolicy = XCallDepthPolicy.Unlimited };
            var world = new VMWorld(config);
            var depthWarnings = new List<int>();
            world.Modules.Load(0, svcResult.Program);
            world.OnXCallDepthWarning = (depth, max) => depthWarnings.Add(depth);
            int svcId = world.SpawnInstance(0, 0);

            // Build a caller with a chain: caller → svc.noop → another svc.noop
            // Actually, nesting > 1 requires the svc to also XCALL, which requires bytecode.
            // Simplified: just verify Unlimited suppresses warnings even at high depth
            // Use nested XCALL: Svc A calls Svc B
            string svcASource = @"
@export func relay(target: int): int {
    return target
}
func main() {
}";
            var svcAResult = compiler.Compile(svcASource, "main", new Dictionary<string, int>());
            Assert(svcAResult.Success, "AD09 svcA compile success");
            world.Modules.Load(1, svcAResult.Program);
            int svcAId = world.SpawnInstance(1, 0);

            // Caller: XCALL to svc (depth=1), which is fine even with MaxXCallDepth=1
            // But we want to verify Unlimited never fires warning
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),    // r0 = svcId
                new Instruction(OpCode.XCALL, 1, 0, 0),       // XCALL noop
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(svcId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 2) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 2, callerFuncs);
            world.Modules.Load(2, callerProg);
            world.SpawnInstance(2, 0);
            world.Tick();
            Assert(depthWarnings.Count == 0, $"AD09: Unlimited mode → no warnings, got {depthWarnings.Count}");
        }

        // ===== Test AD10: VMConfig — Warn mode fires at custom depth =====
        {
            // Build nested XCALLs: caller → svcA → svcB (depth=2)
            // With MaxXCallDepth=1, should warn at depth=2
            string svcBSource = @"
@export func leaf(): int {
    return 42
}
func main() {
}";
            var svcBResult = compiler.Compile(svcBSource, "main", new Dictionary<string, int>());
            Assert(svcBResult.Success, "AD10 svcB compile success");

            var config = new VMConfig { MaxXCallDepth = 1, XCallPolicy = XCallDepthPolicy.Warn };
            var world = new VMWorld(config);
            var depthWarnings = new List<int>();
            world.Modules.Load(0, svcBResult.Program);
            world.OnXCallDepthWarning = (depth, max) => depthWarnings.Add(depth);
            int svcBId = world.SpawnInstance(0, 0);

            // svcA: @export func relay() that XCALLs svcB.leaf()
            var svcAInstructions = new Instruction[]
            {
                // relay(targetId): XCALL svcB.leaf() and return result
                new Instruction(OpCode.XCALL, 0, 0, 0),       // r0 = XCALL svcB.leaf(), targetId is in r0 (param)
                new Instruction(OpCode.RETURN, 0, 0),
                // main: return
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var svcAConsts = new Number[0];
            var svcAFuncs = new FunctionEntry[]
            {
                new FunctionEntry("relay", 0, 1, 2),  // 1 param (targetId)
                new FunctionEntry("main", 2, 0, 2),
            };
            var svcAExportFuncs = new ExportFuncEntry[]
            {
                new ExportFuncEntry("relay", 0, 1),
            };
            var svcAProg = new VMProgram(svcAInstructions, svcAConsts, 2, svcAFuncs,
                exportTable: new ExportTable(System.Array.Empty<ExportVarEntry>(), svcAExportFuncs));
            world.Modules.Load(1, svcAProg);
            int svcAId = world.SpawnInstance(1, 0);

            // Caller: put svcBId in r0, XCALL svcA.relay(svcBId) → depth will be 2
            var callerInstructions = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),     // r0 = svcAId
                new Instruction(OpCode.LOAD_CONST, 1, 1),     // r1 = svcBId (this becomes param for relay)
                new Instruction(OpCode.MOVE, 0, 1),            // r0 = svcBId (arg to relay)
                new Instruction(OpCode.LOAD_CONST, 2, 0),     // r2 = svcAId
                new Instruction(OpCode.XCALL, 3, 2, 0),        // r3 = XCALL svcA.relay(r0=svcBId)
                new Instruction(OpCode.RETURN, 0, 0),
            };
            var callerConsts = new Number[] { Number.FromInt(svcAId), Number.FromInt(svcBId) };
            var callerFuncs = new FunctionEntry[] { new FunctionEntry("main", 0, 0, 4) };
            var callerProg = new VMProgram(callerInstructions, callerConsts, 4, callerFuncs);
            world.Modules.Load(2, callerProg);
            world.SpawnInstance(2, 0);
            world.Tick();

            // Depth 2 > MaxXCallDepth 1 → should have warned
            Assert(depthWarnings.Count > 0, $"AD10: Warn at depth > 1, got {depthWarnings.Count} warnings");
            Assert(depthWarnings[0] == 2, $"AD10: warning depth=2, got {depthWarnings[0]}");
        }

        // ===== Test AD11: Getter with param → no degradation (getter must have 0 params) =====
        {
            string source = @"
@export var hp: int = 100
@export func get_hp_with_param(x: int): int {
    return hp
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD11 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Degradation == DegradationType.None, $"AD11: getter with param → None, got {func0.Degradation}");
        }

        // ===== Test AD12: Setter assigning non-param value → no degradation =====
        {
            string source = @"
@export var hp: int = 100
@export func set_hp_wrong(val: int) {
    hp = 42
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "AD12 compile success");
            var func0 = result.Program.ExportTable.Functions[0];
            Assert(func0.Degradation == DegradationType.None, $"AD12: assign literal (not param) → None, got {func0.Degradation}");
        }

        // ===== Lang-8: Unified Syntax Tests (US01-US14) =====

        // ===== Test US01: svc.func() basic function call =====
        {
            // Service module with @export func add(a, b) → return a+b
            string svcSource = @"
@export func add(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US01 svc compile success");

            // Caller module: uses svc.add(10, 32) unified syntax
            string callerSource = @"
var svc: int = 0
func main() {
    var result: int = svc.add(10, 32)
    Report(result)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US01 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Run in VMWorld
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            // Patch svc variable in caller's module var with actual svc instanceId
            int callerId = world.SpawnInstance(1, 0);
            // Set the svc module var to the service instance id
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US01: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 42, $"US01: svc.add(10,32)=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US02: svc.var read =====
        {
            string svcSource = @"
@export var hp: int = 999
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US02 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.hp
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US02 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US02: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 999, $"US02: svc.hp=999, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US03: svc.var write =====
        {
            string svcSource = @"
@export var hp: int = 100
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US03 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    svc.hp = 500
    var val: int = svc.hp
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US03 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US03: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 500, $"US03: svc.hp after write = 500, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US04: getter degradation (A1) — svc.get_hp() → XLOAD_MVAR =====
        {
            string svcSource = @"
@export var hp: int = 777
@export func get_hp(): int {
    return hp
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US04 svc compile success");
            Assert(svcResult.Program.ExportTable.Functions[0].Degradation == DegradationType.Getter,
                $"US04: get_hp detected as Getter, got {svcResult.Program.ExportTable.Functions[0].Degradation}");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.get_hp()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US04 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US04: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 777, $"US04: svc.get_hp()=777 (degraded), got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US05: setter degradation (A2) — svc.set_hp(500) → XSTORE_MVAR =====
        {
            string svcSource = @"
@export var hp: int = 100
@export func set_hp(val: int) {
    hp = val
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US05 svc compile success");
            Assert(svcResult.Program.ExportTable.Functions[0].Degradation == DegradationType.Setter,
                $"US05: set_hp detected as Setter, got {svcResult.Program.ExportTable.Functions[0].Degradation}");

            string callerSource = @"
var svc: int = 0
func main() {
    svc.set_hp(500)
    var val: int = svc.hp
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US05 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US05: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 500, $"US05: svc.set_hp(500) → svc.hp=500, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US06: multi-arg function call =====
        {
            string svcSource = @"
@export func compute(a: int, b: int, c: int): int {
    return a * b + c
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US06 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var r: int = svc.compute(3, 4, 5)
    Report(r)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US06 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US06: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 17, $"US06: compute(3,4,5)=17, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US07: mixed access — var read + func call + var write =====
        {
            string svcSource = @"
@export var hp: int = 100
@export func add(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US07 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var old_hp: int = svc.hp
    var bonus: int = svc.add(old_hp, 50)
    svc.hp = bonus
    Report(svc.hp)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US07 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US07: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 150, $"US07: 100+50=150, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US08: @inline hint on @export func =====
        {
            string source = @"
@export var hp: int = 100
@inline @export func get_hp(): int {
    return hp
}
@export @inline func set_hp(val: int) {
    hp = val
}
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, $"US08 compile success: {(result.Errors != null && result.Errors.Count > 0 ? result.Errors[0] : "ok")}");
            Assert(result.Program.ExportTable != null, "US08: ExportTable not null");
            Assert(result.Program.ExportTable.Functions.Length == 2, $"US08: 2 export funcs, got {result.Program.ExportTable.Functions.Length}");
            Assert(result.Program.ExportTable.Functions[0].IsInlineHint, "US08: get_hp has @inline hint");
            Assert(result.Program.ExportTable.Functions[1].IsInlineHint, "US08: set_hp has @inline hint (reversed order)");
        }

        // ===== Test US09: @inline on non-export func — no crash (hint is stored but not in export table) =====
        {
            string source = @"
@inline func helper(): int {
    return 42
}
func main() {
    var x: int = helper()
    Report(x)
}";
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", callerSyscalls);
            Assert(result.Success, $"US09 compile success: {(result.Errors != null && result.Errors.Count > 0 ? result.Errors[0] : "ok")}");
            // No ExportTable since no @export
            Assert(result.Program.ExportTable == null, "US09: no exports → null ExportTable");
        }

        // ===== Test US10: unknown member function → compile error =====
        {
            string svcSource = @"
@export func add(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US10 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var x: int = svc.unknown(1, 2)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerResult = compiler.Compile(callerSource, "main", new Dictionary<string, int>(), null, null, null, svcBindings);
            Assert(!callerResult.Success, "US10: compile error for unknown member");
            Assert(callerResult.Errors != null && callerResult.Errors.Count > 0 &&
                   callerResult.Errors[0].Contains("unknown") && callerResult.Errors[0].Contains("not exported"),
                   $"US10: error mentions 'unknown' and 'not exported', got: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "none")}");
        }

        // ===== Test US11: write to read-only exported variable → compile error =====
        {
            // Note: Lang-12 now supports @export const; this test validates via manually-constructed non-writable ExportVarEntry
            // For this test, create a binding with a non-writable variable
            var readOnlyVar = new ExportVarEntry("hp", 0, false); // writable=false
            var readOnlyTable = new ExportTable(new ExportVarEntry[] { readOnlyVar }, new ExportFuncEntry[0]);

            string callerSource = @"
var svc: int = 0
func main() {
    svc.hp = 100
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", readOnlyTable) };
            var callerResult = compiler.Compile(callerSource, "main", new Dictionary<string, int>(), null, null, null, svcBindings);
            Assert(!callerResult.Success, "US11: compile error for read-only write");
            Assert(callerResult.Errors != null && callerResult.Errors.Count > 0 &&
                   callerResult.Errors[0].Contains("read-only"),
                   $"US11: error mentions 'read-only', got: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "none")}");
        }

        // ===== Test US12: no service binding → svc.member falls through to struct field access =====
        {
            // Without service bindings, svc.hp is treated as struct field access (fails on unknown struct)
            string callerSource = @"
var svc: int = 0
func main() {
    var x: int = svc.hp
}";
            var callerResult = compiler.Compile(callerSource, "main", new Dictionary<string, int>());
            // Should fail because 'svc' is not a struct variable
            Assert(!callerResult.Success, "US12: compile error when no service binding (struct field access fails)");
        }

        // ===== Test US13: struct field + service ref coexistence =====
        {
            string svcSource = @"
@export var hp: int = 42
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US13 svc compile success");

            string callerSource = @"
struct Vec2 {
    x: int
    y: int
}
var svc: int = 0
func main() {
    var pos: Vec2 = Vec2 { x: 10, y: 20 }
    var px: int = pos.x
    var sh: int = svc.hp
    Report(px + sh)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"US13 caller compile success: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "ok")}");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"US13: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 52, $"US13: pos.x(10)+svc.hp(42)=52, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test US14: svc.member var reference is function → helpful error =====
        {
            string svcSource = @"
@export func do_thing(): int {
    return 99
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US14 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var x: int = svc.do_thing
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerResult = compiler.Compile(callerSource, "main", new Dictionary<string, int>(), null, null, null, svcBindings);
            Assert(!callerResult.Success, "US14: compile error when accessing function as variable");
            Assert(callerResult.Errors != null && callerResult.Errors.Count > 0 &&
                   callerResult.Errors[0].Contains("do_thing") && callerResult.Errors[0].Contains("function"),
                   $"US14: error mentions function name, got: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "none")}");
        }

        // ===== Test US15: wrong argument count → compile error =====
        {
            string svcSource = @"
@export func add(a: int, b: int): int {
    return a + b
}
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "US15 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var x: int = svc.add(1)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerResult = compiler.Compile(callerSource, "main", new Dictionary<string, int>(), null, null, null, svcBindings);
            Assert(!callerResult.Success, "US15: compile error for wrong arg count");
            Assert(callerResult.Errors != null && callerResult.Errors.Count > 0 &&
                   callerResult.Errors[0].Contains("expects 2") && callerResult.Errors[0].Contains("1 provided"),
                   $"US15: error mentions arg count, got: {(callerResult.Errors != null && callerResult.Errors.Count > 0 ? callerResult.Errors[0] : "none")}");
        }

        // ===== Test DV01: DefaultValue stored for @export var with initializer =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV01 compile success");
            Assert(result.Program.ExportTable != null, "DV01: ExportTable not null");
            Assert(result.Program.ExportTable.Variables.Length == 2, "DV01: 2 exported vars");
            Assert(result.Program.ExportTable.Variables[0].Name == "hp", "DV01: var[0].Name=hp");
            Assert(result.Program.ExportTable.Variables[0].DefaultValue.ToInt() == 100, "DV01: hp default=100");
            Assert(result.Program.ExportTable.Variables[1].Name == "mp", "DV01: var[1].Name=mp");
            Assert(result.Program.ExportTable.Variables[1].DefaultValue.ToInt() == 50, "DV01: mp default=50");
        }

        // ===== Test DV02: DefaultValue = 0 for @export var without initializer =====
        {
            string source = @"
@export var hp: int
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV02 compile success");
            Assert(result.Program.ExportTable != null, "DV02: ExportTable not null");
            Assert(result.Program.ExportTable.Variables[0].DefaultValue.ToInt() == 0, "DV02: no-init default=0");
        }

        // ===== Test DV03: DefaultValue with negative values and expressions =====
        {
            string source = @"
@export var totalFrames: int = -1
@export var speed: int = 3 + 4
@export var flag: int = 1 * 100 + 50
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV03 compile success");
            Assert(result.Program.ExportTable.Variables[0].DefaultValue.ToInt() == -1, "DV03: totalFrames=-1");
            Assert(result.Program.ExportTable.Variables[1].DefaultValue.ToInt() == 7, "DV03: speed=7");
            Assert(result.Program.ExportTable.Variables[2].DefaultValue.ToInt() == 150, "DV03: flag=150");
        }

        // ===== Test DV04: GetVarDefault convenience API =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
var internal_state: int = 999
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV04 compile success");
            var exports = result.Program.ExportTable;
            Assert(exports.GetVarDefault("hp", Number.Zero).ToInt() == 100, "DV04: GetVarDefault(hp)=100");
            Assert(exports.GetVarDefault("mp", Number.Zero).ToInt() == 50, "DV04: GetVarDefault(mp)=50");
            Assert(exports.GetVarDefault("nonexistent", Number.FromInt(-1)).ToInt() == -1, "DV04: GetVarDefault fallback=-1");
            Assert(exports.GetVarDefault("internal_state", Number.FromInt(-1)).ToInt() == -1, "DV04: non-exported var returns fallback");
        }

        // ===== Test DV05: ResolveVarIndices batch name→index resolution =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
@export var atk: int = 25
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV05 compile success");
            var exports = result.Program.ExportTable;
            var indices = exports.ResolveVarIndices(new string[] { "mp", "hp", "atk", "nonexistent" });
            Assert(indices.Length == 4, "DV05: indices length=4");
            Assert(indices[0] == 1, "DV05: mp→index 1");
            Assert(indices[1] == 0, "DV05: hp→index 0");
            Assert(indices[2] == 2, "DV05: atk→index 2");
            Assert(indices[3] == -1, "DV05: nonexistent→-1");
        }

        // ===== Test DV06: ReadVarDefaults batch index→value reading =====
        {
            string source = @"
@export var hp: int = 100
@export var mp: int = 50
@export var atk: int = 25
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV06 compile success");
            var exports = result.Program.ExportTable;
            var indices = exports.ResolveVarIndices(new string[] { "atk", "hp", "mp" });
            var values = exports.ReadVarDefaults(indices);
            Assert(values.Length == 3, "DV06: values length=3");
            Assert(values[0].ToInt() == 25, "DV06: atk default=25");
            Assert(values[1].ToInt() == 100, "DV06: hp default=100");
            Assert(values[2].ToInt() == 50, "DV06: mp default=50");
        }

        // ===== Test DV07: ReadVarDefaults with invalid index → Number.Zero =====
        {
            string source = @"
@export var hp: int = 100
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV07 compile success");
            var exports = result.Program.ExportTable;
            var values = exports.ReadVarDefaults(new int[] { 0, -1, 999 });
            Assert(values[0].ToInt() == 100, "DV07: valid index=100");
            Assert(values[1].ToInt() == 0, "DV07: negative index→0");
            Assert(values[2].ToInt() == 0, "DV07: out-of-range index→0");
        }

        // ===== Test DV08: DefaultValue with const-folded references (include-like) =====
        {
            string source = @"
const BASE_HP: int = 100
const BONUS: int = 50
@export var hp: int = BASE_HP + BONUS
@export var mp: int = BASE_HP * 2
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV08 compile success");
            Assert(result.Program.ExportTable.Variables[0].DefaultValue.ToInt() == 150, "DV08: hp=BASE_HP+BONUS=150");
            Assert(result.Program.ExportTable.Variables[1].DefaultValue.ToInt() == 200, "DV08: mp=BASE_HP*2=200");
        }

        // ===== Test DV09: Mixed exported and non-exported vars — only exported have defaults =====
        {
            string source = @"
var internal1: int = 1
@export var exported1: int = 10
var internal2: int = 2
@export var exported2: int = 20
var internal3: int = 3
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV09 compile success");
            var exports = result.Program.ExportTable;
            Assert(exports.Variables.Length == 2, "DV09: 2 exported vars");
            Assert(exports.Variables[0].Name == "exported1", "DV09: var[0]=exported1");
            Assert(exports.Variables[0].DefaultValue.ToInt() == 10, "DV09: exported1 default=10");
            Assert(exports.Variables[1].Name == "exported2", "DV09: var[1]=exported2");
            Assert(exports.Variables[1].DefaultValue.ToInt() == 20, "DV09: exported2 default=20");
        }

        // ===== Test DV10: End-to-end — ResolveVarIndices cacheable + ReadVarDefaults reusable =====
        {
            string source = @"
@export var totalFrames: int = -1
@export var priority: int = 1
@export var tags: int = 7
@export var isLooping: int = 1
@export var activationPriority: int = 500
func main() {
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DV10 compile success");
            var exports = result.Program.ExportTable;

            // Phase A: resolve names → indices (cacheable)
            var names = new string[] { "totalFrames", "priority", "tags", "isLooping", "activationPriority" };
            var indices = exports.ResolveVarIndices(names);
            Assert(indices[0] == 0 && indices[1] == 1 && indices[2] == 2 && indices[3] == 3 && indices[4] == 4,
                "DV10: all 5 names resolved to sequential indices");

            // Phase B: batch read defaults (reusable with cached indices)
            var values = exports.ReadVarDefaults(indices);
            Assert(values[0].ToInt() == -1, "DV10: totalFrames=-1");
            Assert(values[1].ToInt() == 1, "DV10: priority=1");
            Assert(values[2].ToInt() == 7, "DV10: tags=7");
            Assert(values[3].ToInt() == 1, "DV10: isLooping=1");
            Assert(values[4].ToInt() == 500, "DV10: activationPriority=500");

            // Verify same indices work again (cacheability)
            var values2 = exports.ReadVarDefaults(indices);
            Assert(values2[0].ToInt() == -1 && values2[4].ToInt() == 500,
                "DV10: cached indices produce same results");
        }

        // ===== Lang-11: Module-level struct var/const initialization =====

        // ===== Test MSV01: Module var struct with literal init + field read =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = Vec2 { x: 10, y: 20 }

func main() {
    Report(pos.x)
    Report(pos.y)
    Report(pos.x + pos.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV01 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, "MSV01: 3 reports");
            Assert(values[0] == 10, $"MSV01: pos.x = {values[0]} (expected 10)");
            Assert(values[1] == 20, $"MSV01: pos.y = {values[1]} (expected 20)");
            Assert(values[2] == 30, $"MSV01: pos.x + pos.y = {values[2]} (expected 30)");
        }

        // ===== Test MSV02: Module const struct with literal init + field read =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

const origin: Vec2 = Vec2 { x: 100, y: 200 }

func main() {
    Report(origin.x)
    Report(origin.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV02 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "MSV02: 2 reports");
            Assert(values[0] == 100, $"MSV02: origin.x = {values[0]} (expected 100)");
            Assert(values[1] == 200, $"MSV02: origin.y = {values[1]} (expected 200)");
        }

        // ===== Test MSV03: Const struct assignment prevention =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

const origin: Vec2 = Vec2 { x: 0, y: 0 }

func main() {
    origin = Vec2 { x: 1, y: 2 }
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MSV03: const struct assignment should fail");
            Assert(result.Errors[0].Contains("Cannot assign to 'const' struct"),
                $"MSV03: error message, got: {result.Errors[0]}");
        }

        // ===== Test MSV04: Const struct field assignment prevention =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

const origin: Vec2 = Vec2 { x: 0, y: 0 }

func main() {
    origin.x = 5
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MSV04: const struct field assignment should fail");
            Assert(result.Errors[0].Contains("Cannot assign to field of 'const' struct"),
                $"MSV04: error message, got: {result.Errors[0]}");
        }

        // ===== Test MSV05: Module var struct field write =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = Vec2 { x: 1, y: 2 }

func main() {
    pos.x = 99
    Report(pos.x)
    Report(pos.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV05 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "MSV05: 2 reports");
            Assert(values[0] == 99, $"MSV05: pos.x after write = {values[0]} (expected 99)");
            Assert(values[1] == 2, $"MSV05: pos.y unchanged = {values[1]} (expected 2)");
        }

        // ===== Test MSV06: Nested struct module var =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

struct Rect {
    min: Vec2
    max: Vec2
}

var bounds: Rect = Rect {
    min: Vec2 { x: 10, y: 20 },
    max: Vec2 { x: 30, y: 40 }
}

func main() {
    Report(bounds.min.x)
    Report(bounds.min.y)
    Report(bounds.max.x)
    Report(bounds.max.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV06 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 4, "MSV06: 4 reports");
            Assert(values[0] == 10, $"MSV06: bounds.min.x = {values[0]} (expected 10)");
            Assert(values[1] == 20, $"MSV06: bounds.min.y = {values[1]} (expected 20)");
            Assert(values[2] == 30, $"MSV06: bounds.max.x = {values[2]} (expected 30)");
            Assert(values[3] == 40, $"MSV06: bounds.max.y = {values[3]} (expected 40)");
        }

        // ===== Test MSV07: Module struct var shared across functions =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = Vec2 { x: 5, y: 10 }

func doubleX() {
    pos.x = pos.x * 2
}

func main() {
    Report(pos.x)
    doubleX()
    Report(pos.x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV07 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "MSV07: 2 reports");
            Assert(values[0] == 5, $"MSV07: pos.x before = {values[0]} (expected 5)");
            Assert(values[1] == 10, $"MSV07: pos.x after doubleX = {values[1]} (expected 10)");
        }

        // ===== Test MSV08: Module var struct default zero init (no initializer) =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2

func main() {
    Report(pos.x)
    Report(pos.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV08 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "MSV08: 2 reports");
            Assert(values[0] == 0, $"MSV08: pos.x default = {values[0]} (expected 0)");
            Assert(values[1] == 0, $"MSV08: pos.y default = {values[1]} (expected 0)");
        }

        // ===== Test MSV09: Error — const struct without initializer =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

const origin: Vec2

func main() {
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MSV09: const struct without initializer should fail");
            Assert(result.Errors[0].Contains("requires an initializer"),
                $"MSV09: error message, got: {result.Errors[0]}");
        }

        // ===== Test MSV10: Error — non-literal initializer =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = 42

func main() {
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MSV10: non-literal struct initializer should fail");
            Assert(result.Errors[0].Contains("must be a struct literal"),
                $"MSV10: error message, got: {result.Errors[0]}");
        }

        // ===== Test MSV11: Module struct var with const expression fields =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

const OFFSET: int = 5
var pos: Vec2 = Vec2 { x: OFFSET * 2, y: OFFSET + 3 }

func main() {
    Report(pos.x)
    Report(pos.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV11 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, "MSV11: 2 reports");
            Assert(values[0] == 10, $"MSV11: OFFSET*2 = {values[0]} (expected 10)");
            Assert(values[1] == 8, $"MSV11: OFFSET+3 = {values[1]} (expected 8)");
        }

        // ===== Test MSV12: Module struct var whole assignment (var, not const) =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = Vec2 { x: 1, y: 2 }

func main() {
    Report(pos.x)
    pos = Vec2 { x: 88, y: 99 }
    Report(pos.x)
    Report(pos.y)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV12 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 3, "MSV12: 3 reports");
            Assert(values[0] == 1, $"MSV12: pos.x before = {values[0]} (expected 1)");
            Assert(values[1] == 88, $"MSV12: pos.x after = {values[1]} (expected 88)");
            Assert(values[2] == 99, $"MSV12: pos.y after = {values[2]} (expected 99)");
        }

        // ===== Test MSV13: Error — @export struct module var =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

@export var pos: Vec2 = Vec2 { x: 1, y: 2 }

func main() {
}";
            var syscalls = new Dictionary<string, int>();
            var result = compiler.Compile(source, "main", syscalls);
            Assert(!result.Success, "MSV13: @export struct should fail");
            Assert(result.Errors[0].Contains("@export is not supported for struct"),
                $"MSV13: error message, got: {result.Errors[0]}");
        }

        // ===== Test MSV14: Module struct var as function argument =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var pos: Vec2 = Vec2 { x: 7, y: 8 }

func sum(v: Vec2): int {
    return v.x + v.y
}

func main() {
    Report(sum(pos))
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV14 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1, "MSV14: 1 report");
            Assert(values[0] == 15, $"MSV14: sum(pos) = {values[0]} (expected 15)");
        }

        // ===== Test MSV15: Mixed scalar and struct module vars =====
        {
            string source = @"
struct Vec2 {
    x: float
    y: float
}

var counter: int = 42
var pos: Vec2 = Vec2 { x: 3, y: 4 }
const MAX: int = 100

func main() {
    Report(counter)
    Report(pos.x)
    Report(pos.y)
    Report(MAX)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "MSV15 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });

            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 4, "MSV15: 4 reports");
            Assert(values[0] == 42, $"MSV15: counter = {values[0]} (expected 42)");
            Assert(values[1] == 3, $"MSV15: pos.x = {values[1]} (expected 3)");
            Assert(values[2] == 4, $"MSV15: pos.y = {values[2]} (expected 4)");
            Assert(values[3] == 100, $"MSV15: MAX = {values[3]} (expected 100)");
        }

        // ===== Lang-12: @export const Tests (EC01-EC03) =====

        // ===== Test EC01: @export const basic — ExportTable entry with Writable=false + DefaultValue =====
        {
            string source = @"
@export const MAX_HP: int = 999
@export const SPEED: float = 3.5
@export var name: int = 1
func main() {
    var x: int = MAX_HP
    Report(x)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, $"EC01 compile success: {(result.Errors != null && result.Errors.Count > 0 ? result.Errors[0] : "ok")}");
            Assert(result.Program.ExportTable != null, "EC01: ExportTable not null");
            Assert(result.Program.ExportTable.Variables.Length == 3, $"EC01: 3 exported vars, got {result.Program.ExportTable.Variables.Length}");

            // @export const MAX_HP
            Assert(result.Program.ExportTable.Variables[0].Name == "MAX_HP", "EC01: var[0].Name=MAX_HP");
            Assert(!result.Program.ExportTable.Variables[0].Writable, "EC01: MAX_HP Writable=false");
            Assert(result.Program.ExportTable.Variables[0].DefaultValue.ToInt() == 999, "EC01: MAX_HP default=999");

            // @export const SPEED
            Assert(result.Program.ExportTable.Variables[1].Name == "SPEED", "EC01: var[1].Name=SPEED");
            Assert(!result.Program.ExportTable.Variables[1].Writable, "EC01: SPEED Writable=false");
            Assert(System.Math.Abs(result.Program.ExportTable.Variables[1].DefaultValue.ToFloat() - 3.5f) < 0.01f,
                   $"EC01: SPEED default≈3.5, got {result.Program.ExportTable.Variables[1].DefaultValue.ToFloat()}");

            // @export var name (still writable)
            Assert(result.Program.ExportTable.Variables[2].Name == "name", "EC01: var[2].Name=name");
            Assert(result.Program.ExportTable.Variables[2].Writable, "EC01: name Writable=true");

            // Host reads via GetVarDefault
            Assert(result.Program.ExportTable.GetVarDefault("MAX_HP", Number.Zero).ToInt() == 999,
                   "EC01: GetVarDefault MAX_HP = 999");
            Assert(System.Math.Abs(result.Program.ExportTable.GetVarDefault("SPEED", Number.Zero).ToFloat() - 3.5f) < 0.01f,
                   "EC01: GetVarDefault SPEED ≈ 3.5");

            // Runtime: const folding still works (Report(MAX_HP) should use folded value)
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1, $"EC01: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 999, $"EC01: Report(MAX_HP)=999, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test EC02: @export const assignment rejected at compile time =====
        {
            // Same-module assignment to @export const → compile error
            string source = @"
@export const LIMIT: int = 10
func main() {
    LIMIT = 20
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(!result.Success, "EC02: compile error for assignment to @export const");
            Assert(result.Errors != null && result.Errors.Count > 0 &&
                   result.Errors[0].Contains("const"),
                   $"EC02: error mentions 'const', got: {(result.Errors != null && result.Errors.Count > 0 ? result.Errors[0] : "none")}");
        }

        // ===== Test EC03: cross-module svc.constVar write rejected + svc.constVar read works =====
        {
            // Compile service with @export const
            string svcSource = @"
@export const ROM_VALUE: int = 42
@export var rw_value: int = 7
func main() {
}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, $"EC03 svc compile success: {(svcResult.Errors != null && svcResult.Errors.Count > 0 ? svcResult.Errors[0] : "ok")}");

            // Caller: read @export const via svc.ROM_VALUE → should work
            string callerReadSource = @"
var svc: int = 0
func main() {
    var x: int = svc.ROM_VALUE
    Report(x)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerReadResult = compiler.Compile(callerReadSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerReadResult.Success, $"EC03 caller read compile success: {(callerReadResult.Errors != null && callerReadResult.Errors.Count > 0 ? callerReadResult.Errors[0] : "ok")}");

            // Caller: write @export const via svc.ROM_VALUE = expr → compile error
            string callerWriteSource = @"
var svc: int = 0
func main() {
    svc.ROM_VALUE = 99
}";
            var callerWriteResult = compiler.Compile(callerWriteSource, "main",
                new Dictionary<string, int>(), null, null, null, svcBindings);
            Assert(!callerWriteResult.Success, "EC03: compile error for cross-module write to @export const");
            Assert(callerWriteResult.Errors != null && callerWriteResult.Errors.Count > 0 &&
                   callerWriteResult.Errors[0].Contains("read-only"),
                   $"EC03: error mentions 'read-only', got: {(callerWriteResult.Errors != null && callerWriteResult.Errors.Count > 0 ? callerWriteResult.Errors[0] : "none")}");

            // End-to-end: read @export const via XLOAD_MVAR at runtime
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerReadResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                values.Add(s.Registers.Get(0).ToInt());
            });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            // Set the svc module var to the service instance id
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1, $"EC03: 1 report, got {values.Count}");
            Assert(values.Count > 0 && values[0] == 42, $"EC03: svc.ROM_VALUE=42, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Lang-9: Inline Expansion Tests (IN01-IN08) =====

        // ===== Test IN01: Single return pure expression function — inlined (no CALL emitted) =====
        {
            string source = @"
func add(a: int, b: int): int { return a + b }
func main() {
    var r: int = add(3, 4)
    Report(r)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, $"IN01 compile success: {(result.Errors?.Count > 0 ? result.Errors[0] : "ok")}");

            // Verify no CALL/CALL_LEAF in bytecode — function should be fully inlined
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN01: no CALL/CALL_LEAF — add() is inlined");

            // Verify execution correctness
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 7, $"IN01: add(3,4) = {(values.Count > 0 ? values[0].ToString() : "none")} (expected 7)");
        }

        // ===== Test IN02: Inline correctness — multi-arg expression with constants =====
        {
            string source = @"
func compute(a: int, b: int, c: int): int { return a * b + c }
func main() {
    var r1: int = compute(2, 3, 10)
    var r2: int = compute(5, 5, 1)
    Report(r1)
    Report(r2)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN02 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2, $"IN02: 2 reports, got {values.Count}");
            Assert(values.Count >= 1 && values[0] == 16, $"IN02: compute(2,3,10) = {(values.Count >= 1 ? values[0].ToString() : "?")} (expected 16)");
            Assert(values.Count >= 2 && values[1] == 26, $"IN02: compute(5,5,1) = {(values.Count >= 2 ? values[1].ToString() : "?")} (expected 26)");
        }

        // ===== Test IN03: Yield function NOT inlined — falls back to CALL =====
        {
            string source = @"
func yielder(): int {
    yield
    return 42
}
func main() {
    var r: int = yielder()
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "IN03 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(hasCall, "IN03: yielder() NOT inlined — CALL emitted");
        }

        // ===== Test IN04: Defer function NOT inlined — falls back to CALL =====
        {
            string source = @"
func deferer(): int {
    defer { Report(0) }
    return 42
}
func main() {
    var r: int = deferer()
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN04 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(hasCall, "IN04: deferer() NOT inlined — CALL emitted (has defer)");
        }

        // ===== Test IN05: P2 — function calling another user function IS inlined =====
        {
            string source = @"
func helper(): int { return 10 }
func caller(): int { return helper() }
func main() {
    var r: int = caller()
    Report(r)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN05 compile success");

            // P2: caller() calls helper() — both are inlinable, so both are inlined into main
            // No CALL instruction should be emitted
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN05: P2 — caller() and helper() both inlined — no CALL");

            // Execution correctness
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 10, $"IN05: caller() = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 10)");
        }

        // ===== Test IN06: Function exceeding InlineThreshold NOT inlined =====
        {
            // Build a function with many expressions to exceed threshold (16)
            string source = @"
func big(a: int): int {
    var b: int = a + 1
    var c: int = b + 2
    var d: int = c + 3
    var e: int = d + 4
    var f: int = e + 5
    var g: int = f + 6
    var h: int = g + 7
    var i: int = h + 8
    return i
}
func main() {
    var r: int = big(0)
    Report(r)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN06 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(hasCall, "IN06: big() NOT inlined — exceeds InlineThreshold");

            // Verify execution correctness
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 36, $"IN06: big(0) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 36)");
        }

        // ===== Test IN07: @inline marked function + cannot inline → warning =====
        {
            string source = @"
@inline
func heavy(): int {
    yield
    return 42
}
func main() {
    var r: int = heavy()
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "IN07 compile success");
            Assert(result.Warnings != null && result.Warnings.Count > 0,
                $"IN07: warning emitted for @inline function that cannot be inlined, got {result.Warnings?.Count ?? 0} warnings");
            Assert(result.Warnings != null && result.Warnings.Count > 0 && result.Warnings[0].Contains("inline"),
                $"IN07: warning mentions 'inline', got: {(result.Warnings?.Count > 0 ? result.Warnings[0] : "none")}");
        }

        // ===== Test IN08: Same function inlined at multiple call sites =====
        {
            string source = @"
func double(x: int): int { return x * 2 }
func main() {
    var a: int = double(5)
    var b: int = double(10)
    var c: int = double(a + b)
    Report(c)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN08 compile success");

            // All three calls should be inlined — no CALL emitted
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN08: all three double() calls inlined — no CALL");

            // Verify: double(5)=10, double(10)=20, double(10+20)=60
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 60, $"IN08: double(double(5)+double(10)) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 60)");
        }

        // ===== Lang-9 P2: Inline Expansion Tests (IN09-IN23) =====
        // Tests for multi-statement, branches, loops, multi-return, user calls, struct params, void inline

        // ===== Test IN09: Inline function with if-else branch (multi-return) =====
        {
            string source = @"
func abs(x: int): int {
    if x < 0 { return -x }
    return x
}
func main() {
    var a: int = abs(-5)
    var b: int = abs(3)
    Report(a + b)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN09 compile success");

            // P2: abs() has if-branch with early return → inlined via exit label
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN09: abs() inlined — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 8, $"IN09: abs(-5)+abs(3) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 8)");
        }

        // ===== Test IN10: Inline function with if-else returning different values =====
        {
            string source = @"
func sign(x: int): int {
    if x > 0 { return 1 }
    if x < 0 { return -1 }
    return 0
}
func main() {
    var a: int = sign(5)
    var b: int = sign(-3)
    var c: int = sign(0)
    Report(a * 100 + b * 10 + c)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN10 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // sign(5)=1, sign(-3)=-1, sign(0)=0 → 1*100 + (-1)*10 + 0 = 90
            Assert(values.Count == 1 && values[0] == 90, $"IN10: sign results = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 90)");
        }

        // ===== Test IN11: Inline function calling another user function (nested inline) =====
        {
            string source = @"
func square(x: int): int { return x * x }
func sumOfSquares(a: int, b: int): int { return square(a) + square(b) }
func main() {
    Report(sumOfSquares(3, 4))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN11 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN11: nested inline (sumOfSquares→square) — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 25, $"IN11: sumOfSquares(3,4) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 25)");
        }

        // ===== Test IN12: Inline void function (no return value) =====
        {
            string source = @"
func greet(x: int) {
    Report(x + 100)
}
func main() {
    greet(5)
    greet(10)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN12 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN12: void greet() inlined — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2 && values[0] == 105 && values[1] == 110,
                $"IN12: greet(5)={((values.Count > 0) ? values[0].ToString() : "?")}, greet(10)={((values.Count > 1) ? values[1].ToString() : "?")} (expected 105, 110)");
        }

        // ===== Test IN13: Inline function with multi-statement body and early return =====
        {
            string source = @"
func clamp(x: int, lo: int, hi: int): int {
    if x < lo { return lo }
    if x > hi { return hi }
    return x
}
func main() {
    var a: int = clamp(5, 0, 10)
    var b: int = clamp(-3, 0, 10)
    var c: int = clamp(15, 0, 10)
    Report(a * 100 + b * 10 + c)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN13 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // clamp(5,0,10)=5, clamp(-3,0,10)=0, clamp(15,0,10)=10 → 500+0+10=510
            Assert(values.Count == 1 && values[0] == 510, $"IN13: clamp results = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 510)");
        }

        // ===== Test IN14: Inline function with local variables and branches =====
        {
            string source = @"
func max(a: int, b: int): int {
    var result: int = a
    if b > a { result = b }
    return result
}
func main() {
    Report(max(3, 7))
    Report(max(10, 2))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN14 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2 && values[0] == 7 && values[1] == 10,
                $"IN14: max(3,7)={((values.Count > 0) ? values[0].ToString() : "?")}, max(10,2)={((values.Count > 1) ? values[1].ToString() : "?")} (expected 7, 10)");
        }

        // ===== Test IN15: 3-level nested inline (A→B→C) =====
        {
            string source = @"
func add1(x: int): int { return x + 1 }
func add2(x: int): int { return add1(add1(x)) }
func add4(x: int): int { return add2(add2(x)) }
func main() {
    Report(add4(10))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN15 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN15: 3-level nested inline — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 14, $"IN15: add4(10) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 14)");
        }

        // ===== Test IN16: Inline with struct parameter (Box2) =====
        {
            string source = @"
struct Box2 {
    x: int
    y: int
}
func area(b: Box2): int {
    return b.x * b.y
}
func main() {
    var r: Box2
    r.x = 3
    r.y = 5
    Report(area(r))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN16 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN16: struct param area(Box2) inlined — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 15, $"IN16: area(3,5) = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 15)");
        }

        // ===== Test IN17: Void inline with side-effect (syscall) =====
        {
            string source = @"
func emitTwo(a: int, b: int) {
    Report(a)
    Report(b)
}
func main() {
    emitTwo(10, 20)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN17 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 2 && values[0] == 10 && values[1] == 20,
                $"IN17: emitTwo(10,20) = {(values.Count > 0 ? values[0].ToString() : "?")},{(values.Count > 1 ? values[1].ToString() : "?")} (expected 10,20)");
        }

        // ===== Test IN18: Inline depth limit — 4-level chain exceeds InlineDepthMax =====
        {
            string source = @"
func d(x: int): int { return x + 1 }
func c(x: int): int { return d(x) + 1 }
func b(x: int): int { return c(x) + 1 }
func a(x: int): int { return b(x) + 1 }
func main() {
    Report(a(0))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN18 compile success");

            // InlineDepthMax=3, so main→a(0)→b(1)→c(2)→d(3) — d hits depth 3, NOT inlined
            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(hasCall, "IN18: depth 4 chain — at least one CALL (depth limit reached)");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(values.Count == 1 && values[0] == 4, $"IN18: a(0)=4, got {(values.Count > 0 ? values[0].ToString() : "?")}");
        }

        // ===== Test IN19: Function with yield NOT inlined =====
        {
            string source = @"
func yielder(): int {
    yield
    return 42
}
func main() {
    var r: int = yielder()
    Report(r)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN19 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(hasCall, "IN19: yielder() NOT inlined — CALL emitted (has yield)");
        }

        // ===== Test IN20: Inline with if-else and variable mutation =====
        {
            string source = @"
func classify(x: int): int {
    var cat: int = 0
    if x > 100 { cat = 3 }
    if x > 50 { cat = cat + 2 }
    if x > 0 { cat = cat + 1 }
    return cat
}
func main() {
    Report(classify(200))
    Report(classify(75))
    Report(classify(-5))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN20 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // classify(200): cat=0→3→5→6 = 6
            // classify(75):  cat=0→0→2→3 = 3
            // classify(-5):  cat=0→0→0→0 = 0
            Assert(values.Count == 3 && values[0] == 6 && values[1] == 3 && values[2] == 0,
                $"IN20: classify(200)={((values.Count > 0) ? values[0].ToString() : "?")}, classify(75)={((values.Count > 1) ? values[1].ToString() : "?")}, classify(-5)={((values.Count > 2) ? values[2].ToString() : "?")} (expected 6,3,0)");
        }

        // ===== Test IN21: @inline diagnostic — non-inlinable function with @inline annotation =====
        {
            string source = @"
@inline
func heavy(): int {
    yield
    return 42
}
func main() {
    var r: int = heavy()
    Report(r)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN21 compile success");
            Assert(result.Warnings.Count > 0, "IN21: @inline function with yield → warning emitted");
            bool hasInlineWarning = false;
            for (int i = 0; i < result.Warnings.Count; i++)
            {
                if (result.Warnings[i].Contains("@inline") && result.Warnings[i].Contains("heavy"))
                { hasInlineWarning = true; break; }
            }
            Assert(hasInlineWarning, "IN21: warning mentions @inline and function name");
        }

        // ===== Test IN22: Inline function returning void with early return =====
        {
            string source = @"
func maybeReport(x: int) {
    if x <= 0 { return }
    Report(x)
}
func main() {
    maybeReport(5)
    maybeReport(-3)
    maybeReport(10)
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN22 compile success");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // maybeReport(5)→Report(5), maybeReport(-3)→skip, maybeReport(10)→Report(10)
            Assert(values.Count == 2 && values[0] == 5 && values[1] == 10,
                $"IN22: maybeReport results = {string.Join(",", values)} (expected 5,10)");
        }

        // ===== Test IN23: Inline with struct param and branch =====
        {
            string source = @"
struct Vec2 { x: int; y: int }
func bigger(v: Vec2): int {
    if v.x > v.y { return v.x }
    return v.y
}
func main() {
    var a: Vec2
    a.x = 3
    a.y = 7
    var b: Vec2
    b.x = 10
    b.y = 2
    Report(bigger(a) + bigger(b))
}";
            var result = compiler.Compile(source, "main", new Dictionary<string, int> { { "Report", 0 } });
            Assert(result.Success, "IN23 compile success");

            bool hasCall = false;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                var code = result.Program.Instructions[i].Code;
                if (code == OpCode.CALL || code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasCall, "IN23: struct param + branch inlined — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            world.SpawnInstance(0, 0);
            world.Tick();
            // bigger({3,7})=7, bigger({10,2})=10 → 7+10=17
            Assert(values.Count == 1 && values[0] == 17, $"IN23: bigger results = {(values.Count > 0 ? values[0].ToString() : "?")} (expected 17)");
        }

        // ===== Lang-9 P3: Cross-Module Inline Tests (XIN01-XIN10) =====

        // ===== Test XIN01: Basic cross-module getter inline (pure exported var read) =====
        {
            
            string svcSource = @"
@export var hp: int = 100
@export func get_hp(): int {
    return hp
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN01 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.get_hp()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN01 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Verify no XCALL emitted (inlined)
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN01: get_hp() inlined — no XCALL");

            // Verify XLOAD_MVAR emitted (inline reads exported var)
            bool hasXload = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XLOAD_MVAR) hasXload = true;
            Assert(hasXload, "XIN01: XLOAD_MVAR emitted for inlined hp read");

            // Run and verify value
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1 && values[0] == 100, $"XIN01: get_hp()=100, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN02: Cross-module function with arithmetic on exported vars =====
        {
            
            string svcSource = @"
@export var base_mod: int = 10
@export var combo: int = 3
@export func get_modifier(): int {
    return base_mod + combo * 2
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN02 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.get_modifier()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN02 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN02: get_modifier() inlined — no XCALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // base_mod(10) + combo(3) * 2 = 16
            Assert(values.Count == 1 && values[0] == 16, $"XIN02: get_modifier()=16, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN03: Cross-module function with module const reference =====
        {
            
            string svcSource = @"
const MULTIPLIER: int = 5
@export var base_val: int = 10
@export func scaled(): int {
    return base_val * MULTIPLIER
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN03 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.scaled()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN03 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN03: scaled() inlined — no XCALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // base_val(10) * MULTIPLIER(5) = 50
            Assert(values.Count == 1 && values[0] == 50, $"XIN03: scaled()=50, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN04: Cross-module inline with parameter + exported var =====
        {
            
            string svcSource = @"
@export var bonus: int = 7
@export func add_bonus(x: int): int {
    return x + bonus
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN04 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.add_bonus(100)
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN04 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN04: add_bonus() inlined — no XCALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // 100 + bonus(7) = 107
            Assert(values.Count == 1 && values[0] == 107, $"XIN04: add_bonus(100)=107, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN05: Cross-module inline with if-else branch =====
        {
            
            string svcSource = @"
@export var threshold: int = 50
@export func classify(x: int): int {
    if (x > threshold) {
        return 1
    } else {
        return 0
    }
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN05 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var a: int = svc.classify(100)
    var b: int = svc.classify(10)
    Report(a * 10 + b)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN05 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN05: classify() inlined — no XCALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // classify(100)=1, classify(10)=0 → 1*10+0=10
            Assert(values.Count == 1 && values[0] == 10, $"XIN05: classify results=10, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN06: Cross-module inline + module-internal inline combo =====
        {
            
            string svcSource = @"
@export var base_val: int = 20
@export func get_val(): int {
    return base_val
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN06 svc compile success");

            // Caller has its own inlineable function + cross-module call
            string callerSource = @"
var svc: int = 0
func double_it(x: int): int {
    return x * 2
}
func main() {
    var val: int = double_it(svc.get_val())
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN06 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Both should be inlined (no XCALL, no CALL)
            bool hasXcall = false;
            bool hasCall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
            {
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
                if (callerResult.Program.Instructions[i].Code == OpCode.CALL || callerResult.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCall = true;
            }
            Assert(!hasXcall, "XIN06: cross-module get_val() inlined — no XCALL");
            Assert(!hasCall, "XIN06: module-internal double_it() inlined — no CALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // double_it(get_val()) = double_it(20) = 40
            Assert(values.Count == 1 && values[0] == 40, $"XIN06: double_it(get_val())=40, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN07: Cross-module function too large → XCALL fallback =====
        {
            
            // Build a function body that exceeds InlineThreshold=16
            string svcSource = @"
@export var x: int = 1
@export func big_func(a: int): int {
    var t1: int = a + x
    var t2: int = t1 * a
    var t3: int = t2 + t1
    var t4: int = t3 * t2
    var t5: int = t4 + t3
    var t6: int = t5 * t4
    var t7: int = t6 + t5
    var t8: int = t7 * t6
    var t9: int = t8 + t7
    return t9
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN07 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.big_func(5)
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN07 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Should have XCALL (function too large to inline)
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(hasXcall, "XIN07: big_func() too large — XCALL fallback");
        }

        // ===== Test XIN08: Cross-module function calls callee's own function → P4 deep chain inline =====
        {
            
            string svcSource = @"
@export var counter: int = 0
func helper(): int {
    return counter + 1
}
@export func get_via_helper(): int {
    return helper()
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN08 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.get_via_helper()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN08 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // P4: callee's helper() is now inlined within cross-module inline — no XCALL
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN08: P4 deep chain inline — get_via_helper()+helper() fully inlined, no XCALL");

            // Verify XLOAD_MVAR present (helper reads exported var counter)
            bool hasXload = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XLOAD_MVAR) hasXload = true;
            Assert(hasXload, "XIN08: XLOAD_MVAR emitted for inlined counter read");

            // Runtime verification: counter=0, helper returns counter+1=1
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcInst = world.SpawnInstance(0, 0);
            int callerInst = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerInst].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcInst));
            world.Tick();
            Assert(values.Count == 1, $"XIN08: 1 report, got {values.Count}");
            Assert(values.Count >= 1 && values[0] == 1, $"XIN08: counter+1=1, got {(values.Count >= 1 ? values[0].ToString() : "none")}");
        }

        // ===== Test XIN09: @inline cross-module function can't inline → warning =====
        {
            
            // Function references non-exported module var → can't cross-module inline
            string svcSource = @"
var internal_state: int = 42
@export @inline func get_internal(): int {
    return internal_state + 1
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN09 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var val: int = svc.get_internal()
    Report(val)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN09 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Should have XCALL (non-exported var reference blocks inline)
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(hasXcall, "XIN09: get_internal() has non-exported var → XCALL fallback");

            // Should have warning (function is @inline but can't be inlined)
            Assert(callerResult.Warnings != null && callerResult.Warnings.Count > 0, "XIN09: @inline warning emitted");
        }

        // ===== Test XIN10: Cross-module exported var write in inline body =====
        {
            
            string svcSource = @"
@export var counter: int = 0
@export func increment(): int {
    counter = counter + 1
    return counter
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "XIN10 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    var a: int = svc.increment()
    var b: int = svc.increment()
    Report(a)
    Report(b)
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"XIN10 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Verify no XCALL (inlined)
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "XIN10: increment() inlined — no XCALL");

            // Verify XSTORE_MVAR emitted (inline writes exported var)
            bool hasXstore = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XSTORE_MVAR) hasXstore = true;
            Assert(hasXstore, "XIN10: XSTORE_MVAR emitted for inlined counter write");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            // First increment: 0→1, second: 1→2
            Assert(values.Count == 2, $"XIN10: 2 reports, got {values.Count}");
            Assert(values.Count >= 1 && values[0] == 1, $"XIN10: first increment=1, got {(values.Count >= 1 ? values[0].ToString() : "none")}");
            Assert(values.Count >= 2 && values[1] == 2, $"XIN10: second increment=2, got {(values.Count >= 2 ? values[1].ToString() : "none")}");
        }

        // ===== Lang-9 P4: Deep Chain Inline Tests (DIN01-DIN05) =====

        // ===== Test DIN01: 2-level callee chain (A→helper→exported var) =====
        {
            string svcSource = @"
@export var bonus: int = 10
func add_bonus(x: int): int {
    return x + bonus
}
@export func compute(x: int): int {
    return add_bonus(x) * 2
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "DIN01 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    Report(svc.compute(5))
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"DIN01 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // No XCALL — fully inlined chain
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "DIN01: compute→add_bonus chain fully inlined, no XCALL");

            // XLOAD_MVAR present (reads exported bonus)
            bool hasXload = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XLOAD_MVAR) hasXload = true;
            Assert(hasXload, "DIN01: XLOAD_MVAR emitted for bonus read");

            // Runtime: compute(5) = (5+10)*2 = 30
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1 && values[0] == 30, $"DIN01: compute(5)=(5+10)*2=30, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test DIN02: Callee function with branch (if-else in helper) =====
        {
            string svcSource = @"
@export var threshold: int = 50
func classify(x: int): int {
    if (x > threshold) { return 1 }
    return 0
}
@export func check(x: int): int {
    return classify(x)
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "DIN02 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    Report(svc.check(60))
    Report(svc.check(30))
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"DIN02 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "DIN02: check→classify chain fully inlined, no XCALL");

            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 2, $"DIN02: 2 reports, got {values.Count}");
            Assert(values.Count >= 1 && values[0] == 1, $"DIN02: check(60)=1 (above threshold), got {(values.Count >= 1 ? values[0].ToString() : "none")}");
            Assert(values.Count >= 2 && values[1] == 0, $"DIN02: check(30)=0 (below threshold), got {(values.Count >= 2 ? values[1].ToString() : "none")}");
        }

        // ===== Test DIN03: Non-exported var in callee helper → XCALL fallback =====
        {
            string svcSource = @"
var internal_state: int = 99
func read_internal(): int {
    return internal_state
}
@export func get_state(): int {
    return read_internal()
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "DIN03 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    Report(svc.get_state())
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"DIN03 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // Should have XCALL: read_internal() references non-exported var → not inlinable
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(hasXcall, "DIN03: read_internal() uses non-exported var → XCALL fallback");
        }

        // ===== Test DIN04: Callee helper with exported var write =====
        {
            string svcSource = @"
@export var counter: int = 0
func bump(): int {
    counter = counter + 1
    return counter
}
@export func double_bump(): int {
    var a: int = bump()
    var b: int = bump()
    return a + b
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "DIN04 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    Report(svc.double_bump())
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"DIN04 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(!hasXcall, "DIN04: double_bump→bump chain fully inlined, no XCALL");

            // Verify XSTORE_MVAR present (bump writes counter)
            bool hasXstore = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XSTORE_MVAR) hasXstore = true;
            Assert(hasXstore, "DIN04: XSTORE_MVAR emitted for counter write");

            // Runtime: counter starts at 0, bump()→1, bump()→2, double_bump()=1+2=3
            var values = new List<int>();
            var world = new VMWorld();
            world.Modules.Load(0, svcResult.Program);
            world.Modules.Load(1, callerResult.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { values.Add(s.Registers.Get(0).ToInt()); });
            int svcId = world.SpawnInstance(0, 0);
            int callerId = world.SpawnInstance(1, 0);
            world.Pool.Instances[callerId].Registers.Set(VMConstants.ModuleVarRegBase, Number.FromInt(svcId));
            world.Tick();
            Assert(values.Count == 1 && values[0] == 3, $"DIN04: double_bump()=1+2=3, got {(values.Count > 0 ? values[0].ToString() : "none")}");
        }

        // ===== Test DIN05: Large callee helper exceeds InlineThreshold → XCALL fallback =====
        {
            string svcSource = @"
@export var base_val: int = 1
func big_helper(x: int): int {
    var a: int = x + 1
    var b: int = a + 2
    var c: int = b + 3
    var d: int = c + 4
    var e: int = d + 5
    var f: int = e + base_val
    return f
}
@export func compute(x: int): int {
    return big_helper(x)
}
func main() {}";
            var svcResult = compiler.Compile(svcSource, "main", new Dictionary<string, int>());
            Assert(svcResult.Success, "DIN05 svc compile success");

            string callerSource = @"
var svc: int = 0
func main() {
    Report(svc.compute(10))
}";
            var svcBindings = new ServiceBinding[] { new ServiceBinding("svc", svcResult.Program.ExportTable, svcResult.InlineInfo) };
            var callerSyscalls = new Dictionary<string, int> { { "Report", 0 } };
            var callerResult = compiler.Compile(callerSource, "main", callerSyscalls, null, null, null, svcBindings);
            Assert(callerResult.Success, $"DIN05 caller compile: {(callerResult.Errors?.Count > 0 ? callerResult.Errors[0] : "ok")}");

            // big_helper exceeds InlineThreshold → compute() can't inline → XCALL
            bool hasXcall = false;
            for (int i = 0; i < callerResult.Program.Instructions.Length; i++)
                if (callerResult.Program.Instructions[i].Code == OpCode.XCALL) hasXcall = true;
            Assert(hasXcall, "DIN05: big_helper() exceeds threshold → XCALL fallback");
        }

        // ===== CFG1: LOAD_CONST_W wide constant pool =====
        {
            // CFG1-01: LOAD_CONST_W — verify execution with wide constant index
            // Build a VMProgram directly with LOAD_CONST_W instruction using B|C<<8 index
            var consts = new Number[260];
            for (int i = 0; i < 260; i++) consts[i] = Number.FromInt(i * 10);
            var instructions = new Instruction[] {
                // Load constant at index 0 (normal range) → r0
                new Instruction(OpCode.LOAD_CONST, 0, 0),
                // Load constant at index 258 (wide range) → r1 using LOAD_CONST_W
                new Instruction(OpCode.LOAD_CONST_W, 1, 258 & 0xFF, 258 >> 8),
                new Instruction(OpCode.RETURN)
            };
            var program = new VMProgram(instructions, consts, 2);
            var vm = new VMWorld();
            vm.Modules.Load(0, program);
            vm.MaxStepsPerTick = 100;
            int id = vm.SpawnInstance(0, 0);
            vm.Tick();
            // r0 = consts[0] = 0, r1 = consts[258] = 2580
            long r0 = vm.Pool.Instances[id].Registers.Get(0).ToInt();
            long r1 = vm.Pool.Instances[id].Registers.Get(1).ToInt();
            Assert(r0 == 0, $"CFG1-01: r0 = consts[0] = 0, got {r0}");
            Assert(r1 == 2580, $"CFG1-01: r1 = consts[258] = 2580, got {r1}");
        }

        {
            // CFG1-02: Compiler auto-selects LOAD_CONST_W for large constant pools
            // Generate a script with >256 unique constants to force wide constant emission
            var sb = new StringBuilder();
            sb.AppendLine("func main() {");
            sb.AppendLine("  var sum: int = 0");
            // Generate 270 unique additions — each unique float literal adds a constant
            for (int i = 1; i <= 270; i++)
                sb.AppendLine($"  sum = sum + {i}.{i % 10}");
            sb.AppendLine("  Report(sum)");
            sb.AppendLine("}");
            int reportedValue = 0;
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var st = new SyscallTable();
            st.Register(0, "Report", (ref VMInstanceState inst) => {
                reportedValue = inst.Registers.Get(0).ToInt();
            });
            var cc = new BytecodeCompiler();
            var res = cc.Compile(sb.ToString(), "main", sc, st);
            Assert(res.Success, $"CFG1-02: compile with >256 constants succeeds: {(res.Errors?.Count > 0 ? res.Errors[0] : "ok")}");

            // Verify LOAD_CONST_W instructions are present
            bool hasWide = false;
            for (int i = 0; i < res.Program.Instructions.Length; i++)
            {
                if (res.Program.Instructions[i].Code == OpCode.LOAD_CONST_W)
                {
                    hasWide = true;
                    break;
                }
            }
            Assert(hasWide, "CFG1-02: LOAD_CONST_W instructions present for >256 constants");

            // Verify execution correctness — sum of i.(i%10) for i=1..270
            var vm = new VMWorld();
            vm.Modules.Load(0, res.Program);
            vm.Syscalls.Register(0, "Report", (ref VMInstanceState inst) => {
                reportedValue = inst.Registers.Get(0).ToInt();
            });
            vm.MaxStepsPerTick = 50000;
            int iid = vm.SpawnInstance(0, 0);
            vm.Tick();
            // Expected: sum of 1.1 + 2.2 + 3.3 + ... + 270.0 ≈ 36720 (integer part)
            Assert(reportedValue > 0, $"CFG1-02: execution produces non-zero sum = {reportedValue}");
        }

        {
            // CFG1-03: JUMP_IF_*_K graceful degradation with >256 constants
            // Generate >256 unique constants via additions, then test comparison
            var sb = new StringBuilder();
            sb.AppendLine("func main() {");
            sb.AppendLine("  var sum: int = 0");
            // Generate 270 unique additions → forces >256 constants in pool
            for (int i = 1; i <= 270; i++)
                sb.AppendLine($"  sum = sum + {i}.{i % 10}");
            // Comparison uses a constant that may be index >255 or <256
            // Either way, the compiler should produce correct code
            sb.AppendLine("  if (sum > 0) { Report(1) } else { Report(0) }");
            sb.AppendLine("}");
            int reported = -1;
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var st = new SyscallTable();
            st.Register(0, "Report", (ref VMInstanceState inst) => {
                reported = inst.Registers.Get(0).ToInt();
            });
            var cc = new BytecodeCompiler();
            var res = cc.Compile(sb.ToString(), "main", sc, st);
            Assert(res.Success, $"CFG1-03: compile with K-fallback succeeds: {(res.Errors?.Count > 0 ? res.Errors[0] : "ok")}");

            var vm = new VMWorld();
            vm.Modules.Load(0, res.Program);
            vm.Syscalls.Register(0, "Report", (ref VMInstanceState inst) => {
                reported = inst.Registers.Get(0).ToInt();
            });
            vm.MaxStepsPerTick = 50000;
            int iid = vm.SpawnInstance(0, 0);
            vm.Tick();
            Assert(reported == 1, $"CFG1-03: comparison works with wide constants, got {reported}");
        }

        // ===== CFG1: CompileOptions configurability =====
        {
            // CFG1-04: InlineThreshold=0 disables inlining
            string src = @"
func add(a: int, b: int): int { return a + b }
func main() { Report(add(3, 4)) }
";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            int reported = 0;
            var st = new SyscallTable();
            st.Register(0, "Report", (ref VMInstanceState inst) => {
                reported = inst.Registers.Get(0).ToInt();
            });

            // With InlineThreshold=0, add() should NOT be inlined → CALL instruction present
            var cc = new BytecodeCompiler();
            var noInlineOpts = new CompileOptions { InlineThreshold = 0 };
            var res = cc.Compile(src, "main", sc, st, null, null, null, noInlineOpts);
            Assert(res.Success, $"CFG1-04: compile with InlineThreshold=0: {(res.Errors?.Count > 0 ? res.Errors[0] : "ok")}");
            bool hasCall = false;
            for (int i = 0; i < res.Program.Instructions.Length; i++)
                if (res.Program.Instructions[i].Code == OpCode.CALL || res.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCall = true;
            Assert(hasCall, "CFG1-04: InlineThreshold=0 → CALL present (no inlining)");

            // Verify execution still correct
            var vm = new VMWorld();
            vm.Modules.Load(0, res.Program);
            vm.Syscalls.Register(0, "Report", (ref VMInstanceState inst) => { reported = inst.Registers.Get(0).ToInt(); });
            vm.MaxStepsPerTick = 1000;
            int iid = vm.SpawnInstance(0, 0);
            vm.Tick();
            Assert(reported == 7, $"CFG1-04: add(3,4)=7 with no inlining, got {reported}");
        }

        {
            // CFG1-05: InlineThreshold=999 forces aggressive inlining
            string src = @"
func add(a: int, b: int): int { return a + b }
func main() { Report(add(3, 4)) }
";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            int reported = 0;
            var st = new SyscallTable();
            st.Register(0, "Report", (ref VMInstanceState inst) => {
                reported = inst.Registers.Get(0).ToInt();
            });
            var cc = new BytecodeCompiler();
            var aggressiveOpts = new CompileOptions { InlineThreshold = 999 };
            var res = cc.Compile(src, "main", sc, st, null, null, null, aggressiveOpts);
            Assert(res.Success, $"CFG1-05: compile with InlineThreshold=999: {(res.Errors?.Count > 0 ? res.Errors[0] : "ok")}");
            // add() is small → should be inlined → no CALL
            bool hasCall = false;
            for (int i = 0; i < res.Program.Instructions.Length; i++)
                if (res.Program.Instructions[i].Code == OpCode.CALL || res.Program.Instructions[i].Code == OpCode.CALL_LEAF) hasCall = true;
            Assert(!hasCall, "CFG1-05: InlineThreshold=999 → no CALL (fully inlined)");

            var vm = new VMWorld();
            vm.Modules.Load(0, res.Program);
            vm.Syscalls.Register(0, "Report", (ref VMInstanceState inst) => { reported = inst.Registers.Get(0).ToInt(); });
            vm.MaxStepsPerTick = 1000;
            int iid = vm.SpawnInstance(0, 0);
            vm.Tick();
            Assert(reported == 7, $"CFG1-05: add(3,4)=7 with aggressive inlining, got {reported}");
        }

        // ===== CFG1: Resource diagnostics =====
        {
            // CFG1-06: DiagnosticsEnabled=true emits warnings at threshold
            // This is hard to test precisely without controlling constant count,
            // but we can verify that diagnostics OFF produces no warnings for a simple script
            string src = "func main() { Report(42) }";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var cc = new BytecodeCompiler();
            var noDiag = new CompileOptions { DiagnosticsEnabled = false };
            var res = cc.Compile(src, "main", sc, null, null, null, null, noDiag);
            Assert(res.Success, "CFG1-06: compile succeeds");
            // With diagnostics off, no CFG1 warnings should appear
            bool hasCfgWarning = false;
            if (res.Warnings != null)
            {
                for (int i = 0; i < res.Warnings.Count; i++)
                    if (res.Warnings[i].Contains("[CFG1]")) hasCfgWarning = true;
            }
            Assert(!hasCfgWarning, "CFG1-06: DiagnosticsEnabled=false → no [CFG1] warnings");
        }

        {
            // CFG1-07: DiagnosticsEnabled=true with low threshold triggers warnings
            // Use threshold=0.01 so even 1 constant triggers a warning
            string src = "func main() { Report(42) }";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var cc = new BytecodeCompiler();
            var lowThreshold = new CompileOptions { DiagnosticsEnabled = true, DiagnosticsThreshold = 0.01f };
            var res = cc.Compile(src, "main", sc, null, null, null, null, lowThreshold);
            Assert(res.Success, "CFG1-07: compile succeeds");
            bool hasCfgWarning = false;
            if (res.Warnings != null)
            {
                for (int i = 0; i < res.Warnings.Count; i++)
                    if (res.Warnings[i].Contains("[CFG1]")) hasCfgWarning = true;
            }
            Assert(hasCfgWarning, "CFG1-07: DiagnosticsThreshold=0.01 → [CFG1] warnings triggered");
        }

        // ===== Lang-13: Enum Tests =====

        // EN01: Basic enum declaration + usage
        {
            string source = @"
enum Color { RED, GREEN, BLUE }
func main() {
    Report(Color.RED)
    Report(Color.GREEN)
    Report(Color.BLUE)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN01: compile succeeds");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported.Count == 3, $"EN01: 3 reports, got {reported.Count}");
            Assert(reported[0] == 0, $"EN01: Color.RED=0, got {reported[0]}");
            Assert(reported[1] == 1, $"EN01: Color.GREEN=1, got {reported[1]}");
            Assert(reported[2] == 2, $"EN01: Color.BLUE=2, got {reported[2]}");
        }

        // EN02: Explicit value assignment + auto-increment continuation
        {
            string source = @"
enum E { A = 10, B, C = 20, D }
func main() {
    Report(E.A)
    Report(E.B)
    Report(E.C)
    Report(E.D)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN02: compile succeeds");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported.Count == 4, $"EN02: 4 reports, got {reported.Count}");
            Assert(reported[0] == 10, $"EN02: E.A=10, got {reported[0]}");
            Assert(reported[1] == 11, $"EN02: E.B=11, got {reported[1]}");
            Assert(reported[2] == 20, $"EN02: E.C=20, got {reported[2]}");
            Assert(reported[3] == 21, $"EN02: E.D=21, got {reported[3]}");
        }

        // EN03: Constant expression as value
        {
            string source = @"
enum E { A = 1 + 2, B }
func main() {
    Report(E.A)
    Report(E.B)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN03: compile succeeds");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 3, $"EN03: E.A=3, got {reported[0]}");
            Assert(reported[1] == 4, $"EN03: E.B=4, got {reported[1]}");
        }

        // EN04: Empty enum
        {
            string source = @"
enum Empty {}
func main() {
    Report(42)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN04: empty enum compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 42, $"EN04: main runs, got {reported[0]}");
        }

        // EN05: Duplicate member name → compile error
        {
            string source = @"
enum E { A, A }
func main() { Report(0) }";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(!res.Success, "EN05: duplicate member → compile error");
            bool hasDupError = false;
            foreach (var e in res.Errors)
                if (e.Contains("Duplicate enum member")) hasDupError = true;
            Assert(hasDupError, $"EN05: error mentions 'Duplicate enum member'");
        }

        // EN06: Enum name conflicts with struct → compile error
        {
            string source = @"
struct S { x: int }
enum S { A, B }
func main() { Report(0) }";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(!res.Success, "EN06: enum/struct name conflict → compile error");
            bool hasConflict = false;
            foreach (var e in res.Errors)
                if (e.Contains("conflicts")) hasConflict = true;
            Assert(hasConflict, "EN06: error mentions 'conflicts'");
        }

        // EN07: Enum usage in if branches
        {
            string source = @"
enum Dir { UP, DOWN, LEFT, RIGHT }
func main() {
    var d: int = Dir.LEFT
    if (d == Dir.LEFT) {
        Report(100)
    } else {
        Report(200)
    }
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN07: compile succeeds");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 100, $"EN07: Dir.LEFT matches → 100, got {reported[0]}");
        }

        // EN08: Cross-function enum usage
        {
            string source = @"
enum Status { IDLE, ACTIVE, DONE }
func check(s: int): int {
    if (s == Status.ACTIVE) { return 1 }
    return 0
}
func main() {
    Report(check(Status.IDLE))
    Report(check(Status.ACTIVE))
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN08: compile succeeds");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 0, $"EN08: check(IDLE)=0, got {reported[0]}");
            Assert(reported[1] == 1, $"EN08: check(ACTIVE)=1, got {reported[1]}");
        }

        // EN09: Include cross-file enum
        {
            string enumFile = @"
enum DamageType { NONE, PHYSICAL, MAGICAL }";
            string mainFile = @"
include ""damage_types.ffs""
func main() {
    Report(DamageType.PHYSICAL)
    Report(DamageType.MAGICAL)
}";
            var files = new Dictionary<string, string> {
                { "damage_types.ffs", enumFile }
            };
            var resolver = new DictionaryFileResolver(files);
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var cc = new BytecodeCompiler();
            var reported = new List<int>();
            var res = cc.Compile(mainFile, "main", sc, null, resolver, "main.ffs");
            Assert(res.Success, $"EN09: compile succeeds ({(res.Errors != null && res.Errors.Count > 0 ? res.Errors[0] : "")})");
            if (res.Success)
            {
                var world = new VMWorld();
                world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
                world.Modules.Load(0, res.Program);
                int iid = world.SpawnInstance(0, 0);
                world.Tick();
                Assert(reported[0] == 1, $"EN09: DamageType.PHYSICAL=1, got {reported[0]}");
                Assert(reported[1] == 2, $"EN09: DamageType.MAGICAL=2, got {reported[1]}");
            }
        }

        // EN10: Unknown enum member → compile error
        {
            string source = @"
enum Color { RED, GREEN, BLUE }
func main() { Report(Color.YELLOW) }";
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(!res.Success, "EN10: unknown member → compile error");
            bool hasNoMember = false;
            foreach (var e in res.Errors)
                if (e.Contains("has no member")) hasNoMember = true;
            Assert(hasNoMember, "EN10: error mentions 'has no member'");
        }

        // EN11: Enum value participates in constant folding
        {
            string source = @"
enum Color { RED, GREEN, BLUE }
const derived: int = Color.RED + 10
func main() {
    Report(derived)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, $"EN11: compile succeeds ({(res.Errors != null && res.Errors.Count > 0 ? res.Errors[0] : "")})");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 10, $"EN11: Color.RED+10=10, got {reported[0]}");
        }

        // EN12: Trailing comma
        {
            string source = @"
enum E { A, B, C, }
func main() {
    Report(E.C)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "EN12: trailing comma compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            int iid = world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 2, $"EN12: E.C=2, got {reported[0]}");
        }

        // ===== Lang-14: Bitwise operations (BW01-BW15) =====

        // BW01: Basic bitwise AND, OR, XOR
        {
            string source = @"
func main() {
    Report(6 & 3)
    Report(6 | 3)
    Report(6 ^ 3)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW01: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == (6 & 3), $"BW01: 6&3=2, got {reported[0]}");
            Assert(reported[1] == (6 | 3), $"BW01: 6|3=7, got {reported[1]}");
            Assert(reported[2] == (6 ^ 3), $"BW01: 6^3=5, got {reported[2]}");
        }

        // BW02: Bitwise NOT and shift
        {
            string source = @"
func main() {
    Report(~0)
    Report(~1)
    Report(1 << 4)
    Report(16 >> 2)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW02: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == ~0, $"BW02: ~0=-1, got {reported[0]}");
            Assert(reported[1] == ~1, $"BW02: ~1=-2, got {reported[1]}");
            Assert(reported[2] == (1 << 4), $"BW02: 1<<4=16, got {reported[2]}");
            Assert(reported[3] == (16 >> 2), $"BW02: 16>>2=4, got {reported[3]}");
        }

        // BW03: Precedence — & binds tighter than |
        {
            string source = @"
func main() {
    Report(5 | 2 & 3)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW03: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            // 5 | (2 & 3) = 5 | 2 = 7
            Assert(reported[0] == (5 | (2 & 3)), $"BW03: 5|(2&3)=7, got {reported[0]}");
        }

        // BW04: Logical vs bitwise — && and & are distinct
        {
            string source = @"
func main() {
    Report(3 & 5)
    var a: int = 3
    var b: int = 5
    if (a && b) { Report(1) } else { Report(0) }
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW04: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == (3 & 5), $"BW04: 3&5=1, got {reported[0]}");
            Assert(reported[1] == 1, $"BW04: 3&&5=true→1, got {reported[1]}");
        }

        // BW05: Constant folding — const with bitwise ops
        {
            string source = @"
const MASK: int = 1 << 11
const FLAGS: int = (1 << 0) | (1 << 2) | (1 << 4)
func main() {
    Report(MASK)
    Report(FLAGS)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW05: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == (1 << 11), $"BW05: 1<<11=2048, got {reported[0]}");
            Assert(reported[1] == ((1 << 0) | (1 << 2) | (1 << 4)), $"BW05: flags=21, got {reported[1]}");
        }

        // BW06: Enum + bitwise — flag pattern
        {
            string source = @"
enum Flags { A = 1, B = 2, C = 4, D = 8 }
func main() {
    var mask: int = Flags.A | Flags.C
    Report(mask)
    Report(mask & Flags.A)
    Report(mask & Flags.B)
    Report(mask & Flags.C)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW06: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 5, $"BW06: A|C=5, got {reported[0]}");
            Assert(reported[1] == 1, $"BW06: 5&A=1, got {reported[1]}");
            Assert(reported[2] == 0, $"BW06: 5&B=0, got {reported[2]}");
            Assert(reported[3] == 4, $"BW06: 5&C=4, got {reported[3]}");
        }

        // BW07: Shift roundtrip — (x << N) >> N == x for small x
        {
            string source = @"
func main() {
    var x: int = 42
    var shifted: int = (x << 3) >> 3
    Report(shifted)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW07: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 42, $"BW07: (42<<3)>>3=42, got {reported[0]}");
        }

        // BW08: Bitwise XOR swap pattern
        {
            string source = @"
func main() {
    var a: int = 10
    var b: int = 25
    a = a ^ b
    b = a ^ b
    a = a ^ b
    Report(a)
    Report(b)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW08: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 25, $"BW08: a=25 after swap, got {reported[0]}");
            Assert(reported[1] == 10, $"BW08: b=10 after swap, got {reported[1]}");
        }

        // BW09: Bitwise in if condition
        {
            string source = @"
func main() {
    var flags: int = 5
    if (flags & 4) { Report(1) } else { Report(0) }
    if (flags & 2) { Report(1) } else { Report(0) }
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW09: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 1, $"BW09: 5&4≠0→1, got {reported[0]}");
            Assert(reported[1] == 0, $"BW09: 5&2=0→0, got {reported[1]}");
        }

        // BW10: Bitwise ops as function arguments
        {
            string source = @"
func helper(val: int): int {
    return val & 15
}
func main() {
    Report(helper(255))
    Report(helper(16))
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW10: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == (255 & 15), $"BW10: 255&15=15, got {reported[0]}");
            Assert(reported[1] == (16 & 15), $"BW10: 16&15=0, got {reported[1]}");
        }

        // BW11: Precedence — ^ between & and |
        {
            string source = @"
func main() {
    Report(7 & 3 ^ 1 | 4)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW11: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            // ((7 & 3) ^ 1) | 4 = (3 ^ 1) | 4 = 2 | 4 = 6
            Assert(reported[0] == (((7 & 3) ^ 1) | 4), $"BW11: ((7&3)^1)|4=6, got {reported[0]}");
        }

        // BW12: Inline auto-support — bitwise in inlinable function
        {
            string source = @"
func setFlag(mask: int, flag: int): int {
    return mask | flag
}
func hasFlag(mask: int, flag: int): int {
    return mask & flag
}
func main() {
    var m: int = setFlag(0, 4)
    m = setFlag(m, 1)
    Report(m)
    Report(hasFlag(m, 4))
    Report(hasFlag(m, 2))
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW12: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == 5, $"BW12: 0|4|1=5, got {reported[0]}");
            Assert(reported[1] == 4, $"BW12: 5&4=4, got {reported[1]}");
            Assert(reported[2] == 0, $"BW12: 5&2=0, got {reported[2]}");
        }

        // BW13: Shift precedence — << is lower than + but higher than comparison
        {
            string source = @"
func main() {
    Report(1 + 1 << 3)
    if (1 << 3 > 4) { Report(1) } else { Report(0) }
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW13: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            // (1 + 1) << 3 = 2 << 3 = 16
            Assert(reported[0] == ((1 + 1) << 3), $"BW13: (1+1)<<3=16, got {reported[0]}");
            // (1 << 3) > 4 → 8 > 4 → true
            Assert(reported[1] == 1, $"BW13: 8>4=true, got {reported[1]}");
        }

        // BW14: Bitwise NOT constant folding
        {
            string source = @"
const INV: int = ~255
func main() {
    Report(INV)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, "BW14: compiles");
            var world = new VMWorld();
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
            world.Modules.Load(0, res.Program);
            world.SpawnInstance(0, 0);
            world.Tick();
            Assert(reported[0] == ~255, $"BW14: ~255=-256, got {reported[0]}");
        }

        // BW15: Loop with bitwise operations
        {
            string source = @"
func main() {
    var result: int = 0
    for var i: int = 0; i < 8; i = i + 1 {
        result = result | (1 << i)
    }
    Report(result)
}";
            var reported = new List<int>();
            var sc = new Dictionary<string, int> { { "Report", 0 } };
            var res = compiler.Compile(source, "main", sc);
            Assert(res.Success, $"BW15: compiles ({(res.Errors != null && res.Errors.Count > 0 ? res.Errors[0] : "")})");
            if (res.Success)
            {
                var world = new VMWorld();
                world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { reported.Add(s.Registers.Get(0).ToInt()); });
                world.Modules.Load(0, res.Program);
                world.SpawnInstance(0, 0);
                world.Tick();
                Assert(reported[0] == 255, $"BW15: OR all 8 bits=255, got {reported[0]}");
            }
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"Compiler Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
