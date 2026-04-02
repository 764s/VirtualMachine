using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FFVM.Compiler;

namespace FFVM.Debug
{
    /// <summary>
    /// LSP (Language Server Protocol) server for FFVM script language support.
    /// Single-threaded message loop: reads JSON-RPC requests/notifications via stdin,
    /// responds via stdout. Reuses ContentLengthStream + JsonHelper from DAP.
    ///
    /// MVP messages (D-LSP-03):
    ///   Requests:       initialize, shutdown
    ///   Notifications:  initialized, exit, textDocument/didOpen, textDocument/didChange
    ///   Server→Client:  textDocument/publishDiagnostics
    ///
    /// Compile-on-change: every didOpen/didChange triggers full recompile → diagnostics push.
    /// </summary>
    public class LspServer
    {
        private readonly Stream _input;
        private readonly Stream _output;

        // --- Session state ---
        private bool _running;
        private bool _shutdownRequested;

        // --- Document store (uri → content) ---
        private readonly Dictionary<string, string> _documents = new Dictionary<string, string>();

        // --- Syscall table for compilation (stub, no real syscalls needed for diagnostics) ---
        private readonly Dictionary<string, int> _defaultSyscalls;

        /// <summary>
        /// Exposed for testing: all diagnostics published since last clear.
        /// Key = URI, Value = list of diagnostic objects.
        /// </summary>
        internal readonly List<(string uri, List<object> diagnostics)> PublishedDiagnostics
            = new List<(string, List<object>)>();

        public LspServer(Stream input, Stream output)
        {
            _input = input;
            _output = output;
            _defaultSyscalls = new Dictionary<string, int>();
        }

        /// <summary>
        /// Main loop: read LSP messages and dispatch.
        /// Blocks until exit notification or stream close.
        /// </summary>
        public void Run()
        {
            _running = true;

            while (_running)
            {
                string messageText = ContentLengthStream.ReadMessage(_input);
                if (messageText == null)
                    break; // Stream closed

                var message = JsonObject.Parse(messageText);
                if (message == null)
                    continue;

                string method = message.GetString("method");
                bool hasId = message.ContainsKey("id");

                if (hasId)
                {
                    // Request — needs a response
                    HandleRequest(message, method);
                }
                else
                {
                    // Notification — no response
                    HandleNotification(message, method);
                }
            }
        }

        private void HandleRequest(JsonObject message, string method)
        {
            int id = message.GetInt("id");
            var parameters = message.GetObject("params");

            JsonObject result = null;
            bool success = true;
            string errorMessage = null;
            int errorCode = 0;

            try
            {
                switch (method)
                {
                    case "initialize":
                        result = HandleInitialize(parameters);
                        break;
                    case "shutdown":
                        HandleShutdown();
                        result = null; // null result is valid for shutdown
                        break;
                    default:
                        success = false;
                        errorCode = -32601; // MethodNotFound
                        errorMessage = $"Method not found: {method}";
                        break;
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorCode = -32603; // InternalError
                errorMessage = ex.Message;
            }

            SendResponse(id, success, result, errorCode, errorMessage);
        }

        private void HandleNotification(JsonObject message, string method)
        {
            var parameters = message.GetObject("params");

            switch (method)
            {
                case "initialized":
                    // Client confirms initialization — nothing to do
                    break;
                case "exit":
                    _running = false;
                    break;
                case "textDocument/didOpen":
                    HandleDidOpen(parameters);
                    break;
                case "textDocument/didChange":
                    HandleDidChange(parameters);
                    break;
                // Ignore unknown notifications
            }
        }

        // ============================================================
        // Handlers
        // ============================================================

        private JsonObject HandleInitialize(JsonObject parameters)
        {
            var capabilities = new JsonObject();

            // Full document sync: client sends entire document on change
            var textDocSync = new JsonObject();
            textDocSync.Set("openClose", true);
            textDocSync.Set("change", 1); // TextDocumentSyncKind.Full = 1
            capabilities.Set("textDocumentSync", textDocSync);

            var result = new JsonObject();
            result.Set("capabilities", capabilities);
            return result;
        }

        private void HandleShutdown()
        {
            _shutdownRequested = true;
        }

        private void HandleDidOpen(JsonObject parameters)
        {
            if (parameters == null) return;
            var textDocument = parameters.GetObject("textDocument");
            if (textDocument == null) return;

            string uri = textDocument.GetString("uri");
            string text = textDocument.GetString("text");
            if (uri == null) return;

            _documents[uri] = text ?? "";
            CompileAndPublishDiagnostics(uri, text ?? "");
        }

        private void HandleDidChange(JsonObject parameters)
        {
            if (parameters == null) return;
            var textDocument = parameters.GetObject("textDocument");
            if (textDocument == null) return;

            string uri = textDocument.GetString("uri");
            if (uri == null) return;

            // Full document sync: take the last content change
            var changes = parameters.GetArray("contentChanges");
            if (changes != null && changes.Count > 0)
            {
                var lastChange = changes[changes.Count - 1] as JsonObject;
                if (lastChange != null)
                {
                    string text = lastChange.GetString("text");
                    _documents[uri] = text ?? "";
                    CompileAndPublishDiagnostics(uri, text ?? "");
                }
            }
        }

        // ============================================================
        // Diagnostics (LSP3)
        // ============================================================

        private void CompileAndPublishDiagnostics(string uri, string source)
        {
            var diagnostics = new List<object>();

            if (!string.IsNullOrEmpty(source))
            {
                var compiler = new BytecodeCompiler();
                var result = compiler.Compile(source, "entry", _defaultSyscalls);

                if (!result.Success && result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
                        var diag = ErrorToDiagnostic(error, source);
                        diagnostics.Add(diag);
                    }
                }
            }

            PublishDiagnostics(uri, diagnostics);
        }

