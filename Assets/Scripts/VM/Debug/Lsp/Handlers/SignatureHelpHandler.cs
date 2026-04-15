// Responsibility:
//   Adapter for textDocument/signatureHelp request.
// Owns:
//   Request parsing and SignatureHelpService invocation only.
// Inputs/Outputs:
//   In: protocol request params and trigger context.
//   Out: signature-help response model.
// Allowed Dependencies:
//   - Query.SignatureHelpService
//   - Infrastructure.Paths.PathCanonicalizer
// Forbidden Dependencies:
//   - Alias/member-call semantic resolution internals.
//   - Stream IO handling.
// Invariants:
//   - Signature request normalization is identical to completion path.
// Boundary Closure:
//   Upstream: LspRequestDispatcher.
//   Downstream: LspResponseWriter.

namespace FFVM.Debug.Lsp.Handlers
{
	public interface ISignatureHelpHandler
	{
		object Handle(object requestParams);
	}
}
