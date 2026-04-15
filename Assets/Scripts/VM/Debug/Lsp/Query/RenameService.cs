// Responsibility:
//   Rename and prepare-rename semantic service built on SymbolQueryCore.
// Owns:
//   Rename eligibility checks and rename-range collection contract.
// Inputs/Outputs:
//   In: SymbolQueryRequest plus rename target name.
//   Out: prepare range and workspace-edit candidate ranges.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Contracts.SymbolQueryResult
//   - Infrastructure.Paths.PathKey
// Forbidden Dependencies:
//   - Protocol workspace-edit serialization.
//   - File watch event handling.
// Invariants:
//   - PrepareRename and Rename share exactly one symbol resolution result.
//   - Rename ranges never include non-symbol tokens.
// Boundary Closure:
//   Upstream: RenameHandler and PrepareRenameHandler.
//   Downstream: response writer and workspace edit builder.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface IRenameService
	{
		SymbolQueryResult PrepareRename(SymbolQueryRequest request);

		SymbolQueryResult ExecuteRename(SymbolQueryRequest request, string newName);
	}
}
