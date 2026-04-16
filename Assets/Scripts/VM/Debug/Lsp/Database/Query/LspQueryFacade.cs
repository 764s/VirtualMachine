// Responsibility:
//   Stable query entrypoint translating LSP operations to snapshot/index reads.
// Owns:
//   Operation-to-query mapping boundary only (no protocol serialization).
// Inputs/Outputs:
//   In: snapshot + normalized query request.
//   Out: SymbolQueryResult / operation payload models.
// Allowed Dependencies:
//   - CodeDatabaseSnapshot
//   - SymbolQueryRequest
//   - SymbolQueryResult
// Forbidden Dependencies:
//   - Snapshot mutation and change ingestion.
//   - JSON-RPC message writing.
// Invariants:
//   - Same snapshot + same request yields deterministic result.
// Boundary Closure:
//   Upstream: request handlers/services.
//   Downstream: response writer adapters.

using FFVM.Debug.Lsp.Database.Contracts;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public interface ILspQueryFacade
	{
		SymbolQueryResult QueryDefinition(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QueryReferences(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QueryHover(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QueryCompletion(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QuerySignatureHelp(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QueryPrepareRename(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		SymbolQueryResult QueryRename(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbols(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);

		LspSemanticTokensPayload QuerySemanticTokensFull(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request);
	}
}
