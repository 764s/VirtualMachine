using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FFVM;
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
            session.AddDidOpen("file:///test.ffs", "func entry() { wait 1 }");
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
            session.AddDidOpen("file:///test.ffs", "func entry() { wait 1 }");
            session.AddDidChange("file:///test.ffs", "func entry() { wait 2 }");
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
            session.AddDidOpen("file:///err.ffs", "func entry() { var x: int = ");
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
                    Assert(uri == "file:///err.ffs", $"LSP-T05: uri = file:///err.ffs, got {uri}");
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
            session.AddDidOpen("file:///fix.ffs", "func entry() { var x: int = }");
            // Then fix it
            session.AddDidChange("file:///fix.ffs", "func entry() {\n    var x: int = 42\n}");
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
            session.AddDidOpen("file:///ok.ffs", "func entry() {\n    var x: int = 42\n    wait 1\n}");
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
            session.AddDidOpen("file:///a.ffs", "func entry() { wait 1 }");
            session.AddDidOpen("file:///b.ffs", "func entry() { invalid syntax }");
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
                if (p?.GetString("uri") == "file:///a.ffs") diagA = d;
                if (p?.GetString("uri") == "file:///b.ffs") diagB = d;
            }

            Assert(diagA != null, "LSP-T12: diagnostics for a.ffs");
            Assert(diagB != null, "LSP-T12: diagnostics for b.ffs");

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
            session.AddDidOpen("file:///sym.ffs", source);
            session.AddDocumentSymbol("file:///sym.ffs");
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
            session.AddDidOpen("file:///empty.ffs", "");
            session.AddDocumentSymbol("file:///empty.ffs");
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
            session.AddDidOpen("file:///hover.ffs", source);
            // Hover on "add" — line 0 (0-based), character 5 (0-based: "func " = 5 chars, 'a' of 'add' is at col 5)
            session.AddHover("file:///hover.ffs", 0, 5);
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
            session.AddDidOpen("file:///hvar.ffs", source);
            // Hover on "x" in "wait x" — line 2 (0-based), character 7 (0-based: "  wait " = 7 chars)
            session.AddHover("file:///hvar.ffs", 2, 7);
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
            session.AddDidOpen("file:///hnull.ffs", source);
            // Hover on line 1, char 0 (whitespace before "wait")
            session.AddHover("file:///hnull.ffs", 1, 0);
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
            session.AddDidOpen("file:///def.ffs", source);
            // Go to definition on "helper()" call in entry — line 4 (0-based), character 16 (0-based)
            // "  var x: int = helper()" → "helper" starts at col 15
            session.AddDefinition("file:///def.ffs", 4, 16);
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
            session.AddDidOpen("file:///defvar.ffs", source);
            // Go to definition on "counter" in "wait counter" — line 2 (0-based)
            // "  wait counter" → "counter" starts at col 7
            session.AddDefinition("file:///defvar.ffs", 2, 7);
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
            session.AddDidOpen("file:///defnull.ffs", source);
            // Position on "1" literal — no definition
            session.AddDefinition("file:///defnull.ffs", 1, 7);
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
            session.AddDidOpen("file:///ref.ffs", source);
            // References on "helper" function name in declaration — line 0, col 5
            session.AddReferences("file:///ref.ffs", 0, 5);
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
            session.AddDidOpen("file:///refvar.ffs", source);
            // References on "x" in "wait x" — line 3, col 7
            session.AddReferences("file:///refvar.ffs", 3, 7);
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
            session.AddDidOpen("file:///comp.ffs", source);
            // Complete at empty line 2, col 2 (inside entry function)
            session.AddCompletion("file:///comp.ffs", 2, 2);
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
            session.AddDidOpen("file:///dot.ffs", source);
            // Complete after "v." on line 6, col 4
            session.AddCompletion("file:///dot.ffs", 6, 4);
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
            session.AddDidOpen("file:///empty.ffs", "");
            session.AddCompletion("file:///empty.ffs", 0, 0);
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
                    // Should have keywords (16) only — includes 'include' keyword
                    Assert(items.Count == 17, $"LSP5-T04: 17 keyword items for empty file, got {items.Count}");
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
            session.AddDidOpen("file:///sys.ffs", source);
            session.AddCompletion("file:///sys.ffs", 1, 2);
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
            session.AddDidOpen("file:///scope.ffs", source);
            // Complete inside entry function at line 6 (the empty line)
            session.AddCompletion("file:///scope.ffs", 6, 2);
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
            session.AddDidOpen("file:///detail.ffs", source);
            session.AddCompletion("file:///detail.ffs", 4, 2);
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
            session.AddDidOpen("file:///params.ffs", source);
            session.AddCompletion("file:///params.ffs", 1, 2);
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

        // ================================================================
        // F. LSP6 Syscall Declaration Protocol Tests
        // ================================================================

        // ===== Test LSP6-T01: syscall with signature appears in completion =====
        {
            string source = "func entry() {\n  \n}";
            var session = new LspBatchSession();
            var syscalls = new Dictionary<string, int> { { "PlayAnim", 0 }, { "GetHP", 1 } };
            var signatures = new Dictionary<string, SyscallSignature>
            {
                { "PlayAnim", new SyscallSignature(
                    new[] { new SyscallParamInfo("animId", "int"), new SyscallParamInfo("speed", "float") },
                    "void", "Play animation by ID at given speed") },
                { "GetHP", new SyscallSignature(
                    new SyscallParamInfo[0], "int", "Get current hit points") }
            };
            session.SetSyscalls(syscalls, signatures);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///lsp6.ffs", source);
            session.AddCompletion("file:///lsp6.ffs", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP6-T01: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP6-T01: result is array");
                if (items != null)
                {
                    // Find PlayAnim and verify signature in detail
                    string playAnimDetail = null;
                    string getHPDetail = null;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        if (label == "PlayAnim") playAnimDetail = item?.GetString("detail");
                        if (label == "GetHP") getHPDetail = item?.GetString("detail");
                    }
                    Assert(playAnimDetail != null, "LSP6-T01: PlayAnim found");
                    Assert(playAnimDetail != null && playAnimDetail.Contains("animId: int"),
                        $"LSP6-T01: PlayAnim detail contains param info, got '{playAnimDetail}'");
                    Assert(playAnimDetail != null && playAnimDetail.Contains("speed: float"),
                        $"LSP6-T01: PlayAnim detail contains second param, got '{playAnimDetail}'");
                    Assert(getHPDetail != null, "LSP6-T01: GetHP found");
                    Assert(getHPDetail != null && getHPDetail.Contains("int"),
                        $"LSP6-T01: GetHP detail contains return type, got '{getHPDetail}'");
                }
            }
        }

        // ===== Test LSP6-T02: .ffvm.d.json loading populates syscall metadata =====
        {
            string source = "func entry() {\n  \n}";
            string declJson = @"{
  ""syscalls"": [
    { ""name"": ""Attack"", ""slot"": 0,
      ""parameters"": [{ ""name"": ""target"", ""type"": ""int"" }, { ""name"": ""damage"", ""type"": ""float"" }],
      ""returnType"": ""void"", ""description"": ""Attack a target"" },
    { ""name"": ""Heal"", ""slot"": 1,
      ""parameters"": [{ ""name"": ""amount"", ""type"": ""int"" }],
      ""returnType"": ""int"", ""description"": ""Heal and return new HP"" }
  ]
}";
            var session = new LspBatchSession();
            session.SetDeclarationJson(declJson);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///lsp6decl.ffs", source);
            session.AddCompletion("file:///lsp6decl.ffs", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP6-T02: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP6-T02: result is array");
                if (items != null)
                {
                    string attackDetail = null;
                    string healDetail = null;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        if (label == "Attack") attackDetail = item?.GetString("detail");
                        if (label == "Heal") healDetail = item?.GetString("detail");
                    }
                    Assert(attackDetail != null, "LSP6-T02: Attack found from declaration");
                    Assert(attackDetail != null && attackDetail.Contains("target: int"),
                        $"LSP6-T02: Attack detail has params, got '{attackDetail}'");
                    Assert(healDetail != null, "LSP6-T02: Heal found from declaration");
                    Assert(healDetail != null && healDetail.Contains("amount: int"),
                        $"LSP6-T02: Heal detail has params, got '{healDetail}'");
                    Assert(healDetail != null && healDetail.Contains(": int"),
                        $"LSP6-T02: Heal detail contains return type, got '{healDetail}'");
                }
            }
        }

        // ===== Test LSP6-T03: multi-param syscall detail format is correct =====
        {
            string source = "func entry() {\n  \n}";
            var session = new LspBatchSession();
            var syscalls = new Dictionary<string, int> { { "MoveTo", 0 } };
            var signatures = new Dictionary<string, SyscallSignature>
            {
                { "MoveTo", new SyscallSignature(
                    new[] {
                        new SyscallParamInfo("x", "float"),
                        new SyscallParamInfo("y", "float"),
                        new SyscallParamInfo("z", "float")
                    },
                    "void", null) }
            };
            session.SetSyscalls(syscalls, signatures);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///lsp6fmt.ffs", source);
            session.AddCompletion("file:///lsp6fmt.ffs", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP6-T03: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP6-T03: result is array");
                if (items != null)
                {
                    string moveToDetail = null;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        if (item?.GetString("label") == "MoveTo")
                        {
                            moveToDetail = item?.GetString("detail");
                            break;
                        }
                    }
                    Assert(moveToDetail == "(syscall) MoveTo(x: float, y: float, z: float)",
                        $"LSP6-T03: exact detail format, got '{moveToDetail}'");
                }
            }
        }

        // ===== Test LSP6-T04: syscall without signature shows backward-compatible detail =====
        {
            string source = "func entry() {\n  \n}";
            var session = new LspBatchSession();
            // Register syscall without signature
            var syscalls = new Dictionary<string, int> { { "LegacyCall", 0 } };
            session.SetSyscalls(syscalls, null);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///lsp6compat.ffs", source);
            session.AddCompletion("file:///lsp6compat.ffs", 1, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var compResp = session.ExpectResponse(1);
            Assert(compResp != null, "LSP6-T04: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP6-T04: result is array");
                if (items != null)
                {
                    string legacyDetail = null;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        if (item?.GetString("label") == "LegacyCall")
                        {
                            legacyDetail = item?.GetString("detail");
                            break;
                        }
                    }
                    Assert(legacyDetail == "(syscall) LegacyCall",
                        $"LSP6-T04: legacy detail format preserved, got '{legacyDetail}'");
                }
            }
        }

        // ================================================================
        // F. LSP7 Tests — signatureHelp (parameter hints)
        // ================================================================

        // ===== Test LSP7-T01: signatureHelp for user function on '(' =====
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}\nfunc entry() {\n  add(\n}";
            // cursor at line 4 (0-based), char 6: right after "add("
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig1.ffs", source);
            session.AddSignatureHelp("file:///sig1.ffs", 4, 6);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var sigResp = session.ExpectResponse(1); // signatureHelp
            Assert(sigResp != null, "LSP7-T01: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T01: result is not null");
                if (result != null)
                {
                    var sigs = result.GetArray("signatures");
                    Assert(sigs != null && sigs.Count == 1, $"LSP7-T01: one signature, got {sigs?.Count}");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        string label = sig?.GetString("label");
                        Assert(label == "func add(a: int, b: int): int",
                            $"LSP7-T01: label matches, got '{label}'");
                        var parms = sig?.GetArray("parameters");
                        Assert(parms != null && parms.Count == 2,
                            $"LSP7-T01: 2 parameters, got {parms?.Count}");
                    }
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 0, $"LSP7-T01: activeParameter=0, got {ap}");
                }
            }
        }

        // ===== Test LSP7-T02: activeParameter increments on ',' =====
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}\nfunc entry() {\n  add(1, \n}";
            // cursor at line 4, char 9: right after "add(1, "
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig2.ffs", source);
            session.AddSignatureHelp("file:///sig2.ffs", 4, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T02: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T02: result is not null");
                if (result != null)
                {
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 1, $"LSP7-T02: activeParameter=1, got {ap}");
                }
            }
        }

        // ===== Test LSP7-T03: syscall signatureHelp =====
        {
            string source = "func entry() {\n  PlayAnim(\n}";
            var session = new LspBatchSession();
            var syscalls = new Dictionary<string, int> { { "PlayAnim", 0 } };
            var signatures = new Dictionary<string, SyscallSignature>
            {
                { "PlayAnim", new SyscallSignature(
                    new[] { new SyscallParamInfo("animId", "int"), new SyscallParamInfo("speed", "float") },
                    "void", "Play animation") }
            };
            session.SetSyscalls(syscalls, signatures);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig3.ffs", source);
            session.AddSignatureHelp("file:///sig3.ffs", 1, 12);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T03: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T03: result is not null");
                if (result != null)
                {
                    var sigs = result.GetArray("signatures");
                    Assert(sigs != null && sigs.Count == 1, $"LSP7-T03: one signature");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        string label = sig?.GetString("label");
                        Assert(label != null && label.Contains("PlayAnim"),
                            $"LSP7-T03: label contains PlayAnim, got '{label}'");
                        var parms = sig?.GetArray("parameters");
                        Assert(parms != null && parms.Count == 2,
                            $"LSP7-T03: 2 parameters, got {parms?.Count}");
                        if (parms != null && parms.Count > 0)
                        {
                            var p0 = parms[0] as JsonObject;
                            Assert(p0?.GetString("label") == "animId: int",
                                $"LSP7-T03: first param label, got '{p0?.GetString("label")}'");
                        }
                    }
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 0, $"LSP7-T03: activeParameter=0, got {ap}");
                }
            }
        }

        // ===== Test LSP7-T04: unknown function returns null =====
        {
            string source = "func entry() {\n  unknown(\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig4.ffs", source);
            session.AddSignatureHelp("file:///sig4.ffs", 1, 10);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T04: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result == null, $"LSP7-T04: result is null for unknown function");
            }
        }

        // ===== Test LSP7-T05: nested parentheses in arguments =====
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}\nfunc mul(x: int, y: int): int {\n  return x\n}\nfunc entry() {\n  add(mul(1, 2), \n}";
            // cursor at line 7, after "add(mul(1, 2), " → activeParameter should be 1 (second param of add)
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig5.ffs", source);
            session.AddSignatureHelp("file:///sig5.ffs", 7, 17);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T05: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T05: result is not null");
                if (result != null)
                {
                    var sigs = result.GetArray("signatures");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        string label = sig?.GetString("label");
                        Assert(label != null && label.Contains("add"),
                            $"LSP7-T05: outer function is 'add', got '{label}'");
                    }
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 1, $"LSP7-T05: activeParameter=1 for second arg, got {ap}");
                }
            }
        }

        // ===== Test LSP7-T06: three-parameter function, third param active =====
        {
            string source = "func tri(a: int, b: int, c: int): int {\n  return a\n}\nfunc entry() {\n  tri(1, 2, \n}";
            // cursor at line 4, after "tri(1, 2, " → activeParameter = 2
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig6.ffs", source);
            session.AddSignatureHelp("file:///sig6.ffs", 4, 12);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T06: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T06: result is not null");
                if (result != null)
                {
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 2, $"LSP7-T06: activeParameter=2 for third arg, got {ap}");
                    var sigs = result.GetArray("signatures");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        var parms = sig?.GetArray("parameters");
                        Assert(parms != null && parms.Count == 3,
                            $"LSP7-T06: 3 parameters, got {parms?.Count}");
                    }
                }
            }
        }

        // ===== Test LSP7-T07: signatureHelp capability advertised =====
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            Assert(initResp != null, "LSP7-T07: initialize response received");
            if (initResp != null)
            {
                var caps = initResp.GetObject("result")?.GetObject("capabilities");
                var sigProv = caps?.GetObject("signatureHelpProvider");
                Assert(sigProv != null, "LSP7-T07: signatureHelpProvider capability present");
                if (sigProv != null)
                {
                    var trigChars = sigProv.GetArray("triggerCharacters");
                    Assert(trigChars != null && trigChars.Count >= 2,
                        $"LSP7-T07: trigger characters present, got {trigChars?.Count}");
                }
            }
        }

        // ===== Test LSP7-T08: syscall second param active =====
        {
            string source = "func entry() {\n  PlayAnim(1, \n}";
            var session = new LspBatchSession();
            var syscalls = new Dictionary<string, int> { { "PlayAnim", 0 } };
            var signatures = new Dictionary<string, SyscallSignature>
            {
                { "PlayAnim", new SyscallSignature(
                    new[] { new SyscallParamInfo("animId", "int"), new SyscallParamInfo("speed", "float") },
                    "void", "Play animation") }
            };
            session.SetSyscalls(syscalls, signatures);
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sig8.ffs", source);
            session.AddSignatureHelp("file:///sig8.ffs", 1, 15);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "LSP7-T08: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "LSP7-T08: result is not null");
                if (result != null)
                {
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 1, $"LSP7-T08: activeParameter=1, got {ap}");
                }
            }
        }

        // ===== Test FF3-LSP-01: signatureHelp shows default values =====
        {
            string source = "func greet(a: int, b: int = 10, c: int = 20): int {\n  return a + b + c\n}\nfunc entry() {\n  greet(1, \n}";
            // cursor at line 4 after "greet(1, " → activeParameter = 1 (b)
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ff3sig.ffs", source);
            session.AddSignatureHelp("file:///ff3sig.ffs", 4, 11);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var sigResp = session.ExpectResponse(1);
            Assert(sigResp != null, "FF3-LSP-01: signatureHelp response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "FF3-LSP-01: result is not null");
                if (result != null)
                {
                    var sigs = result.GetArray("signatures");
                    Assert(sigs != null && sigs.Count > 0, "FF3-LSP-01: has signatures");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        string label = sig?.GetString("label");
                        Assert(label != null && label.Contains("b: int = 10") && label.Contains("c: int = 20"),
                            $"FF3-LSP-01: label shows defaults, got '{label}'");
                        var parms = sig?.GetArray("parameters");
                        Assert(parms != null && parms.Count == 3, $"FF3-LSP-01: 3 params, got {parms?.Count}");
                        if (parms != null && parms.Count >= 2)
                        {
                            var p1 = parms[1] as JsonObject;
                            string p1Label = p1?.GetString("label");
                            Assert(p1Label != null && p1Label.Contains("= 10"),
                                $"FF3-LSP-01: param 'b' label shows '= 10', got '{p1Label}'");
                        }
                    }
                    int ap = result.GetInt("activeParameter");
                    Assert(ap == 1, $"FF3-LSP-01: activeParameter=1, got {ap}");
                }
            }
        }

        // ===== Test FF3-LSP-02: hover on optional parameter shows default =====
        {
            string source = "func foo(x: int, y: int = 42): int {\n  return x + y\n}";
            // hover on 'y' at line 1, col 17..17
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ff3hover.ffs", source);
            session.AddHover("file:///ff3hover.ffs", 0, 17);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            Assert(hoverResp != null, "FF3-LSP-02: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value");
                    Assert(value != null && value.Contains("= 42"),
                        $"FF3-LSP-02: hover shows default '= 42', got '{value}'");
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

        // LSP6: optional syscall declarations
        private Dictionary<string, int> _syscalls;
        private Dictionary<string, SyscallSignature> _signatures;
        private string _declarationJson;

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

        public void AddSignatureHelp(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            AddRequest("textDocument/signatureHelp", parameters);
        }

        /// <summary>
        /// Register syscall declarations to be passed to the LspServer (LSP6).
        /// </summary>
        public void SetSyscalls(Dictionary<string, int> syscalls, Dictionary<string, SyscallSignature> signatures)
        {
            _syscalls = syscalls;
            _signatures = signatures;
        }

        /// <summary>
        /// Set a .ffvm.d.json string to be loaded by the server on startup (LSP6).
        /// </summary>
        public void SetDeclarationJson(string json)
        {
            _declarationJson = json;
        }

        public void Run()
        {
            _inputMs.Position = 0;
            var outputMs = new MemoryStream();
            LspServer server;
            if (_syscalls != null || _signatures != null)
                server = new LspServer(_inputMs, outputMs, _syscalls, _signatures);
            else
                server = new LspServer(_inputMs, outputMs);

            if (_declarationJson != null)
                server.LoadDeclarationJson(_declarationJson);

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
