using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using FFVM.AST;
using FFVM.Compiler;

namespace FFVM.Debug
{
    /// <summary>
    /// LSP (Language Server Protocol) server for FFVM script language support.
    /// Single-threaded message loop: reads JSON-RPC requests/notifications via stdin,
    /// responds via stdout. Reuses ContentLengthStream + JsonHelper from DAP.
    ///
    /// Messages:
    ///   Requests:       initialize, shutdown,
    ///                   textDocument/documentSymbol, textDocument/hover,
    ///                   textDocument/definition, textDocument/references,
    ///                   textDocument/completion, textDocument/signatureHelp,
    ///                   textDocument/rename, textDocument/prepareRename,
    ///                   textDocument/semanticTokens/full,
    ///                   workspace/willRenameFiles
    ///   Notifications:  initialized, exit, textDocument/didOpen, textDocument/didChange,
    ///                   textDocument/didClose, workspace/didChangeWatchedFiles
    ///   Server→Client:  textDocument/publishDiagnostics
    ///
    /// Compile-on-change: every didOpen/didChange triggers full recompile → diagnostics push + AST cache.
    /// </summary>
    public class LspServer
    {
        private readonly Stream _input;
        private readonly Stream _output;

        // --- Session state ---
        private bool _running;
        private bool _shutdownRequested;

        // --- R1: DocumentStore — encapsulates document content, per-file AST, and merged AST caches ---
        private readonly DocumentStore _docStore = new DocumentStore();

        /// <summary>
        /// R1: Encapsulates document content and AST caches.
        /// Provides a single point for Open/Change/Close/Get operations,
        /// replacing scattered dictionary accesses across handlers.
        /// </summary>
        private class DocumentStore
        {
            private readonly Dictionary<string, string> _content = new Dictionary<string, string>();
            private readonly Dictionary<string, ModuleNode> _asts = new Dictionary<string, ModuleNode>();
            private readonly Dictionary<string, ModuleNode> _mergedAsts = new Dictionary<string, ModuleNode>();

            // DX10: Include dependency graph.
            // _includeDependents: resolvedFilePath → set of URIs that include this file.
            // When a file changes, look up its resolved path to find all dependents that need re-diagnosis.
            private readonly Dictionary<string, HashSet<string>> _includeDependents
                = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            // DX10: Forward dependency: URI → set of resolved file paths it includes.
            // Used to remove stale edges when imports change.
            private readonly Dictionary<string, HashSet<string>> _includeForward
                = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            public void SetContent(string uri, string text) => _content[uri] = text ?? "";
            public bool TryGetContent(string uri, out string content) => _content.TryGetValue(uri, out content);
            public bool HasContent(string uri) => _content.ContainsKey(uri);

            public void SetAst(string uri, ModuleNode ast) => _asts[uri] = ast;
            public bool HasAst(string uri) => _asts.ContainsKey(uri);
            public ModuleNode GetAst(string uri) => uri != null && _asts.TryGetValue(uri, out var ast) ? ast : null;

            public void SetMergedAst(string uri, ModuleNode ast) => _mergedAsts[uri] = ast;
            public bool HasMergedAst(string uri) => _mergedAsts.ContainsKey(uri);
            public ModuleNode GetMergedAst(string uri) => uri != null && _mergedAsts.TryGetValue(uri, out var ast) ? ast : null;

            /// <summary>Remove all data for a closed document.</summary>
            public void Remove(string uri) { _content.Remove(uri); _asts.Remove(uri); _mergedAsts.Remove(uri); }

            /// <summary>
            /// DX10: Update the dependency graph for a file.
            /// Removes old edges for this URI, then adds new edges based on resolved import paths.
            /// </summary>
            public void UpdateDependencies(string uri, List<string> resolvedImportPaths)
            {
                // Remove old forward edges
                HashSet<string> oldImports;
                if (_includeForward.TryGetValue(uri, out oldImports))
                {
                    foreach (string oldPath in oldImports)
                    {
                        HashSet<string> dependents;
                        if (_includeDependents.TryGetValue(oldPath, out dependents))
                        {
                            dependents.Remove(uri);
                            if (dependents.Count == 0)
                                _includeDependents.Remove(oldPath);
                        }
                    }
                }

                // Set new forward edges
                var newImports = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (resolvedImportPaths != null)
                {
                    foreach (string path in resolvedImportPaths)
                    {
                        newImports.Add(path);
                        HashSet<string> dependents;
                        if (!_includeDependents.TryGetValue(path, out dependents))
                        {
                            dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            _includeDependents[path] = dependents;
                        }
                        dependents.Add(uri);
                    }
                }
                _includeForward[uri] = newImports;
            }

            /// <summary>
            /// DX10: Get all URIs that depend on (include) the given resolved file path.
            /// Returns empty set if no dependents.
            /// </summary>
            public HashSet<string> GetDependents(string resolvedFilePath)
            {
                HashSet<string> result;
                if (resolvedFilePath != null && _includeDependents.TryGetValue(resolvedFilePath, out result))
                    return result;
                return new HashSet<string>();
            }

            /// <summary>
            /// DX10: Get all open document URIs (for workspace-wide operations).
            /// </summary>
            public IEnumerable<string> GetAllOpenUris()
            {
                return _content.Keys;
            }

            /// <summary>
            /// DX11: Migrate all cached data from oldUri to newUri after a file rename.
            /// Moves content, AST, merged AST, and updates dependency graph edges.
            /// </summary>
            public void RenameUri(string oldUri, string newUri)
            {
                if (oldUri == null || newUri == null || oldUri == newUri) return;

                // Migrate content
                string content;
                if (_content.TryGetValue(oldUri, out content))
                {
                    _content[newUri] = content;
                    _content.Remove(oldUri);
                }

                // Migrate ASTs
                ModuleNode ast;
                if (_asts.TryGetValue(oldUri, out ast))
                {
                    _asts[newUri] = ast;
                    _asts.Remove(oldUri);
                }
                ModuleNode mergedAst;
                if (_mergedAsts.TryGetValue(oldUri, out mergedAst))
                {
                    _mergedAsts[newUri] = mergedAst;
                    _mergedAsts.Remove(oldUri);
                }

                // Migrate forward dependency edges
                HashSet<string> forwardPaths;
                if (_includeForward.TryGetValue(oldUri, out forwardPaths))
                {
                    _includeForward[newUri] = forwardPaths;
                    _includeForward.Remove(oldUri);

                    // Update reverse edges: replace oldUri with newUri in each dependent set
                    foreach (string resolvedPath in forwardPaths)
                    {
                        HashSet<string> dependents;
                        if (_includeDependents.TryGetValue(resolvedPath, out dependents))
                        {
                            if (dependents.Remove(oldUri))
                                dependents.Add(newUri);
                        }
                    }
                }
            }

            /// <summary>
            /// DX11: Apply LSP TextEdit list to a document's cached content.
            /// Each edit replaces a range [startLine:startChar, endLine:endChar] with newText.
            /// Edits are applied in reverse order (bottom-up) to preserve earlier positions.
            /// </summary>
            public void ApplyTextEdits(string uri, List<JsonObject> edits)
            {
                if (uri == null || edits == null || edits.Count == 0) return;
                string content;
                if (!_content.TryGetValue(uri, out content)) return;

                // Sort edits in reverse order (by start position, descending)
                var sorted = new List<JsonObject>(edits);
                sorted.Sort((a, b) =>
                {
                    var ra = a.GetObject("range");
                    var rb = b.GetObject("range");
                    var sa = ra?.GetObject("start");
                    var sb = rb?.GetObject("start");
                    int lineA = sa?.GetInt("line") ?? 0;
                    int lineB = sb?.GetInt("line") ?? 0;
                    if (lineA != lineB) return lineB.CompareTo(lineA); // descending
                    int charA = sa?.GetInt("character") ?? 0;
                    int charB = sb?.GetInt("character") ?? 0;
                    return charB.CompareTo(charA); // descending
                });

                // Split content into lines (preserve line endings for accurate reconstruction)
                var lines = new List<string>();
                int pos = 0;
                while (pos < content.Length)
                {
                    int nlIdx = content.IndexOf('\n', pos);
                    if (nlIdx < 0)
                    {
                        lines.Add(content.Substring(pos));
                        break;
                    }
                    lines.Add(content.Substring(pos, nlIdx - pos + 1)); // include '\n'
                    pos = nlIdx + 1;
                }
                if (content.Length > 0 && content[content.Length - 1] == '\n')
                    lines.Add(""); // trailing empty line after final newline

                foreach (var edit in sorted)
                {
                    var range = edit.GetObject("range");
                    if (range == null) continue;
                    var start = range.GetObject("start");
                    var end = range.GetObject("end");
                    if (start == null || end == null) continue;

                    int startLine = start.GetInt("line");
                    int startChar = start.GetInt("character");
                    int endLine = end.GetInt("line");
                    int endChar = end.GetInt("character");
                    string newText = edit.GetString("newText") ?? "";

                    // Clamp to valid range
                    if (startLine < 0) startLine = 0;
                    if (endLine < 0) endLine = 0;
                    if (startLine >= lines.Count) startLine = lines.Count - 1;
                    if (endLine >= lines.Count) endLine = lines.Count - 1;

                    // Get the text of start line (without trailing newline) and end line
                    string startLineText = lines[startLine].TrimEnd('\n', '\r');
                    string endLineText = lines[endLine].TrimEnd('\n', '\r');
                    string endLineEnding = lines[endLine].Substring(endLineText.Length);

                    if (startChar > startLineText.Length) startChar = startLineText.Length;
                    if (endChar > endLineText.Length) endChar = endLineText.Length;

                    // Build replacement: prefix + newText + suffix
                    string prefix = startLineText.Substring(0, startChar);
                    string suffix = endLineText.Substring(endChar) + endLineEnding;
                    string replacement = prefix + newText + suffix;

                    // Replace lines[startLine..endLine] with the replacement
                    for (int i = endLine; i > startLine; i--)
                        lines.RemoveAt(i);
                    lines[startLine] = replacement;
                }

                // Reconstruct content
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lines.Count; i++)
                    sb.Append(lines[i]);
                _content[uri] = sb.ToString();
            }
        }

        /// <summary>
        /// DX10: File resolver overlay that checks open document contents first,
        /// then falls back to the underlying disk-based resolver.
        /// This enables cascade recompile to see in-memory changes to included files.
        /// </summary>
        private class OverlayFileResolver : IFileResolver
        {
            private readonly IFileResolver _base;
            private readonly DocumentStore _docStore;
            private readonly string _rootPath;

            public OverlayFileResolver(IFileResolver baseResolver, DocumentStore docStore, string rootPath)
            {
                _base = baseResolver;
                _docStore = docStore;
                _rootPath = rootPath;
            }

            public string ReadFile(string path)
            {
                // Check if any open document matches this resolved path
                string resolved = _base.ResolveFilePath(path);
                if (resolved != null)
                {
                    string uri = PathToFileUri(resolved);
                    string content;
                    if (_docStore.TryGetContent(uri, out content))
                        return content;
                }
                return _base.ReadFile(path);
            }

            public string ResolveFilePath(string path)
            {
                return _base.ResolveFilePath(path);
            }
        }

        // --- Syscall table for compilation (stub, no real syscalls needed for diagnostics) ---
        private readonly Dictionary<string, int> _defaultSyscalls;

        // --- LSP6: Syscall signature metadata for enhanced completion ---
        private readonly Dictionary<string, SyscallSignature> _syscallSignatures;

        // --- DX4-P0: Workspace root path and file resolver for include support ---
        private string _rootPath;
        private IFileResolver _fileResolver;

        // --- DX4-P1: Project file configuration ---
        private ProjectFile _projectFile;
        private CompileOptions _projectCompileOptions;

