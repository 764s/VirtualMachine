// Responsibility:
//   In-memory scaffold implementation of the database execution orchestrator.
// Owns:
//   Stable stage order and outcome envelope shape for future concrete logic.
// Inputs/Outputs:
//   In: DatabaseExecutionInput from the single database entrypoint.
//   Out: DatabaseExecutionOutcome with stage traces and placeholder failure result.
// Allowed Dependencies:
//   - IDatabaseExecutionOrchestrator
//   - DatabaseExecutionInput / DatabaseExecutionOutcome
// Forbidden Dependencies:
//   - Protocol adapters and handler-specific branching.
//   - Hidden write entrypoints bypassing IWorkspaceCodeDatabase.Execute.
// Invariants:
//   - Stage order remains fixed even before business logic is implemented.
//   - No semantic processing is performed in this scaffold.
// Boundary Closure:
//   Upstream: IWorkspaceCodeDatabase.Execute.
//   Downstream: future concrete planner/task-center/commit operations.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryDatabaseExecutionOrchestrator : IDatabaseExecutionOrchestrator
	{
		public DatabaseExecutionOutcome Execute(DatabaseExecutionInput input)
		{
			if (input == null)
				throw new ArgumentNullException(nameof(input));

			var currentSnapshot = input.CurrentSnapshot ?? CodeDatabaseSnapshot.Empty();
			var request = input.Request;

			// Stage declarations are intentionally explicit and ordered.
			var trace = new List<DatabaseExecutionTraceEntry>
			{
				CreateTrace(DatabaseExecutionStage.ValidateRequest),
				CreateTrace(DatabaseExecutionStage.ValidateVersionGate),
				CreateTrace(DatabaseExecutionStage.HighFrequencyAdmission),
				CreateTrace(DatabaseExecutionStage.PlanTasks),
				CreateTrace(DatabaseExecutionStage.EnqueueTasks),
				CreateTrace(DatabaseExecutionStage.ExecuteTasks),
				CreateTrace(DatabaseExecutionStage.ComposeSnapshot),
				CreateTrace(DatabaseExecutionStage.CommitSnapshot),
				CreateTrace(DatabaseExecutionStage.BuildOperationResult)
			};

			var operationResult = DatabaseOperationResult.Failure(
				request,
				currentSnapshot.Version,
				currentSnapshot,
				"Execution orchestrator scaffold: stage pipeline is fixed, business logic not implemented.");

			return new DatabaseExecutionOutcome(
				operationResult,
				null,
				null,
				null,
				currentSnapshot,
				trace);
		}

		private static DatabaseExecutionTraceEntry CreateTrace(DatabaseExecutionStage stage)
		{
			return new DatabaseExecutionTraceEntry(
				stage,
				false,
				"Stage declared by scaffold; no business execution yet.",
				DateTime.UtcNow);
		}
	}
}
