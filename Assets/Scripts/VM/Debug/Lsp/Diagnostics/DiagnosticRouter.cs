// Responsibility:
//   Route diagnostics to publication targets using ownership policy.
// Owns:
//   Grouping, deduplication, and publish-target mapping.
// Inputs/Outputs:
//   In: diagnostic stream and DiagnosticOwnershipPolicy decisions.
//   Out: per-document diagnostic batches for protocol notification layer.
// Allowed Dependencies:
//   - DiagnosticOwnershipPolicy
//   - Infrastructure.Paths.PathKey
//   - Infrastructure.Workspace.WorkspaceSnapshot
// Forbidden Dependencies:
//   - Symbol resolution logic.
//   - Request handler branching.
// Invariants:
//   - Same input diagnostics produce stable per-file output batches.
//   - No direct raw-path comparison.
// Boundary Closure:
//   Upstream: compile/analysis pipeline.
//   Downstream: notification dispatcher and response writer.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;
using FFVM.Debug.Lsp.Infrastructure.Workspace;

namespace FFVM.Debug.Lsp.Diagnostics
{
	public interface IDiagnosticRouter
	{
		IReadOnlyDictionary<PathKey, IReadOnlyList<DiagnosticItem>> Route(
			IEnumerable<DiagnosticItem> diagnostics,
			IWorkspaceSnapshot snapshot);
	}
}
