// Responsibility:
//   Single bridge contract between VS Code protocol layer and database layer.
// Owns:
//   Request/notification translation boundary and diagnostics pull-out contract.
// Inputs/Outputs:
//   In: normalized LSP JsonObject params from protocol loop.
//   Out: query payload objects and queued diagnostics payloads.
// Allowed Dependencies:
//   - JsonObject
//   - DocumentKeyNormalizer
// Forbidden Dependencies:
//   - Direct stdio transport writes.
//   - Protocol framing and JSON-RPC envelope concerns.
// Invariants:
//   - Bridge methods are side-effect scoped to database-facing operations.
//   - Diagnostics are emitted only via TryDequeueDiagnostics contract.
// Boundary Closure:
//   Upstream: LspServerNew request/notification dispatch.
//   Downstream: database operation/query components.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Paths;

namespace FFVM.Debug.Lsp.Integration.VsCode
{
	public sealed class LspPublishedDiagnostics
	{
		private static readonly IReadOnlyList<object> EmptyDiagnostics = new List<object>(0);

		public string Uri { get; }
		public IReadOnlyList<object> Diagnostics { get; }
		public int? Version { get; }

		public LspPublishedDiagnostics(string uri, IReadOnlyList<object> diagnostics, int? version)
		{
			Uri = DocumentKeyNormalizer.Normalize(uri);
			Diagnostics = diagnostics ?? EmptyDiagnostics;
			Version = version;
		}
	}

	public sealed class LspClientRequest
	{
		public string Method { get; }
		public JsonObject Parameters { get; }
		public string RequestToken { get; }

		public LspClientRequest(string method, JsonObject parameters, string requestToken)
		{
			Method = method ?? string.Empty;
			Parameters = parameters ?? new JsonObject();
			RequestToken = requestToken ?? string.Empty;
		}
	}

	public interface ILspVsCodeDatabaseBridge
	{
		void Initialize(JsonObject initializeParams);

		void Shutdown(JsonObject shutdownParams);

		void Initialized(JsonObject initializedParams);

		void Exit(JsonObject exitParams);

		void DidOpen(JsonObject didOpenParams);

		void DidChange(JsonObject didChangeParams);

		void DidClose(JsonObject didCloseParams);

		void DidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams);

		IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbol(JsonObject requestParams);

		LspHoverPayload QueryHover(JsonObject requestParams);

		LspDefinitionPayload QueryDefinition(JsonObject requestParams);

		IReadOnlyList<LspReferenceItem> QueryReferences(JsonObject requestParams);

		IReadOnlyList<LspCompletionItem> QueryCompletion(JsonObject requestParams);

		LspSignatureHelpPayload QuerySignatureHelp(JsonObject requestParams);

		LspRenamePayload QueryRename(JsonObject requestParams);

		LspPrepareRenamePayload QueryPrepareRename(JsonObject requestParams);

		LspSemanticTokensPayload QuerySemanticTokensFull(JsonObject requestParams);

		JsonObject QueryWillRenameFiles(JsonObject requestParams);

		bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics);

		bool TryDequeueClientRequest(out LspClientRequest request);

		void HandleClientRequestResponse(string method, string requestToken, object result, JsonObject error);
	}

	public sealed class NoOpLspVsCodeDatabaseBridge : ILspVsCodeDatabaseBridge
	{
		public void Initialize(JsonObject initializeParams)
		{
		}

		public void Shutdown(JsonObject shutdownParams)
		{
		}

		public void Initialized(JsonObject initializedParams)
		{
		}

		public void Exit(JsonObject exitParams)
		{
		}

		public void DidOpen(JsonObject didOpenParams)
		{
		}

		public void DidChange(JsonObject didChangeParams)
		{
		}

		public void DidClose(JsonObject didCloseParams)
		{
		}

		public void DidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams)
		{
		}

		public IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbol(JsonObject requestParams)
		{
			return new List<LspDocumentSymbolItem>(0);
		}

		public LspHoverPayload QueryHover(JsonObject requestParams)
		{
			return null;
		}

		public LspDefinitionPayload QueryDefinition(JsonObject requestParams)
		{
			return null;
		}

		public IReadOnlyList<LspReferenceItem> QueryReferences(JsonObject requestParams)
		{
			return new List<LspReferenceItem>(0);
		}

		public IReadOnlyList<LspCompletionItem> QueryCompletion(JsonObject requestParams)
		{
			return new List<LspCompletionItem>(0);
		}

		public LspSignatureHelpPayload QuerySignatureHelp(JsonObject requestParams)
		{
			return null;
		}

		public LspRenamePayload QueryRename(JsonObject requestParams)
		{
			return null;
		}

		public LspPrepareRenamePayload QueryPrepareRename(JsonObject requestParams)
		{
			return null;
		}

		public LspSemanticTokensPayload QuerySemanticTokensFull(JsonObject requestParams)
		{
			return new LspSemanticTokensPayload(new List<int>(0), string.Empty);
		}

		public JsonObject QueryWillRenameFiles(JsonObject requestParams)
		{
			return null;
		}

		public bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics)
		{
			diagnostics = null;
			return false;
		}

		public bool TryDequeueClientRequest(out LspClientRequest request)
		{
			request = null;
			return false;
		}

		public void HandleClientRequestResponse(string method, string requestToken, object result, JsonObject error)
		{
		}
	}
}
