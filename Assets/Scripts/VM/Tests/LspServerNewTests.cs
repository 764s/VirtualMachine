using System;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using FFVM.Debug;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Integration.VsCode;
using FFVM.Debug.Lsp.Protocol;
using UnityEngine;

/// <summary>
/// LspServerNew protocol + bridge smoke tests.
///
/// Priority goal:
/// 1) prove server request/notification loop works for real user-facing flows.
/// 2) keep a small regression harness for long-running multi-turn evolution.
/// </summary>
public static class LspServerNewTests
{
#if UNITY_EDITOR
    [UnityEditor.MenuItem("TestVM/RunLspServerNewTests")]
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
        // LSPNEW-01: initialize response contains capability payload
        // ================================================================
        {
            var session = new LspServerNewBatchSession();
            int initializeId = session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(new NoOpLspVsCodeDatabaseBridge());

            JsonObject initializeResponse = session.FindResponse(initializeId);
            JsonObject result = initializeResponse != null ? initializeResponse.GetObject(JsonRpcFields.Result) : null;
            JsonObject capabilities = result != null ? result.GetObject(LspFields.Capabilities) : null;

            Assert(
                initializeResponse != null
                && capabilities != null,
                "LSPNEW-01: initialize returns capabilities envelope");
        }

        // ================================================================
        // LSPNEW-02: unknown request returns MethodNotFound
        // ================================================================
        {
            var session = new LspServerNewBatchSession();
            int unknownId = session.AddRequest("workspace/unknownFeature", new JsonObject());
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(new NoOpLspVsCodeDatabaseBridge());

            JsonObject response = session.FindResponse(unknownId);
            JsonObject error = response != null ? response.GetObject(JsonRpcFields.Error) : null;
            int code = error != null ? error.GetInt(JsonRpcFields.Code, 0) : 0;

            Assert(
                response != null
                && error != null
                && code == JsonRpcErrorCodes.MethodNotFound,
                "LSPNEW-02: unknown method returns JSON-RPC MethodNotFound");
        }

