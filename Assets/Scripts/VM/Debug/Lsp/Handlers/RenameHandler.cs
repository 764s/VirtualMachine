// Responsibility:
//   Adapter for textDocument/rename request.
// Owns:
//   Request parsing and RenameService invocation only.
// Inputs/Outputs:
//   In: protocol params with newName.
//   Out: workspace-edit model.
// Allowed Dependencies:
//   - Query.RenameService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Inline rename-range discovery logic.
//   - Direct edit serialization.
// Invariants:
//   - Uses the same symbol-resolution path as PrepareRenameHandler.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IRenameHandler
	{
		object Handle(object requestParams);
	}
}
