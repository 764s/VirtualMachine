// Responsibility:
//   Canonical change-event contract entering database write-side.
// Owns:
//   Event kind, target document identity, and opaque normalized payload.
// Inputs/Outputs:
//   In: protocol/document/file-watcher adapters.
//   Out: write pipeline input for snapshot regeneration.
// Allowed Dependencies:
//   - PathKey
// Forbidden Dependencies:
//   - Query and index read-side behavior.
// Invariants:
//   - Event kind is explicit and non-ambiguous.
//   - Payload is already normalized by adapters.
// Boundary Closure:
//   Upstream: LSP notification handlers.
//   Downstream: IWorkspaceCodeDatabase.Execute (ApplyChangeSet operation).

using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseChangeKind
	{
		Unknown = 0,
		DocumentOpened,
		DocumentChanged,
		DocumentClosed,
		FileRenamed,
		WatchedFilesChanged,
		FullResyncRequested
	}

	public sealed class DatabaseChangeEvent
	{
		public DatabaseChangeKind Kind { get; }
		public PathKey DocumentKey { get; }
		public int? VersionHint { get; }
		public object Payload { get; }

		public DatabaseChangeEvent(DatabaseChangeKind kind, PathKey documentKey, int? versionHint, object payload)
		{
			Kind = kind;
			DocumentKey = documentKey;
			VersionHint = versionHint;
			Payload = payload;
		}
	}
}
