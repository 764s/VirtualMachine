using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// Debug Phase 2 validation: DBG3 (breakpoint bridge) + DBG5 (variable display) + DBG6 (call stack).
/// Gate 0: command-line debugging capability — zero external dependencies.
/// </summary>
public static class DebugTests
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("TestVM/RunDebugTests")]
#endif
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

        // ================================================================
        // DBG3: Host Breakpoint Bridge
        // ================================================================

        // ===== Test DBG3-01: Single breakpoint triggers callback =====
        {
            string source = @"
func main() {
    Before()
    After()
}";
            var syscalls = new Dictionary<string, int> { { "Before", 0 }, { "After", 1 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG3-01 compile");

            var log = new List<string>();
            var bpHits = new List<(int instId, int ip, int line)>();

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Before", (ref VMInstanceState s) => { log.Add("Before"); });
            world.Syscalls.Register(1, "After", (ref VMInstanceState s) => { log.Add("After"); });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) => { bpHits.Add((instId, ip, line)); };
            world.Debugger = dbg;

            // Find which line "After()" is on (line 4, 1-based)
            dbg.AddBreakpoint(4);

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(bpHits.Count >= 1, $"DBG3-01: breakpoint hit count >= 1, got {bpHits.Count}");
            if (bpHits.Count >= 1)
            {
                Assert(bpHits[0].line == 4, $"DBG3-01: hit line == 4, got {bpHits[0].line}");
                Assert(bpHits[0].instId == id, $"DBG3-01: hit instanceId == {id}, got {bpHits[0].instId}");
            }
            // Script should still complete (breakpoint doesn't halt in Phase 2 callback mode)
            Assert(log.Contains("Before") && log.Contains("After"), "DBG3-01: script completed normally");
        }

        // ===== Test DBG3-02: Multiple breakpoints all trigger =====
        {
            string source = @"
func main() {
    A()
    B()
    C()
}";
            var syscalls = new Dictionary<string, int> { { "A", 0 }, { "B", 1 }, { "C", 2 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG3-02 compile");

            var bpHits = new List<int>(); // collect hit lines

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "A", (ref VMInstanceState s) => { });
            world.Syscalls.Register(1, "B", (ref VMInstanceState s) => { });
            world.Syscalls.Register(2, "C", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) => { bpHits.Add(line); };
            world.Debugger = dbg;

            dbg.AddBreakpoint(3); // A()
            dbg.AddBreakpoint(4); // B()
            dbg.AddBreakpoint(5); // C()

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(bpHits.Contains(3), "DBG3-02: breakpoint on line 3 hit");
            Assert(bpHits.Contains(4), "DBG3-02: breakpoint on line 4 hit");
            Assert(bpHits.Contains(5), "DBG3-02: breakpoint on line 5 hit");
        }

        // ===== Test DBG3-03: No breakpoints → no callback, normal execution =====
        {
            string source = @"
func main() {
    Ping()
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG3-03 compile");

            bool callbackFired = false;
            var log = new List<string>();

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { log.Add("Ping"); });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) => { callbackFired = true; };
            world.Debugger = dbg;
            // No breakpoints added

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(!callbackFired, "DBG3-03: no breakpoints → no callback");
            Assert(log.Count == 1, "DBG3-03: script ran normally");
        }

        // ===== Test DBG3-04: Debugger == null → zero overhead, normal execution =====
        {
            string source = @"
func main() {
    Ping()
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG3-04 compile");

            var log = new List<string>();

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { log.Add("Ping"); });
            // world.Debugger is null by default

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Debugger == null, "DBG3-04: Debugger is null");
            Assert(log.Count == 1, "DBG3-04: script ran normally without debugger");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0, "DBG3-04: completed");
        }

        // ===== Test DBG3-05: Breakpoint in loop triggers each iteration (different ticks) =====
        {
            string source = @"
func main() {
    var i: int = 0
    while i < 3 {
        Ping()
        i = i + 1
        yield
    }
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG3-05 compile");

            var bpLines = new List<int>();

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) => { bpLines.Add(line); };
            world.Debugger = dbg;

            // Find which source line Ping() is on (line 5)
            dbg.AddBreakpoint(5);

            world.SpawnInstance(0, 0);
            // 3 iterations, each yields → 3 ticks (plus one final tick to complete)
            for (int t = 0; t < 5; t++) world.Tick();

            Assert(bpLines.Count == 3, $"DBG3-05: breakpoint hit 3 times in loop, got {bpLines.Count}");
        }

        // ================================================================
        // DBG5: Variable Display Adapter
        // ================================================================

        // ===== Test DBG5-01: Scalar variable value at breakpoint =====
        {
            string source = @"
func main() {
    var x: int = 42
    var y: int = 7
    Report(x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG5-01 compile");

            List<VariableInfo> capturedVars = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedVars = dbg.GetVariables(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(5); // Report(x) line

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedVars != null, "DBG5-01: variables captured at breakpoint");
            if (capturedVars != null)
            {
                var xVar = capturedVars.Find(v => v.Name == "x");
                var yVar = capturedVars.Find(v => v.Name == "y");
                Assert(xVar.Name == "x" && xVar.Value.ToInt() == 42, $"DBG5-01: x = 42, got {xVar.Value.ToInt()}");
                Assert(yVar.Name == "y" && yVar.Value.ToInt() == 7, $"DBG5-01: y = 7, got {yVar.Value.ToInt()}");
                Assert(!xVar.IsStruct, "DBG5-01: x is not struct");
            }
        }

        // ===== Test DBG5-02: Struct variable fields at breakpoint =====
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
    Report(v.x)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG5-02 compile");

            List<VariableInfo> capturedVars = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedVars = dbg.GetVariables(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(11); // Report(v.x) line

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedVars != null, "DBG5-02: variables captured at breakpoint");
            if (capturedVars != null)
            {
                var vVar = capturedVars.Find(v => v.Name == "v");
                Assert(vVar.Name == "v", "DBG5-02: found struct variable 'v'");
                Assert(vVar.IsStruct, "DBG5-02: v is struct");
                if (vVar.IsStruct && vVar.FieldNames != null && vVar.FieldValues != null)
                {
                    Assert(vVar.FieldNames.Length == 2, $"DBG5-02: v has 2 fields, got {vVar.FieldNames.Length}");
                    Assert(vVar.FieldNames[0] == "x" && vVar.FieldValues[0].ToInt() == 10,
                        $"DBG5-02: v.x = 10, got {vVar.FieldValues[0].ToInt()}");
                    Assert(vVar.FieldNames[1] == "y" && vVar.FieldValues[1].ToInt() == 20,
                        $"DBG5-02: v.y = 20, got {vVar.FieldValues[1].ToInt()}");
                }
            }
        }

        // ===== Test DBG5-03: Variables scoped to current function =====
        {
            // Lang-9: add if-branch to prevent inlining (preserve call stack for debug test)
            string source = @"
func helper(n: int): int {
    var local_h: int = n + 100
    if local_h > 0 { Report(local_h) }
    return local_h
}

func main() {
    var local_m: int = 5
    var result: int = helper(local_m)
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG5-03 compile");

            List<VariableInfo> capturedVars = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedVars = dbg.GetVariables(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(5); // Report(local_h) line — inside helper()

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedVars != null, "DBG5-03: variables captured");
            if (capturedVars != null)
            {
                // Should see helper()'s variables, not main()'s
                bool hasLocalH = capturedVars.Exists(v => v.Name == "local_h");
                bool hasLocalM = capturedVars.Exists(v => v.Name == "local_m");
                Assert(hasLocalH, "DBG5-03: helper's local_h visible");
                Assert(!hasLocalM, "DBG5-03: main's local_m NOT visible (different scope)");
                if (hasLocalH)
                {
                    var lh = capturedVars.Find(v => v.Name == "local_h");
                    Assert(lh.Value.ToInt() == 105, $"DBG5-03: local_h = 105, got {lh.Value.ToInt()}");
                }
            }
        }

        // ================================================================
        // DBG6: Call Stack Inspection
        // ================================================================

        // ===== Test DBG6-01: Single function — call stack has 1 frame =====
        {
            string source = @"
func main() {
    Ping()
}";
            var syscalls = new Dictionary<string, int> { { "Ping", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG6-01 compile");

            List<CallStackEntry> capturedStack = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Ping", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedStack = dbg.GetCallStack(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(3); // Ping() line

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedStack != null, "DBG6-01: call stack captured");
            if (capturedStack != null)
            {
                Assert(capturedStack.Count == 1, $"DBG6-01: 1 frame, got {capturedStack.Count}");
                if (capturedStack.Count >= 1)
                {
                    Assert(capturedStack[0].FunctionName == "main", $"DBG6-01: frame 0 = 'main', got '{capturedStack[0].FunctionName}'");
                    Assert(capturedStack[0].SourceLine == 3, $"DBG6-01: frame 0 line = 3, got {capturedStack[0].SourceLine}");
                }
            }
        }

        // ===== Test DBG6-02: Two-level call — call stack has 2 frames =====
        {
            string source = @"
func helper() {
    Report(99)
}

func main() {
    helper()
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG6-02 compile");

            List<CallStackEntry> capturedStack = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedStack = dbg.GetCallStack(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(3); // Report(99) line — inside helper()

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedStack != null, "DBG6-02: call stack captured");
            if (capturedStack != null)
            {
                Assert(capturedStack.Count == 2, $"DBG6-02: 2 frames, got {capturedStack.Count}");
                if (capturedStack.Count >= 2)
                {
                    Assert(capturedStack[0].FunctionName == "helper",
                        $"DBG6-02: frame 0 = 'helper', got '{capturedStack[0].FunctionName}'");
                    Assert(capturedStack[1].FunctionName == "main",
                        $"DBG6-02: frame 1 = 'main', got '{capturedStack[1].FunctionName}'");
                }
            }
        }

        // ===== Test DBG6-03: Three-level call — a → b → c =====
        {
            string source = @"
func c() {
    Report(1)
}

func b() {
    c()
}

func main() {
    b()
}";
            var syscalls = new Dictionary<string, int> { { "Report", 0 } };
            var result = compiler.Compile(source, "main", syscalls);
            Assert(result.Success, "DBG6-03 compile");

            List<CallStackEntry> capturedStack = null;

            var world = new VMWorld();
            world.Modules.Load(0, result.Program);
            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) => { });

            var dbg = new ScriptDebugger();
            dbg.OnBreakpointHit = (instId, ip, line) =>
            {
                capturedStack = dbg.GetCallStack(result.Program, ref world.Pool.Instances[instId]);
            };
            world.Debugger = dbg;

            dbg.AddBreakpoint(3); // Report(1) line — inside c()

            world.SpawnInstance(0, 0);
            world.Tick();

            Assert(capturedStack != null, "DBG6-03: call stack captured");
            if (capturedStack != null)
            {
                Assert(capturedStack.Count == 3, $"DBG6-03: 3 frames, got {capturedStack.Count}");
                if (capturedStack.Count >= 3)
                {
                    Assert(capturedStack[0].FunctionName == "c",
                        $"DBG6-03: frame 0 = 'c', got '{capturedStack[0].FunctionName}'");
                    Assert(capturedStack[1].FunctionName == "b",
                        $"DBG6-03: frame 1 = 'b', got '{capturedStack[1].FunctionName}'");
                    Assert(capturedStack[2].FunctionName == "main",
                        $"DBG6-03: frame 2 = 'main', got '{capturedStack[2].FunctionName}'");
                }
            }
        }

        // ================================================================
        // Summary
        // ================================================================
        Debug.Log($"\n===== DebugTests: {passed} passed, {failed} failed =====");
    }
}
