// Responsibility:
//   Authoritative document text/version store for workspace and open buffers.
// Owns:
//   Current text state, version tracking, and source acquisition precedence.
// Inputs/Outputs:
//   In: didOpen/didChange/didClose and file-system refresh events.
//   Out: consistent per-file snapshots for index/query/diagnostics.
// Allowed Dependencies:
//   - Infrastructure.Paths.PathKey
//   - Infrastructure.Text.LineMap
// Forbidden Dependencies:
//   - Symbol query logic.
//   - Protocol response serialization.
// Invariants:
//   - Snapshot reads are side-effect free.
//   - Version monotonicity is preserved per document.
// Boundary Closure:
//   Upstream: notification handlers and file watchers.
//   Downstream: WorkspaceSnapshot, index rebuild, query services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Infrastructure.Workspace
{
	public interface IWorkspaceDocumentStore
	{
		void Upsert(PathKey documentKey, string text, int? version);

		bool Remove(PathKey documentKey);

		bool TryGetDocument(PathKey documentKey, out WorkspaceDocumentSnapshot snapshot);

		IEnumerable<PathKey> GetAllDocumentKeys();

		IWorkspaceSnapshot CaptureSnapshot();
	}
}
