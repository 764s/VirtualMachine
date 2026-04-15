// Responsibility:
//   Adapter for textDocument/references request.
// Owns:
//   Request parsing and ReferencesService invocation only.
// Inputs/Outputs:
//   In: protocol request params and includeDeclaration option.
//   Out: reference location response model.
// Allowed Dependencies:
//   - Query.ReferencesService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Index traversal implementation.
//   - Diagnostics routing.
// Invariants:
//   - Cursor normalization matches DefinitionHandler behavior.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IReferencesHandler
	{
		object Handle(object requestParams);
	}
}
