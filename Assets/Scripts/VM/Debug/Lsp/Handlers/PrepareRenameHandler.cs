// Responsibility:
//   Adapter for textDocument/prepareRename request.
// Owns:
//   Request parsing and prepare-rename call into RenameService.
// Inputs/Outputs:
//   In: protocol request params.
//   Out: rename range or explicit not-renameable result.
// Allowed Dependencies:
//   - Query.RenameService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Standalone symbol lookup not shared with rename.
//   - Protocol stream writes.
// Invariants:
//   - Must share identical symbol identity resolution with RenameHandler.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IPrepareRenameHandler
	{
		object Handle(object requestParams);
	}
}