        /// <summary>
        /// Parse error string to extract line number and create LSP Diagnostic object.
        /// Handles formats: "msg (line N)", "msg at L:C", or fallback to line 0.
        /// </summary>
        internal static JsonObject ErrorToDiagnostic(string error, string source)
        {
            int line = 0;
            int col = 0;
            string message = error;

            // Try to extract "(line N)" at end
            var lineMatch = Regex.Match(error, @"\(line\s+(\d+)\)\s*$");
            if (lineMatch.Success)
            {
                line = int.Parse(lineMatch.Groups[1].Value) - 1; // LSP uses 0-based lines
                message = error.Substring(0, lineMatch.Index).TrimEnd();
            }
            else
            {
                // Try to extract "at L:C"
                var atMatch = Regex.Match(error, @"at\s+(\d+):(\d+)");
                if (atMatch.Success)
                {
                    line = int.Parse(atMatch.Groups[1].Value) - 1;
                    col = int.Parse(atMatch.Groups[2].Value) - 1;
                }
            }

            // Clamp to valid range
            if (line < 0) line = 0;
            if (col < 0) col = 0;

            var range = new JsonObject();
            var start = new JsonObject();
            start.Set("line", line);
            start.Set("character", col);
            var end = new JsonObject();
            end.Set("line", line);
            end.Set("character", col + 1);
            range.Set("start", start);
            range.Set("end", end);

            var diagnostic = new JsonObject();
            diagnostic.Set("range", range);
            diagnostic.Set("severity", 1); // DiagnosticSeverity.Error = 1
            diagnostic.Set("source", "ffvm");
            diagnostic.Set("message", message);

            return diagnostic;
        }

        private void PublishDiagnostics(string uri, List<object> diagnostics)
        {
            // Track for testing
            PublishedDiagnostics.Add((uri, diagnostics));

            var parameters = new JsonObject();
            parameters.Set("uri", uri);
            parameters.Set("diagnostics", diagnostics);

            SendNotification("textDocument/publishDiagnostics", parameters);
        }

        // ============================================================
        // Protocol helpers
        // ============================================================

        private void SendResponse(int id, bool success, JsonObject result, int errorCode, string errorMessage)
        {
            var response = new JsonObject();
            response.Set("jsonrpc", "2.0");
            response.Set("id", id);

            if (success)
            {
                // result can be null (e.g., shutdown response)
                response.Set("result", result != null ? (object)result : null);
            }
            else
            {
                var error = new JsonObject();
                error.Set("code", errorCode);
                error.Set("message", errorMessage ?? "Unknown error");
                response.Set("error", error);
            }

            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        private void SendNotification(string method, JsonObject parameters)
        {
            var notification = new JsonObject();
            notification.Set("jsonrpc", "2.0");
            notification.Set("method", method);
            if (parameters != null)
                notification.Set("params", parameters);

            ContentLengthStream.WriteMessage(_output, notification.ToJson());
        }
    }
}
