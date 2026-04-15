// Responsibility:
//   Build or update derived index snapshot from database snapshot.
// Owns:
//   Index regeneration and incremental update boundary.
// Inputs/Outputs:
//   In: CodeDatabaseSnapshot + changed aggregate/document hints.
//   Out: immutable IIndexSnapshot.
// Allowed Dependencies:
//   - CodeDatabaseSnapshot
//   - IIndexSnapshot
// Forbidden Dependencies:
//   - Protocol handlers and response formatting.
//   - Business feature branching.
// Invariants:
//   - Returned index snapshot matches input snapshot version.
// Boundary Closure:
//   Upstream: WorkspaceCodeDatabase write pipeline.
//   Downstream: query facade and read-side services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public interface IIndexMaintainer
	{
		IIndexSnapshot Rebuild(CodeDatabaseSnapshot snapshot);

		IIndexSnapshot Update(
			IIndexSnapshot previous,
			CodeDatabaseSnapshot snapshot,
			IReadOnlyList<PathKey> changedDocuments);
	}
}
