// Responsibility:
//   Pass-through task planner scaffold for operation-to-task projection.
// Owns:
//   Minimal deterministic task graph generation for a single command.
// Inputs/Outputs:
//   In: current snapshot and operation request.
//   Out: task plan with validate/finalize placeholders.
// Allowed Dependencies:
//   - IDatabaseTaskPlanner
// Forbidden Dependencies:
//   - Runtime queueing/execution concerns.
// Invariants:
//   - Always returns a non-null plan for a non-null request.
// Boundary Closure:
//   Upstream: orchestrator planning stage.
//   Downstream: task center enqueue/execute.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class PassThroughDatabaseTaskPlanner : IDatabaseTaskPlanner
	{
		public DatabaseTaskPlan Plan(CodeDatabaseSnapshot snapshot, DatabaseOperationRequest request)
		{
			string commandId = request?.CommandId ?? string.Empty;
			string planId = string.IsNullOrEmpty(commandId)
				? "plan-" + Guid.NewGuid().ToString("N")
				: "plan-" + commandId;

			var tasks = new List<DatabaseTaskDescriptor>
			{
				new DatabaseTaskDescriptor(
					"task-validate",
					DatabaseTaskKind.ValidateOperation,
					"Validate operation shape and admission assumptions.",
					null,
					new ValidateOperationTaskPayload(
						request != null ? request.Kind : DatabaseOperationKind.Unknown,
						request != null ? request.StreamKey : string.Empty,
						request != null ? request.ExpectedVersion : null,
						request != null && request.Changes != null ? request.Changes.Count : 0)),
				new DatabaseTaskDescriptor(
					"task-finalize",
					DatabaseTaskKind.FinalizeOperation,
					"Finalize operation lifecycle state.",
					new[] { "task-validate" },
					new FinalizeOperationTaskPayload("Finalize operation lifecycle state.")),
			};

			long version = snapshot?.Version ?? CodeDatabaseSnapshot.Empty().Version;
			return new DatabaseTaskPlan(planId, commandId, version, tasks, DateTime.UtcNow);
		}
	}
}
