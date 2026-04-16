using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;
using FFVM.Debug.Lsp.Integration.VsCode;

namespace FFVM.Debug
{
    /// <summary>
    /// New LSP server scaffold for VS Code integration.
    ///
    /// This class only provides protocol wiring:
    /// 1) Content-Length framed transport.
    /// 2) JSON-RPC request/notification dispatch.
    /// 3) Empty business bridge methods for future module integration.
    ///
    /// No language feature logic is implemented in this file.
    /// </summary>
    public class LspServerNew
    {
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly ILspVsCodeDatabaseBridge _bridge;

        private bool _running;
        private bool _shutdownRequested;

        public LspServerNew(Stream input, Stream output, ILspVsCodeDatabaseBridge bridge = null)
        {
            _input = input;
            _output = output;
            _bridge = bridge ?? new NoOpLspVsCodeDatabaseBridge();
        }

        /// <summary>
        /// Main loop connected to VS Code over stdio.
        /// Reads JSON-RPC messages and dispatches to request/notification handlers.
        /// </summary>
        public void Run()
        {
            _running = true;

            while (_running)
            {
                string messageText = ContentLengthStream.ReadMessage(_input);
                if (messageText == null)
                    break;

                JsonObject message = JsonObject.Parse(messageText);
                if (message == null)
                    continue;

                HandleIncomingMessage(message);
            }
        }

        private void HandleIncomingMessage(JsonObject message)
        {
            string method = message.GetString("method");
            object id = message.Get("id");

            bool hasMethod = !string.IsNullOrEmpty(method);
            bool isRequest = hasMethod && id != null;
            bool isNotification = hasMethod && id == null;

            if (isRequest)
            {
                HandleRequest(message, method, id);
                return;
            }

            if (isNotification)
            {
                HandleNotification(message, method);
            }
        }

        private void HandleRequest(JsonObject request, string method, object id)
        {
            JsonObject requestParams = request.GetObject("params");

            switch (method)
            {
                case "initialize":
                    BridgeInitialize(requestParams);
                    SendResponse(id, CreateInitializeResult());
                    break;

                case "shutdown":
                    BridgeShutdown(requestParams);
                    _shutdownRequested = true;
                    SendResponse(id, null);
                    break;

                case "textDocument/documentSymbol":
                    SendResponse(id, BridgeDocumentSymbol(requestParams));
                    break;

                case "textDocument/hover":
                    SendResponse(id, BridgeHover(requestParams));
                    break;

                case "textDocument/definition":
                    SendResponse(id, BridgeDefinition(requestParams));
                    break;

                case "textDocument/references":
                    SendResponse(id, BridgeReferences(requestParams));
                    break;

                case "textDocument/completion":
                    SendResponse(id, BridgeCompletion(requestParams));
                    break;

                case "textDocument/signatureHelp":
                    SendResponse(id, BridgeSignatureHelp(requestParams));
                    break;

                case "textDocument/rename":
                    SendResponse(id, BridgeRename(requestParams));
                    break;

                case "textDocument/prepareRename":
                    SendResponse(id, BridgePrepareRename(requestParams));
                    break;

                case "textDocument/semanticTokens/full":
                    SendResponse(id, BridgeSemanticTokensFull(requestParams));
                    break;

                case "workspace/willRenameFiles":
                    SendResponse(id, BridgeWillRenameFiles(requestParams));
                    break;

                default:
                    SendError(id, -32601, "Method not found: " + method);
                    break;
            }
        }

        private void HandleNotification(JsonObject notification, string method)
        {
            JsonObject notificationParams = notification.GetObject("params");

            switch (method)
            {
                case "initialized":
                    BridgeInitialized(notificationParams);
                    break;

                case "exit":
                    BridgeExit(notificationParams);
                    _running = false;
                    break;

                case "textDocument/didOpen":
                    BridgeDidOpen(notificationParams);
                    break;

                case "textDocument/didChange":
                    BridgeDidChange(notificationParams);
                    break;

                case "textDocument/didClose":
                    BridgeDidClose(notificationParams);
                    break;

                case "workspace/didChangeWatchedFiles":
                    BridgeDidChangeWatchedFiles(notificationParams);
                    break;
            }
        }

