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
    ///                   textDocument/completion, textDocument/signatureHelp
    ///   Notifications:  initialized, exit, textDocument/didOpen, textDocument/didChange
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

        // --- Document store (uri → content + cached AST) ---
        private readonly Dictionary<string, string> _documents = new Dictionary<string, string>();
        private readonly Dictionary<string, ModuleNode> _documentAsts = new Dictionary<string, ModuleNode>();

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

        /// <summary>
        /// DX4-P0: Convert a document URI to a relative file path from workspace root.
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
                // Parse to get AST (for symbol queries)
                var parser = new Parser();
                var ast = parser.Parse(source, out var parseErrors);

                // Cache AST if parse succeeded (keep old AST on failure for continued symbol support)
                if (parseErrors == null || parseErrors.Count == 0)
                    _documentAsts[uri] = ast;
                else if (!_documentAsts.ContainsKey(uri) && ast != null)
                    _documentAsts[uri] = ast; // better than nothing on first open

                // DX4-P0: Compile for diagnostics with workspace file resolver support.
                // entryFunc=null → diagnostics-only mode (no entry function requirement).
                // fileResolver → enables include directive resolution from workspace root.
                // DX4-P1: Pass project compile options when available.
                var compiler = new BytecodeCompiler();
                string filePath = _rootPath != null ? UriToFilePath(uri, _rootPath) : null;
                var result = compiler.Compile(source, null, _defaultSyscalls, null,
                    _fileResolver, filePath, null, _projectCompileOptions);

                if (!result.Success && result.Errors != null)
                {
                    foreach (string error in result.Errors)
                    {
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
            if (uri != null && _documentAsts.TryGetValue(uri, out var ast))
                return ast;
            return null;
        }

        // --- documentSymbol ---

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
            var ast = GetCachedAst(uri);
            if (ast == null) return null;

            var position = parameters?.GetObject("position");
            if (position == null) return null;

            int lspLine = position.GetInt("line");
            int lspChar = position.GetInt("character");
            int astLine = lspLine + 1;
            int astCol = lspChar + 1;

            // Try to find a symbol at this position
            string hoverText = FindHoverText(ast, astLine, astCol);
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
                string result = FindHoverInBlock(ast, func, func.Body, line, col);
                if (result != null) return result;

                // Check parameters
                foreach (var param in func.Parameters)
                {
                    // Parameters don't have their own Line/Column, approximate from function
                    // We'll match them by name when found as IdentifierExpr in the body
                }
            }

            return null;
        }

        private static string FindHoverInBlock(ModuleNode ast, FuncDecl currentFunc, BlockStmt block, int line, int col)
        {
            if (block == null) return null;
            foreach (var stmt in block.Statements)
            {
                string result = FindHoverInStmt(ast, currentFunc, stmt, line, col);
                if (result != null) return result;
            }
            return null;
        }

        private static string FindHoverInStmt(ModuleNode ast, FuncDecl currentFunc, Stmt stmt, int line, int col)
        {
            if (stmt == null) return null;

            if (stmt is VarDeclStmt vd)
            {
                // Check initializer FIRST (has precise column matching for calls, identifiers, etc.)
                if (vd.Initializer != null)
                {
                    string result = FindHoverInExpr(ast, currentFunc, vd.Initializer, line, col);
                    if (result != null) return result;
                }
                // Variable name hover — only when cursor is on the name itself
                // VarDeclStmt.Column points to 'var'; name starts at Column + 4 ("var ")
                if (vd.Line == line)
                {
                    int nameStart = vd.Column + 4; // skip "var "
                    if (ColMatches(nameStart, vd.Name.Length, col))
                    {
                        string typeStr = vd.TypeName ?? "int";
                        return $"var {vd.Name}: {typeStr}";
                    }
                }
            }
            else if (stmt is IfStmt ifs)
            {
                string r = FindHoverInExpr(ast, currentFunc, ifs.Condition, line, col);
                if (r != null) return r;
                r = FindHoverInStmt(ast, currentFunc, ifs.ThenBranch, line, col);
                if (r != null) return r;
                r = FindHoverInStmt(ast, currentFunc, ifs.ElseBranch, line, col);
                if (r != null) return r;
            }
            else if (stmt is WhileStmt ws)
            {
                string r = FindHoverInExpr(ast, currentFunc, ws.Condition, line, col);
                if (r != null) return r;
                r = FindHoverInStmt(ast, currentFunc, ws.Body, line, col);
                if (r != null) return r;
            }
            else if (stmt is ForStmt fs)
            {
                string r = FindHoverInStmt(ast, currentFunc, fs.Initializer, line, col);
                if (r != null) return r;
                r = FindHoverInExpr(ast, currentFunc, fs.Condition, line, col);
                if (r != null) return r;
                r = FindHoverInExpr(ast, currentFunc, fs.Increment, line, col);
                if (r != null) return r;
                r = FindHoverInStmt(ast, currentFunc, fs.Body, line, col);
                if (r != null) return r;
            }
            else if (stmt is BlockStmt bs)
            {
                return FindHoverInBlock(ast, currentFunc, bs, line, col);
            }
            else if (stmt is ExprStmt es)
            {
                return FindHoverInExpr(ast, currentFunc, es.Expression, line, col);
            }
            else if (stmt is ReturnStmt rs)
            {
                if (rs.Value != null)
                    return FindHoverInExpr(ast, currentFunc, rs.Value, line, col);
            }
            else if (stmt is WaitStmt wst)
            {
                return FindHoverInExpr(ast, currentFunc, wst.FrameCount, line, col);
            }
            else if (stmt is DeferStmt ds)
            {
                return FindHoverInBlock(ast, currentFunc, ds.Body, line, col);
            }
            else if (stmt is UsingStmt us)
            {
                foreach (var arg in us.Arguments)
                {
                    string r = FindHoverInExpr(ast, currentFunc, arg, line, col);
                    if (r != null) return r;
                }
                return FindHoverInBlock(ast, currentFunc, us.Body, line, col);
            }

            return null;
        }

        private static string FindHoverInExpr(ModuleNode ast, FuncDecl currentFunc, Expr expr, int line, int col)
        {
            if (expr == null) return null;

            if (expr is IdentifierExpr id && id.Line == line && ColMatches(id.Column, id.Name.Length, col))
            {
                // Look up variable declaration in current function
                string typeInfo = FindVarType(currentFunc, id.Name);
                if (typeInfo != null)
                    return $"var {id.Name}: {typeInfo}";
                // Check if it's a parameter
                foreach (var p in currentFunc.Parameters)
                {
                    if (p.Name == id.Name)
                    {
                        string paramHover = $"(parameter) {FormatParamDecl(p)}";
                        if (p.DocComment != null)
                            paramHover += $"\n\n{p.DocComment}";
                        return paramHover;
                    }
                }
                return $"{id.Name}";
            }

            if (expr is CallExpr call && call.Line == line && ColMatches(call.Column, call.FunctionName.Length, col))
            {
                // Look up function declaration
                foreach (var func in ast.Functions)
                {
                    if (func.Name == call.FunctionName)
                        return FormatFuncHover(func);
                }
                return $"func {call.FunctionName}(...)";
            }

            if (expr is FieldAccessExpr fa && fa.Line == line)
            {
                // Lang-13: enum member hover — when target is an enum name, show member info
                if (fa.Target is IdentifierExpr faEnumId)
                {
                    foreach (var en in ast.Enums)
                    {
                        if (en.Name == faEnumId.Name)
                        {
                            // Cursor might be on the enum name itself or on the member name
                            if (ColMatches(faEnumId.Column, faEnumId.Name.Length, col))
                            {
                                return FormatEnumHover(en);
                            }
                            // Check if cursor is on the member name (after the dot)
                            foreach (var member in en.Members)
                            {
                                if (member.Name == fa.FieldName)
                                {
                                    return $"(enum member) {en.Name}.{member.Name}";
                                }
                            }
                            return null;
                        }
                    }
                }
                return FindHoverInExpr(ast, currentFunc, fa.Target, line, col);
            }

            // Recurse into sub-expressions
            if (expr is BinaryExpr bin)
            {
                string r = FindHoverInExpr(ast, currentFunc, bin.Left, line, col);
                if (r != null) return r;
                return FindHoverInExpr(ast, currentFunc, bin.Right, line, col);
            }
            if (expr is UnaryExpr un)
            {
                return FindHoverInExpr(ast, currentFunc, un.Operand, line, col);
            }
            if (expr is AssignExpr assign)
            {
                string r = FindHoverInExpr(ast, currentFunc, assign.Target, line, col);
                if (r != null) return r;
                return FindHoverInExpr(ast, currentFunc, assign.Value, line, col);
            }
            if (expr is CallExpr call2)
            {
                foreach (var arg in call2.Arguments)
                {
                    string r = FindHoverInExpr(ast, currentFunc, arg, line, col);
                    if (r != null) return r;
                }
            }
            if (expr is StructLiteralExpr sl)
            {
                foreach (var f in sl.Fields)
                {
                    string r = FindHoverInExpr(ast, currentFunc, f.Value, line, col);
                    if (r != null) return r;
                }
            }

            return null;
        }

        /// <summary>
        /// Find the type of a variable declared in a function's body.
        /// Walks statements looking for VarDeclStmt with matching name.
        /// </summary>
        private static string FindVarType(FuncDecl func, string varName)
        {
            return FindVarTypeInBlock(func.Body, varName);
        }

        private static string FindVarTypeInBlock(BlockStmt block, string varName)
        {
            if (block == null) return null;
            foreach (var stmt in block.Statements)
            {
                string r = FindVarTypeInStmt(stmt, varName);
                if (r != null) return r;
            }
            return null;
        }

        private static string FindVarTypeInStmt(Stmt stmt, string varName)
        {
            if (stmt is VarDeclStmt vd && vd.Name == varName)
                return vd.TypeName ?? "int";
            if (stmt is BlockStmt bs) return FindVarTypeInBlock(bs, varName);
            if (stmt is IfStmt ifs)
            {
                string r = FindVarTypeInStmt(ifs.ThenBranch, varName);
                if (r != null) return r;
                return FindVarTypeInStmt(ifs.ElseBranch, varName);
            }
            if (stmt is WhileStmt ws) return FindVarTypeInStmt(ws.Body, varName);
            if (stmt is ForStmt fs)
            {
                string r = FindVarTypeInStmt(fs.Initializer, varName);
                if (r != null) return r;
                return FindVarTypeInStmt(fs.Body, varName);
            }
            if (stmt is DeferStmt ds) return FindVarTypeInBlock(ds.Body, varName);
            if (stmt is UsingStmt us) return FindVarTypeInBlock(us.Body, varName);
            return null;
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
            return $"func {func.Name}({string.Join(", ", parts)}){ret}";
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

        // --- definition ---

        private JsonObject HandleDefinition(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            if (ast == null) return null;

            var position = parameters?.GetObject("position");
            if (position == null) return null;

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            // Find what symbol is at this position
            var target = FindSymbolAtPosition(ast, astLine, astCol);
            if (target == null) return null;

            // Find the definition location
            var defLoc = FindDefinitionLocation(ast, target.Value.name, target.Value.kind, target.Value.scopeFunc);
            if (defLoc == null) return null;

            var loc = MakeLocation(uri, defLoc.Value.line, defLoc.Value.col, defLoc.Value.nameLen);
            return loc;
        }

        // --- references ---

        private JsonObject HandleReferences(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            if (ast == null) return MakeArrayResult(new List<object>());

            var position = parameters?.GetObject("position");
            if (position == null) return MakeArrayResult(new List<object>());

            int astLine = position.GetInt("line") + 1;
            int astCol = position.GetInt("character") + 1;

            var target = FindSymbolAtPosition(ast, astLine, astCol);
            if (target == null) return MakeArrayResult(new List<object>());

            var locations = new List<object>();
            CollectReferences(ast, target.Value.name, target.Value.kind, uri, locations);
            return MakeArrayResult(locations);
        }

        // ============================================================
        // LSP5: Code completion
        // ============================================================

        private JsonObject HandleCompletion(JsonObject parameters)
        {
            string uri = GetDocumentUri(parameters);
            var ast = GetCachedAst(uri);
            string source = null;
            if (uri != null) _documents.TryGetValue(uri, out source);

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

            if (isDotContext && dotPrefix != null && ast != null)
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
                        resolvedStructType = FindVariableStructType(ast, containingFunc, dotPrefix);
                    }
                    else
                    {
                        // Chained case: "varName.field1.field2." — resolve through struct types
                        string rootVar = dotPrefix.Substring(0, dotIdx);
                        string fieldChain = dotPrefix.Substring(dotIdx + 1);
                        string currentType = FindVariableStructType(ast, containingFunc, rootVar);
                        if (currentType != null)
                        {
                            string[] parts = fieldChain.Split('.');
                            for (int pi = 0; pi < parts.Length && currentType != null; pi++)
                            {
                                string nextType = null;
                                foreach (var st in ast.Structs)
                                {
                                    if (st.Name == currentType)
                                    {
                                        foreach (var field in st.Fields)
                                        {
                                            if (field.Name == parts[pi])
                                            {
                                                // Check if this field's type is a struct
                                                foreach (var st2 in ast.Structs)
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
                        foreach (var st in ast.Structs)
                        {
                            if (st.Name == resolvedStructType)
                            {
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
                    foreach (var en in ast.Enums)
                    {
                        if (en.Name == dotPrefix)
                        {
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

                if (ast != null)
                {
                    // Functions
                    foreach (var func in ast.Functions)
                    {
                        string detail = FormatFuncSignature(func);
                        if (func.DocComment != null)
                            detail += "  — " + func.DocComment.Replace("\n", " ");
                        items.Add(MakeCompletionItem(func.Name, 3 /* Function */,
                            detail, FormatFuncDoc(func)));
                    }

                    // Structs
                    foreach (var st in ast.Structs)
                    {
                        items.Add(MakeCompletionItem(st.Name, 22 /* Struct */, null));
                    }

                    // Lang-13: Enums
                    foreach (var en in ast.Enums)
                    {
                        items.Add(MakeCompletionItem(en.Name, 13 /* Enum */, $"enum {en.Name}"));
                    }

                    // Lang-1: Module-level variables and constants
                    foreach (var mv in ast.ModuleVariables)
                    {
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
            if (uri != null) _documents.TryGetValue(uri, out source);
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
            var ast = GetCachedAst(uri);

            // User-defined function
            if (ast != null)
            {
                foreach (var func in ast.Functions)
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

        private static string FindVarStructTypeInBlock(ModuleNode ast, BlockStmt block, string varName)
        {
            if (block == null) return null;
            foreach (var stmt in block.Statements)
            {
                if (stmt is VarDeclStmt vd && vd.Name == varName)
                {
                    if (vd.TypeName != null)
                    {
                        foreach (var st in ast.Structs)
                        {
                            if (st.Name == vd.TypeName)
                                return vd.TypeName;
                        }
                    }
                    return null;
                }
                string r = null;
                if (stmt is BlockStmt bs) r = FindVarStructTypeInBlock(ast, bs, varName);
                else if (stmt is IfStmt ifs)
                {
                    r = FindVarStructTypeInStmt(ast, ifs.ThenBranch, varName);
                    if (r == null) r = FindVarStructTypeInStmt(ast, ifs.ElseBranch, varName);
                }
                else if (stmt is WhileStmt ws) r = FindVarStructTypeInStmt(ast, ws.Body, varName);
                else if (stmt is ForStmt fs)
                {
                    r = FindVarStructTypeInStmt(ast, fs.Initializer, varName);
                    if (r == null) r = FindVarStructTypeInStmt(ast, fs.Body, varName);
                }
                else if (stmt is DeferStmt ds) r = FindVarStructTypeInBlock(ast, ds.Body, varName);
                else if (stmt is UsingStmt us) r = FindVarStructTypeInBlock(ast, us.Body, varName);
                if (r != null) return r;
            }
            return null;
        }

        private static string FindVarStructTypeInStmt(ModuleNode ast, Stmt stmt, string varName)
        {
            if (stmt is BlockStmt bs) return FindVarStructTypeInBlock(ast, bs, varName);
            if (stmt is VarDeclStmt vd && vd.Name == varName)
            {
                if (vd.TypeName != null)
                {
                    foreach (var st in ast.Structs)
                    {
                        if (st.Name == vd.TypeName)
                            return vd.TypeName;
                    }
                }
                return null;
            }
            return null;
        }

        /// <summary>
        /// Collect all variables declared in a block before the given AST line.
        /// </summary>
        private static void CollectVariablesInScope(BlockStmt block, int beforeAstLine, List<object> items)
        {
            if (block == null) return;
            foreach (var stmt in block.Statements)
            {
                if (stmt is VarDeclStmt vd)
                {
                    if (vd.Line < beforeAstLine)
                    {
                        string typeStr = vd.TypeName ?? "int";
                        items.Add(MakeCompletionItem(vd.Name, 6 /* Variable */,
                            $"var {vd.Name}: {typeStr}"));
                    }
                }
                // Recurse into nested blocks that contain the cursor
                if (stmt is IfStmt ifs)
                {
                    CollectVariablesInStmt(ifs.ThenBranch, beforeAstLine, items);
                    CollectVariablesInStmt(ifs.ElseBranch, beforeAstLine, items);
                }
                else if (stmt is WhileStmt ws)
                    CollectVariablesInStmt(ws.Body, beforeAstLine, items);
                else if (stmt is ForStmt fs)
                {
                    CollectVariablesInStmt(fs.Initializer, beforeAstLine, items);
                    CollectVariablesInStmt(fs.Body, beforeAstLine, items);
                }
                else if (stmt is BlockStmt bs)
                    CollectVariablesInScope(bs, beforeAstLine, items);
                else if (stmt is DeferStmt ds)
                    CollectVariablesInScope(ds.Body, beforeAstLine, items);
                else if (stmt is UsingStmt us)
                    CollectVariablesInScope(us.Body, beforeAstLine, items);
            }
        }

        private static void CollectVariablesInStmt(Stmt stmt, int beforeAstLine, List<object> items)
        {
            if (stmt is BlockStmt bs) CollectVariablesInScope(bs, beforeAstLine, items);
            else if (stmt is VarDeclStmt vd && vd.Line < beforeAstLine)
            {
                string typeStr = vd.TypeName ?? "int";
                items.Add(MakeCompletionItem(vd.Name, 6 /* Variable */, $"var {vd.Name}: {typeStr}"));
            }
        }

        // ============================================================
        // LSP4: Symbol lookup engine
        // ============================================================

        private enum SymbolKindTag { Function, Variable, Struct, Parameter, Enum }

        private struct SymbolAtPosition
        {
            public string name;
            public SymbolKindTag kind;
            public string scopeFunc; // null for top-level symbols
        }

        /// <summary>
        /// Find what symbol (function/variable/struct/parameter) is at the given AST position.
        /// </summary>
        private static SymbolAtPosition? FindSymbolAtPosition(ModuleNode ast, int line, int col)
        {
            // Check function names (on the FuncDecl line)
            foreach (var func in ast.Functions)
            {
                int nameStart = func.Column + "func".Length + 1;
                if (func.Line == line && ColMatches(nameStart, func.Name.Length, col))
                    return new SymbolAtPosition { name = func.Name, kind = SymbolKindTag.Function };
            }

            // Check struct names
            foreach (var st in ast.Structs)
            {
                int nameStart = st.Column + "struct".Length + 1;
                if (st.Line == line && ColMatches(nameStart, st.Name.Length, col))
                    return new SymbolAtPosition { name = st.Name, kind = SymbolKindTag.Struct };
            }

            // Lang-13: Check enum names
            foreach (var en in ast.Enums)
            {
                int nameStart = en.Column + "enum".Length + 1;
                if (en.Line == line && ColMatches(nameStart, en.Name.Length, col))
                    return new SymbolAtPosition { name = en.Name, kind = SymbolKindTag.Enum };
            }

            // Walk function bodies
            foreach (var func in ast.Functions)
            {
                var result = FindSymbolInBlock(func, func.Body, line, col);
                if (result != null) return result;
            }

            return null;
        }

        private static SymbolAtPosition? FindSymbolInBlock(FuncDecl func, BlockStmt block, int line, int col)
        {
            if (block == null) return null;
            foreach (var stmt in block.Statements)
            {
                var r = FindSymbolInStmt(func, stmt, line, col);
                if (r != null) return r;
            }
            return null;
        }

        private static SymbolAtPosition? FindSymbolInStmt(FuncDecl func, Stmt stmt, int line, int col)
        {
            if (stmt == null) return null;

            if (stmt is VarDeclStmt vd)
            {
                if (vd.Initializer != null)
                {
                    var r = FindSymbolInExpr(func, vd.Initializer, line, col);
                    if (r != null) return r;
                }
                // Match the variable name itself in the declaration
                if (vd.Line == line)
                    return new SymbolAtPosition { name = vd.Name, kind = SymbolKindTag.Variable, scopeFunc = func.Name };
            }
            else if (stmt is ExprStmt es)
                return FindSymbolInExpr(func, es.Expression, line, col);
            else if (stmt is BlockStmt bs)
                return FindSymbolInBlock(func, bs, line, col);
            else if (stmt is IfStmt ifs)
            {
                var r = FindSymbolInExpr(func, ifs.Condition, line, col);
                if (r != null) return r;
                r = FindSymbolInStmt(func, ifs.ThenBranch, line, col);
                if (r != null) return r;
                return FindSymbolInStmt(func, ifs.ElseBranch, line, col);
            }
            else if (stmt is WhileStmt ws)
            {
                var r = FindSymbolInExpr(func, ws.Condition, line, col);
                if (r != null) return r;
                return FindSymbolInStmt(func, ws.Body, line, col);
            }
            else if (stmt is ForStmt fs)
            {
                var r = FindSymbolInStmt(func, fs.Initializer, line, col);
                if (r != null) return r;
                r = FindSymbolInExpr(func, fs.Condition, line, col);
                if (r != null) return r;
                r = FindSymbolInExpr(func, fs.Increment, line, col);
                if (r != null) return r;
                return FindSymbolInStmt(func, fs.Body, line, col);
            }
            else if (stmt is ReturnStmt rs && rs.Value != null)
                return FindSymbolInExpr(func, rs.Value, line, col);
            else if (stmt is WaitStmt wst)
                return FindSymbolInExpr(func, wst.FrameCount, line, col);
            else if (stmt is DeferStmt ds)
                return FindSymbolInBlock(func, ds.Body, line, col);
            else if (stmt is UsingStmt us)
            {
                foreach (var arg in us.Arguments)
                {
                    var r = FindSymbolInExpr(func, arg, line, col);
                    if (r != null) return r;
                }
                return FindSymbolInBlock(func, us.Body, line, col);
            }
            return null;
        }

        private static SymbolAtPosition? FindSymbolInExpr(FuncDecl func, Expr expr, int line, int col)
        {
            if (expr == null) return null;

            if (expr is IdentifierExpr id && id.Line == line && ColMatches(id.Column, id.Name.Length, col))
            {
                // Determine if it's a parameter or variable
                foreach (var p in func.Parameters)
                {
                    if (p.Name == id.Name)
                        return new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Parameter, scopeFunc = func.Name };
                }
                return new SymbolAtPosition { name = id.Name, kind = SymbolKindTag.Variable, scopeFunc = func.Name };
            }

            if (expr is CallExpr call && call.Line == line && ColMatches(call.Column, call.FunctionName.Length, col))
            {
                return new SymbolAtPosition { name = call.FunctionName, kind = SymbolKindTag.Function };
            }

            // Recurse
            if (expr is BinaryExpr bin)
            {
                var r = FindSymbolInExpr(func, bin.Left, line, col);
                if (r != null) return r;
                return FindSymbolInExpr(func, bin.Right, line, col);
            }
            if (expr is UnaryExpr un)
                return FindSymbolInExpr(func, un.Operand, line, col);
            if (expr is AssignExpr assign)
            {
                var r = FindSymbolInExpr(func, assign.Target, line, col);
                if (r != null) return r;
                return FindSymbolInExpr(func, assign.Value, line, col);
            }
            if (expr is CallExpr call2)
            {
                foreach (var arg in call2.Arguments)
                {
                    var r = FindSymbolInExpr(func, arg, line, col);
                    if (r != null) return r;
                }
            }
            if (expr is FieldAccessExpr fa)
            {
                return FindSymbolInExpr(func, fa.Target, line, col);
            }

            return null;
        }

        /// <summary>
        /// Find the definition location for a named symbol.
        /// Returns (line, col, nameLen) in AST coordinates (1-based).
        /// </summary>
        private static (int line, int col, int nameLen)? FindDefinitionLocation(
            ModuleNode ast, string name, SymbolKindTag kind, string scopeFunc)
        {
            if (kind == SymbolKindTag.Function)
            {
                foreach (var func in ast.Functions)
                {
                    if (func.Name == name)
                    {
                        int nameCol = func.Column + "func".Length + 1;
                        return (func.Line, nameCol, func.Name.Length);
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
                        return (st.Line, nameCol, st.Name.Length);
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
                        return (en.Line, nameCol, en.Name.Length);
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
                                    // Parameters don't have their own Line/Column;
                                    // use the function declaration line
                                    return (func.Line, func.Column, func.Name.Length);
                                }
                            }
                        }

                        // Check variable declarations in body
                        var loc = FindVarDeclLocation(func.Body, name);
                        if (loc != null) return loc;
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
        /// Collect all reference locations for a named symbol across the AST.
        /// </summary>
        private static void CollectReferences(ModuleNode ast, string name, SymbolKindTag kind, string uri, List<object> locations)
        {
            if (kind == SymbolKindTag.Function)
            {
                // Function declaration
                foreach (var func in ast.Functions)
                {
                    if (func.Name == name)
                    {
                        int nameCol = func.Column + "func".Length + 1;
                        locations.Add(MakeLocation(uri, func.Line, nameCol, name.Length));
                    }
                }

                // Function call sites
                foreach (var func in ast.Functions)
                {
                    CollectCallRefsInBlock(func.Body, name, uri, locations);
                }
            }
            else if (kind == SymbolKindTag.Struct)
            {
                // Struct declaration
                foreach (var st in ast.Structs)
                {
                    if (st.Name == name)
                    {
                        int nameCol = st.Column + "struct".Length + 1;
                        locations.Add(MakeLocation(uri, st.Line, nameCol, name.Length));
                    }
                }

                // Struct usage in VarDeclStmt.TypeName
                foreach (var func in ast.Functions)
                {
                    CollectTypeRefsInBlock(func.Body, name, uri, locations);
                }
            }
            // Lang-13: enum references
            else if (kind == SymbolKindTag.Enum)
            {
                // Enum declaration
                foreach (var en in ast.Enums)
                {
                    if (en.Name == name)
                    {
                        int nameCol = en.Column + "enum".Length + 1;
                        locations.Add(MakeLocation(uri, en.Line, nameCol, name.Length));
                    }
                }
            }
            else // Variable or Parameter
            {
                // All identifier references with matching name across all functions
                foreach (var func in ast.Functions)
                {
                    CollectIdentRefsInBlock(func.Body, name, uri, locations);
                }
            }
        }

        private static void CollectCallRefsInBlock(BlockStmt block, string funcName, string uri, List<object> locations)
        {
            if (block == null) return;
            foreach (var stmt in block.Statements)
                CollectCallRefsInStmt(stmt, funcName, uri, locations);
        }

        private static void CollectCallRefsInStmt(Stmt stmt, string funcName, string uri, List<object> locations)
        {
            if (stmt == null) return;
            if (stmt is ExprStmt es) CollectCallRefsInExpr(es.Expression, funcName, uri, locations);
            else if (stmt is BlockStmt bs) CollectCallRefsInBlock(bs, funcName, uri, locations);
            else if (stmt is VarDeclStmt vd && vd.Initializer != null) CollectCallRefsInExpr(vd.Initializer, funcName, uri, locations);
            else if (stmt is IfStmt ifs)
            {
                CollectCallRefsInExpr(ifs.Condition, funcName, uri, locations);
                CollectCallRefsInStmt(ifs.ThenBranch, funcName, uri, locations);
                CollectCallRefsInStmt(ifs.ElseBranch, funcName, uri, locations);
            }
            else if (stmt is WhileStmt ws)
            {
                CollectCallRefsInExpr(ws.Condition, funcName, uri, locations);
                CollectCallRefsInStmt(ws.Body, funcName, uri, locations);
            }
            else if (stmt is ForStmt fs)
            {
                CollectCallRefsInStmt(fs.Initializer, funcName, uri, locations);
                CollectCallRefsInExpr(fs.Condition, funcName, uri, locations);
                CollectCallRefsInExpr(fs.Increment, funcName, uri, locations);
                CollectCallRefsInStmt(fs.Body, funcName, uri, locations);
            }
            else if (stmt is ReturnStmt rs && rs.Value != null) CollectCallRefsInExpr(rs.Value, funcName, uri, locations);
            else if (stmt is WaitStmt wst) CollectCallRefsInExpr(wst.FrameCount, funcName, uri, locations);
            else if (stmt is DeferStmt ds) CollectCallRefsInBlock(ds.Body, funcName, uri, locations);
            else if (stmt is UsingStmt us)
            {
                foreach (var arg in us.Arguments) CollectCallRefsInExpr(arg, funcName, uri, locations);
                CollectCallRefsInBlock(us.Body, funcName, uri, locations);
            }
        }

        private static void CollectCallRefsInExpr(Expr expr, string funcName, string uri, List<object> locations)
        {
            if (expr == null) return;
            if (expr is CallExpr call)
            {
                if (call.FunctionName == funcName)
                    locations.Add(MakeLocation(uri, call.Line, call.Column, funcName.Length));
                foreach (var arg in call.Arguments) CollectCallRefsInExpr(arg, funcName, uri, locations);
            }
            else if (expr is BinaryExpr bin)
            {
                CollectCallRefsInExpr(bin.Left, funcName, uri, locations);
                CollectCallRefsInExpr(bin.Right, funcName, uri, locations);
            }
            else if (expr is UnaryExpr un) CollectCallRefsInExpr(un.Operand, funcName, uri, locations);
            else if (expr is AssignExpr assign)
            {
                CollectCallRefsInExpr(assign.Target, funcName, uri, locations);
                CollectCallRefsInExpr(assign.Value, funcName, uri, locations);
            }
            else if (expr is FieldAccessExpr fa) CollectCallRefsInExpr(fa.Target, funcName, uri, locations);
            else if (expr is StructLiteralExpr sl)
            {
                foreach (var f in sl.Fields) CollectCallRefsInExpr(f.Value, funcName, uri, locations);
            }
        }

        private static void CollectIdentRefsInBlock(BlockStmt block, string varName, string uri, List<object> locations)
        {
            if (block == null) return;
            foreach (var stmt in block.Statements)
                CollectIdentRefsInStmt(stmt, varName, uri, locations);
        }

        private static void CollectIdentRefsInStmt(Stmt stmt, string varName, string uri, List<object> locations)
        {
            if (stmt == null) return;
            if (stmt is VarDeclStmt vd)
            {
                // Include the declaration itself as a reference
                if (vd.Name == varName)
                    locations.Add(MakeLocation(uri, vd.Line, vd.Column, varName.Length));
                if (vd.Initializer != null) CollectIdentRefsInExpr(vd.Initializer, varName, uri, locations);
            }
            else if (stmt is ExprStmt es) CollectIdentRefsInExpr(es.Expression, varName, uri, locations);
            else if (stmt is BlockStmt bs) CollectIdentRefsInBlock(bs, varName, uri, locations);
            else if (stmt is IfStmt ifs)
            {
                CollectIdentRefsInExpr(ifs.Condition, varName, uri, locations);
                CollectIdentRefsInStmt(ifs.ThenBranch, varName, uri, locations);
                CollectIdentRefsInStmt(ifs.ElseBranch, varName, uri, locations);
            }
            else if (stmt is WhileStmt ws)
            {
                CollectIdentRefsInExpr(ws.Condition, varName, uri, locations);
                CollectIdentRefsInStmt(ws.Body, varName, uri, locations);
            }
            else if (stmt is ForStmt fs)
            {
                CollectIdentRefsInStmt(fs.Initializer, varName, uri, locations);
                CollectIdentRefsInExpr(fs.Condition, varName, uri, locations);
                CollectIdentRefsInExpr(fs.Increment, varName, uri, locations);
                CollectIdentRefsInStmt(fs.Body, varName, uri, locations);
            }
            else if (stmt is ReturnStmt rs && rs.Value != null) CollectIdentRefsInExpr(rs.Value, varName, uri, locations);
            else if (stmt is WaitStmt wst) CollectIdentRefsInExpr(wst.FrameCount, varName, uri, locations);
            else if (stmt is DeferStmt ds) CollectIdentRefsInBlock(ds.Body, varName, uri, locations);
            else if (stmt is UsingStmt us)
            {
                foreach (var arg in us.Arguments) CollectIdentRefsInExpr(arg, varName, uri, locations);
                CollectIdentRefsInBlock(us.Body, varName, uri, locations);
            }
        }

        private static void CollectIdentRefsInExpr(Expr expr, string varName, string uri, List<object> locations)
        {
            if (expr == null) return;
            if (expr is IdentifierExpr id && id.Name == varName)
                locations.Add(MakeLocation(uri, id.Line, id.Column, varName.Length));
            else if (expr is BinaryExpr bin)
            {
                CollectIdentRefsInExpr(bin.Left, varName, uri, locations);
                CollectIdentRefsInExpr(bin.Right, varName, uri, locations);
            }
            else if (expr is UnaryExpr un) CollectIdentRefsInExpr(un.Operand, varName, uri, locations);
            else if (expr is AssignExpr assign)
            {
                CollectIdentRefsInExpr(assign.Target, varName, uri, locations);
                CollectIdentRefsInExpr(assign.Value, varName, uri, locations);
            }
            else if (expr is CallExpr call)
            {
                foreach (var arg in call.Arguments) CollectIdentRefsInExpr(arg, varName, uri, locations);
            }
            else if (expr is FieldAccessExpr fa) CollectIdentRefsInExpr(fa.Target, varName, uri, locations);
            else if (expr is StructLiteralExpr sl)
            {
                foreach (var f in sl.Fields) CollectIdentRefsInExpr(f.Value, varName, uri, locations);
            }
        }

        private static void CollectTypeRefsInBlock(BlockStmt block, string typeName, string uri, List<object> locations)
        {
            if (block == null) return;
            foreach (var stmt in block.Statements)
                CollectTypeRefsInStmt(stmt, typeName, uri, locations);
        }

        private static void CollectTypeRefsInStmt(Stmt stmt, string typeName, string uri, List<object> locations)
        {
            if (stmt == null) return;
            if (stmt is VarDeclStmt vd && vd.TypeName == typeName)
                locations.Add(MakeLocation(uri, vd.Line, vd.Column, typeName.Length));
            else if (stmt is BlockStmt bs) CollectTypeRefsInBlock(bs, typeName, uri, locations);
            else if (stmt is IfStmt ifs)
            {
                CollectTypeRefsInStmt(ifs.ThenBranch, typeName, uri, locations);
                CollectTypeRefsInStmt(ifs.ElseBranch, typeName, uri, locations);
            }
            else if (stmt is WhileStmt ws) CollectTypeRefsInStmt(ws.Body, typeName, uri, locations);
            else if (stmt is ForStmt fs)
            {
                CollectTypeRefsInStmt(fs.Initializer, typeName, uri, locations);
                CollectTypeRefsInStmt(fs.Body, typeName, uri, locations);
            }
            else if (stmt is DeferStmt ds) CollectTypeRefsInBlock(ds.Body, typeName, uri, locations);
            else if (stmt is UsingStmt us) CollectTypeRefsInBlock(us.Body, typeName, uri, locations);
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
