// Responsibility:
//   In-memory scaffold implementation of the single database entrypoint.
// Owns:
//   Current snapshot reference and Execute-to-orchestrator handoff boundary.
// Inputs/Outputs:
//   In: DatabaseOperationRequest from external callers.
//   Out: DatabaseOperationResult produced by execution orchestrator.
// Allowed Dependencies:
//   - IDatabaseExecutionOrchestrator
//   - IDatabaseTaskPlanner
//   - IDatabaseTaskCenter
//   - IDatabaseOperationCoalescer
//   - IDatabaseSupersessionPolicy
//   - IDatabaseSnapshotCommitter
// Forbidden Dependencies:
//   - Direct protocol message handling.
//   - Secondary write entrypoints bypassing Execute.
// Invariants:
//   - All operations pass through request shape validation.
//   - Snapshot reference updates only from successful execution outcomes.
// Boundary Closure:
//   Upstream: handlers, adapters, and composition root.
//   Downstream: IDatabaseExecutionOrchestrator.

using System;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryWorkspaceCodeDatabase : IWorkspaceCodeDatabase
	{
		private readonly IDatabaseExecutionOrchestrator _orchestrator;
		private readonly IDatabaseTaskPlanner _taskPlanner;
		private readonly IDatabaseTaskCenter _taskCenter;
		private readonly IDatabaseOperationCoalescer _operationCoalescer;
		private readonly IDatabaseSupersessionPolicy _supersessionPolicy;
		private readonly IDatabaseConflictResolver _conflictResolver;
		private readonly IDatabaseCommandLifecyclePolicy _lifecyclePolicy;
		private readonly IDatabaseCommandLifecycleSink _lifecycleSink;
		private readonly IDatabaseDecisionLogSink _decisionLogSink;
		private readonly IDatabaseSnapshotCommitter _snapshotCommitter;

		private CodeDatabaseSnapshot _currentSnapshot;

		public DatabaseExecutionOutcome LastOutcome { get; private set; }

		public InMemoryWorkspaceCodeDatabase(
			IDatabaseExecutionOrchestrator orchestrator,
			IDatabaseTaskPlanner taskPlanner = null,
			IDatabaseTaskCenter taskCenter = null,
			IDatabaseOperationCoalescer operationCoalescer = null,
			IDatabaseSupersessionPolicy supersessionPolicy = null,
			IDatabaseConflictResolver conflictResolver = null,
			IDatabaseCommandLifecyclePolicy lifecyclePolicy = null,
			IDatabaseCommandLifecycleSink lifecycleSink = null,
			IDatabaseDecisionLogSink decisionLogSink = null,
			IDatabaseSnapshotCommitter snapshotCommitter = null)
		{
			_orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
			_taskPlanner = taskPlanner ?? new PassThroughDatabaseTaskPlanner();
			_taskCenter = taskCenter ?? new InMemoryDatabaseTaskCenter();
			_operationCoalescer = operationCoalescer ?? new DefaultDatabaseOperationCoalescer();
			_supersessionPolicy = supersessionPolicy ?? new DefaultDatabaseSupersessionPolicy();
			_conflictResolver = conflictResolver ?? new DefaultDatabaseConflictResolver();
			_lifecyclePolicy = lifecyclePolicy ?? new DefaultDatabaseCommandLifecyclePolicy();
			_lifecycleSink = lifecycleSink ?? new InMemoryDatabaseCommandLifecycleSink();
			_decisionLogSink = decisionLogSink ?? new InMemoryDatabaseDecisionLogSink();
			_snapshotCommitter = snapshotCommitter ?? new PassThroughDatabaseSnapshotCommitter();
			_currentSnapshot = CodeDatabaseSnapshot.Empty();
		}

		public DatabaseOperationResult Execute(DatabaseOperationRequest request)
		{
			if (request == null)
			{
				return DatabaseOperationResult.Failure(
					null,
					_currentSnapshot.Version,
					_currentSnapshot,
					"Database operation request is required.");
			}

			if (!request.IsShapeValid(out string shapeError))
			{
				return DatabaseOperationResult.Failure(
					request,
					_currentSnapshot.Version,
					_currentSnapshot,
					shapeError);
			}

			var input = new DatabaseExecutionInput(
				request,
				_currentSnapshot,
				_taskPlanner,
				_taskCenter,
				_operationCoalescer,
				_supersessionPolicy,
				_conflictResolver,
				_lifecyclePolicy,
				_lifecycleSink,
				_decisionLogSink,
				_snapshotCommitter,
				InferScenario(request));

			DatabaseExecutionOutcome outcome = _orchestrator.Execute(input);
			LastOutcome = outcome;

			if (outcome != null
				&& outcome.OperationResult != null
				&& outcome.OperationResult.Succeeded
				&& outcome.NextSnapshot != null)
			{
				_currentSnapshot = outcome.NextSnapshot;
			}

			if (outcome?.OperationResult != null)
				return outcome.OperationResult;

			return DatabaseOperationResult.Failure(
				request,
				_currentSnapshot.Version,
				_currentSnapshot,
				"Execution orchestrator returned no operation result.");
		}

		private static HighFrequencyScenarioKind InferScenario(DatabaseOperationRequest request)
		{
			if (request == null || request.Kind != DatabaseOperationKind.ApplyChangeSet)
				return HighFrequencyScenarioKind.Unknown;

			return string.IsNullOrWhiteSpace(request.StreamKey)
				? HighFrequencyScenarioKind.Unknown
				: HighFrequencyScenarioKind.DocumentDidChangeBurst;
		}
	}
}
