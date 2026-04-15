// Responsibility:
//   Default high-frequency stream policies for coalescing and supersession.
// Owns:
//   Stateless arbitration rules for stream-local request churn.
// Inputs/Outputs:
//   In: existing/incoming operation requests.
//   Out: coalesce/supersession decisions.
// Allowed Dependencies:
//   - IDatabaseOperationCoalescer
//   - IDatabaseSupersessionPolicy
// Forbidden Dependencies:
//   - Task queue or commit mechanics.
// Invariants:
//   - Decisions depend only on request metadata.
// Boundary Closure:
//   Upstream: orchestrator admission stage.
//   Downstream: enqueue/cancel execution paths.

using System;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class DefaultDatabaseOperationCoalescer : IDatabaseOperationCoalescer
	{
		public bool CanCoalesce(DatabaseOperationRequest existing, DatabaseOperationRequest incoming)
		{
			return existing != null
				&& incoming != null
				&& existing.Kind == DatabaseOperationKind.ApplyChangeSet
				&& incoming.Kind == DatabaseOperationKind.ApplyChangeSet
				&& !string.IsNullOrWhiteSpace(existing.StreamKey)
				&& string.Equals(existing.StreamKey, incoming.StreamKey, StringComparison.Ordinal);
		}

		public DatabaseCoalesceResult Coalesce(DatabaseOperationRequest existing, DatabaseOperationRequest incoming)
		{
			if (!CanCoalesce(existing, incoming))
			{
				return new DatabaseCoalesceResult(
					DatabaseCoalesceDecision.None,
					incoming,
					"Coalesce conditions were not satisfied.");
			}

			if (incoming.Priority > existing.Priority)
			{
				return new DatabaseCoalesceResult(
					DatabaseCoalesceDecision.ReplaceExisting,
					incoming,
					"Incoming command replaces lower-priority existing command.");
			}

			if (incoming.Priority < existing.Priority)
			{
				return new DatabaseCoalesceResult(
					DatabaseCoalesceDecision.KeepExisting,
					existing,
					"Existing command retained due to higher priority.");
			}

			return new DatabaseCoalesceResult(
				DatabaseCoalesceDecision.MergeIntoNew,
				incoming,
				"Equivalent-priority requests coalesced by keeping latest incoming command.");
		}
	}

	public sealed class DefaultDatabaseSupersessionPolicy : IDatabaseSupersessionPolicy
	{
		public bool ShouldCancelExisting(DatabaseOperationRequest existing, DatabaseOperationRequest incoming)
		{
			if (existing == null || incoming == null)
				return false;

			if (string.IsNullOrWhiteSpace(existing.StreamKey)
				|| !string.Equals(existing.StreamKey, incoming.StreamKey, StringComparison.Ordinal))
			{
				return false;
			}

			if (incoming.Priority > existing.Priority)
				return true;

			if (incoming.Priority < existing.Priority)
				return false;

			return incoming.CreatedAtUtc >= existing.CreatedAtUtc;
		}
	}
}
