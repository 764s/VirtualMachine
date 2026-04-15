// Responsibility:
//   Command conflict-resolution contracts for operation admission and arbitration.
// Owns:
//   Conflict taxonomy, resolution actions, context and decision envelopes.
// Inputs/Outputs:
//   In: incoming request + existing request + runtime context.
//   Out: deterministic conflict decision and optional merged request.
// Allowed Dependencies:
//   - DatabaseOperationRequest
//   - DatabaseExecutionStage
//   - HighFrequencyScenarioKind
// Forbidden Dependencies:
//   - Protocol layer behavior and feature-level semantic logic.
// Invariants:
//   - Conflict decisions are explicit and reproducible.
//   - Resolver does not mutate requests in-place.
// Boundary Closure:
//   Upstream: execution orchestrator admission stage.
//   Downstream: task center enqueue/cancel and lifecycle projection.

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseConflictKind
	{
		None = 0,
		VersionGateMismatch,
		StreamDuplicate,
		StreamSuperseded,
		PriorityPreemption,
		TimeoutExpired,
		ShapeInvalid,
		Unknown
	}

	public enum DatabaseConflictResolutionAction
	{
		None = 0,
		AllowIncoming,
		RejectIncoming,
		KeepExistingAndRejectIncoming,
		CancelExistingAndAllowIncoming,
		CoalesceIntoIncoming,
		QueueIncoming
	}

	public sealed class DatabaseConflictContext
	{
		public DatabaseOperationRequest Incoming { get; }
		public DatabaseOperationRequest Existing { get; }
		public long CurrentSnapshotVersion { get; }
		public DatabaseExecutionStage Stage { get; }
		public HighFrequencyScenarioKind Scenario { get; }

		public DatabaseConflictContext(
			DatabaseOperationRequest incoming,
			DatabaseOperationRequest existing,
			long currentSnapshotVersion,
			DatabaseExecutionStage stage,
			HighFrequencyScenarioKind scenario)
		{
			Incoming = incoming;
			Existing = existing;
			CurrentSnapshotVersion = currentSnapshotVersion;
			Stage = stage;
			Scenario = scenario;
		}
	}

	public sealed class DatabaseConflictDecision
	{
		public DatabaseConflictKind Kind { get; }
		public DatabaseConflictResolutionAction Action { get; }
		public DatabaseOperationRequest MergedIncoming { get; }
		public string ExistingCommandIdToCancel { get; }
		public string Message { get; }

		public DatabaseConflictDecision(
			DatabaseConflictKind kind,
			DatabaseConflictResolutionAction action,
			DatabaseOperationRequest mergedIncoming,
			string existingCommandIdToCancel,
			string message)
		{
			Kind = kind;
			Action = action;
			MergedIncoming = mergedIncoming;
			ExistingCommandIdToCancel = existingCommandIdToCancel ?? string.Empty;
			Message = message ?? string.Empty;
		}
	}

	public interface IDatabaseConflictResolver
	{
		DatabaseConflictDecision Resolve(DatabaseConflictContext context);
	}
}
