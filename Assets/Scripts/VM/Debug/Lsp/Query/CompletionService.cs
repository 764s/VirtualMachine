// Responsibility:
//   Completion candidate provider using scope and symbol index context.
// Owns:
//   Candidate set construction and ranking inputs (not transport formatting).
// Inputs/Outputs:
//   In: SymbolQueryRequest and workspace index snapshot.
//   Out: completion item models before protocol serialization.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Index.WorkspaceSymbolIndex
// Forbidden Dependencies:
//   - JSON-RPC response framing.
//   - Diagnostic ownership decisions.
// Invariants:
//   - Candidate identity references canonical SymbolIdentity values.
// Boundary Closure:
//   Upstream: CompletionHandler.
//   Downstream: protocol response writer.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface ICompletionService
	{
		SymbolQueryResult Execute(SymbolQueryRequest request);
	}
}
