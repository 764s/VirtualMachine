using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFVM;
using FFVM.Compiler;
using FFVM.Debug;
using UnityEngine;

/// <summary>
/// DAP Phase 3A + 3B tests: Content-Length framing, JSON helper, DAP protocol interaction,
/// and single-step debugging (next/stepIn/stepOut).
/// Gate 1: verify DAP server can handle a complete debug session programmatically.
/// Gate 2: verify three single-step behaviors work correctly via DAP.
/// </summary>
public static class DapTests
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("TestVM/RunDapTests")]
#endif
    public static void RunAll()
    {
        int passed = 0;
        int failed = 0;
        TestHarness.BeginSuite("DapTests");

        void Assert(bool condition, string testName)
        {
            if (condition) passed++; else failed++;
            TestHarness.Assert(condition, testName);
        }

        // ================================================================
        // A. Content-Length Stream Tests
        // ================================================================

        // ===== Test DAP-A01: ReadMessage parses Content-Length framed message =====
        {
            string body = "{\"type\":\"request\",\"command\":\"initialize\"}";
            string framed = $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(framed));

            string result = ContentLengthStream.ReadMessage(stream);
            Assert(result == body, "DAP-A01: ReadMessage parses Content-Length message");
        }

        // ===== Test DAP-A02: WriteMessage + ReadMessage roundtrip =====
        {
            string original = "{\"seq\":1,\"type\":\"response\",\"success\":true}";
            var ms = new MemoryStream();
            ContentLengthStream.WriteMessage(ms, original);

            ms.Position = 0;
            string result = ContentLengthStream.ReadMessage(ms);
            Assert(result == original, "DAP-A02: WriteMessage → ReadMessage roundtrip");
        }

        // ===== Test DAP-A03: ReadMessage handles multi-byte UTF-8 =====
        {
            string body = "{\"name\":\"变量\"}";
            var ms = new MemoryStream();
            ContentLengthStream.WriteMessage(ms, body);
            ms.Position = 0;
            string result = ContentLengthStream.ReadMessage(ms);
            Assert(result == body, "DAP-A03: Content-Length handles UTF-8");
        }

        // ===== Test DAP-A04: ReadMessage returns null on empty stream =====
        {
            var ms = new MemoryStream(new byte[0]);
            string result = ContentLengthStream.ReadMessage(ms);
            Assert(result == null, "DAP-A04: ReadMessage returns null on empty stream");
        }

        // ===== Test DAP-A05: Multiple messages in sequence =====
        {
            var ms = new MemoryStream();
            ContentLengthStream.WriteMessage(ms, "msg1");
            ContentLengthStream.WriteMessage(ms, "msg2");
            ContentLengthStream.WriteMessage(ms, "msg3");
            ms.Position = 0;

            Assert(ContentLengthStream.ReadMessage(ms) == "msg1", "DAP-A05: first message");
            Assert(ContentLengthStream.ReadMessage(ms) == "msg2", "DAP-A05: second message");
            Assert(ContentLengthStream.ReadMessage(ms) == "msg3", "DAP-A05: third message");
        }

        // ================================================================
        // B. JSON Helper Tests
        // ================================================================

        // ===== Test DAP-B01: JsonObject roundtrip =====
        {
            var obj = new JsonObject();
            obj.Set("seq", 1);
            obj.Set("type", "request");
            obj.Set("success", true);
            obj.Set("name", "test");

            string json = obj.ToJson();
            var parsed = JsonObject.Parse(json);

            Assert(parsed.GetInt("seq") == 1, "DAP-B01: int roundtrip");
            Assert(parsed.GetString("type") == "request", "DAP-B01: string roundtrip");
            Assert(parsed.GetBool("success") == true, "DAP-B01: bool roundtrip");
            Assert(parsed.GetString("name") == "test", "DAP-B01: name roundtrip");
        }

        // ===== Test DAP-B02: Nested object roundtrip =====
        {
            var inner = new JsonObject();
            inner.Set("line", 42);
            inner.Set("path", "/test/file.ffs");

            var outer = new JsonObject();
            outer.Set("source", inner);
            outer.Set("command", "setBreakpoints");

            string json = outer.ToJson();
            var parsed = JsonObject.Parse(json);

            Assert(parsed.GetString("command") == "setBreakpoints", "DAP-B02: outer string");
            var source = parsed.GetObject("source");
            Assert(source != null, "DAP-B02: nested object exists");
            Assert(source.GetInt("line") == 42, "DAP-B02: nested int");
            Assert(source.GetString("path") == "/test/file.ffs", "DAP-B02: nested string");
        }

        // ===== Test DAP-B03: Array roundtrip =====
        {
            var obj = new JsonObject();
            var arr = new List<object> { 1.0, 2.0, 3.0 };
            obj.Set("items", arr);

            string json = obj.ToJson();
            var parsed = JsonObject.Parse(json);
            var items = parsed.GetArray("items");
            Assert(items != null && items.Count == 3, "DAP-B03: array roundtrip count");
        }

        // ===== Test DAP-B04: String escaping roundtrip =====
        {
            var obj = new JsonObject();
            obj.Set("text", "line1\nline2\ttab \"quoted\" \\slash");

            string json = obj.ToJson();
            var parsed = JsonObject.Parse(json);
            Assert(parsed.GetString("text") == "line1\nline2\ttab \"quoted\" \\slash", "DAP-B04: string escaping");
        }

        // ===== Test DAP-B05: Null value =====
        {
            var obj = new JsonObject();
            obj.Set("nullVal", null);

            string json = obj.ToJson();
            var parsed = JsonObject.Parse(json);
            Assert(parsed.Get("nullVal") == null, "DAP-B05: null roundtrip");
        }

        // ================================================================
        // C. DapServer Basic Protocol Tests
        // ================================================================

        // ===== Test DAP-C01: Initialize returns capabilities =====
        {
            var session = new DapBatchSession();
            session.AddRequest("initialize", new JsonObject());
            session.AddRequest("disconnect", new JsonObject());
            session.Run();

            var initEvent = session.ReadNext();
            Assert(initEvent?.GetString("event") == "initialized", "DAP-C01: initialized event");

            var initResp = session.ReadNext();
            Assert(initResp?.GetBool("success") == true, "DAP-C01: initialize success");
            var body = initResp?.GetObject("body");
            Assert(body != null && body.GetBool("supportsConfigurationDoneRequest"), "DAP-C01: capabilities");
        }

        // ===== Test DAP-C02: Unknown command returns success=false =====
        {
            var session = new DapBatchSession();
            session.AddRequest("initialize", new JsonObject());
            session.AddRequest("unknownCommand", new JsonObject());
            session.AddRequest("disconnect", new JsonObject());
            session.Run();

            session.SkipUntilResponse("initialize");
            var unkResp = session.ReadNext();
            Assert(unkResp?.GetBool("success") == false, "DAP-C02: unknown command → success=false");
        }

        // ================================================================
        // D. Full DAP Session Tests
        // ================================================================

        // ===== Test DAP-D01: Full session — breakpoint + stackTrace + variables =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d01.ffs");
            File.WriteAllText(scriptPath, @"
func main() {
    var x: int = 42
    var y: int = 7
    var z: int = x + y
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 5));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddStackTrace();
                session.AddScopes(0);
                session.AddVariables(1);
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                // Parse outputs
                var initEvent = session.ExpectEvent("initialized");
                Assert(initEvent != null, "DAP-D01: initialized event");

                var initResp = session.ExpectResponse("initialize");
                Assert(initResp?.GetBool("success") == true, "DAP-D01: initialize success");

                var launchResp = session.ExpectResponse("launch");
                Assert(launchResp?.GetBool("success") == true, "DAP-D01: launch success");

                var bpResp = session.ExpectResponse("setBreakpoints");
                Assert(bpResp?.GetBool("success") == true, "DAP-D01: setBreakpoints success");
                var bpBody = bpResp?.GetObject("body");
                var bps = bpBody?.GetArray("breakpoints");
                Assert(bps != null && bps.Count == 1, $"DAP-D01: one breakpoint returned, got {bps?.Count}");
                if (bps?.Count > 0)
                {
                    var bp0 = bps[0] as JsonObject;
                    Assert(bp0?.GetBool("verified") == true, "DAP-D01: breakpoint verified");
                }

                session.ExpectResponse("configurationDone");

                var stoppedEvent = session.ExpectEvent("stopped");
                Assert(stoppedEvent != null, "DAP-D01: stopped event received");
                var stoppedBody = stoppedEvent?.GetObject("body");
                Assert(stoppedBody?.GetString("reason") == "breakpoint", "DAP-D01: stopped reason = breakpoint");

                session.ExpectResponse("continue");

                var stResp = session.ExpectResponse("stackTrace");
                var frames = stResp?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames != null && frames.Count >= 1, $"DAP-D01: at least 1 frame, got {frames?.Count}");
                if (frames?.Count >= 1)
                {
                    var f0 = frames[0] as JsonObject;
                    Assert(f0?.GetString("name") == "main", "DAP-D01: top frame = main");
                    Assert(f0?.GetInt("line") == 5, $"DAP-D01: line = 5, got {f0?.GetInt("line")}");
                }

                session.ExpectResponse("scopes");

                var varResp = session.ExpectResponse("variables");
                var variables = varResp?.GetObject("body")?.GetArray("variables");
                Assert(variables != null && variables.Count >= 2, $"DAP-D01: >= 2 vars, got {variables?.Count}");
                if (variables != null)
                {
                    bool foundX = false, foundY = false;
                    foreach (var vObj in variables)
                    {
                        var v = vObj as JsonObject;
                        if (v?.GetString("name") == "x" && v?.GetString("value") == "42") foundX = true;
                        if (v?.GetString("name") == "y" && v?.GetString("value") == "7") foundY = true;
                    }
                    Assert(foundX, "DAP-D01: x = 42");
                    Assert(foundY, "DAP-D01: y = 7");
                }

                session.ExpectResponse("disconnect");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D02: Breakpoint in function — 2-frame call stack =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d02.ffs");
            // Lang-9 P2: body must exceed InlineThreshold to preserve CALL frame for DAP test
            File.WriteAllText(scriptPath, @"
func helper(): int {
    var val: int = 100
    var t1: int = val + 1
    var t2: int = t1 + 2
    var t3: int = t2 + 3
    var t4: int = t3 + 4
    if val > 0 { val = t4 }
    return val
}

func main() {
    var result: int = helper()
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 8));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddStackTrace();
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                var stoppedEvent = session.ExpectEvent("stopped");
                Assert(stoppedEvent != null, "DAP-D02: stopped in helper");

                session.ExpectResponse("continue");

                var stResp = session.ExpectResponse("stackTrace");
                var frames = stResp?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames != null && frames.Count == 2, $"DAP-D02: 2 frames, got {frames?.Count}");
                if (frames?.Count >= 2)
                {
                    Assert((frames[0] as JsonObject)?.GetString("name") == "helper", "DAP-D02: frame 0 = helper");
                    Assert((frames[1] as JsonObject)?.GetString("name") == "main", "DAP-D02: frame 1 = main");
                    Assert((frames[0] as JsonObject)?.GetInt("line") == 8,
                        $"DAP-D02: helper line = 8, got {(frames[0] as JsonObject)?.GetInt("line")}");
                }
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D03: Struct variable expansion =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d03.ffs");
            File.WriteAllText(scriptPath, @"
struct Vec2 {
    x: int
    y: int
}

func main() {
    var v: Vec2
    v.x = 10
    v.y = 20
    var z: int = v.x
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 11));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddScopes(0);
                session.AddVariables(1);
                session.AddVariables(1000); // struct expansion
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                var stoppedEvent = session.ExpectEvent("stopped");
                Assert(stoppedEvent != null, "DAP-D03: stopped event");

                session.ExpectResponse("continue");
                session.ExpectResponse("scopes");

                var varResp = session.ExpectResponse("variables");
                var variables = varResp?.GetObject("body")?.GetArray("variables");

                JsonObject structVar = null;
                int structRef = 0;
                if (variables != null)
                {
                    foreach (var vObj in variables)
                    {
                        var v = vObj as JsonObject;
                        if (v?.GetString("name") == "v" && v?.GetString("type") == "struct")
                        {
                            structVar = v;
                            structRef = v.GetInt("variablesReference");
                            break;
                        }
                    }
                }

                Assert(structVar != null, "DAP-D03: struct variable 'v' found");
                Assert(structRef >= 1000, $"DAP-D03: struct variablesReference >= 1000, got {structRef}");

                var fieldResp = session.ExpectResponse("variables");
                var fields = fieldResp?.GetObject("body")?.GetArray("variables");
                Assert(fields != null && fields.Count == 2, $"DAP-D03: 2 fields, got {fields?.Count}");
                if (fields?.Count >= 2)
                {
                    Assert((fields[0] as JsonObject)?.GetString("name") == "x"
                        && (fields[0] as JsonObject)?.GetString("value") == "10", "DAP-D03: v.x = 10");
                    Assert((fields[1] as JsonObject)?.GetString("name") == "y"
                        && (fields[1] as JsonObject)?.GetString("value") == "20", "DAP-D03: v.y = 20");
                }
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D04: No breakpoints → terminated event =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d04.ffs");
            File.WriteAllText(scriptPath, "func main() {\n    var x: int = 1\n}\n");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                var terminatedEvent = session.ExpectEvent("terminated");
                Assert(terminatedEvent != null, "DAP-D04: terminated event when no breakpoints");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D05: Threads returns single thread =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d05.ffs");
            File.WriteAllText(scriptPath, "func main() {\n    var x: int = 1\n}\n");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("threads", new JsonObject());
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("launch");
                var threadsResp = session.ExpectResponse("threads");
                Assert(threadsResp?.GetBool("success") == true, "DAP-D05: threads success");
                var threads = threadsResp?.GetObject("body")?.GetArray("threads");
                Assert(threads != null && threads.Count == 1, "DAP-D05: single thread");
                if (threads?.Count > 0)
                    Assert((threads[0] as JsonObject)?.GetInt("id") == 1, "DAP-D05: threadId = 1");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D06: Continue after breakpoint → second continue completes =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_d06.ffs");
            File.WriteAllText(scriptPath, @"
func main() {
    var a: int = 10
    var b: int = 20
    var c: int = a + b
    var d: int = c * 2
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 5));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddVariables(1);
                session.AddContinue(); // second continue → should complete
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                var stopped = session.ExpectEvent("stopped");
                Assert(stopped != null, "DAP-D06: stopped at breakpoint");

                session.ExpectResponse("continue");

                var varResp = session.ExpectResponse("variables");
                var vars = varResp?.GetObject("body")?.GetArray("variables");
                bool foundA = false, foundB = false;
                if (vars != null)
                {
                    foreach (var vObj in vars)
                    {
                        var v = vObj as JsonObject;
                        if (v?.GetString("name") == "a" && v?.GetString("value") == "10") foundA = true;
                        if (v?.GetString("name") == "b" && v?.GetString("value") == "20") foundB = true;
                    }
                }
                Assert(foundA, "DAP-D06: a = 10 at breakpoint");
                Assert(foundB, "DAP-D06: b = 20 at breakpoint");

                var terminated = session.ExpectEvent("terminated");
                Assert(terminated != null, "DAP-D06: terminated after second continue");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-D07: Unknown command → success=false =====
        {
            var session = new DapBatchSession();
            session.AddRequest("initialize", new JsonObject());
            session.AddRequest("unknownCommand", new JsonObject());
            session.AddRequest("disconnect", new JsonObject());
            session.Run();

            session.SkipUntilResponse("initialize");
            var unkResp = session.ExpectResponse("unknownCommand");
            Assert(unkResp?.GetBool("success") == false, "DAP-D07: unknown command → success=false");
        }

        // ================================================================
        // E. StandaloneRunner Stream Integration Test
        // ================================================================

        // ===== Test DAP-E01: Full pipeline via ContentLengthStream =====
        {
            var inputMs = new MemoryStream();
            var outputMs = new MemoryStream();
            int seq = 1;
            WriteRequest(inputMs, ref seq, "initialize", new JsonObject());
            WriteRequest(inputMs, ref seq, "disconnect", new JsonObject());
            inputMs.Position = 0;

            var server = new DapServer(inputMs, outputMs);
            server.Run();

            outputMs.Position = 0;
            var msg1 = JsonObject.Parse(ContentLengthStream.ReadMessage(outputMs));
            var msg2 = JsonObject.Parse(ContentLengthStream.ReadMessage(outputMs));

            Assert(msg1?.GetString("type") == "event" && msg1?.GetString("event") == "initialized",
                "DAP-E01: initialized event via stream");
            Assert(msg2?.GetString("type") == "response" && msg2?.GetBool("success") == true,
                "DAP-E01: initialize response via stream");
        }

        // ================================================================
        // S. Phase 3B — Single-Step Tests (DAP-S01 ~ DAP-S08)
        // ================================================================

        // ===== Test DAP-S01: FindNextLineIP (Step Over helper) =====
        {
            // Script:
            // line 2: var a: int = 1
            // line 3: var b: int = 2
            // line 4: var c: int = a + b
            string source = "func main() {\n    var a: int = 1\n    var b: int = 2\n    var c: int = a + b\n}\n";
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DAP-S01: compile success");

            // Find the IP for line 2
            int ipLine2 = -1;
            for (int i = 0; i < result.Program.SourceMap.Length; i++)
            {
                if (result.Program.SourceMap[i] == 2) { ipLine2 = i; break; }
            }
            Assert(ipLine2 >= 0, "DAP-S01: found IP for line 2");

            int nextIP = ScriptDebugger.FindNextLineIP(result.Program, ipLine2);
            Assert(nextIP > ipLine2, "DAP-S01: next IP > current IP");
            int nextLine = result.Program.SourceMap[nextIP];
            Assert(nextLine == 3, $"DAP-S01: next line = 3, got {nextLine}");
        }

        // ===== Test DAP-S02: FindStepIntoIP enters CALL =====
        {
            // Lang-9 P2: body must exceed InlineThreshold to preserve CALL for step-into test
            string source = "func helper(): int {\n    var x: int = 42\n    var t1: int = x + 1\n    var t2: int = t1 + 2\n    var t3: int = t2 + 3\n    var t4: int = t3 + 4\n    if x > 0 { x = t4 }\n    return x\n}\n\nfunc main() {\n    var x: int = helper()\n}\n";
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DAP-S02: compile success");

            // Find a CALL or CALL_LEAF instruction
            int callIP = -1;
            for (int i = 0; i < result.Program.Instructions.Length; i++)
            {
                if (result.Program.Instructions[i].Code == OpCode.CALL ||
                    result.Program.Instructions[i].Code == OpCode.CALL_LEAF)
                {
                    callIP = i;
                    break;
                }
            }
            Assert(callIP >= 0, "DAP-S02: found CALL/CALL_LEAF instruction");

            int stepIntoIP = ScriptDebugger.FindStepIntoIP(result.Program, callIP);
            Assert(stepIntoIP == result.Program.Instructions[callIP].A,
                $"DAP-S02: stepInto target = CALL.A = {result.Program.Instructions[callIP].A}, got {stepIntoIP}");
        }

        // ===== Test DAP-S03: FindStepIntoIP degrades to Step Over on non-CALL =====
        {
            string source = "func main() {\n    var a: int = 1\n    var b: int = 2\n}\n";
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, "main", new Dictionary<string, int>());
            Assert(result.Success, "DAP-S03: compile success");

            int ipLine2 = -1;
            for (int i = 0; i < result.Program.SourceMap.Length; i++)
            {
                if (result.Program.SourceMap[i] == 2) { ipLine2 = i; break; }
            }
            Assert(ipLine2 >= 0, "DAP-S03: found IP for line 2");

            int stepIntoIP = ScriptDebugger.FindStepIntoIP(result.Program, ipLine2);
            int nextLineIP = ScriptDebugger.FindNextLineIP(result.Program, ipLine2);
            Assert(stepIntoIP == nextLineIP, $"DAP-S03: stepInto == nextLine = {nextLineIP}, got {stepIntoIP}");
        }

        // ===== Test DAP-S04: FindStepOutIP returns ReturnIP =====
        {
            // We need to check that FindStepOutIP returns the correct ReturnIP
            // Set up a VMInstanceState with a CallStack entry
            var inst = new VMInstanceState();
            inst.CallStackDepth = 1;
            inst.CallStack.Set(0, new CallFrame { ReturnIP = 42, RegisterBase = 0, CleanupBase = 0 });

            int stepOutIP = ScriptDebugger.FindStepOutIP(ref inst);
            Assert(stepOutIP == 42, $"DAP-S04: stepOut IP = 42, got {stepOutIP}");
        }

        // ===== Test DAP-S05: Temp breakpoint auto-clears after one hit =====
        {
            var debugger = new ScriptDebugger();
            int hitCount = 0;
            debugger.OnBreakpointHit = (id, ip, line) => hitCount++;
            debugger.SetTempBreakpoint(5);

            int[] sourceMap = { 0, 1, 2, 3, 4, 5, 6 };

            // First check at IP=5 should hit
            bool hit1 = debugger.CheckBreakpoint(0, 5, sourceMap);
            Assert(hit1, "DAP-S05: temp breakpoint hit at IP=5");
            Assert(hitCount == 1, "DAP-S05: hit count = 1");

            debugger.ResetTickState();

            // Second check at IP=5 should NOT hit (auto-cleared)
            bool hit2 = debugger.CheckBreakpoint(0, 5, sourceMap);
            Assert(!hit2, "DAP-S05: temp breakpoint auto-cleared");
            Assert(hitCount == 1, "DAP-S05: hit count still 1");
        }

        // ===== Test DAP-S06: Full DAP session — next (Step Over) =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_s06.ffs");
            File.WriteAllText(scriptPath, @"
func main() {
    var a: int = 10
    var b: int = 20
    var c: int = a + b
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 3));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();              // run to breakpoint at line 3
                session.AddStackTrace();             // check we're at line 3
                session.AddNext();                   // step over to line 4
                session.AddStackTrace();             // check we're at line 4
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");
                var stopped1 = session.ExpectEvent("stopped");
                Assert(stopped1 != null, "DAP-S06: stopped at breakpoint");
                if (stopped1 != null)
                    Assert(stopped1.GetObject("body")?.GetString("reason") == "breakpoint", "DAP-S06: reason = breakpoint");

                session.ExpectResponse("continue");

                var st1 = session.ExpectResponse("stackTrace");
                var frames1 = st1?.GetObject("body")?.GetArray("stackFrames");
                int line1 = (frames1 != null && frames1.Count > 0) ? ((frames1[0] as JsonObject)?.GetInt("line") ?? 0) : 0;
                Assert(line1 == 3, $"DAP-S06: first stop at line 3, got {line1}");

                // Step over → next line
                var stopped2 = session.ExpectEvent("stopped");
                Assert(stopped2 != null, "DAP-S06: stopped after next");
                if (stopped2 != null)
                    Assert(stopped2.GetObject("body")?.GetString("reason") == "step", "DAP-S06: reason = step");

                session.ExpectResponse("next");

                var st2 = session.ExpectResponse("stackTrace");
                var frames2 = st2?.GetObject("body")?.GetArray("stackFrames");
                int line2 = (frames2 != null && frames2.Count > 0) ? ((frames2[0] as JsonObject)?.GetInt("line") ?? 0) : 0;
                Assert(line2 == 4, $"DAP-S06: after next at line 4, got {line2}");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-S07: Full DAP session — stepIn enters function =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_s07.ffs");
            // Lang-9 P2: body must exceed InlineThreshold to preserve CALL for step-into test
            File.WriteAllText(scriptPath, @"
func helper(): int {
    var val: int = 42
    var t1: int = val + 1
    var t2: int = t1 + 2
    var t3: int = t2 + 3
    var t4: int = t3 + 4
    if val > 0 { val = t4 }
    return val
}

func main() {
    var x: int = helper()
    var y: int = x + 1
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                // Set breakpoint at line 13 (var x: int = helper())
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 13));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();              // run to breakpoint at line 9
                session.AddStackTrace();             // verify at line 9
                session.AddStepIn();                 // step into helper()
                session.AddStackTrace();             // should be inside helper
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                // Stop at breakpoint
                var stopped1 = session.ExpectEvent("stopped");
                Assert(stopped1 != null, "DAP-S07: stopped at breakpoint");

                session.ExpectResponse("continue");

                var st1 = session.ExpectResponse("stackTrace");
                var frames1 = st1?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames1 != null && frames1.Count == 1, $"DAP-S07: 1 frame at breakpoint, got {frames1?.Count}");
                string fn1 = (frames1?[0] as JsonObject)?.GetString("name");
                Assert(fn1 == "main", $"DAP-S07: at main, got {fn1}");

                // Step into helper()
                var stopped2 = session.ExpectEvent("stopped");
                Assert(stopped2 != null, "DAP-S07: stopped after stepIn");
                Assert(stopped2.GetObject("body")?.GetString("reason") == "step", "DAP-S07: reason = step");

                session.ExpectResponse("stepIn");

                var st2 = session.ExpectResponse("stackTrace");
                var frames2 = st2?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames2 != null && frames2.Count == 2, $"DAP-S07: 2 frames after stepIn, got {frames2?.Count}");
                string fn2 = (frames2?[0] as JsonObject)?.GetString("name");
                Assert(fn2 == "helper", $"DAP-S07: inside helper, got {fn2}");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-S08: Full DAP session — stepOut returns to caller =====
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), "dap_test_s08.ffs");
            // Lang-9 P2: body must exceed InlineThreshold to preserve CALL frame for step-out test
            File.WriteAllText(scriptPath, @"
func helper(): int {
    var val: int = 42
    var t1: int = val + 1
    var t2: int = t1 + 2
    var t3: int = t2 + 3
    var t4: int = t3 + 4
    if val > 0 { val = t4 }
    return val
}

func main() {
    var x: int = helper()
    var y: int = x + 1
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                // Set breakpoint at line 3 (inside helper: var val: int = 42)
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 3));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();              // run to breakpoint inside helper at line 3
                session.AddStackTrace();             // verify inside helper
                session.AddStepOut();                // step out of helper back to main
                session.AddStackTrace();             // should be back in main
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("configurationDone");

                // Stop at breakpoint inside helper
                var stopped1 = session.ExpectEvent("stopped");
                Assert(stopped1 != null, "DAP-S08: stopped at breakpoint in helper");

                session.ExpectResponse("continue");

                var st1 = session.ExpectResponse("stackTrace");
                var frames1 = st1?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames1 != null && frames1.Count == 2, $"DAP-S08: 2 frames at breakpoint, got {frames1?.Count}");
                string fn1 = (frames1?[0] as JsonObject)?.GetString("name");
                Assert(fn1 == "helper", $"DAP-S08: inside helper, got {fn1}");

                // Step out → back to main
                var stopped2 = session.ExpectEvent("stopped");
                Assert(stopped2 != null, "DAP-S08: stopped after stepOut");
                Assert(stopped2.GetObject("body")?.GetString("reason") == "step", "DAP-S08: reason = step");

                session.ExpectResponse("stepOut");

                var st2 = session.ExpectResponse("stackTrace");
                var frames2 = st2?.GetObject("body")?.GetArray("stackFrames");
                Assert(frames2 != null && frames2.Count == 1, $"DAP-S08: 1 frame after stepOut, got {frames2?.Count}");
                string fn2 = (frames2?[0] as JsonObject)?.GetString("name");
                Assert(fn2 == "main", $"DAP-S08: back in main, got {fn2}");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ================================================================
        // E002 DAP Tests: syscallDecl loading
        // ================================================================

        // E002-DAP-01: DapServer launch with syscallDecl loads syscall declarations
        {
            string scriptContent = @"
func main() {
    Report(42)
}";
            string declContent = @"{
    ""syscalls"": {
        ""Report"": { ""params"": [{ ""name"": ""value"", ""type"": ""int"" }], ""returnType"": ""void"", ""description"": ""report value"" }
    }
}";
            string scriptPath = Path.Combine(Path.GetTempPath(), $"e002_dap_{Guid.NewGuid()}.ffs");
            string declPath = Path.Combine(Path.GetTempPath(), $"e002_dap_{Guid.NewGuid()}.ffvm.d.json");
            try
            {
                File.WriteAllText(scriptPath, scriptContent);
                File.WriteAllText(declPath, declContent);

                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                // Launch with syscallDecl
                var launchArgs = new JsonObject();
                launchArgs.Set("program", scriptPath);
                launchArgs.Set("syscallDecl", declPath);
                session.AddRequest("launch", launchArgs);
                session.AddRequest("configurationDone", null);
                session.AddContinue();
                session.AddRequest("disconnect", null);

                session.Run();

                var initResp = session.ExpectResponse("initialize");
                Assert(initResp?.GetBool("success") == true, "E002-DAP-01: initialize success");
                var launchResp = session.ExpectResponse("launch");
                Assert(launchResp?.GetBool("success") == true, "E002-DAP-01: launch with syscallDecl success");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
                if (File.Exists(declPath)) File.Delete(declPath);
            }
        }

        // E002-DAP-02: DapServer launch without syscallDecl — syscall scripts fail to compile
        {
            string scriptContent = @"
func main() {
    Report(42)
}";
            string scriptPath = Path.Combine(Path.GetTempPath(), $"e002_dap2_{Guid.NewGuid()}.ffs");
            try
            {
                File.WriteAllText(scriptPath, scriptContent);

                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath); // no syscallDecl → Report is unknown
                session.AddRequest("disconnect", null);

                session.Run();

                var initResp = session.ExpectResponse("initialize");
                Assert(initResp?.GetBool("success") == true, "E002-DAP-02: initialize success");
                var launchResp = session.ExpectResponse("launch");
                // Launch should fail because Report is not a known function
                Assert(launchResp?.GetBool("success") == false, "E002-DAP-02: launch without syscallDecl fails for syscall script");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ================================================================
        // F. setBreakpoints source.path bucketing (Phase 1 "止血")
        // ================================================================

        // ===== Test DAP-F01: foreign source.path returns unverified, does not clear existing =====
        // Scenario: client first sets a real breakpoint on the launched script, then sends
        // setBreakpoints for an unrelated file. The unrelated request must NOT clear the
        // existing breakpoint, and must report all of its own entries as verified=false.
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"dap_f01_{Guid.NewGuid()}.ffs");
            string foreignPath = Path.Combine(Path.GetTempPath(), $"dap_f01_other_{Guid.NewGuid()}.ffs");
            File.WriteAllText(scriptPath, @"
func main() {
    var x: int = 42
    var y: int = 7
    var z: int = x + y
}");

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                // 1) Real breakpoint at line 5 (z = x + y) for the launched script
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(scriptPath, 5));
                // 2) Foreign request for a different file — must not clear (1)
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(foreignPath, 3, 4));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("launch");

                var bp1 = session.ExpectResponse("setBreakpoints");
                var bps1 = bp1?.GetObject("body")?.GetArray("breakpoints");
                Assert(bps1 != null && bps1.Count == 1, "DAP-F01: main-script setBreakpoints returned 1 entry");
                Assert((bps1?[0] as JsonObject)?.GetBool("verified") == true,
                    "DAP-F01: main-script breakpoint verified=true");

                var bp2 = session.ExpectResponse("setBreakpoints");
                Assert(bp2?.GetBool("success") == true, "DAP-F01: foreign setBreakpoints request succeeded");
                var bps2 = bp2?.GetObject("body")?.GetArray("breakpoints");
                Assert(bps2 != null && bps2.Count == 2,
                    $"DAP-F01: foreign setBreakpoints returned 2 entries, got {bps2?.Count}");
                bool allUnverified = true;
                if (bps2 != null)
                {
                    foreach (var entry in bps2)
                    {
                        if ((entry as JsonObject)?.GetBool("verified") != false)
                            allUnverified = false;
                    }
                }
                Assert(allUnverified, "DAP-F01: foreign breakpoints all verified=false");

                // Existing breakpoint must still hit — proves foreign request did not clear state.
                session.ExpectResponse("configurationDone");
                var stopped = session.ExpectEvent("stopped");
                Assert(stopped != null, "DAP-F01: existing main-script breakpoint still hits after foreign request");
                Assert(stopped?.GetObject("body")?.GetString("reason") == "breakpoint",
                    "DAP-F01: stopped reason = breakpoint");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ===== Test DAP-F02: matching source.path applies breakpoints (path normalization) =====
        // Scenario: source.path differs in casing/separator from the launch program path
        // but resolves to the same file via Path.GetFullPath. The breakpoint must still verify.
        {
            string scriptPath = Path.Combine(Path.GetTempPath(), $"dap_f02_{Guid.NewGuid()}.ffs");
            File.WriteAllText(scriptPath, @"
func main() {
    var x: int = 1
    var y: int = 2
}");
            // Build an unnormalized variant that still resolves to the same file
            string unnormalized = Path.Combine(Path.GetDirectoryName(scriptPath) ?? ".", ".",
                Path.GetFileName(scriptPath));

            try
            {
                var session = new DapBatchSession();
                session.AddRequest("initialize", new JsonObject());
                session.AddLaunch(scriptPath);
                session.AddRequest("setBreakpoints", MakeSetBreakpointsArgs(unnormalized, 4));
                session.AddRequest("configurationDone", new JsonObject());
                session.AddContinue();
                session.AddRequest("disconnect", new JsonObject());
                session.Run();

                session.SkipUntilResponse("launch");

                var bpResp = session.ExpectResponse("setBreakpoints");
                Assert(bpResp?.GetBool("success") == true, "DAP-F02: setBreakpoints success");
                var bps = bpResp?.GetObject("body")?.GetArray("breakpoints");
                Assert(bps != null && bps.Count == 1, "DAP-F02: one breakpoint returned");
                Assert((bps?[0] as JsonObject)?.GetBool("verified") == true,
                    "DAP-F02: unnormalized-but-equivalent path verified=true");
            }
            finally
            {
                if (File.Exists(scriptPath)) File.Delete(scriptPath);
            }
        }

        // ================================================================
        // Summary
        // ================================================================
        Debug.Log($"\n===== DapTests: {passed} passed, {failed} failed =====");
        TestHarness.EndSuite();
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static JsonObject MakeSetBreakpointsArgs(string scriptPath, params int[] lines)
    {
        var args = new JsonObject();
        var source = new JsonObject();
        source.Set("path", scriptPath);
        args.Set("source", source);

        var bpList = new List<object>();
        foreach (int line in lines)
        {
            var bp = new JsonObject();
            bp.Set("line", line);
            bpList.Add(bp);
        }
        args.Set("breakpoints", bpList);
        return args;
    }

    private static void WriteRequest(MemoryStream ms, ref int seq, string command, JsonObject arguments)
    {
        var req = new JsonObject();
        req.Set("seq", seq++);
        req.Set("type", "request");
        req.Set("command", command);
        if (arguments != null)
            req.Set("arguments", arguments);
        ContentLengthStream.WriteMessage(ms, req.ToJson());
    }

    /// <summary>
    /// Batch-mode DAP test session. All requests are added upfront, server runs once,
    /// then outputs are consumed in order. Simple, deterministic, no concurrency.
    /// </summary>
    private class DapBatchSession
    {
        private readonly MemoryStream _inputMs = new MemoryStream();
        private int _seq = 1;
        private List<JsonObject> _messages;
        private int _readIndex;

        public void AddRequest(string command, JsonObject arguments)
        {
            var req = new JsonObject();
            req.Set("seq", _seq++);
            req.Set("type", "request");
            req.Set("command", command);
            if (arguments != null)
                req.Set("arguments", arguments);
            ContentLengthStream.WriteMessage(_inputMs, req.ToJson());
        }

        public void AddLaunch(string scriptPath)
        {
            var args = new JsonObject();
            args.Set("program", scriptPath);
            AddRequest("launch", args);
        }

        public void AddContinue()
        {
            var args = new JsonObject();
            args.Set("threadId", 1);
            AddRequest("continue", args);
        }

        public void AddStackTrace()
        {
            var args = new JsonObject();
            args.Set("threadId", 1);
            AddRequest("stackTrace", args);
        }

        public void AddScopes(int frameId)
        {
            var args = new JsonObject();
            args.Set("frameId", frameId);
            AddRequest("scopes", args);
        }

        public void AddVariables(int variablesReference)
        {
            var args = new JsonObject();
            args.Set("variablesReference", variablesReference);
            AddRequest("variables", args);
        }

        public void Run()
        {
            _inputMs.Position = 0;
            var outputMs = new MemoryStream();
            var server = new DapServer(_inputMs, outputMs);
            server.Run();

            outputMs.Position = 0;
            _messages = new List<JsonObject>();
            while (true)
            {
                string msg = ContentLengthStream.ReadMessage(outputMs);
                if (msg == null) break;
                var parsed = JsonObject.Parse(msg);
                if (parsed != null) _messages.Add(parsed);
            }
            _readIndex = 0;
        }

        /// <summary>Number of captured messages.</summary>
        public int MessageCount => _messages?.Count ?? 0;

        /// <summary>Dump all messages for debugging (does not move readIndex).</summary>
        public void DumpAll()
        {
            if (_messages == null) { Debug.Log("  (no messages)"); return; }
            for (int i = 0; i < _messages.Count; i++)
            {
                var m = _messages[i];
                string type = m.GetString("type") ?? "?";
                string detail = "";
                if (type == "event") detail = m.GetString("event") ?? "?";
                else if (type == "response") detail = m.GetString("command") ?? "?";
                string extra = "";
                if (type == "event" && detail == "stopped")
                {
                    var body = m.GetObject("body");
                    extra = " reason=" + (body != null ? body.GetString("reason") ?? "?" : "null");
                }
                Debug.Log("  MSG[" + i + "] " + type + ": " + detail + extra);
            }
        }

        public JsonObject ReadNext()
        {
            if (_messages != null && _readIndex < _messages.Count)
                return _messages[_readIndex++];
            return null;
        }

        public JsonObject ExpectResponse(string command)
        {
            while (_messages != null && _readIndex < _messages.Count)
            {
                var msg = _messages[_readIndex++];
                if (msg.GetString("type") == "response" && msg.GetString("command") == command)
                    return msg;
            }
            return null;
        }

        public JsonObject ExpectEvent(string eventName)
        {
            while (_messages != null && _readIndex < _messages.Count)
            {
                var msg = _messages[_readIndex++];
                if (msg.GetString("type") == "event" && msg.GetString("event") == eventName)
                    return msg;
            }
            return null;
        }

        public void AddNext()
        {
            var args = new JsonObject();
            args.Set("threadId", 1);
            AddRequest("next", args);
        }

        public void AddStepIn()
        {
            var args = new JsonObject();
            args.Set("threadId", 1);
            AddRequest("stepIn", args);
        }

        public void AddStepOut()
        {
            var args = new JsonObject();
            args.Set("threadId", 1);
            AddRequest("stepOut", args);
        }

        public void SkipUntilResponse(string command)
        {
            while (_messages != null && _readIndex < _messages.Count)
            {
                var msg = _messages[_readIndex++];
                if (msg.GetString("type") == "response" && msg.GetString("command") == command)
                    return;
            }
        }
    }
}
