// Responsibility:
//   Pass-through snapshot committer scaffold for orchestrator commit stage.
// Owns:
//   Minimal commit behavior that accepts provided next snapshot.
// Inputs/Outputs:
//   In: current snapshot, next snapshot candidate, and request metadata.
//   Out: committed snapshot output with success/failure flag.
// Allowed Dependencies:
//   - IDatabaseSnapshotCommitter
// Forbidden Dependencies:
//   - Persistent storage and protocol interactions.
// Invariants:
//   - If next snapshot is null, current snapshot is committed unchanged.
// Boundary Closure:
//   Upstream: orchestrator commit stage.
//   Downstream: in-memory database snapshot reference update.

namespace FFVM.Debug.Lsp.Database
{
	public sealed class PassThroughDatabaseSnapshotCommitter : IDatabaseSnapshotCommitter
	{
		public bool TryCommit(
			CodeDatabaseSnapshot currentSnapshot,
			CodeDatabaseSnapshot nextSnapshot,
			DatabaseOperationRequest request,
			out CodeDatabaseSnapshot committedSnapshot,
			out string error)
		{
			error = null;
			committedSnapshot = nextSnapshot ?? currentSnapshot ?? CodeDatabaseSnapshot.Empty();
			return true;
		}
	}
}
