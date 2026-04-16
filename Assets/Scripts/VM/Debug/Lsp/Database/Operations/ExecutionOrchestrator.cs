// Responsibility:
//   Command execution orchestration contract for the single database entrypoint.
// Owns:
//   Stage model, execution input/output envelopes, and snapshot commit boundary.
// Inputs/Outputs:
//   In: operation request + current snapshot + planner/task-center/policies.
//   Out: execution outcome including operation result and stage traces.
// Allowed Dependencies:
//   - DatabaseOperationRequest
//   - CodeDatabaseSnapshot
//   - IDatabaseTaskPlanner
//   - IDatabaseTaskCenter
//   - IDatabaseOperationCoalescer
//   - IDatabaseSupersessionPolicy
//   - IDatabaseConflictResolver
//   - IDatabaseCommandLifecyclePolicy
//   - IDatabaseCommandLifecycleSink
//   - IDatabaseDecisionLogSink
// Forbidden Dependencies:
//   - Protocol transport concerns.
//   - Direct handler-level branching logic.
// Invariants:
//   - Orchestrator does not create extra public write entrypoints.
//   - Commit can only happen through IDatabaseSnapshotCommitter.
// Boundary Closure:
//   Upstream: IWorkspaceCodeDatabase.Execute.
//   Downstream: task planning/center + snapshot committer.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseExecutionStage
	{
		Unknown = 0,
		ValidateRequest,
		ValidateVersionGate,
		HighFrequencyAdmission,
		PlanTasks,
		EnqueueTasks,
		ExecuteTasks,
		ComposeSnapshot,
		CommitSnapshot,
		BuildOperationResult
	}

	public sealed class DatabaseExecutionTraceEntry
	{
		public DatabaseExecutionStage Stage { get; }
		public bool Succeeded { get; }
		public string Message { get; }
		public DateTime TimestampUtc { get; }

		public DatabaseExecutionTraceEntry(
			DatabaseExecutionStage stage,
			bool succeeded,
			string message,
			DateTime timestampUtc)
		{
			Stage = stage;
			Succeeded = succeeded;
			Message = message ?? string.Empty;
			TimestampUtc = timestampUtc;
		}
	}

	public sealed class DatabaseExecutionInput
	{
		public DatabaseOperationRequest Request { get; }
		public CodeDatabaseSnapshot CurrentSnapshot { get; }
		public IIndexMaintainer IndexMaintainer { get; }
		public IDatabaseTaskPlanner TaskPlanner { get; }
		public IDatabaseTaskCenter TaskCenter { get; }
		public IDatabaseOperationCoalescer OperationCoalescer { get; }
		public IDatabaseSupersessionPolicy SupersessionPolicy { get; }
		public IDatabaseConflictResolver ConflictResolver { get; }
		public IDatabaseCommandLifecyclePolicy LifecyclePolicy { get; }
		public IDatabaseCommandLifecycleSink LifecycleSink { get; }
		public IDatabaseDecisionLogSink DecisionLogSink { get; }
		public IDatabaseSnapshotCommitter SnapshotCommitter { get; }
		public HighFrequencyScenarioKind Scenario { get; }

		public DatabaseExecutionInput(
			DatabaseOperationRequest request,
			CodeDatabaseSnapshot currentSnapshot,
			IIndexMaintainer indexMaintainer,
			IDatabaseTaskPlanner taskPlanner,
			IDatabaseTaskCenter taskCenter,
			IDatabaseOperationCoalescer operationCoalescer,
			IDatabaseSupersessionPolicy supersessionPolicy,
			IDatabaseConflictResolver conflictResolver,
			IDatabaseCommandLifecyclePolicy lifecyclePolicy,
			IDatabaseCommandLifecycleSink lifecycleSink,
			IDatabaseDecisionLogSink decisionLogSink,
			IDatabaseSnapshotCommitter snapshotCommitter,
			HighFrequencyScenarioKind scenario)
		{
			Request = request;
			CurrentSnapshot = currentSnapshot;
			IndexMaintainer = indexMaintainer;
			TaskPlanner = taskPlanner;
			TaskCenter = taskCenter;
			OperationCoalescer = operationCoalescer;
			SupersessionPolicy = supersessionPolicy;
			ConflictResolver = conflictResolver;
			LifecyclePolicy = lifecyclePolicy;
			LifecycleSink = lifecycleSink;
			DecisionLogSink = decisionLogSink;
			SnapshotCommitter = snapshotCommitter;
			Scenario = scenario;
		}
	}

	public sealed class DatabaseExecutionOutcome
	{
		private static readonly IReadOnlyList<DatabaseExecutionTraceEntry> EmptyTrace
			= new List<DatabaseExecutionTraceEntry>(0);
		private static readonly IReadOnlyList<DatabaseConflictDecision> EmptyConflicts
			= new List<DatabaseConflictDecision>(0);
		private static readonly IReadOnlyList<DatabaseDecisionLogEntry> EmptyDecisions
			= new List<DatabaseDecisionLogEntry>(0);

		public DatabaseOperationResult OperationResult { get; }
		public DatabaseTaskPlan PlannedTaskPlan { get; }
		public DatabaseTaskEnqueueResult EnqueueResult { get; }
		public DatabaseTaskExecutionReport ExecutionReport { get; }
		public CodeDatabaseSnapshot NextSnapshot { get; }
		public DatabaseCommandLifecycleTrace LifecycleTrace { get; }
		public IReadOnlyList<DatabaseConflictDecision> ConflictDecisions { get; }
		public IReadOnlyList<DatabaseDecisionLogEntry> DecisionEntries { get; }
		public IReadOnlyList<DatabaseExecutionTraceEntry> Trace { get; }

		public DatabaseExecutionOutcome(
			DatabaseOperationResult operationResult,
			DatabaseTaskPlan plannedTaskPlan,
			DatabaseTaskEnqueueResult enqueueResult,
			DatabaseTaskExecutionReport executionReport,
			CodeDatabaseSnapshot nextSnapshot,
			DatabaseCommandLifecycleTrace lifecycleTrace,
			IReadOnlyList<DatabaseConflictDecision> conflictDecisions,
			IReadOnlyList<DatabaseDecisionLogEntry> decisionEntries,
			IReadOnlyList<DatabaseExecutionTraceEntry> trace)
		{
			OperationResult = operationResult;
			PlannedTaskPlan = plannedTaskPlan;
			EnqueueResult = enqueueResult;
			ExecutionReport = executionReport;
			NextSnapshot = nextSnapshot;
			LifecycleTrace = lifecycleTrace;
			ConflictDecisions = conflictDecisions ?? EmptyConflicts;
			DecisionEntries = decisionEntries ?? EmptyDecisions;
			Trace = trace ?? EmptyTrace;
		}
	}

	public interface IDatabaseSnapshotCommitter
	{
		bool TryCommit(
			CodeDatabaseSnapshot currentSnapshot,
			CodeDatabaseSnapshot nextSnapshot,
			DatabaseOperationRequest request,
			out CodeDatabaseSnapshot committedSnapshot,
			out string error);
	}

	public interface IDatabaseExecutionOrchestrator
	{
		DatabaseExecutionOutcome Execute(DatabaseExecutionInput input);
	}
}