        // --- DX4-P4: Server-initiated requests (window/showMessageRequest, workspace/applyEdit) ---
        private int _nextServerRequestId = 900001;
        private readonly Dictionary<int, string> _pendingServerRequests = new Dictionary<int, string>();

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
            _syscallSignatures = new Dictionary<string, SyscallSignature>();
        }

        /// <summary>
        /// Construct with pre-registered syscall declarations (LSP6).
        /// </summary>
        public LspServer(Stream input, Stream output,
                         Dictionary<string, int> syscalls,
                         Dictionary<string, SyscallSignature> signatures)
        {
            _input = input;
            _output = output;
            _defaultSyscalls = syscalls ?? new Dictionary<string, int>();
            _syscallSignatures = signatures ?? new Dictionary<string, SyscallSignature>();
        }

        /// <summary>
        /// Load syscall declarations from a .ffvm.d.json string (LSP6).
        /// Merges into existing syscall table and signature metadata.
        /// JSON format: { "syscalls": [ { "name": "...", "slot": N, "parameters": [...], "returnType": "...", "description": "..." } ] }
        /// </summary>
        public void LoadDeclarationJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;

            var root = JsonObject.Parse(json);
            if (root == null) return;

            var syscalls = root.GetArray("syscalls");
            if (syscalls == null) return;

            foreach (var item in syscalls)
            {
                var obj = item as JsonObject;
                if (obj == null) continue;

                string name = obj.GetString("name");
                int slot = obj.GetInt("slot", -1);
                if (string.IsNullOrEmpty(name) || slot < 0) continue;

                // Register in compilation table
                _defaultSyscalls[name] = slot;

                // Parse parameters
                var paramList = obj.GetArray("parameters");
                var parms = new List<SyscallParamInfo>();
                if (paramList != null)
                {
                    foreach (var pItem in paramList)
                    {
                        var pObj = pItem as JsonObject;
                        if (pObj == null) continue;
                        string pName = pObj.GetString("name");
                        string pType = pObj.GetString("type");
                        if (!string.IsNullOrEmpty(pName) && !string.IsNullOrEmpty(pType))
                            parms.Add(new SyscallParamInfo(pName, pType));
                    }
                }

                string returnType = obj.GetString("returnType");
                string description = obj.GetString("description");

                _syscallSignatures[name] = new SyscallSignature(
                    parms.ToArray(),
                    returnType,
                    description
                );
            }
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

                if (hasId && method != null)
                {
                    // Request — needs a response
                    HandleRequest(message, method);
                }
                else if (hasId && method == null)
                {
                    // DX4-P4: Response to a server-initiated request
                    HandleServerResponse(message);
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
                    case "textDocument/documentSymbol":
                        result = HandleDocumentSymbol(parameters);
                        break;
                    case "textDocument/hover":
                        result = HandleHover(parameters);
                        break;
                    case "textDocument/definition":
                        result = HandleDefinition(parameters);
                        break;
                    case "textDocument/references":
                        result = HandleReferences(parameters);
                        break;
                    case "textDocument/completion":
                        result = HandleCompletion(parameters);
                        break;
                    case "textDocument/signatureHelp":
                        result = HandleSignatureHelp(parameters);
                        break;
                    case "textDocument/rename":
                        result = HandleRename(parameters);
                        break;
                    case "textDocument/prepareRename":
                        result = HandlePrepareRename(parameters);
                        break;
                    case "textDocument/semanticTokens/full":
                        result = HandleSemanticTokensFull(parameters);
                        break;
                    case "workspace/willRenameFiles":
                        result = HandleWillRenameFiles(parameters);
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
                    // Client confirms initialization — DX4-P4: check if .ffproj creation should be offered
                    CheckAndOfferFfprojCreation();
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
                case "textDocument/didClose":
                    HandleDidClose(parameters);
                    break;
                case "workspace/didChangeWatchedFiles":
                    HandleDidChangeWatchedFiles(parameters);
                    break;
                // Ignore unknown notifications
            }
        }

        // ============================================================
        // Handlers
        // ============================================================

        private JsonObject HandleInitialize(JsonObject parameters)
        {
            // DX4-P0: Parse rootUri to establish workspace root for file resolution
            if (parameters != null)
            {
                string rootUri = parameters.GetString("rootUri");
                if (rootUri != null)
                {
                    _rootPath = UriToPath(rootUri);
                }
                else
                {
                    // Fallback: rootPath (deprecated in LSP but still sent by some clients)
                    string rootPath = parameters.GetString("rootPath");
                    if (rootPath != null)
                        _rootPath = rootPath;
                }

                if (_rootPath != null)
                {
                    // DX4-P1: Try to discover and load a .ffproj project file first.
                    // If found, use its includePaths for file resolution and hostDeclarations for syscalls.
                    _projectFile = ProjectFile.TryDiscover(_rootPath);
                    if (_projectFile != null)
                    {
                        // Build file resolver from project's includePaths
                        _fileResolver = _projectFile.BuildFileResolver();

                        // Load host declaration files specified by project
                        LoadProjectHostDeclarations(_projectFile);

                        // Apply project compile options
                        _projectCompileOptions = _projectFile.CompileOptions;
                    }
                    else
                    {
                        // DX4-P0 fallback: use workspace root as sole include base
                        _fileResolver = new FileSystemFileResolver(_rootPath);
                    }

                    // Auto-discover .ffvm.d.json declaration files in workspace root (DX4-P0)
                    // This runs regardless of .ffproj presence — always provides fallback discovery
                    DiscoverDeclarationFiles(_rootPath);
                }
            }

            var capabilities = new JsonObject();

            // Full document sync: client sends entire document on change
            var textDocSync = new JsonObject();
            textDocSync.Set("openClose", true);
            textDocSync.Set("change", 1); // TextDocumentSyncKind.Full = 1
            capabilities.Set("textDocumentSync", textDocSync);

            // LSP4: Symbol analysis capabilities
            capabilities.Set("documentSymbolProvider", true);
            capabilities.Set("hoverProvider", true);
            capabilities.Set("definitionProvider", true);
            capabilities.Set("referencesProvider", true);

            // LSP5: Code completion
            var completionProvider = new JsonObject();
            var triggerChars = new List<object> { "." };
            completionProvider.Set("triggerCharacters", triggerChars);
            capabilities.Set("completionProvider", completionProvider);

            // LSP7: Signature help (parameter hints)
            var signatureHelpProvider = new JsonObject();
            var sigTriggerChars = new List<object> { "(", "," };
            signatureHelpProvider.Set("triggerCharacters", sigTriggerChars);
            capabilities.Set("signatureHelpProvider", signatureHelpProvider);

            // DX5: Rename support (textDocument/rename + textDocument/prepareRename)
            var renameProvider = new JsonObject();
            renameProvider.Set("prepareProvider", true);
            capabilities.Set("renameProvider", renameProvider);

            // DX5: Semantic tokens for struct/enum coloring
            var semanticTokensProvider = new JsonObject();
            semanticTokensProvider.Set("full", true);
            var legend = new JsonObject();
            // tokenTypes: type(0), struct(1), enum(2), enumMember(3), property(4), variable(5),
            //             function(6), parameter(7), string(8), keyword(9)
            var tokenTypes = new List<object> { "type", "struct", "enum", "enumMember", "property",
                                                 "variable", "function", "parameter", "string", "keyword" };
            legend.Set("tokenTypes", tokenTypes);
            legend.Set("tokenModifiers", new List<object> { "declaration", "definition" });
            semanticTokensProvider.Set("legend", legend);
            capabilities.Set("semanticTokensProvider", semanticTokensProvider);

            // DX6: File rename support — workspace/willRenameFiles
            // Server can update include paths when .ffs files are renamed.
            var workspace = new JsonObject();
            var fileOps = new JsonObject();
            var willRenameFilter = new JsonObject();
            var filterPattern = new JsonObject();
            filterPattern.Set("glob", "**/*.ffs");
            var filters = new List<object> { new JsonObject() };
            ((JsonObject)filters[0]).Set("pattern", filterPattern);
            willRenameFilter.Set("filters", filters);
            fileOps.Set("willRename", willRenameFilter);
            workspace.Set("fileOperations", fileOps);
            capabilities.Set("workspace", workspace);

            var result = new JsonObject();
            result.Set("capabilities", capabilities);
            return result;
        }

        /// <summary>
        /// DX4-P0: Convert a file:// URI to a local filesystem path.
        /// Handles percent-encoding and platform-specific path separators.
        /// </summary>
        internal static string UriToPath(string uri)
        {
            if (uri == null) return null;
            // file:///path/to/dir or file:///C:/path on Windows
            if (uri.StartsWith("file:///", StringComparison.Ordinal))
            {
                string path = uri.Substring("file:///".Length);
                // Decode percent-encoded characters
                path = Uri.UnescapeDataString(path);
                // On Unix, paths start with / — add it back
                // On Windows, paths start with drive letter (e.g. C:/...)
                if (path.Length >= 2 && path[1] == ':')
                    return path; // Windows path: C:/...
                return "/" + path; // Unix path: /home/...
            }
            // file://host/path (UNC) — unlikely but handle gracefully
            if (uri.StartsWith("file://", StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(uri.Substring("file://".Length));
            }
            return uri; // fallback: return as-is
        }

        /// <summary>
        /// DX4-P0: Scan workspace directory tree for .ffvm.d.json files and load them.
        /// Recursive scan (AllDirectories) — discovers declaration files in subdirectories too.
        /// </summary>
        private void DiscoverDeclarationFiles(string rootPath)
        {
            try
            {
                if (!Directory.Exists(rootPath)) return;
                string[] files = Directory.GetFiles(rootPath, "*.ffvm.d.json", SearchOption.AllDirectories);
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        string json = File.ReadAllText(files[i]);
                        LoadDeclarationJson(json);
                    }
                    catch
                    {
                        // Ignore individual file read errors — don't crash LSP for bad declaration files
                    }
                }
            }
            catch
            {
                // Ignore directory scan errors — rootPath may not exist or be inaccessible
            }
        }

        /// <summary>
        /// DX4-P1: Load host declaration files specified in a .ffproj project file.
        /// Each path in hostDeclarations is resolved relative to the project directory.
        /// </summary>
        private void LoadProjectHostDeclarations(ProjectFile project)
        {
            if (project == null || project.HostDeclarations == null) return;
            foreach (string declPath in project.HostDeclarations)
            {
                try
                {
                    string absPath = project.ResolvePath(declPath);
                    if (absPath != null && File.Exists(absPath))
                    {
                        string json = File.ReadAllText(absPath);
                        LoadDeclarationJson(json);
                    }
                }
                catch
                {
                    // Ignore individual file errors — don't crash LSP for bad declaration files
                }
            }
        }

        // ============================================================
        // DX4-P4: LSP-assisted .ffproj creation
        // ============================================================

        /// <summary>
        /// DX4-P4: Send a server-to-client request and track its ID for response handling.
        /// Returns the assigned request ID.
        /// </summary>
        private int SendRequest(string method, JsonObject parameters)
        {
            int id = _nextServerRequestId++;
            _pendingServerRequests[id] = method;

            var request = new JsonObject();
            request.Set("jsonrpc", "2.0");
            request.Set("id", id);
            request.Set("method", method);
            if (parameters != null)
                request.Set("params", parameters);

            ContentLengthStream.WriteMessage(_output, request.ToJson());
            return id;
        }

        /// <summary>
        /// DX4-P4: Handle a response from the client to a server-initiated request.
        /// </summary>
        private void HandleServerResponse(JsonObject message)
        {
            int id = message.GetInt("id");
            if (!_pendingServerRequests.TryGetValue(id, out string method))
                return; // unknown response — ignore
            _pendingServerRequests.Remove(id);

            var result = message.GetObject("result");

            switch (method)
            {
                case "window/showMessageRequest":
                    HandleShowMessageResponse(result);
                    break;
                case "workspace/applyEdit":
                    // No action needed — just acknowledge
                    break;
            }
        }

        /// <summary>
        /// DX4-P4: After initialized, check if workspace has .ffs files but no .ffproj.
        /// If so, send window/showMessageRequest to prompt the user.
        /// </summary>
        private void CheckAndOfferFfprojCreation()
        {
            // Only check if we have a workspace root and no project file was discovered
            if (_rootPath == null || _projectFile != null) return;

            try
            {
                if (!Directory.Exists(_rootPath)) return;
                string[] ffsFiles = Directory.GetFiles(_rootPath, "*.ffs", SearchOption.AllDirectories);
                if (ffsFiles.Length == 0) return;
            }
            catch
            {
                return; // filesystem errors — don't crash LSP
            }

            // Has .ffs files but no .ffproj → prompt user
            var parameters = new JsonObject();
            parameters.Set("type", 3); // MessageType.Info = 3
            parameters.Set("message", "Detected FFScript files but no project configuration. Create .ffproj?");

            var actions = new List<object>();

            var createAction = new JsonObject();
            createAction.Set("title", "Create");
            actions.Add(createAction);

            var ignoreAction = new JsonObject();
            ignoreAction.Set("title", "Ignore");
            actions.Add(ignoreAction);

            var neverAction = new JsonObject();
            neverAction.Set("title", "Don't ask again");
            actions.Add(neverAction);

            parameters.Set("actions", actions);

            SendRequest("window/showMessageRequest", parameters);
        }

        /// <summary>
        /// DX4-P4: Handle the user's response to the .ffproj creation prompt.
        /// </summary>
        private void HandleShowMessageResponse(JsonObject result)
        {
            // null result means user dismissed the dialog (e.g. pressed Escape)
            if (result == null) return;
            string title = result.GetString("title");
            if (title == "Create")
            {
                CreateFfprojViaApplyEdit();
            }
            // "Ignore" or "Don't ask again" → do nothing
        }

        /// <summary>
        /// DX4-P4: Send workspace/applyEdit to create a .ffproj template file.
        /// Uses documentChanges with CreateFile + TextDocumentEdit.
        /// </summary>
        private void CreateFfprojViaApplyEdit()
        {
            if (_rootPath == null) return;

            // DX5: Use workspace folder name as .ffproj filename (e.g., "MyProject.ffproj")
            string folderName = Path.GetFileName(_rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string ffprojFileName = !string.IsNullOrEmpty(folderName) ? folderName + ".ffproj" : "project.ffproj";
            string ffprojPath = Path.Combine(_rootPath, ffprojFileName);
            string ffprojUri = PathToFileUri(ffprojPath);
            string template = ProjectFile.GenerateTemplate(null);

            var parameters = new JsonObject();
            var edit = new JsonObject();

            var documentChanges = new List<object>();

            // 1. CreateFile resource operation
            var createFile = new JsonObject();
            createFile.Set("kind", "create");
            createFile.Set("uri", ffprojUri);
            var createOptions = new JsonObject();
            createOptions.Set("overwrite", false);
            createOptions.Set("ignoreIfExists", true);
            createFile.Set("options", createOptions);
            documentChanges.Add(createFile);

            // 2. TextDocumentEdit to insert template content
            var textDocEdit = new JsonObject();
            var textDoc = new JsonObject();
            textDoc.Set("uri", ffprojUri);
            textDoc.Set("version", null); // null = document not yet open
            textDocEdit.Set("textDocument", textDoc);

            var edits = new List<object>();
            var textEdit = new JsonObject();
            var range = new JsonObject();
            var start = new JsonObject();
            start.Set("line", 0);
            start.Set("character", 0);
            var end = new JsonObject();
            end.Set("line", 0);
            end.Set("character", 0);
            range.Set("start", start);
            range.Set("end", end);
            textEdit.Set("range", range);
            textEdit.Set("newText", template);
            edits.Add(textEdit);

            textDocEdit.Set("edits", edits);
            documentChanges.Add(textDocEdit);

            edit.Set("documentChanges", documentChanges);
            parameters.Set("edit", edit);

            SendRequest("workspace/applyEdit", parameters);
        }

        /// <summary>
        /// DX4-P4: Convert a local filesystem path to a file:// URI.
        /// </summary>
        internal static string PathToFileUri(string path)
        {
            if (path == null) return null;
            path = path.Replace('\\', '/');
            // Windows path: C:/... → file:///C:/...
            if (path.Length >= 2 && path[1] == ':')
                return "file:///" + path;
            // Unix path: /home/... → file:///home/...
            return "file:///" + path.TrimStart('/');
        }
        /// Used as filePath parameter for include cycle detection and diagnostics.
        /// Returns the relative path if under rootPath, otherwise returns the absolute path as fallback.
        /// Returns null only if the URI itself cannot be parsed.
        /// </summary>
        internal static string UriToFilePath(string uri, string rootPath)
        {
            string absPath = UriToPath(uri);
            if (absPath == null || rootPath == null) return null;
            // Normalize separators
            absPath = absPath.Replace('\\', '/');
            string normalizedRoot = rootPath.Replace('\\', '/');
            if (!normalizedRoot.EndsWith("/")) normalizedRoot += "/";
            if (absPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return absPath.Substring(normalizedRoot.Length);
            // Not under workspace root — use full path as filePath
            return absPath;
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

            _docStore.SetContent(uri, text ?? "");
            CompileAndPublishDiagnostics(uri, text ?? "");

            // DX10: Cascade — recompile open files that include this file
            string filePath = UriToPath(uri);
            if (filePath != null)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri };
                RecompileDependents(filePath, visited);
            }
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
                    _docStore.SetContent(uri, text ?? "");
                    CompileAndPublishDiagnostics(uri, text ?? "");

                    // DX10: Cascade — recompile open files that include this file
                    string filePath = UriToPath(uri);
                    if (filePath != null)
                    {
                        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri };
                        RecompileDependents(filePath, visited);
                    }
                }
            }
        }

        /// <summary>
        /// E003: Handle textDocument/didClose notification.
        /// Clears cached document content, per-file AST, and merged AST for the closed file.
        /// Also clears diagnostics so stale errors don't persist in the editor.
        /// </summary>
        private void HandleDidClose(JsonObject parameters)
        {
            if (parameters == null) return;
            var textDocument = parameters.GetObject("textDocument");
            if (textDocument == null) return;

            string uri = textDocument.GetString("uri");
            if (uri == null) return;

            _docStore.Remove(uri);
            // Clear diagnostics for the closed file
            PublishDiagnostics(uri, new List<object>());
        }

        /// <summary>
        /// DX10: Handle workspace/didChangeWatchedFiles notification.
        /// When .ffs files change on disk (external edit, git checkout, etc.),
        /// recompile open files that include the changed files.
        /// FileChangeType: 1=Created, 2=Changed, 3=Deleted.
        /// </summary>
        private void HandleDidChangeWatchedFiles(JsonObject parameters)
        {
            if (parameters == null || _fileResolver == null) return;
            var fileChanges = parameters.GetArray("changes");
            if (fileChanges == null || fileChanges.Count == 0) return;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var change in fileChanges)
            {
                var changeObj = change as JsonObject;
                if (changeObj == null) continue;
                string changedUri = changeObj.GetString("uri");
                if (changedUri == null) continue;

                string changedPath = UriToPath(changedUri);
                if (changedPath == null) continue;

                // If this file is open, the editor already sent didChange — skip.
                // Only cascade to dependents of un-opened files changing on disk.
                if (!_docStore.HasContent(changedUri))
                {
                    // Recompile open files that depend on this changed disk file
                    RecompileDependents(changedPath, visited);
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
                // Parse to get AST (for symbol queries)
                var parser = new Parser();
                var ast = parser.Parse(source, out var parseErrors);

                // Cache AST if parse succeeded (keep old AST on failure for continued symbol support)
                if (parseErrors == null || parseErrors.Count == 0)
                    _docStore.SetAst(uri, ast);
                else if (!_docStore.HasAst(uri) && ast != null)
                    _docStore.SetAst(uri, ast); // better than nothing on first open

                // DX10: Build overlay resolver so Preprocessor and Compiler see open-doc content for includes.
                IFileResolver effectiveResolver = _fileResolver;
                if (_fileResolver != null)
                    effectiveResolver = new OverlayFileResolver(_fileResolver, _docStore, _rootPath);

                // DX4-P3: Build merged AST via Preprocessor for cross-file symbol queries.
                // This includes all symbols from included files with OriginFile set.
                if (_fileResolver != null)
                {
                    // Use absolute path so OriginFile values are consistent with resolved include paths
                    string mergeFilePath = UriToPath(uri);
                    var preprocessor = new Preprocessor(effectiveResolver);
                    var mergedAst = preprocessor.Resolve(source, mergeFilePath ?? "main", out var ppErrors);
                    if (ppErrors == null || ppErrors.Count == 0)
                        _docStore.SetMergedAst(uri, mergedAst);
                    else if (!_docStore.HasMergedAst(uri) && mergedAst != null)
                        _docStore.SetMergedAst(uri, mergedAst);

                    // DX10: Update dependency graph using ALL transitively resolved file paths.
                    // This ensures that editing a deeply-included file cascades to all open dependents.
                    _docStore.UpdateDependencies(uri, preprocessor.ResolvedFilePaths);
                }
                else
                {
                    // No file resolver — merged AST is same as per-file AST
                    var cachedAst = _docStore.GetAst(uri);
                    if (cachedAst != null)
                        _docStore.SetMergedAst(uri, cachedAst);
                }

                // DX4-P0: Compile for diagnostics with workspace file resolver support.
                // entryFunc=null → diagnostics-only mode (no entry function requirement).
                // fileResolver → enables include directive resolution from workspace root.
                // DX4-P1: Pass project compile options when available.
                var compiler = new BytecodeCompiler();
                string filePath = _rootPath != null ? UriToFilePath(uri, _rootPath) : null;
                var result = compiler.Compile(source, null, _defaultSyscalls, null,
                    effectiveResolver, filePath, null, _projectCompileOptions);

                if (!result.Success && result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
                        // DX8: Skip cross-file errors (tagged with [origin.ffs] prefix
                        // where origin differs from the current file).
                        // These errors belong to included files and will appear when
                        // those files are opened.
                        if (IsCrossFileError(error, filePath))
                            continue;
                        var diag = ErrorToDiagnostic(error, source);
                        diagnostics.Add(diag);
                    }
                }

                // Lang-8: emit warnings as severity=2 (Warning)
                if (result.Warnings != null)
                {
                    foreach (string warning in result.Warnings)
                    {
                        var diag = ErrorToDiagnostic(warning, source);
                        // Override severity from Error(1) to Warning(2)
                        diag.Set("severity", 2);
                        diagnostics.Add(diag);
                    }
                }
            }

            PublishDiagnostics(uri, diagnostics);
        }

        /// <summary>
        /// DX10: Recompile all open documents that depend on the given resolved file path.
        /// Called after a file changes to propagate diagnostics to files that include it.
        /// Uses a visited set to prevent infinite recursion in diamond/cyclic include chains.
        /// </summary>
        private void RecompileDependents(string resolvedFilePath, HashSet<string> visited)
        {
            if (resolvedFilePath == null) return;
            // Snapshot: CompileAndPublishDiagnostics may modify the dependency graph during iteration
            var dependents = new List<string>(_docStore.GetDependents(resolvedFilePath));
            foreach (string depUri in dependents)
            {
                if (visited.Contains(depUri)) continue;
                visited.Add(depUri);

                string content;
                if (_docStore.TryGetContent(depUri, out content))
                {
                    CompileAndPublishDiagnostics(depUri, content);
                    // Recurse: this file's own dependents may also need updating
                    string depPath = UriToPath(depUri);
                    if (depPath != null)
                        RecompileDependents(depPath, visited);
                }
            }
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

            // DX8: Strip [origin.ffs] prefix from error messages (cross-file tag)
            if (message.StartsWith("["))
            {
                int closeBracket = message.IndexOf(']');
                if (closeBracket > 0 && closeBracket + 1 < message.Length)
                    message = message.Substring(closeBracket + 1).TrimStart();
            }

            // Try to extract "(line N)" at end
            var lineMatch = Regex.Match(message, @"\(line\s+(\d+)\)\s*$");
            if (lineMatch.Success)
            {
                line = int.Parse(lineMatch.Groups[1].Value) - 1; // LSP uses 0-based lines
                message = message.Substring(0, lineMatch.Index).TrimEnd();
            }
            else
            {
                // Try to extract "at L:C"
                var atMatch = Regex.Match(message, @"at\s+(\d+):(\d+)");
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

        /// <summary>
        /// DX8: Check if an error message was tagged as originating from a different file.
        /// Format: "[origin.ffs] error message..." — if origin differs from currentFilePath, it's cross-file.
        /// Also handles preprocessor-tagged errors with the same format.
        /// </summary>
        internal static bool IsCrossFileError(string error, string currentFilePath)
        {
            if (error == null || !error.StartsWith("[")) return false;
            int closeBracket = error.IndexOf(']');
            if (closeBracket < 2) return false;
            string origin = error.Substring(1, closeBracket - 1);
            // If currentFilePath is null/empty, we can't compare — don't filter
            if (string.IsNullOrEmpty(currentFilePath)) return false;
            // Cross-file if origin doesn't match the current file
            return origin != currentFilePath;
        }

        /// <summary>
        /// DX15: Check if a symbol's OriginFile indicates it comes from a different file
        /// than the current document. Used to filter private symbols in cross-file completion.
        /// Returns true if the symbol is definitively from another file.
        /// Returns false if OriginFile is null/empty or matches the current file path.
        /// </summary>
        internal static bool IsFromOtherFile(string originFile, string currentFilePath)
        {
            if (string.IsNullOrEmpty(originFile) || string.IsNullOrEmpty(currentFilePath)) return false;
            string normOrigin = originFile.Replace('\\', '/');
            string normCurrent = currentFilePath.Replace('\\', '/');
            return !string.Equals(normOrigin, normCurrent, StringComparison.OrdinalIgnoreCase);
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
        // LSP4: Symbol analysis handlers
        // ============================================================

        /// <summary>
        /// Extract textDocument URI from request params.
        /// </summary>
        private string GetDocumentUri(JsonObject parameters)
        {
            return parameters?.GetObject("textDocument")?.GetString("uri");
        }

        /// <summary>
        /// Get cached AST for a document URI. Returns null if not available.
        /// </summary>
        private ModuleNode GetCachedAst(string uri)
        {
            return _docStore.GetAst(uri);
        }

        /// <summary>
        /// DX4-P3: Get merged (preprocessor-resolved) AST for a document URI.
        /// Falls back to the per-file AST if no merged AST is available.
        /// The merged AST includes symbols from all included files with OriginFile set.
        /// </summary>
        private ModuleNode GetMergedAst(string uri)
        {
            return _docStore.GetMergedAst(uri) ?? _docStore.GetAst(uri);
        }

        /// <summary>
        /// DX4-P3: Convert an OriginFile path (relative or absolute) to a file:// URI.
        /// Used for cross-file navigation (definition/references).
        /// </summary>
        internal static string FilePathToUri(string originFile, string rootPath)
        {
            if (originFile == null) return null;
            // Resolve relative paths against rootPath
            string absPath;
            if (rootPath != null && !Path.IsPathRooted(originFile))
                absPath = Path.GetFullPath(Path.Combine(rootPath, originFile));
            else if (Path.IsPathRooted(originFile))
                absPath = originFile;
            else
                return null; // relative path without rootPath — cannot resolve
            // Normalize separators for URI
            absPath = absPath.Replace('\\', '/');
            // Append .ffs extension if the file doesn't have one and the .ffs file exists
            if (!absPath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
            {
                string withExt = absPath + ".ffs";
                if (File.Exists(withExt))
                    absPath = withExt;
            }
            // Build file:// URI
            if (absPath.Length >= 2 && absPath[1] == ':')
                return "file:///" + Uri.EscapeDataString(absPath).Replace("%2F", "/").Replace("%3A", ":");
            return "file:///" + absPath.TrimStart('/');
        }

        private JsonObject HandleDocumentSymbol(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            if (ast == null) return MakeArrayResult(new List<object>());

            // Collect all top-level symbols with their line numbers for source order
            var entries = new List<(int line, JsonObject symbol)>();

            // Functions
            foreach (var func in ast.Functions)
            {
                entries.Add((func.Line, MakeSymbolInfo(func.Name, 12 /* Function */, func.Line, func.Column, func.Name.Length)));
            }

            // Structs
            foreach (var st in ast.Structs)
            {
                entries.Add((st.Line, MakeSymbolInfo(st.Name, 23 /* Struct */, st.Line, st.Column, st.Name.Length)));
            }

            // Lang-13: Enums
            foreach (var en in ast.Enums)
            {
                entries.Add((en.Line, MakeSymbolInfo(en.Name, 10 /* Enum */, en.Line, en.Column, en.Name.Length)));
            }

            // Sort by source line to preserve declaration order
            entries.Sort((a, b) => a.line.CompareTo(b.line));

            var symbols = new List<object>();
            foreach (var e in entries) symbols.Add(e.symbol);
            return MakeArrayResult(symbols);
        }

        private static JsonObject MakeSymbolInfo(string name, int kind, int line, int col, int nameLength)
        {
            var sym = new JsonObject();
            sym.Set("name", name);
            sym.Set("kind", kind);

            int lspLine = Math.Max(0, line - 1);
            int lspChar = Math.Max(0, col - 1);
            // DocumentSymbol uses "range" (full declaration) and "selectionRange" (name only)
            sym.Set("range", MakeRange(lspLine, lspChar, lspLine, lspChar + Math.Max(1, nameLength)));
            sym.Set("selectionRange", MakeRange(lspLine, lspChar, lspLine, lspChar + Math.Max(1, nameLength)));
            return sym;
        }

        /// <summary>
        /// Create LSP Location object. Uri is set only if non-null.
        /// Line/col are 1-based (AST convention), converted to 0-based (LSP convention).
        /// </summary>
        private static JsonObject MakeLocation(string uri, int line, int col, int nameLength)
        {
            var loc = new JsonObject();
            if (uri != null) loc.Set("uri", uri);

            int lspLine = Math.Max(0, line - 1);
            int lspChar = Math.Max(0, col - 1);
            var range = MakeRange(lspLine, lspChar, lspLine, lspChar + Math.Max(1, nameLength));
            loc.Set("range", range);
            return loc;
        }

        private static JsonObject MakeRange(int startLine, int startChar, int endLine, int endChar)
        {
            var range = new JsonObject();
            var start = new JsonObject();
            start.Set("line", startLine);
            start.Set("character", startChar);
            var end = new JsonObject();
            end.Set("line", endLine);
            end.Set("character", endChar);
            range.Set("start", start);
            range.Set("end", end);
            return range;
        }

        /// <summary>
        /// Wraps a list as a JSON-RPC result. For requests that return arrays
        /// (documentSymbol, references), we encode the array as a special object
        /// with "_array" key since our JsonObject doesn't support top-level arrays.
        /// The SendResponse method detects this and serializes correctly.
        /// </summary>
        private static JsonObject MakeArrayResult(List<object> items)
        {
            var wrapper = new JsonObject();
            wrapper.Set("_array", items);
            return wrapper;
        }

        // --- hover ---

        private JsonObject HandleHover(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);

            var position = parameters?.GetObject("position");
            if (position == null) return null;

            int lspLine = position.GetInt("line");
            int lspChar = position.GetInt("character");
            int astLine = lspLine + 1;
            int astCol = lspChar + 1;

            // DX17: Use unified symbol resolution (shared with Definition/References/Rename)
            var (symbol, mergedAst) = ResolveSymbol(uri, astLine, astCol);

            string hoverText = null;
            if (symbol != null && symbol.Value.Kind != SymbolKindTag.IncludeFile)
            {
                hoverText = FormatHoverForSymbol(symbol.Value, mergedAst);
            }

            // Fallback for keyword-level hover (cursor on "func"/"struct"/"enum"/"var"/"const" keyword)
            // and edge cases where ResolveSymbol doesn't match but FindHoverText does
            if (hoverText == null)
            {
                if (mergedAst == null) mergedAst = GetMergedAst(uri);
                if (mergedAst != null) hoverText = FindHoverText(mergedAst, astLine, astCol);
            }

            if (hoverText == null) return null;

            // Auto-wrap in code fence if not already markdown
            if (!hoverText.StartsWith("```"))
                hoverText = $"```ffvm\n{hoverText}\n```";

            var result = new JsonObject();
            var contents = new JsonObject();
            contents.Set("kind", "markdown");
            contents.Set("value", hoverText);
            result.Set("contents", contents);
            return result;
        }

        /// <summary>
        /// Find hover text for the symbol at (line, col) in the AST.
        /// Returns null if no symbol found.
        /// </summary>
        internal static string FindHoverText(ModuleNode ast, int line, int col)
        {
            // Check function declarations (hover on 'func' keyword line, on name)
            foreach (var func in ast.Functions)
            {
                if (MatchesName(func.Line, func.Column, "func".Length + 1, func.Name, line, col))
                {
                    return FormatFuncHover(func);
                }
            }

            // Check struct declarations
            foreach (var st in ast.Structs)
            {
                if (MatchesName(st.Line, st.Column, "struct".Length + 1, st.Name, line, col))
                {
                    return FormatStructHover(st);
                }
            }

            // Lang-1: Check module-level variable/const declarations
            foreach (var mv in ast.ModuleVariables)
            {
                string keyword = mv.IsConst ? "const" : "var";
                if (MatchesName(mv.Line, mv.Column, keyword.Length + 1, mv.Name, line, col))
                {
                    string prefix = mv.IsConst ? "const" : "var";
                    string typeStr = mv.TypeName ?? "int";
                    return $"(module {prefix}) {mv.Name}: {typeStr}";
                }
            }

            // Lang-13: Check enum declarations
            foreach (var en in ast.Enums)
            {
                if (MatchesName(en.Line, en.Column, "enum".Length + 1, en.Name, line, col))
                {
                    return FormatEnumHover(en);
                }
            }

            // Walk function bodies for identifiers, calls, var decls
            foreach (var func in ast.Functions)
            {
                // DX13: Check parameter declarations in function signature
                foreach (var param in func.Parameters)
                {
                    if (param.NameLine > 0 && param.NameLine == line && ColMatches(param.NameColumn, param.Name.Length, col))
                    {
                        string paramHover = $"(parameter) {FormatParamDecl(param)}";
                        if (param.DocComment != null)
                            paramHover += $"\n\n{param.DocComment}";
                        return paramHover;
                    }
                }

                string result = FindHoverInBlock(ast, func, func.Body, line, col);
                if (result != null) return result;
            }

            return null;
        }

        // R1: FindHover — replaced hand-written Block/Stmt/Expr triad with AstWalker subclass.
        private static string FindHoverInBlock(ModuleNode ast, FuncDecl currentFunc, BlockStmt block, int line, int col)
        {
            var w = new FindHoverWalker(ast, currentFunc, line, col);
            w.WalkBlock(block);
            return w.Result;
        }

        private sealed class FindHoverWalker : AstWalker
        {
            private readonly ModuleNode _ast;
            private readonly FuncDecl _func;
            private readonly int _line;
            private readonly int _col;
            public string Result;

            public FindHoverWalker(ModuleNode ast, FuncDecl func, int line, int col) { _ast = ast; _func = func; _line = line; _col = col; }

            private void SetResult(string r) { Result = r; _abort = true; }

            protected override bool VisitStmt(Stmt stmt)
            {
                if (stmt is VarDeclStmt vd)
                {
                    // Check initializer FIRST (has precise column matching for calls, identifiers, etc.)
                    if (vd.Initializer != null)
                    {
                        WalkExpr(vd.Initializer);
                        if (Result != null) return true;
                    }
                    // Variable name hover — only when cursor is on the name itself
                    // VarDeclStmt.Column points to 'var'; name starts at Column + 4 ("var ")
                    if (vd.Line == _line)
                    {
                        int nameStart = vd.Column + 4; // skip "var "
                        if (ColMatches(nameStart, vd.Name.Length, _col))
                        {
                            string typeStr = vd.TypeName ?? "int";
                            SetResult($"var {vd.Name}: {typeStr}");
                        }
                    }
                    return true; // handled all VarDeclStmt paths
                }
                return false;
            }

            protected override bool VisitExpr(Expr expr)
            {
                if (expr is IdentifierExpr id && id.Line == _line && ColMatches(id.Column, id.Name.Length, _col))
                {
                    // Look up variable declaration in current function
                    string typeInfo = FindVarType(_func, id.Name);
                    if (typeInfo != null)
                    { SetResult($"var {id.Name}: {typeInfo}"); return true; }
                    // Check if it's a parameter
                    foreach (var p in _func.Parameters)
                    {
                        if (p.Name == id.Name)
                        {
                            string paramHover = $"(parameter) {FormatParamDecl(p)}";
                            if (p.DocComment != null)
                                paramHover += $"\n\n{p.DocComment}";
                            SetResult(paramHover);
                            return true;
                        }
                    }
                    SetResult($"{id.Name}");
                    return true;
                }

                if (expr is CallExpr call && call.Line == _line && ColMatches(call.Column, call.FunctionName.Length, _col))
                {
                    // Look up function declaration
                    foreach (var func in _ast.Functions)
                    {
                        if (func.Name == call.FunctionName)
                        { SetResult(FormatFuncHover(func)); return true; }
                    }
                    SetResult($"func {call.FunctionName}(...)");
                    return true;
                }

                if (expr is FieldAccessExpr fa && fa.Line == _line)
                {
                    // Lang-13: enum member hover — when target is an enum name, show member info
                    if (fa.Target is IdentifierExpr faEnumId)
                    {
                        foreach (var en in _ast.Enums)
                        {
                            if (en.Name == faEnumId.Name)
                            {
                                // Cursor might be on the enum name itself or on the member name
                                if (ColMatches(faEnumId.Column, faEnumId.Name.Length, _col))
                                { SetResult(FormatEnumHover(en)); return true; }
                                // Check if cursor is on the member name (after the dot)
                                foreach (var member in en.Members)
                                {
                                    if (member.Name == fa.FieldName)
                                    { SetResult($"(enum member) {en.Name}.{member.Name}"); return true; }
                                }
                                return true; // matched enum, but no member hit — stop
                            }
                        }
                    }
                    // Not an enum — continue walking into target
                    return false;
                }

                if (expr is StructLiteralExpr sl)
                {
                    // US: Hover on struct literal name → show struct definition with doc comment
                    if (sl.Line == _line && sl.TypeName != null && ColMatches(sl.Column, sl.TypeName.Length, _col))
                    {
                        foreach (var st in _ast.Structs)
                        {
                            if (st.Name == sl.TypeName)
                            { SetResult(FormatStructHover(st)); return true; }
                        }
                        SetResult($"struct {sl.TypeName}");
                        return true;
                    }
                    // Continue into field values
                    return false;
                }

                return false; // continue walking children
            }
        }

        /// <summary>
        /// Find the type of a variable declared in a function's body.
        /// Walks statements looking for VarDeclStmt with matching name.
        /// </summary>
        // R1: FindVarType — replaced hand-written Block/Stmt triad with AstWalker subclass.
        private static string FindVarType(FuncDecl func, string varName)
        {
            var w = new FindVarTypeWalker(varName);
            w.WalkBlock(func.Body);
            return w.Result;
        }

        private sealed class FindVarTypeWalker : AstWalker
        {
            private readonly string _varName;
            public string Result;
            public FindVarTypeWalker(string varName) { _varName = varName; }
            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                if (vd.Name == _varName)
                {
                    Result = vd.TypeName ?? "int";
                    _abort = true;
                }
            }
        }

        /// <summary>
        /// Check if column col falls within [startCol, startCol + nameLen).
        /// </summary>
        private static bool ColMatches(int startCol, int nameLen, int col)
        {
            return col >= startCol && col < startCol + nameLen;
        }

        /// <summary>
        /// Check if position matches a keyword+name pattern (e.g., "func entry" → name starts after keyword).
        /// FuncDecl.Line/Column point to 'func' keyword. Name starts at Column + offset.
        /// We match on either the keyword or the name.
        /// </summary>
        private static bool MatchesName(int declLine, int declCol, int keywordPlusSpace, string name, int line, int col)
        {
            if (declLine != line) return false;
            int nameStart = declCol + keywordPlusSpace;
            return ColMatches(nameStart, name.Length, col) || ColMatches(declCol, keywordPlusSpace - 1, col);
        }

        private static string FormatParamDecl(ParamDecl p)
        {
            string s = $"{p.Name}: {p.TypeName}";
            if (p.DefaultValue != null)
                s += $" = {FormatDefaultValue(p.DefaultValue)}";
            return s;
        }

        private static string FormatDefaultValue(Expr expr)
        {
            if (expr is IntLiteralExpr il) return il.Value.ToString();
            if (expr is NumberLiteralExpr nl) return nl.Value.ToString();
            if (expr is BoolLiteralExpr bl) return bl.Value ? "true" : "false";
            if (expr is UnaryExpr ue && ue.Kind == NodeKind.Negate)
                return $"-{FormatDefaultValue(ue.Operand)}";
            return "...";
        }

        private static string FormatFuncSignature(FuncDecl func)
        {
            var parts = new List<string>();
            foreach (var p in func.Parameters)
                parts.Add(FormatParamDecl(p));
            string ret = func.ReturnType != null ? $": {func.ReturnType}" : "";
            string prefix = func.IsExternal ? "external func" : "func";
            return $"{prefix} {func.Name}({string.Join(", ", parts)}){ret}";
        }

        private static string FormatFuncHover(FuncDecl func)
        {
            string sig = FormatFuncSignature(func);
            var sb = new System.Text.StringBuilder();
            sb.Append($"```ffvm\n{sig}\n```");
            if (func.DocComment != null)
            {
                sb.Append($"\n---\n{func.DocComment}");
            }
            bool hasParamDoc = false;
            foreach (var p in func.Parameters)
            {
                if (p.DocComment != null)
                {
                    if (!hasParamDoc) { sb.Append("\n\n**Parameters:**\n"); hasParamDoc = true; }
                    sb.Append($"\n- `{p.Name}` — {p.DocComment}");
                }
            }
            if (func.ReturnDoc != null)
                sb.Append($"\n\n**Returns:** {func.ReturnDoc}");
            return sb.ToString();
        }

        private static string FormatFuncDoc(FuncDecl func)
        {
            var parts = new List<string>();
            if (func.DocComment != null) parts.Add(func.DocComment);
            foreach (var p in func.Parameters)
            {
                if (p.DocComment != null)
                    parts.Add($"@param `{p.Name}` — {p.DocComment}");
            }
            if (func.ReturnDoc != null) parts.Add($"@return {func.ReturnDoc}");
            return parts.Count > 0 ? string.Join("\n\n", parts) : null;
        }

        private static string FormatStructSignature(StructDecl st)
        {
            var fields = new List<string>();
            foreach (var f in st.Fields)
                fields.Add($"  {f.Name}: {f.TypeName}");
            return $"struct {st.Name} {{\n{string.Join("\n", fields)}\n}}";
        }

        private static string FormatStructHover(StructDecl st)
        {
            string sig = FormatStructSignature(st);
            if (st.DocComment == null)
                return $"```ffvm\n{sig}\n```";
            return $"```ffvm\n{sig}\n```\n---\n{st.DocComment}";
        }

        /// <summary>
        /// Lang-13: Format enum hover text showing all members and their values.
        /// </summary>
        private static string FormatEnumHover(EnumDecl en)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("enum ").Append(en.Name).Append(" {");
            int nextValue = 0;
            for (int i = 0; i < en.Members.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(" ").Append(en.Members[i].Name);
                if (en.Members[i].ValueExpr != null)
                {
                    // Show the explicit value expression as-is; we approximate with sequential logic
                    sb.Append(" = ...");
                }
                // For display, just show the member name; values are compile-time
            }
            if (en.Members.Count > 0) sb.Append(" ");
            sb.Append("}");
            string sig = sb.ToString();
            if (en.DocComment == null)
                return $"```ffvm\n{sig}\n```";
            return $"```ffvm\n{sig}\n```\n---\n{en.DocComment}";
        }

        /// <summary>
        /// DX17: Generate hover text from a resolved symbol by looking up the AST node.
        /// Returns null if no hover text can be generated for this symbol kind.
        /// </summary>
        private static string FormatHoverForSymbol(ResolvedSymbol symbol, ModuleNode ast)
        {
            if (ast == null) return null;

            switch (symbol.Kind)
            {
                case SymbolKindTag.Function:
                    foreach (var func in ast.Functions)
                        if (func.Name == symbol.Name) return FormatFuncHover(func);
                    return $"func {symbol.Name}(...)";

                case SymbolKindTag.Struct:
                    foreach (var st in ast.Structs)
                        if (st.Name == symbol.Name) return FormatStructHover(st);
                    return $"struct {symbol.Name}";

                case SymbolKindTag.Enum:
                    foreach (var en in ast.Enums)
                        if (en.Name == symbol.Name) return FormatEnumHover(en);
                    return $"enum {symbol.Name}";

                case SymbolKindTag.Variable:
                    // Check function-scoped variables first
                    if (symbol.ScopeFunc != null)
                    {
                        foreach (var func in ast.Functions)
                        {
                            if (func.Name == symbol.ScopeFunc)
                            {
                                string typeInfo = FindVarType(func, symbol.Name);
                                if (typeInfo != null) return $"var {symbol.Name}: {typeInfo}";
                            }
                        }
                    }
                    // Check module-level variables
                    foreach (var mv in ast.ModuleVariables)
                    {
                        if (mv.Name == symbol.Name)
                        {
                            string prefix = mv.IsConst ? "const" : "var";
                            string typeStr = mv.TypeName ?? "int";
                            return $"(module {prefix}) {mv.Name}: {typeStr}";
                        }
                    }
                    return symbol.Name;

                case SymbolKindTag.Parameter:
                    if (symbol.ScopeFunc != null)
                    {
                        foreach (var func in ast.Functions)
                        {
                            if (func.Name == symbol.ScopeFunc)
                            {
                                foreach (var p in func.Parameters)
                                {
                                    if (p.Name == symbol.Name)
                                    {
                                        string paramHover = $"(parameter) {FormatParamDecl(p)}";
                                        if (p.DocComment != null)
                                            paramHover += $"\n\n{p.DocComment}";
                                        return paramHover;
                                    }
                                }
                            }
                        }
                    }
                    return $"(parameter) {symbol.Name}";

                case SymbolKindTag.StructField:
                    if (symbol.ParentName != null)
                    {
                        foreach (var st in ast.Structs)
                        {
                            if (st.Name == symbol.ParentName)
                            {
                                foreach (var f in st.Fields)
                                    if (f.Name == symbol.Name)
                                        return $"(field) {symbol.ParentName}.{f.Name}: {f.TypeName}";
                            }
                        }
                    }
                    // parentName unknown — search all structs
                    foreach (var st in ast.Structs)
                        foreach (var f in st.Fields)
                            if (f.Name == symbol.Name)
                                return $"(field) {st.Name}.{f.Name}: {f.TypeName}";
                    return $"(field) {symbol.Name}";

                case SymbolKindTag.EnumMember:
                    return $"(enum member) {symbol.ParentName}.{symbol.Name}";

                default:
                    return null;
            }
        }

        // --- symbol resolution helpers ---

        /// <summary>
        /// DX17: Unified symbol resolution. Combines dual-AST symbol identification
        /// (FindSymbolAtPosition on per-file + merged AST) with definition location
        /// (FindDefinitionLocation) into a single ResolvedSymbol.
        ///
        /// Returns (resolved symbol, merged AST for further operations like CollectReferencesWithOrigin).
        /// For IncludeFile symbols, DefLine/DefCol/OriginFile are not populated.
        /// </summary>
        private (ResolvedSymbol? symbol, ModuleNode mergedAst) ResolveSymbol(string uri, int astLine, int astCol)
        {
            var ast = GetCachedAst(uri);
            if (ast == null) return (null, null);

            // Per-file AST resolves include declarations (merged AST strips Imports)
            var perFileTarget = FindSymbolAtPosition(ast, astLine, astCol);
            var mergedAst = GetMergedAst(uri);

            // IncludeFile is only detectable from per-file AST
            if (perFileTarget != null && perFileTarget.Value.kind == SymbolKindTag.IncludeFile)
            {
                return (new ResolvedSymbol
                {
                    Kind = SymbolKindTag.IncludeFile,
                    Name = perFileTarget.Value.name,
                }, mergedAst);
            }

            // Dual-AST resolution: fall back to merged AST for cross-file symbols
            var resolvedTarget = perFileTarget;
            if (resolvedTarget == null || resolvedTarget.Value.kind == SymbolKindTag.Variable
                || (resolvedTarget.Value.kind == SymbolKindTag.StructField && resolvedTarget.Value.parentName == null))
            {
                var mergedTarget = FindSymbolAtPosition(mergedAst, astLine, astCol);
                if (mergedTarget != null) resolvedTarget = mergedTarget;
            }

            if (resolvedTarget == null) return (null, mergedAst);

            // Compute definition location in one pass (eliminates second lookup)
            var defLoc = FindDefinitionLocation(mergedAst, resolvedTarget.Value.name,
                resolvedTarget.Value.kind, resolvedTarget.Value.scopeFunc,
                resolvedTarget.Value.parentName, resolvedTarget.Value.declLine, resolvedTarget.Value.declCol);

            return (new ResolvedSymbol
            {
                Kind = resolvedTarget.Value.kind,
                Name = resolvedTarget.Value.name,
                ParentName = resolvedTarget.Value.parentName,
                ScopeFunc = resolvedTarget.Value.scopeFunc,
                DefLine = defLoc?.line ?? 0,
                DefCol = defLoc?.col ?? 0,
                NameLen = defLoc?.nameLen ?? 0,
                OriginFile = defLoc?.originFile,
                ScopeDeclLine = resolvedTarget.Value.declLine,
                ScopeDeclCol = resolvedTarget.Value.declCol,
            }, mergedAst);
        }

        // --- definition ---

        private JsonObject HandleDefinition(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var position = parameters?.GetObject("position");
            if (uri == null || position == null) return null;

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            // DX17: Unified symbol resolution (replaces ResolveSymbolDualAst + FindDefinitionLocation)
            var (symbol, _) = ResolveSymbol(uri, astLine, astCol);
            if (symbol == null) return null;

            if (symbol.Value.Kind == SymbolKindTag.IncludeFile)
            {
                string includeUri = ResolveIncludeFileUri(uri, symbol.Value.Name);
                if (includeUri != null)
                    return MakeLocation(includeUri, 1, 1, 0);
                return null;
            }

            if (symbol.Value.DefLine == 0) return null;

            // DX4-P3: Resolve target URI — may point to a different file via OriginFile
            string targetUri = ResolveOriginUri(uri, symbol.Value.OriginFile);
            var loc = MakeLocation(targetUri, symbol.Value.DefLine, symbol.Value.DefCol, symbol.Value.NameLen);
            return loc;
        }

        /// <summary>
        /// DX4-P3: Resolve the target URI for a definition or reference location.
        /// If the symbol's OriginFile matches the current file's path, returns the requesting URI.
        /// Otherwise, converts OriginFile to a file:// URI for cross-file navigation.
        /// When OriginFile is a relative module name (not an absolute path), uses
        /// the workspace file resolver's include paths to locate the actual file.
        /// </summary>
        private string ResolveOriginUri(string requestingUri, string originFile)
        {
            if (originFile == null) return requestingUri;

            // If originFile is a relative path, try resolving via file resolver's include paths first.
            // This handles cases where OriginFile is a module name (e.g. "common/constants")
            // rather than an absolute filesystem path.
            string resolvedOriginFile = originFile;
            if (_fileResolver != null && !Path.IsPathRooted(originFile))
            {
                string resolved = _fileResolver.ResolveFilePath(originFile);
                if (resolved != null)
                    resolvedOriginFile = resolved;
            }

            // Check if originFile matches the requesting document's path
            string requestingPath = _rootPath != null ? UriToFilePath(requestingUri, _rootPath) : null;
            if (requestingPath != null)
            {
                // Normalize both for comparison
                string normOrigin = resolvedOriginFile.Replace('\\', '/');
                string normReq = requestingPath.Replace('\\', '/');
                if (string.Equals(normOrigin, normReq, StringComparison.OrdinalIgnoreCase))
                    return requestingUri;
            }
            // Also check full absolute path
            string requestingAbsPath = UriToPath(requestingUri);
            if (requestingAbsPath != null)
            {
                string normOriginAbs = resolvedOriginFile.Replace('\\', '/');
                string normAbsReq = requestingAbsPath.Replace('\\', '/');
                if (string.Equals(normOriginAbs, normAbsReq, StringComparison.OrdinalIgnoreCase))
                    return requestingUri;
            }

            // Cross-file: convert OriginFile to URI
            string crossUri = FilePathToUri(resolvedOriginFile, _rootPath);
            return crossUri ?? requestingUri;
        }

        // --- references ---

        private JsonObject HandleReferences(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var position = parameters?.GetObject("position");
            if (uri == null || position == null) return MakeArrayResult(new List<object>());

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            // DX17: Unified symbol resolution
            var (symbol, mergedAst) = ResolveSymbol(uri, astLine, astCol);

            // DX5: Include file references — find all includes of the same path
            if (symbol != null && symbol.Value.Kind == SymbolKindTag.IncludeFile)
            {
                var locations = new List<object>();
                var perFileAst = GetCachedAst(uri);
                if (perFileAst != null)
                    CollectIncludeReferences(perFileAst, symbol.Value.Name, uri, locations);
                return MakeArrayResult(locations);
            }

            if (symbol == null) return MakeArrayResult(new List<object>());

            // DX4-P3: Use merged AST for cross-file reference collection
            var locs = new List<object>();
            CollectReferencesWithOrigin(mergedAst, symbol.Value.Name, symbol.Value.Kind, uri, locs, symbol.Value.ParentName, symbol.Value.ScopeFunc, symbol.Value.ScopeDeclLine, symbol.Value.ScopeDeclCol);
            return MakeArrayResult(locs);
        }

        // ============================================================
        // LSP5: Code completion
        // ============================================================

        private JsonObject HandleCompletion(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            // DX4-P3: Use merged AST for symbol lists (includes cross-file symbols)
            var mergedAst = GetMergedAst(uri);
            string source = null;
            if (uri != null) _docStore.TryGetContent(uri, out source);

            // DX15: Resolve current file's absolute path for private visibility filtering.
            // Private symbols from other files should not appear in completion.
            string currentFilePath = uri != null ? UriToPath(uri) : null;

            var position = parameters?.GetObject("position");
            if (position == null) return MakeArrayResult(new List<object>());

            int lspLine = position.GetInt("line");
            int lspChar = position.GetInt("character");

            // Detect dot context: check if the character before cursor is '.'
            string lineText = GetLineText(source, lspLine);
            bool isDotContext = false;
            string dotPrefix = null;
            if (lineText != null && lspChar > 0 && lspChar <= lineText.Length)
            {
                // Walk back: if we see '.' possibly preceded by identifier
                int checkPos = lspChar - 1;
                // Skip any partial identifier typed after the dot
                while (checkPos >= 0 && checkPos < lineText.Length && (char.IsLetterOrDigit(lineText[checkPos]) || lineText[checkPos] == '_'))
                    checkPos--;
                if (checkPos >= 0 && lineText[checkPos] == '.')
                {
                    isDotContext = true;
                    // SN1: Extract the full dot-chain before the dot (e.g. "a.inner" from "a.inner.|")
                    int chainEnd = checkPos;
                    int chainStart = chainEnd - 1;
                    while (chainStart >= 0 && (char.IsLetterOrDigit(lineText[chainStart]) || lineText[chainStart] == '_' || lineText[chainStart] == '.'))
                        chainStart--;
                    chainStart++;
                    if (chainStart < chainEnd)
                        dotPrefix = lineText.Substring(chainStart, chainEnd - chainStart);
                }
            }

            var items = new List<object>();

            if (isDotContext && dotPrefix != null && mergedAst != null)
            {
                // SN1: Struct field completion with nested dot-chain support
                // dotPrefix can be "a" or "a.inner" etc.
                FuncDecl containingFunc = FindContainingFunction(ast, lspLine + 1);
                if (containingFunc != null)
                {
                    string resolvedStructType = null;
                    int dotIdx = dotPrefix.IndexOf('.');
                    if (dotIdx < 0)
                    {
                        // Simple case: "varName."
                        resolvedStructType = FindVariableStructType(mergedAst, containingFunc, dotPrefix);
                    }
                    else
                    {
                        // Chained case: "varName.field1.field2." — resolve through struct types
                        string rootVar = dotPrefix.Substring(0, dotIdx);
                        string fieldChain = dotPrefix.Substring(dotIdx + 1);
                        string currentType = FindVariableStructType(mergedAst, containingFunc, rootVar);
                        if (currentType != null)
                        {
                            string[] parts = fieldChain.Split('.');
                            for (int pi = 0; pi < parts.Length && currentType != null; pi++)
                            {
                                string nextType = null;
                                foreach (var st in mergedAst.Structs)
                                {
                                    if (st.Name == currentType)
                                    {
                                        foreach (var field in st.Fields)
                                        {
                                            if (field.Name == parts[pi])
                                            {
                                                // Check if this field's type is a struct
                                                foreach (var st2 in mergedAst.Structs)
                                                {
                                                    if (st2.Name == field.TypeName)
                                                    {
                                                        nextType = field.TypeName;
                                                        break;
                                                    }
                                                }
                                                break;
                                            }
                                        }
                                        break;
                                    }
                                }
                                currentType = nextType;
                            }
                            resolvedStructType = currentType;
                        }
                    }

                    if (resolvedStructType != null)
                    {
                        foreach (var st in mergedAst.Structs)
                        {
                            if (st.Name == resolvedStructType)
                            {
                                // DX15: Skip field completion for private structs from other files
                                if (st.IsPrivate && IsFromOtherFile(st.OriginFile, currentFilePath)) break;
                                foreach (var field in st.Fields)
                                {
                                    items.Add(MakeCompletionItem(field.Name, 5 /* Field */,
                                        $"{field.Name}: {field.TypeName}"));
                                }
                                break;
                            }
                        }
                    }
                }

                // Lang-13: enum member completion — "EnumName." → list members
                if (dotPrefix != null && dotPrefix.IndexOf('.') < 0)
                {
                    foreach (var en in mergedAst.Enums)
                    {
                        if (en.Name == dotPrefix)
                        {
                            // DX15: Skip member completion for private enums from other files
                            if (en.IsPrivate && IsFromOtherFile(en.OriginFile, currentFilePath)) break;
                            foreach (var member in en.Members)
                            {
                                items.Add(MakeCompletionItem(member.Name, 20 /* EnumMember */,
                                    $"{en.Name}.{member.Name}"));
                            }
                            break;
                        }
                    }
                }
            }
            else
            {
                // General completion: keywords + functions + variables + structs + syscalls
                // Keywords
                foreach (string kw in Lexer.Keywords.Keys)
                {
                    items.Add(MakeCompletionItem(kw, 14 /* Keyword */, null));
                }

                if (mergedAst != null)
                {
                    // Functions
                    foreach (var func in mergedAst.Functions)
                    {
                        // DX15: Skip private functions from other files
                        if (func.IsPrivate && IsFromOtherFile(func.OriginFile, currentFilePath)) continue;
                        string detail = FormatFuncSignature(func);
                        if (func.DocComment != null)
                            detail += "  — " + func.DocComment.Replace("\n", " ");
                        items.Add(MakeCompletionItem(func.Name, 3 /* Function */,
                            detail, FormatFuncDoc(func)));
                    }

                    // Structs
                    foreach (var st in mergedAst.Structs)
                    {
                        // DX15: Skip private structs from other files
                        if (st.IsPrivate && IsFromOtherFile(st.OriginFile, currentFilePath)) continue;
                        items.Add(MakeCompletionItem(st.Name, 22 /* Struct */, null));
                    }

                    // Lang-13: Enums
                    foreach (var en in mergedAst.Enums)
                    {
                        // DX15: Skip private enums from other files
                        if (en.IsPrivate && IsFromOtherFile(en.OriginFile, currentFilePath)) continue;
                        items.Add(MakeCompletionItem(en.Name, 13 /* Enum */, $"enum {en.Name}"));
                    }

                    // Lang-1: Module-level variables and constants
                    foreach (var mv in mergedAst.ModuleVariables)
                    {
                        // DX15: Skip private module variables from other files
                        if (mv.IsPrivate && IsFromOtherFile(mv.OriginFile, currentFilePath)) continue;
                        string prefix = mv.IsConst ? "const" : "var";
                        string typeStr = mv.TypeName ?? "int";
                        items.Add(MakeCompletionItem(mv.Name, 6 /* Variable */,
                            $"(module {prefix}) {mv.Name}: {typeStr}"));
                    }

                    // Scope-aware variables: find which function contains the cursor
                    FuncDecl containingFunc = FindContainingFunction(ast, lspLine + 1);
                    if (containingFunc != null)
                    {
                        // Parameters
                        foreach (var param in containingFunc.Parameters)
                        {
                            items.Add(MakeCompletionItem(param.Name, 6 /* Variable */,
                                $"(parameter) {FormatParamDecl(param)}"));
                        }
                        // Local variables declared before cursor line
                        CollectVariablesInScope(containingFunc.Body, lspLine + 1, items);
                    }
                }

                // Syscall names (with signature metadata from LSP6 if available)
                foreach (string name in _defaultSyscalls.Keys)
                {
                    string detail;
                    SyscallSignature sig;
                    if (_syscallSignatures.TryGetValue(name, out sig))
                        detail = sig.Format(name);
                    else
                        detail = $"(syscall) {name}";
                    items.Add(MakeCompletionItem(name, 3 /* Function */, detail));
                }
            }

            return MakeArrayResult(items);
        }

        // ============================================================
        // LSP7: Signature help (parameter hints)
        // ============================================================

        private JsonObject HandleSignatureHelp(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            string source = null;
            if (uri != null) _docStore.TryGetContent(uri, out source);
            if (source == null) return null;

            var position = parameters?.GetObject("position");
            if (position == null) return null;

            int lspLine = position.GetInt("line");
            int lspChar = position.GetInt("character");

            // Convert LSP position to offset in source
            int offset = LspPositionToOffset(source, lspLine, lspChar);
            if (offset < 0) return null;

            // Scan backwards from cursor to find the function name and active parameter index
            string funcName;
            int activeParam;
            if (!FindCallContext(source, offset, out funcName, out activeParam))
                return null;

            // Look up function signature: first user functions, then syscalls
            // DX4-P3: Use merged AST for cross-file function signatures
            var mergedAst = GetMergedAst(uri);

            // User-defined function
            if (mergedAst != null)
            {
                foreach (var func in mergedAst.Functions)
                {
                    if (func.Name == funcName)
                        return MakeSignatureHelp(FormatFuncSignature(func), func.Parameters, activeParam, func.DocComment);
                }
            }

            // Syscall with signature metadata (LSP6)
            SyscallSignature sig;
            if (_syscallSignatures.TryGetValue(funcName, out sig))
                return MakeSignatureHelp(sig.Format(funcName), sig.Parameters, activeParam);

            // Syscall without metadata — no parameter info available
            if (_defaultSyscalls.ContainsKey(funcName))
                return null;

            return null;
        }

        /// <summary>
        /// Convert LSP 0-based line/character to a character offset in the source string.
        /// </summary>
        private static int LspPositionToOffset(string source, int lspLine, int lspChar)
        {
            int line = 0;
            int i = 0;
            while (i < source.Length && line < lspLine)
            {
                if (source[i] == '\n') line++;
                i++;
            }
            if (line != lspLine) return -1;
            int result = i + lspChar;
            return result <= source.Length ? result : -1;
        }

        /// <summary>
        /// Scan backwards from cursor offset to find the enclosing function call name
        /// and the 0-based index of the active parameter (comma count).
        /// Handles nested parentheses and string literals.
        /// </summary>
        private static bool FindCallContext(string source, int offset, out string funcName, out int activeParam)
        {
            funcName = null;
            activeParam = 0;

            int depth = 0;
            int commas = 0;
            int i = offset - 1;

            while (i >= 0)
            {
                char c = source[i];

                // Skip string literals (scan backwards to matching quote, handling escapes)
                if (c == '"')
                {
                    i--;
                    while (i >= 0)
                    {
                        if (source[i] == '"')
                        {
                            // Check if this quote is escaped (count preceding backslashes)
                            int bs = 0;
                            int j = i - 1;
                            while (j >= 0 && source[j] == '\\') { bs++; j--; }
                            if (bs % 2 == 0) break; // unescaped quote — end of string
                        }
                        i--;
                    }
                    i--;
                    continue;
                }

                if (c == ')')
                {
                    depth++;
                    i--;
                    continue;
                }

                if (c == '(')
                {
                    if (depth > 0)
                    {
                        depth--;
                        i--;
                        continue;
                    }
                    // Found the opening paren of our call — extract function name
                    int nameEnd = i;
                    int nameStart = nameEnd - 1;
                    // Skip whitespace before '('
                    while (nameStart >= 0 && char.IsWhiteSpace(source[nameStart])) nameStart--;
                    nameEnd = nameStart + 1;
                    // Read identifier characters
                    while (nameStart >= 0 && (char.IsLetterOrDigit(source[nameStart]) || source[nameStart] == '_'))
                        nameStart--;
                    nameStart++;
                    if (nameStart < nameEnd)
                    {
                        funcName = source.Substring(nameStart, nameEnd - nameStart);
                        activeParam = commas;
                        return true;
                    }
                    return false;
                }

                if (c == ',' && depth == 0)
                {
                    commas++;
                }

                i--;
            }

            return false;
        }

        /// <summary>
        /// Build a SignatureHelp response object.
        /// </summary>
        private static JsonObject MakeSignatureHelp(string label, IReadOnlyList<ParamDecl> funcParams, int activeParam, string funcDoc = null)
        {
            var paramInfos = new List<object>();
            foreach (var p in funcParams)
            {
                var pi = new JsonObject();
                pi.Set("label", FormatParamDecl(p));
                if (p.DocComment != null)
                {
                    var doc = new JsonObject();
                    doc.Set("kind", "markdown");
                    doc.Set("value", p.DocComment);
                    pi.Set("documentation", doc);
                }
                paramInfos.Add(pi);
            }
            return BuildSignatureHelpResult(label, paramInfos, activeParam, funcDoc);
        }

        private static JsonObject MakeSignatureHelp(string label, SyscallParamInfo[] syscallParams, int activeParam)
        {
            var paramInfos = new List<object>();
            foreach (var p in syscallParams)
            {
                var pi = new JsonObject();
                pi.Set("label", $"{p.Name}: {p.TypeName}");
                paramInfos.Add(pi);
            }
            return BuildSignatureHelpResult(label, paramInfos, activeParam);
        }

        private static JsonObject BuildSignatureHelpResult(string label, List<object> paramInfos, int activeParam, string funcDoc = null)
        {
            var sig = new JsonObject();
            sig.Set("label", label);
            sig.Set("parameters", paramInfos);
            if (funcDoc != null)
            {
                var doc = new JsonObject();
                doc.Set("kind", "markdown");
                doc.Set("value", funcDoc);
                sig.Set("documentation", doc);
            }

            var signatures = new List<object> { sig };

            var result = new JsonObject();
            result.Set("signatures", signatures);
            result.Set("activeSignature", 0);
            result.Set("activeParameter", activeParam);
            return result;
        }

        private static JsonObject MakeCompletionItem(string label, int kind, string detail, string documentation = null)
        {
            var item = new JsonObject();
            item.Set("label", label);
            item.Set("kind", kind);
            if (detail != null)
                item.Set("detail", detail);
            if (documentation != null)
            {
                var doc = new JsonObject();
                doc.Set("kind", "markdown");
                doc.Set("value", documentation);
                item.Set("documentation", doc);
            }
            return item;
        }

        /// <summary>
        /// Get the text of a specific line (0-based) from source.
        /// </summary>
        private static string GetLineText(string source, int lspLine)
        {
            if (source == null) return null;
            int lineIdx = 0;
            int start = 0;
            for (int i = 0; i <= source.Length; i++)
            {
                if (i == source.Length || source[i] == '\n')
                {
                    if (lineIdx == lspLine)
                    {
                        int end = i;
                        if (end > start && source[end - 1] == '\r') end--;
                        return source.Substring(start, end - start);
                    }
                    lineIdx++;
                    start = i + 1;
                }
            }
            return null;
        }

        /// <summary>
        /// Find the function declaration that contains the given AST line.
        /// Approximation: picks the latest function starting before the cursor.
        /// Works well for sequential single-file scripts (typical FFVM usage).
        /// </summary>
        private static FuncDecl FindContainingFunction(ModuleNode ast, int astLine)
        {
            FuncDecl best = null;
            foreach (var func in ast.Functions)
            {
                if (func.Line <= astLine)
                {
                    if (best == null || func.Line > best.Line)
                        best = func;
                }
            }
            return best;
        }

        /// <summary>
        /// Find the struct type name of a local variable or parameter.
        /// Returns null if not a struct or not found.
        /// </summary>
        private static string FindVariableStructType(ModuleNode ast, FuncDecl func, string varName)
        {
            // Check parameters
            foreach (var param in func.Parameters)
            {
                if (param.Name == varName)
                {
                    // Check if param type is a known struct
                    foreach (var st in ast.Structs)
                    {
                        if (st.Name == param.TypeName)
                            return param.TypeName;
                    }
                    return null;
                }
            }
            // Check local variables
            string localResult = FindVarStructTypeInBlock(ast, func.Body, varName);
            if (localResult != null) return localResult;
            // Lang-1: Check module-level variables
            foreach (var mv in ast.ModuleVariables)
            {
                if (mv.Name == varName && mv.TypeName != null)
                {
                    foreach (var st in ast.Structs)
                    {
                        if (st.Name == mv.TypeName)
                            return mv.TypeName;
                    }
                    return null;
                }
            }
            return null;
        }

        // R1: FindVarStructType — replaced hand-written Block/Stmt triad with AstWalker subclass.
        private static string FindVarStructTypeInBlock(ModuleNode ast, BlockStmt block, string varName)
        {
            var w = new FindVarStructTypeWalker(ast, varName);
            w.WalkBlock(block);
            return w.Result;
        }

        private sealed class FindVarStructTypeWalker : AstWalker
        {
            private readonly ModuleNode _ast;
            private readonly string _varName;
            public string Result;
            public FindVarStructTypeWalker(ModuleNode ast, string varName) { _ast = ast; _varName = varName; }
            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                if (vd.Name == _varName)
                {
                    if (vd.TypeName != null)
                    {
                        foreach (var st in _ast.Structs)
                        {
                            if (st.Name == vd.TypeName)
                            { Result = vd.TypeName; break; }
                        }
                    }
                    _abort = true; // found the variable (whether struct-typed or not)
                }
            }
        }

        // R1: CollectVariablesInScope — replaced hand-written Block/Stmt pair with AstWalker subclass.
        private static void CollectVariablesInScope(BlockStmt block, int beforeAstLine, List<object> items)
        {
            var w = new CollectVarsInScopeWalker(beforeAstLine, items);
            w.WalkBlock(block);
        }

        private sealed class CollectVarsInScopeWalker : AstWalker
        {
            private readonly int _beforeAstLine;
            private readonly List<object> _items;
            public CollectVarsInScopeWalker(int beforeAstLine, List<object> items) { _beforeAstLine = beforeAstLine; _items = items; }
            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                if (vd.Line < _beforeAstLine)
                {
                    string typeStr = vd.TypeName ?? "int";
                    _items.Add(MakeCompletionItem(vd.Name, 6 /* Variable */, $"var {vd.Name}: {typeStr}"));
                }
            }
        }

        // ============================================================
        // LSP4: Symbol lookup engine
        // ============================================================

        private enum SymbolKindTag { Function, Variable, Struct, Parameter, Enum, IncludeFile, StructField, EnumMember }

        private struct SymbolAtPosition
        {
            public string name;
            public SymbolKindTag kind;
            public string scopeFunc; // null for top-level symbols
            public string parentName; // for StructField → struct name, for EnumMember → enum name
            /// <summary>DX16: 1-based line of the governing VarDeclStmt for scope-isolated variable references. 0 = unresolved (module-level or parameter).</summary>
            public int declLine;
            /// <summary>DX16: 1-based column of the governing VarDeclStmt for scope-isolated variable references.</summary>
            public int declCol;
        }

        /// <summary>
        /// DX17: Unified symbol resolution result combining identity (from FindSymbolAtPosition)
        /// and definition location (from FindDefinitionLocation) into a single struct.
        /// </summary>
        private struct ResolvedSymbol
        {
            public SymbolKindTag Kind;
            public string Name;
            public string ParentName;       // struct/enum name for fields/members
            public string ScopeFunc;        // containing function (null for top-level)

            // Definition location (from FindDefinitionLocation, 1-based AST coords)
            public int DefLine, DefCol;     // 0 if definition not found
            public int NameLen;             // name token length
            public string OriginFile;       // cross-file origin (null = same file)

            // DX16: Scope isolation (from FindSymbolAtPosition)
            public int ScopeDeclLine;       // governing VarDeclStmt line (0 = no scope isolation)
            public int ScopeDeclCol;        // governing VarDeclStmt column
        }

        /// <summary>
        /// Find what symbol (function/variable/struct/parameter/include/struct field/enum member) is at the given AST position.
        /// </summary>
        private static SymbolAtPosition? FindSymbolAtPosition(ModuleNode ast, int line, int col)
        {
            // DX5: Check include declarations (cursor on file path string)
            foreach (var imp in ast.Imports)
            {
                if (imp.Line == line)
                {
                    // include "path" → the string starts after 'include "'
                    int pathStart = imp.Column + "include".Length + 2; // include + space + opening quote
                    int pathLen = imp.ModulePath.Length;
                    if (ColMatches(pathStart, pathLen, col))
                        return new SymbolAtPosition { name = imp.ModulePath, kind = SymbolKindTag.IncludeFile };
                }
            }

            // Check function names (on the FuncDecl line)
            foreach (var func in ast.Functions)
            {
                int nameStart = func.Column + "func".Length + 1;
                if (func.Line == line && ColMatches(nameStart, func.Name.Length, col))
                    return new SymbolAtPosition { name = func.Name, kind = SymbolKindTag.Function };

                // DX13: Check parameter declarations in function signature
                foreach (var p in func.Parameters)
                {
                    if (p.NameLine > 0 && p.NameLine == line && ColMatches(p.NameColumn, p.Name.Length, col))
                        return new SymbolAtPosition { name = p.Name, kind = SymbolKindTag.Parameter, scopeFunc = func.Name };
                }
            }

            // Check struct names
            foreach (var st in ast.Structs)
            {
                int nameStart = st.Column + "struct".Length + 1;
                if (st.Line == line && ColMatches(nameStart, st.Name.Length, col))
                    return new SymbolAtPosition { name = st.Name, kind = SymbolKindTag.Struct };

                // DX5: Check struct field names in struct declaration body
                foreach (var field in st.Fields)
                {
                    if (field.Line == line && ColMatches(field.Column, field.Name.Length, col))
                        return new SymbolAtPosition { name = field.Name, kind = SymbolKindTag.StructField, parentName = st.Name };
                }
            }

            // Lang-13: Check enum names
            foreach (var en in ast.Enums)
            {
                int nameStart = en.Column + "enum".Length + 1;
                if (en.Line == line && ColMatches(nameStart, en.Name.Length, col))
                    return new SymbolAtPosition { name = en.Name, kind = SymbolKindTag.Enum };

                // DX5: Check enum member names in enum declaration body
                foreach (var member in en.Members)
                {
                    if (member.Line == line && ColMatches(member.Column, member.Name.Length, col))
                        return new SymbolAtPosition { name = member.Name, kind = SymbolKindTag.EnumMember, parentName = en.Name };
                }
            }

            // B001: Check module-level variable declarations (names, type annotations, initializers)
            foreach (var mv in ast.ModuleVariables)
            {
                // Check variable name
                if (mv.NameLine > 0 && mv.NameLine == line && ColMatches(mv.NameColumn, mv.Name.Length, col))
                    return new SymbolAtPosition { name = mv.Name, kind = SymbolKindTag.Variable };

                // Check type annotation (resolve to Struct or Enum)
                if (mv.TypeNameLine > 0 && mv.TypeNameLine == line && mv.TypeName != null
                    && ColMatches(mv.TypeNameColumn, mv.TypeName.Length, col))
                {
                    string baseName = GetBaseTypeName(mv.TypeName);
                    foreach (var st in ast.Structs)
                    {
                        if (st.Name == baseName)
                            return new SymbolAtPosition { name = baseName, kind = SymbolKindTag.Struct };
                    }
                    foreach (var en in ast.Enums)
                    {
                        if (en.Name == baseName)
                            return new SymbolAtPosition { name = baseName, kind = SymbolKindTag.Enum };
                    }
                }

                // Walk initializer expression (for enum member access, struct literals, calls, etc.)
                if (mv.Initializer != null)
                {
                    var w = new FindSymbolWalker(ast, null, line, col);
                    w.WalkExpr(mv.Initializer);
                    if (w.Result != null) return w.Result;
                }
            }

            // Walk function bodies
            foreach (var func in ast.Functions)
            {
                var result = FindSymbolInBlock(ast, func, func.Body, line, col);
                if (result != null) return result;
            }

            return null;
        }

        // R1: FindSymbol — replaced hand-written Block/Stmt/Expr triad with AstWalker subclass.
        private static SymbolAtPosition? FindSymbolInBlock(ModuleNode ast, FuncDecl func, BlockStmt block, int line, int col)
        {
            var w = new FindSymbolWalker(ast, func, line, col);
            w.WalkBlock(block);
            return w.Result;
        }

        private sealed class FindSymbolWalker : AstWalker
        {
            private readonly ModuleNode _ast;
            private readonly FuncDecl _func;
            private readonly int _line;
            private readonly int _col;
            public SymbolAtPosition? Result;

            // DX16: Track the active variable declaration per variable name for scope isolation.
            // Key = variable name, Value = (declLine, declCol) of the most recent VarDeclStmt.
            private Dictionary<string, (int line, int col)> _activeDecls = new Dictionary<string, (int line, int col)>();

            public FindSymbolWalker(ModuleNode ast, FuncDecl func, int line, int col) { _ast = ast; _func = func; _line = line; _col = col; }

            private void SetResult(SymbolAtPosition r) { Result = r; _abort = true; }

            // DX16: Save/restore active declarations around block scopes
            private Dictionary<string, (int line, int col)> SaveDecls()
            {
                return new Dictionary<string, (int line, int col)>(_activeDecls);
            }
            private void RestoreDecls(Dictionary<string, (int line, int col)> saved)
            {
                _activeDecls = saved;
            }

            protected override bool VisitStmt(Stmt stmt)
            {
                // DX16: BlockStmt creates a scope boundary — save/restore active declarations
                if (stmt is BlockStmt bs)
                {
                    var saved = SaveDecls();
                    foreach (var s in bs.Statements)
                    {
                        if (_abort) break;
                        WalkStmt(s);
                    }
                    RestoreDecls(saved);
                    return true; // skip default dispatch
                }

                // DX16: ForStmt creates a scope boundary for its initializer variable
                if (stmt is ForStmt fs)
                {
                    var saved = SaveDecls();
                    WalkStmt(fs.Initializer);
                    if (!_abort) WalkExpr(fs.Condition);
                    if (!_abort) WalkExpr(fs.Increment);
                    if (!_abort) WalkStmt(fs.Body);
                    RestoreDecls(saved);
                    return true; // skip default dispatch
                }

                if (stmt is VarDeclStmt vd)
                {
                    // Walk initializer first (has precise column matching for calls, identifiers, etc.)
                    // DX16: Initializer references use the PREVIOUS active declaration
                    if (vd.Initializer != null)
                    {
                        WalkExpr(vd.Initializer);
                        if (Result != null) return true;
                    }
                    // DX16: After processing initializer, update active declaration for this variable name
                    _activeDecls[vd.Name] = (vd.Line, vd.Column);
                    // US: Check type annotation — if cursor is on the type name, resolve as Struct or Enum
                    if (vd.TypeNameLine > 0 && vd.TypeNameLine == _line && vd.TypeName != null
                        && ColMatches(vd.TypeNameColumn, vd.TypeName.Length, _col))
                    {
                        string baseName = GetBaseTypeName(vd.TypeName);
                        foreach (var st in _ast.Structs)
                        {
                            if (st.Name == baseName)
                            { SetResult(new SymbolAtPosition { name = baseName, kind = SymbolKindTag.Struct }); return true; }
                        }
                        foreach (var en in _ast.Enums)
                        {
                            if (en.Name == baseName)
                            { SetResult(new SymbolAtPosition { name = baseName, kind = SymbolKindTag.Enum }); return true; }
                        }
                    }
                    // Match the variable name itself in the declaration
                    if (vd.Line == _line)
                    {
                        int nameStart = vd.Column + (vd.IsConst ? "const".Length : "var".Length) + 1;
                        if (ColMatches(nameStart, vd.Name.Length, _col))
                        { SetResult(new SymbolAtPosition { name = vd.Name, kind = SymbolKindTag.Variable, scopeFunc = _func?.Name, declLine = vd.Line, declCol = vd.Column }); return true; }
                    }
                    return true; // handled all VarDeclStmt paths (skip default dispatch which would re-walk initializer)
                }
                return false; // let AstWalker dispatch other statement types normally
            }

            protected override bool VisitExpr(Expr expr)
            {
                if (expr is IdentifierExpr id && id.Line == _line && ColMatches(id.Column, id.Name.Length, _col))
                {
                    // US: Check if identifier is an enum name (e.g., "Color" in "Color.RED")
                    foreach (var en in _ast.Enums)
                    {
                        if (en.Name == id.Name)
                        { SetResult(new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Enum }); return true; }
                    }
                    // US: Check if identifier is a struct name (e.g., struct literal or type ref)
                    foreach (var st in _ast.Structs)
                    {
                        if (st.Name == id.Name)
                        { SetResult(new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Struct }); return true; }
                    }
                    // Determine if it's a parameter or variable
                    if (_func != null)
                    {
                        foreach (var p in _func.Parameters)
                        {
                            if (p.Name == id.Name)
                            { SetResult(new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Parameter, scopeFunc = _func.Name }); return true; }
                        }
                    }
                    // DX16: Resolve governing declaration from scope tracking
                    int dl = 0, dc = 0;
                    if (_activeDecls.TryGetValue(id.Name, out var activeDecl))
                    { dl = activeDecl.line; dc = activeDecl.col; }
                    SetResult(new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Variable, scopeFunc = _func?.Name, declLine = dl, declCol = dc });
                    return true;
                }

                if (expr is CallExpr call && call.Line == _line && ColMatches(call.Column, call.FunctionName.Length, _col))
                {
                    SetResult(new SymbolAtPosition { name = call.FunctionName, kind = SymbolKindTag.Function });
                    return true;
                }

                // B001: StructLiteralExpr — check cursor on type name (e.g., "Box4" in "Box4 { ... }")
                if (expr is StructLiteralExpr sl && sl.Line == _line && ColMatches(sl.Column, sl.TypeName.Length, _col))
                {
                    foreach (var st in _ast.Structs)
                    {
                        if (st.Name == sl.TypeName)
                        { SetResult(new SymbolAtPosition { name = sl.TypeName, kind = SymbolKindTag.Struct }); return true; }
                    }
                }

                if (expr is FieldAccessExpr fa)
                {
                    // DX7: Check if cursor is on the field name token (after '.')
                    if (fa.FieldNameLine == _line && fa.FieldNameLine > 0 && ColMatches(fa.FieldNameColumn, fa.FieldName.Length, _col))
                    {
                        // US: Check if target is an enum → return EnumMember instead of StructField
                        if (fa.Target is IdentifierExpr faId)
                        {
                            foreach (var en in _ast.Enums)
                            {
                                if (en.Name == faId.Name)
                                { SetResult(new SymbolAtPosition { name = fa.FieldName, kind = SymbolKindTag.EnumMember, parentName = en.Name }); return true; }
                            }
                        }
                        // Parent struct cannot always be resolved from AST alone (no type inference);
                        // callers handle parentName=null by searching all structs.
                        SetResult(new SymbolAtPosition { name = fa.FieldName, kind = SymbolKindTag.StructField, parentName = null });
                        return true;
                    }
                    // Continue into target (don't skip children)
                    return false;
                }

                return false; // continue walking children
            }
        }

        /// <summary>
        /// DX4-P3/DX5: Find the definition location with OriginFile for cross-file navigation.
        /// Returns (line, col, nameLen, originFile) where originFile may differ from the requesting file.
        /// Returns null if no symbol found. Coordinates are 1-based (AST convention).
        /// </summary>
        private static (int line, int col, int nameLen, string originFile)? FindDefinitionLocation(
            ModuleNode ast, string name, SymbolKindTag kind, string scopeFunc, string parentName = null, int declLine = 0, int declCol = 0)
        {
            if (kind == SymbolKindTag.Function)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == name)
                    {
                        int nameCol = func.Column + "func".Length + 1;
                        return (func.Line, nameCol, func.Name.Length, func.OriginFile);
                    }
                }
            }
            else if (kind == SymbolKindTag.Struct)
            {
                foreach (var st in ast.Structs)
                {
                    if (st.Name == name)
                    {
                        int nameCol = st.Column + "struct".Length + 1;
                        return (st.Line, nameCol, st.Name.Length, st.OriginFile);
                    }
                }
            }
            // Lang-13: enum definition
            else if (kind == SymbolKindTag.Enum)
            {
                foreach (var en in ast.Enums)
                {
                    if (en.Name == name)
                    {
                        int nameCol = en.Column + "enum".Length + 1;
                        return (en.Line, nameCol, en.Name.Length, en.OriginFile);
                    }
                }
            }
            // DX5: struct field definition
            else if (kind == SymbolKindTag.StructField && parentName != null)
            {
                foreach (var st in ast.Structs)
                {
                    if (st.Name == parentName)
                    {
                        foreach (var field in st.Fields)
                        {
                            if (field.Name == name)
                                return (field.Line, field.Column, field.Name.Length, st.OriginFile);
                        }
                    }
                }
            }
            // DX7: struct field definition when parentName is unknown (from field access expression)
            else if (kind == SymbolKindTag.StructField && parentName == null)
            {
                // Search all structs for the field name — first match wins
                foreach (var st in ast.Structs)
                {
                    foreach (var field in st.Fields)
                    {
                        if (field.Name == name)
                            return (field.Line, field.Column, field.Name.Length, st.OriginFile);
                    }
                }
            }
            // DX5: enum member definition
            else if (kind == SymbolKindTag.EnumMember && parentName != null)
            {
                foreach (var en in ast.Enums)
                {
                    if (en.Name == parentName)
                    {
                        foreach (var member in en.Members)
                        {
                            if (member.Name == name)
                                return (member.Line, member.Column, member.Name.Length, en.OriginFile);
                        }
                    }
                }
            }
            else if (kind == SymbolKindTag.Variable || kind == SymbolKindTag.Parameter)
            {
                // Find the declaration in the scope function
                foreach (var func in ast.Functions)
                {
                    if (func.Name == scopeFunc)
                    {
                        // Check parameters
                        if (kind == SymbolKindTag.Parameter)
                        {
                            foreach (var p in func.Parameters)
                            {
                                if (p.Name == name)
                                {
                                    // DX13: Use precise parameter name position (DX9 NameLine/NameColumn)
                                    if (p.NameLine > 0)
                                        return (p.NameLine, p.NameColumn, p.Name.Length, func.OriginFile);
                                    // Fallback to function declaration line if position not available
                                    return (func.Line, func.Column, func.Name.Length, func.OriginFile);
                                }
                            }
                        }

                        // DX16: If we have a precise declaration position, use it directly
                        if (declLine > 0)
                            return (declLine, declCol, name.Length, func.OriginFile);

                        // Check variable declarations in body
                        var loc = FindVarDeclLocation(func.Body, name);
                        if (loc != null) return (loc.Value.line, loc.Value.col, loc.Value.nameLen, func.OriginFile);
                    }
                }

                // B001: Fallback to module-level variable declarations
                foreach (var mv in ast.ModuleVariables)
                {
                    if (mv.Name == name)
                    {
                        int defLine = mv.NameLine > 0 ? mv.NameLine : mv.Line;
                        int defCol = mv.NameLine > 0 ? mv.NameColumn : mv.Column;
                        return (defLine, defCol, mv.Name.Length, mv.OriginFile);
                    }
                }
            }
            return null;
        }

        private static (int line, int col, int nameLen)? FindVarDeclLocation(BlockStmt block, string name)
        {
            if (block == null) return null;
            foreach (var stmt in block.Statements)
            {
                if (stmt is VarDeclStmt vd && vd.Name == name)
                    return (vd.Line, vd.Column, name.Length);
                if (stmt is BlockStmt bs) { var r = FindVarDeclLocation(bs, name); if (r != null) return r; }
                if (stmt is IfStmt ifs)
                {
                    if (ifs.ThenBranch is BlockStmt tb) { var r = FindVarDeclLocation(tb, name); if (r != null) return r; }
                    if (ifs.ElseBranch is BlockStmt eb) { var r = FindVarDeclLocation(eb, name); if (r != null) return r; }
                }
                if (stmt is WhileStmt ws && ws.Body is BlockStmt wb) { var r = FindVarDeclLocation(wb, name); if (r != null) return r; }
                if (stmt is ForStmt fs)
                {
                    if (fs.Initializer is VarDeclStmt fvd && fvd.Name == name) return (fvd.Line, fvd.Column, name.Length);
                    if (fs.Body is BlockStmt fb) { var r = FindVarDeclLocation(fb, name); if (r != null) return r; }
                }
                if (stmt is DeferStmt ds) { var r = FindVarDeclLocation(ds.Body, name); if (r != null) return r; }
                if (stmt is UsingStmt us) { var r = FindVarDeclLocation(us.Body, name); if (r != null) return r; }
            }
            return null;
        }

        /// <summary>
        /// DX18: Collect references with cross-file URI resolution via OriginFile.
        /// Uses UnifiedRefsWalker for all body/initializer traversal, eliminating per-SymbolKindTag walker dispatch.
        /// Declaration locations are collected first, then a single traversal pass collects all usage references.
        /// </summary>
        private void CollectReferencesWithOrigin(ModuleNode ast, string name, SymbolKindTag kind, string requestingUri, List<object> locations, string parentName = null, string scopeFunc = null, int declLine = 0, int declCol = 0)
        {
            // --- Phase 1: Collect declaration locations ---
            CollectDeclarationLocations(ast, name, kind, requestingUri, locations, parentName, scopeFunc);

            // --- Phase 2: Collect usage references via unified traversal ---

            // DX13: Parameter — scoped to declaring function only
            if (kind == SymbolKindTag.Parameter && scopeFunc != null)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == scopeFunc)
                    {
                        if (func.Body != null)
                        {
                            string funcUri = ResolveOriginUri(requestingUri, func.OriginFile);
                            var w = new UnifiedRefsWalker(kind, name, parentName, funcUri, locations);
                            w.WalkBlock(func.Body);
                        }
                        break;
                    }
                }
                return;
            }

            // DX16: Scope-isolated variable — scoped to declaring function with declaration tracking
            if (kind == SymbolKindTag.Variable && scopeFunc != null && declLine > 0)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == scopeFunc)
                    {
                        if (func.Body != null)
                        {
                            string funcUri = ResolveOriginUri(requestingUri, func.OriginFile);
                            var w = new UnifiedRefsWalker(kind, name, parentName, funcUri, locations, declLine, declCol);
                            w.WalkBlock(func.Body);
                        }
                        break;
                    }
                }
                return;
            }

            // DX7: StructField with unknown parentName — walk once per struct that has matching field
            if (kind == SymbolKindTag.StructField && parentName == null)
            {
                foreach (var func in ast.Functions)
                {
                    string funcUri = ResolveOriginUri(requestingUri, func.OriginFile);
                    var w = new UnifiedRefsWalker(kind, name, parentName, funcUri, locations);
                    w.WalkBlock(func.Body);
                }
                foreach (var mv in ast.ModuleVariables)
                {
                    if (mv.Initializer != null)
                    {
                        string mvUri = ResolveOriginUri(requestingUri, mv.OriginFile);
                        var w = new UnifiedRefsWalker(kind, name, parentName, mvUri, locations);
                        w.WalkExpr(mv.Initializer);
                    }
                }
                return;
            }

            // General case: walk all function bodies + module variable initializers
            foreach (var func in ast.Functions)
            {
                string funcUri = ResolveOriginUri(requestingUri, func.OriginFile);
                var w = new UnifiedRefsWalker(kind, name, parentName, funcUri, locations);
                w.WalkBlock(func.Body);
            }

            foreach (var mv in ast.ModuleVariables)
            {
                string mvUri = ResolveOriginUri(requestingUri, mv.OriginFile);
                // B001: Module variable type annotations (Struct/Enum)
                if ((kind == SymbolKindTag.Struct || kind == SymbolKindTag.Enum) && mv.TypeName != null && GetBaseTypeName(mv.TypeName) == name)
                {
                    if (mv.TypeNameLine > 0)
                        locations.Add(MakeLocation(mvUri, mv.TypeNameLine, mv.TypeNameColumn, name.Length));
                    else
                        locations.Add(MakeLocation(mvUri, mv.Line, mv.Column, name.Length));
                }
                // B001: Module variable name declaration (Variable)
                if (kind == SymbolKindTag.Variable && mv.Name == name)
                {
                    int defLine = mv.NameLine > 0 ? mv.NameLine : mv.Line;
                    int defCol = mv.NameLine > 0 ? mv.NameColumn : mv.Column;
                    locations.Add(MakeLocation(mvUri, defLine, defCol, name.Length));
                }
                // B001: Walk initializer expressions
                if (mv.Initializer != null)
                {
                    var w = new UnifiedRefsWalker(kind, name, parentName, mvUri, locations);
                    w.WalkExpr(mv.Initializer);
                }
            }
        }

        /// <summary>
        /// DX18: Collect declaration locations (function/struct/enum/field/member/parameter declarations).
        /// Separated from usage-reference collection for clarity.
        /// </summary>
        private void CollectDeclarationLocations(ModuleNode ast, string name, SymbolKindTag kind, string requestingUri, List<object> locations, string parentName, string scopeFunc)
        {
            if (kind == SymbolKindTag.Function)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == name)
                    {
                        string targetUri = ResolveOriginUri(requestingUri, func.OriginFile);
                        int nameCol = func.Column + "func".Length + 1;
                        locations.Add(MakeLocation(targetUri, func.Line, nameCol, name.Length));
                    }
                }
            }
            else if (kind == SymbolKindTag.Struct)
            {
                foreach (var st in ast.Structs)
                {
                    if (st.Name == name)
                    {
                        string targetUri = ResolveOriginUri(requestingUri, st.OriginFile);
                        int nameCol = st.Column + "struct".Length + 1;
                        locations.Add(MakeLocation(targetUri, st.Line, nameCol, name.Length));
                    }
                }
            }
            else if (kind == SymbolKindTag.Enum)
            {
                foreach (var en in ast.Enums)
                {
                    if (en.Name == name)
                    {
                        string targetUri = ResolveOriginUri(requestingUri, en.OriginFile);
                        int nameCol = en.Column + "enum".Length + 1;
                        locations.Add(MakeLocation(targetUri, en.Line, nameCol, name.Length));
                    }
                }
            }
            else if (kind == SymbolKindTag.StructField)
            {
                if (parentName != null)
                {
                    foreach (var st in ast.Structs)
                    {
                        if (st.Name == parentName)
                        {
                            string targetUri = ResolveOriginUri(requestingUri, st.OriginFile);
                            foreach (var field in st.Fields)
                            {
                                if (field.Name == name)
                                    locations.Add(MakeLocation(targetUri, field.Line, field.Column, name.Length));
                            }
                        }
                    }
                }
                else
                {
                    // DX7: Unknown parent — search all structs
                    foreach (var st in ast.Structs)
                    {
                        string targetUri = ResolveOriginUri(requestingUri, st.OriginFile);
                        foreach (var field in st.Fields)
                        {
                            if (field.Name == name)
                                locations.Add(MakeLocation(targetUri, field.Line, field.Column, name.Length));
                        }
                    }
                }
            }
            else if (kind == SymbolKindTag.EnumMember && parentName != null)
            {
                foreach (var en in ast.Enums)
                {
                    if (en.Name == parentName)
                    {
                        string targetUri = ResolveOriginUri(requestingUri, en.OriginFile);
                        foreach (var member in en.Members)
                        {
                            if (member.Name == name)
                                locations.Add(MakeLocation(targetUri, member.Line, member.Column, name.Length));
                        }
                    }
                }
            }
            else if (kind == SymbolKindTag.Parameter && scopeFunc != null)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == scopeFunc)
                    {
                        string funcUri = ResolveOriginUri(requestingUri, func.OriginFile);
                        foreach (var p in func.Parameters)
                        {
                            if (p.Name == name && p.NameLine > 0)
                            {
                                locations.Add(MakeLocation(funcUri, p.NameLine, p.NameColumn, name.Length));
                                break;
                            }
                        }
                        break;
                    }
                }
            }
            // Variable declarations are handled inline in CollectReferencesWithOrigin (module vars)
            // or by ScopedIdentRefsWalker (function-local vars via VisitVarDecl/VisitStmt).
        }

        // ============================================================
        // DX18: UnifiedRefsWalker — single walker for all reference forms.
        // Replaces CallRefsWalker, IdentRefsWalker, ScopedIdentRefsWalker,
        // TypeRefsWalker, StructLiteralTypeRefsWalker, EnumIdentRefsWalker,
        // FieldAccessRefsWalker, EnumMemberAccessRefsWalker.
        // ============================================================

        /// <summary>
        /// DX18: Unified reference walker. Matches all reference patterns based on the target's SymbolKindTag.
        /// For scope-isolated variables (declLine > 0), includes scope tracking via VisitStmt.
        /// </summary>
        private sealed class UnifiedRefsWalker : AstWalker
        {
            private readonly SymbolKindTag _kind;
            private readonly string _name;
            private readonly string _parentName;
            private readonly string _uri;
            private readonly List<object> _locations;

            // DX16 scope-isolation fields (active only when _scopeIsolated == true)
            private readonly bool _scopeIsolated;
            private readonly int _targetDeclLine;
            private readonly int _targetDeclCol;
            private Dictionary<string, (int line, int col)> _activeDecls;

            public UnifiedRefsWalker(SymbolKindTag kind, string name, string parentName, string uri, List<object> locations, int declLine = 0, int declCol = 0)
            {
                _kind = kind;
                _name = name;
                _parentName = parentName;
                _uri = uri;
                _locations = locations;
                _scopeIsolated = kind == SymbolKindTag.Variable && declLine > 0;
                _targetDeclLine = declLine;
                _targetDeclCol = declCol;
                if (_scopeIsolated)
                    _activeDecls = new Dictionary<string, (int line, int col)>();
            }

            // --- Scope-isolation helpers (DX16) ---

            private bool IsTargetActive
            {
                get
                {
                    if (_activeDecls != null && _activeDecls.TryGetValue(_name, out var d))
                        return d.line == _targetDeclLine && d.col == _targetDeclCol;
                    return false;
                }
            }

            private Dictionary<string, (int line, int col)> SaveDecls()
            {
                return new Dictionary<string, (int line, int col)>(_activeDecls);
            }

            private void RestoreDecls(Dictionary<string, (int line, int col)> saved)
            {
                _activeDecls = saved;
            }

            // --- VisitStmt: scope tracking for scope-isolated variables ---

            protected override bool VisitStmt(Stmt stmt)
            {
                if (!_scopeIsolated) return false; // default dispatch for non-scoped modes

                if (stmt is BlockStmt bs)
                {
                    var saved = SaveDecls();
                    foreach (var s in bs.Statements)
                    {
                        if (_abort) break;
                        WalkStmt(s);
                    }
                    RestoreDecls(saved);
                    return true;
                }

                if (stmt is ForStmt fs)
                {
                    var saved = SaveDecls();
                    WalkStmt(fs.Initializer);
                    if (!_abort) WalkExpr(fs.Condition);
                    if (!_abort) WalkExpr(fs.Increment);
                    if (!_abort) WalkStmt(fs.Body);
                    RestoreDecls(saved);
                    return true;
                }

                if (stmt is VarDeclStmt vd)
                {
                    if (vd.Name == _name)
                    {
                        if (vd.Initializer != null)
                            WalkExpr(vd.Initializer);
                        _activeDecls[vd.Name] = (vd.Line, vd.Column);
                        if (IsTargetActive)
                            _locations.Add(MakeLocation(_uri, vd.Line, vd.Column, _name.Length));
                    }
                    else
                    {
                        if (IsTargetActive && vd.Initializer != null)
                            WalkExpr(vd.Initializer);
                    }
                    return true;
                }

                return false;
            }

            // --- VisitVarDecl: type annotation + variable name matching ---

            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                // Type annotations (Struct/Enum references)
                if ((_kind == SymbolKindTag.Struct || _kind == SymbolKindTag.Enum) && vd.TypeName != null)
                {
                    string baseName = GetBaseTypeName(vd.TypeName);
                    if (baseName == _name)
                    {
                        if (vd.TypeNameLine > 0)
                            _locations.Add(MakeLocation(_uri, vd.TypeNameLine, vd.TypeNameColumn, _name.Length));
                        else
                            _locations.Add(MakeLocation(_uri, vd.Line, vd.Column, _name.Length));
                    }
                }

                // Variable/Parameter name declarations (unscoped only; scoped handled by VisitStmt)
                if ((_kind == SymbolKindTag.Variable || _kind == SymbolKindTag.Parameter) && !_scopeIsolated && vd.Name == _name)
                    _locations.Add(MakeLocation(_uri, vd.Line, vd.Column, _name.Length));
            }

            // --- VisitExpr: all expression-level reference patterns ---

            protected override bool VisitExpr(Expr expr)
            {
                switch (_kind)
                {
                    case SymbolKindTag.Function:
                        // CallExpr: function call site
                        if (expr is CallExpr call && call.FunctionName == _name)
                            _locations.Add(MakeLocation(_uri, call.Line, call.Column, _name.Length));
                        break;

                    case SymbolKindTag.Variable:
                    case SymbolKindTag.Parameter:
                        // IdentifierExpr: variable/parameter usage
                        if (expr is IdentifierExpr vid && vid.Name == _name)
                        {
                            if (!_scopeIsolated || IsTargetActive)
                                _locations.Add(MakeLocation(_uri, vid.Line, vid.Column, _name.Length));
                        }
                        break;

                    case SymbolKindTag.Struct:
                        // StructLiteralExpr: struct literal type name (e.g., "Vec2 { ... }")
                        if (expr is StructLiteralExpr slStruct && slStruct.TypeName == _name)
                            _locations.Add(MakeLocation(_uri, slStruct.Line, slStruct.Column, _name.Length));
                        break;

                    case SymbolKindTag.Enum:
                        // FieldAccessExpr: enum identifier in EnumName.MEMBER pattern
                        if (expr is FieldAccessExpr faEnum && faEnum.Target is IdentifierExpr eid && eid.Name == _name)
                            _locations.Add(MakeLocation(_uri, eid.Line, eid.Column, _name.Length));
                        break;

                    case SymbolKindTag.StructField:
                        // FieldAccessExpr: field access (e.g., "obj.fieldName")
                        if (expr is FieldAccessExpr faField)
                        {
                            if (faField.FieldName == _name && faField.FieldNameLine > 0)
                                _locations.Add(MakeLocation(_uri, faField.FieldNameLine, faField.FieldNameColumn, _name.Length));
                        }
                        // StructLiteralExpr: field in struct literal (e.g., "Foo { fieldName: ... }")
                        else if (expr is StructLiteralExpr slField)
                        {
                            foreach (var f in slField.Fields)
                            {
                                if (f.FieldName == _name)
                                    _locations.Add(MakeLocation(_uri, slField.Line, slField.Column, _name.Length));
                            }
                        }
                        break;

                    case SymbolKindTag.EnumMember:
                        // FieldAccessExpr: enum member access (e.g., "Color.RED" — the RED part)
                        if (expr is FieldAccessExpr faMember)
                        {
                            if (faMember.FieldName == _name && faMember.FieldNameLine > 0
                                && _parentName != null && faMember.Target is IdentifierExpr mid && mid.Name == _parentName)
                            {
                                _locations.Add(MakeLocation(_uri, faMember.FieldNameLine, faMember.FieldNameColumn, _name.Length));
                            }
                        }
                        break;
                }
                return false; // continue walking children
            }
        }

        // ============================================================
        // DX5: Include file helpers
        // ============================================================

        /// <summary>
        /// DX5: Resolve an include path to a file:// URI for navigation.
        /// </summary>
        private string ResolveIncludeFileUri(string requestingUri, string includePath)
        {
            if (includePath == null) return null;

            // Try with .ffs extension first, then without
            string[] candidates = includePath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase)
                ? new[] { includePath }
                : new[] { includePath + ".ffs", includePath };

            if (_rootPath != null)
            {
                foreach (var candidate in candidates)
                {
                    string absPath = Path.GetFullPath(Path.Combine(_rootPath, candidate));
                    if (File.Exists(absPath))
                        return PathToFileUri(absPath);
                }
                // Try via project file resolver paths
                if (_projectFile != null)
                {
                    foreach (var incPath in _projectFile.IncludePaths)
                    {
                        string basePath = Path.IsPathRooted(incPath) ? incPath : Path.Combine(_rootPath, incPath);
                        foreach (var candidate in candidates)
                        {
                            string absPath = Path.GetFullPath(Path.Combine(basePath, candidate));
                            if (File.Exists(absPath))
                                return PathToFileUri(absPath);
                        }
                    }
                }
            }

            // Fallback: try FilePathToUri
            string uri = FilePathToUri(includePath, _rootPath);
            return uri;
        }

        /// <summary>
        /// DX5: Collect all include declarations referencing the same module path.
        /// </summary>
        private static void CollectIncludeReferences(ModuleNode ast, string modulePath, string uri, List<object> locations)
        {
            foreach (var imp in ast.Imports)
            {
                if (imp.ModulePath == modulePath)
                {
                    int pathStart = imp.Column + "include".Length + 2; // 'include "' prefix
                    locations.Add(MakeLocation(uri, imp.Line, pathStart, modulePath.Length));
                }
            }
        }

        // ============================================================
        // DX6: File rename — workspace/willRenameFiles
        // ============================================================

        /// <summary>
        /// DX6: Handle workspace/willRenameFiles request.
        /// When a .ffs file is renamed, scan all workspace files for include directives
        /// referencing the old path and return a WorkspaceEdit to update them.
        /// Covers: plain include, include-as alias, cross-includePaths resolution.
        /// </summary>
        private JsonObject HandleWillRenameFiles(JsonObject parameters)
        {
            if (parameters == null || _rootPath == null) return new JsonObject();

            var files = parameters.GetArray("files");
            if (files == null || files.Count == 0) return new JsonObject();

            // Collect all text edits grouped by file URI
            var editsByUri = new Dictionary<string, List<object>>();

            // DX11: Track file renames for post-response state update
            var fileRenames = new List<(string oldUri, string newUri)>();

            foreach (var item in files)
            {
                var fileRename = item as JsonObject;
                if (fileRename == null) continue;

                string oldUri = fileRename.GetString("oldUri");
                string newUri = fileRename.GetString("newUri");
                if (oldUri == null || newUri == null) continue;

                fileRenames.Add((oldUri, newUri));

                // Convert URIs to workspace-relative include paths (without .ffs extension)
                string oldAbsPath = UriToPath(oldUri);
                string newAbsPath = UriToPath(newUri);
                if (oldAbsPath == null || newAbsPath == null) continue;

                // Determine which include paths could reference this file
                var oldIncludePaths = ResolveFileToIncludePaths(oldAbsPath);
                var newIncludePaths = ResolveFileToIncludePaths(newAbsPath);
                if (oldIncludePaths.Count == 0) continue;

                // Scan all .ffs files in workspace for imports matching old paths
                ScanWorkspaceForRenames(oldIncludePaths, newIncludePaths, editsByUri);
            }

            // Build WorkspaceEdit response
            if (editsByUri.Count == 0 && fileRenames.Count == 0) return new JsonObject();

            JsonObject workspaceEdit;
            if (editsByUri.Count > 0)
            {
                var changes = new JsonObject();
                foreach (var kvp in editsByUri)
                    changes.Set(kvp.Key, kvp.Value);

                workspaceEdit = new JsonObject();
                workspaceEdit.Set("changes", changes);
            }
            else
            {
                workspaceEdit = new JsonObject();
            }

            // DX11: Pre-apply edits + rename state so subsequent operations see updated content.
            // VSCode will apply the WorkspaceEdit and may send didChange later, but we need
            // consistency NOW for the next willRenameFiles call.
            ApplyRenameState(editsByUri, fileRenames);

            return workspaceEdit;
        }

        /// <summary>
        /// DX11: Pre-apply WorkspaceEdit results to DocumentStore so that subsequent
        /// willRenameFiles requests (consecutive rename scenario) see updated content.
        /// Steps: 1) Apply text edits to open documents, 2) Migrate URI keys for renamed files,
        /// 3) Recompile affected files to update AST + dependency graph.
        /// </summary>
        private void ApplyRenameState(Dictionary<string, List<object>> editsByUri, List<(string oldUri, string newUri)> fileRenames)
        {
            // Step 1: Apply text edits to documents that are open in the editor.
            // For documents not in _docStore (not opened), the edits will be applied
            // by VSCode and reflected when the file is opened or via didChangeWatchedFiles.
            foreach (var kvp in editsByUri)
            {
                string uri = kvp.Key;
                if (!_docStore.HasContent(uri)) continue;

                var edits = new List<JsonObject>();
                foreach (var edit in kvp.Value)
                {
                    var jsonEdit = edit as JsonObject;
                    if (jsonEdit != null)
                        edits.Add(jsonEdit);
                }
                _docStore.ApplyTextEdits(uri, edits);
            }

            // Step 2: Migrate URI keys for renamed files.
            foreach (var (oldUri, newUri) in fileRenames)
            {
                if (_docStore.HasContent(oldUri))
                    _docStore.RenameUri(oldUri, newUri);
            }

            // Step 3: Recompile documents that had text edits applied, to refresh AST + dependency graph.
            foreach (var kvp in editsByUri)
            {
                string uri = kvp.Key;
                // If this URI was renamed, use the new URI
                string effectiveUri = uri;
                foreach (var (oldUri, newUri) in fileRenames)
                {
                    if (uri == oldUri) { effectiveUri = newUri; break; }
                }

                string content;
                if (_docStore.TryGetContent(effectiveUri, out content))
                    CompileAndPublishDiagnostics(effectiveUri, content);
            }
        }

        /// <summary>
        /// DX6: Resolve an absolute file path to all possible include path strings.
        /// Returns a list of (includePath, basePath) tuples where includePath is what appears
        /// in source code (e.g. "utils" or "lib/helper") and basePath is the base directory
        /// used for resolution.
        /// </summary>
        private List<(string includePath, string basePath)> ResolveFileToIncludePaths(string absPath)
        {
            var result = new List<(string, string)>();
            if (absPath == null) return result;

            absPath = Path.GetFullPath(absPath).Replace('\\', '/');

            // Collect all base directories to check: workspace root + project includePaths
            var baseDirs = new List<string>();
            if (_rootPath != null)
                baseDirs.Add(Path.GetFullPath(_rootPath).Replace('\\', '/'));

            if (_projectFile != null)
            {
                foreach (var incPath in _projectFile.IncludePaths)
                {
                    string basePath = Path.IsPathRooted(incPath)
                        ? incPath
                        : Path.Combine(_rootPath, incPath);
                    basePath = Path.GetFullPath(basePath).Replace('\\', '/');
                    if (!baseDirs.Contains(basePath))
                        baseDirs.Add(basePath);
                }
            }

            foreach (var baseDir in baseDirs)
            {
                string normalizedBase = baseDir.EndsWith("/") ? baseDir : baseDir + "/";
                if (absPath.StartsWith(normalizedBase, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = absPath.Substring(normalizedBase.Length);
                    // Strip .ffs extension if present (include paths typically omit it)
                    string withoutExt = relative;
                    if (withoutExt.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
                        withoutExt = withoutExt.Substring(0, withoutExt.Length - 4);
                    result.Add((withoutExt, baseDir));
                    // Also add with .ffs extension (some users include "utils.ffs" explicitly)
                    if (relative != withoutExt)
                        result.Add((relative, baseDir));
                }
            }

            return result;
        }

        /// <summary>
        /// DX6: Scan all .ffs files in workspace for include directives matching old paths
        /// and build text edits to replace with new paths.
        /// </summary>
        private void ScanWorkspaceForRenames(
            List<(string includePath, string basePath)> oldPaths,
            List<(string includePath, string basePath)> newPaths,
            Dictionary<string, List<object>> editsByUri)
        {
            if (_rootPath == null || oldPaths.Count == 0 || newPaths.Count == 0) return;

            // Build lookup: old include path → new include path
            // Match by base directory: for each oldPath entry, find newPath entry with same basePath
            var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var oldEntry in oldPaths)
            {
                foreach (var newEntry in newPaths)
                {
                    if (string.Equals(oldEntry.basePath, newEntry.basePath, StringComparison.OrdinalIgnoreCase))
                    {
                        // Preserve extension convention: if old had .ffs, new should too
                        if (oldEntry.includePath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase)
                            == newEntry.includePath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
                        {
                            renameMap[oldEntry.includePath] = newEntry.includePath;
                        }
                    }
                }
            }

            if (renameMap.Count == 0) return;

            // Scan workspace .ffs files
            string[] ffsFiles;
            try
            {
                ffsFiles = Directory.GetFiles(_rootPath, "*.ffs", SearchOption.AllDirectories);
            }
            catch
            {
                return; // Can't scan workspace
            }

            var parser = new Parser();
            foreach (var filePath in ffsFiles)
            {
                string source;
                try { source = File.ReadAllText(filePath); }
                catch { continue; }

                // Check if this file is open in the editor (use cached content)
                string fileUri = PathToFileUri(filePath);
                string cached;
                if (_docStore.TryGetContent(fileUri, out cached))
                    source = cached;

                var ast = parser.Parse(source, out var errors);
                if (ast == null) continue;

                foreach (var imp in ast.Imports)
                {
                    if (renameMap.TryGetValue(imp.ModulePath, out string newPath))
                    {
                        // Calculate position of the include path string (inside quotes)
                        // include "path" or include "path" as Alias
                        // AST Line/Column are 1-based; convert to 0-based for LSP
                        int lspLine = imp.Line - 1;
                        int pathStart = imp.Column - 1 + "include".Length + 2; // 'include "' = keyword + space + quote
                        int pathLen = imp.ModulePath.Length;

                        var edit = MakeTextEdit(lspLine, pathStart, lspLine, pathStart + pathLen, newPath);

                        if (!editsByUri.ContainsKey(fileUri))
                            editsByUri[fileUri] = new List<object>();
                        editsByUri[fileUri].Add(edit);
                    }
                }
            }
        }

        /// <summary>
        /// DX6: Create an LSP TextEdit object.
        /// </summary>
        private static JsonObject MakeTextEdit(int startLine, int startChar, int endLine, int endChar, string newText)
        {
            var range = new JsonObject();
            var start = new JsonObject();
            start.Set("line", startLine);
            start.Set("character", startChar);
            range.Set("start", start);
            var end = new JsonObject();
            end.Set("line", endLine);
            end.Set("character", endChar);
            range.Set("end", end);

            var textEdit = new JsonObject();
            textEdit.Set("range", range);
            textEdit.Set("newText", newText);
            return textEdit;
        }

        /// <summary>
        /// DX5: Prepare rename — validate that the position is on a renamable symbol
        /// and return the range + placeholder text.
        /// </summary>
        private JsonObject HandlePrepareRename(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var position = parameters?.GetObject("position");
            if (uri == null || position == null) return null;

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            // DX17: Unified symbol resolution
            var (symbol, mergedAst) = ResolveSymbol(uri, astLine, astCol);

            // Include files are not renamable via text rename (file system operation)
            if (symbol == null || symbol.Value.Kind == SymbolKindTag.IncludeFile) return null;

            // Return range at cursor position with the symbol name as placeholder
            int lspLine = position.GetInt("line");

            // Calculate the actual start of the symbol name at cursor
            int nameLen = symbol.Value.Name.Length;
            var perFileAst = GetCachedAst(uri);
            int nameStartCol = FindNameStartCol(perFileAst, symbol.Value, astLine, astCol);
            int lspStartChar = Math.Max(0, nameStartCol - 1);

            var result = new JsonObject();
            result.Set("range", MakeRange(lspLine, lspStartChar, lspLine, lspStartChar + nameLen));
            result.Set("placeholder", symbol.Value.Name);
            return result;
        }

        /// <summary>
        /// DX5: Find the 1-based start column of a symbol name at the given position.
        /// DX17: Updated to accept ResolvedSymbol instead of SymbolAtPosition.
        /// </summary>
        private static int FindNameStartCol(ModuleNode ast, ResolvedSymbol target, int line, int col)
        {
            if (target.Kind == SymbolKindTag.Function)
            {
                foreach (var func in ast.Functions)
                {
                    int nameStart = func.Column + "func".Length + 1;
                    if (func.Line == line && func.Name == target.Name && ColMatches(nameStart, func.Name.Length, col))
                        return nameStart;
                }
                // Could be a call site — check expressions
            }
            else if (target.Kind == SymbolKindTag.Struct)
            {
                foreach (var st in ast.Structs)
                {
                    int nameStart = st.Column + "struct".Length + 1;
                    if (st.Line == line && st.Name == target.Name && ColMatches(nameStart, st.Name.Length, col))
                        return nameStart;
                }
            }
            else if (target.Kind == SymbolKindTag.Enum)
            {
                foreach (var en in ast.Enums)
                {
                    int nameStart = en.Column + "enum".Length + 1;
                    if (en.Line == line && en.Name == target.Name && ColMatches(nameStart, en.Name.Length, col))
                        return nameStart;
                }
            }
            else if (target.Kind == SymbolKindTag.StructField)
            {
                foreach (var st in ast.Structs)
                    foreach (var f in st.Fields)
                        if (f.Line == line && f.Name == target.Name && ColMatches(f.Column, f.Name.Length, col))
                            return f.Column;
            }
            else if (target.Kind == SymbolKindTag.EnumMember)
            {
                foreach (var en in ast.Enums)
                    foreach (var m in en.Members)
                        if (m.Line == line && m.Name == target.Name && ColMatches(m.Column, m.Name.Length, col))
                            return m.Column;
            }
            // Fallback: col points somewhere in the name, back up to find start
            return Math.Max(1, col - target.Name.Length + 1);
        }

        /// <summary>
        /// DX5: Handle rename — find all references and generate text edits.
        /// </summary>
        private JsonObject HandleRename(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var position = parameters?.GetObject("position");
            if (uri == null || position == null) return null;

            string newName = parameters.GetString("newName");
            if (string.IsNullOrEmpty(newName)) return null;

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            // DX17: Unified symbol resolution
            var (symbol, mergedAst) = ResolveSymbol(uri, astLine, astCol);

            // Include files are not renamable
            if (symbol == null || symbol.Value.Kind == SymbolKindTag.IncludeFile) return null;

            // Collect all reference locations
            var locations = new List<object>();
            CollectReferencesWithOrigin(mergedAst, symbol.Value.Name, symbol.Value.Kind, uri, locations, symbol.Value.ParentName, symbol.Value.ScopeFunc, symbol.Value.ScopeDeclLine, symbol.Value.ScopeDeclCol);

            // Group locations by URI → text edits
            var editsByUri = new Dictionary<string, List<object>>();
            foreach (var locObj in locations)
            {
                var loc = locObj as JsonObject;
                if (loc == null) continue;
                string locUri = loc.GetString("uri") ?? uri;
                var range = loc.GetObject("range");
                if (range == null) continue;

                if (!editsByUri.TryGetValue(locUri, out var edits))
                {
                    edits = new List<object>();
                    editsByUri[locUri] = edits;
                }
                var textEdit = new JsonObject();
                textEdit.Set("range", range);
                textEdit.Set("newText", newName);
                edits.Add(textEdit);
            }

            // Build WorkspaceEdit
            var changes = new JsonObject();
            foreach (var kv in editsByUri)
                changes.Set(kv.Key, kv.Value);

            var workspaceEdit = new JsonObject();
            workspaceEdit.Set("changes", changes);
            return workspaceEdit;
        }

        // ============================================================
        // DX5: Semantic tokens
        // ============================================================

        /// <summary>
        /// DX5: Produce semantic tokens for struct/enum type coloring.
        /// Token types legend: type(0), struct(1), enum(2), enumMember(3), property(4),
        ///                     variable(5), function(6), parameter(7), string(8)
        /// Each token is encoded as 5 ints: deltaLine, deltaStartChar, length, tokenType, tokenModifiers.
        /// </summary>
        private JsonObject HandleSemanticTokensFull(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            if (ast == null)
            {
                var empty = new JsonObject();
                empty.Set("data", new List<object>());
                return empty;
            }

            // Build sets of known names for lookup
            var mergedAst = GetMergedAst(uri);
            var structNames = new HashSet<string>();
            var enumNames = new HashSet<string>();
            var funcNames = new HashSet<string>();
            if (mergedAst != null)
            {
                foreach (var st in mergedAst.Structs) structNames.Add(st.Name);
                foreach (var en in mergedAst.Enums) enumNames.Add(en.Name);
                foreach (var fn in mergedAst.Functions) funcNames.Add(fn.Name);
            }

            // Collect semantic tokens as (line, col, length, tokenType, modifiers)
            var rawTokens = new List<(int line, int col, int len, int type, int mod)>();

            // 1. Struct declarations (name token)
            foreach (var st in ast.Structs)
            {
                int nameStart = st.Column + "struct".Length + 1;
                rawTokens.Add((st.Line, nameStart, st.Name.Length, 1 /* struct */, 1 /* declaration */));
                // Struct field declarations — property tokens
                foreach (var field in st.Fields)
                {
                    rawTokens.Add((field.Line, field.Column, field.Name.Length, 4 /* property */, 1 /* declaration */));
                    // DX7: struct field type annotation — semantic token for struct/enum types
                    if (field.TypeNameLine > 0)
                    {
                        string baseType = GetBaseTypeName(field.TypeName);
                        if (structNames.Contains(baseType))
                            rawTokens.Add((field.TypeNameLine, field.TypeNameColumn, baseType.Length, 1 /* struct */, 0));
                        else if (enumNames.Contains(baseType))
                            rawTokens.Add((field.TypeNameLine, field.TypeNameColumn, baseType.Length, 2 /* enum */, 0));
                    }
                }
            }

            // 2. Enum declarations (name token + member tokens)
            foreach (var en in ast.Enums)
            {
                int nameStart = en.Column + "enum".Length + 1;
                rawTokens.Add((en.Line, nameStart, en.Name.Length, 2 /* enum */, 1 /* declaration */));
                foreach (var member in en.Members)
                {
                    rawTokens.Add((member.Line, member.Column, member.Name.Length, 3 /* enumMember */, 1 /* declaration */));
                }
            }

            // 3. Function declarations: external keyword, parameter names + type annotations, local variable tokens
            foreach (var func in ast.Functions)
            {
                // DX9: 'external' keyword token
                if (func.IsExternal && func.ExternalLine > 0)
                    rawTokens.Add((func.ExternalLine, func.ExternalColumn, "external".Length, 9 /* keyword */, 0));

                // DX9: parameter name tokens
                foreach (var param in func.Parameters)
                {
                    if (param.NameLine > 0)
                        rawTokens.Add((param.NameLine, param.NameColumn, param.Name.Length, 7 /* parameter */, 1 /* declaration */));

                    // DX7: parameter type annotations
                    if (param.TypeNameLine > 0)
                    {
                        string baseType = GetBaseTypeName(param.TypeName);
                        if (structNames.Contains(baseType))
                            rawTokens.Add((param.TypeNameLine, param.TypeNameColumn, baseType.Length, 1 /* struct */, 0));
                        else if (enumNames.Contains(baseType))
                            rawTokens.Add((param.TypeNameLine, param.TypeNameColumn, baseType.Length, 2 /* enum */, 0));
                    }
                }
                // DX7: local variable type annotations + DX9: local variable name tokens (skip for external funcs — no body)
                if (func.Body != null)
                {
                    CollectTypeUsageTokens(func.Body, structNames, enumNames, rawTokens);
                    CollectVarNameTokens(func.Body, rawTokens);
                }
            }

            // DX7: module-level variable type annotations + DX9: module-level variable name tokens
            foreach (var mv in ast.ModuleVariables)
            {
                // DX9: variable name token
                if (mv.NameLine > 0)
                    rawTokens.Add((mv.NameLine, mv.NameColumn, mv.Name.Length, 5 /* variable */, 1 /* declaration */));

                if (mv.TypeNameLine > 0)
                {
                    string baseType = GetBaseTypeName(mv.TypeName);
                    if (structNames.Contains(baseType))
                        rawTokens.Add((mv.TypeNameLine, mv.TypeNameColumn, baseType.Length, 1 /* struct */, 0));
                    else if (enumNames.Contains(baseType))
                        rawTokens.Add((mv.TypeNameLine, mv.TypeNameColumn, baseType.Length, 2 /* enum */, 0));
                }
            }

            // DX8: Expression-based semantic tokens for enum references and field access.
            // Build lookup: enum members (EnumName.MEMBER) and struct field names.
            var enumMemberNames = new HashSet<string>(); // "EnumName.Member"
            if (mergedAst != null)
            {
                foreach (var en in mergedAst.Enums)
                    foreach (var m in en.Members)
                        enumMemberNames.Add(en.Name + "." + m.Name);
            }

            // 4. Walk expressions in function bodies for enum references, field access, and variable references
            foreach (var func in ast.Functions)
            {
                if (func.Body != null)
                    CollectExprSemanticTokens(func.Body, enumNames, enumMemberNames, structNames, funcNames, rawTokens);
            }

            // 5. Walk module-level variable initializers for enum references and variable references
            foreach (var mv in ast.ModuleVariables)
            {
                if (mv.Initializer != null)
                {
                    var w = new ExprSemanticTokensWalker(enumNames, enumMemberNames, structNames, funcNames, rawTokens);
                    w.WalkExpr(mv.Initializer);
                }
            }

            // Sort by (line, col) for delta encoding
            rawTokens.Sort((a, b) =>
            {
                int cmp = a.line.CompareTo(b.line);
                return cmp != 0 ? cmp : a.col.CompareTo(b.col);
            });

            // Delta-encode: convert absolute (line, col) to (deltaLine, deltaStartChar)
            var data = new List<object>();
            int prevLine = 0;
            int prevCol = 0;
            foreach (var tok in rawTokens)
            {
                int lspLine = tok.line - 1; // AST is 1-based, LSP is 0-based
                int lspCol = tok.col - 1;
                int deltaLine = lspLine - prevLine;
                int deltaCol = deltaLine == 0 ? lspCol - prevCol : lspCol;
                data.Add(deltaLine);
                data.Add(deltaCol);
                data.Add(tok.len);
                data.Add(tok.type);
                data.Add(tok.mod);
                prevLine = lspLine;
                prevCol = lspCol;
            }

            var result = new JsonObject();
            result.Set("data", data);
            return result;
        }

        /// <summary>
        /// DX7: Walk a block to find type annotations in local variable declarations
        /// that reference struct/enum types, and emit semantic tokens for them.
        /// </summary>

        /// <summary>
        /// DX7: Extract the base type name from a potentially dotted type (e.g. "Alias.Struct" → "Alias").
        /// For simple types, returns the type name unchanged.
        /// </summary>
        private static string GetBaseTypeName(string typeName)
        {
            int dotIndex = typeName.IndexOf('.');
            return dotIndex >= 0 ? typeName.Substring(0, dotIndex) : typeName;
        }

        // R1: TypeUsageTokens — replaced hand-written Block/Stmt triad with AstWalker subclass.
        private static void CollectTypeUsageTokens(BlockStmt block, HashSet<string> structNames, HashSet<string> enumNames,
            List<(int line, int col, int len, int type, int mod)> tokens)
        {
            var w = new TypeUsageTokensWalker(structNames, enumNames, tokens);
            w.WalkBlock(block);
        }

        private sealed class TypeUsageTokensWalker : AstWalker
        {
            private readonly HashSet<string> _structNames;
            private readonly HashSet<string> _enumNames;
            private readonly List<(int line, int col, int len, int type, int mod)> _tokens;
            public TypeUsageTokensWalker(HashSet<string> structNames, HashSet<string> enumNames, List<(int line, int col, int len, int type, int mod)> tokens) { _structNames = structNames; _enumNames = enumNames; _tokens = tokens; }
            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                if (vd.TypeNameLine > 0)
                {
                    string baseType = GetBaseTypeName(vd.TypeName);
                    if (_structNames.Contains(baseType))
                        _tokens.Add((vd.TypeNameLine, vd.TypeNameColumn, baseType.Length, 1 /* struct */, 0));
                    else if (_enumNames.Contains(baseType))
                        _tokens.Add((vd.TypeNameLine, vd.TypeNameColumn, baseType.Length, 2 /* enum */, 0));
                }
            }
        }

        // R1: VarNameTokens — replaced hand-written Block/Stmt triad with AstWalker subclass.
        private static void CollectVarNameTokens(BlockStmt block,
            List<(int line, int col, int len, int type, int mod)> tokens)
        {
            var w = new VarNameTokensWalker(tokens);
            w.WalkBlock(block);
        }

        private sealed class VarNameTokensWalker : AstWalker
        {
            private readonly List<(int line, int col, int len, int type, int mod)> _tokens;
            public VarNameTokensWalker(List<(int line, int col, int len, int type, int mod)> tokens) { _tokens = tokens; }
            protected override void VisitVarDecl(VarDeclStmt vd)
            {
                if (vd.NameLine > 0)
                    _tokens.Add((vd.NameLine, vd.NameColumn, vd.Name.Length, 5 /* variable */, 1 /* declaration */));
            }
        }

        // ============================================================
        // DX8: Expression-based semantic tokens
        // ============================================================

        // R1: ExprSemanticTokens — replaced hand-written Block/Stmt/Expr triad with AstWalker subclass.
        private static void CollectExprSemanticTokens(BlockStmt block,
            HashSet<string> enumNames, HashSet<string> enumMemberNames,
            HashSet<string> structNames, HashSet<string> funcNames,
            List<(int line, int col, int len, int type, int mod)> tokens)
        {
            var w = new ExprSemanticTokensWalker(enumNames, enumMemberNames, structNames, funcNames, tokens);
            w.WalkBlock(block);
        }

        private sealed class ExprSemanticTokensWalker : AstWalker
        {
            private readonly HashSet<string> _enumNames;
            private readonly HashSet<string> _enumMemberNames;
            private readonly HashSet<string> _structNames;
            private readonly HashSet<string> _funcNames;
            private readonly List<(int line, int col, int len, int type, int mod)> _tokens;
            public ExprSemanticTokensWalker(HashSet<string> enumNames, HashSet<string> enumMemberNames, HashSet<string> structNames, HashSet<string> funcNames, List<(int line, int col, int len, int type, int mod)> tokens) { _enumNames = enumNames; _enumMemberNames = enumMemberNames; _structNames = structNames; _funcNames = funcNames; _tokens = tokens; }

            protected override bool VisitExpr(Expr expr)
            {
                if (expr is FieldAccessExpr fa)
                {
                    // Check if this is EnumName.MEMBER access
                    if (fa.Target is IdentifierExpr enumTarget && _enumNames.Contains(enumTarget.Name))
                    {
                        // Emit enum type token for the target
                        if (enumTarget.Line > 0)
                            _tokens.Add((enumTarget.Line, enumTarget.Column, enumTarget.Name.Length, 2 /* enum */, 0));
                        // Emit enumMember token for the field
                        if (fa.FieldNameLine > 0)
                            _tokens.Add((fa.FieldNameLine, fa.FieldNameColumn, fa.FieldName.Length, 3 /* enumMember */, 0));
                        return true; // skip children — we handled the target
                    }
                    else
                    {
                        // Struct field access → emit property token for the field name
                        if (fa.FieldNameLine > 0)
                            _tokens.Add((fa.FieldNameLine, fa.FieldNameColumn, fa.FieldName.Length, 4 /* property */, 0));
                        return false; // continue into target for nested access (a.b.c)
                    }
                }

                if (expr is StructLiteralExpr sl)
                {
                    // Struct literal type name → struct token
                    if (_structNames.Contains(sl.TypeName) && sl.Line > 0)
                        _tokens.Add((sl.Line, sl.Column, sl.TypeName.Length, 1 /* struct */, 0));
                    return false; // continue into field value expressions
                }

                // DX9: Identifier → variable token for variable/parameter references
                // Skip struct names, enum names, and function names (they have their own token types)
                if (expr is IdentifierExpr ident)
                {
                    if (ident.Line > 0 && !_structNames.Contains(ident.Name)
                        && !_enumNames.Contains(ident.Name) && !_funcNames.Contains(ident.Name))
                        _tokens.Add((ident.Line, ident.Column, ident.Name.Length, 5 /* variable */, 0));
                    return true; // leaf node
                }

                return false; // continue walking
            }
        }

        // ============================================================
        // R1: Unified AST Walker — single-point dispatch for all
        // Block/Stmt/Expr traversal, eliminating duplicated switch
        // logic across 40+ hand-written methods.
        // ============================================================

        /// <summary>
        /// Generic AST walker that dispatches all Stmt and Expr subtypes once.
        /// Subclasses override only the hooks they care about.
        ///
        /// Walk methods are non-virtual and handle child dispatch uniformly.
        /// Hook methods (Visit*) are virtual and called before child traversal.
        /// If a Visit hook returns true, child traversal is skipped (early-out).
        /// </summary>
        private class AstWalker
        {
            /// <summary>
            /// Set to true in any Visit hook to immediately stop the entire walk.
            /// All Walk methods check this at entry and bail out.
            /// </summary>
            protected bool _abort;

            // ---- Block ----
            public void WalkBlock(BlockStmt block)
            {
                if (block == null) return;
                foreach (var stmt in block.Statements)
                {
                    if (_abort) return;
                    WalkStmt(stmt);
                }
            }

            // ---- Stmt dispatch ----
            public void WalkStmt(Stmt stmt)
            {
                if (stmt == null || _abort) return;
                if (VisitStmt(stmt)) return; // early-out (skip children)

                if (stmt is ExprStmt es)
                    WalkExpr(es.Expression);
                else if (stmt is BlockStmt bs)
                    WalkBlock(bs);
                else if (stmt is VarDeclStmt vd)
                {
                    VisitVarDecl(vd);
                    WalkExpr(vd.Initializer);
                }
                else if (stmt is IfStmt ifs)
                {
                    WalkExpr(ifs.Condition);
                    WalkStmt(ifs.ThenBranch);
                    WalkStmt(ifs.ElseBranch);
                }
                else if (stmt is WhileStmt ws)
                {
                    WalkExpr(ws.Condition);
                    WalkStmt(ws.Body);
                }
                else if (stmt is ForStmt fs)
                {
                    WalkStmt(fs.Initializer);
                    WalkExpr(fs.Condition);
                    WalkExpr(fs.Increment);
                    WalkStmt(fs.Body);
                }
                else if (stmt is ReturnStmt rs)
                    WalkExpr(rs.Value);
                else if (stmt is WaitStmt wst)
                    WalkExpr(wst.FrameCount);
                else if (stmt is DeferStmt ds)
                    WalkBlock(ds.Body);
                else if (stmt is UsingStmt us)
                {
                    foreach (var arg in us.Arguments)
                    {
                        if (_abort) return;
                        WalkExpr(arg);
                    }
                    WalkBlock(us.Body);
                }
                // DX17: WaitForStmt has a TargetInstanceId expression that must be walked
                else if (stmt is WaitForStmt wfs)
                    WalkExpr(wfs.TargetInstanceId);
                // YieldStmt — no children to walk
            }

            // ---- Expr dispatch ----
            public void WalkExpr(Expr expr)
            {
                if (expr == null || _abort) return;
                if (VisitExpr(expr)) return; // early-out (skip children)

                if (expr is BinaryExpr bin)
                {
                    WalkExpr(bin.Left);
                    WalkExpr(bin.Right);
                }
                else if (expr is UnaryExpr un)
                    WalkExpr(un.Operand);
                else if (expr is AssignExpr assign)
                {
                    WalkExpr(assign.Target);
                    WalkExpr(assign.Value);
                }
                else if (expr is CallExpr call)
                {
                    foreach (var arg in call.Arguments)
                    {
                        if (_abort) return;
                        WalkExpr(arg);
                    }
                }
                else if (expr is MemberCallExpr mc)
                {
                    foreach (var arg in mc.Arguments)
                    {
                        if (_abort) return;
                        WalkExpr(arg);
                    }
                }
                else if (expr is FieldAccessExpr fa)
                    WalkExpr(fa.Target);
                else if (expr is StructLiteralExpr sl)
                {
                    foreach (var f in sl.Fields)
                    {
                        if (_abort) return;
                        WalkExpr(f.Value);
                    }
                }
                else if (expr is SyscallExpr sc)
                {
                    foreach (var arg in sc.Arguments)
                    {
                        if (_abort) return;
                        WalkExpr(arg);
                    }
                }
                // Leaf nodes: IdentifierExpr, NumberLiteralExpr, IntLiteralExpr,
                // BoolLiteralExpr, StringIdLiteralExpr, StringLiteralExpr — no children
            }

            // ---- Hooks (override in subclasses) ----

            /// <summary>Called before dispatching a statement's children. Return true to skip children.</summary>
            protected virtual bool VisitStmt(Stmt stmt) => false;

            /// <summary>Called for VarDeclStmt specifically, before walking its initializer.</summary>
            protected virtual void VisitVarDecl(VarDeclStmt vd) { }

            /// <summary>Called before dispatching an expression's children. Return true to skip children.</summary>
            protected virtual bool VisitExpr(Expr expr) => false;
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
                // Check for array result wrapper (documentSymbol, references return arrays)
                if (result != null && result.ContainsKey("_array"))
                {
                    response.Set("result", result.GetArray("_array"));
                }
                else
                {
                    // result can be null (e.g., shutdown response)
                    response.Set("result", result != null ? (object)result : null);
                }
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
