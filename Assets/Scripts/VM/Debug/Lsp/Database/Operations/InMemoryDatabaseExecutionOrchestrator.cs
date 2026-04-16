// Responsibility:
//   In-memory scaffold implementation of the database execution orchestrator.
// Owns:
//   Stage order, lifecycle transitions, conflict admission wiring, and decision logs.
// Inputs/Outputs:
//   In: DatabaseExecutionInput from the single database entrypoint.
//   Out: DatabaseExecutionOutcome with pass-through scaffold behavior and observability artifacts.
// Allowed Dependencies:
//   - IDatabaseExecutionOrchestrator
//   - DatabaseExecutionInput / DatabaseExecutionOutcome
// Forbidden Dependencies:
//   - Protocol adapters and handler-specific branching.
//   - Hidden write entrypoints bypassing IWorkspaceCodeDatabase.Execute.
// Invariants:
//   - Stage order remains fixed in scaffold mode.
//   - No feature-level semantic processing is executed.
// Boundary Closure:
//   Upstream: IWorkspaceCodeDatabase.Execute.
//   Downstream: planner/task-center/commit hooks and observability sinks.

using System;
using System.Collections.Generic;
using FFVM.Debug;
using DocumentKeyNormalizer = FFVM.Debug.Lsp.Database.Paths.DocumentKeyNormalizer;
using PathKey = FFVM.Debug.Lsp.Database.Paths.PathKey;

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
			var transitions = new List<DatabaseCommandStateTransition>();
			var conflicts = new List<DatabaseConflictDecision>();
			var decisions = new List<DatabaseDecisionLogEntry>();

			bool canProceed = true;
			string stopReason = string.Empty;
			var currentState = DatabaseCommandState.Created;
			var failureState = DatabaseCommandState.Failed;

			WriteDecision(
				input,
				decisions,
				incomingRequest,
				DatabaseExecutionStage.ValidateRequest,
				DatabaseDecisionCategory.Lifecycle,
				DatabaseDecisionSeverity.Info,
				"CMD_CREATED",
				"Command entered orchestrator with Created state.",
				null);

			// Stage 1: validate request envelope.
			if (incomingRequest == null)
			{
				canProceed = false;
				stopReason = "Operation request is required.";
				failureState = DatabaseCommandState.Rejected;
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, false, stopReason);
				WriteDecision(input, decisions, incomingRequest, DatabaseExecutionStage.ValidateRequest, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Error, "VALIDATION_REQUEST_NULL", stopReason, null);
			}
			else if (!incomingRequest.IsShapeValid(out string validationError))
			{
				canProceed = false;
				stopReason = validationError;
				failureState = DatabaseCommandState.Rejected;
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, false, stopReason);
				WriteDecision(input, decisions, incomingRequest, DatabaseExecutionStage.ValidateRequest, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Error, "VALIDATION_SHAPE_FAILED", stopReason, null);

				TryTransition(
					input,
					transitions,
					incomingRequest,
					ref currentState,
					DatabaseExecutionStage.ValidateRequest,
					DatabaseCommandState.Rejected,
					DatabaseCommandTransitionReason.ValidationFailed,
					stopReason,
					out _);
			}
			else
			{
				AddTrace(trace, DatabaseExecutionStage.ValidateRequest, true, "Request shape validation passed.");
				WriteDecision(input, decisions, incomingRequest, DatabaseExecutionStage.ValidateRequest, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Info, "VALIDATION_SHAPE_PASSED", "Request shape validation passed.", null);

				if (!TryTransition(
					input,
					transitions,
					incomingRequest,
					ref currentState,
					DatabaseExecutionStage.ValidateRequest,
					DatabaseCommandState.Admitted,
					DatabaseCommandTransitionReason.ValidationPassed,
					"Request admitted after validation.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.ValidateRequest, false, stopReason);
					WriteDecision(input, decisions, incomingRequest, DatabaseExecutionStage.ValidateRequest, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Error, "LIFECYCLE_TRANSITION_FAILED", stopReason, null);
				}
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
				failureState = DatabaseCommandState.Rejected;
				AddTrace(trace, DatabaseExecutionStage.ValidateVersionGate, false, stopReason);
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ValidateVersionGate, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Warning, "VERSION_GATE_MISMATCH", stopReason, DatabaseDecisionPayload.ForVersionGate(effectiveRequest.ExpectedVersion, currentSnapshot.Version));

				TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.ValidateVersionGate,
					DatabaseCommandState.Rejected,
					DatabaseCommandTransitionReason.VersionGateFailed,
					stopReason,
					out _);
			}
			else
			{
				AddTrace(trace, DatabaseExecutionStage.ValidateVersionGate, true, "Version gate validation passed.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ValidateVersionGate, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Info, "VERSION_GATE_PASSED", "Version gate validation passed.", null);
			}

			// Stage 3: high-frequency admission.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.HighFrequencyAdmission, false, "Skipped due to previous stage failure.");
			}
			else
			{
				bool admissionSucceeded;
				string admissionMessage;
				effectiveRequest = ApplyHighFrequencyAdmission(
					input,
					effectiveRequest,
					currentSnapshot.Version,
					conflicts,
					decisions,
					out admissionSucceeded,
					out admissionMessage);

				AddTrace(trace, DatabaseExecutionStage.HighFrequencyAdmission, admissionSucceeded, admissionMessage);

				if (!admissionSucceeded)
				{
					canProceed = false;
					stopReason = admissionMessage;
					failureState = DatabaseCommandState.Rejected;

					TryTransition(
						input,
						transitions,
						effectiveRequest,
						ref currentState,
						DatabaseExecutionStage.HighFrequencyAdmission,
						DatabaseCommandState.Rejected,
						DatabaseCommandTransitionReason.AdmissionRejected,
						stopReason,
						out _);
				}
				else
				{
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Admission, DatabaseDecisionSeverity.Info, "ADMISSION_PASSED", admissionMessage, null);
				}
			}

			// Stage 4: task planning.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.PlanTasks, false, "Skipped due to previous stage failure.");
			}
			else if (input.TaskPlanner == null)
			{
				plannedTaskPlan = CreateFallbackPlan(currentSnapshot, effectiveRequest, "No planner wired; using fallback no-op plan.");
				AddTrace(trace, DatabaseExecutionStage.PlanTasks, true, "No planner wired; fallback no-op plan created.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.PlanTasks, DatabaseDecisionCategory.Planning, DatabaseDecisionSeverity.Warning, "PLAN_FALLBACK", "No planner wired; fallback no-op plan created.", null);
			}
			else
			{
				plannedTaskPlan = input.TaskPlanner.Plan(currentSnapshot, effectiveRequest);
				if (plannedTaskPlan == null)
				{
					plannedTaskPlan = CreateFallbackPlan(currentSnapshot, effectiveRequest, "Task planner returned null plan; fallback no-op plan used.");
					AddTrace(trace, DatabaseExecutionStage.PlanTasks, true, "Task planner returned null; fallback no-op plan created.");
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.PlanTasks, DatabaseDecisionCategory.Planning, DatabaseDecisionSeverity.Warning, "PLAN_NULL_FALLBACK", "Task planner returned null; fallback no-op plan created.", null);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.PlanTasks, true, "Task plan created.");
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.PlanTasks, DatabaseDecisionCategory.Planning, DatabaseDecisionSeverity.Info, "PLAN_CREATED", "Task plan created.", DatabaseDecisionPayload.ForPlanIdentity(plannedTaskPlan.PlanId));
				}
			}

			if (canProceed)
			{
				if (!TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.PlanTasks,
					DatabaseCommandState.Planned,
					DatabaseCommandTransitionReason.Planned,
					"Task planning stage completed.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.PlanTasks, false, stopReason);
				}
			}

			// Stage 5: enqueue tasks.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, "Skipped due to previous stage failure.");
			}
			else if (input.TaskCenter == null)
			{
				enqueueResult = CreateBypassEnqueueResult(effectiveRequest, "No task center wired; enqueue stage bypassed.");
				AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, true, "No task center wired; enqueue stage bypassed.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.EnqueueTasks, DatabaseDecisionCategory.Queueing, DatabaseDecisionSeverity.Warning, "QUEUE_BYPASS", "No task center wired; enqueue stage bypassed.", null);
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
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, stopReason);
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.EnqueueTasks, DatabaseDecisionCategory.Queueing, DatabaseDecisionSeverity.Error, "QUEUE_FAILED", stopReason, null);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, true, "Task enqueue accepted.");
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.EnqueueTasks, DatabaseDecisionCategory.Queueing, DatabaseDecisionSeverity.Info, "QUEUE_ACCEPTED", "Task enqueue accepted.", DatabaseDecisionPayload.ForQueueAcceptance(enqueueResult.Disposition, enqueueResult.QueueTicket));
				}
			}

			if (canProceed)
			{
				if (!TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.EnqueueTasks,
					DatabaseCommandState.Enqueued,
					DatabaseCommandTransitionReason.Enqueued,
					"Enqueue stage completed.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.EnqueueTasks, false, stopReason);
				}
			}

			// Stage 6: execute tasks.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, false, "Skipped due to previous stage failure.");
			}
			else
			{
				if (!TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.ExecuteTasks,
					DatabaseCommandState.Executing,
					DatabaseCommandTransitionReason.ExecutionStarted,
					"Execution stage started.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, false, stopReason);
				}
			}

			if (canProceed && input.TaskCenter == null)
			{
				executionReport = CreateBypassExecutionReport(effectiveRequest, "No task center wired; execute stage bypassed.");
				AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, true, "No task center wired; execute stage bypassed.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ExecuteTasks, DatabaseDecisionCategory.Execution, DatabaseDecisionSeverity.Warning, "EXECUTE_BYPASS", "No task center wired; execute stage bypassed.", null);
			}
			else if (canProceed)
			{
				executionReport = input.TaskCenter.Execute(plannedTaskPlan, effectiveRequest);
				if (executionReport == null || !executionReport.Succeeded)
				{
					canProceed = false;
					stopReason = executionReport != null
						? executionReport.Message
						: "Task execution failed: null execution report.";
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, false, stopReason);
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ExecuteTasks, DatabaseDecisionCategory.Execution, DatabaseDecisionSeverity.Error, "EXECUTE_FAILED", stopReason, null);

					TryTransition(
						input,
						transitions,
						effectiveRequest,
						ref currentState,
						DatabaseExecutionStage.ExecuteTasks,
						DatabaseCommandState.Failed,
						DatabaseCommandTransitionReason.ExecutionFailed,
						stopReason,
						out _);
				}
				else
				{
					AddTrace(trace, DatabaseExecutionStage.ExecuteTasks, true, "Task execution report succeeded.");
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ExecuteTasks, DatabaseDecisionCategory.Execution, DatabaseDecisionSeverity.Info, "EXECUTE_SUCCEEDED", "Task execution report succeeded.", null);
				}
			}

			// Stage 7: compose next snapshot.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, false, "Skipped due to previous stage failure.");
			}
			else
			{
				nextSnapshot = ComposeSnapshot(currentSnapshot, executionReport, effectiveRequest, input.IndexMaintainer);
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, true, "ComposeSnapshot completed.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ComposeSnapshot, DatabaseDecisionCategory.Compose, DatabaseDecisionSeverity.Info, "COMPOSE_COMPLETED", "ComposeSnapshot completed.", DatabaseDecisionPayload.ForSnapshotVersion(nextSnapshot != null ? nextSnapshot.Version : 0L));

				if (!TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.ComposeSnapshot,
					DatabaseCommandState.Composed,
					DatabaseCommandTransitionReason.ComposeSucceeded,
					"Compose stage completed.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, false, stopReason);
				}
			}

			// Stage 8: commit snapshot.
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, "Skipped due to previous stage failure.");
			}
			else if (input.SnapshotCommitter == null)
			{
				AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, true, "No snapshot committer wired; commit stage pass-through.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.CommitSnapshot, DatabaseDecisionCategory.Commit, DatabaseDecisionSeverity.Warning, "COMMIT_BYPASS", "No snapshot committer wired; commit stage pass-through.", null);

				if (!TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.CommitSnapshot,
					DatabaseCommandState.Committed,
					DatabaseCommandTransitionReason.CommitSucceeded,
					"Commit stage bypassed as successful pass-through.",
					out string transitionError))
				{
					canProceed = false;
					stopReason = transitionError;
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, stopReason);
				}
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
					failureState = DatabaseCommandState.Failed;
					AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, stopReason);
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.CommitSnapshot, DatabaseDecisionCategory.Commit, DatabaseDecisionSeverity.Error, "COMMIT_FAILED", stopReason, null);

					TryTransition(
						input,
						transitions,
						effectiveRequest,
						ref currentState,
						DatabaseExecutionStage.CommitSnapshot,
						DatabaseCommandState.Failed,
						DatabaseCommandTransitionReason.CommitFailed,
						stopReason,
						out _);
				}
				else
				{
					nextSnapshot = committedSnapshot;
					AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, true, "Snapshot commit succeeded.");
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.CommitSnapshot, DatabaseDecisionCategory.Commit, DatabaseDecisionSeverity.Info, "COMMIT_SUCCEEDED", "Snapshot commit succeeded.", null);

					if (!TryTransition(
						input,
						transitions,
						effectiveRequest,
						ref currentState,
						DatabaseExecutionStage.CommitSnapshot,
						DatabaseCommandState.Committed,
						DatabaseCommandTransitionReason.CommitSucceeded,
						"Commit stage completed.",
						out string transitionError))
					{
						canProceed = false;
						stopReason = transitionError;
						failureState = DatabaseCommandState.Failed;
						AddTrace(trace, DatabaseExecutionStage.CommitSnapshot, false, stopReason);
					}
				}
			}

			// Stage 9: build operation result.
			if (canProceed)
			{
				TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.BuildOperationResult,
					DatabaseCommandState.Completed,
					DatabaseCommandTransitionReason.Completed,
					"Command completed successfully.",
					out _);
			}
			else if (!IsTerminalState(currentState))
			{
				DatabaseCommandTransitionReason failReason = failureState == DatabaseCommandState.Rejected
					? DatabaseCommandTransitionReason.Rejected
					: DatabaseCommandTransitionReason.Failed;

				TryTransition(
					input,
					transitions,
					effectiveRequest,
					ref currentState,
					DatabaseExecutionStage.BuildOperationResult,
					failureState,
					failReason,
					string.IsNullOrEmpty(stopReason) ? "Command terminated with failure state." : stopReason,
					out _);
			}

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
					finalMessage,
					currentState)
				: DatabaseOperationResult.Failure(
					effectiveRequest,
					currentSnapshot.Version,
					currentSnapshot,
					finalMessage,
					currentState);

			AddTrace(trace, DatabaseExecutionStage.BuildOperationResult, true, "Operation result created.");
			WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.BuildOperationResult, DatabaseDecisionCategory.Result, canProceed ? DatabaseDecisionSeverity.Info : DatabaseDecisionSeverity.Error, canProceed ? "RESULT_SUCCESS" : "RESULT_FAILURE", "Operation result created.", DatabaseDecisionPayload.ForResultState(currentState, canProceed));

			var lifecycleTrace = new DatabaseCommandLifecycleTrace(
				effectiveRequest?.CommandId,
				effectiveRequest?.CorrelationId,
				currentState,
				transitions);

			return new DatabaseExecutionOutcome(
				operationResult,
				plannedTaskPlan,
				enqueueResult,
				executionReport,
				nextSnapshot,
				lifecycleTrace,
				conflicts,
				decisions,
				trace);
		}

		private static bool TryTransition(
			DatabaseExecutionInput input,
			ICollection<DatabaseCommandStateTransition> transitions,
			DatabaseOperationRequest request,
			ref DatabaseCommandState currentState,
			DatabaseExecutionStage stage,
			DatabaseCommandState toState,
			DatabaseCommandTransitionReason reason,
			string message,
			out string error)
		{
			error = null;
			if (input != null && input.LifecyclePolicy != null)
			{
				if (!input.LifecyclePolicy.CanTransition(currentState, toState, out error))
					return false;
			}

			var transition = new DatabaseCommandStateTransition(
				request?.CommandId,
				request?.CorrelationId,
				stage,
				currentState,
				toState,
				reason,
				message,
				DateTime.UtcNow);

			transitions.Add(transition);
			if (input != null && input.LifecycleSink != null)
				input.LifecycleSink.Record(transition);

			currentState = toState;
			return true;
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

		private static void WriteDecision(
			DatabaseExecutionInput input,
			ICollection<DatabaseDecisionLogEntry> decisions,
			DatabaseOperationRequest request,
			DatabaseExecutionStage stage,
			DatabaseDecisionCategory category,
			DatabaseDecisionSeverity severity,
			string code,
			string message,
			DatabaseDecisionPayload payload)
		{
			var entry = new DatabaseDecisionLogEntry(
				request?.CommandId,
				request?.CorrelationId,
				stage,
				category,
				severity,
				code,
				message,
				DateTime.UtcNow,
				payload ?? DatabaseDecisionPayload.None);

			decisions.Add(entry);
			if (input != null && input.DecisionLogSink != null)
				input.DecisionLogSink.Write(entry);
		}

		private static bool IsTerminalState(DatabaseCommandState state)
		{
			return state == DatabaseCommandState.Completed
				|| state == DatabaseCommandState.Rejected
				|| state == DatabaseCommandState.Canceled
				|| state == DatabaseCommandState.TimedOut
				|| state == DatabaseCommandState.Failed;
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
			long currentSnapshotVersion,
			ICollection<DatabaseConflictDecision> conflicts,
			ICollection<DatabaseDecisionLogEntry> decisions,
			out bool succeeded,
			out string message)
		{
			succeeded = true;
			message = "No high-frequency admission action.";

			if (!IsHighFrequencyCandidate(incoming))
				return incoming;

			if (input.TaskCenter == null)
			{
				succeeded = true;
				message = "No task center wired; high-frequency admission skipped in pass-through mode.";
				return incoming;
			}

			DatabaseOperationRequest existingPending;
			bool hasExisting = input.TaskCenter.TryGetLatestPending(incoming.StreamKey, out existingPending);

			var effective = incoming;

			if (hasExisting && existingPending != null && input.ConflictResolver != null)
			{
				var context = new DatabaseConflictContext(
					effective,
					existingPending,
					currentSnapshotVersion,
					DatabaseExecutionStage.HighFrequencyAdmission,
					input.Scenario);

				var resolverDecision = input.ConflictResolver.Resolve(context);
				if (resolverDecision != null)
				{
					conflicts.Add(resolverDecision);

					switch (resolverDecision.Action)
					{
						case DatabaseConflictResolutionAction.RejectIncoming:
						case DatabaseConflictResolutionAction.KeepExistingAndRejectIncoming:
							succeeded = false;
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Incoming command rejected by conflict resolver."
								: resolverDecision.Message;
							WriteDecision(input, decisions, incoming, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Warning, "CONFLICT_REJECT_INCOMING", message, DatabaseDecisionPayload.ForConflictDecision(resolverDecision));
							return incoming;

						case DatabaseConflictResolutionAction.CoalesceIntoIncoming:
							effective = resolverDecision.MergedIncoming ?? incoming;
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Incoming command replaced by conflict-merged command."
								: resolverDecision.Message;
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_COALESCE", message, DatabaseDecisionPayload.ForConflictDecision(resolverDecision));
							break;

						case DatabaseConflictResolutionAction.CancelExistingAndAllowIncoming:
							input.TaskCenter.CancelSuperseded(
								effective.StreamKey,
								effective.CommandId,
								string.IsNullOrEmpty(resolverDecision.Message)
									? "Canceled existing by conflict resolver."
									: resolverDecision.Message);
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Conflict resolver canceled existing and accepted incoming."
								: resolverDecision.Message;
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_CANCEL_EXISTING", message, DatabaseDecisionPayload.ForConflictDecision(resolverDecision));
							break;

						case DatabaseConflictResolutionAction.AllowIncoming:
						case DatabaseConflictResolutionAction.QueueIncoming:
						case DatabaseConflictResolutionAction.None:
						default:
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Conflict resolver allowed incoming command."
								: resolverDecision.Message;
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_ALLOW_INCOMING", message, DatabaseDecisionPayload.ForConflictDecision(resolverDecision));
							break;
					}
				}
			}

			if (hasExisting && existingPending != null && input.OperationCoalescer != null)
			{
				bool canCoalesce = input.OperationCoalescer.CanCoalesce(existingPending, effective);
				if (canCoalesce)
				{
					DatabaseCoalesceResult coalesce = input.OperationCoalescer.Coalesce(existingPending, effective);
					if (coalesce != null)
					{
						DatabaseConflictDecision synthetic = null;
						switch (coalesce.Decision)
						{
							case DatabaseCoalesceDecision.KeepExisting:
								synthetic = new DatabaseConflictDecision(
									DatabaseConflictKind.StreamDuplicate,
									DatabaseConflictResolutionAction.KeepExistingAndRejectIncoming,
									null,
									existingPending.CommandId,
									"Coalescer keep-existing decision observed in scaffold mode.");
								succeeded = true;
								message = synthetic.Message;
								break;

							case DatabaseCoalesceDecision.MergeIntoNew:
								effective = coalesce.MergedRequest ?? effective;
								synthetic = new DatabaseConflictDecision(
									DatabaseConflictKind.StreamDuplicate,
									DatabaseConflictResolutionAction.CoalesceIntoIncoming,
									effective,
									string.Empty,
									string.IsNullOrEmpty(coalesce.Message)
										? "Coalescer merged incoming command."
										: coalesce.Message);
								message = synthetic.Message;
								break;

							case DatabaseCoalesceDecision.ReplaceExisting:
								synthetic = new DatabaseConflictDecision(
									DatabaseConflictKind.StreamSuperseded,
									DatabaseConflictResolutionAction.CancelExistingAndAllowIncoming,
									null,
									existingPending.CommandId,
									string.IsNullOrEmpty(coalesce.Message)
										? "Coalescer requested replacing existing pending command."
										: coalesce.Message);
								message = synthetic.Message;
								break;

							case DatabaseCoalesceDecision.None:
							default:
								synthetic = new DatabaseConflictDecision(
									DatabaseConflictKind.None,
									DatabaseConflictResolutionAction.AllowIncoming,
									null,
									string.Empty,
									string.IsNullOrEmpty(coalesce.Message)
										? "Coalescer made no structural change."
										: coalesce.Message);
								message = synthetic.Message;
								break;
						}

						if (synthetic != null)
						{
							conflicts.Add(synthetic);
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_COALESCER_DECISION", synthetic.Message, DatabaseDecisionPayload.ForConflictDecision(synthetic));
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
				WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Admission, DatabaseDecisionSeverity.Info, "ADMISSION_CANCEL_SUPERSEDED", "Canceled superseded pending commands in stream.", DatabaseDecisionPayload.ForStreamCancellation(canceledCount, effective.StreamKey));
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
					WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Admission, DatabaseDecisionSeverity.Info, "ADMISSION_POLICY_CANCEL", "Supersession policy canceled pending commands.", DatabaseDecisionPayload.ForStreamCancellation(canceledByPolicy, effective.StreamKey));
				}
			}

			return effective;
		}

		private static DatabaseTaskPlan CreateFallbackPlan(
			CodeDatabaseSnapshot currentSnapshot,
			DatabaseOperationRequest request,
			string description)
		{
			string commandId = request?.CommandId ?? string.Empty;
			string planId = string.IsNullOrEmpty(commandId)
				? "fallback-plan"
				: "fallback-plan-" + commandId;

			var tasks = new List<DatabaseTaskDescriptor>
			{
				new DatabaseTaskDescriptor(
					"fallback-finalize",
					DatabaseTaskKind.FinalizeOperation,
					description,
					null,
					new FinalizeOperationTaskPayload(description))
			};

			return new DatabaseTaskPlan(
				planId,
				commandId,
				currentSnapshot.Version,
				tasks,
				DateTime.UtcNow);
		}

		private static DatabaseTaskEnqueueResult CreateBypassEnqueueResult(
			DatabaseOperationRequest request,
			string message)
		{
			string commandId = request?.CommandId ?? string.Empty;
			string streamKey = request?.StreamKey ?? string.Empty;
			string ticket = string.IsNullOrEmpty(commandId)
				? "bypass-ticket"
				: "bypass-ticket-" + commandId;

			return new DatabaseTaskEnqueueResult(
				true,
				commandId,
				streamKey,
				ticket,
				0,
				DatabaseTaskEnqueueDisposition.Bypassed,
				message);
		}

		private static DatabaseTaskExecutionReport CreateBypassExecutionReport(
			DatabaseOperationRequest request,
			string message)
		{
			var results = new List<DatabaseTaskExecutionResult>
			{
				new DatabaseTaskExecutionResult(
					"bypass-execute",
					DatabaseTaskKind.FinalizeOperation,
					DatabaseTaskExecutionStatus.Succeeded,
					message,
					DatabaseTaskOutput.None)
			};

			return new DatabaseTaskExecutionReport(
				true,
				request?.CommandId,
				request?.CorrelationId,
				results,
				DatabaseTaskOutput.None,
				message);
		}

		private static CodeDatabaseSnapshot ComposeSnapshot(
			CodeDatabaseSnapshot currentSnapshot,
			DatabaseTaskExecutionReport executionReport,
			DatabaseOperationRequest request,
			IIndexMaintainer indexMaintainer)
		{
			CodeDatabaseSnapshot baseline = currentSnapshot ?? CodeDatabaseSnapshot.Empty();
			CodeDatabaseSnapshot rawSnapshot;

			if (executionReport != null
				&& executionReport.Output != null
				&& executionReport.Output.Kind == DatabaseTaskOutputKind.Snapshot
				&& executionReport.Output.Snapshot != null)
			{
				CodeDatabaseSnapshot fromReport = executionReport.Output.Snapshot;
				long reportVersion = request != null && request.Kind == DatabaseOperationKind.ReadSnapshot
					? baseline.Version
					: baseline.Version + 1;

				rawSnapshot = StampSnapshotVersion(fromReport, reportVersion, fromReport.IndexSnapshot);
				return EnsureIndexSnapshot(rawSnapshot, baseline, request, indexMaintainer);
			}

			if (request == null)
				return EnsureIndexSnapshot(baseline, baseline, null, indexMaintainer);

			switch (request.Kind)
			{
				case DatabaseOperationKind.ReadSnapshot:
					rawSnapshot = baseline;
					break;

				case DatabaseOperationKind.ResetDatabase:
					rawSnapshot = new CodeDatabaseSnapshot(
						baseline.Version + 1,
						DateTime.UtcNow,
						new List<DataAggregate>(0),
						new List<DataFact>(0),
						null);
					break;

				case DatabaseOperationKind.ReplaceSnapshot:
					rawSnapshot = StampSnapshotVersion(
						request.ReplacementSnapshot ?? CodeDatabaseSnapshot.Empty(),
						baseline.Version + 1,
						request.ReplacementSnapshot != null ? request.ReplacementSnapshot.IndexSnapshot : null);
					break;

				case DatabaseOperationKind.ApplyChangeSet:
					rawSnapshot = ComposeApplyChangeSetSnapshot(baseline, request);
					break;

				default:
					rawSnapshot = baseline;
					break;
			}

			return EnsureIndexSnapshot(rawSnapshot, baseline, request, indexMaintainer);
		}

		private static CodeDatabaseSnapshot EnsureIndexSnapshot(
			CodeDatabaseSnapshot snapshot,
			CodeDatabaseSnapshot previousSnapshot,
			DatabaseOperationRequest request,
			IIndexMaintainer indexMaintainer)
		{
			CodeDatabaseSnapshot candidate = snapshot ?? CodeDatabaseSnapshot.Empty();
			if (indexMaintainer == null)
				return candidate;

			DatabaseOperationKind kind = request != null ? request.Kind : DatabaseOperationKind.Unknown;
			bool shouldRebuild = kind == DatabaseOperationKind.ApplyChangeSet
				|| kind == DatabaseOperationKind.ReplaceSnapshot
				|| kind == DatabaseOperationKind.ResetDatabase;

			bool shouldRepair = candidate.IndexSnapshot == null
				|| candidate.IndexSnapshot.SnapshotVersion != candidate.Version;

			if (kind != DatabaseOperationKind.ApplyChangeSet
				&& !shouldRebuild
				&& kind != DatabaseOperationKind.ReadSnapshot)
				return candidate;

			if (kind == DatabaseOperationKind.ReadSnapshot && !shouldRepair)
				return candidate;

			if (kind == DatabaseOperationKind.ApplyChangeSet)
			{
				bool fullResyncRequested;
				IReadOnlyList<PathKey> changedDocuments = ExtractChangedDocumentsForIndexing(request, out fullResyncRequested);

				if (!fullResyncRequested
					&& previousSnapshot != null
					&& previousSnapshot.IndexSnapshot != null
					&& changedDocuments != null
					&& changedDocuments.Count > 0)
				{
					IIndexSnapshot updated = indexMaintainer.Update(previousSnapshot.IndexSnapshot, candidate, changedDocuments);
					if (updated != null)
						return StampSnapshotVersion(candidate, candidate.Version, updated);
				}

				IIndexSnapshot rebuiltForApply = indexMaintainer.Rebuild(candidate);
				return StampSnapshotVersion(candidate, candidate.Version, rebuiltForApply);
			}

			if (!shouldRebuild && !shouldRepair)
				return candidate;

			IIndexSnapshot rebuilt = indexMaintainer.Rebuild(candidate);
			return StampSnapshotVersion(candidate, candidate.Version, rebuilt);
		}

		private static IReadOnlyList<PathKey> ExtractChangedDocumentsForIndexing(
			DatabaseOperationRequest request,
			out bool fullResyncRequested)
		{
			fullResyncRequested = false;
			if (request == null || request.Changes == null || request.Changes.Count == 0)
				return new List<PathKey>(0);

			var changedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < request.Changes.Count; i++)
			{
				DatabaseChangeEvent change = request.Changes[i];
				if (change == null)
					continue;

				if (change.Kind == DatabaseChangeKind.FullResyncRequested)
				{
					fullResyncRequested = true;
					return new List<PathKey>(0);
				}

				string documentKey = NormalizeDocumentKey(change.DocumentKey.Value);
				if (!string.IsNullOrWhiteSpace(documentKey))
					changedSet.Add(documentKey);

				string payloadDocument = NormalizeDocumentKey(change.Payload != null ? change.Payload.DocumentUri : string.Empty);
				if (!string.IsNullOrWhiteSpace(payloadDocument))
					changedSet.Add(payloadDocument);

				if (change.Kind == DatabaseChangeKind.FileRenamed
					&& TryExtractRename(change, out string oldDocumentKey, out string newDocumentKey))
				{
					if (!string.IsNullOrWhiteSpace(oldDocumentKey))
						changedSet.Add(oldDocumentKey);

					if (!string.IsNullOrWhiteSpace(newDocumentKey))
						changedSet.Add(newDocumentKey);
				}

				if (change.Kind == DatabaseChangeKind.WatchedFilesChanged
					&& TryShouldDeleteByWatcher(change, out string deletedDocumentKey)
					&& !string.IsNullOrWhiteSpace(deletedDocumentKey))
				{
					changedSet.Add(deletedDocumentKey);
				}
			}

			var ordered = new List<string>(changedSet);
			ordered.Sort(StringComparer.OrdinalIgnoreCase);

			var changedDocuments = new List<PathKey>(ordered.Count);
			for (int i = 0; i < ordered.Count; i++)
				changedDocuments.Add(new PathKey(ordered[i]));

			return changedDocuments;
		}

		private static CodeDatabaseSnapshot ComposeApplyChangeSetSnapshot(
			CodeDatabaseSnapshot currentSnapshot,
			DatabaseOperationRequest request)
		{
			CodeDatabaseSnapshot baseline = currentSnapshot ?? CodeDatabaseSnapshot.Empty();
			if (request == null || request.Changes == null || request.Changes.Count == 0)
				return baseline;

			long nextVersion = baseline.Version + 1;

			var aggregatesByDocument = new Dictionary<string, DataAggregate>(StringComparer.OrdinalIgnoreCase);
			if (baseline.Aggregates != null)
			{
				for (int i = 0; i < baseline.Aggregates.Count; i++)
				{
					DataAggregate aggregate = baseline.Aggregates[i];
					if (aggregate == null)
						continue;

					string aggregateDocument = NormalizeDocumentKey(aggregate.DocumentKey.Value);
					if (string.IsNullOrWhiteSpace(aggregateDocument))
						continue;

					aggregatesByDocument[aggregateDocument] = aggregate;
				}
			}

			var touchedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var removedDocuments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var renamedDocuments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			bool fullResyncRequested = false;

			for (int i = 0; i < request.Changes.Count; i++)
			{
				DatabaseChangeEvent change = request.Changes[i];
				if (change == null)
					continue;

				if (change.Kind == DatabaseChangeKind.FullResyncRequested)
				{
					aggregatesByDocument.Clear();
					touchedDocuments.Clear();
					removedDocuments.Clear();
					renamedDocuments.Clear();
					fullResyncRequested = true;
					continue;
				}

				string documentKey = NormalizeDocumentKey(change.DocumentKey.Value);
				switch (change.Kind)
				{
					case DatabaseChangeKind.DocumentOpened:
					case DatabaseChangeKind.DocumentChanged:
						if (string.IsNullOrWhiteSpace(documentKey))
							break;

						aggregatesByDocument.TryGetValue(documentKey, out DataAggregate existingAggregate);
						DataAggregate updatedAggregate = ComposeDocumentAggregate(existingAggregate, change, documentKey);
						aggregatesByDocument[documentKey] = updatedAggregate;
						touchedDocuments.Add(documentKey);
						removedDocuments.Remove(documentKey);
						break;

					case DatabaseChangeKind.DocumentClosed:
						if (string.IsNullOrWhiteSpace(documentKey))
							break;

						aggregatesByDocument.Remove(documentKey);
						removedDocuments.Add(documentKey);
						touchedDocuments.Add(documentKey);
						break;

					case DatabaseChangeKind.FileRenamed:
						if (!TryExtractRename(change, out string oldDocumentKey, out string newDocumentKey))
							break;

						if (aggregatesByDocument.TryGetValue(oldDocumentKey, out DataAggregate renamedAggregate))
						{
							aggregatesByDocument.Remove(oldDocumentKey);
							aggregatesByDocument[newDocumentKey] = CloneAggregateForDocument(
								renamedAggregate,
								new PathKey(newDocumentKey),
								change.VersionHint);
						}

						renamedDocuments[oldDocumentKey] = newDocumentKey;
						removedDocuments.Add(oldDocumentKey);
						touchedDocuments.Add(oldDocumentKey);
						touchedDocuments.Add(newDocumentKey);
						break;

					case DatabaseChangeKind.WatchedFilesChanged:
						if (TryShouldDeleteByWatcher(change, out string deletedDocumentKey)
							&& !string.IsNullOrWhiteSpace(deletedDocumentKey))
						{
							aggregatesByDocument.Remove(deletedDocumentKey);
							removedDocuments.Add(deletedDocumentKey);
							touchedDocuments.Add(deletedDocumentKey);
						}
						break;

					case DatabaseChangeKind.Unknown:
					default:
						break;
				}
			}

			var aggregates = new List<DataAggregate>(aggregatesByDocument.Count);
			foreach (KeyValuePair<string, DataAggregate> pair in aggregatesByDocument)
				aggregates.Add(pair.Value);

			aggregates.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.DocumentKey.Value, right.DocumentKey.Value));

			var facts = new List<DataFact>();
			if (!fullResyncRequested && baseline.Facts != null)
			{
				for (int i = 0; i < baseline.Facts.Count; i++)
				{
					DataFact fact = baseline.Facts[i];
					if (fact == null)
						continue;

					string factDocument = NormalizeDocumentKey(fact.DocumentKey.Value);
					if (string.IsNullOrWhiteSpace(factDocument))
						continue;

					if (renamedDocuments.TryGetValue(factDocument, out string renamedDocument))
					{
						facts.Add(CloneFactForDocument(fact, new PathKey(renamedDocument), nextVersion));
						continue;
					}

					if (removedDocuments.Contains(factDocument))
						continue;

					if (touchedDocuments.Contains(factDocument))
						continue;

					facts.Add(fact);
				}
			}

			return new CodeDatabaseSnapshot(
				nextVersion,
				DateTime.UtcNow,
				aggregates,
				facts,
				null);
		}

		private static DataAggregate ComposeDocumentAggregate(
			DataAggregate existingAggregate,
			DatabaseChangeEvent change,
			string documentKey)
		{
			string languageId = existingAggregate != null ? existingAggregate.LanguageId : string.Empty;
			if (TryExtractLanguageId(change.Payload, out string extractedLanguageId)
				&& !string.IsNullOrWhiteSpace(extractedLanguageId))
			{
				languageId = extractedLanguageId;
			}

			string textHash = existingAggregate != null ? existingAggregate.TextHash : string.Empty;
			if (TryExtractText(change.Payload, out string text))
				textHash = ComputeStableTextHash(text);

			int? sourceVersion = ResolveSourceVersion(change, existingAggregate != null ? existingAggregate.SourceVersion : null);

			return new DataAggregate(
				CreateAggregateId(new PathKey(documentKey)),
				DataAggregateKind.Document,
				new PathKey(documentKey),
				languageId,
				textHash,
				sourceVersion,
				new List<DataFact>(0));
		}

		private static DataAggregate CloneAggregateForDocument(
			DataAggregate aggregate,
			PathKey documentKey,
			int? sourceVersionOverride)
		{
			if (aggregate == null)
			{
				return new DataAggregate(
					CreateAggregateId(documentKey),
					DataAggregateKind.Document,
					documentKey,
					string.Empty,
					string.Empty,
					sourceVersionOverride,
					new List<DataFact>(0));
			}

			return new DataAggregate(
				CreateAggregateId(documentKey),
				aggregate.Kind,
				documentKey,
				aggregate.LanguageId,
				aggregate.TextHash,
				sourceVersionOverride ?? aggregate.SourceVersion,
				aggregate.Facts);
		}

		private static DataFact CloneFactForDocument(DataFact fact, PathKey documentKey, long snapshotVersion)
		{
			return new DataFact(
				fact.Id,
				CreateAggregateId(documentKey),
				fact.Kind,
				documentKey,
				fact.Span,
				snapshotVersion,
				fact.Payload);
		}

		private static DataAggregateId CreateAggregateId(PathKey documentKey)
		{
			string normalized = NormalizeDocumentKey(documentKey.Value);
			return new DataAggregateId("agg:doc:" + normalized.ToLowerInvariant());
		}

		private static int? ResolveSourceVersion(DatabaseChangeEvent change, int? fallback)
		{
			if (change == null)
				return fallback;

			if (change.VersionHint.HasValue)
				return change.VersionHint;

			return fallback;
		}

		private static bool TryExtractLanguageId(DatabaseChangePayload payload, out string languageId)
		{
			languageId = string.Empty;
			if (payload is DocumentOpenedChangePayload opened
				&& !string.IsNullOrWhiteSpace(opened.LanguageId))
			{
				languageId = opened.LanguageId;
				return true;
			}

			if (payload is DocumentMetadataChangePayload metadata
				&& !string.IsNullOrWhiteSpace(metadata.LanguageId))
			{
				languageId = metadata.LanguageId;
				return true;
			}

			return false;
		}

		private static bool TryExtractText(DatabaseChangePayload payload, out string text)
		{
			text = string.Empty;
			if (payload == null)
				return false;

			if (payload is DocumentOpenedChangePayload opened)
			{
				text = opened.Text;
				return true;
			}

			if (payload is DocumentChangedChangePayload changed)
			{
				text = changed.Text;
				return true;
			}

			if (payload is DocumentMetadataChangePayload metadata)
			{
				text = metadata.Text;
				return !string.IsNullOrEmpty(text);
			}

			return false;
		}

		private static bool TryExtractRename(DatabaseChangeEvent change, out string oldDocumentKey, out string newDocumentKey)
		{
			oldDocumentKey = string.Empty;
			newDocumentKey = string.Empty;
			if (change == null)
				return false;

			if (!(change.Payload is FileRenamedChangePayload payload))
				return false;

			oldDocumentKey = NormalizeDocumentKey(payload.OldDocumentUri);
			newDocumentKey = NormalizeDocumentKey(payload.NewDocumentUri);

			if (string.IsNullOrWhiteSpace(oldDocumentKey) || string.IsNullOrWhiteSpace(newDocumentKey))
				return false;

			return true;
		}

		private static bool TryShouldDeleteByWatcher(DatabaseChangeEvent change, out string documentKey)
		{
			documentKey = NormalizeDocumentKey(change != null ? change.DocumentKey.Value : string.Empty);
			if (change == null || !(change.Payload is WatchedFileChangedChangePayload payload))
				return false;

			if (!string.IsNullOrWhiteSpace(payload.DocumentUri))
				documentKey = NormalizeDocumentKey(payload.DocumentUri);

			return payload.ChangeType == WatchedFileChangeType.Deleted;
		}

		private static string NormalizeDocumentKey(string value)
		{
			return DocumentKeyNormalizer.Normalize(value);
		}

		private static string ComputeStableTextHash(string text)
		{
			if (string.IsNullOrEmpty(text))
				return string.Empty;

			unchecked
			{
				uint hash = 2166136261;
				for (int i = 0; i < text.Length; i++)
				{
					hash ^= text[i];
					hash *= 16777619;
				}

				return hash.ToString("X8");
			}
		}

		private static CodeDatabaseSnapshot StampSnapshotVersion(
			CodeDatabaseSnapshot snapshot,
			long version,
			IIndexSnapshot indexSnapshot)
		{
			CodeDatabaseSnapshot candidate = snapshot ?? CodeDatabaseSnapshot.Empty();
			return new CodeDatabaseSnapshot(
				version,
				DateTime.UtcNow,
				candidate.Aggregates,
				candidate.Facts,
				indexSnapshot);
		}
	}
}
