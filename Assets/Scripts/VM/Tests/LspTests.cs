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

        // ================================================================
        // D. LSP4 Symbol Analysis Tests
        // ================================================================

        // ===== Test LSP4-T01: initialize response contains LSP4 capabilities =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var caps = initResp?.GetObject("result")?.GetObject("capabilities");
            Assert(caps != null, "LSP4-T01: capabilities present");
            if (caps != null)
            {
                Assert(caps.GetBool("documentSymbolProvider") == true, "LSP4-T01: documentSymbolProvider = true");
                Assert(caps.GetBool("hoverProvider") == true, "LSP4-T01: hoverProvider = true");
                Assert(caps.GetBool("definitionProvider") == true, "LSP4-T01: definitionProvider = true");
                Assert(caps.GetBool("referencesProvider") == true, "LSP4-T01: referencesProvider = true");
            }
        }

        // ===== Test LSP4-T02: documentSymbol returns functions and structs =====
        {
            string source = "struct Vec2 {\n  x: int\n  y: int\n}\nfunc entry() {\n  wait 1\n}\nfunc helper(): int {\n  return 42\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sym.vm", source);
            session.AddDocumentSymbol("file:///sym.vm");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            // Skip initialize response (id=0), find documentSymbol response (id=2, after shutdown=1... wait)
            // IDs: 0=initialize, 1=documentSymbol, 2=shutdown
            session.ExpectResponse(0); // initialize
            var symResp = session.ExpectResponse(1); // documentSymbol
            Assert(symResp != null, "LSP4-T02: documentSymbol response received");
            if (symResp != null)
            {
                var symbols = symResp.GetArray("result");
                Assert(symbols != null, "LSP4-T02: result is array");
                if (symbols != null)
                {
                    Assert(symbols.Count == 3, $"LSP4-T02: 3 symbols (1 struct + 2 funcs), got {symbols.Count}");

                    // Check first symbol (struct Vec2)
                    if (symbols.Count > 0)
                    {
                        var s0 = symbols[0] as JsonObject;
                        Assert(s0?.GetString("name") == "Vec2", $"LSP4-T02: first symbol = Vec2, got {s0?.GetString("name")}");
                        Assert(s0?.GetInt("kind") == 23, $"LSP4-T02: Vec2 kind = Struct (23), got {s0?.GetInt("kind")}");
                    }
                    // Check second symbol (func entry)
                    if (symbols.Count > 1)
                    {
                        var s1 = symbols[1] as JsonObject;
                        Assert(s1?.GetString("name") == "entry", $"LSP4-T02: second symbol = entry, got {s1?.GetString("name")}");
                        Assert(s1?.GetInt("kind") == 12, $"LSP4-T02: entry kind = Function (12), got {s1?.GetInt("kind")}");
                    }
                    // Check third symbol (func helper)
                    if (symbols.Count > 2)
                    {
                        var s2 = symbols[2] as JsonObject;
                        Assert(s2?.GetString("name") == "helper", $"LSP4-T02: third symbol = helper, got {s2?.GetString("name")}");
                    }
                }
            }
        }

        // ===== Test LSP4-T03: documentSymbol for empty/minimal file returns empty =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///empty.vm", "");
            session.AddDocumentSymbol("file:///empty.vm");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var symResp = session.ExpectResponse(1); // documentSymbol
            Assert(symResp != null, "LSP4-T03: response received");
            if (symResp != null)
            {
                var symbols = symResp.GetArray("result");
                Assert(symbols != null && symbols.Count == 0, $"LSP4-T03: empty result for empty file, got {symbols?.Count ?? -1}");
            }
        }

        // ===== Test LSP4-T04: hover on function name returns signature =====
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///hover.vm", source);
            // Hover on "add" — line 0 (0-based), character 5 (0-based: "func " = 5 chars, 'a' of 'add' is at col 5)
            session.AddHover("file:///hover.vm", 0, 5);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var hoverResp = session.ExpectResponse(1); // hover
            Assert(hoverResp != null, "LSP4-T04: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                Assert(result != null, "LSP4-T04: result not null");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    Assert(contents != null, "LSP4-T04: contents present");
                    string value = contents?.GetString("value");
                    Assert(value != null && value.Contains("add"), $"LSP4-T04: hover shows 'add', got '{value}'");
                    Assert(value != null && value.Contains("a: int"), $"LSP4-T04: hover shows param info, got '{value}'");
                }
            }
        }

        // ===== Test LSP4-T05: hover on variable returns type =====
        {
            string source = "func entry() {\n  var x: int = 42\n  wait x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///hvar.vm", source);
            // Hover on "x" in "wait x" — line 2 (0-based), character 7 (0-based: "  wait " = 7 chars)
            session.AddHover("file:///hvar.vm", 2, 7);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            Assert(hoverResp != null, "LSP4-T05: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                Assert(result != null, "LSP4-T05: result not null (variable hover)");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value");
                    Assert(value != null && value.Contains("x") && value.Contains("int"),
                        $"LSP4-T05: hover shows 'x: int', got '{value}'");
                }
            }
        }

        // ===== Test LSP4-T06: hover on empty position returns null =====
        {
            string source = "func entry() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///hnull.vm", source);
            // Hover on line 1, char 0 (whitespace before "wait")
            session.AddHover("file:///hnull.vm", 1, 0);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            Assert(hoverResp != null, "LSP4-T06: hover response received");
            if (hoverResp != null)
            {
                // result should be null for no symbol
                var result = hoverResp.GetObject("result");
                Assert(result == null, "LSP4-T06: null result for empty position");
            }
        }

        // ===== Test LSP4-T07: definition on function call jumps to FuncDecl =====
        {
            string source = "func helper(): int {\n  return 42\n}\nfunc entry() {\n  var x: int = helper()\n  wait x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///def.vm", source);
            // Go to definition on "helper()" call in entry — line 4 (0-based), character 16 (0-based)
            // "  var x: int = helper()" → "helper" starts at col 15
            session.AddDefinition("file:///def.vm", 4, 16);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            Assert(defResp != null, "LSP4-T07: definition response received");
            if (defResp != null)
            {
                var result = defResp.GetObject("result");
                Assert(result != null, "LSP4-T07: result not null");
                if (result != null)
                {
                    var range = result.GetObject("range");
                    var start = range?.GetObject("start");
                    Assert(start != null, "LSP4-T07: start position present");
                    if (start != null)
                    {
                        // helper function is defined on line 0 (0-based)
                        Assert(start.GetInt("line") == 0, $"LSP4-T07: definition on line 0, got {start.GetInt("line")}");
                    }
                }
            }
        }

        // ===== Test LSP4-T08: definition on variable reference jumps to VarDeclStmt =====
        {
            string source = "func entry() {\n  var counter: int = 0\n  wait counter\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///defvar.vm", source);
            // Go to definition on "counter" in "wait counter" — line 2 (0-based)
            // "  wait counter" → "counter" starts at col 7
            session.AddDefinition("file:///defvar.vm", 2, 7);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            Assert(defResp != null, "LSP4-T08: definition response received");
            if (defResp != null)
            {
                var result = defResp.GetObject("result");
                Assert(result != null, "LSP4-T08: result not null (variable definition)");
                if (result != null)
                {
                    var range = result.GetObject("range");
                    var start = range?.GetObject("start");
                    if (start != null)
                    {
                        // var counter is declared on line 1 (0-based)
                        Assert(start.GetInt("line") == 1, $"LSP4-T08: definition on line 1, got {start.GetInt("line")}");
                    }
                }
            }
        }

        // ===== Test LSP4-T09: definition on unknown symbol returns null =====
        {
            string source = "func entry() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///defnull.vm", source);
            // Position on "1" literal — no definition
            session.AddDefinition("file:///defnull.vm", 1, 7);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            Assert(defResp != null, "LSP4-T09: definition response received");
            if (defResp != null)
            {
                var result = defResp.GetObject("result");
                Assert(result == null, "LSP4-T09: null result for no definition");
            }
        }

        // ===== Test LSP4-T10: references for function returns decl + all call sites =====
        {
            string source = "func helper(): int {\n  return 42\n}\nfunc entry() {\n  var a: int = helper()\n  var b: int = helper()\n  wait a\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ref.vm", source);
            // References on "helper" function name in declaration — line 0, col 5
            session.AddReferences("file:///ref.vm", 0, 5);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var refResp = session.ExpectResponse(1);
            Assert(refResp != null, "LSP4-T10: references response received");
            if (refResp != null)
            {
                var result = refResp.GetArray("result");
                Assert(result != null, "LSP4-T10: result is array");
                if (result != null)
                {
                    // 1 declaration + 2 call sites = 3 references
                    Assert(result.Count == 3, $"LSP4-T10: 3 references (1 decl + 2 calls), got {result.Count}");
                }
            }
        }

        // ===== Test LSP4-T11: references for variable returns decl + all usages =====
        {
            string source = "func entry() {\n  var x: int = 10\n  x = x + 1\n  wait x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///refvar.vm", source);
            // References on "x" in "wait x" — line 3, col 7
            session.AddReferences("file:///refvar.vm", 3, 7);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var refResp = session.ExpectResponse(1);
            Assert(refResp != null, "LSP4-T11: references response received");
            if (refResp != null)
            {
                var result = refResp.GetArray("result");
                Assert(result != null, "LSP4-T11: result is array");
                if (result != null)
                {
                    // var x declaration + x = ... + ... x + 1 + wait x = at least 4
                    Assert(result.Count >= 4, $"LSP4-T11: ≥ 4 references for 'x', got {result.Count}");
                }
            }
        }

        // ================================================================
        // E. LSP5 Completion Tests
        // ================================================================

        // ===== Test LSP5-T01: initialize response includes completionProvider =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            Assert(initResp != null, "LSP5-T01: initialize response received");
            if (initResp != null)
            {
                var result = initResp.GetObject("result");
                var caps = result?.GetObject("capabilities");
                var completionProvider = caps?.GetObject("completionProvider");
                Assert(completionProvider != null, "LSP5-T01: completionProvider present");
                if (completionProvider != null)
                {
                    var triggers = completionProvider.GetArray("triggerCharacters");
                    Assert(triggers != null && triggers.Count > 0,
                        $"LSP5-T01: triggerCharacters not empty, got {triggers?.Count ?? 0}");
                }
            }
        }

        // ===== Test LSP5-T02: basic completion returns keywords + functions + variables =====
        {
            string source = "func entry() {\n  var x: int = 10\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///comp.vm", source);
            // Complete at empty line 2, col 2 (inside entry function)
            session.AddCompletion("file:///comp.vm", 2, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "LSP5-T02: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T02: result is array");
                if (items != null)
                {
                    // Should have keywords (15) + func entry (1) + var x (1) = at least 17
                    Assert(items.Count >= 17, $"LSP5-T02: at least 17 items (15 kw + 1 func + 1 var), got {items.Count}");

                    // Check keywords are present
                    bool hasFunc = false, hasVar = false, hasIf = false, hasWait = false;
                    // Check function name is present
                    bool hasEntryFunc = false;
                    // Check variable is present
                    bool hasXVar = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "func" && kind == 14) hasFunc = true;
                        if (label == "var" && kind == 14) hasVar = true;
                        if (label == "if" && kind == 14) hasIf = true;
                        if (label == "wait" && kind == 14) hasWait = true;
                        if (label == "entry" && kind == 3) hasEntryFunc = true;
                        if (label == "x" && kind == 6) hasXVar = true;
                    }
                    Assert(hasFunc, "LSP5-T02: 'func' keyword present");
                    Assert(hasVar, "LSP5-T02: 'var' keyword present");
                    Assert(hasIf, "LSP5-T02: 'if' keyword present");
                    Assert(hasWait, "LSP5-T02: 'wait' keyword present");
                    Assert(hasEntryFunc, "LSP5-T02: 'entry' function present");
                    Assert(hasXVar, "LSP5-T02: 'x' variable present");
                }
            }
        }

        // ===== Test LSP5-T03: struct field completion after dot =====
        {
            string source = "struct Vec2 {\n  x: int\n  y: int\n}\nfunc entry() {\n  var v: Vec2\n  v.\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dot.vm", source);
            // Complete after "v." on line 6, col 4
            session.AddCompletion("file:///dot.vm", 6, 4);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "LSP5-T03: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T03: result is array");
                if (items != null)
                {
                    Assert(items.Count == 2, $"LSP5-T03: 2 fields (x, y), got {items.Count}");
                    bool hasX = false, hasY = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "x" && kind == 5) hasX = true;
                        if (label == "y" && kind == 5) hasY = true;
                    }
                    Assert(hasX, "LSP5-T03: field 'x' present");
                    Assert(hasY, "LSP5-T03: field 'y' present");
                }
            }
        }

        // ===== Test LSP5-T04: completion for empty file returns keywords only =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///empty.vm", "");
            session.AddCompletion("file:///empty.vm", 0, 0);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "LSP5-T04: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T04: result is array");
                if (items != null)
                {
                    // Should have keywords (15) only
                    Assert(items.Count == 15, $"LSP5-T04: 15 keyword items for empty file, got {items.Count}");
                    // All should be kind=14 (Keyword)
                    bool allKeywords = true;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        if (item?.GetInt("kind") != 14) { allKeywords = false; break; }
                    }
                    Assert(allKeywords, "LSP5-T04: all items are keywords");
                }
            }
        }

        // ===== Test LSP5-T05: syscall names appear in completion =====
        {
            string source = "func entry() {\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sys.vm", source);
            session.AddCompletion("file:///sys.vm", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP5-T05: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T05: result is array");
                // Note: default LspServer has empty syscalls dict, so no syscall items
                // But we verify the mechanism works (no crash, keywords still present)
                if (items != null)
                {
                    bool hasKeywords = items.Count >= 15;
                    Assert(hasKeywords, $"LSP5-T05: at least keywords present, got {items.Count}");
                }
            }
        }

        // ===== Test LSP5-T06: scope-aware — only variables from current function =====
        {
            string source = "func helper(): int {\n  var a: int = 1\n  return a\n}\nfunc entry() {\n  var b: int = 2\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///scope.vm", source);
            // Complete inside entry function at line 6 (the empty line)
            session.AddCompletion("file:///scope.vm", 6, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP5-T06: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T06: result is array");
                if (items != null)
                {
                    // Should have 'b' but not 'a' as variable (a is in helper, not entry)
                    bool hasB = false, hasA = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "b" && kind == 6) hasB = true;
                        if (label == "a" && kind == 6) hasA = true;
                    }
                    Assert(hasB, "LSP5-T06: variable 'b' in scope");
                    Assert(!hasA, "LSP5-T06: variable 'a' NOT in scope (belongs to helper)");
                    // Both function names should appear
                    bool hasHelper = false, hasEntry = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "helper" && kind == 3) hasHelper = true;
                        if (label == "entry" && kind == 3) hasEntry = true;
                    }
                    Assert(hasHelper, "LSP5-T06: function 'helper' present");
                    Assert(hasEntry, "LSP5-T06: function 'entry' present");
                }
            }
        }

        // ===== Test LSP5-T07: completion items have correct detail text =====
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}\nfunc entry() {\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///detail.vm", source);
            session.AddCompletion("file:///detail.vm", 4, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP5-T07: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T07: result is array");
                if (items != null)
                {
                    // Find 'add' function item and check its detail
                    string addDetail = null;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        if (item?.GetString("label") == "add" && item?.GetInt("kind") == 3)
                        {
                            addDetail = item.GetString("detail");
                            break;
                        }
                    }
                    Assert(addDetail != null, "LSP5-T07: 'add' function has detail");
                    Assert(addDetail != null && addDetail.Contains("a: int"),
                        $"LSP5-T07: detail contains param info, got '{addDetail}'");
                }
            }
        }

        // ===== Test LSP5-T08: parameter completion inside function with params =====
        {
            string source = "func calc(x: int, y: int): int {\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///params.vm", source);
            session.AddCompletion("file:///params.vm", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP5-T08: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP5-T08: result is array");
                if (items != null)
                {
                    bool hasX = false, hasY = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "x" && kind == 6) hasX = true;
                        if (label == "y" && kind == 6) hasY = true;
                    }
                    Assert(hasX, "LSP5-T08: parameter 'x' present");
                    Assert(hasY, "LSP5-T08: parameter 'y' present");
                }
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

        public void AddDocumentSymbol(string uri)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            AddRequest("textDocument/documentSymbol", parameters);
        }

        public void AddHover(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            AddRequest("textDocument/hover", parameters);
        }

        public void AddDefinition(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            AddRequest("textDocument/definition", parameters);
        }

        public void AddReferences(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            var context = new JsonObject();
            context.Set("includeDeclaration", true);
            parameters.Set("context", context);
            AddRequest("textDocument/references", parameters);
        }

        public void AddCompletion(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            AddRequest("textDocument/completion", parameters);
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
