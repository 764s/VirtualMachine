// Responsibility:
//   Default deterministic conflict resolver for command admission arbitration.
// Owns:
//   Version/priority/stream-based conflict resolution decisions.
// Inputs/Outputs:
//   In: conflict context with incoming/existing command and runtime version.
//   Out: a deterministic conflict decision.
// Allowed Dependencies:
//   - IDatabaseConflictResolver
// Forbidden Dependencies:
//   - Semantic query behaviors or protocol concerns.
// Invariants:
//   - Same input context yields same decision.
// Boundary Closure:
//   Upstream: orchestrator admission stage.
//   Downstream: queue cancellation/coalescing actions.

using System;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class DefaultDatabaseConflictResolver : IDatabaseConflictResolver
	{
		public DatabaseConflictDecision Resolve(DatabaseConflictContext context)
		{
			if (context == null || context.Incoming == null)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.ShapeInvalid,
					DatabaseConflictResolutionAction.RejectIncoming,
					null,
					string.Empty,
					"Incoming request is required for conflict resolution.");
			}

			if (!context.Incoming.IsShapeValid(out string shapeError))
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.ShapeInvalid,
					DatabaseConflictResolutionAction.RejectIncoming,
					null,
					string.Empty,
					shapeError);
			}

			if (context.Incoming.Timeout.HasValue)
			{
				TimeSpan age = DateTime.UtcNow - context.Incoming.CreatedAtUtc;
				if (age > context.Incoming.Timeout.Value)
				{
					return new DatabaseConflictDecision(
						DatabaseConflictKind.TimeoutExpired,
						DatabaseConflictResolutionAction.RejectIncoming,
						null,
						string.Empty,
						"Incoming command timed out before admission.");
				}
			}

			if (context.Incoming.ExpectedVersion.HasValue
				&& context.Incoming.ExpectedVersion.Value != context.CurrentSnapshotVersion)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.VersionGateMismatch,
					DatabaseConflictResolutionAction.RejectIncoming,
					null,
					string.Empty,
					"ExpectedVersion does not match current snapshot version.");
			}

			if (context.Existing == null)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.None,
					DatabaseConflictResolutionAction.AllowIncoming,
					context.Incoming,
					string.Empty,
					"No existing command conflict detected.");
			}

			bool sameStream = !string.IsNullOrWhiteSpace(context.Incoming.StreamKey)
				&& string.Equals(context.Incoming.StreamKey, context.Existing.StreamKey, StringComparison.Ordinal);

			if (!sameStream)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.None,
					DatabaseConflictResolutionAction.AllowIncoming,
					context.Incoming,
					string.Empty,
					"Existing command is not in the same stream.");
			}

			if (context.Incoming.Priority > context.Existing.Priority)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.PriorityPreemption,
					DatabaseConflictResolutionAction.CancelExistingAndAllowIncoming,
					context.Incoming,
					context.Existing.CommandId,
					"Incoming command preempts existing command by higher priority.");
			}

			if (context.Incoming.Priority < context.Existing.Priority)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.PriorityPreemption,
					DatabaseConflictResolutionAction.KeepExistingAndRejectIncoming,
					null,
					context.Existing.CommandId,
					"Existing command keeps ownership due to higher priority.");
			}

			if (context.Incoming.CreatedAtUtc >= context.Existing.CreatedAtUtc)
			{
				return new DatabaseConflictDecision(
					DatabaseConflictKind.StreamSuperseded,
					DatabaseConflictResolutionAction.CancelExistingAndAllowIncoming,
					context.Incoming,
					context.Existing.CommandId,
					"Incoming command supersedes older command in the same stream.");
			}

			return new DatabaseConflictDecision(
				DatabaseConflictKind.StreamDuplicate,
				DatabaseConflictResolutionAction.KeepExistingAndRejectIncoming,
				null,
				context.Existing.CommandId,
				"Existing command remains active for the stream.");
		}
	}
}
