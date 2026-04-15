// Responsibility:
//   Workspace-level include-as alias mapping graph.
// Owns:
//   Alias namespace bindings and reverse lookup rules.
// Inputs/Outputs:
//   In: include-as metadata from source files.
//   Out: alias resolution data for member-call queries.
// Allowed Dependencies:
//   - Infrastructure.Paths.PathKey
//   - Infrastructure.Workspace.WorkspaceSnapshot
// Forbidden Dependencies:
//   - Protocol payload interpretation.
//   - Text span conversion logic.
// Invariants:
//   - Alias binding is scoped and deterministic for one snapshot.
//   - Ambiguous alias collisions are represented explicitly.
// Boundary Closure:
//   Upstream: preprocessor metadata extraction.
//   Downstream: SymbolResolver and completion/signature services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Index
{
	public interface IAliasGraph
	{
		bool TryResolveAlias(PathKey sourceDocumentKey, string alias, out PathKey targetDocumentKey);

		IEnumerable<string> GetAliases(PathKey sourceDocumentKey);
	}
}
