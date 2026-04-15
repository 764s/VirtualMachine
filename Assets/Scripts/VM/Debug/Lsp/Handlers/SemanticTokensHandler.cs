// Responsibility:
//   Adapter for semantic tokens requests.
// Owns:
//   Parameter parsing and semantic-token service invocation contract.
// Inputs/Outputs:
//   In: protocol request params.
//   Out: semantic-token data model.
// Allowed Dependencies:
//   - Query/Index semantic-token provider abstraction.
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Token classification heuristics duplicated from parser.
//   - Diagnostics ownership policy.
// Invariants:
//   - Token ranges use shared span conversion policy.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface ISemanticTokensHandler
	{
		object Handle(object requestParams);
	}
}
