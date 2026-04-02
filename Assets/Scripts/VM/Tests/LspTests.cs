using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFVM.Debug;
using UnityEngine;

/// <summary>
/// LSP Phase 4 tests: LspServer lifecycle, document sync, and diagnostics.
/// Verifies the LSP server can handle initialization, document management,
/// and compile-on-change diagnostics for FFVM scripts.
/// </summary>
public static class LspTests
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("TestVM/RunLspTests")]
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

        // ================================================================
        // A. LSP Lifecycle Tests
        // ================================================================

        // ===== Test LSP-T01: initialize response contains capabilities =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0); // id=0
            Assert(initResp != null, "LSP-T01: initialize response received");

            if (initResp != null)
            {
                var result = initResp.GetObject("result");
                Assert(result != null, "LSP-T01: result not null");
                if (result != null)
                {
                    var caps = result.GetObject("capabilities");
                    Assert(caps != null, "LSP-T01: capabilities present");
                    if (caps != null)
                    {
                        var sync = caps.GetObject("textDocumentSync");
                        Assert(sync != null, "LSP-T01: textDocumentSync present");
                        if (sync != null)
                        {
                            Assert(sync.GetBool("openClose") == true, "LSP-T01: openClose = true");
                            Assert(sync.GetInt("change") == 1, "LSP-T01: change = Full (1)");
                        }
                    }
                }
            }
        }

        // ===== Test LSP-T02: full lifecycle initialize → initialized → shutdown → exit =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            Assert(initResp != null, "LSP-T02: initialize response");

            var shutdownResp = session.ExpectResponse(1);
            Assert(shutdownResp != null, "LSP-T02: shutdown response");

            // Server should have stopped after exit
            Assert(session.ServerStopped, "LSP-T02: server stopped after exit");
        }

        // ================================================================
        // B. Document Sync Tests
        // ================================================================

        // ===== Test LSP-T03: didOpen stores document content =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.vm", "func entry() { wait 1 }");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            // didOpen should trigger compilation → diagnostics
            var diag = session.FindNotification("textDocument/publishDiagnostics");
            Assert(diag != null, "LSP-T03: publishDiagnostics received after didOpen");
        }

        // ===== Test LSP-T04: didChange updates document content =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.vm", "func entry() { wait 1 }");
            session.AddDidChange("file:///test.vm", "func entry() { wait 2 }");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            // Should have received two publishDiagnostics (one per open/change)
            var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
            Assert(diags.Count >= 2, $"LSP-T04: got {diags.Count} publishDiagnostics, expected ≥ 2");
        }

        // ================================================================
        // C. Diagnostics Tests
        // ================================================================

        // ===== Test LSP-T05: didOpen with syntax error → diagnostics contain error =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            // Missing closing brace — parse error
            session.AddDidOpen("file:///err.vm", "func entry() { var x: int = ");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var diag = session.FindNotification("textDocument/publishDiagnostics");
            Assert(diag != null, "LSP-T05: diagnostics received");
            if (diag != null)
            {
                var parameters = diag.GetObject("params");
                Assert(parameters != null, "LSP-T05: params present");
                if (parameters != null)
                {
                    string uri = parameters.GetString("uri");
                    Assert(uri == "file:///err.vm", $"LSP-T05: uri = file:///err.vm, got {uri}");
                    var diagList = parameters.GetArray("diagnostics");
                    Assert(diagList != null && diagList.Count > 0, "LSP-T05: diagnostics list non-empty");

                    if (diagList != null && diagList.Count > 0)
                    {
                        var first = diagList[0] as JsonObject;
                        Assert(first != null, "LSP-T05: first diagnostic is JsonObject");
                        if (first != null)
                        {
                            Assert(first.GetInt("severity") == 1, "LSP-T05: severity = Error (1)");
                            string msg = first.GetString("message");
                            Assert(!string.IsNullOrEmpty(msg), "LSP-T05: message not empty");
                            Assert(first.GetString("source") == "ffvm", "LSP-T05: source = ffvm");
                        }
                    }
                }
            }
        }

        // ===== Test LSP-T06: didChange fixes error → diagnostics become empty =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            // First open with error
            session.AddDidOpen("file:///fix.vm", "func entry() { var x: int = }");
            // Then fix it
            session.AddDidChange("file:///fix.vm", "func entry() {\n    var x: int = 42\n}");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
            Assert(diags.Count >= 2, $"LSP-T06: got {diags.Count} diagnostics notifications, expected ≥ 2");

            if (diags.Count >= 2)
            {
                // First notification should have errors
                var firstParams = diags[0].GetObject("params");
                var firstDiags = firstParams?.GetArray("diagnostics");
                Assert(firstDiags != null && firstDiags.Count > 0, "LSP-T06: first notification has errors");

                // Last notification should be empty (errors fixed)
                var lastParams = diags[diags.Count - 1].GetObject("params");
                var lastDiags = lastParams?.GetArray("diagnostics");
                Assert(lastDiags != null && lastDiags.Count == 0, "LSP-T06: last notification has no errors");
            }
        }

        // ===== Test LSP-T07: didOpen valid script → empty diagnostics =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ok.vm", "func entry() {\n    var x: int = 42\n    wait 1\n}");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var diag = session.FindNotification("textDocument/publishDiagnostics");
            Assert(diag != null, "LSP-T07: diagnostics received for valid script");
            if (diag != null)
            {
                var parameters = diag.GetObject("params");
                var diagList = parameters?.GetArray("diagnostics");
                Assert(diagList != null && diagList.Count == 0, "LSP-T07: diagnostics list is empty for valid script");
            }
        }

        // ===== Test LSP-T08: ErrorToDiagnostic parses "(line N)" format =====
        {
            var diag = LspServer.ErrorToDiagnostic("Undefined variable 'x' (line 5)", "");
            var range = diag.GetObject("range");
            var start = range?.GetObject("start");
            Assert(start != null, "LSP-T08: range.start present");
            if (start != null)
            {
                Assert(start.GetInt("line") == 4, $"LSP-T08: line = 4 (0-based), got {start.GetInt("line")}");
            }
            string msg = diag.GetString("message");
            Assert(msg == "Undefined variable 'x'", $"LSP-T08: message stripped of (line N), got '{msg}'");
        }

        // ===== Test LSP-T09: ErrorToDiagnostic parses "at L:C" format =====
        {
            var diag = LspServer.ErrorToDiagnostic("Unexpected token 'x' at 3:7", "");
            var range = diag.GetObject("range");
            var start = range?.GetObject("start");
            Assert(start != null, "LSP-T09: range.start present");
            if (start != null)
            {
                Assert(start.GetInt("line") == 2, $"LSP-T09: line = 2 (0-based), got {start.GetInt("line")}");
                Assert(start.GetInt("character") == 6, $"LSP-T09: col = 6 (0-based), got {start.GetInt("character")}");
            }
        }

        // ===== Test LSP-T10: ErrorToDiagnostic handles no line info =====
        {
            var diag = LspServer.ErrorToDiagnostic("Some generic error", "");
            var range = diag.GetObject("range");
            var start = range?.GetObject("start");
            Assert(start != null, "LSP-T10: range.start present");
            if (start != null)
            {
                Assert(start.GetInt("line") == 0, "LSP-T10: fallback line = 0");
                Assert(start.GetInt("character") == 0, "LSP-T10: fallback character = 0");
            }
        }

        // ===== Test LSP-T11: unknown method returns MethodNotFound error =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddRequest("unknownMethod", null);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            // Skip initialize response
            session.ExpectResponse(0);
            // Next should be error response for unknownMethod
            var errResp = session.ExpectResponse(1);
            Assert(errResp != null, "LSP-T11: response for unknown method");
            if (errResp != null)
            {
                var error = errResp.GetObject("error");
                Assert(error != null, "LSP-T11: error object present");
                if (error != null)
                {
                    Assert(error.GetInt("code") == -32601, $"LSP-T11: error code = -32601, got {error.GetInt("code")}");
                }
            }
        }

        // ===== Test LSP-T12: multiple files get independent diagnostics =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///a.vm", "func entry() { wait 1 }");
            session.AddDidOpen("file:///b.vm", "func entry() { invalid syntax }");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
            Assert(diags.Count >= 2, $"LSP-T12: got {diags.Count} notifications, expected ≥ 2");

            // Find diagnostics for each file
            JsonObject diagA = null, diagB = null;
            foreach (var d in diags)
            {
                var p = d.GetObject("params");
                if (p?.GetString("uri") == "file:///a.vm") diagA = d;
                if (p?.GetString("uri") == "file:///b.vm") diagB = d;
            }

            Assert(diagA != null, "LSP-T12: diagnostics for a.vm");
            Assert(diagB != null, "LSP-T12: diagnostics for b.vm");

            if (diagA != null)
            {
                var aList = diagA.GetObject("params")?.GetArray("diagnostics");
                Assert(aList != null && aList.Count == 0, "LSP-T12: a.vm has no errors");
            }
            if (diagB != null)
            {
                var bList = diagB.GetObject("params")?.GetArray("diagnostics");
                Assert(bList != null && bList.Count > 0, "LSP-T12: b.vm has errors");
            }
        }

        Debug.Log($"\n===== LspTests: {passed} passed, {failed} failed =====");
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Batch-mode LSP test session. All messages are added upfront, server runs once,
    /// then outputs are consumed in order. Mirrors DapBatchSession pattern.
    /// </summary>
    private class LspBatchSession
    {
        private readonly MemoryStream _inputMs = new MemoryStream();
        private int _nextId;
        private List<JsonObject> _messages;
        private int _readIndex;
        public bool ServerStopped { get; private set; }

        public void AddRequest(string method, JsonObject parameters)
        {
            var req = new JsonObject();
            req.Set("jsonrpc", "2.0");
            req.Set("id", _nextId++);
            req.Set("method", method);
            if (parameters != null)
                req.Set("params", parameters);
            ContentLengthStream.WriteMessage(_inputMs, req.ToJson());
        }

        public void AddNotification(string method, JsonObject parameters)
        {
            var notif = new JsonObject();
            notif.Set("jsonrpc", "2.0");
            notif.Set("method", method);
            if (parameters != null)
                notif.Set("params", parameters);
            ContentLengthStream.WriteMessage(_inputMs, notif.ToJson());
        }

        public void AddInitialize()
        {
            var parameters = new JsonObject();
            var caps = new JsonObject();
            parameters.Set("capabilities", caps);
            AddRequest("initialize", parameters);
        }

        public void AddInitialized()
        {
            AddNotification("initialized", null);
        }

        public void AddShutdown()
        {
            AddRequest("shutdown", null);
        }

        public void AddExit()
        {
            AddNotification("exit", null);
        }

        public void AddDidOpen(string uri, string text)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            textDoc.Set("languageId", "ffvm");
            textDoc.Set("version", 1);
            textDoc.Set("text", text);
            parameters.Set("textDocument", textDoc);
            AddNotification("textDocument/didOpen", parameters);
        }

        public void AddDidChange(string uri, string newText)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            textDoc.Set("version", 2);
            parameters.Set("textDocument", textDoc);

            var change = new JsonObject();
            change.Set("text", newText);
            var changes = new List<object> { change };
            parameters.Set("contentChanges", changes);

            AddNotification("textDocument/didChange", parameters);
        }

        public void Run()
        {
            _inputMs.Position = 0;
            var outputMs = new MemoryStream();
            var server = new LspServer(_inputMs, outputMs);
            server.Run();
            ServerStopped = true;

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

        public JsonObject ReadNext()
        {
            if (_messages != null && _readIndex < _messages.Count)
                return _messages[_readIndex++];
            return null;
        }

        public JsonObject ExpectResponse(int id)
        {
            if (_messages == null) return null;
            for (int i = _readIndex; i < _messages.Count; i++)
            {
                var msg = _messages[i];
                if (msg.ContainsKey("id") && msg.GetInt("id") == id && !msg.ContainsKey("method"))
                {
                    _readIndex = i + 1;
                    return msg;
                }
            }
            return null;
        }

        public JsonObject FindNotification(string method)
        {
            if (_messages == null) return null;
            foreach (var msg in _messages)
            {
                if (msg.GetString("method") == method)
                    return msg;
            }
            return null;
        }

        public List<JsonObject> FindAllNotifications(string method)
        {
            var result = new List<JsonObject>();
            if (_messages == null) return result;
            foreach (var msg in _messages)
            {
                if (msg.GetString("method") == method)
                    result.Add(msg);
            }
            return result;
        }
    }
}
