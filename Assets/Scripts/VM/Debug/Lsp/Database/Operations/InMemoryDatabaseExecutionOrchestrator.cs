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
			var incomingRequest = input.Request;
			var effectiveRequest = incomingRequest;

			DatabaseTaskPlan plannedTaskPlan = null;
			DatabaseTaskEnqueueResult enqueueResult = null;
			DatabaseTaskExecutionReport executionReport = null;
			CodeDatabaseSnapshot nextSnapshot = currentSnapshot;

			var trace = new List<DatabaseExecutionTraceEntry>();
			bool canProceed = true;
			string stopReason = string.Empty;

			// Stage 1: validate request envelope.
			if (incomingRequest == null)
			{
				canProceed = false;
				stopReason = "Operation request is required.";
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, false, stopReason);
			}
			else if (!incomingRequest.IsShapeValid(out string validationError))
			{
				canProceed = false;
				stopReason = validationError;
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, false, stopReason);
			}
			else
			{
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, true, "Request shape validation passed.");
			}

			// Stage 2: validate version gate.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ValidateVersionGate, false, "Skipped due to previous stage failure.");
			}
			else if (effectiveRequest.ExpectedVersion.HasValue && effectiveRequest.ExpectedVersion.Value != currentSnapshot.Version)
			{
				canProceed = false;
				stopReason = "ExpectedVersion does not match current snapshot version.";
				AddTrace(trace, DatabaseExecutionStage.ValidateVersionGate, false, stopReason);
			}
			else
			{
				AddTrace(trace, DatabaseExecutionStage.ValidateVersionGate, true, "Version gate validation passed.");
			}

			// Stage 3: high-frequency admission (coalesce/supersede hooks).
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.HighFrequencyAdmission, false, "Skipped due to previous stage failure.");
			}
			else
			{
				bool admissionSucceeded;
				string admissionMessage;
				effectiveRequest = ApplyHighFrequencyAdmission(input, effectiveRequest, out admissionSucceeded, out admissionMessage);
				AddTrace(trace, DatabaseExecutionStage.HighFrequencyAdmission, admissionSucceeded, admissionMessage);

				if (!admissionSucceeded)
				{
					canProceed = false;
					stopReason = admissionMessage;
				}
			}

			// Stage 4: task planning.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.PlanTasks, false, "Skipped due to previous stage failure.");
			}
			else if (input.TaskPlanner == null)
			{
				canProceed = false;
				stopReason = "Task planner is required in orchestrator scaffold wiring.";
				AddTrace(trace, DatabaseExecutionStage.PlanTasks, false, stopReason);
			}
			else
			{
				plannedTaskPlan = input.TaskPlanner.Plan(currentSnapshot, effectiveRequest);
				if (plannedTaskPlan == null)
				{
					canProceed = false;
					stopReason = "Task planner returned null plan.";
					AddTrace(trace, DatabaseExecutionStage.PlanTasks, false, stopReason);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.PlanTasks, true, "Task plan created.");
				}
			}

			// Stage 5: enqueue tasks.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, "Skipped due to previous stage failure.");
			}
			else if (input.TaskCenter == null)
			{
				canProceed = false;
				stopReason = "Task center is required for enqueue stage.";
				AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, stopReason);
			}
			else
			{
				enqueueResult = input.TaskCenter.Enqueue(plannedTaskPlan, effectiveRequest);
				if (enqueueResult == null || !enqueueResult.Accepted)
				{
					canProceed = false;
					stopReason = enqueueResult != null
						? enqueueResult.Message
						: "Task enqueue failed: null enqueue result.";
					AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, stopReason);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, true, "Task enqueue accepted.");
				}
			}

			// Stage 6: execute tasks.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, false, "Skipped due to previous stage failure.");
			}
			else
			{
				executionReport = input.TaskCenter.Execute(plannedTaskPlan, effectiveRequest);
				if (executionReport == null || !executionReport.Succeeded)
				{
					canProceed = false;
					stopReason = executionReport != null
						? executionReport.Message
						: "Task execution failed: null execution report.";
					AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, false, stopReason);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, true, "Task execution report succeeded.");
				}
			}

			// Stage 7: compose next snapshot (intentionally not implemented).
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, false, "Skipped due to previous stage failure.");
			}
			else
			{
				canProceed = false;
				stopReason = "ComposeSnapshot is intentionally left unimplemented in scaffold.";
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, false, stopReason);
			}

			// Stage 8: commit snapshot.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, "Skipped due to previous stage failure.");
			}
			else if (input.SnapshotCommitter == null)
			{
				canProceed = false;
				stopReason = "Snapshot committer is required for commit stage.";
				AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, stopReason);
			}
			else
			{
				CodeDatabaseSnapshot committedSnapshot;
				string commitError;
				bool committed = input.SnapshotCommitter.TryCommit(
					currentSnapshot,
					nextSnapshot,
					effectiveRequest,
					out committedSnapshot,
					out commitError);

				if (!committed || committedSnapshot == null)
				{
					canProceed = false;
					stopReason = string.IsNullOrEmpty(commitError)
						? "Snapshot commit failed in scaffold wiring."
						: commitError;
					AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, stopReason);
				}
				else
				{
					nextSnapshot = committedSnapshot;
					AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, true, "Snapshot commit succeeded.");
				}
			}

			// Stage 9: build operation result.
			var finalMessage = canProceed
				? "Execution orchestrator scaffold completed all stages."
				: (string.IsNullOrEmpty(stopReason)
					? "Execution orchestrator scaffold stopped before completion."
					: stopReason);

			var operationResult = canProceed
				? DatabaseOperationResult.Success(
					effectiveRequest,
					currentSnapshot.Version,
					nextSnapshot.Version,
					nextSnapshot,
					finalMessage)
				: DatabaseOperationResult.Failure(
					effectiveRequest,
					currentSnapshot.Version,
					currentSnapshot,
					finalMessage);

			AddTrace(trace, DatabaseExecutionStage.BuildOperationResult, true, "Operation result created.");

			return new DatabaseExecutionOutcome(
				operationResult,
				plannedTaskPlan,
				enqueueResult,
				executionReport,
				nextSnapshot,
				trace);
		}

		private static void AddTrace(
			ICollection<DatabaseExecutionTraceEntry> trace,
			DatabaseExecutionStage stage,
			bool succeeded,
			string message)
		{
			trace.Add(new DatabaseExecutionTraceEntry(
				stage,
				succeeded,
				message,
				DateTime.UtcNow));
		}

		private static bool IsHighFrequencyCandidate(DatabaseOperationRequest request)
		{
			return request != null
				&& request.Kind == DatabaseOperationKind.ApplyChangeSet
				&& request.StreamBehavior != DatabaseOperationStreamBehavior.None
				&& !string.IsNullOrWhiteSpace(request.StreamKey);
		}

		private static DatabaseOperationRequest ApplyHighFrequencyAdmission(
			DatabaseExecutionInput input,
			DatabaseOperationRequest incoming,
			out bool succeeded,
			out string message)
		{
			succeeded = true;
			message = "No high-frequency admission action.";

			if (!IsHighFrequencyCandidate(incoming))
				return incoming;

			if (input.TaskCenter == null)
			{
				succeeded = false;
				message = "High-frequency admission requires task center when stream behavior is enabled.";
				return incoming;
			}

			DatabaseOperationRequest existingPending;
			bool hasExisting = input.TaskCenter.TryGetLatestPending(incoming.StreamKey, out existingPending);

			var effective = incoming;
			if (hasExisting && existingPending != null && input.OperationCoalescer != null)
			{
				bool canCoalesce = input.OperationCoalescer.CanCoalesce(existingPending, incoming);
				if (canCoalesce)
				{
					DatabaseCoalesceResult coalesce = input.OperationCoalescer.Coalesce(existingPending, incoming);
					if (coalesce != null)
					{
						switch (coalesce.Decision)
						{
							case DatabaseCoalesceDecision.KeepExisting:
								succeeded = false;
								message = "Incoming command skipped by high-frequency coalesce policy (keep existing).";
								return incoming;

							case DatabaseCoalesceDecision.MergeIntoNew:
								effective = coalesce.MergedRequest ?? incoming;
								message = string.IsNullOrEmpty(coalesce.Message)
									? "High-frequency coalesce merged into incoming command."
									: coalesce.Message;
								break;

							case DatabaseCoalesceDecision.ReplaceExisting:
							case DatabaseCoalesceDecision.None:
							default:
								message = string.IsNullOrEmpty(coalesce.Message)
									? "High-frequency coalesce evaluated with no structural merge."
									: coalesce.Message;
								break;
						}
					}
				}
			}

			if (effective.StreamBehavior == DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded)
			{
				int canceledCount = input.TaskCenter.CancelSuperseded(
					effective.StreamKey,
					effective.CommandId,
					"Superseded by incoming stream command.");

				message = message + " Canceled superseded pending commands: " + canceledCount + ".";
			}

			if (hasExisting && existingPending != null && input.SupersessionPolicy != null)
			{
				bool shouldCancel = input.SupersessionPolicy.ShouldCancelExisting(existingPending, effective);
				if (shouldCancel)
				{
					int canceledByPolicy = input.TaskCenter.CancelSuperseded(
						effective.StreamKey,
						effective.CommandId,
						"Canceled by supersession policy.");

					message = message + " Policy canceled superseded pending commands: " + canceledByPolicy + ".";
				}
			}

			return effective;
		}
	}
}
