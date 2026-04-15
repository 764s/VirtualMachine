// Responsibility:
//   Single logical authority for workspace semantic data snapshots.
// Owns:
//   Snapshot lifecycle, version progression, and change ingestion boundary.
// Inputs/Outputs:
//   In: normalized database operation requests.
//   Out: immutable CodeDatabaseSnapshot for read-side query/index consumers.
// Allowed Dependencies:
//   - DatabaseOperationRequest
//   - DatabaseOperationResult
//   - CodeDatabaseSnapshot
// Forbidden Dependencies:
//   - Protocol transport and JSON-RPC message handling.
//   - Feature-specific query branching.
// Invariants:
//   - Version is monotonic and globally unique per snapshot.
//   - Readers only observe immutable snapshots.
// Boundary Closure:
//   Upstream: handlers, adapters, and composition root.
//   Downstream: task planner/center and read-side query facade.

namespace FFVM.Debug.Lsp.Database
{
	public interface IWorkspaceCodeDatabase
	{
		DatabaseOperationResult Execute(DatabaseOperationRequest request);
	}
}
