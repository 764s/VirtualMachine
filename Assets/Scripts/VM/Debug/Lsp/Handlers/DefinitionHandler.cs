// Responsibility:
//   Adapter for textDocument/definition request.
// Owns:
//   Request parameter parsing and DefinitionService invocation only.
// Inputs/Outputs:
//   In: protocol request params.
//   Out: definition response model.
// Allowed Dependencies:
//   - Query.DefinitionService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Inline symbol matching logic.
//   - Direct JSON-RPC stream writes.
// Invariants:
//   - All positions and URIs are normalized before query call.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IDefinitionHandler
	{
		object Handle(object requestParams);
	}
}
