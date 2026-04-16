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

        private static class JsonRpcFields
        {
            public const string JsonRpc = "jsonrpc";
            public const string Version = "2.0";
            public const string Method = "method";
            public const string Id = "id";
            public const string Params = "params";
            public const string Result = "result";
            public const string Error = "error";
            public const string Code = "code";
            public const string Message = "message";
        }

        private static class LspMethods
        {
            public const string Initialize = "initialize";
            public const string Shutdown = "shutdown";
            public const string DocumentSymbol = "textDocument/documentSymbol";
            public const string Hover = "textDocument/hover";
            public const string Definition = "textDocument/definition";
            public const string References = "textDocument/references";
            public const string Completion = "textDocument/completion";
            public const string SignatureHelp = "textDocument/signatureHelp";
            public const string Rename = "textDocument/rename";
            public const string PrepareRename = "textDocument/prepareRename";
            public const string SemanticTokensFull = "textDocument/semanticTokens/full";
            public const string WillRenameFiles = "workspace/willRenameFiles";
            public const string Initialized = "initialized";
            public const string Exit = "exit";
            public const string DidOpen = "textDocument/didOpen";
            public const string DidChange = "textDocument/didChange";
            public const string DidClose = "textDocument/didClose";
            public const string DidChangeWatchedFiles = "workspace/didChangeWatchedFiles";
            public const string PublishDiagnostics = "textDocument/publishDiagnostics";
        }

        private static class LspFields
        {
            public const string Capabilities = "capabilities";
            public const string ServerInfo = "serverInfo";
            public const string Name = "name";
            public const string Version = "version";
            public const string Uri = "uri";
            public const string Diagnostics = "diagnostics";
            public const string Contents = "contents";
            public const string Kind = "kind";
            public const string Value = "value";
            public const string Range = "range";
            public const string SelectionRange = "selectionRange";
            public const string Label = "label";
            public const string Detail = "detail";
            public const string Documentation = "documentation";
            public const string Signatures = "signatures";
            public const string ActiveSignature = "activeSignature";
            public const string ActiveParameter = "activeParameter";
            public const string Placeholder = "placeholder";
            public const string Changes = "changes";
            public const string Data = "data";
            public const string Start = "start";
            public const string End = "end";
            public const string Line = "line";
            public const string Character = "character";
            public const string NewText = "newText";
        }

        private static class LspValues
        {
            public const string ServerName = "FFVM LSP (New Scaffold)";
            public const string ServerVersion = "0.1.0-placeholder";
            public const string UnknownErrorMessage = "Unknown error";
            public const string Markdown = "markdown";
        }

        private static class SymbolKindNames
        {
            public const string Function = "Function";
            public const string Struct = "Struct";
            public const string Enum = "Enum";
            public const string Variable = "Variable";
            public const string Parameter = "Parameter";
            public const string StructField = "StructField";
            public const string EnumMember = "EnumMember";
        }

        private static class JsonRpcErrorCodes
        {
            public const int MethodNotFound = -32601;
        }

        private enum LspDocumentSymbolKindCode
        {
            EnumType = 10,
            Function = 12,
            Variable = 13,
            Struct = 23,
            Parameter = 26
        }

        private enum LspCompletionItemKindCode
        {
            Text = 1,
            Function = 3,
            Field = 5,
            Variable = 6,
            EnumType = 13,
            EnumMember = 20,
            Struct = 22
        }

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
            string method = message.GetString(JsonRpcFields.Method);
            object id = message.Get(JsonRpcFields.Id);

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
            JsonObject requestParams = request.GetObject(JsonRpcFields.Params);

            switch (method)
            {
                case LspMethods.Initialize:
                    BridgeInitialize(requestParams);
                    SendResponse(id, CreateInitializeResult());
                    break;

                case LspMethods.Shutdown:
                    BridgeShutdown(requestParams);
                    _shutdownRequested = true;
                    SendResponse(id, null);
                    break;

                case LspMethods.DocumentSymbol:
                    SendResponse(id, BridgeDocumentSymbol(requestParams));
                    break;

                case LspMethods.Hover:
                    SendResponse(id, BridgeHover(requestParams));
                    break;

                case LspMethods.Definition:
                    SendResponse(id, BridgeDefinition(requestParams));
                    break;

                case LspMethods.References:
                    SendResponse(id, BridgeReferences(requestParams));
                    break;

                case LspMethods.Completion:
                    SendResponse(id, BridgeCompletion(requestParams));
                    break;

                case LspMethods.SignatureHelp:
                    SendResponse(id, BridgeSignatureHelp(requestParams));
                    break;

                case LspMethods.Rename:
                    SendResponse(id, BridgeRename(requestParams));
                    break;

                case LspMethods.PrepareRename:
                    SendResponse(id, BridgePrepareRename(requestParams));
                    break;

                case LspMethods.SemanticTokensFull:
                    SendResponse(id, BridgeSemanticTokensFull(requestParams));
                    break;

                case LspMethods.WillRenameFiles:
                    SendResponse(id, BridgeWillRenameFiles(requestParams));
                    break;

                default:
                    SendError(id, JsonRpcErrorCodes.MethodNotFound, "Method not found: " + method);
                    break;
            }
        }

        private void HandleNotification(JsonObject notification, string method)
        {
            JsonObject notificationParams = notification.GetObject(JsonRpcFields.Params);

            switch (method)
            {
                case LspMethods.Initialized:
                    BridgeInitialized(notificationParams);
                    break;

                case LspMethods.Exit:
                    BridgeExit(notificationParams);
                    _running = false;
                    break;

                case LspMethods.DidOpen:
                    BridgeDidOpen(notificationParams);
                    break;

                case LspMethods.DidChange:
                    BridgeDidChange(notificationParams);
                    break;

                case LspMethods.DidClose:
                    BridgeDidClose(notificationParams);
                    break;

                case LspMethods.DidChangeWatchedFiles:
                    BridgeDidChangeWatchedFiles(notificationParams);
                    break;
            }
        }

        private JsonObject CreateInitializeResult()
        {
            var result = new JsonObject();

            // Minimal capability payload to complete VS Code initialize handshake.
            var capabilities = new JsonObject();
            result.Set(LspFields.Capabilities, capabilities);

            var serverInfo = new JsonObject();
            serverInfo.Set(LspFields.Name, LspValues.ServerName);
            serverInfo.Set(LspFields.Version, LspValues.ServerVersion);
            result.Set(LspFields.ServerInfo, serverInfo);

            return result;
        }

        private void SendResponse(object id, object result)
        {
            var response = new JsonObject();
            response.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            response.Set(JsonRpcFields.Id, id);
            response.Set(JsonRpcFields.Result, result);
            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        private void SendError(object id, int code, string message)
        {
            var error = new JsonObject();
            error.Set(JsonRpcFields.Code, code);
            error.Set(JsonRpcFields.Message, message ?? LspValues.UnknownErrorMessage);

            var response = new JsonObject();
            response.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            response.Set(JsonRpcFields.Id, id);
            response.Set(JsonRpcFields.Error, error);

            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        /// <summary>
        /// Outbound bridge for publishDiagnostics.
        /// Business producers should pass already-built diagnostics payload.
        /// </summary>
        public void PublishDiagnostics(string uri, List<object> diagnostics, int? version = null)
        {
            var payload = new JsonObject();
            payload.Set(LspFields.Uri, DocumentKeyNormalizer.Normalize(uri));
            payload.Set(LspFields.Diagnostics, diagnostics ?? new List<object>());
            if (version.HasValue)
                payload.Set(LspFields.Version, version.Value);

            SendNotification(LspMethods.PublishDiagnostics, payload);
        }

        private void SendNotification(string method, object notificationParams)
        {
            var notification = new JsonObject();
            notification.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            notification.Set(JsonRpcFields.Method, method ?? string.Empty);
            notification.Set(JsonRpcFields.Params, notificationParams ?? new JsonObject());
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
                item.Set(LspFields.Name, symbol.Name ?? string.Empty);
                item.Set(LspFields.Kind, MapDocumentSymbolKind(symbol.Kind));
                item.Set(LspFields.Range, range);
                item.Set(LspFields.SelectionRange, range);
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
            contents.Set(LspFields.Kind, LspValues.Markdown);
            contents.Set(LspFields.Value, summary);

            var result = new JsonObject();
            result.Set(LspFields.Contents, contents);
            return result;
        }

        private static JsonObject ConvertDefinition(LspDefinitionPayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set(LspFields.Uri, DocumentKeyNormalizer.Normalize(payload.DocumentKey));
            result.Set(LspFields.Range, MakeRangeFromPayload(payload.SourcePayload, payload.Span));
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
                location.Set(LspFields.Uri, DocumentKeyNormalizer.Normalize(item.DocumentKey));
                location.Set(LspFields.Range, MakeRangeFromPayload(item.SourcePayload, item.Span));
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
                projected.Set(LspFields.Label, item.Label ?? string.Empty);
                projected.Set(LspFields.Kind, MapCompletionKind(item.Kind));
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    projected.Set(LspFields.Detail, item.Detail);
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
                    projected.Set(LspFields.Label, signature.Label ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(signature.Source))
                    {
                        var doc = new JsonObject();
                        doc.Set(LspFields.Kind, LspValues.Markdown);
                        doc.Set(LspFields.Value, "Source: " + DocumentKeyNormalizer.Normalize(signature.Source));
                        projected.Set(LspFields.Documentation, doc);
                    }

                    signatures.Add(projected);
                }
            }

            var result = new JsonObject();
            result.Set(LspFields.Signatures, signatures);
            result.Set(LspFields.ActiveSignature, payload.ActiveSignature);
            result.Set(LspFields.ActiveParameter, payload.ActiveParameter);
            return result;
        }

        private static JsonObject ConvertPrepareRename(LspPrepareRenamePayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set(LspFields.Range, MakeRangeFromSpan(payload.Range));
            result.Set(LspFields.Placeholder, payload.Placeholder ?? string.Empty);
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
                textEdit.Set(LspFields.Range, MakeRangeFromSpan(edit.Range));
                textEdit.Set(LspFields.NewText, edit.NewText ?? string.Empty);
                edits.Add(textEdit);
            }

            foreach (KeyValuePair<string, List<object>> pair in grouped)
                changes.Set(pair.Key, pair.Value);

            var result = new JsonObject();
            result.Set(LspFields.Changes, changes);
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
            result.Set(LspFields.Data, data);
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
            start.Set(LspFields.Line, startLine < 0 ? 0 : startLine);
            start.Set(LspFields.Character, startCharacter < 0 ? 0 : startCharacter);

            var end = new JsonObject();
            end.Set(LspFields.Line, endLine < 0 ? 0 : endLine);
            end.Set(LspFields.Character, endCharacter < 0 ? 0 : endCharacter);

            var range = new JsonObject();
            range.Set(LspFields.Start, start);
            range.Set(LspFields.End, end);
            return range;
        }

        private static int MapDocumentSymbolKind(string kind)
        {
            if (string.Equals(kind, SymbolKindNames.Function, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Function;
            if (string.Equals(kind, SymbolKindNames.Struct, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Struct;
            if (string.Equals(kind, SymbolKindNames.Enum, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.EnumType;
            if (string.Equals(kind, SymbolKindNames.Variable, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Variable;
            if (string.Equals(kind, SymbolKindNames.Parameter, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Parameter;
            return (int)LspDocumentSymbolKindCode.Variable;
        }

        private static int MapCompletionKind(string kind)
        {
            if (string.Equals(kind, SymbolKindNames.Function, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Function;
            if (string.Equals(kind, SymbolKindNames.Struct, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Struct;
            if (string.Equals(kind, SymbolKindNames.Enum, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.EnumType;
            if (string.Equals(kind, SymbolKindNames.StructField, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Field;
            if (string.Equals(kind, SymbolKindNames.EnumMember, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.EnumMember;
            if (string.Equals(kind, SymbolKindNames.Variable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, SymbolKindNames.Parameter, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Variable;
            return (int)LspCompletionItemKindCode.Text;
        }
    }
}
