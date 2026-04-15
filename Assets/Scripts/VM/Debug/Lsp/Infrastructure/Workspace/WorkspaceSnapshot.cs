// Responsibility:
//   Immutable read model of workspace state for one query/compile cycle.
// Owns:
//   Frozen document set and related metadata view.
// Inputs/Outputs:
//   In: WorkspaceDocumentStore state at capture time.
//   Out: stable data source for query/index/diagnostics operations.
// Allowed Dependencies:
//   - Infrastructure.Workspace.WorkspaceDocumentStore
//   - Infrastructure.Paths.PathKey
// Forbidden Dependencies:
//   - Protocol handlers.
//   - Live mutable updates.
// Invariants:
//   - Snapshot content does not change during a single operation.
//   - All downstream services operate against one snapshot instance.
// Boundary Closure:
//   Upstream: LspServer operation boundary and document store.
//   Downstream: Symbol resolver, index traversal, diagnostics router.

using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Infrastructure.Workspace
{
	public sealed class WorkspaceDocumentSnapshot
	{
		public PathKey DocumentKey { get; }
		public string Text { get; }
		public int? Version { get; }

		public WorkspaceDocumentSnapshot(PathKey documentKey, string text, int? version)
		{
			DocumentKey = documentKey;
			Text = text ?? string.Empty;
			Version = version;
		}
	}

	public interface IWorkspaceSnapshot
	{
		DateTime CapturedAtUtc { get; }

		bool TryGetDocument(PathKey documentKey, out WorkspaceDocumentSnapshot snapshot);

		IEnumerable<WorkspaceDocumentSnapshot> GetDocuments();
	}
}
