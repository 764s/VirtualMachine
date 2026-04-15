// Responsibility:
//   Adapter for textDocument/completion request.
// Owns:
//   Request parsing and CompletionService invocation only.
// Inputs/Outputs:
//   In: protocol request params and trigger metadata.
//   Out: completion response model.
// Allowed Dependencies:
//   - Query.CompletionService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Candidate ranking implementation.
//   - Diagnostic publication.
// Invariants:
//   - Trigger context is forwarded without semantic reinterpretation.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface ICompletionHandler
	{
		object Handle(object requestParams);
	}
}
