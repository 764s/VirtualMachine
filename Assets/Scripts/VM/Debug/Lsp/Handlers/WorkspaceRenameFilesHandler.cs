// Responsibility:
//   Adapter for workspace/willRenameFiles request.
// Owns:
//   Request parsing and path-update planning invocation only.
// Inputs/Outputs:
//   In: protocol rename-file params.
//   Out: workspace-edit updates for include/import paths.
// Allowed Dependencies:
//   - Infrastructure.Paths.PathCanonicalizer
//   - Index.WorkspaceSymbolIndex
//   - Query rename/path-update service abstraction.
// Forbidden Dependencies:
//   - Direct path string comparisons.
//   - Semantic symbol lookup branching in handler.
// Invariants:
//   - All path matching uses canonical keys.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IWorkspaceRenameFilesHandler
	{
		object Handle(object requestParams);
	}
}
