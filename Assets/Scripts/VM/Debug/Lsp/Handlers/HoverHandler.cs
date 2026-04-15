// Responsibility:
//   Adapter for textDocument/hover request.
// Owns:
//   Request parsing and HoverService invocation only.
// Inputs/Outputs:
//   In: protocol request params.
//   Out: hover response model.
// Allowed Dependencies:
//   - Query.HoverService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Symbol disambiguation heuristics.
//   - Response wire serialization.
// Invariants:
//   - Hover position normalization follows shared request policy.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface IHoverHandler
	{
		object Handle(object requestParams);
	}
}
