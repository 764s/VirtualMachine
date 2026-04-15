// Responsibility:
//   Hover payload provider using resolved symbol identity.
// Owns:
//   Hover content composition policy only.
// Inputs/Outputs:
//   In: SymbolQueryRequest and symbol metadata.
//   Out: hover content blocks and target range.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Contracts.SymbolIdentity
// Forbidden Dependencies:
//   - Protocol markdown transport policy.
//   - Completion sorting heuristics.
// Invariants:
//   - Hover target identity matches definition identity at same cursor.
// Boundary Closure:
//   Upstream: HoverHandler.
//   Downstream: protocol response writer.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface IHoverService
	{
		SymbolQueryResult Execute(SymbolQueryRequest request);
	}
}
