using System;
using System.Collections.Generic;
using System.IO;
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
            object result = _bridge.QueryDocumentSymbol(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeHover(JsonObject requestParams)
        {
            object result = _bridge.QueryHover(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeDefinition(JsonObject requestParams)
        {
            object result = _bridge.QueryDefinition(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeReferences(JsonObject requestParams)
        {
            object result = _bridge.QueryReferences(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeCompletion(JsonObject requestParams)
        {
            object result = _bridge.QueryCompletion(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeSignatureHelp(JsonObject requestParams)
        {
            object result = _bridge.QuerySignatureHelp(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeRename(JsonObject requestParams)
        {
            object result = _bridge.QueryRename(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgePrepareRename(JsonObject requestParams)
        {
            object result = _bridge.QueryPrepareRename(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeSemanticTokensFull(JsonObject requestParams)
        {
            object result = _bridge.QuerySemanticTokensFull(requestParams);
            FlushBridgeDiagnostics();
            return result;
        }

        private object BridgeWillRenameFiles(JsonObject requestParams)
        {
            object result = _bridge.QueryWillRenameFiles(requestParams);
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
    }
}