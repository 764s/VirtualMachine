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
                    // Should have keywords (22) only — includes 'include', 'public', 'private', 'override', 'external' keywords
                    Assert(items.Count == 22, $"LSP5-T04: 22 keyword items for empty file, got {items.Count}");
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

        // ===== Lang-13: LSP Enum Tests =====

        // LSP-EN01: documentSymbol includes enums
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///en01.ffs", source);
            session.AddDocumentSymbol("file:///en01.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var symResp = session.ExpectResponse(1); // documentSymbol
            Assert(symResp != null, "LSP-EN01: documentSymbol response received");
            if (symResp != null)
            {
                var symbols = symResp.GetArray("result");
                Assert(symbols != null, "LSP-EN01: result is array");
                if (symbols != null)
                {
                    Assert(symbols.Count == 2, $"LSP-EN01: 2 symbols (1 enum + 1 func), got {symbols.Count}");
                    if (symbols.Count >= 1)
                    {
                        var s0 = symbols[0] as JsonObject;
                        Assert(s0?.GetString("name") == "Color", $"LSP-EN01: first symbol = Color, got {s0?.GetString("name")}");
                        Assert(s0?.GetInt("kind") == 10, $"LSP-EN01: Color kind = Enum (10), got {s0?.GetInt("kind")}");
                    }
                }
            }
        }

        // LSP-EN02: EnumName. dot completion lists members
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  Color.\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///en02.ffs", source);
            // Complete after "Color." on line 2, col 8
            session.AddCompletion("file:///en02.ffs", 2, 8);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "LSP-EN02: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP-EN02: result is array");
                if (items != null)
                {
                    Assert(items.Count == 3, $"LSP-EN02: 3 enum members, got {items.Count}");
                    bool hasRed = false, hasGreen = false, hasBlue = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "RED" && kind == 20) hasRed = true;
                        if (label == "GREEN" && kind == 20) hasGreen = true;
                        if (label == "BLUE" && kind == 20) hasBlue = true;
                    }
                    Assert(hasRed, "LSP-EN02: RED member present");
                    Assert(hasGreen, "LSP-EN02: GREEN member present");
                    Assert(hasBlue, "LSP-EN02: BLUE member present");
                }
            }
        }

        // LSP-EN03: Enum name hover shows definition
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///en03.ffs", source);
            // Hover on "Color" on line 0, col 5 (inside "Color" name)
            session.AddHover("file:///en03.ffs", 0, 5);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var hoverResp = session.ExpectResponse(1); // hover
            Assert(hoverResp != null, "LSP-EN03: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                Assert(result != null, "LSP-EN03: result is not null");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value");
                    Assert(value != null && value.Contains("enum Color"), $"LSP-EN03: hover shows 'enum Color', got '{value}'");
                }
            }
        }

        // LSP-EN04: Enum member hover (in function body expression)
        {
            // Hover on "Color" in "Color.RED" expression → should show enum info
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  var x: int = Color.RED\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///en04.ffs", source);
            // Hover on "Color" inside "Color.RED" on line 2, col 15 (inside "Color")
            session.AddHover("file:///en04.ffs", 2, 15);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var hoverResp = session.ExpectResponse(1); // hover
            Assert(hoverResp != null, "LSP-EN04: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                // Expect non-null result with enum info
                Assert(result != null, "LSP-EN04: result is not null (hover on enum ref in expression)");
            }
        }

        // LSP-EN05: General completion lists enum names
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  \n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///en05.ffs", source);
            // Complete at empty line inside function body
            session.AddCompletion("file:///en05.ffs", 2, 2);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "LSP-EN05: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                Assert(items != null, "LSP-EN05: result is array");
                if (items != null)
                {
                    bool hasColorEnum = false;
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        string label = item?.GetString("label");
                        int kind = item?.GetInt("kind") ?? 0;
                        if (label == "Color" && kind == 13) hasColorEnum = true;
                    }
                    Assert(hasColorEnum, "LSP-EN05: 'Color' enum name present with kind=13 (Enum)");
                }
            }
        }

        // ================================================================
        // J. DX4-P0: Workspace & Diagnostics-Only Mode Tests
        // ================================================================

        // DX4-P0-01: UriToPath converts file:// URI to local path (Unix)
        {
            string path = LspServer.UriToPath("file:///home/user/workspace");
            Assert(path == "/home/user/workspace", "DX4-P0-01: Unix path from file:// URI, got '" + path + "'");
        }

        // DX4-P0-02: UriToPath handles percent-encoded characters
        {
            string path = LspServer.UriToPath("file:///home/user/my%20workspace");
            Assert(path == "/home/user/my workspace", "DX4-P0-02: percent-decoded path, got '" + path + "'");
        }

        // DX4-P0-03: UriToPath returns null for null input
        {
            string path = LspServer.UriToPath(null);
            Assert(path == null, "DX4-P0-03: null input → null output");
        }

        // DX4-P0-04: UriToFilePath computes relative path from workspace root
        {
            string rel = LspServer.UriToFilePath("file:///home/user/project/src/main.ffs", "/home/user/project");
            Assert(rel == "src/main.ffs", "DX4-P0-04: relative path from root, got '" + rel + "'");
        }

        // DX4-P0-05: UriToFilePath returns full path when not under root
        {
            string rel = LspServer.UriToFilePath("file:///other/path/main.ffs", "/home/user/project");
            Assert(rel == "/other/path/main.ffs", "DX4-P0-05: not under root → full path, got '" + rel + "'");
        }

        // DX4-P0-06: entryFunc=null compiles without "entry function not found" error
        {
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var result = compiler.Compile("func helper(): int { return 42 }", null, new Dictionary<string, int>());
            Assert(result.Success, "DX4-P0-06: entryFunc=null compiles successfully");
        }

        // DX4-P0-07: entryFunc=null with multiple functions — all compile
        {
            string source = "func foo(): int { return 1 }\nfunc bar(): int { return foo() + 1 }";
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var result = compiler.Compile(source, null, new Dictionary<string, int>());
            Assert(result.Success, "DX4-P0-07: entryFunc=null with multiple funcs compiles, errors=" +
                (result.Errors != null ? string.Join("; ", result.Errors) : "none"));
        }

        // DX4-P0-08: entryFunc=null still catches real errors
        {
            var compiler = new FFVM.Compiler.BytecodeCompiler();
            var result = compiler.Compile("func foo() { return unknown_var }", null, new Dictionary<string, int>());
            Assert(!result.Success, "DX4-P0-08: entryFunc=null still reports real errors");
        }

        // DX4-P0-09: LSP diagnostics-only mode — no entry func → no error diagnostic
        {
            string source = "func helper(): int { return 42 }";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx4p0-09.ffs", source);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
            bool hasEntryError = false;
            foreach (var notif in allDiags)
            {
                var p = notif.GetObject("params");
                if (p == null) continue;
                var diags = p.GetArray("diagnostics");
                if (diags == null) continue;
                foreach (var d in diags)
                {
                    var dObj = d as JsonObject;
                    string msg = dObj?.GetString("message") ?? "";
                    if (msg.Contains("Entry function") || msg.Contains("entry"))
                        hasEntryError = true;
                }
            }
            Assert(!hasEntryError, "DX4-P0-09: no 'entry function not found' error in diagnostics-only mode");
        }

        // DX4-P0-10: LSP initialize with rootUri — capabilities still returned
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p0_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                Assert(initResp != null, "DX4-P0-10: initialize response received");
                if (initResp != null)
                {
                    var result = initResp.GetObject("result");
                    var caps = result?.GetObject("capabilities");
                    Assert(caps != null, "DX4-P0-10: capabilities present with rootUri");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P0-11: LSP auto-discovers .ffvm.d.json in workspace root
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p0_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                // Write a .ffvm.d.json with a syscall declaration
                string declJson = "{ \"syscalls\": [ { \"name\": \"TestSyscall\", \"slot\": 99 } ] }";
                File.WriteAllText(Path.Combine(tmpDir, "host.ffvm.d.json"), declJson);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                // Script that calls TestSyscall — should not produce "unknown syscall" error
                string source = "func main() { TestSyscall() }";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen("file:///dx4p0-11.ffs", source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasUnknownSyscall = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("TestSyscall") && (msg.Contains("Unknown") || msg.Contains("unknown")))
                            hasUnknownSyscall = true;
                    }
                }
                Assert(!hasUnknownSyscall, "DX4-P0-11: auto-discovered .ffvm.d.json → TestSyscall recognized");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P0-12: LSP include resolution via workspace FileResolver
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p0_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                // Write an included file
                File.WriteAllText(Path.Combine(tmpDir, "common.ffs"), "func helper(): int { return 42 }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                // Main script includes common.ffs and calls helper
                string source = "include \"common\"\nfunc main() { var x: int = helper() }";
                string fileUri = rootUri + "/main.ffs";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasIncludeError = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("common") || msg.Contains("helper") || msg.Contains("include"))
                            hasIncludeError = true;
                    }
                }
                Assert(!hasIncludeError, "DX4-P0-12: include resolved via workspace FileResolver, no errors");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P0-13: .ffvm.d.json auto-discovery in subdirectory
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p0_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            string subDir = Path.Combine(tmpDir, "host");
            Directory.CreateDirectory(subDir);
            try
            {
                string declJson = "{ \"syscalls\": [ { \"name\": \"SubDirSyscall\", \"slot\": 50 } ] }";
                File.WriteAllText(Path.Combine(subDir, "sub.ffvm.d.json"), declJson);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "func main() { SubDirSyscall() }";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen("file:///dx4p0-13.ffs", source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasUnknownSyscall = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("SubDirSyscall") && (msg.Contains("Unknown") || msg.Contains("unknown")))
                            hasUnknownSyscall = true;
                    }
                }
                Assert(!hasUnknownSyscall, "DX4-P0-13: .ffvm.d.json auto-discovered in subdirectory");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // K. DX4-P1: .ffproj Project File Tests
        // ================================================================

        // DX4-P1-01: ProjectFile.Parse — basic includePaths
        {
            string json = "{ \"includePaths\": [\"modules/game\", \"modules/skill\"] }";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test/project");
            Assert(pf != null, "DX4-P1-01: parsed successfully");
            Assert(pf.IncludePaths.Length == 2, "DX4-P1-01: 2 include paths, got " + pf.IncludePaths.Length);
            Assert(pf.IncludePaths[0] == "modules/game", "DX4-P1-01: first path is modules/game");
            Assert(pf.IncludePaths[1] == "modules/skill", "DX4-P1-01: second path is modules/skill");
        }

        // DX4-P1-02: ProjectFile.Parse — hostDeclarations
        {
            string json = "{ \"hostDeclarations\": [\"host/skill.ffvm.d.json\", \"host/ai.ffvm.d.json\"] }";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test/project");
            Assert(pf != null, "DX4-P1-02: parsed successfully");
            Assert(pf.HostDeclarations.Length == 2, "DX4-P1-02: 2 host declarations, got " + pf.HostDeclarations.Length);
            Assert(pf.HostDeclarations[0] == "host/skill.ffvm.d.json", "DX4-P1-02: first decl path");
            Assert(pf.HostDeclarations[1] == "host/ai.ffvm.d.json", "DX4-P1-02: second decl path");
        }

        // DX4-P1-03: ProjectFile.Parse — entry field
        {
            string json = "{ \"entry\": \"scripts/main.ffs\" }";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test/project");
            Assert(pf != null, "DX4-P1-03: parsed successfully");
            Assert(pf.Entry == "scripts/main.ffs", "DX4-P1-03: entry = scripts/main.ffs, got '" + pf.Entry + "'");
        }

        // DX4-P1-04: ProjectFile.Parse — compileOptions (inlineThreshold)
        {
            string json = "{ \"compileOptions\": { \"inlineThreshold\": 32, \"diagnosticsEnabled\": false } }";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test/project");
            Assert(pf != null, "DX4-P1-04: parsed successfully");
            Assert(pf.CompileOptions != null, "DX4-P1-04: compileOptions present");
            Assert(pf.CompileOptions.InlineThreshold == 32, "DX4-P1-04: inlineThreshold=32, got " + pf.CompileOptions.InlineThreshold);
            Assert(pf.CompileOptions.DiagnosticsEnabled == false, "DX4-P1-04: diagnosticsEnabled=false");
        }

        // DX4-P1-05: ProjectFile.Parse — empty JSON → defaults
        {
            string json = "{}";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test/project");
            Assert(pf != null, "DX4-P1-05: empty JSON still parses");
            Assert(pf.IncludePaths.Length == 0, "DX4-P1-05: no include paths");
            Assert(pf.HostDeclarations.Length == 0, "DX4-P1-05: no host declarations");
            Assert(pf.Entry == null, "DX4-P1-05: entry is null");
            Assert(pf.CompileOptions == null, "DX4-P1-05: compileOptions is null (use defaults)");
        }

        // DX4-P1-06: ProjectFile.Parse — null/empty input → null
        {
            var pf1 = FFVM.Compiler.ProjectFile.Parse(null, "/test");
            Assert(pf1 == null, "DX4-P1-06: null input → null");
            var pf2 = FFVM.Compiler.ProjectFile.Parse("", "/test");
            Assert(pf2 == null, "DX4-P1-06: empty input → null");
        }

        // DX4-P1-07: ProjectFile.TryDiscover — finds .ffproj in directory
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "my_project.ffproj"),
                    "{ \"includePaths\": [\"src\"], \"entry\": \"main.ffs\" }");
                var pf = FFVM.Compiler.ProjectFile.TryDiscover(tmpDir);
                Assert(pf != null, "DX4-P1-07: discovered .ffproj");
                Assert(pf.IncludePaths.Length == 1, "DX4-P1-07: 1 include path");
                Assert(pf.Entry == "main.ffs", "DX4-P1-07: entry = main.ffs");
                Assert(pf.ProjectDir == tmpDir, "DX4-P1-07: projectDir set to containing directory");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P1-08: ProjectFile.TryDiscover — no .ffproj → null
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var pf = FFVM.Compiler.ProjectFile.TryDiscover(tmpDir);
                Assert(pf == null, "DX4-P1-08: no .ffproj → null");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P1-09: CompositeFileResolver — resolves from first matching path
        {
            var files1 = new Dictionary<string, string> { { "common", "func c1(): int { return 1 }" } };
            var files2 = new Dictionary<string, string> { { "common", "func c2(): int { return 2 }" } };
            var resolver = new FFVM.Compiler.CompositeFileResolver(new FFVM.Compiler.IFileResolver[]
            {
                new FFVM.Compiler.DictionaryFileResolver(files1),
                new FFVM.Compiler.DictionaryFileResolver(files2)
            });
            string content = resolver.ReadFile("common");
            Assert(content != null, "DX4-P1-09: resolved common");
            Assert(content.Contains("c1"), "DX4-P1-09: first resolver wins, got '" + content + "'");
        }

        // DX4-P1-10: CompositeFileResolver — falls through to second path
        {
            var files1 = new Dictionary<string, string> { { "game", "func g(): int { return 1 }" } };
            var files2 = new Dictionary<string, string> { { "skill", "func s(): int { return 2 }" } };
            var resolver = new FFVM.Compiler.CompositeFileResolver(new FFVM.Compiler.IFileResolver[]
            {
                new FFVM.Compiler.DictionaryFileResolver(files1),
                new FFVM.Compiler.DictionaryFileResolver(files2)
            });
            string content = resolver.ReadFile("skill");
            Assert(content != null, "DX4-P1-10: resolved from second resolver");
            Assert(content.Contains("s()"), "DX4-P1-10: second resolver content");
        }

        // DX4-P1-11: LSP initialize + .ffproj → includePaths used for include resolution
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            string modulesDir = Path.Combine(tmpDir, "modules");
            Directory.CreateDirectory(modulesDir);
            try
            {
                // Write .ffproj with includePaths pointing to modules/
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"),
                    "{ \"includePaths\": [\"modules\"] }");
                // Write an includable file in modules/
                File.WriteAllText(Path.Combine(modulesDir, "helpers.ffs"),
                    "func helper(): int { return 42 }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                // Script includes "helpers" — should resolve via includePaths
                string source = "include \"helpers\"\nfunc main() { var x: int = helper() }";
                string fileUri = rootUri + "/main.ffs";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasError = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("helpers") || msg.Contains("helper") || msg.Contains("include"))
                            hasError = true;
                    }
                }
                Assert(!hasError, "DX4-P1-11: include resolved via .ffproj includePaths");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P1-12: LSP initialize + .ffproj → hostDeclarations loaded (syscall recognized)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            string hostDir = Path.Combine(tmpDir, "host");
            Directory.CreateDirectory(hostDir);
            try
            {
                // Write a host declaration file
                string declJson = "{ \"syscalls\": [ { \"name\": \"ProjectSyscall\", \"slot\": 77 } ] }";
                File.WriteAllText(Path.Combine(hostDir, "skill.ffvm.d.json"), declJson);
                // Write .ffproj pointing to the host declaration
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"),
                    "{ \"hostDeclarations\": [\"host/skill.ffvm.d.json\"] }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "func main() { ProjectSyscall() }";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen("file:///dx4p1-12.ffs", source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasUnknownSyscall = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("ProjectSyscall") && (msg.Contains("Unknown") || msg.Contains("unknown")))
                            hasUnknownSyscall = true;
                    }
                }
                Assert(!hasUnknownSyscall, "DX4-P1-12: .ffproj hostDeclarations → ProjectSyscall recognized");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P1-13: LSP initialize + .ffproj → multiple includePaths (cross-directory resolution)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            string gameDir = Path.Combine(tmpDir, "game");
            string skillDir = Path.Combine(tmpDir, "skill");
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(skillDir);
            try
            {
                // Write .ffproj with two includePaths
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"),
                    "{ \"includePaths\": [\"game\", \"skill\"] }");
                // Write files in each directory
                File.WriteAllText(Path.Combine(gameDir, "core.ffs"),
                    "func game_init(): int { return 1 }");
                File.WriteAllText(Path.Combine(skillDir, "base.ffs"),
                    "func skill_init(): int { return 2 }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                // Script includes from both directories
                string source = "include \"core\"\ninclude \"base\"\nfunc main() { var a: int = game_init()\nvar b: int = skill_init() }";
                string fileUri = rootUri + "/main.ffs";
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var allDiags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasError = false;
                foreach (var notif in allDiags)
                {
                    var p = notif.GetObject("params");
                    if (p == null) continue;
                    var diags = p.GetArray("diagnostics");
                    if (diags == null) continue;
                    foreach (var d in diags)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("core") || msg.Contains("base") || msg.Contains("game_init") || msg.Contains("skill_init"))
                            hasError = true;
                    }
                }
                Assert(!hasError, "DX4-P1-13: multiple includePaths resolve cross-directory includes");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P1-14: ProjectFile.Parse — compileOptions with all fields
        {
            string json = "{ \"compileOptions\": { \"inlineThreshold\": 8, \"inlineDepthMax\": 5, \"maxHoistedPerLoop\": 4, \"diagnosticsEnabled\": true } }";
            var pf = FFVM.Compiler.ProjectFile.Parse(json, "/test");
            Assert(pf != null && pf.CompileOptions != null, "DX4-P1-14: all compileOptions parsed");
            Assert(pf.CompileOptions.InlineThreshold == 8, "DX4-P1-14: inlineThreshold=8, got " + pf.CompileOptions.InlineThreshold);
            Assert(pf.CompileOptions.InlineDepthMax == 5, "DX4-P1-14: inlineDepthMax=5, got " + pf.CompileOptions.InlineDepthMax);
            Assert(pf.CompileOptions.MaxHoistedPerLoop == 4, "DX4-P1-14: maxHoistedPerLoop=4, got " + pf.CompileOptions.MaxHoistedPerLoop);
            Assert(pf.CompileOptions.DiagnosticsEnabled == true, "DX4-P1-14: diagnosticsEnabled=true");
        }

        // DX4-P1-15: ProjectFile.ResolvePath — relative and absolute paths
        {
            var pf = new FFVM.Compiler.ProjectFile { ProjectDir = "/home/user/project" };
            string abs = pf.ResolvePath("/absolute/path.ffs");
            Assert(abs == "/absolute/path.ffs", "DX4-P1-15: absolute path unchanged, got '" + abs + "'");
            string rel = pf.ResolvePath("src/main.ffs");
            Assert(rel.EndsWith("src/main.ffs"), "DX4-P1-15: relative resolved, got '" + rel + "'");
            Assert(rel.StartsWith("/"), "DX4-P1-15: result is absolute, got '" + rel + "'");
        }

        // DX4-P1-16: ProjectFile.BuildFileResolver — no includePaths uses projectDir
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p1_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "test.ffs"), "func t(): int { return 1 }");
                var pf = new FFVM.Compiler.ProjectFile { ProjectDir = tmpDir };
                var resolver = pf.BuildFileResolver();
                Assert(resolver != null, "DX4-P1-16: resolver created from projectDir");
                string content = resolver.ReadFile("test");
                Assert(content != null, "DX4-P1-16: resolved test.ffs from projectDir");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // L. DX4-P3: Cross-file symbol query tests
        // ================================================================

        // DX4-P3-01: FilePathToUri — relative path with rootPath
        {
            string uri = LspServer.FilePathToUri("common.ffs", "/home/user/project");
            Assert(uri == "file:///home/user/project/common.ffs", "DX4-P3-01: relative → file URI, got '" + uri + "'");
        }

        // DX4-P3-02: FilePathToUri — absolute path preserved
        {
            string uri = LspServer.FilePathToUri("/home/user/project/src/common.ffs", "/home/user/project");
            Assert(uri == "file:///home/user/project/src/common.ffs", "DX4-P3-02: absolute → file URI, got '" + uri + "'");
        }

        // DX4-P3-03: FilePathToUri — null inputs
        {
            Assert(LspServer.FilePathToUri(null, "/root") == null, "DX4-P3-03a: null originFile → null");
            Assert(LspServer.FilePathToUri("rel.ffs", null) == null, "DX4-P3-03b: relative + null rootPath → null");
        }

        // DX4-P3-04: Preprocessor sets OriginFile on included function
        {
            var resolver = new FFVM.Compiler.DictionaryFileResolver(new Dictionary<string, string> {
                { "common", "func helper(): int { return 42 }" }
            });
            var preprocessor = new FFVM.Compiler.Preprocessor(resolver);
            string mainSrc = "include \"common\"\nfunc main() { var x: int = helper() }";
            var merged = preprocessor.Resolve(mainSrc, "main.ffs", out var errors);
            Assert(errors == null || errors.Count == 0, "DX4-P3-04a: no preprocessor errors");
            bool found = false;
            foreach (var func in merged.Functions)
            {
                if (func.Name == "helper")
                {
                    Assert(func.OriginFile == "common", "DX4-P3-04b: helper OriginFile = 'common', got '" + func.OriginFile + "'");
                    found = true;
                }
            }
            Assert(found, "DX4-P3-04c: helper found in merged AST");
        }

        // DX4-P3-05: Go-to-definition on cross-file function — jumps to included file
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "common.ffs"), "func helper(): int { return 42 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"common\"\nfunc main() { var x: int = helper() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "helper()" call on line 1, "var x: int = helper()" → "helper" starts at col 27
                session.AddDefinition(fileUri, 1, 27);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                Assert(defResp != null, "DX4-P3-05a: definition response received");
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX4-P3-05b: result not null (cross-file definition)");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("common.ffs"),
                        "DX4-P3-05c: definition URI points to common.ffs, got '" + defUri + "'");
                    var range = result.GetObject("range");
                    var start = range?.GetObject("start");
                    Assert(start != null && start.GetInt("line") == 0,
                        "DX4-P3-05d: definition at line 0 in common.ffs");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-06: Go-to-definition on same-file function still works
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "func helper(): int { return 42 }\nfunc main() { var x: int = helper() }";
                string fileUri = rootUri + "/test.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "helper()" call on line 1, col ~28
                session.AddDefinition(fileUri, 1, 28);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX4-P3-06a: result not null");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("test.ffs"),
                        "DX4-P3-06b: same-file definition URI");
                    var range = result.GetObject("range");
                    var start = range?.GetObject("start");
                    Assert(start != null && start.GetInt("line") == 0,
                        "DX4-P3-06c: definition at line 0 (same file)");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-07: Cross-file hover on included function shows signature
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "common.ffs"),
                    "/// Returns the answer\nfunc helper(): int { return 42 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"common\"\nfunc main() { var x: int = helper() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddHover(fileUri, 1, 27);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var hoverResp = session.ExpectResponse(1);
                var result = hoverResp?.GetObject("result");
                Assert(result != null, "DX4-P3-07a: hover result not null (cross-file function)");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value") ?? "";
                    Assert(value.Contains("helper"), "DX4-P3-07b: hover shows helper, got '" + value + "'");
                    Assert(value.Contains("int"), "DX4-P3-07c: hover shows return type");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-08: Cross-file completion includes function from included file
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "common.ffs"),
                    "func crossHelper(): int { return 99 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"common\"\nfunc main() { var x: int = crossHelper() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddCompletion(fileUri, 1, 18);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var compResp = session.ExpectResponse(1);
                bool foundCrossHelper = false;
                var arr = compResp?.GetArray("result");
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var itemObj = item as JsonObject;
                        if (itemObj != null && itemObj.GetString("label") == "crossHelper")
                        {
                            foundCrossHelper = true;
                            break;
                        }
                    }
                }
                Assert(foundCrossHelper, "DX4-P3-08: cross-file function appears in completion");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-09: Cross-file signature help for included function
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"),
                    "func add(a: int, b: int): int { return a + b }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"lib\"\nfunc main() { var r: int = add(1, 2) }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                session.AddSignatureHelp(fileUri, 1, 32);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var sigResp = session.ExpectResponse(1);
                var result = sigResp?.GetObject("result");
                if (result != null)
                {
                    var signatures = result.GetArray("signatures");
                    Assert(signatures != null && signatures.Count > 0,
                        "DX4-P3-09a: cross-file signature help has signatures");
                    if (signatures != null && signatures.Count > 0)
                    {
                        var sig = signatures[0] as JsonObject;
                        string label = sig?.GetString("label") ?? "";
                        Assert(label.Contains("add"), "DX4-P3-09b: signature label contains 'add', got '" + label + "'");
                        Assert(label.Contains("a: int"), "DX4-P3-09c: signature shows param types");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-10: Merged AST per-document isolation — file B without include doesn't get file A's symbols
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "common.ffs"), "func shared(): int { return 1 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string srcA = "include \"common\"\nfunc mainA() { var x: int = shared() }";
                string srcB = "func mainB() { var y: int = 99 }";
                string uriA = rootUri + "/a.ffs";
                string uriB = rootUri + "/b.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(uriA, srcA);
                session.AddDidOpen(uriB, srcB);
                session.AddCompletion(uriA, 1, 30);
                session.AddCompletion(uriB, 0, 20);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var compA = session.ExpectResponse(1);
                var compB = session.ExpectResponse(2);

                // File A should have "shared" in completion
                bool aHasShared = false;
                var arrA = compA?.GetArray("result");
                if (arrA != null) foreach (var item in arrA)
                {
                    var itemObj = item as JsonObject;
                    if (itemObj?.GetString("label") == "shared") { aHasShared = true; break; }
                }
                Assert(aHasShared, "DX4-P3-10a: file A completion includes cross-file 'shared'");

                // File B should NOT have "shared"
                bool bHasShared = false;
                var arrB = compB?.GetArray("result");
                if (arrB != null) foreach (var item in arrB)
                {
                    var itemObj = item as JsonObject;
                    if (itemObj?.GetString("label") == "shared") { bHasShared = true; break; }
                }
                Assert(!bHasShared, "DX4-P3-10b: file B does NOT include 'shared' (no include)");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-11: Transitive include — definition jumps to transitively included file
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "util.ffs"),
                    "func utilFunc(): int { return 7 }");
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"),
                    "include \"util\"\nfunc libFunc(): int { return utilFunc() }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"lib\"\nfunc main() { var x: int = utilFunc() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "utilFunc()" call on line 1
                session.AddDefinition(fileUri, 1, 28);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX4-P3-11a: transitive definition result not null");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("util.ffs"),
                        "DX4-P3-11b: transitive definition URI → util.ffs, got '" + defUri + "'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-12: Cross-file struct field dot-completion
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "types.ffs"),
                    "struct Point { x: int; y: int }\nfunc makePoint(): Point { return Point { x: 1, y: 2 } }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                // Use makePoint() to avoid struct literal parse issue in main file
                string source = "include \"types\"\nfunc main() {\n    var p: Point = makePoint()\n    var a: int = p.x\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Dot-completion on "p." — line 3, after the dot at "p." col ~19
                session.AddCompletion(fileUri, 3, 19);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var compResp = session.ExpectResponse(1);
                bool hasX = false, hasY = false;
                var arr = compResp?.GetArray("result");
                if (arr != null) foreach (var item in arr)
                {
                    var itemObj = item as JsonObject;
                    string label = itemObj?.GetString("label") ?? "";
                    if (label == "x") hasX = true;
                    if (label == "y") hasY = true;
                }
                Assert(hasX && hasY, "DX4-P3-12: dot-completion shows cross-file struct fields (x, y)");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-13: No workspace root — no crash, falls back to per-file AST
        {
            string source = "func helper(): int { return 42 }\nfunc main() { var x: int = helper() }";
            var session = new LspBatchSession();
            session.AddInitialize(); // no rootUri
            session.AddInitialized();
            session.AddDidOpen("file:///noroot.ffs", source);
            session.AddDefinition("file:///noroot.ffs", 1, 28);
            session.AddCompletion("file:///noroot.ffs", 1, 18);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            var compResp = session.ExpectResponse(2);
            Assert(defResp != null, "DX4-P3-13a: definition works without workspace root");
            bool foundHelper = false;
            var arr = compResp?.GetArray("result");
            if (arr != null) foreach (var item in arr)
            {
                var itemObj = item as JsonObject;
                if (itemObj?.GetString("label") == "helper") { foundHelper = true; break; }
            }
            Assert(foundHelper, "DX4-P3-13b: completion includes same-file helper without workspace");
        }

        // DX4-P3-14: Go-to-definition across non-root include path (ffproj includePaths)
        // Regression test: include resolved via a subdirectory include path must produce
        // a valid OriginFile so that cross-file go-to-definition URI points to the actual file.
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_ip_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                // Create subdirectory structure: scripts/common/helpers.ffs
                string scriptsDir = Path.Combine(tmpDir, "scripts");
                string commonDir = Path.Combine(scriptsDir, "common");
                Directory.CreateDirectory(commonDir);
                File.WriteAllText(Path.Combine(commonDir, "helpers.ffs"), "func helper(): int { return 42 }");

                // Create .ffproj with includePaths pointing to the subdirectory
                string ffproj = "{ \"includePaths\": [\".\", \"scripts\"] }";
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), ffproj);

                // main.ffs at workspace root includes "common/helpers" — resolved via "scripts" include path
                string source = "include \"common/helpers\"\nfunc main() { var x: int = helper() }";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), source);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "helper()" call on line 1, col 27
                session.AddDefinition(fileUri, 1, 27);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                Assert(defResp != null, "DX4-P3-14a: definition response received");
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX4-P3-14b: result not null (cross-file via include path)");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    // The URI must point to the actual file in scripts/common/helpers.ffs
                    Assert(defUri != null && defUri.Contains("helpers.ffs"),
                        "DX4-P3-14c: definition URI points to helpers.ffs, got '" + defUri + "'");
                    Assert(defUri != null && defUri.Contains("scripts/common/helpers"),
                        "DX4-P3-14d: definition URI includes correct subdirectory path, got '" + defUri + "'");
                    var range = result.GetObject("range");
                    var start = range?.GetObject("start");
                    Assert(start != null && start.GetInt("line") == 0,
                        "DX4-P3-14e: definition at line 0 in helpers.ffs");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-15: Cross-file go-to-definition for struct type across non-root include path
        // Verifies that struct definitions in included files (resolved via includePaths) produce
        // correct URIs for go-to-definition on type annotations.
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_st_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                // Create subdirectory structure: modules/types.ffs with struct definition
                string modulesDir = Path.Combine(tmpDir, "modules");
                Directory.CreateDirectory(modulesDir);
                File.WriteAllText(Path.Combine(modulesDir, "types.ffs"), "struct Vec2 { x: int; y: int }");

                // Create .ffproj with includePaths pointing to the subdirectory
                string ffproj = "{ \"includePaths\": [\"modules\"] }";
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), ffproj);

                // main.ffs includes "types" — resolved via "modules" include path
                string source = "include \"types\"\nfunc main() { var v: Vec2 = Vec2 { x = 1; y = 2 } }";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), source);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "Vec2" type annotation on line 1 — "var v: Vec2" — col 22 (inside "Vec2")
                session.AddDefinition(fileUri, 1, 22);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                Assert(defResp != null, "DX4-P3-15a: definition response received");
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX4-P3-15b: result not null (cross-file struct via include path)");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("types.ffs"),
                        "DX4-P3-15c: definition URI points to types.ffs, got '" + defUri + "'");
                    Assert(defUri != null && defUri.Contains("modules/types"),
                        "DX4-P3-15d: definition URI includes correct subdirectory path, got '" + defUri + "'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P3-16: Cross-file find-references across non-root include path
        // Verifies that references to functions defined in included files (resolved via includePaths)
        // produce correct URIs for both the definition site and usage site.
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p3_ref_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string scriptsDir = Path.Combine(tmpDir, "scripts");
                string commonDir = Path.Combine(scriptsDir, "common");
                Directory.CreateDirectory(commonDir);
                File.WriteAllText(Path.Combine(commonDir, "util.ffs"), "func calcDamage(): int { return 10 }");

                string ffproj = "{ \"includePaths\": [\".\", \"scripts\"] }";
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), ffproj);

                string source = "include \"common/util\"\nfunc main() { var x: int = calcDamage() }";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), source);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // "calcDamage()" call on line 1 — col 28
                session.AddReferences(fileUri, 1, 28);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var refResp = session.ExpectResponse(1);
                Assert(refResp != null, "DX4-P3-16a: references response received");
                var resultArr = refResp?.GetArray("result");
                Assert(resultArr != null && resultArr.Count >= 2,
                    "DX4-P3-16b: ≥2 reference locations (def + usage), got " + (resultArr?.Count ?? 0));
                if (resultArr != null && resultArr.Count >= 2)
                {
                    // Collect all URIs
                    bool hasUtilUri = false;
                    bool hasMainUri = false;
                    for (int ri = 0; ri < resultArr.Count; ri++)
                    {
                        var loc = resultArr[ri] as JsonObject;
                        string locUri = loc?.GetString("uri");
                        if (locUri != null && locUri.Contains("util.ffs")) hasUtilUri = true;
                        if (locUri != null && locUri.Contains("main.ffs")) hasMainUri = true;
                    }
                    Assert(hasUtilUri, "DX4-P3-16c: references include definition in util.ffs");
                    Assert(hasMainUri, "DX4-P3-16d: references include usage in main.ffs");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // N. DX4-P4: LSP-Assisted .ffproj Creation Tests
        // ================================================================

        // DX4-P4-01: workspace with .ffs but no .ffproj → server sends window/showMessageRequest
        // DX4-P4-02: showMessageRequest message text is correct
        // DX4-P4-03: showMessageRequest has 3 actions (Create, Ignore, Don't ask again)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p4_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { var x: int = 1 }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                // Pre-queue response to the server's showMessageRequest (id=900001) — user clicks "Ignore"
                var ignoreResult = new JsonObject();
                ignoreResult.Set("title", "Ignore");
                session.AddResponse(900001, ignoreResult);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var showMsgReq = session.FindRequest("window/showMessageRequest");
                Assert(showMsgReq != null, "DX4-P4-01: showMessageRequest sent when .ffs exists without .ffproj");

                var msgParams = showMsgReq?.GetObject("params");
                string message = msgParams?.GetString("message");
                Assert(message != null && message.Contains(".ffproj"), "DX4-P4-02: message mentions .ffproj, got '" + (message ?? "null") + "'");

                var actions = msgParams?.GetArray("actions");
                bool hasCreate = false, hasIgnore = false, hasNever = false;
                if (actions != null)
                {
                    foreach (var a in actions)
                    {
                        var aObj = a as JsonObject;
                        string title = aObj?.GetString("title");
                        if (title == "Create") hasCreate = true;
                        if (title == "Ignore") hasIgnore = true;
                        if (title == "Don't ask again") hasNever = true;
                    }
                }
                Assert(hasCreate && hasIgnore && hasNever,
                    "DX4-P4-03: 3 actions present (Create=" + hasCreate + ", Ignore=" + hasIgnore + ", Don't ask again=" + hasNever + ")");

                // "Ignore" was chosen → no applyEdit should be sent
                var applyEditReq = session.FindRequest("workspace/applyEdit");
                Assert(applyEditReq == null, "DX4-P4-04: Ignore → no workspace/applyEdit sent");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P4-05: user clicks "Create" → workspace/applyEdit sent with CreateFile + TextDocumentEdit
        // DX4-P4-06: applyEdit URI points to .ffproj in workspace root
        // DX4-P4-07: applyEdit template matches ProjectFile.GenerateTemplate(null)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p4_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { var x: int = 1 }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                // User clicks "Create" → response to showMessageRequest
                var createResult = new JsonObject();
                createResult.Set("title", "Create");
                session.AddResponse(900001, createResult);
                // Response to workspace/applyEdit
                var applyResult = new JsonObject();
                applyResult.Set("applied", true);
                session.AddResponse(900002, applyResult);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var applyEditReq = session.FindRequest("workspace/applyEdit");
                Assert(applyEditReq != null, "DX4-P4-05: Create → workspace/applyEdit sent");

                // Check the applyEdit content
                var editParams = applyEditReq?.GetObject("params");
                var edit = editParams?.GetObject("edit");
                var docChanges = edit?.GetArray("documentChanges");

                // First element: CreateFile
                bool hasCreateFile = false;
                bool uriPointsToFfproj = false;
                string templateContent = null;

                if (docChanges != null && docChanges.Count >= 2)
                {
                    var cf = docChanges[0] as JsonObject;
                    hasCreateFile = cf?.GetString("kind") == "create";
                    string cfUri = cf?.GetString("uri");
                    uriPointsToFfproj = cfUri != null && cfUri.EndsWith(".ffproj");

                    // Second element: TextDocumentEdit
                    var tde = docChanges[1] as JsonObject;
                    var edits = tde?.GetArray("edits");
                    if (edits != null && edits.Count > 0)
                    {
                        var te = edits[0] as JsonObject;
                        templateContent = te?.GetString("newText");
                    }

                    Assert(uriPointsToFfproj, "DX4-P4-06: applyEdit URI ends with .ffproj, got '" + (cfUri ?? "null") + "'");
                }
                else
                {
                    Assert(false, "DX4-P4-06: applyEdit URI ends with .ffproj (documentChanges missing or incomplete)");
                }

                Assert(hasCreateFile, "DX4-P4-05b: first documentChange is CreateFile");

                string expectedTemplate = FFVM.Compiler.ProjectFile.GenerateTemplate(null);
                Assert(templateContent == expectedTemplate,
                    "DX4-P4-07: template content matches GenerateTemplate(null)");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P4-08: user dismisses dialog (null result) → no applyEdit
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p4_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { var x: int = 1 }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                // User dismisses dialog → null result
                session.AddResponse(900001, null);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var applyEditReq = session.FindRequest("workspace/applyEdit");
                Assert(applyEditReq == null, "DX4-P4-08: dismissed dialog → no workspace/applyEdit sent");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P4-09: workspace with .ffproj → no showMessageRequest
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p4_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { var x: int = 1 }");
                File.WriteAllText(Path.Combine(tmpDir, ".ffproj"), "{\"includePaths\":[\".\"],\"hostDeclarations\":[],\"entry\":null,\"compileOptions\":{}}");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var showMsgReq = session.FindRequest("window/showMessageRequest");
                Assert(showMsgReq == null, "DX4-P4-09: .ffproj exists → no showMessageRequest sent");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P4-10: workspace with no .ffs files → no showMessageRequest
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx4p4_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                // Create a non-.ffs file
                File.WriteAllText(Path.Combine(tmpDir, "readme.txt"), "hello");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var showMsgReq = session.FindRequest("window/showMessageRequest");
                Assert(showMsgReq == null, "DX4-P4-10: no .ffs files → no showMessageRequest sent");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX4-P4-11: no rootUri → no showMessageRequest
        {
            var session = new LspBatchSession();
            session.AddInitialize(); // no rootUri
            session.AddInitialized();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var showMsgReq = session.FindRequest("window/showMessageRequest");
            Assert(showMsgReq == null, "DX4-P4-11: no rootUri → no showMessageRequest sent");
        }

        // DX4-P4-12: PathToFileUri converts Unix path correctly
        {
            string uri = LspServer.PathToFileUri("/home/user/project/.ffproj");
            Assert(uri == "file:///home/user/project/.ffproj", "DX4-P4-12: Unix path to URI, got '" + (uri ?? "null") + "'");
        }

        // DX4-P4-13: PathToFileUri handles null
        {
            string uri = LspServer.PathToFileUri(null);
            Assert(uri == null, "DX4-P4-13: null path → null URI");
        }

        // ============================================================
        // O. DX5: Usability — Rename, Semantic Tokens, Include Navigation, Struct/Enum Members
        // ============================================================

        // DX5-01: Rename capability is declared
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();
            var initResp = session.ExpectResponse(0);
            var caps = initResp?.GetObject("result")?.GetObject("capabilities");
            var rename = caps?.GetObject("renameProvider");
            Assert(rename != null, "DX5-01: renameProvider declared in capabilities");
            if (rename != null)
                Assert(rename.GetBool("prepareProvider", false), "DX5-01: prepareProvider=true");
        }

        // DX5-02: Semantic tokens capability is declared
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddShutdown();
            session.AddExit();
            session.Run();
            var initResp = session.ExpectResponse(0);
            var caps = initResp?.GetObject("result")?.GetObject("capabilities");
            var semTok = caps?.GetObject("semanticTokensProvider");
            Assert(semTok != null, "DX5-02: semanticTokensProvider declared in capabilities");
            if (semTok != null)
            {
                var legend = semTok.GetObject("legend");
                Assert(legend != null, "DX5-02: legend present");
                var tokenTypes = legend?.GetArray("tokenTypes");
                Assert(tokenTypes != null && tokenTypes.Count > 0, "DX5-02: tokenTypes non-empty");
            }
        }

        // DX5-03: Semantic tokens — struct declaration produces struct token
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sem.ffs", source);
            session.AddSemanticTokensFull("file:///sem.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // init
            var semResp = session.ExpectResponse(1);
            Assert(semResp != null, "DX5-03: semantic tokens response received");
            var semResult = semResp?.GetObject("result");
            var data = semResult?.GetArray("data");
            Assert(data != null && data.Count >= 5, "DX5-03: data array has tokens, count=" + (data?.Count ?? 0));
            // First token should be struct name "Vec2" on line 0
            if (data != null && data.Count >= 5)
            {
                int deltaLine = Convert.ToInt32(data[0]);
                int tokenType = Convert.ToInt32(data[3]);
                Assert(deltaLine == 0, $"DX5-03: first token on line 0, got {deltaLine}");
                Assert(tokenType == 1, $"DX5-03: first token is struct (type=1), got {tokenType}");
            }
        }

        // DX5-04: Semantic tokens — enum declaration produces enum + enumMember tokens
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sem2.ffs", source);
            session.AddSemanticTokensFull("file:///sem2.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // Should have: enum name (type=2) + 3 enum members (type=3) = 4 tokens × 5 ints = 20
            Assert(data != null && data.Count >= 20, $"DX5-04: ≥20 data ints for enum+3 members, got {data?.Count ?? 0}");
            if (data != null && data.Count >= 5)
            {
                int firstType = Convert.ToInt32(data[3]);
                Assert(firstType == 2, $"DX5-04: first token is enum (type=2), got {firstType}");
            }
        }

        // DX5-05: Semantic tokens — struct field (property) token in declaration
        {
            string source = "struct Pos { x: int; y: int }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sem3.ffs", source);
            session.AddSemanticTokensFull("file:///sem3.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // struct name (type=1) + 2 fields (type=4) = 3 tokens × 5 ints = 15
            Assert(data != null && data.Count >= 15, $"DX5-05: ≥15 data ints for struct+2 fields, got {data?.Count ?? 0}");
            // Second token should be field 'x' (type=4=property)
            if (data != null && data.Count >= 10)
            {
                int secondType = Convert.ToInt32(data[8]);
                Assert(secondType == 4, $"DX5-05: second token is property (type=4), got {secondType}");
            }
        }

        // DX5-06: prepareRename on a function name returns valid range
        {
            string source = "func helper(): int {\n  return 42\n}\nfunc main() {\n  var x: int = helper()\n  wait x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ren.ffs", source);
            // prepareRename on "helper" in declaration — line 0, col 5 (0-based)
            session.AddPrepareRename("file:///ren.ffs", 0, 5);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var prepResp = session.ExpectResponse(1);
            Assert(prepResp != null, "DX5-06: prepareRename response received");
            var prepResult = prepResp?.GetObject("result");
            Assert(prepResult != null, "DX5-06: prepareRename result not null");
            if (prepResult != null)
            {
                string placeholder = prepResult.GetString("placeholder");
                Assert(placeholder == "helper", $"DX5-06: placeholder='helper', got '{placeholder}'");
            }
        }

        // DX5-07: Rename function — all references updated
        {
            string source = "func helper(): int {\n  return 42\n}\nfunc main() {\n  var x: int = helper()\n  wait x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ren2.ffs", source);
            // Rename "helper" to "assist" — on call site line 4, col 15
            session.AddRename("file:///ren2.ffs", 4, 15, "assist");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            Assert(renResp != null, "DX5-07: rename response received");
            var renResult = renResp?.GetObject("result");
            Assert(renResult != null, "DX5-07: rename result not null (WorkspaceEdit)");
            if (renResult != null)
            {
                var changes = renResult.GetObject("changes");
                Assert(changes != null, "DX5-07: changes map present");
                if (changes != null)
                {
                    var fileEdits = changes.GetArray("file:///ren2.ffs");
                    // 1 declaration + 1 call = 2 edits
                    Assert(fileEdits != null && fileEdits.Count == 2,
                        $"DX5-07: 2 text edits (decl+call), got {fileEdits?.Count ?? 0}");
                    if (fileEdits != null && fileEdits.Count > 0)
                    {
                        var firstEdit = fileEdits[0] as JsonObject;
                        Assert(firstEdit?.GetString("newText") == "assist",
                            "DX5-07: newText='assist'");
                    }
                }
            }
        }

        // DX5-08: Rename variable — all references updated
        {
            string source = "func main() {\n  var counter: int = 0\n  counter = counter + 1\n  wait counter\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///ren3.ffs", source);
            // Rename "counter" in declaration — line 1, col 6
            session.AddRename("file:///ren3.ffs", 1, 6, "cnt");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            var changes = renResp?.GetObject("result")?.GetObject("changes");
            var edits = changes?.GetArray("file:///ren3.ffs");
            // var counter declaration + counter = ... + ... counter + 1 + wait counter = ≥4
            Assert(edits != null && edits.Count >= 4,
                $"DX5-08: ≥4 text edits for variable rename, got {edits?.Count ?? 0}");
        }

        // DX5-09: Definition on struct field → jumps to field declaration
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  wait v.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sf.ffs", source);
            // Definition on "x" in struct declaration body — line 0
            // struct Vec2 { x: int; y: int }
            //              ^ col 14 (0-based)
            session.AddDefinition("file:///sf.ffs", 0, 14);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            var result = defResp?.GetObject("result");
            Assert(result != null, "DX5-09: definition on struct field name returns result");
        }

        // DX5-10: Definition on enum member → jumps to enum member declaration
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///em.ffs", source);
            // Definition on "RED" in enum declaration — line 0
            // enum Color { RED, GREEN, BLUE }
            //              ^ col 13 (0-based)
            session.AddDefinition("file:///em.ffs", 0, 13);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            var result = defResp?.GetObject("result");
            Assert(result != null, "DX5-10: definition on enum member returns result");
        }

        // DX5-11: References on struct name → includes declaration + type annotation usage
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///sr.ffs", source);
            // References on "Vec2" in struct declaration — line 0, col 7
            session.AddReferences("file:///sr.ffs", 0, 7);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var refResp = session.ExpectResponse(1);
            var refs = refResp?.GetArray("result");
            // 1 declaration + 1 usage in "var v: Vec2" = at least 2
            Assert(refs != null && refs.Count >= 2,
                $"DX5-11: ≥2 references for struct name, got {refs?.Count ?? 0}");
        }

        // DX5-12: Include navigation — definition on include path opens file
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx5_incnav_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "utils.ffs"), "func add(a: int, b: int): int { return a + b }");
                string mainSource = "include \"utils\"\nfunc main() {\n  var r: int = add(1, 2)\n  wait r\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                // Also create .ffproj for proper workspace setup
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                // Definition on "utils" in include line — line 0, col 9 (inside the string)
                session.AddDefinition(mainUri, 0, 9);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "DX5-12: definition on include path returns result");
                if (result != null)
                {
                    string targetUri = result.GetString("uri");
                    Assert(targetUri != null && targetUri.Contains("utils"),
                        $"DX5-12: target URI contains 'utils', got '{targetUri}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX5-13: Include references — find all includes of same file
        {
            string source = "include \"utils\"\ninclude \"utils\"\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///incref.ffs", source);
            // References on "utils" in first include — line 0, col 9
            session.AddReferences("file:///incref.ffs", 0, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var refResp = session.ExpectResponse(1);
            var refs = refResp?.GetArray("result");
            Assert(refs != null && refs.Count == 2,
                $"DX5-13: 2 references for duplicate include, got {refs?.Count ?? 0}");
        }

        // DX5-14: prepareRename on include path returns null (not renamable)
        {
            string source = "include \"utils\"\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///incprep.ffs", source);
            session.AddPrepareRename("file:///incprep.ffs", 0, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var prepResp = session.ExpectResponse(1);
            var prepResult = prepResp?.GetObject("result");
            Assert(prepResult == null, "DX5-14: prepareRename on include path returns null");
        }

        // DX5-15: ffproj auto-creation uses folder name
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "MyProject_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { wait 1 }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                var createResult = new JsonObject();
                createResult.Set("title", "Create");
                session.AddResponse(900001, createResult);
                var applyResult = new JsonObject();
                applyResult.Set("applied", true);
                session.AddResponse(900002, applyResult);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var applyEditReq = session.FindRequest("workspace/applyEdit");
                Assert(applyEditReq != null, "DX5-15: workspace/applyEdit sent");
                if (applyEditReq != null)
                {
                    var docChanges = applyEditReq.GetObject("params")?.GetObject("edit")?.GetArray("documentChanges");
                    if (docChanges != null && docChanges.Count >= 1)
                    {
                        var cf = docChanges[0] as JsonObject;
                        string cfUri = cf?.GetString("uri");
                        string folderName = Path.GetFileName(tmpDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                        string expected = folderName + ".ffproj";
                        Assert(cfUri != null && cfUri.EndsWith(expected),
                            $"DX5-15: URI ends with '{expected}', got '{cfUri}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX5-16: GenerateTemplate produces parseable JSON (with comments)
        {
            string template = FFVM.Compiler.ProjectFile.GenerateTemplate(null);
            Assert(template.Contains("//"), "DX5-16: template contains comment lines");
            var pf = FFVM.Compiler.ProjectFile.Parse(template, "/test");
            Assert(pf != null, "DX5-16: template with comments is parseable");
            Assert(pf.IncludePaths.Length == 1 && pf.IncludePaths[0] == ".", "DX5-16: default includePath preserved");
        }

        // DX5-17: StripLineComments preserves strings containing //
        {
            string input = "{ \"url\": \"https://example.com\" }";
            string stripped = FFVM.Compiler.ProjectFile.StripLineComments(input);
            Assert(stripped.Contains("https://example.com"), "DX5-17: URL inside string preserved");
        }

        // DX5-18: Rename struct name — declaration and type usage updated
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///renstruct.ffs", source);
            // Rename "Vec2" in declaration — line 0, col 7
            session.AddRename("file:///renstruct.ffs", 0, 7, "Point");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            var changes = renResp?.GetObject("result")?.GetObject("changes");
            var edits = changes?.GetArray("file:///renstruct.ffs");
            // declaration + type usage in "var v: Vec2" = at least 2
            Assert(edits != null && edits.Count >= 2,
                $"DX5-18: ≥2 text edits for struct rename, got {edits?.Count ?? 0}");
        }

        // DX5-19: Rename enum name
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///renenum.ffs", source);
            // Rename "Color" — line 0, col 5 (0-based)
            session.AddRename("file:///renenum.ffs", 0, 5, "Hue");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            var changes = renResp?.GetObject("result")?.GetObject("changes");
            var edits = changes?.GetArray("file:///renenum.ffs");
            // at least the declaration
            Assert(edits != null && edits.Count >= 1,
                $"DX5-19: ≥1 text edit for enum rename, got {edits?.Count ?? 0}");
        }

        // DX5-20: Semantic tokens — struct decl + field tokens (type usage relies on TextMate)
        {
            string source = "struct Pos { x: int }\nfunc main() {\n  var p: Pos = Pos { x: 1 }\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///semuse.ffs", source);
            session.AddSemanticTokensFull("file:///semuse.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // struct decl name + field = 2 tokens × 5 = 10 ints
            Assert(data != null && data.Count >= 10,
                $"DX5-20: ≥10 data ints for struct decl + field, got {data?.Count ?? 0}");
        }

        // ============================================================
        // DX6: Include file rename — workspace/willRenameFiles
        // ============================================================

        // DX6-01: Basic rename — single include reference updated
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_basic_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "utils.ffs"), "func add(a: int, b: int): int { return a + b }");
                string mainSource = "include \"utils\"\nfunc main() {\n  var r: int = add(1, 2)\n  wait r\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";
                string oldFileUri = rootUri + "/utils.ffs";
                string newFileUri = rootUri + "/helpers.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(oldFileUri, newFileUri);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0); // initialize
                var renResp = session.ExpectResponse(1); // willRenameFiles
                var wsEdit = renResp?.GetObject("result");
                var changes = wsEdit?.GetObject("changes");
                Assert(changes != null, "DX6-01: WorkspaceEdit has changes");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 1,
                        $"DX6-01: 1 text edit for renamed include, got {edits?.Count ?? 0}");
                    if (edits != null && edits.Count > 0)
                    {
                        var edit = edits[0] as JsonObject;
                        string newText = edit?.GetString("newText");
                        Assert(newText == "helpers",
                            $"DX6-01: newText='helpers', got '{newText}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-02: Rename with include-as alias — include path updated, alias preserved
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_alias_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "math.ffs"), "func square(n: int): int { return n * n }");
                string mainSource = "include \"math\" as Math\nfunc main() {\n  var r: int = Math.square(3)\n  wait r\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/math.ffs", rootUri + "/arithmetic.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-02: WorkspaceEdit has changes");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 1,
                        $"DX6-02: 1 text edit for include-as rename, got {edits?.Count ?? 0}");
                    if (edits != null && edits.Count > 0)
                    {
                        var edit = edits[0] as JsonObject;
                        string newText = edit?.GetString("newText");
                        Assert(newText == "arithmetic",
                            $"DX6-02: newText='arithmetic', got '{newText}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-03: No-op — rename a file not referenced by any include
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_noop_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "unused.ffs"), "func noop(): int { return 0 }");
                string mainSource = "func main() {\n  wait 1\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/unused.ffs", rootUri + "/still_unused.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var wsEdit = renResp?.GetObject("result");
                // Should return empty object (no changes needed)
                var changes = wsEdit?.GetObject("changes");
                bool isEmpty = changes == null;
                Assert(isEmpty, "DX6-03: no changes for unreferenced file rename");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-04: Multiple includes in same file — both updated
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_multi_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 42 }");
                string mainSource = "include \"lib\"\ninclude \"lib\"\nfunc main() {\n  wait helper()\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/lib.ffs", rootUri + "/library.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-04: WorkspaceEdit has changes");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 2,
                        $"DX6-04: 2 text edits for duplicate includes, got {edits?.Count ?? 0}");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-05: Cross-file — rename updates includes in multiple files
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_cross_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "shared.ffs"), "func common(): int { return 1 }");
                string mainSource = "include \"shared\"\nfunc main() {\n  wait common()\n}";
                string otherSource = "include \"shared\"\nfunc other(): int { return common() + 1 }";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "other.ffs"), otherSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/shared.ffs", rootUri + "/base.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-05: WorkspaceEdit has changes");
                if (changes != null)
                {
                    // Both main.ffs and other.ffs should have edits
                    var mainEdits = changes.GetArray(mainUri);
                    string otherUri = rootUri + "/other.ffs";
                    var otherUriNormalized = LspServer.PathToFileUri(Path.Combine(tmpDir, "other.ffs"));
                    var otherEdits = changes.GetArray(otherUriNormalized);
                    int totalEdits = (mainEdits?.Count ?? 0) + (otherEdits?.Count ?? 0);
                    Assert(totalEdits == 2,
                        $"DX6-05: 2 total edits across files, got {totalEdits}");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-06: Subdirectory rename — include "sub/module" updated correctly
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_subdir_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string subDir = Path.Combine(tmpDir, "sub");
                Directory.CreateDirectory(subDir);
                File.WriteAllText(Path.Combine(subDir, "module.ffs"), "func subFunc(): int { return 7 }");
                string mainSource = "include \"sub/module\"\nfunc main() {\n  wait subFunc()\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";
                string oldFileUri = rootUri + "/sub/module.ffs";
                string newFileUri = rootUri + "/sub/component.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(oldFileUri, newFileUri);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-06: WorkspaceEdit has changes for subdir rename");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 1,
                        $"DX6-06: 1 edit for subdir include, got {edits?.Count ?? 0}");
                    if (edits != null && edits.Count > 0)
                    {
                        var edit = edits[0] as JsonObject;
                        string newText = edit?.GetString("newText");
                        Assert(newText == "sub/component",
                            $"DX6-06: newText='sub/component', got '{newText}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-07: Explicit .ffs extension in include — include "utils.ffs" → "helpers.ffs"
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_ext_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "utils.ffs"), "func tool(): int { return 5 }");
                string mainSource = "include \"utils.ffs\"\nfunc main() {\n  wait tool()\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/utils.ffs", rootUri + "/helpers.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-07: changes for .ffs extension include");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 1,
                        $"DX6-07: 1 edit for explicit .ffs include, got {edits?.Count ?? 0}");
                    if (edits != null && edits.Count > 0)
                    {
                        var edit = edits[0] as JsonObject;
                        string newText = edit?.GetString("newText");
                        Assert(newText == "helpers.ffs",
                            $"DX6-07: newText='helpers.ffs', got '{newText}'");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-08: No rootPath — returns empty (graceful fallback)
        {
            var session = new LspBatchSession();
            session.AddInitialize(); // no rootUri
            session.AddInitialized();
            session.AddWillRenameFiles("file:///old.ffs", "file:///new.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            var wsEdit = renResp?.GetObject("result");
            var changes = wsEdit?.GetObject("changes");
            Assert(changes == null, "DX6-08: no changes without rootPath");
        }

        // DX6-09: Text edit range — verify line/character positions are correct
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx6_range_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "old.ffs"), "func f(): int { return 1 }");
                // include on line 1 (0-indexed), with leading code on line 0
                string mainSource = "func setup(): int { return 0 }\ninclude \"old\"\nfunc main() {\n  wait f()\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
                File.WriteAllText(Path.Combine(tmpDir, "test.ffproj"), "{ \"includePaths\": [\".\"] }");

                string rootUri = "file:///" + tmpDir.Replace('\\', '/').TrimStart('/');
                string mainUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddWillRenameFiles(rootUri + "/old.ffs", rootUri + "/fresh.ffs");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var changes = renResp?.GetObject("result")?.GetObject("changes");
                Assert(changes != null, "DX6-09: changes present");
                if (changes != null)
                {
                    var edits = changes.GetArray(mainUri);
                    Assert(edits != null && edits.Count == 1, "DX6-09: 1 edit");
                    if (edits != null && edits.Count > 0)
                    {
                        var edit = edits[0] as JsonObject;
                        var range = edit?.GetObject("range");
                        var start = range?.GetObject("start");
                        int line = start?.GetInt("line") ?? -1;
                        int character = start?.GetInt("character") ?? -1;
                        // include "old" on line 1, col 0: 'include "' = 9 chars → path starts at col 9
                        Assert(line == 1 && character == 9,
                            $"DX6-09: edit at line=1, char=9, got line={line}, char={character}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX6-10: Capability advertised — initialize response includes workspace.fileOperations.willRename
        {
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var caps = initResp?.GetObject("result")?.GetObject("capabilities");
            var ws = caps?.GetObject("workspace");
            var fileOps = ws?.GetObject("fileOperations");
            var willRename = fileOps?.GetObject("willRename");
            Assert(willRename != null, "DX6-10: workspace.fileOperations.willRename capability advertised");
        }

        // ============================================================
        // DX7: AST precise position tracking (field access + type annotations)
        // ============================================================

        // DX7-01: FieldAccessExpr — references on v.x includes usage site
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  wait v.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7fa.ffs", source);
            // References on "x" in struct field declaration — line 0, col 14 (0-based)
            session.AddReferences("file:///dx7fa.ffs", 0, 14);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var refResp = session.ExpectResponse(1);
            var refs = refResp?.GetArray("result");
            // 1 decl (struct field) + 1 struct literal + 1 field access (v.x) = ≥3
            Assert(refs != null && refs.Count >= 3,
                $"DX7-01: ≥3 references for struct field (decl+literal+usage), got {refs?.Count ?? 0}");
        }

        // DX7-02: FieldAccessExpr — definition on v.x jumps to struct field
        {
            string source = "struct Pos { px: int; py: int }\nfunc main() {\n  var p: Pos = Pos { px: 10, py: 20 }\n  wait p.px\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7def.ffs", source);
            // Definition on "px" in "p.px" — line 3, col 9 (0-based: 'wait p.px', px starts at col 9)
            session.AddDefinition("file:///dx7def.ffs", 3, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var defResp = session.ExpectResponse(1);
            var result = defResp?.GetObject("result");
            Assert(result != null, "DX7-02: definition on field access (v.px) returns result");
        }

        // DX7-03: Semantic tokens — type annotation on local variable colored as struct
        {
            string source = "struct Color { r: int; g: int; b: int }\nfunc main() {\n  var c: Color = Color { r: 255, g: 0, b: 0 }\n  wait c.r\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7sem.ffs", source);
            session.AddSemanticTokensFull("file:///dx7sem.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // struct decl "Color" + 3 fields (r,g,b) + type annotation "Color" in var decl = ≥5 tokens × 5 = ≥25 ints
            Assert(data != null && data.Count >= 25,
                $"DX7-03: ≥25 data ints (struct decl + fields + type usage), got {data?.Count ?? 0}");
        }

        // DX7-04: Semantic tokens — parameter type annotation colored as struct
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc length(v: Vec2): int {\n  return v.x + v.y\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7param.ffs", source);
            session.AddSemanticTokensFull("file:///dx7param.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // struct "Vec2" decl + 2 fields + param type "Vec2" = 4 tokens × 5 = 20 ints
            Assert(data != null && data.Count >= 20,
                $"DX7-04: ≥20 data ints (struct + fields + param type), got {data?.Count ?? 0}");
        }

        // DX7-05: Semantic tokens — struct field type annotation (nested struct type)
        {
            string source = "struct Inner { val: int }\nstruct Outer { child: Inner }\nfunc main() {\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7nested.ffs", source);
            session.AddSemanticTokensFull("file:///dx7nested.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // "Inner" decl + "val" field + "Outer" decl + "child" field + "Inner" type annotation = 5 tokens × 5 = 25
            Assert(data != null && data.Count >= 25,
                $"DX7-05: ≥25 data ints (2 structs + 2 fields + 1 field type), got {data?.Count ?? 0}");
        }

        // DX7-06: Rename struct field — includes usage site v.x
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc main() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  wait v.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7ren.ffs", source);
            // Rename "x" in struct field declaration — line 0, col 14 (0-based)
            session.AddRename("file:///dx7ren.ffs", 0, 14, "px");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var renResp = session.ExpectResponse(1);
            var changes = renResp?.GetObject("result")?.GetObject("changes");
            var edits = changes?.GetArray("file:///dx7ren.ffs");
            // decl (struct x:) + struct literal x: + field access v.x = ≥3 edits
            Assert(edits != null && edits.Count >= 3,
                $"DX7-06: ≥3 text edits for field rename (decl+literal+access), got {edits?.Count ?? 0}");
        }

        // DX7-07: Semantic tokens — enum type annotation in variable
        {
            string source = "enum Dir { Up, Down, Left, Right }\nfunc main() {\n  var d: Dir = Dir.Up\n  wait 1\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7enum.ffs", source);
            session.AddSemanticTokensFull("file:///dx7enum.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            // enum "Dir" decl + 4 members + "Dir" type annotation in var = 6 tokens × 5 = 30 ints
            Assert(data != null && data.Count >= 30,
                $"DX7-07: ≥30 data ints (enum decl + members + type usage), got {data?.Count ?? 0}");
        }

        // DX7-08: prepareRename on field access usage site works
        {
            string source = "struct Pt { x: int; y: int }\nfunc main() {\n  var p: Pt = Pt { x: 1, y: 2 }\n  wait p.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx7prep.ffs", source);
            // prepareRename on "x" in "p.x" — line 3, col 9
            session.AddPrepareRename("file:///dx7prep.ffs", 3, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var prepResp = session.ExpectResponse(1);
            var result = prepResp?.GetObject("result");
            Assert(result != null, "DX7-08: prepareRename on field access returns result");
            if (result != null)
            {
                string placeholder = result.GetString("placeholder");
                Assert(placeholder == "x",
                    $"DX7-08: placeholder='x', got '{placeholder}'");
            }
        }

        // ================================================================
        // Q. DX-US: Usability Smoke Tests — per-requirement coverage
        //    Covers: include navigation, go-to-definition, hover (doc comments),
        //    findReferences (cross-file), signatureHelp (cross-file), rename,
        //    .ffproj includePaths navigation, hostDeclarations syscall hover.
        // ================================================================

        // US-01: Include file go-to-definition — clicking on include path opens target file
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "utils.ffs"), "func utilHelper(): int { return 1 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"utils\"\nfunc main() { var x: int = utilHelper() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Click on include path "utils" — line 0, col inside the path string
                session.AddDefinition(fileUri, 0, 10);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                Assert(defResp != null, "US-01a: definition response received for include path");
                var result = defResp?.GetObject("result");
                Assert(result != null, "US-01b: definition result not null (include navigation)");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("utils"),
                        $"US-01c: target URI references utils file, got '{defUri}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-02: Same-file go-to-definition — function, struct, enum, variable
        {
            string source =
                "struct Vec2 { x: int; y: int }\n" +    // line 0
                "enum Dir { UP, DOWN }\n" +               // line 1
                "func helper(): int { return 42 }\n" +    // line 2
                "func main() {\n" +                        // line 3
                "    var v: Vec2 = Vec2 { x: 1, y: 2 }\n" + // line 4
                "    var d: Dir = Dir.UP\n" +               // line 5
                "    var r: int = helper()\n" +             // line 6
                "}";                                        // line 7

            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///us02.ffs", source);
            // Go-to-definition on "helper()" call at line 6
            session.AddDefinition("file:///us02.ffs", 6, 20);
            // Go-to-definition on "Vec2" type usage at line 4 — "var v: Vec2" → Vec2 at col 11
            session.AddDefinition("file:///us02.ffs", 4, 11);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var defFunc = session.ExpectResponse(1);
            var defStruct = session.ExpectResponse(2);

            // Function definition
            var funcResult = defFunc?.GetObject("result");
            Assert(funcResult != null, "US-02a: same-file function go-to-definition returns result");
            if (funcResult != null)
            {
                var range = funcResult.GetObject("range");
                var start = range?.GetObject("start");
                Assert(start != null && start.GetInt("line") == 2,
                    $"US-02b: function definition at line 2, got {start?.GetInt("line")}");
            }

            // Struct definition
            var structResult = defStruct?.GetObject("result");
            Assert(structResult != null, "US-02c: same-file struct go-to-definition returns result");
            if (structResult != null)
            {
                var range = structResult.GetObject("range");
                var start = range?.GetObject("start");
                Assert(start != null && start.GetInt("line") == 0,
                    $"US-02d: struct definition at line 0, got {start?.GetInt("line")}");
            }
        }

        // US-03: Cross-file go-to-definition — function, struct, enum
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "types.ffs"),
                    "/// A 2D vector\nstruct Vec2 { x: int; y: int }\n/// Direction enum\nenum Dir { UP, DOWN }\nfunc makeVec(): Vec2 { return Vec2 { x: 0, y: 0 } }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"types\"\nfunc main() {\n    var v: Vec2 = makeVec()\n    var d: Dir = Dir.UP\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Go-to-definition on "makeVec()" call — line 2, col ~18
                session.AddDefinition(fileUri, 2, 18);
                // Go-to-definition on "Vec2" type annotation — line 2, "var v: Vec2" → col ~11
                session.AddDefinition(fileUri, 2, 11);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defFunc = session.ExpectResponse(1);
                var defStruct = session.ExpectResponse(2);

                // Cross-file function definition → types.ffs
                var funcResult = defFunc?.GetObject("result");
                Assert(funcResult != null, "US-03a: cross-file function definition result not null");
                if (funcResult != null)
                {
                    string defUri = funcResult.GetString("uri");
                    Assert(defUri != null && defUri.Contains("types.ffs"),
                        $"US-03b: function definition URI → types.ffs, got '{defUri}'");
                }

                // Cross-file struct definition → types.ffs
                var structResult = defStruct?.GetObject("result");
                Assert(structResult != null, "US-03c: cross-file struct definition result not null");
                if (structResult != null)
                {
                    string defUri = structResult.GetString("uri");
                    Assert(defUri != null && defUri.Contains("types.ffs"),
                        $"US-03d: struct definition URI → types.ffs, got '{defUri}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-04: Enum triple-slash doc hover at declaration site
        {
            string source = "/// The direction enum\nenum Dir { UP, DOWN }\nfunc main() { var d: int = Dir.UP }";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///us04.ffs", source);
            // Hover on "Dir" at declaration — line 1, col ~5 (inside "Dir")
            session.AddHover("file:///us04.ffs", 1, 5);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            var result = hoverResp?.GetObject("result");
            Assert(result != null, "US-04a: enum doc hover at declaration returns result");
            if (result != null)
            {
                var contents = result.GetObject("contents");
                string value = contents?.GetString("value") ?? "";
                Assert(value.Contains("enum Dir"), $"US-04b: hover shows 'enum Dir', got '{value}'");
                Assert(value.Contains("The direction enum"), $"US-04c: hover shows doc comment, got '{value}'");
            }
        }

        // US-05: Enum triple-slash doc hover at usage site (same file)
        {
            string source = "/// The direction enum\nenum Dir { UP, DOWN }\nfunc main() {\n    var d: int = Dir.UP\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///us05.ffs", source);
            // Hover on "Dir" in "Dir.UP" expression — line 3, col ~17 (the "Dir" part of "Dir.UP")
            session.AddHover("file:///us05.ffs", 3, 17);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            var result = hoverResp?.GetObject("result");
            Assert(result != null, "US-05a: enum doc hover at usage site returns result");
            if (result != null)
            {
                var contents = result.GetObject("contents");
                string value = contents?.GetString("value") ?? "";
                Assert(value.Contains("Dir"), $"US-05b: hover shows 'Dir', got '{value}'");
                Assert(value.Contains("The direction enum"), $"US-05c: hover shows doc comment at usage site, got '{value}'");
            }
        }

        // US-06: Struct triple-slash doc hover at declaration site
        {
            string source = "/// A 2D vector type\nstruct Vec2 { x: int; y: int }\nfunc main() { var v: Vec2 = Vec2 { x: 0, y: 0 } }";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///us06.ffs", source);
            // Hover on "Vec2" at struct declaration — line 1, col ~7 (the "struct Vec2" keyword)
            session.AddHover("file:///us06.ffs", 1, 9);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            var result = hoverResp?.GetObject("result");
            Assert(result != null, "US-06a: struct doc hover at declaration returns result");
            if (result != null)
            {
                var contents = result.GetObject("contents");
                string value = contents?.GetString("value") ?? "";
                Assert(value.Contains("struct Vec2"), $"US-06b: hover shows 'struct Vec2', got '{value}'");
                Assert(value.Contains("A 2D vector type"), $"US-06c: hover shows doc comment, got '{value}'");
            }
        }

        // US-07: Struct triple-slash doc hover at usage site (same file)
        {
            string source = "/// A 2D vector type\nstruct Vec2 { x: int; y: int }\nfunc main() {\n    var v: Vec2 = Vec2 { x: 0, y: 0 }\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///us07.ffs", source);
            // Hover on "Vec2" in struct literal "Vec2 { x: 0, y: 0 }" — line 3
            // "    var v: Vec2 = Vec2 { x: 0, y: 0 }" → the second "Vec2" is a CallExpr or IdentifierExpr
            // The type annotation "Vec2" after colon starts at col 11
            // We try the struct literal name "Vec2" at col 18
            session.AddHover("file:///us07.ffs", 3, 18);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            var initResp = session.ExpectResponse(0);
            var hoverResp = session.ExpectResponse(1);
            var result = hoverResp?.GetObject("result");
            Assert(result != null, "US-07a: struct doc hover at usage site returns result");
            if (result != null)
            {
                var contents = result.GetObject("contents");
                string value = contents?.GetString("value") ?? "";
                Assert(value.Contains("Vec2"), $"US-07b: hover shows 'Vec2', got '{value}'");
            }
        }

        // US-08: Struct triple-slash doc hover at usage site (cross-file)
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "types.ffs"),
                    "/// A 2D vector type\nstruct Vec2 { x: int; y: int }\nfunc makeVec(): Vec2 { return Vec2 { x: 0, y: 0 } }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"types\"\nfunc main() {\n    var v: Vec2 = makeVec()\n    var a: int = v.x\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Hover on "makeVec()" — line 2, col ~18
                session.AddHover(fileUri, 2, 18);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var hoverResp = session.ExpectResponse(1);
                var result = hoverResp?.GetObject("result");
                Assert(result != null, "US-08a: cross-file hover on function from included file returns result");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value") ?? "";
                    Assert(value.Contains("makeVec"), $"US-08b: hover shows function name, got '{value}'");
                    Assert(value.Contains("Vec2"), $"US-08c: hover shows return type Vec2, got '{value}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-09: Cross-file findReferences for function — finds decl + all call sites across files
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"),
                    "func helper(): int { return 42 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"lib\"\nfunc main() {\n    var a: int = helper()\n    var b: int = helper()\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // References on "helper()" call — line 2, col ~17
                session.AddReferences(fileUri, 2, 17);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var refsResp = session.ExpectResponse(1);
                var refs = refsResp?.GetArray("result");
                Assert(refs != null && refs.Count >= 3,
                    $"US-09: cross-file function references ≥3 (1 decl in lib + 2 calls in main), got {refs?.Count ?? 0}");

                // Verify references span multiple files (at least one in lib.ffs, at least one in main.ffs)
                bool hasLib = false, hasMain = false;
                if (refs != null)
                {
                    foreach (var r in refs)
                    {
                        var rObj = r as JsonObject;
                        string rUri = rObj?.GetString("uri") ?? "";
                        if (rUri.Contains("lib.ffs")) hasLib = true;
                        if (rUri.Contains("main.ffs")) hasMain = true;
                    }
                }
                Assert(hasLib, "US-09b: references include declaration in lib.ffs");
                Assert(hasMain, "US-09c: references include call sites in main.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-10: Cross-file findReferences for struct — finds decl + type usages across files
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "types.ffs"),
                    "struct Point { x: int; y: int }\nfunc makePoint(): Point { return Point { x: 0, y: 0 } }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"types\"\nfunc main() {\n    var p: Point = makePoint()\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // References on "Point" struct name — line 2, "var p: Point" → col ~11
                session.AddReferences(fileUri, 2, 11);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var refsResp = session.ExpectResponse(1);
                var refs = refsResp?.GetArray("result");
                Assert(refs != null && refs.Count >= 2,
                    $"US-10: cross-file struct references ≥2 (decl in types + usage in main), got {refs?.Count ?? 0}");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-11: Cross-file findReferences for enum
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "defs.ffs"),
                    "enum Color { RED, GREEN, BLUE }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"defs\"\nfunc main() {\n    var c: int = Color.RED\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // References on "Color" enum usage — line 2, col in "Color.RED" → "Color" at ~17
                session.AddReferences(fileUri, 2, 17);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var refsResp = session.ExpectResponse(1);
                var refs = refsResp?.GetArray("result");
                Assert(refs != null && refs.Count >= 1,
                    $"US-11: cross-file enum references ≥1 (decl in defs), got {refs?.Count ?? 0}");

                // Verify the declaration in defs.ffs is included
                bool hasDefs = false;
                if (refs != null)
                {
                    foreach (var r in refs)
                    {
                        var rObj = r as JsonObject;
                        string rUri = rObj?.GetString("uri") ?? "";
                        if (rUri.Contains("defs.ffs")) hasDefs = true;
                    }
                }
                Assert(hasDefs, "US-11b: enum references include declaration in defs.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-12: Function triple-slash hover cross-file shows full params (not "...")
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "math.ffs"),
                    "/// Adds two numbers\n/// @param a — first number\n/// @param b — second number\n/// @return the sum\nfunc add(a: int, b: int): int { return a + b }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"math\"\nfunc main() { var r: int = add(1, 2) }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Hover on "add(1, 2)" call — line 1, col ~28 (on "add")
                session.AddHover(fileUri, 1, 28);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var hoverResp = session.ExpectResponse(1);
                var result = hoverResp?.GetObject("result");
                Assert(result != null, "US-12a: cross-file function hover returns result");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value") ?? "";
                    Assert(value.Contains("add"), $"US-12b: hover shows 'add', got '{value}'");
                    Assert(value.Contains("a: int"), $"US-12c: hover shows param 'a: int' (not '...'), got '{value}'");
                    Assert(value.Contains("b: int"), $"US-12d: hover shows param 'b: int' (not '...'), got '{value}'");
                    Assert(!value.Contains("(...)"), $"US-12e: hover does NOT show '(...)' fallback, got '{value}'");
                    Assert(value.Contains("Adds two numbers"), $"US-12f: hover shows doc comment, got '{value}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-13: Function signature help cross-file shows params (not "...")
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "math.ffs"),
                    "func multiply(x: int, y: int): int { return x * y }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"math\"\nfunc main() { var r: int = multiply(3, 4) }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Signature help on "multiply(" — line 1, col ~37 (after opening paren)
                session.AddSignatureHelp(fileUri, 1, 37);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var sigResp = session.ExpectResponse(1);
                var result = sigResp?.GetObject("result");
                Assert(result != null, "US-13a: cross-file signature help returns result");
                if (result != null)
                {
                    var signatures = result.GetArray("signatures");
                    Assert(signatures != null && signatures.Count > 0,
                        "US-13b: signature help has signatures");
                    if (signatures != null && signatures.Count > 0)
                    {
                        var sig = signatures[0] as JsonObject;
                        string label = sig?.GetString("label") ?? "";
                        Assert(label.Contains("x: int"), $"US-13c: signature shows 'x: int', got '{label}'");
                        Assert(label.Contains("y: int"), $"US-13d: signature shows 'y: int', got '{label}'");
                        Assert(!label.Contains("..."), $"US-13e: signature does NOT show '...' fallback, got '{label}'");

                        // Check parameters array exists with correct count
                        var parameters2 = sig?.GetArray("parameters");
                        Assert(parameters2 != null && parameters2.Count == 2,
                            $"US-13f: signature has 2 parameters, got {parameters2?.Count ?? 0}");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-14: Cross-file struct field go-to-definition
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "types.ffs"),
                    "struct Point { px: int; py: int }\nfunc makePoint(): Point { return Point { px: 0, py: 0 } }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"types\"\nfunc main() {\n    var p: Point = makePoint()\n    var a: int = p.px\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Go-to-definition on "px" in "p.px" — line 3, col ~19 (on "px")
                session.AddDefinition(fileUri, 3, 19);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "US-14a: cross-file struct field definition result not null");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("types.ffs"),
                        $"US-14b: struct field definition URI → types.ffs, got '{defUri}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-15: Cross-file enum member go-to-definition
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "defs.ffs"),
                    "enum Color { RED, GREEN, BLUE }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"defs\"\nfunc main() {\n    var c: int = Color.RED\n}";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Go-to-definition on "RED" in "Color.RED" — line 2
                // "    var c: int = Color.RED" → "Color" at ~17, "." at ~22, "RED" at ~23
                session.AddDefinition(fileUri, 2, 23);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "US-15a: cross-file enum member definition result not null");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("defs.ffs"),
                        $"US-15b: enum member definition URI → defs.ffs, got '{defUri}'");
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-16: Cross-file rename function updates both files
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"),
                    "func oldName(): int { return 42 }");
                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"lib\"\nfunc main() { var r: int = oldName() }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Rename "oldName" call at line 1, col ~33
                session.AddRename(fileUri, 1, 33, "newName");
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var renResp = session.ExpectResponse(1);
                var result = renResp?.GetObject("result");
                Assert(result != null, "US-16a: cross-file rename result not null");
                if (result != null)
                {
                    var changes = result.GetObject("changes");
                    Assert(changes != null, "US-16b: rename WorkspaceEdit has changes");
                    if (changes != null)
                    {
                        // Check that changes span multiple URIs (both lib and main)
                        int totalEdits = 0;
                        bool hasLib = false, hasMain = false;
                        foreach (var key in changes.Keys)
                        {
                            var edits = changes.GetArray(key);
                            if (edits != null) totalEdits += edits.Count;
                            if (key.Contains("lib")) hasLib = true;
                            if (key.Contains("main")) hasMain = true;
                        }
                        Assert(totalEdits >= 2,
                            $"US-16c: ≥2 total edits for cross-file rename, got {totalEdits}");
                        Assert(hasLib && hasMain,
                            "US-16d: rename edits span both lib.ffs and main.ffs");
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-17: .ffproj includePaths — include from non-root directory resolves and navigates
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string modulesDir = Path.Combine(tmpDir, "modules");
                Directory.CreateDirectory(modulesDir);
                File.WriteAllText(Path.Combine(modulesDir, "math.ffs"),
                    "func add(a: int, b: int): int { return a + b }");
                string projJson = "{ \"includePaths\": [\"modules\"] }";
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), projJson);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "include \"math\"\nfunc main() { var r: int = add(1, 2) }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Verify no include-not-found diagnostics
                // Also test definition on "add" call — line 1, col ~28
                session.AddDefinition(fileUri, 1, 28);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var defResp = session.ExpectResponse(1);
                var result = defResp?.GetObject("result");
                Assert(result != null, "US-17a: .ffproj includePaths → definition on included function resolves");
                if (result != null)
                {
                    string defUri = result.GetString("uri");
                    Assert(defUri != null && defUri.Contains("math"),
                        $"US-17b: definition URI → math.ffs, got '{defUri}'");
                }

                // Check diagnostics for no include errors
                var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasIncludeError = false;
                foreach (var diag in diags)
                {
                    var diagParams = diag.GetObject("params");
                    var diagArr = diagParams?.GetArray("diagnostics");
                    if (diagArr != null)
                    {
                        foreach (var d in diagArr)
                        {
                            var dObj = d as JsonObject;
                            string msg = dObj?.GetString("message") ?? "";
                            if (msg.Contains("include") || msg.Contains("not found"))
                                hasIncludeError = true;
                        }
                    }
                }
                Assert(!hasIncludeError, "US-17c: no include-related errors with .ffproj includePaths");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // US-18: .ffproj hostDeclarations — syscall hover shows signature
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "us_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                string hostDir = Path.Combine(tmpDir, "host");
                Directory.CreateDirectory(hostDir);
                string declJson = "{ \"syscalls\": [ { \"name\": \"PlayAnim\", \"slot\": 0, \"parameters\": [{ \"name\": \"animId\", \"type\": \"int\" }], \"returnType\": \"void\", \"description\": \"Play an animation\" } ] }";
                File.WriteAllText(Path.Combine(hostDir, "host.ffvm.d.json"), declJson);
                string projJson = "{ \"hostDeclarations\": [\"host/host.ffvm.d.json\"] }";
                File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), projJson);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string source = "func main() { PlayAnim(1) }";
                string fileUri = rootUri + "/main.ffs";

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(fileUri, source);
                // Hover on "PlayAnim" — line 0, col ~15
                session.AddHover(fileUri, 0, 15);
                // Also verify no "unknown syscall" diagnostic
                session.AddShutdown();
                session.AddExit();
                session.Run();

                var initResp = session.ExpectResponse(0);
                var hoverResp = session.ExpectResponse(1);
                var result = hoverResp?.GetObject("result");
                Assert(result != null, "US-18a: syscall hover via .ffproj hostDeclarations returns result");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value") ?? "";
                    Assert(value.Contains("PlayAnim"), $"US-18b: hover shows 'PlayAnim', got '{value}'");
                }

                // Check no "unknown syscall" diagnostic
                var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasUnknownSyscall = false;
                foreach (var diag in diags)
                {
                    var diagParams = diag.GetObject("params");
                    var diagArr = diagParams?.GetArray("diagnostics");
                    if (diagArr != null)
                    {
                        foreach (var d in diagArr)
                        {
                            var dObj = d as JsonObject;
                            string msg = dObj?.GetString("message") ?? "";
                            if (msg.Contains("unknown") && msg.Contains("syscall"))
                                hasUnknownSyscall = true;
                        }
                    }
                }
                Assert(!hasUnknownSyscall, "US-18c: no 'unknown syscall' diagnostic with .ffproj hostDeclarations");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ===== DX8: external func LSP tests =====

        // DX8-01: external func completion
        {
            string source = "external func SetHitbox(id: int, x: int, y: int, w: int, h: int)\nfunc entry() {\n  Set\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddCompletion("file:///test.ffs", 2, 5); // cursor after "Set"
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var compResp = session.ExpectResponse(1); // completion
            Assert(compResp != null, "DX8-01a: completion response received");
            if (compResp != null)
            {
                var items = compResp.GetArray("result");
                bool foundSetHitbox = false;
                if (items != null)
                {
                    foreach (var obj in items)
                    {
                        var item = obj as JsonObject;
                        if (item?.GetString("label") == "SetHitbox")
                        {
                            foundSetHitbox = true;
                            string detail = item.GetString("detail") ?? "";
                            Assert(detail.Contains("external func"), $"DX8-01b: completion detail shows 'external func', got '{detail}'");
                            break;
                        }
                    }
                }
                Assert(foundSetHitbox, "DX8-01c: SetHitbox appears in completion");
            }
        }

        // DX8-02: external func hover
        {
            string source = "external func SetHitbox(id: int, x: int, y: int, w: int, h: int): int";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddHover("file:///test.ffs", 0, 20); // hover over function name "SetHitbox"
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var hoverResp = session.ExpectResponse(1); // hover
            Assert(hoverResp != null, "DX8-02a: hover response received");
            if (hoverResp != null)
            {
                var result = hoverResp.GetObject("result");
                if (result != null)
                {
                    var contents = result.GetObject("contents");
                    string value = contents?.GetString("value") ?? "";
                    Assert(value.Contains("external func"), $"DX8-02b: hover shows 'external func', got '{value}'");
                    Assert(value.Contains("SetHitbox"), $"DX8-02c: hover shows 'SetHitbox', got '{value}'");
                    Assert(value.Contains("id: int"), $"DX8-02d: hover shows params, got '{value}'");
                }
            }
        }

        // DX8-03: external func signature help
        {
            string source = "external func SetHitbox(id: int, x: int, y: int)\nfunc entry() {\n  SetHitbox(\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddSignatureHelp("file:///test.ffs", 2, 12); // cursor inside SetHitbox(
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var sigResp = session.ExpectResponse(1); // signature help
            Assert(sigResp != null, "DX8-03a: signature help response received");
            if (sigResp != null)
            {
                var result = sigResp.GetObject("result");
                Assert(result != null, "DX8-03b: signature help result not null");
                if (result != null)
                {
                    var sigs = result.GetArray("signatures");
                    Assert(sigs != null && sigs.Count > 0, "DX8-03c: has signature");
                    if (sigs != null && sigs.Count > 0)
                    {
                        var sig = sigs[0] as JsonObject;
                        string label = sig?.GetString("label") ?? "";
                        Assert(label.Contains("id: int"), $"DX8-03d: signature shows params, got '{label}'");
                    }
                }
            }
        }

        // DX8-04: external func no diagnostic for calls (diagnostic-only mode)
        {
            string source = "external func PlayAnim(id: int)\nfunc entry() {\n  PlayAnim(1)\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            // Check no diagnostics about "Unknown function"
            var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
            bool hasUnknown = false;
            foreach (var diag in diags)
            {
                var diagParams = diag.GetObject("params");
                var diagArr = diagParams?.GetArray("diagnostics");
                if (diagArr != null)
                {
                    foreach (var d in diagArr)
                    {
                        var dObj = d as JsonObject;
                        string msg = dObj?.GetString("message") ?? "";
                        if (msg.Contains("Unknown function") && msg.Contains("PlayAnim"))
                            hasUnknown = true;
                    }
                }
            }
            Assert(!hasUnknown, "DX8-04: no 'Unknown function' diagnostic for external func");
        }

        // DX8-05: cross-file error line fix
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "dx8_test_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(tmpDir);
            try
            {
                // lib.ffs references a non-existent function at line 2
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"),
                    "func helper() {\n  NonExistentFunc()\n}");
                // main.ffs includes lib.ffs — error is in lib.ffs, not main.ffs
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"),
                    "include \"lib.ffs\"\nfunc entry() {\n  helper()\n}");

                string rootUri = "file:///" + tmpDir.Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";
                string mainSource = File.ReadAllText(Path.Combine(tmpDir, "main.ffs"));

                var session = new LspBatchSession();
                session.AddInitializeWithRootUri(rootUri);
                session.AddInitialized();
                session.AddDidOpen(mainUri, mainSource);
                session.AddShutdown();
                session.AddExit();
                session.Run();

                session.ExpectResponse(0); // initialize
                // Check that the "Unknown function" error from lib.ffs is not shown in main.ffs
                var diags = session.FindAllNotifications("textDocument/publishDiagnostics");
                bool hasNonExistent = false;
                foreach (var diag in diags)
                {
                    var diagParams = diag.GetObject("params");
                    string diagUri = diagParams?.GetString("uri") ?? "";
                    if (!diagUri.Contains("main.ffs")) continue;
                    var diagArr = diagParams?.GetArray("diagnostics");
                    if (diagArr != null)
                    {
                        foreach (var d in diagArr)
                        {
                            var dObj = d as JsonObject;
                            string msg = dObj?.GetString("message") ?? "";
                            if (msg.Contains("NonExistentFunc"))
                                hasNonExistent = true;
                        }
                    }
                }
                Assert(!hasNonExistent, "DX8-05: cross-file error is not shown in main.ffs diagnostics");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // DX8-06: semantic tokens for enum member access
        {
            string source = "enum Color { RED, GREEN, BLUE }\nfunc entry() {\n  var c: int = Color.RED\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddSemanticTokensFull("file:///test.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var semResp = session.ExpectResponse(1); // semantic tokens
            Assert(semResp != null, "DX8-06a: semantic tokens response received");
            if (semResp != null)
            {
                var result = semResp.GetObject("result");
                var data = result?.GetArray("data");
                Assert(data != null && data.Count > 0, "DX8-06b: semantic tokens has data");
                // Look for enum member token (type=3) in the data
                bool hasEnumMemberToken = false;
                if (data != null)
                {
                    for (int i = 0; i + 4 < data.Count; i += 5)
                    {
                        int tokenType = Convert.ToInt32(data[i + 3]);
                        if (tokenType == 3) { hasEnumMemberToken = true; break; }
                    }
                }
                Assert(hasEnumMemberToken, "DX8-06c: semantic tokens include enumMember token for Color.RED");
            }
        }

        // DX8-07: semantic tokens for field access
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc entry() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  var a: int = v.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///test.ffs", source);
            session.AddSemanticTokensFull("file:///test.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0); // initialize
            var semResp = session.ExpectResponse(1); // semantic tokens
            Assert(semResp != null, "DX8-07a: semantic tokens response received");
            if (semResp != null)
            {
                var result = semResp.GetObject("result");
                var data = result?.GetArray("data");
                Assert(data != null && data.Count > 0, "DX8-07b: semantic tokens has data");
                // Look for property token (type=4) for field access
                int propertyCount = 0;
                if (data != null)
                {
                    for (int i = 0; i + 4 < data.Count; i += 5)
                    {
                        int tokenType = Convert.ToInt32(data[i + 3]);
                        if (tokenType == 4) propertyCount++;
                    }
                }
                // At least 3 property tokens: x, y in struct decl + x in v.x field access
                Assert(propertyCount >= 3, $"DX8-07c: ≥3 property tokens (struct fields + field access), got {propertyCount}");
            }
        }

        // DX8-08: IsCrossFileError unit test
        {
            Assert(LspServer.IsCrossFileError("[lib.ffs] Unknown function 'Foo' (line 5)", "main.ffs"),
                "DX8-08a: cross-file error detected");
            Assert(!LspServer.IsCrossFileError("[main.ffs] Unknown function 'Foo' (line 5)", "main.ffs"),
                "DX8-08b: same-file error not filtered");
            Assert(!LspServer.IsCrossFileError("Unknown function 'Foo' (line 5)", "main.ffs"),
                "DX8-08c: untagged error not filtered");
        }

        // ============================================================
        // DX9: Semantic token coloring improvements
        // ============================================================

        // DX9-01: 'external' keyword gets keyword token (type=9)
        {
            string source = "external func SetPos(x: int, y: int): int";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9ext.ffs", source);
            session.AddSemanticTokensFull("file:///dx9ext.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-01a: semantic tokens has data");
            // First token should be 'external' keyword (type=9)
            bool hasKeyword = false;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 9) { hasKeyword = true; break; }
                }
            }
            Assert(hasKeyword, "DX9-01b: semantic tokens include keyword token for 'external'");
        }

        // DX9-02: parameter names get parameter token (type=7)
        {
            string source = "func add(a: int, b: int): int {\n  return a + b\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9param.ffs", source);
            session.AddSemanticTokensFull("file:///dx9param.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-02a: semantic tokens has data");
            int paramCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 7) paramCount++;
                }
            }
            // Should have 2 parameter tokens: 'a' and 'b' in function definition
            Assert(paramCount == 2, $"DX9-02b: 2 parameter tokens (a, b), got {paramCount}");
        }

        // DX9-03: local variable declarations get variable token (type=5)
        {
            string source = "func entry() {\n  var x: int = 10\n  const y: int = 20\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9var.ffs", source);
            session.AddSemanticTokensFull("file:///dx9var.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-03a: semantic tokens has data");
            int varCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 5) varCount++;
                }
            }
            // Should have 2 variable tokens: 'x' and 'y' declarations
            Assert(varCount == 2, $"DX9-03b: 2 variable tokens (x, y decl), got {varCount}");
        }

        // DX9-04: module-level variable declarations get variable token (type=5)
        {
            string source = "var g: int = 42\nconst PI: int = 3\nfunc entry() {\n  var x: int = g + PI\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9mod.ffs", source);
            session.AddSemanticTokensFull("file:///dx9mod.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-04a: semantic tokens has data");
            int varCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 5) varCount++;
                }
            }
            // g(decl) + PI(decl) + x(decl) + g(ref) + PI(ref) = 5 variable tokens
            Assert(varCount == 5, $"DX9-04b: 5 variable tokens (g,PI decls + x decl + g,PI refs), got {varCount}");
        }

        // DX9-05: variable references in expressions (identifier → variable token)
        {
            string source = "func calc(n: int): int {\n  var result: int = n * 2\n  return result\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9ref.ffs", source);
            session.AddSemanticTokensFull("file:///dx9ref.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-05a: semantic tokens has data");
            int varCount = 0;
            int paramCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 5) varCount++;
                    if (tokenType == 7) paramCount++;
                }
            }
            // param: n(decl)=1, var: result(decl)=1 + n(ref in expr)=1 + result(ref in return)=1 = 3
            Assert(paramCount == 1, $"DX9-05b: 1 parameter token (n), got {paramCount}");
            Assert(varCount == 3, $"DX9-05c: 3 variable tokens (result decl + n ref + result ref), got {varCount}");
        }

        // DX9-06: dot-access target gets variable token + field gets property token
        {
            string source = "struct Vec2 { x: int; y: int }\nfunc entry() {\n  var v: Vec2 = Vec2 { x: 1, y: 2 }\n  var a: int = v.x\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9dot.ffs", source);
            session.AddSemanticTokensFull("file:///dx9dot.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-06a: semantic tokens has data");
            int varCount = 0;
            int propCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 5) varCount++;
                    if (tokenType == 4) propCount++;
                }
            }
            // var tokens: v(decl) + a(decl) + v(ref in v.x) = 3
            Assert(varCount == 3, $"DX9-06b: 3 variable tokens (v decl, a decl, v ref), got {varCount}");
            // property tokens: x(decl) + y(decl) + x(access in v.x) = 3
            Assert(propCount >= 3, $"DX9-06c: ≥3 property tokens (field decls + access), got {propCount}");
        }

        // DX9-07: chained dot access (a.b.c) — all parts colored
        {
            string source = "struct Inner { val: int }\nstruct Outer { child: Inner }\nfunc entry() {\n  var o: Outer = Outer { child: Inner { val: 5 } }\n  var r: int = o.child.val\n}";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9chain.ffs", source);
            session.AddSemanticTokensFull("file:///dx9chain.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-07a: semantic tokens has data");
            int varCount = 0;
            int propCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 5) varCount++;
                    if (tokenType == 4) propCount++;
                }
            }
            // var tokens: o(decl) + r(decl) + o(ref in chain) = 3
            Assert(varCount == 3, $"DX9-07b: 3 variable tokens (o decl, r decl, o ref), got {varCount}");
            // property tokens: val(decl) + child(decl) + child(access) + val(access) = 4
            Assert(propCount >= 4, $"DX9-07c: ≥4 property tokens (2 field decls + 2 field accesses), got {propCount}");
        }

        // DX9-08: external func parameter names also get parameter token
        {
            string source = "external func Move(dx: int, dy: int): int";
            var session = new LspBatchSession();
            session.AddInitialize();
            session.AddInitialized();
            session.AddDidOpen("file:///dx9extparam.ffs", source);
            session.AddSemanticTokensFull("file:///dx9extparam.ffs");
            session.AddShutdown();
            session.AddExit();
            session.Run();

            session.ExpectResponse(0);
            var semResp = session.ExpectResponse(1);
            var data = semResp?.GetObject("result")?.GetArray("data");
            Assert(data != null && data.Count > 0, "DX9-08a: semantic tokens has data");
            int keywordCount = 0;
            int paramCount = 0;
            if (data != null)
            {
                for (int i = 0; i + 4 < data.Count; i += 5)
                {
                    int tokenType = Convert.ToInt32(data[i + 3]);
                    if (tokenType == 9) keywordCount++;
                    if (tokenType == 7) paramCount++;
                }
            }
            Assert(keywordCount == 1, $"DX9-08b: 1 keyword token (external), got {keywordCount}");
            Assert(paramCount == 2, $"DX9-08c: 2 parameter tokens (dx, dy), got {paramCount}");
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

        /// <summary>
        /// DX4-P0: Initialize with rootUri for workspace-aware tests.
        /// </summary>
        public void AddInitializeWithRootUri(string rootUri)
        {
            var parameters = new JsonObject();
            var caps = new JsonObject();
            parameters.Set("capabilities", caps);
            parameters.Set("rootUri", rootUri);
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
        /// DX5: Add a textDocument/rename request.
        /// </summary>
        public void AddRename(string uri, int line, int character, string newName)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            parameters.Set("newName", newName);
            AddRequest("textDocument/rename", parameters);
        }

        /// <summary>
        /// DX5: Add a textDocument/prepareRename request.
        /// </summary>
        public void AddPrepareRename(string uri, int line, int character)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            var pos = new JsonObject();
            pos.Set("line", line);
            pos.Set("character", character);
            parameters.Set("position", pos);
            AddRequest("textDocument/prepareRename", parameters);
        }

        /// <summary>
        /// DX5: Add a textDocument/semanticTokens/full request.
        /// </summary>
        public void AddSemanticTokensFull(string uri)
        {
            var parameters = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", uri);
            parameters.Set("textDocument", textDoc);
            AddRequest("textDocument/semanticTokens/full", parameters);
        }

        /// <summary>
        /// DX6: Add a workspace/willRenameFiles request with one file rename.
        /// </summary>
        public void AddWillRenameFiles(string oldUri, string newUri)
        {
            var parameters = new JsonObject();
            var fileRename = new JsonObject();
            fileRename.Set("oldUri", oldUri);
            fileRename.Set("newUri", newUri);
            var files = new List<object> { fileRename };
            parameters.Set("files", files);
            AddRequest("workspace/willRenameFiles", parameters);
        }

        /// <summary>
        /// DX6: Add a workspace/willRenameFiles request with multiple file renames.
        /// </summary>
        public void AddWillRenameFilesMulti(List<(string oldUri, string newUri)> renames)
        {
            var parameters = new JsonObject();
            var files = new List<object>();
            foreach (var (oldUri, newUri) in renames)
            {
                var fileRename = new JsonObject();
                fileRename.Set("oldUri", oldUri);
                fileRename.Set("newUri", newUri);
                files.Add(fileRename);
            }
            parameters.Set("files", files);
            AddRequest("workspace/willRenameFiles", parameters);
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

        /// <summary>
        /// DX4-P4: Queue a client response to a server-initiated request.
        /// </summary>
        public void AddResponse(int id, JsonObject result)
        {
            var resp = new JsonObject();
            resp.Set("jsonrpc", "2.0");
            resp.Set("id", id);
            resp.Set("result", result != null ? (object)result : null);
            ContentLengthStream.WriteMessage(_inputMs, resp.ToJson());
        }

        /// <summary>
        /// DX4-P4: Find a server-sent request by method name in output messages.
        /// Returns the first matching request (has id + method).
        /// </summary>
        public JsonObject FindRequest(string method)
        {
            if (_messages == null) return null;
            foreach (var msg in _messages)
            {
                if (msg.ContainsKey("id") && msg.GetString("method") == method)
                    return msg;
            }
            return null;
        }

        /// <summary>
        /// DX4-P4: Find all server-sent requests by method name in output messages.
        /// </summary>
        public List<JsonObject> FindAllRequests(string method)
        {
            var result = new List<JsonObject>();
            if (_messages == null) return result;
            foreach (var msg in _messages)
            {
                if (msg.ContainsKey("id") && msg.GetString("method") == method)
                    result.Add(msg);
            }
            return result;
        }
    }
}