        private JsonObject CreateInitializeResult()
        {
            var result = new JsonObject();

            // Minimal capability payload to complete VS Code initialize handshake.
            var capabilities = new JsonObject();
            result.Set("capabilities", capabilities);

            var serverInfo = new JsonObject();
            serverInfo.Set("name", "FFVM LSP (New Scaffold)");
            serverInfo.Set("version", "0.1.0-placeholder");
            result.Set("serverInfo", serverInfo);

            return result;
        }

        private void SendResponse(object id, object result)
        {
            var response = new JsonObject();
            response.Set("jsonrpc", "2.0");
            response.Set("id", id);
            response.Set("result", result);
            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        private void SendError(object id, int code, string message)
        {
            var error = new JsonObject();
            error.Set("code", code);
            error.Set("message", message ?? "Unknown error");

            var response = new JsonObject();
            response.Set("jsonrpc", "2.0");
            response.Set("id", id);
            response.Set("error", error);

            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        /// <summary>
        /// Outbound bridge for publishDiagnostics.
        /// Business producers should pass already-built diagnostics payload.
        /// </summary>
        public void PublishDiagnostics(string uri, List<object> diagnostics, int? version = null)
        {
            var payload = new JsonObject();
            payload.Set("uri", DocumentKeyNormalizer.Normalize(uri));
            payload.Set("diagnostics", diagnostics ?? new List<object>());
            if (version.HasValue)
                payload.Set("version", version.Value);

            SendNotification("textDocument/publishDiagnostics", payload);
        }

        private void SendNotification(string method, object notificationParams)
        {
            var notification = new JsonObject();
            notification.Set("jsonrpc", "2.0");
            notification.Set("method", method ?? string.Empty);
            notification.Set("params", notificationParams ?? new JsonObject());
            ContentLengthStream.WriteMessage(_output, notification.ToJson());
        }

        private void FlushBridgeDiagnostics()
        {
            while (_bridge.TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics) && diagnostics != null)
            {
                PublishDiagnostics(
                    diagnostics.Uri,
                    diagnostics.Diagnostics != null ? new List<object>(diagnostics.Diagnostics) : null,
                    diagnostics.Version);
            }
        }

        // ------------------------------------------------------------
        // VS Code bridge points (delegated to ILspVsCodeDatabaseBridge)
        // ------------------------------------------------------------

        private void BridgeInitialize(JsonObject initializeParams)
        {
            _bridge.Initialize(initializeParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeShutdown(JsonObject shutdownParams)
        {
            _bridge.Shutdown(shutdownParams);
            FlushBridgeDiagnostics();
        }

        private object BridgeDocumentSymbol(JsonObject requestParams)
        {
            IReadOnlyList<LspDocumentSymbolItem> result = _bridge.QueryDocumentSymbol(requestParams);
            FlushBridgeDiagnostics();
            return ConvertDocumentSymbols(result);
        }

        private object BridgeHover(JsonObject requestParams)
        {
            LspHoverPayload result = _bridge.QueryHover(requestParams);
            FlushBridgeDiagnostics();
            return ConvertHover(result);
        }

        private object BridgeDefinition(JsonObject requestParams)
        {
            LspDefinitionPayload result = _bridge.QueryDefinition(requestParams);
            FlushBridgeDiagnostics();
            return ConvertDefinition(result);
        }

        private object BridgeReferences(JsonObject requestParams)
        {
            IReadOnlyList<LspReferenceItem> result = _bridge.QueryReferences(requestParams);
            FlushBridgeDiagnostics();
            return ConvertReferences(result);
        }

        private object BridgeCompletion(JsonObject requestParams)
        {
            IReadOnlyList<LspCompletionItem> result = _bridge.QueryCompletion(requestParams);
            FlushBridgeDiagnostics();
            return ConvertCompletionItems(result);
        }

        private object BridgeSignatureHelp(JsonObject requestParams)
        {
            LspSignatureHelpPayload result = _bridge.QuerySignatureHelp(requestParams);
            FlushBridgeDiagnostics();
            return ConvertSignatureHelp(result);
        }

        private object BridgeRename(JsonObject requestParams)
        {
            LspRenamePayload result = _bridge.QueryRename(requestParams);
            FlushBridgeDiagnostics();
            return ConvertRename(result);
        }

        private object BridgePrepareRename(JsonObject requestParams)
        {
            LspPrepareRenamePayload result = _bridge.QueryPrepareRename(requestParams);
            FlushBridgeDiagnostics();
            return ConvertPrepareRename(result);
        }

        private object BridgeSemanticTokensFull(JsonObject requestParams)
        {
            LspSemanticTokensPayload result = _bridge.QuerySemanticTokensFull(requestParams);
            FlushBridgeDiagnostics();
            return ConvertSemanticTokens(result);
        }

        private object BridgeWillRenameFiles(JsonObject requestParams)
        {
            JsonObject result = _bridge.QueryWillRenameFiles(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private void BridgeInitialized(JsonObject initializedParams)
        {
            _bridge.Initialized(initializedParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeExit(JsonObject exitParams)
        {
            _bridge.Exit(exitParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeDidOpen(JsonObject didOpenParams)
        {
            _bridge.DidOpen(didOpenParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeDidChange(JsonObject didChangeParams)
        {
            _bridge.DidChange(didChangeParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeDidClose(JsonObject didCloseParams)
        {
            _bridge.DidClose(didCloseParams);
            FlushBridgeDiagnostics();
        }

        private void BridgeDidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams)
        {
            _bridge.DidChangeWatchedFiles(didChangeWatchedFilesParams);
            FlushBridgeDiagnostics();
        }

        // ------------------------------------------------------------
        // Typed payload -> protocol JSON projection
        // ------------------------------------------------------------

        private static List<object> ConvertDocumentSymbols(IReadOnlyList<LspDocumentSymbolItem> symbols)
        {
            if (symbols == null || symbols.Count == 0)
                return new List<object>(0);

            var output = new List<object>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                LspDocumentSymbolItem symbol = symbols[i];
                if (symbol == null)
                    continue;

                JsonObject range = MakeRangeFromSpan(symbol.DeclarationSpan);
                var item = new JsonObject();
                item.Set("name", symbol.Name ?? string.Empty);
                item.Set("kind", MapDocumentSymbolKind(symbol.Kind));
                item.Set("range", range);
                item.Set("selectionRange", range);
                output.Add(item);
            }

            return output;
        }

        private static JsonObject ConvertHover(LspHoverPayload payload)
        {
            if (payload == null)
                return null;

            string summary = payload.Summary ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(payload.Scope))
                summary += "\n\nScope: " + payload.Scope;
            if (!string.IsNullOrWhiteSpace(payload.ParentName))
                summary += "\nParent: " + payload.ParentName;

            var contents = new JsonObject();
            contents.Set("kind", "markdown");
            contents.Set("value", summary);

            var result = new JsonObject();
            result.Set("contents", contents);
            return result;
        }

        private static JsonObject ConvertDefinition(LspDefinitionPayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set("uri", DocumentKeyNormalizer.Normalize(payload.DocumentKey));
            result.Set("range", MakeRangeFromPayload(payload.SourcePayload, payload.Span));
            return result;
        }

        private static List<object> ConvertReferences(IReadOnlyList<LspReferenceItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<object>(0);

            var output = new List<object>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                LspReferenceItem item = items[i];
                if (item == null)
                    continue;

                var location = new JsonObject();
                location.Set("uri", DocumentKeyNormalizer.Normalize(item.DocumentKey));
                location.Set("range", MakeRangeFromPayload(item.SourcePayload, item.Span));
                output.Add(location);
            }

            return output;
        }

        private static List<object> ConvertCompletionItems(IReadOnlyList<LspCompletionItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<object>(0);

            var output = new List<object>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                LspCompletionItem item = items[i];
                if (item == null)
                    continue;

                var projected = new JsonObject();
                projected.Set("label", item.Label ?? string.Empty);
                projected.Set("kind", MapCompletionKind(item.Kind));
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    projected.Set("detail", item.Detail);
                output.Add(projected);
            }

            return output;
        }

        private static JsonObject ConvertSignatureHelp(LspSignatureHelpPayload payload)
        {
            if (payload == null)
                return null;

            var signatures = new List<object>();
            if (payload.Signatures != null)
            {
                for (int i = 0; i < payload.Signatures.Count; i++)
                {
                    LspSignatureItem signature = payload.Signatures[i];
                    if (signature == null)
                        continue;

                    var projected = new JsonObject();
                    projected.Set("label", signature.Label ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(signature.Source))
                    {
                        var doc = new JsonObject();
                        doc.Set("kind", "markdown");
                        doc.Set("value", "Source: " + DocumentKeyNormalizer.Normalize(signature.Source));
                        projected.Set("documentation", doc);
                    }

                    signatures.Add(projected);
                }
            }

            var result = new JsonObject();
            result.Set("signatures", signatures);
            result.Set("activeSignature", payload.ActiveSignature);
            result.Set("activeParameter", payload.ActiveParameter);
            return result;
        }

        private static JsonObject ConvertPrepareRename(LspPrepareRenamePayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set("range", MakeRangeFromSpan(payload.Range));
            result.Set("placeholder", payload.Placeholder ?? string.Empty);
            return result;
        }

        private static JsonObject ConvertRename(LspRenamePayload payload)
        {
            if (payload == null || payload.Edits == null || payload.Edits.Count == 0)
                return null;

            var changes = new JsonObject();
            var grouped = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < payload.Edits.Count; i++)
            {
                LspRenameEdit edit = payload.Edits[i];
                if (edit == null)
                    continue;

                string key = DocumentKeyNormalizer.Normalize(edit.DocumentKey);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!grouped.TryGetValue(key, out List<object> edits))
                {
                    edits = new List<object>();
                    grouped[key] = edits;
                }

                var textEdit = new JsonObject();
                textEdit.Set("range", MakeRangeFromSpan(edit.Range));
                textEdit.Set("newText", edit.NewText ?? string.Empty);
                edits.Add(textEdit);
            }

            foreach (KeyValuePair<string, List<object>> pair in grouped)
                changes.Set(pair.Key, pair.Value);

            var result = new JsonObject();
            result.Set("changes", changes);
            return result;
        }

        private static JsonObject ConvertSemanticTokens(LspSemanticTokensPayload payload)
        {
            if (payload == null)
                return null;

            var data = new List<object>();
            if (payload.Data != null)
            {
                for (int i = 0; i < payload.Data.Count; i++)
                    data.Add(payload.Data[i]);
            }

            var result = new JsonObject();
            result.Set("data", data);
            return result;
        }

        private static JsonObject MakeRangeFromPayload(DataFactPayload payload, TextSpan fallbackSpan)
        {
            if (payload is SymbolDataFactPayload symbol && symbol.HasRange)
            {
                return MakeRange(
                    symbol.StartLine,
                    symbol.StartCharacter,
                    symbol.EndLine,
                    symbol.EndCharacter);
            }

            return MakeRangeFromSpan(fallbackSpan);
        }

        private static JsonObject MakeRangeFromSpan(TextSpan span)
        {
            int start = span.Start < 0 ? 0 : span.Start;
            int length = span.Length <= 0 ? 1 : span.Length;
            return MakeRange(0, start, 0, start + length);
        }

        private static JsonObject MakeRange(int startLine, int startCharacter, int endLine, int endCharacter)
        {
            var start = new JsonObject();
            start.Set("line", startLine < 0 ? 0 : startLine);
            start.Set("character", startCharacter < 0 ? 0 : startCharacter);

            var end = new JsonObject();
            end.Set("line", endLine < 0 ? 0 : endLine);
            end.Set("character", endCharacter < 0 ? 0 : endCharacter);

            var range = new JsonObject();
            range.Set("start", start);
            range.Set("end", end);
            return range;
        }

        private static int MapDocumentSymbolKind(string kind)
        {
            if (string.Equals(kind, "Function", StringComparison.OrdinalIgnoreCase))
                return 12;
            if (string.Equals(kind, "Struct", StringComparison.OrdinalIgnoreCase))
                return 23;
            if (string.Equals(kind, "Enum", StringComparison.OrdinalIgnoreCase))
                return 10;
            if (string.Equals(kind, "Variable", StringComparison.OrdinalIgnoreCase))
                return 13;
            if (string.Equals(kind, "Parameter", StringComparison.OrdinalIgnoreCase))
                return 26;
            return 13;
        }

        private static int MapCompletionKind(string kind)
        {
            if (string.Equals(kind, "Function", StringComparison.OrdinalIgnoreCase))
                return 3;
            if (string.Equals(kind, "Struct", StringComparison.OrdinalIgnoreCase))
                return 22;
            if (string.Equals(kind, "Enum", StringComparison.OrdinalIgnoreCase))
                return 13;
            if (string.Equals(kind, "StructField", StringComparison.OrdinalIgnoreCase))
                return 5;
            if (string.Equals(kind, "EnumMember", StringComparison.OrdinalIgnoreCase))
                return 20;
            if (string.Equals(kind, "Variable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, "Parameter", StringComparison.OrdinalIgnoreCase))
                return 6;
            return 1;
        }
    }
}
