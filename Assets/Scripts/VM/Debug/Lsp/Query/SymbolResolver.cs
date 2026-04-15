// Responsibility:
//   Resolve the symbol-at-position against one workspace snapshot.
// Owns:
//   Position-based candidate extraction and disambiguation policy.
// Inputs/Outputs:
//   In: SymbolQueryRequest and WorkspaceSymbolIndex.
//   Out: SymbolIdentity candidates with provenance metadata.
// Allowed Dependencies:
//   - Contracts (SymbolIdentity, TextPosition, TextSpan)
//   - Index.WorkspaceSymbolIndex
//   - Infrastructure.Text.SpanConverter
// Forbidden Dependencies:
//   - Protocol-level error formatting.
//   - Diagnostic ownership routing.
// Invariants:
//   - Same request against same snapshot yields deterministic candidates.
//   - Shadowing and alias precedence are explicit.
// Boundary Closure:
//   Upstream: SymbolQueryCore.
//   Downstream: operation-specific query services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Contracts;
using FFVM.Debug.Lsp.Index;

namespace FFVM.Debug.Lsp.Query
{
	public interface ISymbolResolver
	{
		bool TryResolve(SymbolQueryRequest request, IWorkspaceSymbolIndex index, out SymbolIdentity symbol);

		IReadOnlyList<SymbolIdentity> ResolveCandidates(SymbolQueryRequest request, IWorkspaceSymbolIndex index);
	}
}
