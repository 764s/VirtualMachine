// Responsibility:
//   Signature-help provider for call-site context.
// Owns:
//   Active signature/parameter selection policy only.
// Inputs/Outputs:
//   In: SymbolQueryRequest and call-site context from resolver/index.
//   Out: signature-help model before protocol transport formatting.
// Allowed Dependencies:
//   - SymbolQueryCore
//   - Index.WorkspaceSymbolIndex
// Forbidden Dependencies:
//   - Request dispatch concerns.
//   - Rename or diagnostics side effects.
// Invariants:
//   - Member-call alias resolution uses AliasGraph through resolver.
// Boundary Closure:
//   Upstream: SignatureHelpHandler.
//   Downstream: protocol response writer.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Query
{
	public interface ISignatureHelpService
	{
		SymbolQueryResult Execute(SymbolQueryRequest request);
	}
}
