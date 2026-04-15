// Responsibility:
//   References operation adapter built on SymbolQueryCore.
// Owns:
//   Reference inclusion/exclusion policy shape (for declaration inclusion options).
// Inputs/Outputs:
//   In: SymbolQueryRequest.
//   Out: reference location set.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Contracts.SymbolQueryResult
// Forbidden Dependencies:
//   - Request JSON decoding.
//   - Diagnostics ownership policy.
// Invariants:
//   - References identity must match definition identity for same cursor.
// Boundary Closure:
//   Upstream: ReferencesHandler.
//   Downstream: protocol response writer.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface IReferencesService
	{
		SymbolQueryResult Execute(SymbolQueryRequest request);
	}
}
