// Responsibility:
//   Internal task planning contracts for command execution inside the database.
// Owns:
//   Task taxonomy, task descriptors, and task plan envelope.
// Inputs/Outputs:
//   In: operation request + base snapshot.
//   Out: deterministic DatabaseTaskPlan for task center execution.
// Allowed Dependencies:
//   - DatabaseOperationRequest
//   - CodeDatabaseSnapshot
// Forbidden Dependencies:
//   - Protocol-layer payloads and transport concerns.
//   - Direct snapshot mutation logic.
// Invariants:
//   - One command maps to one task plan instance.
//   - Task identifiers are unique within one plan.
// Boundary Closure:
//   Upstream: IDatabaseExecutionOrchestrator.
//   Downstream: IDatabaseTaskCenter.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public abstract class DatabaseTaskPayload
	{
	}

	public sealed class ValidateOperationTaskPayload : DatabaseTaskPayload
	{
		public DatabaseOperationKind OperationKind { get; }
		public string StreamKey { get; }
		public long? ExpectedVersion { get; }
		public int ChangeCount { get; }

		public ValidateOperationTaskPayload(
			DatabaseOperationKind operationKind,
			string streamKey,
			long? expectedVersion,
			int changeCount)
		{
			OperationKind = operationKind;
			StreamKey = streamKey ?? string.Empty;
			ExpectedVersion = expectedVersion;
			ChangeCount = changeCount;
		}
	}

	public sealed class FinalizeOperationTaskPayload : DatabaseTaskPayload
	{
		public string FinalizationReason { get; }

		public FinalizeOperationTaskPayload(string finalizationReason)
		{
			FinalizationReason = finalizationReason ?? string.Empty;
		}
	}

	public enum DatabaseTaskKind
	{
		Unknown = 0,
		ValidateOperation,
		ValidateVersionGate,
		ResolveInputDocuments,
		BuildAggregates,
		ExtractFacts,
		RebuildIndexes,
		ComposeSnapshot,
		FinalizeOperation
	}

	public sealed class DatabaseTaskDescriptor
	{
		private static readonly IReadOnlyList<string> EmptyDependencies = new List<string>(0);

		public string TaskId { get; }
		public DatabaseTaskKind Kind { get; }
		public string Description { get; }
		public IReadOnlyList<string> DependsOnTaskIds { get; }
		public DatabaseTaskPayload Payload { get; }

		public DatabaseTaskDescriptor(
			string taskId,
			DatabaseTaskKind kind,
			string description,
			IReadOnlyList<string> dependsOnTaskIds,
			DatabaseTaskPayload payload)
		{
			TaskId = taskId ?? string.Empty;
			Kind = kind;
			Description = description ?? string.Empty;
			DependsOnTaskIds = dependsOnTaskIds ?? EmptyDependencies;
			Payload = payload;
		}
	}

	public sealed class DatabaseTaskPlan
	{
		private static readonly IReadOnlyList<DatabaseTaskDescriptor> EmptyTasks = new List<DatabaseTaskDescriptor>(0);

		public string PlanId { get; }
		public string CommandId { get; }
		public long BaseVersion { get; }
		public IReadOnlyList<DatabaseTaskDescriptor> Tasks { get; }
		public DateTime PlannedAtUtc { get; }

		public DatabaseTaskPlan(
			string planId,
			string commandId,
			long baseVersion,
			IReadOnlyList<DatabaseTaskDescriptor> tasks,
			DateTime plannedAtUtc)
		{
			PlanId = planId ?? string.Empty;
			CommandId = commandId ?? string.Empty;
			BaseVersion = baseVersion;
			Tasks = tasks ?? EmptyTasks;
			PlannedAtUtc = plannedAtUtc;
		}
	}

	public interface IDatabaseTaskPlanner
	{
		DatabaseTaskPlan Plan(CodeDatabaseSnapshot snapshot, DatabaseOperationRequest request);
	}
}
