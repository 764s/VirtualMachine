// Responsibility:
//   Workspace-level include edge graph abstraction.
// Owns:
//   Directed include relations and traversal policy.
// Inputs/Outputs:
//   In: include directives extracted from source metadata.
//   Out: include dependencies for index, rename, and diagnostics propagation.
// Allowed Dependencies:
//   - Infrastructure.Paths.PathKey
//   - Infrastructure.Workspace.WorkspaceSnapshot
// Forbidden Dependencies:
//   - Protocol request handling.
//   - Symbol resolution heuristics.
// Invariants:
//   - Graph edges are canonicalized by PathKey.
//   - Cycle handling policy is explicit and deterministic.
// Boundary Closure:
//   Upstream: parser/preprocessor metadata adapters.
//   Downstream: WorkspaceSymbolIndex and dependency-aware services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Index
{
	public interface IIncludeGraph
	{
		IEnumerable<PathKey> GetDirectIncludes(PathKey sourceDocumentKey);

		IEnumerable<PathKey> GetDependents(PathKey includedDocumentKey);

		bool HasPath(PathKey fromDocumentKey, PathKey toDocumentKey);
	}
}
