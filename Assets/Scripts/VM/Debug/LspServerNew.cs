using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug.Lsp.Database.Paths;
using FFVM.Debug.Lsp.Integration.VsCode;
using FFVM.Debug.Lsp.Protocol;

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
        private sealed class PendingClientRequest
        {
            public PendingClientRequest(string method, string requestToken)
            {
                Method = method ?? string.Empty;
                RequestToken = requestToken ?? string.Empty;
            }

            public string Method { get; }
            public string RequestToken { get; }
        }

        private readonly Stream _input;
        private readonly Stream _output;
        private readonly ILspVsCodeDatabaseBridge _bridge;
        private readonly Dictionary<int, PendingClientRequest> _pendingClientRequests = new Dictionary<int, PendingClientRequest>();

        private bool _running;
        private bool _shutdownRequested;
        private int _nextClientRequestId = 900000;

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
            bool isResponse = !hasMethod && id != null;

            if (isResponse)
            {
                HandleClientResponse(message);
                return;
            }

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

        private void HandleClientResponse(JsonObject response)
        {
            int requestId = response != null ? response.GetInt(JsonRpcFields.Id, -1) : -1;
            if (requestId < 0)
                return;

            if (!_pendingClientRequests.TryGetValue(requestId, out PendingClientRequest pending) || pending == null)
                return;

            _pendingClientRequests.Remove(requestId);

            object result = response.Get(JsonRpcFields.Result);
            JsonObject error = response.GetObject(JsonRpcFields.Error);
            _bridge.HandleClientRequestResponse(pending.Method, pending.RequestToken, result, error);
            FlushBridgeFeedback();
        }

        private void HandleRequest(JsonObject request, string method, object id)
        {
            JsonObject requestParams = request.GetObject(JsonRpcFields.Params);

            switch (method)
            {
                case LspMethods.Initialize:
                    BridgeInitialize(requestParams);
                    SendResponse(id, LspProtocolPayloadProjector.CreateInitializeResult());
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

        private int SendClientRequest(string method, JsonObject requestParams, string requestToken)
        {
            string normalizedMethod = method ?? string.Empty;
            if (string.IsNullOrWhiteSpace(normalizedMethod))
                return -1;

            int id = ++_nextClientRequestId;

            var request = new JsonObject();
            request.Set(JsonRpcFields.JsonRpc, JsonRpcFields.Version);
            request.Set(JsonRpcFields.Id, id);
            request.Set(JsonRpcFields.Method, normalizedMethod);
            request.Set(JsonRpcFields.Params, requestParams ?? new JsonObject());

            _pendingClientRequests[id] = new PendingClientRequest(normalizedMethod, requestToken);
            ContentLengthStream.WriteMessage(_output, request.ToJson());
            return id;
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

        private void FlushBridgeClientRequests()
        {
            while (_bridge.TryDequeueClientRequest(out LspClientRequest request) && request != null)
            {
                if (string.IsNullOrWhiteSpace(request.Method))
                    continue;

                SendClientRequest(request.Method, request.Parameters, request.RequestToken);
            }
        }

        private void FlushBridgeFeedback()
        {
            FlushBridgeDiagnostics();
            FlushBridgeClientRequests();
        }

        // ------------------------------------------------------------
        // VS Code bridge points (delegated to ILspVsCodeDatabaseBridge)
        // ------------------------------------------------------------

        private void BridgeInitialize(JsonObject initializeParams)
        {
            _bridge.Initialize(initializeParams);
            FlushBridgeFeedback();
        }

        private void BridgeShutdown(JsonObject shutdownParams)
        {
            _bridge.Shutdown(shutdownParams);
            FlushBridgeFeedback();
        }

        private object BridgeDocumentSymbol(JsonObject requestParams)
        {
            var result = _bridge.QueryDocumentSymbol(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertDocumentSymbols(result);
        }

        private object BridgeHover(JsonObject requestParams)
        {
            var result = _bridge.QueryHover(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertHover(result);
        }

        private object BridgeDefinition(JsonObject requestParams)
        {
            var result = _bridge.QueryDefinition(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertDefinition(result);
        }

        private object BridgeReferences(JsonObject requestParams)
        {
            var result = _bridge.QueryReferences(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertReferences(result);
        }

        private object BridgeCompletion(JsonObject requestParams)
        {
            var result = _bridge.QueryCompletion(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertCompletionItems(result);
        }

        private object BridgeSignatureHelp(JsonObject requestParams)
        {
            var result = _bridge.QuerySignatureHelp(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertSignatureHelp(result);
        }

        private object BridgeRename(JsonObject requestParams)
        {
            var result = _bridge.QueryRename(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertRename(result);
        }

        private object BridgePrepareRename(JsonObject requestParams)
        {
            var result = _bridge.QueryPrepareRename(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertPrepareRename(result);
        }

        private object BridgeSemanticTokensFull(JsonObject requestParams)
        {
            var result = _bridge.QuerySemanticTokensFull(requestParams);
            FlushBridgeFeedback();
            return LspProtocolPayloadProjector.ConvertSemanticTokens(result);
        }

        private object BridgeWillRenameFiles(JsonObject requestParams)
        {
            JsonObject result = _bridge.QueryWillRenameFiles(requestParams);
            FlushBridgeFeedback();
            return result;
        }

        private void BridgeInitialized(JsonObject initializedParams)
        {
            _bridge.Initialized(initializedParams);
            FlushBridgeFeedback();
        }

        private void BridgeExit(JsonObject exitParams)
        {
            _bridge.Exit(exitParams);
            FlushBridgeFeedback();
        }

        private void BridgeDidOpen(JsonObject didOpenParams)
        {
            _bridge.DidOpen(didOpenParams);
            FlushBridgeFeedback();
        }

        private void BridgeDidChange(JsonObject didChangeParams)
        {
            _bridge.DidChange(didChangeParams);
            FlushBridgeFeedback();
        }

        private void BridgeDidClose(JsonObject didCloseParams)
        {
            _bridge.DidClose(didCloseParams);
            FlushBridgeFeedback();
        }

        private void BridgeDidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams)
        {
            _bridge.DidChangeWatchedFiles(didChangeWatchedFilesParams);
            FlushBridgeFeedback();
        }
    }
}
