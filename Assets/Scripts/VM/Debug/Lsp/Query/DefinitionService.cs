// Responsibility:
//   Definition operation adapter built on SymbolQueryCore.
// Owns:
//   Definition-specific result shaping only.
// Inputs/Outputs:
//   In: SymbolQueryRequest.
//   Out: definition location payload candidates.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Contracts.SymbolQueryResult
// Forbidden Dependencies:
//   - Raw protocol parsing.
//   - Path canonicalization implementation details.
// Invariants:
//   - Definition identity must match rename identity for same cursor.
// Boundary Closure:
//   Upstream: DefinitionHandler.
//   Downstream: protocol response writer.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface IDefinitionService
	{
		SymbolQueryResult Execute(SymbolQueryRequest request);
	}
}