        // ================================================================
        // LSPNEW-03: didOpen invalid source publishes diagnostics
        // ================================================================
        {
            string uri = "file:///tests/lspnew_diag.ffs";
            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, "func broken("));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject notification = session.FindFirstNotification(LspMethods.PublishDiagnostics);
            JsonObject payload = notification != null ? notification.GetObject(JsonRpcFields.Params) : null;
            string publishedUri = payload != null ? payload.GetString(LspFields.Uri) : string.Empty;
            List<object> diagnostics = payload != null ? payload.GetArray(LspFields.Diagnostics) : null;

            Assert(
                notification != null
                && string.Equals(publishedUri, uri, StringComparison.OrdinalIgnoreCase)
                && diagnostics != null
                && diagnostics.Count > 0,
                "LSPNEW-03: invalid didOpen triggers publishDiagnostics with entries");
        }

        // ================================================================
        // LSPNEW-04: didChange valid source clears diagnostics
        // ================================================================
        {
            string uri = "file:///tests/lspnew_change.ffs";
            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, "func broken("));
            session.AddNotification(LspMethods.DidChange, BuildDidChangeParams(uri, 2, "func entry() { wait 1 }"));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            List<JsonObject> diagnosticsNotifications = session.FindAllNotifications(LspMethods.PublishDiagnostics);
            bool hasOpenDiagnostics = false;
            bool hasClearedDiagnostics = false;

            for (int i = 0; i < diagnosticsNotifications.Count; i++)
            {
                JsonObject message = diagnosticsNotifications[i];
                JsonObject payload = message != null ? message.GetObject(JsonRpcFields.Params) : null;
                if (payload == null)
                    continue;

                string publishedUri = payload.GetString(LspFields.Uri);
                if (!string.Equals(publishedUri, uri, StringComparison.OrdinalIgnoreCase))
                    continue;

                List<object> diagnostics = payload.GetArray(LspFields.Diagnostics);
                int version = payload.GetInt("version", -1);

                if (diagnostics != null && diagnostics.Count > 0)
                    hasOpenDiagnostics = true;

                if (diagnostics != null && diagnostics.Count == 0 && version == 2)
                    hasClearedDiagnostics = true;
            }

            Assert(
                hasOpenDiagnostics && hasClearedDiagnostics,
                "LSPNEW-04: didChange valid content emits empty diagnostics clear packet");
        }

        // ================================================================
        // LSPNEW-05: showMessageRequest response drives applyEdit request
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew05_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { wait 1 }");

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                var createResult = new JsonObject();
                createResult.Set("title", "Create");
                session.AddResponse(900001, createResult);

                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject showMessageRequest = session.FindFirstRequest("window/showMessageRequest");
                JsonObject applyEditRequest = session.FindFirstRequest("workspace/applyEdit");

                JsonObject applyParams = applyEditRequest != null ? applyEditRequest.GetObject(JsonRpcFields.Params) : null;
                JsonObject edit = applyParams != null ? applyParams.GetObject("edit") : null;
                List<object> documentChanges = edit != null ? edit.GetArray("documentChanges") : null;

                Assert(
                    showMessageRequest != null
                    && showMessageRequest.GetInt(JsonRpcFields.Id, -1) == 900001,
                    "LSPNEW-05A: initialized workspace emits showMessageRequest with deterministic first client id");

                Assert(
                    applyEditRequest != null
                    && applyEditRequest.GetInt(JsonRpcFields.Id, -1) == 900002
                    && documentChanges != null
                    && documentChanges.Count >= 2,
                    "LSPNEW-05B: Create response produces workspace/applyEdit request payload");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-06: same-file definition resolves helper symbol
        // ================================================================
        {
            string uri = "file:///tests/lspnew_definition.ffs";
            string source = "func helper(): int { return 1 }\nfunc main() {\n    var v: int = helper()\n    wait v\n}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            int definitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(uri, 0, 5));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject definitionResponse = session.FindResponse(definitionId);
            JsonObject definitionResult = definitionResponse != null ? definitionResponse.GetObject(JsonRpcFields.Result) : null;
            string definitionUri = definitionResult != null ? definitionResult.GetString(LspFields.Uri) : string.Empty;

            Assert(
                definitionResult != null
                && definitionUri.IndexOf("lspnew_definition.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                "LSPNEW-06: definition request resolves helper symbol in same file");
        }

        // ================================================================
        // LSPNEW-07: completion returns symbols from opened document
        // ================================================================
        {
            string uri = "file:///tests/lspnew_completion.ffs";
            string source = "func helper(): int { return 1 }\nfunc helperTwo(): int { return 2 }\nfunc main() {\n    \n}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            int completionId = session.AddRequest(LspMethods.Completion, BuildTextDocumentPositionParams(uri, 3, 4));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject completionResponse = session.FindResponse(completionId);
            List<object> completionItems = completionResponse != null
                ? completionResponse.GetArray(JsonRpcFields.Result)
                : null;

            bool hasHelper = ContainsCompletionLabel(completionItems, "helper");

            Assert(
                completionItems != null
                && completionItems.Count > 0
                && hasHelper,
                "LSPNEW-07: completion request includes helper symbol from opened document");
        }

        // ================================================================
        // LSPNEW-08: rename returns workspace edits for declaration + usages
        // ================================================================
        {
            string uri = "file:///tests/lspnew_rename.ffs";
            string source = "func helper(): int { return 1 }\nfunc main() {\n    var a: int = helper()\n    var b: int = helper()\n    wait a + b\n}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            int renameId = session.AddRequest(LspMethods.Rename, BuildRenameParams(uri, 0, 5, "assist"));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject renameResponse = session.FindResponse(renameId);
            JsonObject renameResult = renameResponse != null ? renameResponse.GetObject(JsonRpcFields.Result) : null;
            JsonObject changes = renameResult != null ? renameResult.GetObject(LspFields.Changes) : null;

            int totalEdits = 0;
            bool touchedTargetFile = false;
            if (changes != null)
            {
                foreach (string key in changes.Keys)
                {
                    List<object> edits = changes.GetArray(key);
                    if (edits != null)
                        totalEdits += edits.Count;

                    if (!string.IsNullOrWhiteSpace(key)
                        && key.IndexOf("lspnew_rename.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        touchedTargetFile = true;
                    }
                }
            }

            Assert(
                renameResult != null
                && changes != null
                && touchedTargetFile
                && totalEdits >= 3,
                "LSPNEW-08: rename emits workspace edits for declaration and call sites");
        }

        // ================================================================
        // LSPNEW-09: ranged didChange preserves full document semantics
        // ================================================================
        {
            string uri = "file:///tests/lspnew_incremental.ffs";
            string source = "func main() {\n    var v: int = helper()\n    wait v\n}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            session.AddNotification(
                LspMethods.DidChange,
                BuildDidChangeIncrementalParams(
                    uri,
                    2,
                    new List<(int startLine, int startCharacter, int endLine, int endCharacter, string text)>
                    {
                        (0, 0, 0, 0, "func helper(): int { return 1 }\n")
                    }));

            int definitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(uri, 2, 20));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject definitionResponse = session.FindResponse(definitionId);
            JsonObject definitionResult = definitionResponse != null ? definitionResponse.GetObject(JsonRpcFields.Result) : null;
            string definitionUri = definitionResult != null ? definitionResult.GetString(LspFields.Uri) : string.Empty;

            Assert(
                definitionResult != null
                && definitionUri.IndexOf("lspnew_incremental.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                "LSPNEW-09: ranged didChange keeps full document text for definition resolution");
        }

        // ================================================================
        // LSPNEW-10: references returns declaration + call sites
        // ================================================================
        {
            string uri = "file:///tests/lspnew_references.ffs";
            string source = "func helper(): int { return 1 }\nfunc main() {\n    var a: int = helper()\n    var b: int = helper()\n    wait a + b\n}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            int referencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(uri, 0, 5, true));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            JsonObject referencesResponse = session.FindResponse(referencesId);
            List<object> locations = referencesResponse != null
                ? referencesResponse.GetArray(JsonRpcFields.Result)
                : null;

            bool hasDeclaration = false;
            bool hasUsage = false;
            int locationCount = 0;

            if (locations != null)
            {
                for (int i = 0; i < locations.Count; i++)
                {
                    if (!(locations[i] is JsonObject location))
                        continue;

                    string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                    if (locationUri.IndexOf("lspnew_references.ffs", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    JsonObject range = location.GetObject(LspFields.Range);
                    JsonObject start = range != null ? range.GetObject(LspFields.Start) : null;
                    int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                    locationCount++;
                    if (line == 0)
                        hasDeclaration = true;
                    else if (line > 0)
                        hasUsage = true;
                }
            }

            Assert(
                locations != null
                && locationCount >= 3
                && hasDeclaration
                && hasUsage,
                "LSPNEW-10: references request returns declaration and call-site locations");
        }

        // ================================================================
        // LSPNEW-11: initialize pre-indexes unopened workspace files
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew11_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 1 }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int completionId = session.AddRequest(LspMethods.Completion, BuildTextDocumentPositionParams(libUri, 0, 0));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject completionResponse = session.FindResponse(completionId);
                List<object> completionItems = completionResponse != null
                    ? completionResponse.GetArray(JsonRpcFields.Result)
                    : null;

                bool hasHelper = ContainsCompletionLabel(completionItems, "helper");
                Assert(
                    completionItems != null
                    && completionItems.Count > 0
                    && hasHelper,
                    "LSPNEW-11: initialize pre-indexes unopened workspace files for completion");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-12A: unopened cross-file references return declaration+usage
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew12a_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource = "func helper(): int { return 1 }";
                string mainSource = "include \"lib\"\nfunc main() {\n    var value: int = helper()\n    wait value\n}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int referencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject referencesResponse = session.FindResponse(referencesId);
                List<object> locations = referencesResponse != null
                    ? referencesResponse.GetArray(JsonRpcFields.Result)
                    : null;

                bool hasLibDeclaration = false;
                bool hasMainUsage = false;
                if (locations != null)
                {
                    for (int i = 0; i < locations.Count; i++)
                    {
                        if (!(locations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject range = location.GetObject(LspFields.Range);
                        JsonObject start = range != null ? range.GetObject(LspFields.Start) : null;
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            hasLibDeclaration = true;

                        if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 2)
                            hasMainUsage = true;
                    }
                }

                Assert(
                    locations != null
                    && hasLibDeclaration
                    && hasMainUsage,
                    "LSPNEW-12A: unopened cross-file references include lib declaration and main usage");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-12B: open-buffer content survives watcher delete conflict
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew12b_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 1 }");
                string mainSource = "include \"lib\"\nfunc main() {\n    var value: int = helper()\n    wait value\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main_fc06.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main_fc06.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());
                session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(mainUri, "ffscript", 1, mainSource));
                session.AddNotification(
                    LspMethods.DidChangeWatchedFiles,
                    BuildDidChangeWatchedFilesParams(new List<(string uri, int changeType)>
                    {
                        (mainUri, (int)WatchedFileChangeType.Deleted)
                    }));

                int definitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 1, 5));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject definitionResponse = session.FindResponse(definitionId);
                JsonObject definitionResult = definitionResponse != null ? definitionResponse.GetObject(JsonRpcFields.Result) : null;
                string definitionUri = definitionResult != null ? definitionResult.GetString(LspFields.Uri) : string.Empty;

                Assert(
                    definitionResult != null
                    && definitionUri.IndexOf("main_fc06.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-12B: watcher delete does not evict open-buffer document state");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-12C: bootstrap indexing spans multiple batches
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew12c_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            const int fileCount = 140;

            try
            {
                Directory.CreateDirectory(tmpDir);

                for (int i = 0; i < fileCount; i++)
                {
                    string suffix = i.ToString("D3");
                    string fileName = "batch_" + suffix + ".ffs";
                    string functionName = "batchFunc" + suffix;
                    string source = "func " + functionName + "(): int { return " + i + " }";
                    File.WriteAllText(Path.Combine(tmpDir, fileName), source);
                }

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string tailFile = "batch_" + (fileCount - 1).ToString("D3") + ".ffs";
                string tailUri = rootUri + "/" + tailFile;

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int definitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(tailUri, 0, 5));
                session.AddNotification(LspMethods.Exit, new JsonObject());

                Stopwatch stopwatch = Stopwatch.StartNew();
                session.Run(bridge);
                stopwatch.Stop();

                JsonObject definitionResponse = session.FindResponse(definitionId);
                JsonObject definitionResult = definitionResponse != null ? definitionResponse.GetObject(JsonRpcFields.Result) : null;
                string definitionUri = definitionResult != null ? definitionResult.GetString(LspFields.Uri) : string.Empty;

                Assert(
                    definitionResult != null
                    && definitionUri.IndexOf(tailFile, StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-12C: multi-batch bootstrap indexes tail unopened files");

                Assert(
                    stopwatch.ElapsedMilliseconds < 5000,
                    "LSPNEW-12C: medium-workspace bootstrap+query completes within 5s");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-12D: bootstrap scan excludes Library directory sources
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew12d_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string libraryDir = Path.Combine(tmpDir, "Library");
                Directory.CreateDirectory(libraryDir);

                File.WriteAllText(Path.Combine(tmpDir, "kept.ffs"), "func kept(): int { return 1 }");
                File.WriteAllText(Path.Combine(libraryDir, "ignored.ffs"), "func ignored(): int { return 1 }");

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string keptUri = rootUri + "/kept.ffs";
                string ignoredUri = rootUri + "/Library/ignored.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int keptDefinitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(keptUri, 0, 5));
                int ignoredDefinitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(ignoredUri, 0, 5));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject keptResponse = session.FindResponse(keptDefinitionId);
                JsonObject keptResult = keptResponse != null ? keptResponse.GetObject(JsonRpcFields.Result) : null;
                string keptDefinitionUri = keptResult != null ? keptResult.GetString(LspFields.Uri) : string.Empty;

                JsonObject ignoredResponse = session.FindResponse(ignoredDefinitionId);
                JsonObject ignoredResult = ignoredResponse != null ? ignoredResponse.GetObject(JsonRpcFields.Result) : null;

                Assert(
                    keptResult != null
                    && keptDefinitionUri.IndexOf("kept.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-12D: root source remains indexed when scan filter is enabled");

                Assert(
                    ignoredResult == null,
                    "LSPNEW-12D: Library directory sources are excluded from bootstrap index");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-12E: watcher changed keeps open-buffer source of truth
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew12e_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string openBufferSource = "func helper(): int { return 1 }\nfunc main() {\n    var v: int = helper()\n    wait v\n}";
                string staleDiskSource = "func helper_disk(): int { return 2 }\nfunc main() {\n    var v: int = helper_disk()\n    wait v\n}";

                string filePath = Path.Combine(tmpDir, "priority.ffs");
                File.WriteAllText(filePath, openBufferSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string uri = rootUri + "/priority.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());
                session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, openBufferSource));

                File.WriteAllText(filePath, staleDiskSource);

                session.AddNotification(
                    LspMethods.DidChangeWatchedFiles,
                    BuildDidChangeWatchedFilesParams(new List<(string uri, int changeType)>
                    {
                        (uri, (int)WatchedFileChangeType.Changed)
                    }));

                int definitionId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(uri, 2, 18));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject definitionResponse = session.FindResponse(definitionId);
                JsonObject definitionResult = definitionResponse != null ? definitionResponse.GetObject(JsonRpcFields.Result) : null;
                JsonObject range = definitionResult != null ? definitionResult.GetObject(LspFields.Range) : null;
                JsonObject start = range != null ? range.GetObject(LspFields.Start) : null;
                int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                Assert(
                    definitionResult != null && line == 0,
                    "LSPNEW-12E: watcher changed does not override open-buffer document state");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-13: cross-file references honors includeDeclaration toggle
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew13_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource = "func helper(): int { return 1 }";
                string mainSource = "include \"lib\"\nfunc main() {\n    var a: int = helper()\n    var b: int = helper()\n    wait a + b\n}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int withDeclId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));
                int withoutDeclId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, false));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject withDeclResponse = session.FindResponse(withDeclId);
                JsonObject withoutDeclResponse = session.FindResponse(withoutDeclId);

                List<object> withDeclLocations = withDeclResponse != null
                    ? withDeclResponse.GetArray(JsonRpcFields.Result)
                    : null;
                List<object> withoutDeclLocations = withoutDeclResponse != null
                    ? withoutDeclResponse.GetArray(JsonRpcFields.Result)
                    : null;

                bool withDeclHasDefinition = false;
                bool withDeclHasMainUsage = false;
                if (withDeclLocations != null)
                {
                    for (int i = 0; i < withDeclLocations.Count; i++)
                    {
                        if (!(withDeclLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            withDeclHasDefinition = true;

                        if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 2)
                            withDeclHasMainUsage = true;
                    }
                }

                bool withoutDeclHasDefinition = false;
                int withoutDeclMainUsageCount = 0;
                if (withoutDeclLocations != null)
                {
                    for (int i = 0; i < withoutDeclLocations.Count; i++)
                    {
                        if (!(withoutDeclLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            withoutDeclHasDefinition = true;

                        if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 2)
                            withoutDeclMainUsageCount++;
                    }
                }

                Assert(
                    withDeclLocations != null
                    && withDeclHasDefinition
                    && withDeclHasMainUsage,
                    "LSPNEW-13A: includeDeclaration=true returns both declaration and cross-file usages");

                Assert(
                    withoutDeclLocations != null
                    && !withoutDeclHasDefinition
                    && withoutDeclMainUsageCount >= 2,
                    "LSPNEW-13B: includeDeclaration=false excludes declaration and preserves cross-file usages");

                Assert(
                    AreLocationsSortedAndUnique(withDeclLocations),
                    "LSPNEW-13C: references locations are deduped and stable-ordered");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-14: same-file nested struct-field references
        // ================================================================
        {
            string uri = "file:///tests/lspnew14_nested.ffs";
            string source =
                "struct Stats { hp: int }\n"
                + "struct Player { stats: Stats }\n"
                + "func main() {\n"
                + "    var p: Player = Player { stats: Stats { hp: 10 } }\n"
                + "    var a: int = p.stats.hp\n"
                + "    p.stats.hp = a\n"
                + "    var b: int = p.stats.hp\n"
                + "    wait a + b\n"
                + "}";

            var bridge = new DatabaseBackedVsCodeBridge(
                new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

            var session = new LspServerNewBatchSession();
            session.AddRequest(LspMethods.Initialize, new JsonObject());
            session.AddNotification(LspMethods.Initialized, new JsonObject());
            session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(uri, "ffscript", 1, source));

            int hpReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(uri, 0, 15, true));
            int statsReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(uri, 1, 16, true));
            session.AddNotification(LspMethods.Exit, new JsonObject());
            session.Run(bridge);

            List<object> hpLocations = session.FindResponse(hpReferencesId)?.GetArray(JsonRpcFields.Result);
            List<object> statsLocations = session.FindResponse(statsReferencesId)?.GetArray(JsonRpcFields.Result);

            bool hpHasDefinition = false;
            int hpUsageCount = 0;
            if (hpLocations != null)
            {
                for (int i = 0; i < hpLocations.Count; i++)
                {
                    if (!(hpLocations[i] is JsonObject location))
                        continue;

                    JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                    int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;
                    if (line == 0)
                        hpHasDefinition = true;
                    else if (line >= 4)
                        hpUsageCount++;
                }
            }

            bool statsHasDefinition = false;
            int statsUsageCount = 0;
            if (statsLocations != null)
            {
                for (int i = 0; i < statsLocations.Count; i++)
                {
                    if (!(statsLocations[i] is JsonObject location))
                        continue;

                    JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                    int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;
                    if (line == 1)
                        statsHasDefinition = true;
                    else if (line >= 4)
                        statsUsageCount++;
                }
            }

            Assert(
                hpLocations != null && hpHasDefinition && hpUsageCount >= 3,
                "LSPNEW-14A: nested leaf field references include definition and repeated usages");

            Assert(
                statsLocations != null && statsHasDefinition && statsUsageCount >= 3,
                "LSPNEW-14B: nested middle field references include definition and repeated usages");
        }

        // ================================================================
        // LSPNEW-15: cross-file nested struct-field references
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew15_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource =
                    "struct Stats { hp: int }\n"
                    + "struct Player { stats: Stats }\n"
                    + "func readHp(p: Player): int {\n"
                    + "    return p.stats.hp\n"
                    + "}";

                string mainSource =
                    "include \"lib\"\n"
                    + "func main() {\n"
                    + "    var p: Player = Player { stats: Stats { hp: 42 } }\n"
                    + "    var x: int = p.stats.hp\n"
                    + "    p.stats.hp = x\n"
                    + "    wait x + readHp(p)\n"
                    + "}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int hpReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 15, true));
                int statsReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 1, 16, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                List<object> hpLocations = session.FindResponse(hpReferencesId)?.GetArray(JsonRpcFields.Result);
                List<object> statsLocations = session.FindResponse(statsReferencesId)?.GetArray(JsonRpcFields.Result);

                bool hpHasLibDefinition = false;
                bool hpHasLibUsage = false;
                bool hpHasMainUsage = false;
                if (hpLocations != null)
                {
                    for (int i = 0; i < hpLocations.Count; i++)
                    {
                        if (!(hpLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            hpHasLibDefinition = true;
                        else if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 3)
                            hpHasLibUsage = true;
                        else if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 3)
                            hpHasMainUsage = true;
                    }
                }

                bool statsHasLibDefinition = false;
                bool statsHasLibUsage = false;
                bool statsHasMainUsage = false;
                if (statsLocations != null)
                {
                    for (int i = 0; i < statsLocations.Count; i++)
                    {
                        if (!(statsLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 1)
                            statsHasLibDefinition = true;
                        else if (locationUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 3)
                            statsHasLibUsage = true;
                        else if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 3)
                            statsHasMainUsage = true;
                    }
                }

                Assert(
                    hpLocations != null && hpHasLibDefinition && hpHasLibUsage && hpHasMainUsage,
                    "LSPNEW-15A: cross-file nested leaf field references include declaration and multi-file usages");

                Assert(
                    statsLocations != null && statsHasLibDefinition && statsHasLibUsage && statsHasMainUsage,
                    "LSPNEW-15B: cross-file nested middle field references include declaration and multi-file usages");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-16: T1 cross coverage (include topology + watcher + path normalization)
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew16a_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string sourceC = "func helper(): int { return 1 }";
                string sourceB = "include \"c\"\nfunc fromB(): int {\n    return helper()\n}";
                string sourceA = "include \"b\"\nfunc main() {\n    var value: int = fromB()\n    wait value\n}";

                File.WriteAllText(Path.Combine(tmpDir, "c.ffs"), sourceC);
                File.WriteAllText(Path.Combine(tmpDir, "b.ffs"), sourceB);
                File.WriteAllText(Path.Combine(tmpDir, "a.ffs"), sourceA);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string cUri = rootUri + "/c.ffs";
                string bUri = rootUri + "/b.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int helperReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(cUri, 0, 5, true));
                int fromBReferencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(bUri, 1, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                List<object> helperLocations = session.FindResponse(helperReferencesId)?.GetArray(JsonRpcFields.Result);
                List<object> fromBLocations = session.FindResponse(fromBReferencesId)?.GetArray(JsonRpcFields.Result);

                bool hasCDefinition = false;
                bool hasBUsage = false;
                if (helperLocations != null)
                {
                    for (int i = 0; i < helperLocations.Count; i++)
                    {
                        if (!(helperLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/c.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            hasCDefinition = true;
                        else if (locationUri.IndexOf("/b.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasBUsage = true;
                    }
                }

                bool hasBDefinition = false;
                bool hasAUsage = false;
                if (fromBLocations != null)
                {
                    for (int i = 0; i < fromBLocations.Count; i++)
                    {
                        if (!(fromBLocations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locationUri.IndexOf("/b.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 1)
                            hasBDefinition = true;
                        else if (locationUri.IndexOf("/a.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasAUsage = true;
                    }
                }

                Assert(
                    helperLocations != null
                    && hasCDefinition
                    && hasBUsage
                    && AreLocationsSortedAndUnique(helperLocations)
                    && fromBLocations != null
                    && hasBDefinition
                    && hasAUsage
                    && AreLocationsSortedAndUnique(fromBLocations),
                    "LSPNEW-16A: transitive include chain keeps C->B and B->A references queryable and stable");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew16b_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string sourceD = "func helper(): int { return 1 }";
                string sourceB = "include \"d\"\nfunc fromB(): int {\n    return helper()\n}";
                string sourceC = "include \"d\"\nfunc fromC(): int {\n    return helper()\n}";
                string sourceA = "include \"b\"\ninclude \"c\"\nfunc main() {\n    var value: int = fromB() + fromC()\n    wait value\n}";

                File.WriteAllText(Path.Combine(tmpDir, "d.ffs"), sourceD);
                File.WriteAllText(Path.Combine(tmpDir, "b.ffs"), sourceB);
                File.WriteAllText(Path.Combine(tmpDir, "c.ffs"), sourceC);
                File.WriteAllText(Path.Combine(tmpDir, "a.ffs"), sourceA);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string dUri = rootUri + "/d.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int referencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(dUri, 0, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                List<object> locations = session.FindResponse(referencesId)?.GetArray(JsonRpcFields.Result);

                bool hasD = false;
                bool hasB = false;
                bool hasC = false;
                if (locations != null)
                {
                    for (int i = 0; i < locations.Count; i++)
                    {
                        if (!(locations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        if (locationUri.IndexOf("/d.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasD = true;
                        else if (locationUri.IndexOf("/b.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasB = true;
                        else if (locationUri.IndexOf("/c.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasC = true;
                    }
                }

                Assert(
                    locations != null
                    && locations.Count == 3
                    && hasD
                    && hasB
                    && hasC
                    && AreLocationsSortedAndUnique(locations),
                    "LSPNEW-16B: diamond include references are deduped for shared dependency usage");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew16d_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                string modulesDir = Path.Combine(tmpDir, "modules");
                Directory.CreateDirectory(modulesDir);

                string libSource = "func helper(): int { return 1 }";
                string mainSource = "include \"modules/lib\"\nfunc main() {\n    var value: int = helper()\n    wait value\n}";

                File.WriteAllText(Path.Combine(modulesDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/modules/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int referencesId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                List<object> locations = session.FindResponse(referencesId)?.GetArray(JsonRpcFields.Result);

                bool hasDefinition = false;
                bool hasMainUsage = false;
                if (locations != null)
                {
                    for (int i = 0; i < locations.Count; i++)
                    {
                        if (!(locations[i] is JsonObject location))
                            continue;

                        string locationUri = location.GetString(LspFields.Uri) ?? string.Empty;
                        if (locationUri.IndexOf("/modules/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasDefinition = true;
                        else if (locationUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0)
                            hasMainUsage = true;
                    }
                }

                Assert(
                    locations != null
                    && hasDefinition
                    && hasMainUsage,
                    "LSPNEW-16D: include path normalization handles relative nested path and extension completion");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-17: watcher created converges unresolved include dependency
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew17_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string mainSource = "include \"lib\"\nfunc main() {\n    var value: int = helper()\n    wait value\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var beforeSession = new LspServerNewBatchSession();
                beforeSession.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                beforeSession.AddNotification(LspMethods.Initialized, new JsonObject());
                int beforeCreateDefinitionId = beforeSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(libUri, 0, 5));
                beforeSession.AddNotification(LspMethods.Exit, new JsonObject());
                beforeSession.Run(bridge);

                JsonObject beforeCreateDefinition = beforeSession.FindResponse(beforeCreateDefinitionId)?.GetObject(JsonRpcFields.Result);

                string libSource = "func helper(): int { return 1 }";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);

                var afterSession = new LspServerNewBatchSession();
                afterSession.AddNotification(
                    LspMethods.DidChangeWatchedFiles,
                    BuildDidChangeWatchedFilesParams(new List<(string uri, int changeType)>
                    {
                        (libUri, (int)WatchedFileChangeType.Created)
                    }));

                int afterCreateDefinitionId = afterSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(libUri, 0, 5));
                int afterCreateCompletionId = afterSession.AddRequest(LspMethods.Completion, BuildTextDocumentPositionParams(libUri, 0, 0));
                afterSession.AddNotification(LspMethods.Exit, new JsonObject());
                afterSession.Run(bridge);

                JsonObject afterCreateDefinition = afterSession.FindResponse(afterCreateDefinitionId)?.GetObject(JsonRpcFields.Result);
                string afterCreateUri = afterCreateDefinition != null ? afterCreateDefinition.GetString(LspFields.Uri) : string.Empty;
                List<object> completionItems = afterSession.FindResponse(afterCreateCompletionId)?.GetArray(JsonRpcFields.Result);

                Assert(beforeCreateDefinition == null, "LSPNEW-17A: unresolved include symbol is not queryable before watcher created");
                Assert(
                    afterCreateDefinition != null
                    && afterCreateUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-17B: watcher created converges unresolved include symbol to new definition");
                Assert(
                    completionItems != null
                    && completionItems.Count > 0
                    && ContainsCompletionLabel(completionItems, "helper"),
                    "LSPNEW-17C: watcher created reindexes new file symbols for completion");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-18: watcher changed replaces old symbol facts without residue
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew18_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libV1 = "func helper(): int { return 1 }";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libV1);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var beforeSession = new LspServerNewBatchSession();
                beforeSession.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                beforeSession.AddNotification(LspMethods.Initialized, new JsonObject());
                int beforeChangeDefinitionId = beforeSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(libUri, 0, 5));
                beforeSession.AddNotification(LspMethods.Exit, new JsonObject());
                beforeSession.Run(bridge);

                JsonObject beforeChangeDefinition = beforeSession.FindResponse(beforeChangeDefinitionId)?.GetObject(JsonRpcFields.Result);

                string libV2 = "func helper_new(): int { return 2 }";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libV2);

                var afterSession = new LspServerNewBatchSession();
                afterSession.AddNotification(
                    LspMethods.DidChangeWatchedFiles,
                    BuildDidChangeWatchedFilesParams(new List<(string uri, int changeType)>
                    {
                        (libUri, (int)WatchedFileChangeType.Changed)
                    }));

                int afterChangeDefinitionId = afterSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(libUri, 0, 5));
                int afterChangeCompletionId = afterSession.AddRequest(LspMethods.Completion, BuildTextDocumentPositionParams(libUri, 0, 0));
                afterSession.AddNotification(LspMethods.Exit, new JsonObject());
                afterSession.Run(bridge);

                JsonObject afterChangeDefinition = afterSession.FindResponse(afterChangeDefinitionId)?.GetObject(JsonRpcFields.Result);
                string afterChangeUri = afterChangeDefinition != null ? afterChangeDefinition.GetString(LspFields.Uri) : string.Empty;
                List<object> afterChangeCompletion = afterSession.FindResponse(afterChangeCompletionId)?.GetArray(JsonRpcFields.Result);

                Assert(beforeChangeDefinition != null, "LSPNEW-18A: symbol is queryable before watcher changed update");
                Assert(
                    afterChangeDefinition != null
                    && afterChangeCompletion != null
                    && !ContainsCompletionLabel(afterChangeCompletion, "helper")
                    && ContainsCompletionLabel(afterChangeCompletion, "helper_new"),
                    "LSPNEW-18B: watcher changed replaces stale symbol set with updated completion labels");
                Assert(
                    afterChangeDefinition != null
                    && afterChangeUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-18C: watcher changed reindexes new symbol definitions");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-19: watcher deleted converges to no stale include resolution
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew19_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource = "func helper(): int { return 1 }";
                string mainSource = "include \"lib\"\nfunc main() {\n    var value: int = helper()\n    wait value\n}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var beforeSession = new LspServerNewBatchSession();
                beforeSession.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                beforeSession.AddNotification(LspMethods.Initialized, new JsonObject());
                int beforeDeleteDefinitionId = beforeSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 23));
                beforeSession.AddNotification(LspMethods.Exit, new JsonObject());
                beforeSession.Run(bridge);

                JsonObject beforeDeleteDefinition = beforeSession.FindResponse(beforeDeleteDefinitionId)?.GetObject(JsonRpcFields.Result);

                try { File.Delete(Path.Combine(tmpDir, "lib.ffs")); } catch { }

                var afterSession = new LspServerNewBatchSession();
                afterSession.AddNotification(
                    LspMethods.DidChangeWatchedFiles,
                    BuildDidChangeWatchedFilesParams(new List<(string uri, int changeType)>
                    {
                        (libUri, (int)WatchedFileChangeType.Deleted)
                    }));

                int afterDeleteDefinitionId = afterSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(libUri, 0, 5));
                int afterDeleteMainDefinitionId = afterSession.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 1, 5));
                afterSession.AddNotification(LspMethods.Exit, new JsonObject());
                afterSession.Run(bridge);

                JsonObject afterDeleteDefinition = afterSession.FindResponse(afterDeleteDefinitionId)?.GetObject(JsonRpcFields.Result);
                JsonObject afterDeleteMainDefinition = afterSession.FindResponse(afterDeleteMainDefinitionId)?.GetObject(JsonRpcFields.Result);
                string afterDeleteMainUri = afterDeleteMainDefinition != null ? afterDeleteMainDefinition.GetString(LspFields.Uri) : string.Empty;

                Assert(beforeDeleteDefinition != null, "LSPNEW-19A: include symbol is queryable before watcher deleted event");
                Assert(afterDeleteDefinition == null, "LSPNEW-19B: watcher deleted removes stale include symbol resolution");
                Assert(
                    afterDeleteMainDefinition != null
                    && afterDeleteMainUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-19C: watcher deleted does not corrupt unrelated in-file symbol queries");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }



        // ================================================================

        // LSPNEW-T2-IN-01: plain include cross-file function definition + references

        // ================================================================

        {

            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in01_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try

            {

                Directory.CreateDirectory(tmpDir);



                string libSource = "func add(a: int, b: int): int { return a + b }\nfunc mul(a: int, b: int): int { return a * b }";

                string mainSource = "include \"lib\"\nfunc main() {\n    var s: int = add(1, 2)\n    var p: int = mul(3, 4)\n    wait s + p\n}";



                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);

                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);



                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");

                string libUri = rootUri + "/lib.ffs";

                string mainUri = rootUri + "/main.ffs";



                var bridge = new DatabaseBackedVsCodeBridge(

                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));



                var session = new LspServerNewBatchSession();

                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));

                session.AddNotification(LspMethods.Initialized, new JsonObject());



                // Definition: from call site add(1,2) in main → should jump to lib

                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 17));

                // References: on add declaration in lib → should include lib decl + main usage

                int refId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));

                session.AddNotification(LspMethods.Exit, new JsonObject());

                session.Run(bridge);



                // Check definition

                JsonObject defResponse = session.FindResponse(defId);

                JsonObject defResult = defResponse != null ? defResponse.GetObject(JsonRpcFields.Result) : null;

                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) : string.Empty;



                Assert(

                    defResult != null

                    && defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,

                    "LSPNEW-T2-IN-01A: definition from call site jumps to lib.ffs");



                // Check references

                JsonObject refResponse = session.FindResponse(refId);

                List<object> refLocations = refResponse != null ? refResponse.GetArray(JsonRpcFields.Result) : null;



                bool hasLibDecl = false;

                bool hasMainUsage = false;

                if (refLocations != null)

                {

                    for (int i = 0; i < refLocations.Count; i++)

                    {

                        if (!(refLocations[i] is JsonObject loc))

                            continue;

                        string locUri = loc.GetString(LspFields.Uri) ?? string.Empty;

                        JsonObject range = loc.GetObject(LspFields.Range);

                        JsonObject start = range != null ? range.GetObject(LspFields.Start) : null;

                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;



                        if (locUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)

                            hasLibDecl = true;

                        if (locUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 2)

                            hasMainUsage = true;

                    }

                }



                Assert(

                    refLocations != null && hasLibDecl && hasMainUsage,

                    "LSPNEW-T2-IN-01B: references on add() include lib declaration and main call site");

            }

            finally

            {

                try { Directory.Delete(tmpDir, true); } catch { }

            }

        }




        // ================================================================

        // LSPNEW-T2-IN-02: plain include cross-file var/struct/enum references

        // ================================================================

        {

            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in02_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try

            {

                Directory.CreateDirectory(tmpDir);



                string libSource =

                    "var PI: int = 3\n"

                    + "struct Vec { x: int, y: int }\n"

                    + "enum Dir { Up, Down }";

                string mainSource =

                    "include \"lib\"\n"

                    + "func main() {\n"

                    + "    var c: int = PI\n"

                    + "    var v: Vec = Vec { x: 1, y: 2 }\n"

                    + "    var d: Dir = Dir.Up\n"

                    + "    wait c + v.x\n"

                    + "}";



                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);

                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);



                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");

                string libUri = rootUri + "/lib.ffs";



                var bridge = new DatabaseBackedVsCodeBridge(

                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));



                var session = new LspServerNewBatchSession();

                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));

                session.AddNotification(LspMethods.Initialized, new JsonObject());



                // References on PI (lib line 0, col 4)

                int refVarId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 4, true));

                // References on Vec (lib line 1, col 7)

                int refStructId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 1, 7, true));

                // References on Dir (lib line 2, col 5)

                int refEnumId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 2, 5, true));



                session.AddNotification(LspMethods.Exit, new JsonObject());

                session.Run(bridge);



                // Helper to check cross-file presence

                bool CheckCrossFile(int requestId, string libFile, string mainFile, int declLine)

                {

                    JsonObject resp = session.FindResponse(requestId);

                    List<object> locs = resp != null ? resp.GetArray(JsonRpcFields.Result) : null;

                    if (locs == null) return false;

                    bool hasDef = false;

                    bool hasUse = false;

                    for (int i = 0; i < locs.Count; i++)

                    {

                        if (!(locs[i] is JsonObject loc)) continue;

                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;

                        JsonObject r = loc.GetObject(LspFields.Range);

                        JsonObject s = r != null ? r.GetObject(LspFields.Start) : null;

                        int ln = s != null ? s.GetInt(LspFields.Line, -1) : -1;



                        if (u.IndexOf(libFile, StringComparison.OrdinalIgnoreCase) >= 0 && ln == declLine) hasDef = true;

                        if (u.IndexOf(mainFile, StringComparison.OrdinalIgnoreCase) >= 0 && ln >= 1) hasUse = true;

                    }

                    return hasDef && hasUse;

                }



                Assert(CheckCrossFile(refVarId, "/lib.ffs", "/main.ffs", 0),

                    "LSPNEW-T2-IN-02A: var PI references span lib declaration and main usage");

                Assert(CheckCrossFile(refStructId, "/lib.ffs", "/main.ffs", 1),

                    "LSPNEW-T2-IN-02B: struct Vec references span lib declaration and main usage");

                Assert(CheckCrossFile(refEnumId, "/lib.ffs", "/main.ffs", 2),

                    "LSPNEW-T2-IN-02C: enum Dir references span lib declaration and main usage");

            }

            finally

            {

                try { Directory.Delete(tmpDir, true); } catch { }

            }

        }




        // ================================================================

        // LSPNEW-T2-IN-03: plain include declaration — definition on include

        //   path resolves to target file

        // ================================================================

        {

            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in03_" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try

            {

                Directory.CreateDirectory(tmpDir);



                string libSource = "func libFunc(): int { return 42 }";

                string mainSource = "include \"lib\"\nfunc main() {\n    var v: int = libFunc()\n    wait v\n}";



                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);

                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);



                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");

                string mainUri = rootUri + "/main.ffs";



                var bridge = new DatabaseBackedVsCodeBridge(

                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));



                var session = new LspServerNewBatchSession();

                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));

                session.AddNotification(LspMethods.Initialized, new JsonObject());



                // Definition from call site libFunc() in main line 2 col 17

                int defCallId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 17));

                // References on libFunc() from main — should find lib decl + main call

                int refCallId = session.AddRequest(LspMethods.References, BuildReferencesParams(mainUri, 2, 17, true));

                session.AddNotification(LspMethods.Exit, new JsonObject());

                session.Run(bridge);



                // Definition from call site → should land in lib.ffs

                JsonObject defCallResp = session.FindResponse(defCallId);

                JsonObject defCallResult = defCallResp != null ? defCallResp.GetObject(JsonRpcFields.Result) : null;

                string defCallUri = defCallResult != null ? defCallResult.GetString(LspFields.Uri) : string.Empty;



                Assert(

                    defCallResult != null

                    && defCallUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,

                    "LSPNEW-T2-IN-03A: definition from included function call resolves to source file");



                // References from call site → should include lib declaration and main call

                JsonObject refCallResp = session.FindResponse(refCallId);

                List<object> refLocs = refCallResp != null ? refCallResp.GetArray(JsonRpcFields.Result) : null;

                bool hasLib = false;

                bool hasMain = false;

                if (refLocs != null)

                {

                    for (int i = 0; i < refLocs.Count; i++)

                    {

                        if (!(refLocs[i] is JsonObject loc)) continue;

                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;

                        if (u.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;

                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasMain = true;

                    }

                }



                Assert(

                    refLocs != null && hasLib && hasMain,

                    "LSPNEW-T2-IN-03B: references from included function span both declaring and consuming files");

            }

            finally

            {

                try { Directory.Delete(tmpDir, true); } catch { }

            }

        }



        // ================================================================
        // LSPNEW-T2-IN-01: plain include cross-file function definition + references
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in01_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource = "func add(a: int, b: int): int { return a + b }\nfunc mul(a: int, b: int): int { return a * b }";
                string mainSource = "include \"lib\"\nfunc main() {\n    var s: int = add(1, 2)\n    var p: int = mul(3, 4)\n    wait s + p\n}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition: from call site add(1,2) in main → should jump to lib
                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 17));
                // References: on add declaration in lib → should include lib decl + main usage
                int refId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                // Check definition
                JsonObject defResponse = session.FindResponse(defId);
                JsonObject defResult = defResponse != null ? defResponse.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) : string.Empty;

                Assert(
                    defResult != null
                    && defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-IN-01A: definition from call site jumps to lib.ffs");

                // Check references
                JsonObject refResponse = session.FindResponse(refId);
                List<object> refLocations = refResponse != null ? refResponse.GetArray(JsonRpcFields.Result) : null;

                bool hasLibDecl = false;
                bool hasMainUsage = false;
                if (refLocations != null)
                {
                    for (int i = 0; i < refLocations.Count; i++)
                    {
                        if (!(refLocations[i] is JsonObject loc))
                            continue;
                        string locUri = loc.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject range = loc.GetObject(LspFields.Range);
                        JsonObject start = range != null ? range.GetObject(LspFields.Start) : null;
                        int line = start != null ? start.GetInt(LspFields.Line, -1) : -1;

                        if (locUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line == 0)
                            hasLibDecl = true;
                        if (locUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && line >= 2)
                            hasMainUsage = true;
                    }
                }

                Assert(
                    refLocations != null && hasLibDecl && hasMainUsage,
                    "LSPNEW-T2-IN-01B: references on add() include lib declaration and main call site");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }


        // ================================================================
        // LSPNEW-T2-IN-02: plain include cross-file var/struct/enum references
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in02_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource =
                    "var PI: int = 3\n"
                    + "struct Vec { x: int, y: int }\n"
                    + "enum Dir { Up, Down }";
                string mainSource =
                    "include \"lib\"\n"
                    + "func main() {\n"
                    + "    var c: int = PI\n"
                    + "    var v: Vec = Vec { x: 1, y: 2 }\n"
                    + "    var d: Dir = Dir.Up\n"
                    + "    wait c + v.x\n"
                    + "}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // References on PI (lib line 0, col 4)
                int refVarId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 4, true));
                // References on Vec (lib line 1, col 7)
                int refStructId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 1, 7, true));
                // References on Dir (lib line 2, col 5)
                int refEnumId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 2, 5, true));

                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                // Helper to check cross-file presence
                bool CheckCrossFile(int requestId, string libFile, string mainFile, int declLine)
                {
                    JsonObject resp = session.FindResponse(requestId);
                    List<object> locs = resp != null ? resp.GetArray(JsonRpcFields.Result) : null;
                    if (locs == null) return false;
                    bool hasDef = false;
                    bool hasUse = false;
                    for (int i = 0; i < locs.Count; i++)
                    {
                        if (!(locs[i] is JsonObject loc)) continue;
                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject r = loc.GetObject(LspFields.Range);
                        JsonObject s = r != null ? r.GetObject(LspFields.Start) : null;
                        int ln = s != null ? s.GetInt(LspFields.Line, -1) : -1;

                        if (u.IndexOf(libFile, StringComparison.OrdinalIgnoreCase) >= 0 && ln == declLine) hasDef = true;
                        if (u.IndexOf(mainFile, StringComparison.OrdinalIgnoreCase) >= 0 && ln >= 1) hasUse = true;
                    }
                    return hasDef && hasUse;
                }

                Assert(CheckCrossFile(refVarId, "/lib.ffs", "/main.ffs", 0),
                    "LSPNEW-T2-IN-02A: var PI references span lib declaration and main usage");
                Assert(CheckCrossFile(refStructId, "/lib.ffs", "/main.ffs", 1),
                    "LSPNEW-T2-IN-02B: struct Vec references span lib declaration and main usage");
                Assert(CheckCrossFile(refEnumId, "/lib.ffs", "/main.ffs", 2),
                    "LSPNEW-T2-IN-02C: enum Dir references span lib declaration and main usage");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }


        // ================================================================
        // LSPNEW-T2-IN-03: plain include declaration — definition on include
        //   path resolves to target file
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2in03_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);

                string libSource = "func libFunc(): int { return 42 }";
                string mainSource = "include \"lib\"\nfunc main() {\n    var v: int = libFunc()\n    wait v\n}";

                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));

                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition from call site libFunc() in main line 2 col 17
                int defCallId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 17));
                // References on libFunc() from main — should find lib decl + main call
                int refCallId = session.AddRequest(LspMethods.References, BuildReferencesParams(mainUri, 2, 17, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                // Definition from call site → should land in lib.ffs
                JsonObject defCallResp = session.FindResponse(defCallId);
                JsonObject defCallResult = defCallResp != null ? defCallResp.GetObject(JsonRpcFields.Result) : null;
                string defCallUri = defCallResult != null ? defCallResult.GetString(LspFields.Uri) : string.Empty;

                Assert(
                    defCallResult != null
                    && defCallUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-IN-03A: definition from included function call resolves to source file");

                // References from call site → should include lib declaration and main call
                JsonObject refCallResp = session.FindResponse(refCallId);
                List<object> refLocs = refCallResp != null ? refCallResp.GetArray(JsonRpcFields.Result) : null;
                bool hasLib = false;
                bool hasMain = false;
                if (refLocs != null)
                {
                    for (int i = 0; i < refLocs.Count; i++)
                    {
                        if (!(refLocs[i] is JsonObject loc)) continue;
                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;
                        if (u.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;
                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasMain = true;
                    }
                }

                Assert(
                    refLocs != null && hasLib && hasMain,
                    "LSPNEW-T2-IN-03B: references from included function span both declaring and consuming files");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-AL-01: go-to-definition on aliased function call U.add()
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2al01_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func add(a: int, b: int): int {\n    return a + b\n}";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func main() {\n"
                    + "    var r: int = U.add(1, 2)\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition on "add" in U.add (line 2, col 19 for "add" after "U.")
                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 19));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defResp = session.FindResponse(defId);
                JsonObject defResult = defResp != null ? defResp.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;
                JsonObject defRange = defResult != null ? defResult.GetObject(LspFields.Range) : null;
                JsonObject defStart = defRange != null ? defRange.GetObject(LspFields.Start) : null;
                int defLine = defStart != null ? defStart.GetInt(LspFields.Line, -1) : -1;

                Assert(
                    defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && defLine == 0,
                    "LSPNEW-T2-AL-01: go-to-definition on U.add() jumps to lib.ffs line 0");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-AL-02: references on aliased function span both files
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2al02_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func add(a: int, b: int): int {\n    return a + b\n}";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func main() {\n"
                    + "    var r: int = U.add(1, 2)\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // References on "add" at lib.ffs line 0, col 5
                int refId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 5, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject refResp = session.FindResponse(refId);
                List<object> refLocs = refResp != null ? refResp.GetArray(JsonRpcFields.Result) : null;
                bool hasLib = false, hasMain = false;
                if (refLocs != null)
                {
                    for (int i = 0; i < refLocs.Count; i++)
                    {
                        if (!(refLocs[i] is JsonObject loc)) continue;
                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;
                        if (u.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;
                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasMain = true;
                    }
                }

                Assert(
                    refLocs != null && hasLib && hasMain,
                    "LSPNEW-T2-AL-02: references on add include both lib declaration and U.add() usage");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-AL-03: two different aliases don't interfere
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2al03_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string mathSource = "func add(a: int, b: int): int {\n    return a + b\n}";
                string strSource = "func concat(a: int, b: int): int {\n    return a + b\n}";
                string mainSource =
                    "include \"math\" as M\n"
                    + "include \"str\" as S\n"
                    + "func main() {\n"
                    + "    var r1: int = M.add(1, 2)\n"
                    + "    var r2: int = S.concat(3, 4)\n"
                    + "    wait r1 + r2\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "math.ffs"), mathSource);
                File.WriteAllText(Path.Combine(tmpDir, "str.ffs"), strSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition on M.add → should go to math.ffs
                int defMathId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 3, 20));
                // Definition on S.concat → should go to str.ffs
                int defStrId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 4, 20));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defMathResp = session.FindResponse(defMathId);
                JsonObject defMathResult = defMathResp != null ? defMathResp.GetObject(JsonRpcFields.Result) : null;
                string defMathUri = defMathResult != null ? defMathResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                JsonObject defStrResp = session.FindResponse(defStrId);
                JsonObject defStrResult = defStrResp != null ? defStrResp.GetObject(JsonRpcFields.Result) : null;
                string defStrUri = defStrResult != null ? defStrResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    defMathUri.IndexOf("/math.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && defStrUri.IndexOf("/str.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-AL-03: M.add() resolves to math.ffs and S.concat() resolves to str.ffs independently");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-AL-04: aliased var/struct references via FieldAccess
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2al04_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource =
                    "var PI: int = 3\n"
                    + "struct Vec { x: int, y: int }";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func main() {\n"
                    + "    var c: int = U.PI\n"
                    + "    wait c\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string libUri = rootUri + "/lib.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // References on PI at lib.ffs line 0, col 4
                int refPIId = session.AddRequest(LspMethods.References, BuildReferencesParams(libUri, 0, 4, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject refPIResp = session.FindResponse(refPIId);
                List<object> refPILocs = refPIResp != null ? refPIResp.GetArray(JsonRpcFields.Result) : null;
                bool hasLib = false, hasMain = false;
                if (refPILocs != null)
                {
                    for (int i = 0; i < refPILocs.Count; i++)
                    {
                        if (!(refPILocs[i] is JsonObject loc)) continue;
                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;
                        if (u.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;
                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasMain = true;
                    }
                }

                Assert(
                    refPILocs != null && hasLib && hasMain,
                    "LSPNEW-T2-AL-04: references on PI include lib declaration and U.PI usage in main");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-OV-01: go-to-definition on B.Do() with override jumps to override body
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2ov01_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func Do(): int { return 1 }";
                string mainSource =
                    "include \"lib\" as B\n"
                    + "override func B.Do(): int { return 42 }\n"
                    + "func test() {\n"
                    + "    var r: int = B.Do()\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition on "Do" in B.Do() call — line 3, col 19
                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 3, 19));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defResp = session.FindResponse(defId);
                JsonObject defResult = defResp != null ? defResp.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;
                JsonObject defRange = defResult != null ? defResult.GetObject(LspFields.Range) : null;
                JsonObject defStart = defRange != null ? defRange.GetObject(LspFields.Start) : null;
                int defLine = defStart != null ? defStart.GetInt(LspFields.Line, -1) : -1;

                Assert(
                    defUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && defLine == 1,
                    "LSPNEW-T2-OV-01: go-to-definition on B.Do() jumps to override at main.ffs line 1");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-OV-02: references on override include original + override + call
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2ov02_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func Do(): int { return 1 }";
                string mainSource =
                    "include \"lib\" as B\n"
                    + "override func B.Do(): int { return 42 }\n"
                    + "func test() {\n"
                    + "    var r: int = B.Do()\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // References on "Do" in override declaration — line 1, col 16
                int refId = session.AddRequest(LspMethods.References, BuildReferencesParams(mainUri, 1, 16, true));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject refResp = session.FindResponse(refId);
                List<object> refLocs = refResp != null ? refResp.GetArray(JsonRpcFields.Result) : null;
                bool hasLib = false, hasMainOverride = false, hasMainCall = false;
                if (refLocs != null)
                {
                    for (int i = 0; i < refLocs.Count; i++)
                    {
                        if (!(refLocs[i] is JsonObject loc)) continue;
                        string u = loc.GetString(LspFields.Uri) ?? string.Empty;
                        JsonObject r = loc.GetObject(LspFields.Range);
                        JsonObject s = r != null ? r.GetObject(LspFields.Start) : null;
                        int ln = s != null ? s.GetInt(LspFields.Line, -1) : -1;
                        if (u.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;
                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && ln == 1) hasMainOverride = true;
                        if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0 && ln == 3) hasMainCall = true;
                    }
                }

                Assert(
                    refLocs != null && hasLib && hasMainOverride && hasMainCall,
                    "LSPNEW-T2-OV-02: references on override include original + override decl + call site");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-OV-03: override of non-existing function produces no spurious binding
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2ov03_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func Foo(): int { return 1 }";
                string mainSource =
                    "include \"lib\" as B\n"
                    + "override func B.NotExist(): int { return 0 }\n"
                    + "func test() {\n"
                    + "    var r: int = B.Foo()\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition on B.Foo() — should still go to lib.ffs, not affected by invalid override
                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 3, 19));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defResp = session.FindResponse(defId);
                JsonObject defResult = defResp != null ? defResp.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-OV-03: B.Foo() still resolves to lib.ffs despite invalid override of B.NotExist");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-ID-01: rename via aliased call updates both files
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2id01_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func add(a: int, b: int): int { return a + b }";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func test() {\n"
                    + "    var r: int = U.add(1, 2)\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Rename "add" in U.add() — line 2, col 19 (0-based)
                int renameId = session.AddRequest(LspMethods.Rename, BuildRenameParams(mainUri, 2, 19, "sum"));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject renameResp = session.FindResponse(renameId);
                JsonObject renameResult = renameResp != null ? renameResp.GetObject(JsonRpcFields.Result) : null;
                JsonObject changes = renameResult != null ? renameResult.GetObject(LspFields.Changes) : null;
                bool hasLib = false, hasMain = false;
                if (changes != null)
                {
                    foreach (string key in changes.Keys)
                    {
                        if (key.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasLib = true;
                        if (key.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0) hasMain = true;
                    }
                }

                Assert(
                    changes != null && hasLib && hasMain,
                    "LSPNEW-T2-ID-01: rename via U.add() updates both lib.ffs and main.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-ID-02: aliased include path go-to-definition
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2id02_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource = "func helper(): int { return 42 }";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func test() {\n"
                    + "    var r: int = U.helper()\n"
                    + "    wait r\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // Definition on "lib" path literal — line 0, col 9 (0-based: first char inside quotes)
                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 0, 9));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defResp = session.FindResponse(defId);
                JsonObject defResult = defResp != null ? defResp.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-ID-02: go-to-definition on aliased include path jumps to lib.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-ID-03: duplicate alias diagnostic
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2id03_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "a.ffs"), "func fa(): int { return 1 }");
                File.WriteAllText(Path.Combine(tmpDir, "b.ffs"), "func fb(): int { return 2 }");
                string mainSource =
                    "include \"a\" as U\n"
                    + "include \"b\" as U\n"
                    + "func test() { wait 1 }";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());
                session.AddNotification(LspMethods.DidOpen, BuildDidOpenParams(mainUri, "ffscript", 1, mainSource));

                // Dummy request to flush diagnostics
                int hoverId = session.AddRequest(LspMethods.Hover, BuildTextDocumentPositionParams(mainUri, 2, 5));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                List<JsonObject> diagNotifs = session.FindAllNotifications(LspMethods.PublishDiagnostics);
                bool foundDuplicateError = false;
                for (int dn = 0; dn < diagNotifs.Count; dn++)
                {
                    JsonObject p = diagNotifs[dn].GetObject(JsonRpcFields.Params);
                    if (p == null) continue;
                    string u = p.GetString(LspFields.Uri) ?? string.Empty;
                    if (u.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) < 0
                        && u.IndexOf("main.ffs", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    List<object> diags = p.GetArray(LspFields.Diagnostics);
                    if (diags == null) continue;
                    for (int dj = 0; dj < diags.Count; dj++)
                    {
                        if (diags[dj] is JsonObject d)
                        {
                            string msg = d.GetString("message") ?? string.Empty;
                            if (msg.IndexOf("Duplicate", StringComparison.OrdinalIgnoreCase) >= 0
                                && msg.IndexOf("alias", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foundDuplicateError = true;
                            }
                        }
                    }
                }

                Assert(
                    foundDuplicateError,
                    "LSPNEW-T2-ID-03: duplicate include alias produces diagnostic");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-ID-04: mixed plain include + alias + override coexist
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2id04_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func shared(): int { return 1 }");
                File.WriteAllText(Path.Combine(tmpDir, "math.ffs"), "func calc(): int { return 2 }");
                string mainSource =
                    "include \"lib\"\n"
                    + "include \"math\" as M\n"
                    + "override func M.calc(): int { return 99 }\n"
                    + "func test() {\n"
                    + "    var a: int = shared()\n"
                    + "    var b: int = M.calc()\n"
                    + "    wait a + b\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                // shared() def — line 4, col 17 (plain include)
                int defSharedId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 4, 17));
                // M.calc() def — line 5, col 19 (override)
                int defCalcId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 5, 19));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                // shared() → lib.ffs
                JsonObject sharedResp = session.FindResponse(defSharedId);
                JsonObject sharedResult = sharedResp != null ? sharedResp.GetObject(JsonRpcFields.Result) : null;
                string sharedUri = sharedResult != null ? sharedResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                // M.calc() → main.ffs (override)
                JsonObject calcResp = session.FindResponse(defCalcId);
                JsonObject calcResult = calcResp != null ? calcResp.GetObject(JsonRpcFields.Result) : null;
                string calcUri = calcResult != null ? calcResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    sharedUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && calcUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-ID-04: mixed plain include + alias + override — shared→lib, M.calc→main override");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-GAP-01: include path × plain include — go-to-definition
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2gap01_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 42 }");
                string mainSource = "include \"lib\"\nfunc test() {\n    var r: int = helper()\n    wait r\n}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int defId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 0, 9));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject defResp = session.FindResponse(defId);
                JsonObject defResult = defResp != null ? defResp.GetObject(JsonRpcFields.Result) : null;
                string defUri = defResult != null ? defResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    defUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-GAP-01: go-to-definition on plain include path jumps to lib.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-GAP-02: struct + enum × alias — definition via U.Vec, U.Dir
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2gap02_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource =
                    "struct Vec { x: int, y: int }\n"
                    + "enum Dir { Up, Down }";
                string mainSource =
                    "include \"lib\" as U\n"
                    + "func test() {\n"
                    + "    var v: U.Vec = U.Vec { x: 1, y: 2 }\n"
                    + "    var d: U.Dir = U.Dir.Up\n"
                    + "    wait v.x\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int defVecId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 14));
                int defDirId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 3, 14));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject vecResp = session.FindResponse(defVecId);
                JsonObject vecResult = vecResp != null ? vecResp.GetObject(JsonRpcFields.Result) : null;
                string vecUri = vecResult != null ? vecResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                JsonObject dirResp = session.FindResponse(defDirId);
                JsonObject dirResult = dirResp != null ? dirResp.GetObject(JsonRpcFields.Result) : null;
                string dirUri = dirResult != null ? dirResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    vecUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && dirUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-GAP-02: aliased struct U.Vec and enum U.Dir both resolve to lib.ffs");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-GAP-03: override non-func types — const override definition
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2gap03_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                string libSource =
                    "const LIMIT: int = 10\n"
                    + "struct Cfg { val: int }\n"
                    + "enum Mode { Fast, Slow }";
                string mainSource =
                    "include \"lib\" as B\n"
                    + "override const B.LIMIT: int = 99\n"
                    + "override struct B.Cfg { val: int, extra: int }\n"
                    + "override enum B.Mode { Fast, Slow, Medium }\n"
                    + "func test() {\n"
                    + "    var x: int = B.LIMIT\n"
                    + "    wait x\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), libSource);
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int defLimitId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 5, 19));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject limitResp = session.FindResponse(defLimitId);
                JsonObject limitResult = limitResp != null ? limitResp.GetObject(JsonRpcFields.Result) : null;
                string limitUri = limitResult != null ? limitResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;
                JsonObject limitRange = limitResult != null ? limitResult.GetObject(LspFields.Range) : null;
                JsonObject limitStart = limitRange != null ? limitRange.GetObject(LspFields.Start) : null;
                int limitLine = limitStart != null ? limitStart.GetInt(LspFields.Line, -1) : -1;

                Assert(
                    limitUri.IndexOf("/main.ffs", StringComparison.OrdinalIgnoreCase) >= 0
                    && limitLine == 1,
                    "LSPNEW-T2-GAP-03: override const B.LIMIT → definition at main.ffs override line");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        // ================================================================
        // LSPNEW-T2-GAP-04: enum type × plain include — cross-file definition
        // ================================================================
        {
            string tmpDir = Path.Combine(Path.GetTempPath(), "lspnew_t2gap04_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.CreateDirectory(tmpDir);
                File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "enum Dir { Up, Down }");
                string mainSource =
                    "include \"lib\"\n"
                    + "func test() {\n"
                    + "    var d: Dir = Dir.Up\n"
                    + "    wait 1\n"
                    + "}";
                File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);

                string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
                string mainUri = rootUri + "/main.ffs";

                var bridge = new DatabaseBackedVsCodeBridge(
                    new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
                var session = new LspServerNewBatchSession();
                session.AddRequest(LspMethods.Initialize, BuildInitializeParams(tmpDir));
                session.AddNotification(LspMethods.Initialized, new JsonObject());

                int defDirId = session.AddRequest(LspMethods.Definition, BuildTextDocumentPositionParams(mainUri, 2, 17));
                session.AddNotification(LspMethods.Exit, new JsonObject());
                session.Run(bridge);

                JsonObject dirResp = session.FindResponse(defDirId);
                JsonObject dirResult = dirResp != null ? dirResp.GetObject(JsonRpcFields.Result) : null;
                string dirUri = dirResult != null ? dirResult.GetString(LspFields.Uri) ?? string.Empty : string.Empty;

                Assert(
                    dirUri.IndexOf("/lib.ffs", StringComparison.OrdinalIgnoreCase) >= 0,
                    "LSPNEW-T2-GAP-04: enum Dir in Dir.Up resolves to lib.ffs across plain include");
            }
            finally
            {
                try { Directory.Delete(tmpDir, true); } catch { }
            }
        }

        Debug.Log($"[LspServerNewTests] Completed. Passed={passed}, Failed={failed}");
    }

    private static JsonObject BuildInitializeParams(string rootPath)
    {
        var parameters = new JsonObject();
        if (!string.IsNullOrWhiteSpace(rootPath))
            parameters.Set("rootPath", rootPath);
        return parameters;
    }

    private static JsonObject BuildDidOpenParams(string uri, string languageId, int version, string text)
    {
        var textDocument = new JsonObject();
        textDocument.Set("uri", uri);
        textDocument.Set("languageId", languageId);
        textDocument.Set("version", version);
        textDocument.Set("text", text);

        var parameters = new JsonObject();
        parameters.Set("textDocument", textDocument);
        return parameters;
    }

    private static JsonObject BuildDidChangeParams(string uri, int version, string text)
    {
        var textDocument = new JsonObject();
        textDocument.Set("uri", uri);
        textDocument.Set("version", version);

        var fullTextChange = new JsonObject();
        fullTextChange.Set("text", text);

        var changes = new List<object> { fullTextChange };

        var parameters = new JsonObject();
        parameters.Set("textDocument", textDocument);
        parameters.Set("contentChanges", changes);
        return parameters;
    }

    private static JsonObject BuildDidChangeIncrementalParams(
        string uri,
        int version,
        IReadOnlyList<(int startLine, int startCharacter, int endLine, int endCharacter, string text)> changes)
    {
        var textDocument = new JsonObject();
        textDocument.Set("uri", uri);
        textDocument.Set("version", version);

        var contentChanges = new List<object>();
        if (changes != null)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                (int startLine, int startCharacter, int endLine, int endCharacter, string text) change = changes[i];

                var start = new JsonObject();
                start.Set("line", change.startLine);
                start.Set("character", change.startCharacter);

                var end = new JsonObject();
                end.Set("line", change.endLine);
                end.Set("character", change.endCharacter);

                var range = new JsonObject();
                range.Set("start", start);
                range.Set("end", end);

                var contentChange = new JsonObject();
                contentChange.Set("range", range);
                contentChange.Set("text", change.text ?? string.Empty);
                contentChanges.Add(contentChange);
            }
        }

        var parameters = new JsonObject();
        parameters.Set("textDocument", textDocument);
        parameters.Set("contentChanges", contentChanges);
        return parameters;
    }

    private static JsonObject BuildDidChangeWatchedFilesParams(IReadOnlyList<(string uri, int changeType)> changes)
    {
        var items = new List<object>();
        if (changes != null)
        {
            for (int i = 0; i < changes.Count; i++)
            {
                (string uri, int changeType) change = changes[i];
                var item = new JsonObject();
                item.Set("uri", change.uri ?? string.Empty);
                item.Set("type", change.changeType);
                items.Add(item);
            }
        }

        var parameters = new JsonObject();
        parameters.Set("changes", items);
        return parameters;
    }

    private static JsonObject BuildReferencesParams(string uri, int line, int character, bool includeDeclaration)
    {
        JsonObject parameters = BuildTextDocumentPositionParams(uri, line, character);

        var context = new JsonObject();
        context.Set("includeDeclaration", includeDeclaration);
        parameters.Set("context", context);

        return parameters;
    }

    private static JsonObject BuildTextDocumentPositionParams(string uri, int line, int character)
    {
        var parameters = new JsonObject();

        var textDocument = new JsonObject();
        textDocument.Set("uri", uri);
        parameters.Set("textDocument", textDocument);

        var position = new JsonObject();
        position.Set("line", line);
        position.Set("character", character);
        parameters.Set("position", position);

        return parameters;
    }

    private static JsonObject BuildRenameParams(string uri, int line, int character, string newName)
    {
        JsonObject parameters = BuildTextDocumentPositionParams(uri, line, character);
        parameters.Set("newName", newName ?? string.Empty);
        return parameters;
    }

    private static bool ContainsCompletionLabel(IReadOnlyList<object> items, string expectedLabel)
    {
        if (items == null || string.IsNullOrWhiteSpace(expectedLabel))
            return false;

        for (int i = 0; i < items.Count; i++)
        {
            JsonObject item = items[i] as JsonObject;
            string label = item != null ? item.GetString(LspFields.Label) : string.Empty;
            if (string.Equals(label, expectedLabel, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool AreLocationsSortedAndUnique(IReadOnlyList<object> locations)
    {
        if (locations == null)
            return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string previousSortKey = string.Empty;

        for (int i = 0; i < locations.Count; i++)
        {
            if (!(locations[i] is JsonObject location))
                continue;

            string uri = location.GetString(LspFields.Uri) ?? string.Empty;
            JsonObject start = location.GetObject(LspFields.Range)?.GetObject(LspFields.Start);
            int line = start != null ? start.GetInt(LspFields.Line, 0) : 0;
            int character = start != null ? start.GetInt(LspFields.Character, 0) : 0;

            string sortKey = (uri.ToLowerInvariant())
                + "|" + line.ToString("D8")
                + "|" + character.ToString("D8");

            if (previousSortKey.Length > 0 && string.Compare(sortKey, previousSortKey, StringComparison.Ordinal) < 0)
                return false;

            if (!seen.Add(sortKey))
                return false;

            previousSortKey = sortKey;
        }

        return true;
    }

    private sealed class LspServerNewBatchSession
    {
        private readonly MemoryStream _input = new MemoryStream();
        private List<JsonObject> _messages = new List<JsonObject>();
        private int _nextRequestId = 1;

        public int AddRequest(string method, JsonObject parameters)
        {
            int id = _nextRequestId++;

            var request = new JsonObject();
            request.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            request.Set(JsonRpcFields.Id, id);
            request.Set(JsonRpcFields.Method, method ?? string.Empty);
            request.Set(JsonRpcFields.Params, parameters ?? new JsonObject());

            ContentLengthStream.WriteMessage(_input, request.ToJson());
            return id;
        }

        public void AddNotification(string method, JsonObject parameters)
        {
            var notification = new JsonObject();
            notification.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            notification.Set(JsonRpcFields.Method, method ?? string.Empty);
            notification.Set(JsonRpcFields.Params, parameters ?? new JsonObject());

            ContentLengthStream.WriteMessage(_input, notification.ToJson());
        }

        public void AddResponse(int id, JsonObject result)
        {
            var response = new JsonObject();
            response.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            response.Set(JsonRpcFields.Id, id);
            response.Set(JsonRpcFields.Result, result != null ? (object)result : null);

            ContentLengthStream.WriteMessage(_input, response.ToJson());
        }

        public void Run(ILspVsCodeDatabaseBridge bridge)
        {
            _input.Position = 0;
            var output = new MemoryStream();

            var server = new LspServerNew(_input, output, bridge);
            server.Run();

            output.Position = 0;
            _messages = new List<JsonObject>();

            while (true)
            {
                string raw = ContentLengthStream.ReadMessage(output);
                if (raw == null)
                    break;

                JsonObject parsed = JsonObject.Parse(raw);
                if (parsed != null)
                    _messages.Add(parsed);
            }
        }

        public JsonObject FindResponse(int id)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                JsonObject message = _messages[i];
                if (message == null)
                    continue;

                bool hasMethod = message.ContainsKey(JsonRpcFields.Method);
                bool hasId = message.ContainsKey(JsonRpcFields.Id);
                if (!hasMethod && hasId && message.GetInt(JsonRpcFields.Id, -1) == id)
                    return message;
            }

            return null;
        }

        public JsonObject FindFirstRequest(string method)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                JsonObject message = _messages[i];
                if (message == null)
                    continue;

                bool hasMethod = message.ContainsKey(JsonRpcFields.Method);
                bool hasId = message.ContainsKey(JsonRpcFields.Id);
                if (hasMethod
                    && hasId
                    && string.Equals(message.GetString(JsonRpcFields.Method), method, StringComparison.Ordinal))
                {
                    return message;
                }
            }

            return null;
        }

        public JsonObject FindFirstNotification(string method)
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                JsonObject message = _messages[i];
                if (message == null)
                    continue;

                bool hasMethod = message.ContainsKey(JsonRpcFields.Method);
                bool hasId = message.ContainsKey(JsonRpcFields.Id);
                if (hasMethod
                    && !hasId
                    && string.Equals(message.GetString(JsonRpcFields.Method), method, StringComparison.Ordinal))
                {
                    return message;
                }
            }

            return null;
        }

        public List<JsonObject> FindAllNotifications(string method)
        {
            var results = new List<JsonObject>();
            for (int i = 0; i < _messages.Count; i++)
            {
                JsonObject message = _messages[i];
                if (message == null)
                    continue;

                bool hasMethod = message.ContainsKey(JsonRpcFields.Method);
                bool hasId = message.ContainsKey(JsonRpcFields.Id);
                if (hasMethod
                    && !hasId
                    && string.Equals(message.GetString(JsonRpcFields.Method), method, StringComparison.Ordinal))
                {
                    results.Add(message);
                }
            }

            return results;
        }
    }
}
