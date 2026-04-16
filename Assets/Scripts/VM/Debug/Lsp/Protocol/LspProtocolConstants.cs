namespace FFVM.Debug.Lsp.Protocol
{
    internal static class JsonRpcFields
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

    internal static class JsonRpcErrorCodes
    {
        public const int MethodNotFound = -32601;
    }

    internal static class LspMethods
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

    internal static class LspFields
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

    internal static class LspValues
    {
        public const string ServerName = "FFVM LSP (New Scaffold)";
        public const string ServerVersion = "0.1.0-placeholder";
        public const string UnknownErrorMessage = "Unknown error";
        public const string Markdown = "markdown";
    }

    internal static class LspSymbolKindNames
    {
        public const string Function = "Function";
        public const string Struct = "Struct";
        public const string Enum = "Enum";
        public const string Variable = "Variable";
        public const string Parameter = "Parameter";
        public const string StructField = "StructField";
        public const string EnumMember = "EnumMember";
    }

    internal enum LspDocumentSymbolKindCode
    {
        EnumType = 10,
        Function = 12,
        Variable = 13,
        Struct = 23,
        Parameter = 26
    }

    internal enum LspCompletionItemKindCode
    {
        Text = 1,
        Function = 3,
        Field = 5,
        Variable = 6,
        EnumType = 13,
        EnumMember = 20,
        Struct = 22
    }
}