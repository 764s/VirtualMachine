// Responsibility:
//   Single bridge contract between VS Code protocol layer and database layer.
// Owns:
//   Request/notification translation boundary and diagnostics pull-out contract.
// Inputs/Outputs:
//   In: normalized LSP JsonObject params from protocol loop.
//   Out: query payload objects and queued diagnostics payloads.
// Allowed Dependencies:
//   - JsonObject
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
			Uri = uri ?? string.Empty;
			Diagnostics = diagnostics ?? EmptyDiagnostics;
			Version = version;
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

		object QueryDocumentSymbol(JsonObject requestParams);

		object QueryHover(JsonObject requestParams);

		object QueryDefinition(JsonObject requestParams);

		object QueryReferences(JsonObject requestParams);

		object QueryCompletion(JsonObject requestParams);

		object QuerySignatureHelp(JsonObject requestParams);

		object QueryRename(JsonObject requestParams);

		object QueryPrepareRename(JsonObject requestParams);

		object QuerySemanticTokensFull(JsonObject requestParams);

		object QueryWillRenameFiles(JsonObject requestParams);

		bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics);
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

		public object QueryDocumentSymbol(JsonObject requestParams)
		{
			return null;
		}

		public object QueryHover(JsonObject requestParams)
		{
			return null;
		}

		public object QueryDefinition(JsonObject requestParams)
		{
			return null;
		}

		public object QueryReferences(JsonObject requestParams)
		{
			return null;
		}

		public object QueryCompletion(JsonObject requestParams)
		{
			return null;
		}

		public object QuerySignatureHelp(JsonObject requestParams)
		{
			return null;
		}

		public object QueryRename(JsonObject requestParams)
		{
			return null;
		}

		public object QueryPrepareRename(JsonObject requestParams)
		{
			return null;
		}

		public object QuerySemanticTokensFull(JsonObject requestParams)
		{
			return null;
		}

		public object QueryWillRenameFiles(JsonObject requestParams)
		{
			return null;
		}

		public bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics)
		{
			diagnostics = null;
			return false;
		}
	}
}
