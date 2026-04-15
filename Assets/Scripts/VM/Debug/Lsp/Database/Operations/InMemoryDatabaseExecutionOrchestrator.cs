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
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ValidateVersionGate, DatabaseDecisionCategory.Lifecycle, DatabaseDecisionSeverity.Warning, "VERSION_GATE_MISMATCH", stopReason, new { effectiveRequest.ExpectedVersion, currentVersion = currentSnapshot.Version });

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
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.PlanTasks, DatabaseDecisionCategory.Planning, DatabaseDecisionSeverity.Info, "PLAN_CREATED", "Task plan created.", new { plannedTaskPlan.PlanId });
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
					WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.EnqueueTasks, DatabaseDecisionCategory.Queueing, DatabaseDecisionSeverity.Info, "QUEUE_ACCEPTED", "Task enqueue accepted.", new { enqueueResult.Disposition, enqueueResult.QueueTicket });
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

			// Stage 7: compose next snapshot (pass-through scaffold).
			if (!canProceed)
			{
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, false, "Skipped due to previous stage failure.");
			}
			else
			{
				nextSnapshot = ComposeSnapshotPassThrough(currentSnapshot, executionReport, effectiveRequest);
				AddTrace(trace, DatabaseExecutionStage.ComposeSnapshot, true, "ComposeSnapshot pass-through completed.");
				WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.ComposeSnapshot, DatabaseDecisionCategory.Compose, DatabaseDecisionSeverity.Info, "COMPOSE_PASSTHROUGH", "ComposeSnapshot pass-through completed.", null);

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
			WriteDecision(input, decisions, effectiveRequest, DatabaseExecutionStage.BuildOperationResult, DatabaseDecisionCategory.Result, canProceed ? DatabaseDecisionSeverity.Info : DatabaseDecisionSeverity.Error, canProceed ? "RESULT_SUCCESS" : "RESULT_FAILURE", "Operation result created.", new { finalState = currentState, canProceed });

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
			object payload)
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
				payload);

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
							WriteDecision(input, decisions, incoming, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Warning, "CONFLICT_REJECT_INCOMING", message, resolverDecision);
							return incoming;

						case DatabaseConflictResolutionAction.CoalesceIntoIncoming:
							effective = resolverDecision.MergedIncoming ?? incoming;
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Incoming command replaced by conflict-merged command."
								: resolverDecision.Message;
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_COALESCE", message, resolverDecision);
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
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_CANCEL_EXISTING", message, resolverDecision);
							break;

						case DatabaseConflictResolutionAction.AllowIncoming:
						case DatabaseConflictResolutionAction.QueueIncoming:
						case DatabaseConflictResolutionAction.None:
						default:
							message = string.IsNullOrEmpty(resolverDecision.Message)
								? "Conflict resolver allowed incoming command."
								: resolverDecision.Message;
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_ALLOW_INCOMING", message, resolverDecision);
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
							WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Conflict, DatabaseDecisionSeverity.Info, "CONFLICT_COALESCER_DECISION", synthetic.Message, synthetic);
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
				WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Admission, DatabaseDecisionSeverity.Info, "ADMISSION_CANCEL_SUPERSEDED", "Canceled superseded pending commands in stream.", new { canceledCount, effective.StreamKey });
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
					WriteDecision(input, decisions, effective, DatabaseExecutionStage.HighFrequencyAdmission, DatabaseDecisionCategory.Admission, DatabaseDecisionSeverity.Info, "ADMISSION_POLICY_CANCEL", "Supersession policy canceled pending commands.", new { canceledByPolicy, effective.StreamKey });
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
					null)
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
					null)
			};

			return new DatabaseTaskExecutionReport(
				true,
				request?.CommandId,
				request?.CorrelationId,
				results,
				null,
				message);
		}

		private static CodeDatabaseSnapshot ComposeSnapshotPassThrough(
			CodeDatabaseSnapshot currentSnapshot,
			DatabaseTaskExecutionReport executionReport,
			DatabaseOperationRequest request)
		{
			if (request != null
				&& request.Kind == DatabaseOperationKind.ReplaceSnapshot
				&& request.ReplacementSnapshot != null)
			{
				return request.ReplacementSnapshot;
			}

			if (executionReport != null && executionReport.Output is CodeDatabaseSnapshot fromReport)
				return fromReport;

			return currentSnapshot ?? CodeDatabaseSnapshot.Empty();
		}
	}
}
