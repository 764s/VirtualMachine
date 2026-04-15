// Responsibility:
//   Unified semantic-query orchestrator for all symbol operations.
// Owns:
//   Operation routing and shared invariants for definition/references/rename/hover.
// Inputs/Outputs:
//   In: SymbolQueryRequest, WorkspaceSnapshot, WorkspaceSymbolIndex.
//   Out: SymbolQueryResult with identity/range/reference payloads.
// Allowed Dependencies:
//   - SymbolResolver
//   - Contracts
//   - Index
//   - Infrastructure converters and canonical keys
// Forbidden Dependencies:
//   - JSON-RPC wire concerns and response ids.
//   - Direct document mutation.
// Invariants:
//   - Definition/References/Rename share one identity resolution path.
//   - Range outputs are produced through SpanConverter only.
// Boundary Closure:
//   Upstream: handlers and operation services.
//   Downstream: SymbolQueryResult consumers.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface ISymbolQueryCore
	{
		SymbolQueryResult ResolveAtPosition(SymbolQueryRequest request);

		SymbolQueryResult FindDefinition(SymbolQueryRequest request);

		SymbolQueryResult FindReferences(SymbolQueryRequest request);

		SymbolQueryResult CanRename(SymbolQueryRequest request);

		SymbolQueryResult GetRenameRanges(SymbolQueryRequest request);
	}
}
