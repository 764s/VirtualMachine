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
using System.IO;
using FFVM.AST;
using FFVM.Compiler;
using FFVM.Debug;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Tooling;
using DocumentKeyNormalizer = FFVM.Debug.Lsp.Database.Paths.DocumentKeyNormalizer;
using IncludeTargetResolver = FFVM.Debug.Lsp.Database.Paths.IncludeTargetResolver;
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
				IReadOnlyList<PathKey> changedDocuments = ExtractChangedDocumentsForIndexing(request, previousSnapshot, out fullResyncRequested);

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
			CodeDatabaseSnapshot previousSnapshot,
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

			if (changedSet.Count > 0
				&& previousSnapshot != null
				&& previousSnapshot.IndexSnapshot != null
				&& previousSnapshot.IndexSnapshot.IncludeGraphIndex != null)
			{
				ExpandChangedDocumentsWithDependents(changedSet, previousSnapshot.IndexSnapshot);
			}

			var ordered = new List<string>(changedSet);
			ordered.Sort(StringComparer.OrdinalIgnoreCase);

			var changedDocuments = new List<PathKey>(ordered.Count);
			for (int i = 0; i < ordered.Count; i++)
				changedDocuments.Add(new PathKey(ordered[i]));

			return changedDocuments;
		}

		private static void ExpandChangedDocumentsWithDependents(HashSet<string> changedSet, IIndexSnapshot previousIndex)
		{
			if (changedSet == null
				|| changedSet.Count == 0
				|| previousIndex == null
				|| previousIndex.IncludeGraphIndex == null)
			{
				return;
			}

			var queue = new Queue<string>(changedSet);
			while (queue.Count > 0)
			{
				string current = queue.Dequeue();
				if (string.IsNullOrWhiteSpace(current))
					continue;

				IReadOnlyList<PathKey> dependents = previousIndex.IncludeGraphIndex.GetDependents(new PathKey(current));
				if (dependents == null || dependents.Count == 0)
					continue;

				for (int i = 0; i < dependents.Count; i++)
				{
					string dependentKey = NormalizeDocumentKey(dependents[i].Value);
					if (string.IsNullOrWhiteSpace(dependentKey))
						continue;

					if (changedSet.Add(dependentKey))
						queue.Enqueue(dependentKey);
				}
			}
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
			var rebuiltFactsByDocument = new Dictionary<string, List<DataFact>>(StringComparer.OrdinalIgnoreCase);
			var appliedTierByDocument = new Dictionary<string, DocumentSourceTier>(StringComparer.OrdinalIgnoreCase);
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
					rebuiltFactsByDocument.Clear();
					appliedTierByDocument.Clear();
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

						DocumentSourceTier incomingTier = ResolveSourceTier(change);
						if (appliedTierByDocument.TryGetValue(documentKey, out DocumentSourceTier existingTier)
							&& IsHigherPriorityTier(existingTier, incomingTier))
						{
							break;
						}

						appliedTierByDocument[documentKey] = incomingTier;

						aggregatesByDocument.TryGetValue(documentKey, out DataAggregate existingAggregate);
						DataAggregate updatedAggregate = ComposeDocumentAggregate(existingAggregate, change, documentKey);
						aggregatesByDocument[documentKey] = updatedAggregate;
						touchedDocuments.Add(documentKey);
						removedDocuments.Remove(documentKey);

						if (TryBuildDocumentFacts(change, documentKey, nextVersion, out List<DataFact> rebuiltFacts))
							rebuiltFactsByDocument[documentKey] = rebuiltFacts;
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
						if (appliedTierByDocument.TryGetValue(oldDocumentKey, out DocumentSourceTier renamedTier))
						{
							appliedTierByDocument.Remove(oldDocumentKey);
							appliedTierByDocument[newDocumentKey] = renamedTier;
						}

						if (rebuiltFactsByDocument.TryGetValue(oldDocumentKey, out List<DataFact> rebuiltFactsForOld))
						{
							rebuiltFactsByDocument.Remove(oldDocumentKey);
							var renamedFacts = new List<DataFact>(rebuiltFactsForOld.Count);
							for (int factIndex = 0; factIndex < rebuiltFactsForOld.Count; factIndex++)
								renamedFacts.Add(CloneFactForDocument(rebuiltFactsForOld[factIndex], new PathKey(newDocumentKey), nextVersion));

							rebuiltFactsByDocument[newDocumentKey] = renamedFacts;
						}

						removedDocuments.Add(oldDocumentKey);
						touchedDocuments.Add(oldDocumentKey);
						touchedDocuments.Add(newDocumentKey);
						break;

					case DatabaseChangeKind.WatchedFilesChanged:
						if (TryShouldDeleteByWatcher(change, out string deletedDocumentKey)
							&& !string.IsNullOrWhiteSpace(deletedDocumentKey))
						{
							if (appliedTierByDocument.TryGetValue(deletedDocumentKey, out DocumentSourceTier existingDeleteTier)
								&& IsHigherPriorityTier(existingDeleteTier, DocumentSourceTier.Watcher))
							{
								break;
							}

							appliedTierByDocument[deletedDocumentKey] = DocumentSourceTier.Watcher;
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

					if (touchedDocuments.Contains(factDocument)
						&& rebuiltFactsByDocument.ContainsKey(factDocument))
						continue;

					facts.Add(fact);
				}
			}

			if (!fullResyncRequested)
			{
				foreach (KeyValuePair<string, List<DataFact>> pair in rebuiltFactsByDocument)
				{
					if (pair.Value == null || pair.Value.Count == 0)
						continue;

					for (int i = 0; i < pair.Value.Count; i++)
					{
						DataFact rebuiltFact = pair.Value[i];
						if (rebuiltFact != null)
							facts.Add(rebuiltFact);
					}
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

		private static bool TryBuildDocumentFacts(
			DatabaseChangeEvent change,
			string documentKey,
			long snapshotVersion,
			out List<DataFact> facts)
		{
			facts = new List<DataFact>(0);
			if (change == null || string.IsNullOrWhiteSpace(documentKey))
				return false;

			if (!TryExtractText(change.Payload, out string text))
				return false;

			facts = BuildDocumentFacts(documentKey, text, snapshotVersion);
			return true;
		}

		private static List<DataFact> BuildDocumentFacts(string documentKey, string source, long snapshotVersion)
		{
			var output = new List<DataFact>();
			if (string.IsNullOrWhiteSpace(documentKey))
				return output;

			string normalizedDocument = NormalizeDocumentKey(documentKey);
			if (string.IsNullOrWhiteSpace(normalizedDocument))
				return output;

			var parser = new Parser();
			ModuleNode module = parser.Parse(source ?? string.Empty, out _);
			if (module == null)
				return output;

			var documentPath = new PathKey(normalizedDocument);
			DataAggregateId aggregateId = CreateAggregateId(documentPath);
			var functionSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			var nameSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			Dictionary<string, SymbolIdentity> importedFunctionSymbols = BuildImportedFunctionSymbols(module, normalizedDocument);
			Dictionary<string, SymbolIdentity> importedNameSymbols = BuildImportedNameSymbols(module, normalizedDocument);
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedFunctionSymbols = BuildAliasedFunctionSymbols(module, normalizedDocument);
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedNameSymbols = BuildAliasedNameSymbols(module, normalizedDocument);
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedStructFieldSymbols = BuildImportedStructFieldSymbols(module, normalizedDocument);
			Dictionary<string, Dictionary<string, SymbolIdentity>> importedEnumMemberSymbols = BuildImportedEnumMemberSymbols(module, normalizedDocument);

			int definitionOrdinal = 0;
			for (int i = 0; i < module.Functions.Count; i++)
			{
				FuncDecl function = module.Functions[i];
				if (function == null || string.IsNullOrWhiteSpace(function.Name))
					continue;
				if (!string.IsNullOrEmpty(function.AliasTarget))
					continue;

				int nameLine = function.Line;
				int nameColumn = ResolveFunctionNameColumn(function, source);
				if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, function.Name.Length, out TextSpan span, out int startLine, out int startCharacter, out int endLine, out int endCharacter))
					continue;

				var symbol = new SymbolIdentity(
					SymbolKindTag.Function,
					function.Name,
					string.Empty,
					string.Empty,
					normalizedDocument,
					span,
					BuildFuncDocumentation(function),
					typeName: null,
					isPrivate: function.IsPrivate);

				var payload = new SymbolDataFactPayload(symbol, startLine, startCharacter, endLine, endCharacter);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, function.Name, startLine, startCharacter)),
					aggregateId,
					DataFactKind.SymbolDefinition,
					documentPath,
					span,
					snapshotVersion,
					payload));

				if (!functionSymbols.ContainsKey(function.Name))
					functionSymbols[function.Name] = symbol;
			}

			EmitModuleVarDefinitions(module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output, nameSymbols, ref definitionOrdinal);
			EmitStructNameDefinitions(module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output, nameSymbols, ref definitionOrdinal);
			EmitEnumDefinitions(module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output, nameSymbols, ref definitionOrdinal);

			var overrideFunctionSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			var overrideNameSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			EmitOverrideDefinitions(module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion,
				output, aliasedFunctionSymbols, aliasedNameSymbols,
				overrideFunctionSymbols, overrideNameSymbols, ref definitionOrdinal);

			Dictionary<string, Dictionary<string, StructFieldDescriptor>> localStructFieldSymbols = BuildLocalStructFieldSymbols(
				module,
				normalizedDocument,
				source,
				aggregateId,
				snapshotVersion,
				output,
				ref definitionOrdinal);

			Dictionary<string, Dictionary<string, StructFieldDescriptor>> structFieldSymbolsByStruct = MergeStructFieldSymbols(
				localStructFieldSymbols,
				importedStructFieldSymbols);

			Dictionary<string, Dictionary<string, SymbolIdentity>> localEnumMemberSymbols = BuildLocalEnumMemberSymbols(
				module,
				normalizedDocument,
				source,
				aggregateId,
				snapshotVersion,
				output,
				ref definitionOrdinal);

			Dictionary<string, Dictionary<string, SymbolIdentity>> enumMemberSymbolsByEnum = MergeEnumMemberSymbols(
				localEnumMemberSymbols,
				importedEnumMemberSymbols);

			Dictionary<string, Dictionary<string, SymbolIdentity>> parameterSymbolsByFunc = EmitParameterDefinitionFacts(
				module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output, ref definitionOrdinal);

			var localVarSymbolsByFunc = EmitLocalVarDefinitionFacts(
				module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output, ref definitionOrdinal);

			int referenceOrdinal = 0;
			for (int i = 0; i < module.Functions.Count; i++)
			{
				FuncDecl function = module.Functions[i];
				if (function == null || function.Body == null)
					continue;

				CollectCallReferencesFromStatement(function.Body, call =>
				{
					if (call == null || string.IsNullOrWhiteSpace(call.FunctionName))
						return;

					if (!functionSymbols.TryGetValue(call.FunctionName, out SymbolIdentity symbol) || symbol == null)
					{
						if (!importedFunctionSymbols.TryGetValue(call.FunctionName, out symbol) || symbol == null)
							return;
					}

					if (!TryCreateSpanFromLineColumn(source, call.Line, call.Column, call.FunctionName.Length, out TextSpan span, out int startLine, out int startCharacter, out int endLine, out int endCharacter))
						return;

					var payload = new SymbolDataFactPayload(symbol, startLine, startCharacter, endLine, endCharacter);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("ref", normalizedDocument, referenceOrdinal++, call.FunctionName, startLine, startCharacter)),
						aggregateId,
						DataFactKind.SymbolReference,
						documentPath,
						span,
						snapshotVersion,
						payload));
				});
			}

			EmitAliasedCallReferenceFacts(
				module,
				source,
				normalizedDocument,
				documentPath,
				aggregateId,
				snapshotVersion,
				output,
				aliasedFunctionSymbols,
				overrideFunctionSymbols,
				ref referenceOrdinal);

			EmitStructFieldReferenceFacts(
				module,
				source,
				normalizedDocument,
				documentPath,
				aggregateId,
				snapshotVersion,
				output,
				structFieldSymbolsByStruct,
				ref referenceOrdinal);

			EmitEnumMemberReferenceFacts(
				module,
				source,
				normalizedDocument,
				documentPath,
				aggregateId,
				snapshotVersion,
				output,
				enumMemberSymbolsByEnum,
				ref referenceOrdinal);

			EmitIdentifierReferenceFacts(
				module,
				source,
				normalizedDocument,
				documentPath,
				aggregateId,
				snapshotVersion,
				output,
				nameSymbols,
				importedNameSymbols,
				parameterSymbolsByFunc,
				localVarSymbolsByFunc,
				ref referenceOrdinal);

			EmitAliasedIdentifierReferenceFacts(
				module,
				source,
				normalizedDocument,
				documentPath,
				aggregateId,
				snapshotVersion,
				output,
				aliasedNameSymbols,
				overrideNameSymbols,
				ref referenceOrdinal);

			EmitIncludeEdgeFacts(module, source, normalizedDocument, documentPath, aggregateId, snapshotVersion, output);
			return output;
		}

		private static Dictionary<string, Dictionary<string, StructFieldDescriptor>> BuildLocalStructFieldSymbols(
			ModuleNode module,
			string normalizedDocument,
			string source,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			ref int definitionOrdinal)
		{
			var symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.Ordinal);
			if (module == null || module.Structs == null || module.Structs.Count == 0)
				return symbolsByStruct;

			for (int i = 0; i < module.Structs.Count; i++)
			{
				StructDecl structDecl = module.Structs[i];
				if (structDecl == null || string.IsNullOrWhiteSpace(structDecl.Name) || structDecl.Fields == null)
					continue;

				string structName = structDecl.Name;
				if (!symbolsByStruct.TryGetValue(structName, out Dictionary<string, StructFieldDescriptor> fieldsByName))
				{
					fieldsByName = new Dictionary<string, StructFieldDescriptor>(StringComparer.Ordinal);
					symbolsByStruct[structName] = fieldsByName;
				}

				for (int fieldIndex = 0; fieldIndex < structDecl.Fields.Count; fieldIndex++)
				{
					StructField field = structDecl.Fields[fieldIndex];
					if (field == null || string.IsNullOrWhiteSpace(field.Name))
						continue;

					int fieldLine = field.Line > 0 ? field.Line : structDecl.Line;
					int fieldColumn = field.Column > 0 ? field.Column : structDecl.Column;
					if (!TryCreateSpanFromLineColumn(
						source,
						fieldLine,
						fieldColumn,
						field.Name.Length,
						out TextSpan span,
						out int startLine,
						out int startCharacter,
						out int endLine,
						out int endCharacter))
					{
						continue;
					}

					if (!fieldsByName.ContainsKey(field.Name))
					{
						var symbol = new SymbolIdentity(
							SymbolKindTag.StructField,
							field.Name,
							BuildStructFieldPath(structName, field.Name),
							structName,
							normalizedDocument,
							span,
							documentation: null,
							typeName: GetBaseTypeName(field.TypeName));

						fieldsByName[field.Name] = new StructFieldDescriptor(symbol, field.TypeName);
					}

					SymbolIdentity fieldSymbol = fieldsByName[field.Name].Symbol;
					var payload = new SymbolDataFactPayload(fieldSymbol, startLine, startCharacter, endLine, endCharacter);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, field.Name, startLine, startCharacter)),
						aggregateId,
						DataFactKind.SymbolDefinition,
						new PathKey(normalizedDocument),
						span,
						snapshotVersion,
						payload));
				}
			}

			return symbolsByStruct;
		}

		private static Dictionary<string, Dictionary<string, StructFieldDescriptor>> BuildImportedStructFieldSymbols(
			ModuleNode module,
			string normalizedDocument)
		{
			var symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.Ordinal);
			if (module == null
				|| module.Imports == null
				|| module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
			{
				return symbolsByStruct;
			}

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;

				// DX21 P0-3: skip aliased imports — they stay namespaced, not flat-merged
				if (!string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0)
					continue;

				for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[candidateIndex]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;

					if (!TryReadImportedStructFieldSymbols(candidateUri, out Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedSymbols, visitedTargets))
						continue;

					MergeStructFieldSymbolsInto(symbolsByStruct, importedSymbols);
					break;
				}
			}

			return symbolsByStruct;
		}

		private static bool TryReadImportedStructFieldSymbols(
			string importedDocumentUri,
			out Dictionary<string, Dictionary<string, StructFieldDescriptor>> symbolsByStruct,
			HashSet<string> visitedTargets)
		{
			symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.Ordinal);
			string normalizedUri = NormalizeDocumentKey(importedDocumentUri);
			if (string.IsNullOrWhiteSpace(normalizedUri))
				return false;

			string path = WorkspacePathTool.UriToPath(normalizedUri);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return false;

			string source;
			try
			{
				source = File.ReadAllText(path);
			}
			catch
			{
				return false;
			}

			var parser = new Parser();
			ModuleNode module = parser.Parse(source ?? string.Empty, out _);
			if (module == null)
				return true;

			if (module.Structs != null)
			{
				for (int i = 0; i < module.Structs.Count; i++)
				{
					StructDecl structDecl = module.Structs[i];
					if (structDecl == null || string.IsNullOrWhiteSpace(structDecl.Name) || structDecl.Fields == null)
						continue;
					if (structDecl.IsPrivate) continue;

					if (!symbolsByStruct.TryGetValue(structDecl.Name, out Dictionary<string, StructFieldDescriptor> fieldsByName))
					{
						fieldsByName = new Dictionary<string, StructFieldDescriptor>(StringComparer.Ordinal);
						symbolsByStruct[structDecl.Name] = fieldsByName;
					}

					for (int fieldIndex = 0; fieldIndex < structDecl.Fields.Count; fieldIndex++)
					{
						StructField field = structDecl.Fields[fieldIndex];
						if (field == null || string.IsNullOrWhiteSpace(field.Name))
							continue;

						int fieldLine = field.Line > 0 ? field.Line : structDecl.Line;
						int fieldColumn = field.Column > 0 ? field.Column : structDecl.Column;
						if (!TryCreateSpanFromLineColumn(source, fieldLine, fieldColumn, field.Name.Length, out TextSpan span, out _, out _, out _, out _))
							continue;

						if (fieldsByName.ContainsKey(field.Name))
							continue;

						fieldsByName[field.Name] = new StructFieldDescriptor(
							new SymbolIdentity(
								SymbolKindTag.StructField,
								field.Name,
								BuildStructFieldPath(structDecl.Name, field.Name),
								structDecl.Name,
								normalizedUri,
								span,
								documentation: null,
								typeName: GetBaseTypeName(field.TypeName)),
							field.TypeName);
					}
				}
			}

			// CFR-03: recursively follow transitive includes
			if (module.Imports != null)
			{
				for (int i = 0; i < module.Imports.Count; i++)
				{
					ImportDecl import = module.Imports[i];
					if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
						continue;
					if (!string.IsNullOrEmpty(import.Alias))
						continue;

					List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedUri, import.ModulePath);
					if (candidates == null || candidates.Count == 0)
						continue;

					for (int ci = 0; ci < candidates.Count; ci++)
					{
						string candidateUri = NormalizeDocumentKey(candidates[ci]);
						if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
							continue;
						if (!TryReadImportedStructFieldSymbols(candidateUri, out Dictionary<string, Dictionary<string, StructFieldDescriptor>> transitiveSymbols, visitedTargets))
							continue;
						MergeStructFieldSymbolsInto(symbolsByStruct, transitiveSymbols);
						break;
					}
				}
			}

			return true;
		}

		private static Dictionary<string, Dictionary<string, StructFieldDescriptor>> MergeStructFieldSymbols(
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedSymbols,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> localSymbols)
		{
			var merged = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.Ordinal);
			MergeStructFieldSymbolsInto(merged, importedSymbols);
			MergeStructFieldSymbolsInto(merged, localSymbols);
			return merged;
		}

		private static void MergeStructFieldSymbolsInto(
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> destination,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> incoming)
		{
			if (destination == null || incoming == null)
				return;

			foreach (KeyValuePair<string, Dictionary<string, StructFieldDescriptor>> structPair in incoming)
			{
				if (string.IsNullOrWhiteSpace(structPair.Key) || structPair.Value == null)
					continue;

				if (!destination.TryGetValue(structPair.Key, out Dictionary<string, StructFieldDescriptor> destinationFields))
				{
					destinationFields = new Dictionary<string, StructFieldDescriptor>(StringComparer.Ordinal);
					destination[structPair.Key] = destinationFields;
				}

				foreach (KeyValuePair<string, StructFieldDescriptor> fieldPair in structPair.Value)
				{
					if (string.IsNullOrWhiteSpace(fieldPair.Key)
						|| fieldPair.Value == null
						|| fieldPair.Value.Symbol == null
						|| destinationFields.ContainsKey(fieldPair.Key))
					{
						continue;
					}

					destinationFields[fieldPair.Key] = fieldPair.Value;
				}
			}
		}

		private static void EmitStructFieldReferenceFacts(
			ModuleNode module,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> structFieldSymbolsByStruct,
			ref int referenceOrdinal)
		{
			if (module == null || structFieldSymbolsByStruct == null || structFieldSymbolsByStruct.Count == 0)
				return;

			Dictionary<string, string> moduleVariableTypes = BuildModuleVariableTypeMap(module);

			if (module.Functions != null)
			{
				for (int i = 0; i < module.Functions.Count; i++)
				{
					FuncDecl function = module.Functions[i];
					if (function == null || function.Body == null)
						continue;

					var variableTypes = new Dictionary<string, string>(moduleVariableTypes, StringComparer.Ordinal);
					if (function.Parameters != null)
					{
						for (int paramIndex = 0; paramIndex < function.Parameters.Count; paramIndex++)
						{
							ParamDecl parameter = function.Parameters[paramIndex];
							if (parameter == null || string.IsNullOrWhiteSpace(parameter.Name))
								continue;

							string parameterType = GetBaseTypeName(parameter.TypeName);
							if (!string.IsNullOrWhiteSpace(parameterType))
								variableTypes[parameter.Name] = parameterType;
						}
					}

					CollectStructFieldReferencesFromStatement(
						function.Body,
						variableTypes,
						structFieldSymbolsByStruct,
						source,
						normalizedDocument,
						documentPath,
						aggregateId,
						snapshotVersion,
						output,
						ref referenceOrdinal);
				}
			}

			if (module.ModuleVariables == null)
				return;

			var moduleInitializerTypes = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int i = 0; i < module.ModuleVariables.Count; i++)
			{
				VarDeclStmt variable = module.ModuleVariables[i];
				if (variable == null)
					continue;

				if (variable.Initializer != null)
				{
					ResolveExpressionTypeAndEmitFieldReferences(
						variable.Initializer,
						moduleInitializerTypes,
						structFieldSymbolsByStruct,
						source,
						normalizedDocument,
						documentPath,
						aggregateId,
						snapshotVersion,
						output,
						ref referenceOrdinal);
				}

				string declaredType = GetBaseTypeName(variable.TypeName);
				if (!string.IsNullOrWhiteSpace(variable.Name) && !string.IsNullOrWhiteSpace(declaredType))
					moduleInitializerTypes[variable.Name] = declaredType;
			}
		}

		private static void CollectStructFieldReferencesFromStatement(
			Stmt statement,
			Dictionary<string, string> variableTypes,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> structFieldSymbolsByStruct,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			ref int referenceOrdinal)
		{
			if (statement == null)
				return;

			if (statement is BlockStmt block)
			{
				var scopedVariables = new Dictionary<string, string>(variableTypes, StringComparer.Ordinal);
				if (block.Statements == null)
					return;

				for (int i = 0; i < block.Statements.Count; i++)
				{
					CollectStructFieldReferencesFromStatement(
						block.Statements[i],
						scopedVariables,
						structFieldSymbolsByStruct,
						source,
						normalizedDocument,
						documentPath,
						aggregateId,
						snapshotVersion,
						output,
						ref referenceOrdinal);
				}

				return;
			}

			if (statement is VarDeclStmt variable)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					variable.Initializer,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				string declaredType = GetBaseTypeName(variable.TypeName);
				if (!string.IsNullOrWhiteSpace(variable.Name) && !string.IsNullOrWhiteSpace(declaredType))
					variableTypes[variable.Name] = declaredType;

				return;
			}

			if (statement is IfStmt conditional)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					conditional.Condition,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				CollectStructFieldReferencesFromStatement(
					conditional.ThenBranch,
					new Dictionary<string, string>(variableTypes, StringComparer.Ordinal),
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				CollectStructFieldReferencesFromStatement(
					conditional.ElseBranch,
					new Dictionary<string, string>(variableTypes, StringComparer.Ordinal),
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is WhileStmt loop)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					loop.Condition,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				CollectStructFieldReferencesFromStatement(
					loop.Body,
					new Dictionary<string, string>(variableTypes, StringComparer.Ordinal),
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is ForStmt forLoop)
			{
				var scopedVariables = new Dictionary<string, string>(variableTypes, StringComparer.Ordinal);
				CollectStructFieldReferencesFromStatement(
					forLoop.Initializer,
					scopedVariables,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				ResolveExpressionTypeAndEmitFieldReferences(
					forLoop.Condition,
					scopedVariables,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				ResolveExpressionTypeAndEmitFieldReferences(
					forLoop.Increment,
					scopedVariables,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				CollectStructFieldReferencesFromStatement(
					forLoop.Body,
					scopedVariables,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is ReturnStmt returned)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					returned.Value,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is WaitStmt waited)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					waited.FrameCount,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is WaitForStmt waitedFor)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					waitedFor.TargetInstanceId,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is ExprStmt expression)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					expression.Expression,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is DeferStmt defer)
			{
				CollectStructFieldReferencesFromStatement(
					defer.Body,
					new Dictionary<string, string>(variableTypes, StringComparer.Ordinal),
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return;
			}

			if (statement is UsingStmt usingStmt)
			{
				if (usingStmt.Arguments != null)
				{
					for (int i = 0; i < usingStmt.Arguments.Count; i++)
					{
						ResolveExpressionTypeAndEmitFieldReferences(
							usingStmt.Arguments[i],
							variableTypes,
							structFieldSymbolsByStruct,
							source,
							normalizedDocument,
							documentPath,
							aggregateId,
							snapshotVersion,
							output,
							ref referenceOrdinal);
					}
				}

				CollectStructFieldReferencesFromStatement(
					usingStmt.Body,
					new Dictionary<string, string>(variableTypes, StringComparer.Ordinal),
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
			}
		}

		private static string ResolveExpressionTypeAndEmitFieldReferences(
			Expr expression,
			Dictionary<string, string> variableTypes,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> structFieldSymbolsByStruct,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			ref int referenceOrdinal)
		{
			if (expression == null)
				return string.Empty;

			if (expression is IdentifierExpr identifier)
			{
				if (identifier != null
					&& !string.IsNullOrWhiteSpace(identifier.Name)
					&& variableTypes.TryGetValue(identifier.Name, out string typeName)
					&& !string.IsNullOrWhiteSpace(typeName))
				{
					return GetBaseTypeName(typeName);
				}

				return string.Empty;
			}

			if (expression is CallExpr call)
			{
				if (call.Arguments != null)
				{
					for (int i = 0; i < call.Arguments.Count; i++)
					{
						ResolveExpressionTypeAndEmitFieldReferences(
							call.Arguments[i],
							variableTypes,
							structFieldSymbolsByStruct,
							source,
							normalizedDocument,
							documentPath,
							aggregateId,
							snapshotVersion,
							output,
							ref referenceOrdinal);
					}
				}

				return string.Empty;
			}

			if (expression is MemberCallExpr memberCall)
			{
				if (memberCall.Arguments != null)
				{
					for (int i = 0; i < memberCall.Arguments.Count; i++)
					{
						ResolveExpressionTypeAndEmitFieldReferences(
							memberCall.Arguments[i],
							variableTypes,
							structFieldSymbolsByStruct,
							source,
							normalizedDocument,
							documentPath,
							aggregateId,
							snapshotVersion,
							output,
							ref referenceOrdinal);
					}
				}

				if (!string.IsNullOrWhiteSpace(memberCall.TargetName)
					&& !string.IsNullOrWhiteSpace(memberCall.MemberName)
					&& variableTypes.TryGetValue(memberCall.TargetName, out string targetType)
					&& TryResolveStructFieldDescriptor(structFieldSymbolsByStruct, targetType, memberCall.MemberName, out StructFieldDescriptor descriptor))
				{
					int memberColumn = ResolveMemberCallMemberColumn(memberCall, source);
					AddStructFieldReferenceFact(
						descriptor.Symbol,
						memberCall.MemberName,
						memberCall.Line,
						memberColumn,
						source,
						normalizedDocument,
						documentPath,
						aggregateId,
						snapshotVersion,
						output,
						ref referenceOrdinal);

					return descriptor.FieldTypeName;
				}

				return string.Empty;
			}

			if (expression is FieldAccessExpr field)
			{
				string targetType = ResolveExpressionTypeAndEmitFieldReferences(
					field.Target,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				if (TryResolveStructFieldDescriptor(structFieldSymbolsByStruct, targetType, field.FieldName, out StructFieldDescriptor descriptor))
				{
					int fieldLine = field.FieldNameLine > 0 ? field.FieldNameLine : field.Line;
					int fieldColumn = field.FieldNameColumn > 0 ? field.FieldNameColumn : field.Column;
					AddStructFieldReferenceFact(
						descriptor.Symbol,
						field.FieldName,
						fieldLine,
						fieldColumn,
						source,
						normalizedDocument,
						documentPath,
						aggregateId,
						snapshotVersion,
						output,
						ref referenceOrdinal);

					return descriptor.FieldTypeName;
				}

				return string.Empty;
			}

			if (expression is StructLiteralExpr structLiteral)
			{
				string structType = GetBaseTypeName(structLiteral.TypeName);
				if (structLiteral.Fields != null)
				{
					for (int i = 0; i < structLiteral.Fields.Count; i++)
					{
						var fieldEntry = structLiteral.Fields[i];

						// Emit struct field name reference
						if (!string.IsNullOrWhiteSpace(fieldEntry.FieldName)
							&& !string.IsNullOrWhiteSpace(structType)
							&& fieldEntry.FieldNameLine > 0
							&& TryResolveStructFieldDescriptor(structFieldSymbolsByStruct, structType, fieldEntry.FieldName, out StructFieldDescriptor fieldDesc))
						{
							AddStructFieldReferenceFact(
								fieldDesc.Symbol,
								fieldEntry.FieldName,
								fieldEntry.FieldNameLine,
								fieldEntry.FieldNameColumn,
								source,
								normalizedDocument,
								documentPath,
								aggregateId,
								snapshotVersion,
								output,
								ref referenceOrdinal);
						}

						// Recurse into field value expression
						ResolveExpressionTypeAndEmitFieldReferences(
							fieldEntry.Value,
							variableTypes,
							structFieldSymbolsByStruct,
							source,
							normalizedDocument,
							documentPath,
							aggregateId,
							snapshotVersion,
							output,
							ref referenceOrdinal);
					}
				}

				return structType ?? string.Empty;
			}

			if (expression is BinaryExpr binary)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					binary.Left,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				ResolveExpressionTypeAndEmitFieldReferences(
					binary.Right,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				return string.Empty;
			}

			if (expression is UnaryExpr unary)
			{
				ResolveExpressionTypeAndEmitFieldReferences(
					unary.Operand,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);
				return string.Empty;
			}

			if (expression is AssignExpr assign)
			{
				string targetType = ResolveExpressionTypeAndEmitFieldReferences(
					assign.Target,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				ResolveExpressionTypeAndEmitFieldReferences(
					assign.Value,
					variableTypes,
					structFieldSymbolsByStruct,
					source,
					normalizedDocument,
					documentPath,
					aggregateId,
					snapshotVersion,
					output,
					ref referenceOrdinal);

				return targetType;
			}

			return string.Empty;
		}

		private static void AddStructFieldReferenceFact(
			SymbolIdentity symbol,
			string fieldName,
			int oneBasedLine,
			int oneBasedColumn,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			ref int referenceOrdinal)
		{
			if (symbol == null || string.IsNullOrWhiteSpace(fieldName))
				return;

			if (!TryCreateSpanFromLineColumn(source, oneBasedLine, oneBasedColumn, fieldName.Length, out TextSpan span, out int startLine, out int startCharacter, out int endLine, out int endCharacter))
				return;

			var payload = new SymbolDataFactPayload(symbol, startLine, startCharacter, endLine, endCharacter);
			output.Add(new DataFact(
				new DataFactId(BuildFactId("ref", normalizedDocument, referenceOrdinal++, fieldName, startLine, startCharacter)),
				aggregateId,
				DataFactKind.SymbolReference,
				documentPath,
				span,
				snapshotVersion,
				payload));
		}

		private static Dictionary<string, string> BuildModuleVariableTypeMap(ModuleNode module)
		{
			var output = new Dictionary<string, string>(StringComparer.Ordinal);
			if (module == null || module.ModuleVariables == null)
				return output;

			for (int i = 0; i < module.ModuleVariables.Count; i++)
			{
				VarDeclStmt variable = module.ModuleVariables[i];
				if (variable == null || string.IsNullOrWhiteSpace(variable.Name))
					continue;

				string typeName = GetBaseTypeName(variable.TypeName);
				if (!string.IsNullOrWhiteSpace(typeName))
					output[variable.Name] = typeName;
			}

			return output;
		}

		private static bool TryResolveStructFieldDescriptor(
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> structFieldSymbolsByStruct,
			string structType,
			string fieldName,
			out StructFieldDescriptor descriptor)
		{
			descriptor = null;
			if (structFieldSymbolsByStruct == null
				|| structFieldSymbolsByStruct.Count == 0
				|| string.IsNullOrWhiteSpace(structType)
				|| string.IsNullOrWhiteSpace(fieldName))
			{
				return false;
			}

			string normalizedType = GetBaseTypeName(structType);
			if (string.IsNullOrWhiteSpace(normalizedType))
				return false;

			if (!structFieldSymbolsByStruct.TryGetValue(normalizedType, out Dictionary<string, StructFieldDescriptor> fieldsByName)
				|| fieldsByName == null)
			{
				return false;
			}

			if (!fieldsByName.TryGetValue(fieldName, out descriptor) || descriptor == null || descriptor.Symbol == null)
				return false;

			return true;
		}

		private static int ResolveMemberCallMemberColumn(MemberCallExpr memberCall, string source)
		{
			if (memberCall == null)
				return 1;

			if (string.IsNullOrWhiteSpace(memberCall.MemberName))
				return memberCall.Column > 0 ? memberCall.Column : 1;

			if (!TryGetLineText(source, memberCall.Line - 1, out string lineText))
				return memberCall.Column > 0 ? memberCall.Column : 1;

			int searchStart = memberCall.Column > 0 ? memberCall.Column - 1 : 0;
			if (searchStart < 0)
				searchStart = 0;

			if (!string.IsNullOrWhiteSpace(memberCall.TargetName))
			{
				string qualified = memberCall.TargetName + "." + memberCall.MemberName;
				int qualifiedIndex = lineText.IndexOf(qualified, searchStart, StringComparison.Ordinal);
				if (qualifiedIndex >= 0)
					return qualifiedIndex + memberCall.TargetName.Length + 2;
			}

			int found = lineText.IndexOf(memberCall.MemberName, searchStart, StringComparison.Ordinal);
			if (found < 0)
				found = lineText.IndexOf(memberCall.MemberName, StringComparison.Ordinal);

			if (found >= 0)
				return found + 1;

			return memberCall.Column > 0 ? memberCall.Column : 1;
		}

		private static string BuildStructFieldPath(string parentStructName, string fieldName)
		{
			return (parentStructName ?? string.Empty) + "." + (fieldName ?? string.Empty);
		}

		private static string GetBaseTypeName(string typeName)
		{
			if (string.IsNullOrWhiteSpace(typeName))
				return string.Empty;

			int dot = typeName.IndexOf('.');
			if (dot <= 0)
				return typeName;

			return typeName.Substring(0, dot);
		}

		private sealed class StructFieldDescriptor
		{
			public StructFieldDescriptor(SymbolIdentity symbol, string fieldTypeName)
			{
				Symbol = symbol;
				FieldTypeName = GetBaseTypeName(fieldTypeName);
			}

			public SymbolIdentity Symbol { get; }
			public string FieldTypeName { get; }
		}

		private static Dictionary<string, SymbolIdentity> BuildImportedFunctionSymbols(ModuleNode module, string normalizedDocument)
		{
			var symbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			if (module == null
				|| module.Imports == null
				|| module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
			{
				return symbols;
			}

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;

				// DX21 P0-3: skip aliased imports — they stay namespaced, not flat-merged
				if (!string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0)
					continue;

				for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[candidateIndex]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;

					if (!TryReadImportedFunctionSymbols(candidateUri, out Dictionary<string, SymbolIdentity> importedSymbols, visitedTargets))
						continue;

					foreach (KeyValuePair<string, SymbolIdentity> pair in importedSymbols)
					{
						if (!symbols.ContainsKey(pair.Key) && pair.Value != null)
							symbols[pair.Key] = pair.Value;
					}

					break;
				}
			}

			return symbols;
		}

		private static bool TryReadImportedFunctionSymbols(
			string importedDocumentUri,
			out Dictionary<string, SymbolIdentity> symbols,
			HashSet<string> visitedTargets)
		{
			symbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			string normalizedUri = NormalizeDocumentKey(importedDocumentUri);
			if (string.IsNullOrWhiteSpace(normalizedUri))
				return false;

			string path = WorkspacePathTool.UriToPath(normalizedUri);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return false;

			string source;
			try
			{
				source = File.ReadAllText(path);
			}
			catch
			{
				return false;
			}

			var parser = new Parser();
			ModuleNode module = parser.Parse(source ?? string.Empty, out _);
			if (module == null)
				return true;

			if (module.Functions != null)
			{
				for (int i = 0; i < module.Functions.Count; i++)
				{
					FuncDecl function = module.Functions[i];
					if (function == null || string.IsNullOrWhiteSpace(function.Name))
						continue;
					if (function.IsPrivate) continue;

					int nameLine = function.Line;
					int nameColumn = ResolveFunctionNameColumn(function, source);
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, function.Name.Length, out TextSpan span, out _, out _, out _, out _))
						continue;

					if (!symbols.ContainsKey(function.Name))
					{
						symbols[function.Name] = new SymbolIdentity(
							SymbolKindTag.Function,
							function.Name,
							string.Empty,
							string.Empty,
							normalizedUri,
							span,
							BuildFuncDocumentation(function));
					}
				}
			}

			// CFR-03: recursively follow transitive includes
			if (module.Imports != null)
			{
				for (int i = 0; i < module.Imports.Count; i++)
				{
					ImportDecl import = module.Imports[i];
					if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
						continue;
					if (!string.IsNullOrEmpty(import.Alias))
						continue;

					List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedUri, import.ModulePath);
					if (candidates == null || candidates.Count == 0)
						continue;

					for (int ci = 0; ci < candidates.Count; ci++)
					{
						string candidateUri = NormalizeDocumentKey(candidates[ci]);
						if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
							continue;
						if (!TryReadImportedFunctionSymbols(candidateUri, out Dictionary<string, SymbolIdentity> transitiveSymbols, visitedTargets))
							continue;
						foreach (KeyValuePair<string, SymbolIdentity> pair in transitiveSymbols)
						{
							if (!symbols.ContainsKey(pair.Key) && pair.Value != null)
								symbols[pair.Key] = pair.Value;
						}
						break;
					}
				}
			}

			return true;
		}

		private static void EmitModuleVarDefinitions(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output, Dictionary<string, SymbolIdentity> nameSymbols, ref int definitionOrdinal)
		{
			if (module == null || module.ModuleVariables == null) return;
			for (int i = 0; i < module.ModuleVariables.Count; i++)
			{
				VarDeclStmt v = module.ModuleVariables[i];
				if (v == null || string.IsNullOrWhiteSpace(v.Name)) continue;
				if (!string.IsNullOrEmpty(v.AliasTarget)) continue;
				int nameLine = v.NameLine > 0 ? v.NameLine : v.Line;
				int nameColumn = v.NameColumn > 0 ? v.NameColumn : v.Column;
				if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, v.Name.Length,
					out TextSpan span, out int sl, out int sc, out int el, out int ec))
					continue;
				var symbol = new SymbolIdentity(SymbolKindTag.Variable, v.Name, string.Empty, string.Empty, normalizedDocument, span,
					documentation: BuildVariableDocumentation(v), typeName: GetBaseTypeName(v.TypeName), isPrivate: v.IsPrivate);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, v.Name, sl, sc)),
					aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
					new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				if (!nameSymbols.ContainsKey(v.Name)) nameSymbols[v.Name] = symbol;
			}
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> EmitParameterDefinitionFacts(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output, ref int definitionOrdinal)
		{
			var result = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (module == null || module.Functions == null) return result;
			for (int fi = 0; fi < module.Functions.Count; fi++)
			{
				FuncDecl function = module.Functions[fi];
				if (function == null || string.IsNullOrWhiteSpace(function.Name)) continue;
				if (function.Parameters == null || function.Parameters.Count == 0) continue;
				var paramSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
				for (int pi = 0; pi < function.Parameters.Count; pi++)
				{
					ParamDecl param = function.Parameters[pi];
					if (param == null || string.IsNullOrWhiteSpace(param.Name)) continue;
					int nameLine = param.NameLine;
					int nameColumn = param.NameColumn;
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, param.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var symbol = new SymbolIdentity(SymbolKindTag.Parameter, param.Name,
						function.Name + "." + param.Name, function.Name, normalizedDocument, span,
						documentation: BuildParameterDocumentation(param), typeName: GetBaseTypeName(param.TypeName));
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, param.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
					if (!paramSymbols.ContainsKey(param.Name))
						paramSymbols[param.Name] = symbol;
				}
				if (paramSymbols.Count > 0 && !result.ContainsKey(function.Name))
					result[function.Name] = paramSymbols;
			}
			return result;
		}

		private static Dictionary<string, List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)>> EmitLocalVarDefinitionFacts(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output, ref int definitionOrdinal)
		{
			var result = new Dictionary<string, List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)>>(StringComparer.Ordinal);
			if (module == null || module.Functions == null) return result;
			var scopedCollector = new List<(VarDeclStmt v, int scopeStart, int scopeEnd)>();
			for (int fi = 0; fi < module.Functions.Count; fi++)
			{
				FuncDecl function = module.Functions[fi];
				if (function == null || string.IsNullOrWhiteSpace(function.Name) || function.Body == null) continue;
				scopedCollector.Clear();
				int bodyEnd = ComputeMaxLine(function.Body);
				CollectScopedVarDecls(function.Body, function.Body.Line, bodyEnd, scopedCollector);
				if (scopedCollector.Count == 0) continue;
				var scopedList = new List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)>();
				for (int vi = 0; vi < scopedCollector.Count; vi++)
				{
					VarDeclStmt v = scopedCollector[vi].v;
					int scopeStart = scopedCollector[vi].scopeStart;
					int scopeEnd = scopedCollector[vi].scopeEnd;
					if (v == null || string.IsNullOrWhiteSpace(v.Name)) continue;
					int nameLine = v.NameLine > 0 ? v.NameLine : v.Line;
					int nameColumn = v.NameColumn > 0 ? v.NameColumn : v.Column;
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, v.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var symbol = new SymbolIdentity(SymbolKindTag.Variable, v.Name,
						function.Name + "." + v.Name, function.Name, normalizedDocument, span,
						documentation: BuildVariableDocumentation(v), typeName: GetBaseTypeName(v.TypeName));
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, v.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
					scopedList.Add((v.Name, symbol, scopeStart, scopeEnd));
				}
				if (scopedList.Count > 0 && !result.ContainsKey(function.Name))
					result[function.Name] = scopedList;
			}
			return result;
		}

		private static void CollectVarDeclStmtsFromBody(Stmt stmt, List<VarDeclStmt> result)
		{
			if (stmt == null) return;
			if (stmt is VarDeclStmt v) { result.Add(v); return; }
			if (stmt is BlockStmt block)
			{
				if (block.Statements != null)
					for (int i = 0; i < block.Statements.Count; i++)
						CollectVarDeclStmtsFromBody(block.Statements[i], result);
				return;
			}
			if (stmt is IfStmt ifS)
			{
				CollectVarDeclStmtsFromBody(ifS.ThenBranch, result);
				CollectVarDeclStmtsFromBody(ifS.ElseBranch, result);
				return;
			}
			if (stmt is WhileStmt wh) { CollectVarDeclStmtsFromBody(wh.Body, result); return; }
			if (stmt is ForStmt fs)
			{
				CollectVarDeclStmtsFromBody(fs.Initializer, result);
				CollectVarDeclStmtsFromBody(fs.Body, result);
				return;
			}
			if (stmt is DeferStmt ds) { CollectVarDeclStmtsFromBody(ds.Body, result); return; }
			if (stmt is UsingStmt us) { CollectVarDeclStmtsFromBody(us.Body, result); return; }
		}

		private static int ComputeMaxLine(Stmt stmt)
		{
			if (stmt == null) return 0;
			int max = stmt.Line;
			if (stmt is BlockStmt blk && blk.Statements != null)
				for (int i = 0; i < blk.Statements.Count; i++)
					max = Math.Max(max, ComputeMaxLine(blk.Statements[i]));
			if (stmt is IfStmt ifs) { max = Math.Max(max, ComputeMaxLine(ifs.ThenBranch)); max = Math.Max(max, ComputeMaxLine(ifs.ElseBranch)); }
			if (stmt is WhileStmt whs) max = Math.Max(max, ComputeMaxLine(whs.Body));
			if (stmt is ForStmt frs) { max = Math.Max(max, ComputeMaxLine(frs.Initializer)); max = Math.Max(max, ComputeMaxLine(frs.Body)); }
			if (stmt is DeferStmt dfs) max = Math.Max(max, ComputeMaxLine(dfs.Body));
			if (stmt is UsingStmt uss) max = Math.Max(max, ComputeMaxLine(uss.Body));
			return max;
		}

		private static void CollectScopedVarDecls(Stmt stmt, int scopeStartLine, int scopeEndLine,
			List<(VarDeclStmt v, int scopeStart, int scopeEnd)> result)
		{
			if (stmt == null) return;
			if (stmt is VarDeclStmt vd) { result.Add((vd, scopeStartLine, scopeEndLine)); return; }
			if (stmt is BlockStmt blk)
			{
				if (blk.Statements != null)
					for (int i = 0; i < blk.Statements.Count; i++)
						CollectScopedVarDecls(blk.Statements[i], scopeStartLine, scopeEndLine, result);
				return;
			}
			if (stmt is IfStmt ifs)
			{
				int thenEnd = ComputeMaxLine(ifs.ThenBranch);
				CollectScopedVarDecls(ifs.ThenBranch, ifs.ThenBranch != null ? ifs.ThenBranch.Line : ifs.Line, thenEnd, result);
				if (ifs.ElseBranch != null)
				{
					int elseEnd = ComputeMaxLine(ifs.ElseBranch);
					CollectScopedVarDecls(ifs.ElseBranch, ifs.ElseBranch.Line, elseEnd, result);
				}
				return;
			}
			if (stmt is WhileStmt whs)
			{
				int whEnd = ComputeMaxLine(whs.Body);
				CollectScopedVarDecls(whs.Body, whs.Line, whEnd, result);
				return;
			}
			if (stmt is ForStmt frs)
			{
				int frEnd = ComputeMaxLine(frs.Body);
				CollectScopedVarDecls(frs.Initializer, frs.Line, frEnd, result);
				CollectScopedVarDecls(frs.Body, frs.Line, frEnd, result);
				return;
			}
			if (stmt is DeferStmt dfs)
			{
				int dfEnd = ComputeMaxLine(dfs.Body);
				CollectScopedVarDecls(dfs.Body, dfs.Line, dfEnd, result);
				return;
			}
			if (stmt is UsingStmt uss)
			{
				int usEnd = ComputeMaxLine(uss.Body);
				CollectScopedVarDecls(uss.Body, uss.Line, usEnd, result);
				return;
			}
		}

		private static SymbolIdentity ResolveScopedLocalVar(
			List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)> scopedLocals,
			string name, int line)
		{
			SymbolIdentity best = null;
			int bestWidth = int.MaxValue;
			for (int i = 0; i < scopedLocals.Count; i++)
			{
				var entry = scopedLocals[i];
				if (!string.Equals(entry.name, name, StringComparison.Ordinal)) continue;
				if (line < entry.scopeStart || line > entry.scopeEnd) continue;
				int width = entry.scopeEnd - entry.scopeStart;
				if (width < bestWidth)
				{
					bestWidth = width;
					best = entry.symbol;
				}
			}
			return best;
		}

		private static void EmitStructNameDefinitions(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output, Dictionary<string, SymbolIdentity> nameSymbols, ref int definitionOrdinal)
		{
			if (module == null || module.Structs == null) return;
			for (int i = 0; i < module.Structs.Count; i++)
			{
				StructDecl s = module.Structs[i];
				if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
				if (!string.IsNullOrEmpty(s.AliasTarget)) continue;
				int nameLine = s.NameLine > 0 ? s.NameLine : s.Line;
				int nameColumn = s.NameColumn > 0 ? s.NameColumn : s.Column;
				if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, s.Name.Length,
					out TextSpan span, out int sl, out int sc, out int el, out int ec))
					continue;
				var symbol = new SymbolIdentity(SymbolKindTag.Struct, s.Name, string.Empty, string.Empty, normalizedDocument, span, BuildStructDocumentation(s), typeName: null, isPrivate: s.IsPrivate);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, s.Name, sl, sc)),
					aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
					new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				if (!nameSymbols.ContainsKey(s.Name)) nameSymbols[s.Name] = symbol;
			}
		}

		private static void EmitEnumDefinitions(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output, Dictionary<string, SymbolIdentity> nameSymbols, ref int definitionOrdinal)
		{
			if (module == null || module.Enums == null) return;
			for (int i = 0; i < module.Enums.Count; i++)
			{
				EnumDecl e = module.Enums[i];
				if (e == null || string.IsNullOrWhiteSpace(e.Name)) continue;
				if (!string.IsNullOrEmpty(e.AliasTarget)) continue;
				int nameLine = e.NameLine > 0 ? e.NameLine : e.Line;
				int nameColumn = e.NameColumn > 0 ? e.NameColumn : e.Column;
				if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, e.Name.Length,
					out TextSpan span, out int sl, out int sc, out int el, out int ec))
					continue;
				var symbol = new SymbolIdentity(SymbolKindTag.Enum, e.Name, string.Empty, string.Empty, normalizedDocument, span, BuildEnumDocumentation(e), typeName: null, isPrivate: e.IsPrivate);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, e.Name, sl, sc)),
					aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
					new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				if (!nameSymbols.ContainsKey(e.Name)) nameSymbols[e.Name] = symbol;
			}
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> BuildLocalEnumMemberSymbols(
			ModuleNode module,
			string normalizedDocument,
			string source,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			ref int definitionOrdinal)
		{
			var symbolsByEnum = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (module == null || module.Enums == null || module.Enums.Count == 0)
				return symbolsByEnum;

			for (int i = 0; i < module.Enums.Count; i++)
			{
				EnumDecl enumDecl = module.Enums[i];
				if (enumDecl == null || string.IsNullOrWhiteSpace(enumDecl.Name) || enumDecl.Members == null)
					continue;
				if (!string.IsNullOrEmpty(enumDecl.AliasTarget))
					continue;

				string enumName = enumDecl.Name;
				if (!symbolsByEnum.TryGetValue(enumName, out Dictionary<string, SymbolIdentity> membersByName))
				{
					membersByName = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
					symbolsByEnum[enumName] = membersByName;
				}

				for (int mi = 0; mi < enumDecl.Members.Count; mi++)
				{
					EnumMember member = enumDecl.Members[mi];
					if (member == null || string.IsNullOrWhiteSpace(member.Name))
						continue;

					int memberLine = member.Line > 0 ? member.Line : enumDecl.Line;
					int memberColumn = member.Column > 0 ? member.Column : enumDecl.Column;
					if (!TryCreateSpanFromLineColumn(source, memberLine, memberColumn, member.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;

					if (!membersByName.ContainsKey(member.Name))
					{
						var symbol = new SymbolIdentity(
							SymbolKindTag.EnumMember,
							member.Name,
							enumName + "." + member.Name,
							enumName,
							normalizedDocument,
							span);
						membersByName[member.Name] = symbol;
					}

					SymbolIdentity memberSymbol = membersByName[member.Name];
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, definitionOrdinal++, member.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, new PathKey(normalizedDocument), span, snapshotVersion,
						new SymbolDataFactPayload(memberSymbol, sl, sc, el, ec)));
				}
			}

			return symbolsByEnum;
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> BuildImportedEnumMemberSymbols(
			ModuleNode module, string normalizedDocument)
		{
			var symbols = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (module == null || module.Imports == null || module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
				return symbols;

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;
				if (!string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0)
					continue;

				for (int ci = 0; ci < candidates.Count; ci++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[ci]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;

					if (!TryReadImportedEnumMemberSymbols(candidateUri, out Dictionary<string, Dictionary<string, SymbolIdentity>> importedSymbols, visitedTargets))
						continue;

					foreach (KeyValuePair<string, Dictionary<string, SymbolIdentity>> pair in importedSymbols)
					{
						if (!symbols.TryGetValue(pair.Key, out Dictionary<string, SymbolIdentity> existing))
						{
							existing = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
							symbols[pair.Key] = existing;
						}

						foreach (KeyValuePair<string, SymbolIdentity> member in pair.Value)
						{
							if (!existing.ContainsKey(member.Key) && member.Value != null)
								existing[member.Key] = member.Value;
						}
					}

					break;
				}
			}
			return symbols;
		}

		private static bool TryReadImportedEnumMemberSymbols(
			string importedDocumentUri,
			out Dictionary<string, Dictionary<string, SymbolIdentity>> symbols,
			HashSet<string> visitedTargets)
		{
			symbols = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			string normalizedUri = NormalizeDocumentKey(importedDocumentUri);
			if (string.IsNullOrWhiteSpace(normalizedUri))
				return false;

			string path = WorkspacePathTool.UriToPath(normalizedUri);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
				return false;

			string source;
			try { source = File.ReadAllText(path); }
			catch { return false; }

			var parser = new Parser();
			ModuleNode module = parser.Parse(source ?? string.Empty, out _);
			if (module == null)
				return true;

			if (module.Enums != null)
			{
				for (int ei = 0; ei < module.Enums.Count; ei++)
				{
					EnumDecl enumDecl = module.Enums[ei];
					if (enumDecl == null || string.IsNullOrWhiteSpace(enumDecl.Name) || enumDecl.Members == null)
						continue;
					if (enumDecl.IsPrivate) continue;

					if (!symbols.TryGetValue(enumDecl.Name, out Dictionary<string, SymbolIdentity> membersByName))
					{
						membersByName = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
						symbols[enumDecl.Name] = membersByName;
					}

					for (int mi = 0; mi < enumDecl.Members.Count; mi++)
					{
						EnumMember member = enumDecl.Members[mi];
						if (member == null || string.IsNullOrWhiteSpace(member.Name) || membersByName.ContainsKey(member.Name))
							continue;

						int memberLine = member.Line > 0 ? member.Line : enumDecl.Line;
						int memberColumn = member.Column > 0 ? member.Column : enumDecl.Column;
						if (!TryCreateSpanFromLineColumn(source, memberLine, memberColumn, member.Name.Length,
							out TextSpan span, out _, out _, out _, out _))
							continue;

						membersByName[member.Name] = new SymbolIdentity(
							SymbolKindTag.EnumMember,
							member.Name,
							enumDecl.Name + "." + member.Name,
							enumDecl.Name,
							normalizedUri,
							span);
					}
				}
			}

			// CFR-03: recursively follow transitive includes
			if (module.Imports != null)
			{
				for (int ii = 0; ii < module.Imports.Count; ii++)
				{
					ImportDecl transitiveImport = module.Imports[ii];
					if (transitiveImport == null || string.IsNullOrWhiteSpace(transitiveImport.ModulePath))
						continue;
					if (!string.IsNullOrEmpty(transitiveImport.Alias))
						continue;

					List<string> transCandidates = IncludeTargetResolver.ResolveCandidates(normalizedUri, transitiveImport.ModulePath);
					if (transCandidates == null || transCandidates.Count == 0)
						continue;

					for (int tci = 0; tci < transCandidates.Count; tci++)
					{
						string transUri = NormalizeDocumentKey(transCandidates[tci]);
						if (string.IsNullOrWhiteSpace(transUri) || !visitedTargets.Add(transUri))
							continue;

						if (!TryReadImportedEnumMemberSymbols(transUri, out Dictionary<string, Dictionary<string, SymbolIdentity>> transitiveSymbols, visitedTargets))
							continue;

						foreach (KeyValuePair<string, Dictionary<string, SymbolIdentity>> pair in transitiveSymbols)
						{
							if (!symbols.TryGetValue(pair.Key, out Dictionary<string, SymbolIdentity> existing))
							{
								existing = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
								symbols[pair.Key] = existing;
							}

							foreach (KeyValuePair<string, SymbolIdentity> member in pair.Value)
							{
								if (!existing.ContainsKey(member.Key) && member.Value != null)
									existing[member.Key] = member.Value;
							}
						}

						break;
					}
				}
			}

			return true;
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> MergeEnumMemberSymbols(
			Dictionary<string, Dictionary<string, SymbolIdentity>> localSymbols,
			Dictionary<string, Dictionary<string, SymbolIdentity>> importedSymbols)
		{
			var merged = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (importedSymbols != null)
			{
				foreach (KeyValuePair<string, Dictionary<string, SymbolIdentity>> enumPair in importedSymbols)
				{
					if (string.IsNullOrWhiteSpace(enumPair.Key) || enumPair.Value == null) continue;
					merged[enumPair.Key] = new Dictionary<string, SymbolIdentity>(enumPair.Value, StringComparer.Ordinal);
				}
			}
			if (localSymbols != null)
			{
				foreach (KeyValuePair<string, Dictionary<string, SymbolIdentity>> enumPair in localSymbols)
				{
					if (string.IsNullOrWhiteSpace(enumPair.Key) || enumPair.Value == null) continue;
					if (!merged.TryGetValue(enumPair.Key, out Dictionary<string, SymbolIdentity> existing))
					{
						merged[enumPair.Key] = new Dictionary<string, SymbolIdentity>(enumPair.Value, StringComparer.Ordinal);
					}
					else
					{
						foreach (KeyValuePair<string, SymbolIdentity> memberPair in enumPair.Value)
						{
							if (!string.IsNullOrWhiteSpace(memberPair.Key) && memberPair.Value != null)
								existing[memberPair.Key] = memberPair.Value;
						}
					}
				}
			}
			return merged;
		}

		private static void EmitEnumMemberReferenceFacts(
			ModuleNode module,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, Dictionary<string, SymbolIdentity>> enumMemberSymbolsByEnum,
			ref int referenceOrdinal)
		{
			if (module == null || enumMemberSymbolsByEnum == null || enumMemberSymbolsByEnum.Count == 0)
				return;

			Action<Expr> walkExpr = null;
			Action<Stmt> walkStmt = null;

			int ordinal = referenceOrdinal;

			walkExpr = expr =>
			{
				if (expr == null) return;
				if (expr is FieldAccessExpr fieldAccess)
				{
					if (fieldAccess.Target is IdentifierExpr targetIdent
						&& !string.IsNullOrWhiteSpace(targetIdent.Name)
						&& !string.IsNullOrWhiteSpace(fieldAccess.FieldName)
						&& enumMemberSymbolsByEnum.TryGetValue(targetIdent.Name, out Dictionary<string, SymbolIdentity> members)
						&& members.TryGetValue(fieldAccess.FieldName, out SymbolIdentity memberSymbol))
					{
						int fieldLine = fieldAccess.FieldNameLine > 0 ? fieldAccess.FieldNameLine : fieldAccess.Line;
						int fieldColumn = fieldAccess.FieldNameColumn > 0 ? fieldAccess.FieldNameColumn : fieldAccess.Column;
						if (TryCreateSpanFromLineColumn(source, fieldLine, fieldColumn, fieldAccess.FieldName.Length,
							out TextSpan span, out int sl, out int sc, out int el, out int ec))
						{
							output.Add(new DataFact(
								new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, fieldAccess.FieldName, sl, sc)),
								aggregateId, DataFactKind.SymbolReference, documentPath, span, snapshotVersion,
								new SymbolDataFactPayload(memberSymbol, sl, sc, el, ec)));
						}
					}
					walkExpr(fieldAccess.Target);
					return;
				}
				if (expr is CallExpr call)
				{
					if (call.Arguments != null)
						for (int i = 0; i < call.Arguments.Count; i++)
							walkExpr(call.Arguments[i]);
					return;
				}
				if (expr is BinaryExpr binary) { walkExpr(binary.Left); walkExpr(binary.Right); return; }
				if (expr is UnaryExpr unary) { walkExpr(unary.Operand); return; }
				if (expr is AssignExpr assign) { walkExpr(assign.Target); walkExpr(assign.Value); return; }
				if (expr is StructLiteralExpr structLit)
				{
					if (structLit.Fields != null)
						for (int i = 0; i < structLit.Fields.Count; i++)
							walkExpr(structLit.Fields[i].Value);
					return;
				}
			};

			walkStmt = stmt =>
			{
				if (stmt == null) return;
				if (stmt is BlockStmt block)
				{
					if (block.Statements != null)
						for (int i = 0; i < block.Statements.Count; i++)
							walkStmt(block.Statements[i]);
					return;
				}
				if (stmt is VarDeclStmt variable) { walkExpr(variable.Initializer); return; }
				if (stmt is IfStmt cond) { walkExpr(cond.Condition); walkStmt(cond.ThenBranch); walkStmt(cond.ElseBranch); return; }
				if (stmt is WhileStmt loop) { walkExpr(loop.Condition); walkStmt(loop.Body); return; }
				if (stmt is ForStmt forLoop) { walkStmt(forLoop.Initializer); walkExpr(forLoop.Condition); walkExpr(forLoop.Increment); walkStmt(forLoop.Body); return; }
				if (stmt is ReturnStmt ret) { walkExpr(ret.Value); return; }
				if (stmt is WaitStmt waited) { walkExpr(waited.FrameCount); return; }
				if (stmt is ExprStmt exprStmt) { walkExpr(exprStmt.Expression); return; }
			};

			if (module.Functions != null)
			{
				for (int fi = 0; fi < module.Functions.Count; fi++)
				{
					FuncDecl function = module.Functions[fi];
					if (function == null || function.Body == null) continue;
					walkStmt(function.Body);
				}
			}

			if (module.ModuleVariables != null)
			{
				for (int vi = 0; vi < module.ModuleVariables.Count; vi++)
				{
					VarDeclStmt variable = module.ModuleVariables[vi];
					if (variable != null) walkExpr(variable.Initializer);
				}
			}

			referenceOrdinal = ordinal;
		}

		private static void EmitIdentifierReferenceFacts(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, SymbolIdentity> localNameSymbols,
			Dictionary<string, SymbolIdentity> importedNameSymbols,
			Dictionary<string, Dictionary<string, SymbolIdentity>> parameterSymbolsByFunc,
			Dictionary<string, List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)>> localVarSymbolsByFunc,
			ref int referenceOrdinal)
		{
			if (module == null || module.Functions == null) return;
			int ordinal = referenceOrdinal;

			// DX22 P5: emit module-level variable type annotation + initializer references (L4/L7)
			if (module.ModuleVariables != null)
			{
				for (int vi = 0; vi < module.ModuleVariables.Count; vi++)
				{
					VarDeclStmt mv = module.ModuleVariables[vi];
					if (mv == null) continue;
					// type annotation reference
					if (!string.IsNullOrWhiteSpace(mv.TypeName) && mv.TypeNameLine > 0 && mv.TypeNameColumn > 0)
					{
						if (localNameSymbols.TryGetValue(mv.TypeName, out SymbolIdentity mvTypeSym) && mvTypeSym != null
							|| importedNameSymbols.TryGetValue(mv.TypeName, out mvTypeSym) && mvTypeSym != null)
						{
							if (TryCreateSpanFromLineColumn(source, mv.TypeNameLine, mv.TypeNameColumn, mv.TypeName.Length,
								out TextSpan mvSpan, out int mvSl, out int mvSc, out int mvEl, out int mvEc))
							{
								output.Add(new DataFact(
									new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, mv.TypeName, mvSl, mvSc)),
									aggregateId, DataFactKind.SymbolReference, documentPath, mvSpan, snapshotVersion,
									new SymbolDataFactPayload(mvTypeSym, mvSl, mvSc, mvEl, mvEc)));
							}
						}
					}
					// initializer expression references
					CollectIdentifierReferencesFromExpression(mv.Initializer, ident =>
					{
						if (ident == null || string.IsNullOrWhiteSpace(ident.Name)) return;
						if (!localNameSymbols.TryGetValue(ident.Name, out SymbolIdentity sym) || sym == null)
							if (!importedNameSymbols.TryGetValue(ident.Name, out sym) || sym == null)
								return;
						if (!TryCreateSpanFromLineColumn(source, ident.Line, ident.Column, ident.Name.Length,
							out TextSpan span, out int sl, out int sc, out int el, out int ec))
							return;
						output.Add(new DataFact(
							new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, ident.Name, sl, sc)),
							aggregateId, DataFactKind.SymbolReference, documentPath, span, snapshotVersion,
							new SymbolDataFactPayload(sym, sl, sc, el, ec)));
					});
				}
			}

			for (int fi = 0; fi < module.Functions.Count; fi++)
			{
				FuncDecl function = module.Functions[fi];
				if (function == null) continue;

				// DX22 P5: emit parameter type annotation references (L5)
				if (function.Parameters != null)
				{
					for (int pi = 0; pi < function.Parameters.Count; pi++)
					{
						ParamDecl param = function.Parameters[pi];
						if (param == null || string.IsNullOrWhiteSpace(param.TypeName)) continue;
						if (param.TypeNameLine <= 0 || param.TypeNameColumn <= 0) continue;
						if (!localNameSymbols.TryGetValue(param.TypeName, out SymbolIdentity ptSym) || ptSym == null)
							if (!importedNameSymbols.TryGetValue(param.TypeName, out ptSym) || ptSym == null)
								continue;
						if (!TryCreateSpanFromLineColumn(source, param.TypeNameLine, param.TypeNameColumn, param.TypeName.Length,
							out TextSpan ptSpan, out int ptSl, out int ptSc, out int ptEl, out int ptEc))
							continue;
						output.Add(new DataFact(
							new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, param.TypeName, ptSl, ptSc)),
							aggregateId, DataFactKind.SymbolReference, documentPath, ptSpan, snapshotVersion,
							new SymbolDataFactPayload(ptSym, ptSl, ptSc, ptEl, ptEc)));
					}
				}

				if (function.Body == null) continue;
				Dictionary<string, SymbolIdentity> paramSymbols = null;
				List<(string name, SymbolIdentity symbol, int scopeStart, int scopeEnd)> scopedLocals = null;
				if (function.Name != null)
				{
					parameterSymbolsByFunc?.TryGetValue(function.Name, out paramSymbols);
					localVarSymbolsByFunc?.TryGetValue(function.Name, out scopedLocals);
				}
				CollectIdentifierReferencesFromStatement(function.Body, ident =>
				{
					if (ident == null || string.IsNullOrWhiteSpace(ident.Name)) return;
					SymbolIdentity symbol = null;
					if (scopedLocals != null) symbol = ResolveScopedLocalVar(scopedLocals, ident.Name, ident.Line);
					if (symbol == null && paramSymbols != null) paramSymbols.TryGetValue(ident.Name, out symbol);
					if (symbol == null && !localNameSymbols.TryGetValue(ident.Name, out symbol))
						importedNameSymbols.TryGetValue(ident.Name, out symbol);
					if (symbol == null) return;
					if (!TryCreateSpanFromLineColumn(source, ident.Line, ident.Column, ident.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						return;
					output.Add(new DataFact(
						new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, ident.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolReference, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				});
			}
			referenceOrdinal = ordinal;
		}

		private static void CollectIdentifierReferencesFromStatement(Stmt statement, Action<IdentifierExpr> onIdent)
		{
			if (statement == null || onIdent == null) return;
			if (statement is BlockStmt block)
			{
				if (block.Statements == null) return;
				for (int i = 0; i < block.Statements.Count; i++)
					CollectIdentifierReferencesFromStatement(block.Statements[i], onIdent);
				return;
			}
			if (statement is VarDeclStmt variable)
			{
				// DX21: emit type-annotation name as identifier reference
				if (!string.IsNullOrWhiteSpace(variable.TypeName) && variable.TypeNameLine > 0 && variable.TypeNameColumn > 0)
				{
					var typeIdent = new IdentifierExpr(variable.TypeName);
					typeIdent.Line = variable.TypeNameLine;
					typeIdent.Column = variable.TypeNameColumn;
					onIdent(typeIdent);
				}
				CollectIdentifierReferencesFromExpression(variable.Initializer, onIdent);
				return;
			}
			if (statement is IfStmt conditional)
			{
				CollectIdentifierReferencesFromExpression(conditional.Condition, onIdent);
				CollectIdentifierReferencesFromStatement(conditional.ThenBranch, onIdent);
				CollectIdentifierReferencesFromStatement(conditional.ElseBranch, onIdent);
				return;
			}
			if (statement is WhileStmt loop)
			{
				CollectIdentifierReferencesFromExpression(loop.Condition, onIdent);
				CollectIdentifierReferencesFromStatement(loop.Body, onIdent);
				return;
			}
			if (statement is ForStmt forLoop)
			{
				CollectIdentifierReferencesFromStatement(forLoop.Initializer, onIdent);
				CollectIdentifierReferencesFromExpression(forLoop.Condition, onIdent);
				CollectIdentifierReferencesFromExpression(forLoop.Increment, onIdent);
				CollectIdentifierReferencesFromStatement(forLoop.Body, onIdent);
				return;
			}
			if (statement is ReturnStmt returned)
			{
				CollectIdentifierReferencesFromExpression(returned.Value, onIdent);
				return;
			}
			if (statement is WaitStmt waited)
			{
				CollectIdentifierReferencesFromExpression(waited.FrameCount, onIdent);
				return;
			}
			if (statement is ExprStmt expression)
			{
				CollectIdentifierReferencesFromExpression(expression.Expression, onIdent);
				return;
			}
		}

		private static void CollectIdentifierReferencesFromExpression(Expr expression, Action<IdentifierExpr> onIdent)
		{
			if (expression == null || onIdent == null) return;
			if (expression is IdentifierExpr ident)
			{
				onIdent(ident);
				return;
			}
			if (expression is CallExpr call)
			{
				if (call.Arguments != null)
					for (int i = 0; i < call.Arguments.Count; i++)
						CollectIdentifierReferencesFromExpression(call.Arguments[i], onIdent);
				return;
			}
			if (expression is BinaryExpr binary)
			{
				CollectIdentifierReferencesFromExpression(binary.Left, onIdent);
				CollectIdentifierReferencesFromExpression(binary.Right, onIdent);
				return;
			}
			if (expression is UnaryExpr unary)
			{
				CollectIdentifierReferencesFromExpression(unary.Operand, onIdent);
				return;
			}
			if (expression is AssignExpr assign)
			{
				CollectIdentifierReferencesFromExpression(assign.Target, onIdent);
				CollectIdentifierReferencesFromExpression(assign.Value, onIdent);
				return;
			}
			if (expression is FieldAccessExpr fieldAccess)
			{
				CollectIdentifierReferencesFromExpression(fieldAccess.Target, onIdent);
				return;
			}
			if (expression is StructLiteralExpr structLit)
			{
				if (!string.IsNullOrWhiteSpace(structLit.TypeName) && !structLit.TypeName.Contains('.')
					&& structLit.Line > 0 && structLit.Column > 0)
				{
					var typeIdent = new IdentifierExpr(structLit.TypeName);
					typeIdent.Line = structLit.Line;
					typeIdent.Column = structLit.Column;
					onIdent(typeIdent);
				}
				if (structLit.Fields != null)
					for (int i = 0; i < structLit.Fields.Count; i++)
						CollectIdentifierReferencesFromExpression(structLit.Fields[i].Value, onIdent);
				return;
			}
		}

		private static Dictionary<string, SymbolIdentity> BuildImportedNameSymbols(ModuleNode module, string normalizedDocument)
		{
			var symbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			if (module == null || module.Imports == null || module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
				return symbols;

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;
				// DX21 P0-3: skip aliased imports
				if (!string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0) continue;

				for (int ci = 0; ci < candidates.Count; ci++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[ci]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;
					if (!TryReadImportedNameSymbols(candidateUri, out Dictionary<string, SymbolIdentity> imported, visitedTargets))
						continue;
					foreach (KeyValuePair<string, SymbolIdentity> pair in imported)
					{
						if (!symbols.ContainsKey(pair.Key) && pair.Value != null)
							symbols[pair.Key] = pair.Value;
					}
					break;
				}
			}
			return symbols;
		}

		private static bool TryReadImportedNameSymbols(
			string importedDocumentUri,
			out Dictionary<string, SymbolIdentity> symbols,
			HashSet<string> visitedTargets)
		{
			symbols = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			string normalizedUri = NormalizeDocumentKey(importedDocumentUri);
			if (string.IsNullOrWhiteSpace(normalizedUri)) return false;

			string path = WorkspacePathTool.UriToPath(normalizedUri);
			if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

			string source;
			try { source = File.ReadAllText(path); }
			catch { return false; }

			var parser = new Parser();
			ModuleNode module = parser.Parse(source ?? string.Empty, out _);
			if (module == null) return true;

			if (module.ModuleVariables != null)
			{
				for (int i = 0; i < module.ModuleVariables.Count; i++)
				{
					VarDeclStmt v = module.ModuleVariables[i];
					if (v == null || string.IsNullOrWhiteSpace(v.Name)) continue;
					if (v.IsPrivate) continue;
					int nl = v.NameLine > 0 ? v.NameLine : v.Line;
					int nc = v.NameColumn > 0 ? v.NameColumn : v.Column;
					if (!TryCreateSpanFromLineColumn(source, nl, nc, v.Name.Length, out TextSpan span, out _, out _, out _, out _))
						continue;
					if (!symbols.ContainsKey(v.Name))
						symbols[v.Name] = new SymbolIdentity(SymbolKindTag.Variable, v.Name, string.Empty, string.Empty, normalizedUri, span,
							documentation: null, typeName: GetBaseTypeName(v.TypeName), isPrivate: v.IsPrivate);
				}
			}

			if (module.Structs != null)
			{
				for (int i = 0; i < module.Structs.Count; i++)
				{
					StructDecl s = module.Structs[i];
					if (s == null || string.IsNullOrWhiteSpace(s.Name)) continue;
					if (s.IsPrivate) continue;
					int snl = s.NameLine > 0 ? s.NameLine : s.Line;
					int snc = s.NameColumn > 0 ? s.NameColumn : s.Column;
					if (!TryCreateSpanFromLineColumn(source, snl, snc, s.Name.Length, out TextSpan span, out _, out _, out _, out _))
						continue;
					if (!symbols.ContainsKey(s.Name))
						symbols[s.Name] = new SymbolIdentity(SymbolKindTag.Struct, s.Name, string.Empty, string.Empty, normalizedUri, span, BuildStructDocumentation(s));
				}
			}

			if (module.Enums != null)
			{
				for (int i = 0; i < module.Enums.Count; i++)
				{
					EnumDecl en = module.Enums[i];
					if (en == null || string.IsNullOrWhiteSpace(en.Name)) continue;
					if (en.IsPrivate) continue;
					int enl = en.NameLine > 0 ? en.NameLine : en.Line;
					int enc = en.NameColumn > 0 ? en.NameColumn : en.Column;
					if (!TryCreateSpanFromLineColumn(source, enl, enc, en.Name.Length, out TextSpan span, out _, out _, out _, out _))
						continue;
					if (!symbols.ContainsKey(en.Name))
						symbols[en.Name] = new SymbolIdentity(SymbolKindTag.Enum, en.Name, string.Empty, string.Empty, normalizedUri, span, BuildEnumDocumentation(en));
				}
			}

			// CFR-03: recursively follow transitive includes
			if (module.Imports != null)
			{
				for (int i = 0; i < module.Imports.Count; i++)
				{
					ImportDecl import = module.Imports[i];
					if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
						continue;
					if (!string.IsNullOrEmpty(import.Alias))
						continue;

					List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedUri, import.ModulePath);
					if (candidates == null || candidates.Count == 0)
						continue;

					for (int ci = 0; ci < candidates.Count; ci++)
					{
						string candidateUri = NormalizeDocumentKey(candidates[ci]);
						if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
							continue;
						if (!TryReadImportedNameSymbols(candidateUri, out Dictionary<string, SymbolIdentity> transitiveSymbols, visitedTargets))
							continue;
						foreach (KeyValuePair<string, SymbolIdentity> pair in transitiveSymbols)
						{
							if (!symbols.ContainsKey(pair.Key) && pair.Value != null)
								symbols[pair.Key] = pair.Value;
						}
						break;
					}
				}
			}

			return true;
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> BuildAliasedFunctionSymbols(
			ModuleNode module, string normalizedDocument)
		{
			var result = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (module == null || module.Imports == null || module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
				return result;

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;
				if (string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0) continue;

				for (int ci = 0; ci < candidates.Count; ci++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[ci]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;
					if (!TryReadImportedFunctionSymbols(candidateUri, out Dictionary<string, SymbolIdentity> imported, visitedTargets))
						continue;
					if (!result.ContainsKey(import.Alias))
						result[import.Alias] = imported;
					break;
				}
			}
			return result;
		}

		private static Dictionary<string, Dictionary<string, SymbolIdentity>> BuildAliasedNameSymbols(
			ModuleNode module, string normalizedDocument)
		{
			var result = new Dictionary<string, Dictionary<string, SymbolIdentity>>(StringComparer.Ordinal);
			if (module == null || module.Imports == null || module.Imports.Count == 0
				|| string.IsNullOrWhiteSpace(normalizedDocument))
				return result;

			var visitedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;
				if (string.IsNullOrEmpty(import.Alias))
					continue;

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0) continue;

				for (int ci = 0; ci < candidates.Count; ci++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[ci]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;
					if (!TryReadImportedNameSymbols(candidateUri, out Dictionary<string, SymbolIdentity> imported, visitedTargets))
						continue;
					if (!result.ContainsKey(import.Alias))
						result[import.Alias] = imported;
					break;
				}
			}
			return result;
		}

		private static void EmitAliasedCallReferenceFacts(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedFunctionSymbols,
			Dictionary<string, SymbolIdentity> overrideFunctionSymbols,
			ref int referenceOrdinal)
		{
			if (module == null || module.Functions == null || aliasedFunctionSymbols == null || aliasedFunctionSymbols.Count == 0)
				return;
			int ordinal = referenceOrdinal;
			for (int fi = 0; fi < module.Functions.Count; fi++)
			{
				FuncDecl function = module.Functions[fi];
				if (function == null || function.Body == null) continue;
				CollectMemberCallReferencesFromStatement(function.Body, memberCall =>
				{
					if (memberCall == null || string.IsNullOrWhiteSpace(memberCall.TargetName)
						|| string.IsNullOrWhiteSpace(memberCall.MemberName))
						return;
					// DX21 P3: check override symbols first
					string overrideKey = memberCall.TargetName + "." + memberCall.MemberName;
					SymbolIdentity symbol = null;
					if (overrideFunctionSymbols != null)
						overrideFunctionSymbols.TryGetValue(overrideKey, out symbol);
					if (symbol == null)
					{
						if (!aliasedFunctionSymbols.TryGetValue(memberCall.TargetName, out Dictionary<string, SymbolIdentity> funcsByName)
							|| funcsByName == null)
							return;
						if (!funcsByName.TryGetValue(memberCall.MemberName, out symbol) || symbol == null)
							return;
					}
					int memberColumn = ResolveMemberCallMemberColumn(memberCall, source);
					if (!TryCreateSpanFromLineColumn(source, memberCall.Line, memberColumn, memberCall.MemberName.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						return;
					output.Add(new DataFact(
						new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, memberCall.MemberName, sl, sc)),
						aggregateId, DataFactKind.SymbolReference, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				});
			}
			referenceOrdinal = ordinal;
		}

		private static void CollectMemberCallReferencesFromStatement(Stmt statement, Action<MemberCallExpr> onMemberCall)
		{
			if (statement == null || onMemberCall == null) return;
			if (statement is BlockStmt block)
			{
				if (block.Statements == null) return;
				for (int i = 0; i < block.Statements.Count; i++)
					CollectMemberCallReferencesFromStatement(block.Statements[i], onMemberCall);
				return;
			}
			if (statement is VarDeclStmt variable)
			{
				CollectMemberCallReferencesFromExpression(variable.Initializer, onMemberCall);
				return;
			}
			if (statement is IfStmt conditional)
			{
				CollectMemberCallReferencesFromExpression(conditional.Condition, onMemberCall);
				CollectMemberCallReferencesFromStatement(conditional.ThenBranch, onMemberCall);
				CollectMemberCallReferencesFromStatement(conditional.ElseBranch, onMemberCall);
				return;
			}
			if (statement is WhileStmt loop)
			{
				CollectMemberCallReferencesFromExpression(loop.Condition, onMemberCall);
				CollectMemberCallReferencesFromStatement(loop.Body, onMemberCall);
				return;
			}
			if (statement is ForStmt forLoop)
			{
				CollectMemberCallReferencesFromStatement(forLoop.Initializer, onMemberCall);
				CollectMemberCallReferencesFromExpression(forLoop.Condition, onMemberCall);
				CollectMemberCallReferencesFromExpression(forLoop.Increment, onMemberCall);
				CollectMemberCallReferencesFromStatement(forLoop.Body, onMemberCall);
				return;
			}
			if (statement is ReturnStmt returned)
			{
				CollectMemberCallReferencesFromExpression(returned.Value, onMemberCall);
				return;
			}
			if (statement is WaitStmt waited)
			{
				CollectMemberCallReferencesFromExpression(waited.FrameCount, onMemberCall);
				return;
			}
			if (statement is ExprStmt expression)
			{
				CollectMemberCallReferencesFromExpression(expression.Expression, onMemberCall);
				return;
			}
		}

		private static void CollectMemberCallReferencesFromExpression(Expr expression, Action<MemberCallExpr> onMemberCall)
		{
			if (expression == null || onMemberCall == null) return;
			if (expression is MemberCallExpr memberCall)
			{
				onMemberCall(memberCall);
				if (memberCall.Arguments != null)
					for (int i = 0; i < memberCall.Arguments.Count; i++)
						CollectMemberCallReferencesFromExpression(memberCall.Arguments[i], onMemberCall);
				return;
			}
			if (expression is CallExpr call)
			{
				if (call.Arguments != null)
					for (int i = 0; i < call.Arguments.Count; i++)
						CollectMemberCallReferencesFromExpression(call.Arguments[i], onMemberCall);
				return;
			}
			if (expression is BinaryExpr binary)
			{
				CollectMemberCallReferencesFromExpression(binary.Left, onMemberCall);
				CollectMemberCallReferencesFromExpression(binary.Right, onMemberCall);
				return;
			}
			if (expression is UnaryExpr unary)
			{
				CollectMemberCallReferencesFromExpression(unary.Operand, onMemberCall);
				return;
			}
			if (expression is FieldAccessExpr fieldAccess)
			{
				CollectMemberCallReferencesFromExpression(fieldAccess.Target, onMemberCall);
				return;
			}
		}

		private static void EmitAliasedIdentifierReferenceFacts(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedNameSymbols,
			Dictionary<string, SymbolIdentity> overrideNameSymbols,
			ref int referenceOrdinal)
		{
			if (module == null || module.Functions == null || aliasedNameSymbols == null || aliasedNameSymbols.Count == 0)
				return;
			int ordinal = referenceOrdinal;
			for (int fi = 0; fi < module.Functions.Count; fi++)
			{
				FuncDecl function = module.Functions[fi];
				if (function == null || function.Body == null) continue;
				CollectAliasedFieldAccessFromStatement(function.Body, (aliasName, fieldName, fieldLine, fieldColumn) =>
				{
					// DX21 P3: check override symbols first
					string overrideKey = aliasName + "." + fieldName;
					SymbolIdentity symbol = null;
					if (overrideNameSymbols != null)
						overrideNameSymbols.TryGetValue(overrideKey, out symbol);
					if (symbol == null)
					{
						if (!aliasedNameSymbols.TryGetValue(aliasName, out Dictionary<string, SymbolIdentity> namesByName)
							|| namesByName == null)
							return;
						if (!namesByName.TryGetValue(fieldName, out symbol) || symbol == null)
							return;
					}
					if (!TryCreateSpanFromLineColumn(source, fieldLine, fieldColumn, fieldName.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						return;
					output.Add(new DataFact(
						new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++, fieldName, sl, sc)),
						aggregateId, DataFactKind.SymbolReference, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(symbol, sl, sc, el, ec)));
				});
			}
			referenceOrdinal = ordinal;
		}

		private static void CollectAliasedFieldAccessFromStatement(Stmt statement, Action<string, string, int, int> onAliasedAccess)
		{
			if (statement == null || onAliasedAccess == null) return;
			if (statement is BlockStmt block)
			{
				if (block.Statements == null) return;
				for (int i = 0; i < block.Statements.Count; i++)
					CollectAliasedFieldAccessFromStatement(block.Statements[i], onAliasedAccess);
				return;
			}
			if (statement is VarDeclStmt variable)
			{
				// DX21: type annotation with dot (e.g. var v: U.Vec)
				if (!string.IsNullOrWhiteSpace(variable.TypeName) && variable.TypeName.Contains("."))
				{
					int dotIndex = variable.TypeName.IndexOf('.');
					string alias = variable.TypeName.Substring(0, dotIndex);
					string typeName = variable.TypeName.Substring(dotIndex + 1);
					if (variable.TypeNameLine > 0 && variable.TypeNameColumn > 0)
						onAliasedAccess(alias, typeName, variable.TypeNameLine, variable.TypeNameColumn + dotIndex + 1);
				}
				CollectAliasedFieldAccessFromExpression(variable.Initializer, onAliasedAccess);
				return;
			}
			if (statement is IfStmt conditional)
			{
				CollectAliasedFieldAccessFromExpression(conditional.Condition, onAliasedAccess);
				CollectAliasedFieldAccessFromStatement(conditional.ThenBranch, onAliasedAccess);
				CollectAliasedFieldAccessFromStatement(conditional.ElseBranch, onAliasedAccess);
				return;
			}
			if (statement is WhileStmt loop)
			{
				CollectAliasedFieldAccessFromExpression(loop.Condition, onAliasedAccess);
				CollectAliasedFieldAccessFromStatement(loop.Body, onAliasedAccess);
				return;
			}
			if (statement is ForStmt forLoop)
			{
				CollectAliasedFieldAccessFromStatement(forLoop.Initializer, onAliasedAccess);
				CollectAliasedFieldAccessFromExpression(forLoop.Condition, onAliasedAccess);
				CollectAliasedFieldAccessFromExpression(forLoop.Increment, onAliasedAccess);
				CollectAliasedFieldAccessFromStatement(forLoop.Body, onAliasedAccess);
				return;
			}
			if (statement is ReturnStmt returned)
			{
				CollectAliasedFieldAccessFromExpression(returned.Value, onAliasedAccess);
				return;
			}
			if (statement is WaitStmt waited)
			{
				CollectAliasedFieldAccessFromExpression(waited.FrameCount, onAliasedAccess);
				return;
			}
			if (statement is ExprStmt expression)
			{
				CollectAliasedFieldAccessFromExpression(expression.Expression, onAliasedAccess);
				return;
			}
		}

		private static void CollectAliasedFieldAccessFromExpression(Expr expression, Action<string, string, int, int> onAliasedAccess)
		{
			if (expression == null || onAliasedAccess == null) return;
			if (expression is FieldAccessExpr fieldAccess)
			{
				if (fieldAccess.Target is IdentifierExpr targetIdent
					&& !string.IsNullOrWhiteSpace(targetIdent.Name)
					&& !string.IsNullOrWhiteSpace(fieldAccess.FieldName)
					&& fieldAccess.FieldNameLine > 0 && fieldAccess.FieldNameColumn > 0)
				{
					onAliasedAccess(targetIdent.Name, fieldAccess.FieldName, fieldAccess.FieldNameLine, fieldAccess.FieldNameColumn);
				}
				CollectAliasedFieldAccessFromExpression(fieldAccess.Target, onAliasedAccess);
				return;
			}
			if (expression is StructLiteralExpr structLit)
			{
				// DX21: dotted struct literal (e.g. U.Vec { x: 1 })
				if (!string.IsNullOrWhiteSpace(structLit.TypeName) && structLit.TypeName.Contains("."))
				{
					int dotIndex = structLit.TypeName.IndexOf('.');
					string alias = structLit.TypeName.Substring(0, dotIndex);
					string typeName = structLit.TypeName.Substring(dotIndex + 1);
					if (structLit.Line > 0 && structLit.Column > 0)
						onAliasedAccess(alias, typeName, structLit.Line, structLit.Column + dotIndex + 1);
				}
				if (structLit.Fields != null)
					for (int i = 0; i < structLit.Fields.Count; i++)
						CollectAliasedFieldAccessFromExpression(structLit.Fields[i].Value, onAliasedAccess);
				return;
			}
			if (expression is CallExpr call)
			{
				if (call.Arguments != null)
					for (int i = 0; i < call.Arguments.Count; i++)
						CollectAliasedFieldAccessFromExpression(call.Arguments[i], onAliasedAccess);
				return;
			}
			if (expression is MemberCallExpr memberCall)
			{
				if (memberCall.Arguments != null)
					for (int i = 0; i < memberCall.Arguments.Count; i++)
						CollectAliasedFieldAccessFromExpression(memberCall.Arguments[i], onAliasedAccess);
				return;
			}
			if (expression is BinaryExpr binary)
			{
				CollectAliasedFieldAccessFromExpression(binary.Left, onAliasedAccess);
				CollectAliasedFieldAccessFromExpression(binary.Right, onAliasedAccess);
				return;
			}
			if (expression is UnaryExpr unary)
			{
				CollectAliasedFieldAccessFromExpression(unary.Operand, onAliasedAccess);
				return;
			}
			if (expression is AssignExpr assign)
			{
				CollectAliasedFieldAccessFromExpression(assign.Target, onAliasedAccess);
				CollectAliasedFieldAccessFromExpression(assign.Value, onAliasedAccess);
				return;
			}
		}

		private static void EmitOverrideDefinitions(
			ModuleNode module, string source, string normalizedDocument,
			PathKey documentPath, DataAggregateId aggregateId, long snapshotVersion,
			List<DataFact> output,
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedFunctionSymbols,
			Dictionary<string, Dictionary<string, SymbolIdentity>> aliasedNameSymbols,
			Dictionary<string, SymbolIdentity> overrideFunctionSymbols,
			Dictionary<string, SymbolIdentity> overrideNameSymbols,
			ref int definitionOrdinal)
		{
			if (module == null) return;
			int ordinal = definitionOrdinal;

			if (module.Functions != null && aliasedFunctionSymbols != null)
			{
				for (int i = 0; i < module.Functions.Count; i++)
				{
					FuncDecl f = module.Functions[i];
					if (f == null || string.IsNullOrEmpty(f.AliasTarget) || string.IsNullOrWhiteSpace(f.Name))
						continue;
					if (!aliasedFunctionSymbols.TryGetValue(f.AliasTarget, out var funcsByName) || funcsByName == null)
						continue;
					if (!funcsByName.TryGetValue(f.Name, out SymbolIdentity originalSymbol) || originalSymbol == null)
						continue;
					int nameLine = f.Line;
					int nameColumn = ResolveFunctionNameColumn(f, source);
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, f.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var overrideSymbol = new SymbolIdentity(SymbolKindTag.Function, f.Name,
						f.AliasTarget, string.Empty, normalizedDocument, span);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, ordinal++, f.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(overrideSymbol, sl, sc, el, ec)));
					string overrideKey = f.AliasTarget + "." + f.Name;
					if (!overrideFunctionSymbols.ContainsKey(overrideKey))
						overrideFunctionSymbols[overrideKey] = overrideSymbol;
					EmitOverrideCrossReference(originalSymbol, overrideSymbol, normalizedDocument,
						aggregateId, snapshotVersion, output, ref ordinal);
				}
			}

			if (module.ModuleVariables != null && aliasedNameSymbols != null)
			{
				for (int i = 0; i < module.ModuleVariables.Count; i++)
				{
					VarDeclStmt v = module.ModuleVariables[i];
					if (v == null || string.IsNullOrEmpty(v.AliasTarget) || string.IsNullOrWhiteSpace(v.Name))
						continue;
					if (!aliasedNameSymbols.TryGetValue(v.AliasTarget, out var namesByName) || namesByName == null)
						continue;
					if (!namesByName.TryGetValue(v.Name, out SymbolIdentity originalSymbol) || originalSymbol == null)
						continue;
					int nameLine = v.NameLine > 0 ? v.NameLine : v.Line;
					int nameColumn = v.NameColumn > 0 ? v.NameColumn : v.Column;
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, v.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var overrideSymbol = new SymbolIdentity(SymbolKindTag.Variable, v.Name,
						v.AliasTarget, string.Empty, normalizedDocument, span);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, ordinal++, v.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(overrideSymbol, sl, sc, el, ec)));
					string overrideKey = v.AliasTarget + "." + v.Name;
					if (!overrideNameSymbols.ContainsKey(overrideKey))
						overrideNameSymbols[overrideKey] = overrideSymbol;
					EmitOverrideCrossReference(originalSymbol, overrideSymbol, normalizedDocument,
						aggregateId, snapshotVersion, output, ref ordinal);
				}
			}

			if (module.Structs != null && aliasedNameSymbols != null)
			{
				for (int i = 0; i < module.Structs.Count; i++)
				{
					StructDecl s = module.Structs[i];
					if (s == null || string.IsNullOrEmpty(s.AliasTarget) || string.IsNullOrWhiteSpace(s.Name))
						continue;
					if (!aliasedNameSymbols.TryGetValue(s.AliasTarget, out var namesByName) || namesByName == null)
						continue;
					if (!namesByName.TryGetValue(s.Name, out SymbolIdentity originalSymbol) || originalSymbol == null)
						continue;
					int nameLine = s.NameLine > 0 ? s.NameLine : s.Line;
					int nameColumn = s.NameColumn > 0 ? s.NameColumn : s.Column;
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, s.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var overrideSymbol = new SymbolIdentity(SymbolKindTag.Struct, s.Name,
						s.AliasTarget, string.Empty, normalizedDocument, span);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, ordinal++, s.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(overrideSymbol, sl, sc, el, ec)));
					string overrideKey = s.AliasTarget + "." + s.Name;
					if (!overrideNameSymbols.ContainsKey(overrideKey))
						overrideNameSymbols[overrideKey] = overrideSymbol;
					EmitOverrideCrossReference(originalSymbol, overrideSymbol, normalizedDocument,
						aggregateId, snapshotVersion, output, ref ordinal);
				}
			}

			if (module.Enums != null && aliasedNameSymbols != null)
			{
				for (int i = 0; i < module.Enums.Count; i++)
				{
					EnumDecl e = module.Enums[i];
					if (e == null || string.IsNullOrEmpty(e.AliasTarget) || string.IsNullOrWhiteSpace(e.Name))
						continue;
					if (!aliasedNameSymbols.TryGetValue(e.AliasTarget, out var namesByName) || namesByName == null)
						continue;
					if (!namesByName.TryGetValue(e.Name, out SymbolIdentity originalSymbol) || originalSymbol == null)
						continue;
					int nameLine = e.NameLine > 0 ? e.NameLine : e.Line;
					int nameColumn = e.NameColumn > 0 ? e.NameColumn : e.Column;
					if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, e.Name.Length,
						out TextSpan span, out int sl, out int sc, out int el, out int ec))
						continue;
					var overrideSymbol = new SymbolIdentity(SymbolKindTag.Enum, e.Name,
						e.AliasTarget, string.Empty, normalizedDocument, span);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("def", normalizedDocument, ordinal++, e.Name, sl, sc)),
						aggregateId, DataFactKind.SymbolDefinition, documentPath, span, snapshotVersion,
						new SymbolDataFactPayload(overrideSymbol, sl, sc, el, ec)));
					string overrideKey = e.AliasTarget + "." + e.Name;
					if (!overrideNameSymbols.ContainsKey(overrideKey))
						overrideNameSymbols[overrideKey] = overrideSymbol;
					EmitOverrideCrossReference(originalSymbol, overrideSymbol, normalizedDocument,
						aggregateId, snapshotVersion, output, ref ordinal);
				}
			}

			definitionOrdinal = ordinal;
		}

		private static void EmitOverrideCrossReference(
			SymbolIdentity originalSymbol, SymbolIdentity overrideSymbol,
			string normalizedDocument, DataAggregateId aggregateId,
			long snapshotVersion, List<DataFact> output, ref int ordinal)
		{
			string origDoc = originalSymbol.Origin ?? string.Empty;
			if (string.IsNullOrWhiteSpace(origDoc)) return;
			var origDocPath = new PathKey(origDoc);
			output.Add(new DataFact(
				new DataFactId(BuildFactId("ref", normalizedDocument, ordinal++,
					overrideSymbol.Name + "_override_xref", 0, 0)),
				aggregateId, DataFactKind.SymbolReference, origDocPath,
				originalSymbol.DeclarationSpan, snapshotVersion,
				new SymbolDataFactPayload(overrideSymbol)));
		}

		private static void EmitIncludeEdgeFacts(
			ModuleNode module,
			string source,
			string normalizedDocument,
			PathKey documentPath,
			DataAggregateId aggregateId,
			long snapshotVersion,
			List<DataFact> output)
		{
			if (module == null || module.Imports == null || module.Imports.Count == 0)
				return;

			int includeOrdinal = 0;
			for (int i = 0; i < module.Imports.Count; i++)
			{
				ImportDecl import = module.Imports[i];
				if (import == null || string.IsNullOrWhiteSpace(import.ModulePath))
					continue;

				int nameLine = import.Line;
				int nameColumn = import.Column > 0 ? import.Column : 1;
				int literalLength = import.ModulePath.Length > 0 ? import.ModulePath.Length : 1;

				if (!TryCreateSpanFromLineColumn(source, nameLine, nameColumn, literalLength, out TextSpan span, out int startLine, out int startCharacter, out int endLine, out int endCharacter))
				{
					span = new TextSpan(0, literalLength);
					startLine = nameLine > 0 ? nameLine - 1 : 0;
					startCharacter = nameColumn > 0 ? nameColumn - 1 : 0;
					endLine = startLine;
					endCharacter = startCharacter + literalLength;
				}

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				string targetUri = SelectBestIncludeCandidate(candidates);

				var payload = new IncludeEdgeDataFactPayload(targetUri);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("inc", normalizedDocument, includeOrdinal++, import.ModulePath, startLine, startCharacter)),
					aggregateId,
					DataFactKind.IncludeEdge,
					documentPath,
					span,
					snapshotVersion,
					payload));

				if (!string.IsNullOrEmpty(import.Alias) && !string.IsNullOrWhiteSpace(targetUri))
				{
					var aliasPayload = new AliasBindingDataFactPayload(import.Alias, targetUri);
					output.Add(new DataFact(
						new DataFactId(BuildFactId("alias", normalizedDocument, i, import.Alias, startLine, startCharacter)),
						aggregateId,
						DataFactKind.AliasBinding,
						documentPath,
						span,
						snapshotVersion,
						aliasPayload));
				}

				// Emit include path symbol for position-indexed definition + references
				if (!string.IsNullOrWhiteSpace(targetUri))
				{
					string normalizedTarget = NormalizeDocumentKey(targetUri);
					if (!string.IsNullOrWhiteSpace(normalizedTarget))
					{
						var includeSymbol = new SymbolIdentity(
							SymbolKindTag.IncludeFile,
							import.ModulePath,
							string.Empty,
							string.Empty,
							normalizedTarget,
							new TextSpan(0, 0));

						// Path literal column: PathColumn points to opening quote, +1 = first char inside quotes
						int pathColumn = import.PathColumn > 0 ? import.PathColumn + 1 : nameColumn;
						int pathLine = import.PathLine > 0 ? import.PathLine : nameLine;
						if (TryCreateSpanFromLineColumn(source, pathLine, pathColumn, import.ModulePath.Length,
							out TextSpan pathSpan, out int psl, out int psc, out int pel, out int pec))
						{
							output.Add(new DataFact(
								new DataFactId(BuildFactId("ref", normalizedDocument, includeOrdinal + 1000, import.ModulePath, psl, psc)),
								aggregateId,
								DataFactKind.SymbolReference,
								documentPath,
								pathSpan,
								snapshotVersion,
								new SymbolDataFactPayload(includeSymbol, psl, psc, pel, pec)));

							var targetPath = new PathKey(normalizedTarget);
							output.Add(new DataFact(
								new DataFactId(BuildFactId("def", normalizedDocument, includeOrdinal + 2000, import.ModulePath, 0, 0)),
								aggregateId,
								DataFactKind.SymbolDefinition,
								targetPath,
								new TextSpan(0, 0),
								snapshotVersion,
								new SymbolDataFactPayload(includeSymbol, 0, 0, 0, 0)));
						}
					}
				}
			}
		}

		private static void CollectCallReferencesFromStatement(Stmt statement, Action<CallExpr> onCall)
		{
			if (statement == null || onCall == null)
				return;

			if (statement is BlockStmt block)
			{
				if (block.Statements == null)
					return;

				for (int i = 0; i < block.Statements.Count; i++)
					CollectCallReferencesFromStatement(block.Statements[i], onCall);
				return;
			}

			if (statement is VarDeclStmt variable)
			{
				CollectCallReferencesFromExpression(variable.Initializer, onCall);
				return;
			}

			if (statement is IfStmt conditional)
			{
				CollectCallReferencesFromExpression(conditional.Condition, onCall);
				CollectCallReferencesFromStatement(conditional.ThenBranch, onCall);
				CollectCallReferencesFromStatement(conditional.ElseBranch, onCall);
				return;
			}

			if (statement is WhileStmt loop)
			{
				CollectCallReferencesFromExpression(loop.Condition, onCall);
				CollectCallReferencesFromStatement(loop.Body, onCall);
				return;
			}

			if (statement is ForStmt forLoop)
			{
				CollectCallReferencesFromStatement(forLoop.Initializer, onCall);
				CollectCallReferencesFromExpression(forLoop.Condition, onCall);
				CollectCallReferencesFromExpression(forLoop.Increment, onCall);
				CollectCallReferencesFromStatement(forLoop.Body, onCall);
				return;
			}

			if (statement is ReturnStmt returned)
			{
				CollectCallReferencesFromExpression(returned.Value, onCall);
				return;
			}

			if (statement is WaitStmt waited)
			{
				CollectCallReferencesFromExpression(waited.FrameCount, onCall);
				return;
			}

			if (statement is WaitForStmt waitedFor)
			{
				CollectCallReferencesFromExpression(waitedFor.TargetInstanceId, onCall);
				return;
			}

			if (statement is ExprStmt expression)
			{
				CollectCallReferencesFromExpression(expression.Expression, onCall);
				return;
			}

			if (statement is DeferStmt defer)
			{
				CollectCallReferencesFromStatement(defer.Body, onCall);
				return;
			}

			if (statement is UsingStmt usingStmt)
			{
				if (usingStmt.Arguments != null)
				{
					for (int i = 0; i < usingStmt.Arguments.Count; i++)
						CollectCallReferencesFromExpression(usingStmt.Arguments[i], onCall);
				}

				CollectCallReferencesFromStatement(usingStmt.Body, onCall);
			}
		}

		private static void CollectCallReferencesFromExpression(Expr expression, Action<CallExpr> onCall)
		{
			if (expression == null || onCall == null)
				return;

			if (expression is CallExpr call)
			{
				onCall(call);
				if (call.Arguments != null)
				{
					for (int i = 0; i < call.Arguments.Count; i++)
						CollectCallReferencesFromExpression(call.Arguments[i], onCall);
				}

				return;
			}

			if (expression is BinaryExpr binary)
			{
				CollectCallReferencesFromExpression(binary.Left, onCall);
				CollectCallReferencesFromExpression(binary.Right, onCall);
				return;
			}

			if (expression is UnaryExpr unary)
			{
				CollectCallReferencesFromExpression(unary.Operand, onCall);
				return;
			}

			if (expression is AssignExpr assign)
			{
				CollectCallReferencesFromExpression(assign.Target, onCall);
				CollectCallReferencesFromExpression(assign.Value, onCall);
				return;
			}

			if (expression is FieldAccessExpr field)
			{
				CollectCallReferencesFromExpression(field.Target, onCall);
				return;
			}

			if (expression is MemberCallExpr memberCall)
			{
				if (memberCall.Arguments != null)
				{
					for (int i = 0; i < memberCall.Arguments.Count; i++)
						CollectCallReferencesFromExpression(memberCall.Arguments[i], onCall);
				}

				return;
			}

			if (expression is StructLiteralExpr structLiteral)
			{
				if (structLiteral.Fields == null)
					return;

				for (int i = 0; i < structLiteral.Fields.Count; i++)
					CollectCallReferencesFromExpression(structLiteral.Fields[i].Value, onCall);
			}
		}

		private static int ResolveFunctionNameColumn(FuncDecl function, string source)
		{
			if (function == null)
				return 1;

			if (string.IsNullOrWhiteSpace(function.Name))
				return function.Column > 0 ? function.Column : 1;

			if (!TryGetLineText(source, function.Line - 1, out string lineText))
				return function.Column > 0 ? function.Column + 5 : 1;

			int searchStart = function.Column > 0 ? function.Column - 1 : 0;
			if (searchStart < 0)
				searchStart = 0;

			int found = lineText.IndexOf(function.Name, searchStart, StringComparison.Ordinal);
			if (found < 0)
				found = lineText.IndexOf(function.Name, StringComparison.Ordinal);

			if (found >= 0)
				return found + 1;

			return function.Column > 0 ? function.Column + 5 : 1;
		}

		private static bool TryCreateSpanFromLineColumn(
			string source,
			int oneBasedLine,
			int oneBasedColumn,
			int length,
			out TextSpan span,
			out int startLine,
			out int startCharacter,
			out int endLine,
			out int endCharacter)
		{
			startLine = oneBasedLine > 0 ? oneBasedLine - 1 : 0;
			startCharacter = oneBasedColumn > 0 ? oneBasedColumn - 1 : 0;
			if (length <= 0)
				length = 1;

			int startOffset = ComputeOffsetFromLineCharacter(source ?? string.Empty, startLine, startCharacter);
			if (startOffset < 0)
			{
				span = new TextSpan(0, 0);
				endLine = startLine;
				endCharacter = startCharacter;
				return false;
			}

			span = new TextSpan(startOffset, length);
			endLine = startLine;
			endCharacter = startCharacter + length;
			return true;
		}

		private static bool TryGetLineText(string source, int zeroBasedLine, out string lineText)
		{
			lineText = string.Empty;
			if (zeroBasedLine < 0)
				zeroBasedLine = 0;

			string text = source ?? string.Empty;
			int currentLine = 0;
			int start = 0;
			int index = 0;

			while (index < text.Length && currentLine < zeroBasedLine)
			{
				char c = text[index++];
				if (c == '\r')
				{
					if (index < text.Length && text[index] == '\n')
						index++;

					currentLine++;
					start = index;
				}
				else if (c == '\n')
				{
					currentLine++;
					start = index;
				}
			}

			if (currentLine != zeroBasedLine)
				return false;

			int end = start;
			while (end < text.Length && text[end] != '\r' && text[end] != '\n')
				end++;

			lineText = text.Substring(start, end - start);
			return true;
		}

		private static int ComputeOffsetFromLineCharacter(string source, int line, int character)
		{
			string text = source ?? string.Empty;
			if (line < 0)
				line = 0;

			if (character < 0)
				character = 0;

			int currentLine = 0;
			int index = 0;
			while (index < text.Length && currentLine < line)
			{
				char c = text[index++];
				if (c == '\r')
				{
					if (index < text.Length && text[index] == '\n')
						index++;

					currentLine++;
				}
				else if (c == '\n')
				{
					currentLine++;
				}
			}

			if (currentLine < line)
				return text.Length;

			int lineEnd = index;
			while (lineEnd < text.Length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
				lineEnd++;

			int offset = index + character;
			if (offset > lineEnd)
				offset = lineEnd;

			return offset;
		}

		private static string BuildFuncDocumentation(FuncDecl function)
		{
			if (function == null) return null;

			var sb = new System.Text.StringBuilder();

			// Signature line
			sb.Append("```ffvm\n");
			if (function.IsExternal) sb.Append("external ");
			sb.Append("func ").Append(function.Name).Append('(');
			if (function.Parameters != null)
			{
				for (int i = 0; i < function.Parameters.Count; i++)
				{
					if (i > 0) sb.Append(", ");
					ParamDecl p = function.Parameters[i];
					sb.Append(p.Name);
					if (!string.IsNullOrEmpty(p.TypeName)) sb.Append(": ").Append(p.TypeName);
				}
			}
			sb.Append(')');
			if (!string.IsNullOrEmpty(function.ReturnType)) sb.Append(": ").Append(function.ReturnType);
			sb.Append("\n```");

			// Doc comment
			if (!string.IsNullOrEmpty(function.DocComment))
				sb.Append("\n\n---\n\n").Append(function.DocComment);

			// Parameters
			bool hasParamDoc = false;
			if (function.Parameters != null)
			{
				for (int pi = 0; pi < function.Parameters.Count; pi++)
				{
					ParamDecl p = function.Parameters[pi];
					if (!string.IsNullOrEmpty(p.DocComment))
					{
						if (!hasParamDoc) { sb.Append("\n\n**Parameters:**\n"); hasParamDoc = true; }
						sb.Append("\n- `").Append(p.Name).Append("` — ").Append(p.DocComment);
					}
				}
			}

			// Return doc
			if (!string.IsNullOrEmpty(function.ReturnDoc))
				sb.Append("\n\n**Returns:** ").Append(function.ReturnDoc);

			return sb.ToString();
		}

		private static string BuildStructDocumentation(StructDecl s)
		{
			if (s == null) return null;

			var sb = new System.Text.StringBuilder();
			sb.Append("```ffvm\nstruct ").Append(s.Name).Append(" {\n");
			if (s.Fields != null)
			{
				for (int i = 0; i < s.Fields.Count; i++)
				{
					StructField f = s.Fields[i];
					sb.Append("  ").Append(f.Name).Append(": ").Append(f.TypeName).Append('\n');
				}
			}
			sb.Append("}\n```");
			if (!string.IsNullOrEmpty(s.DocComment))
				sb.Append("\n\n---\n\n").Append(s.DocComment);
			return sb.ToString();
		}

		private static string BuildEnumDocumentation(EnumDecl e)
		{
			if (e == null) return null;

			var sb = new System.Text.StringBuilder();
			sb.Append("```ffvm\nenum ").Append(e.Name).Append(" {\n");
			if (e.Members != null)
			{
				for (int i = 0; i < e.Members.Count; i++)
				{
					EnumMember m = e.Members[i];
					sb.Append("  ").Append(m.Name);
					if (i < e.Members.Count - 1) sb.Append(',');
					sb.Append('\n');
				}
			}
			sb.Append("}\n```");
			if (!string.IsNullOrEmpty(e.DocComment))
				sb.Append("\n\n---\n\n").Append(e.DocComment);
			return sb.ToString();
		}

		private static string BuildVariableDocumentation(VarDeclStmt variable)
		{
			if (variable == null)
				return null;

			if (string.IsNullOrWhiteSpace(variable.DocComment))
				return null;

			var sb = new System.Text.StringBuilder();
			sb.Append("```ffvm\n");
			if (variable.IsExported)
				sb.Append("@export ");
			sb.Append(variable.IsConst ? "const " : "var ");
			sb.Append(variable.Name);
			if (!string.IsNullOrWhiteSpace(variable.TypeName))
				sb.Append(": ").Append(variable.TypeName);
			sb.Append("\n```");
			sb.Append("\n\n---\n\n").Append(variable.DocComment);
			return sb.ToString();
		}

		private static string BuildParameterDocumentation(ParamDecl param)
		{
			if (param == null || string.IsNullOrWhiteSpace(param.DocComment))
				return null;

			var sb = new System.Text.StringBuilder();
			sb.Append("```ffvm\n");
			sb.Append(param.Name);
			if (!string.IsNullOrWhiteSpace(param.TypeName))
				sb.Append(": ").Append(param.TypeName);
			sb.Append("\n```");
			sb.Append("\n\n---\n\n").Append(param.DocComment);
			return sb.ToString();
		}

		private static string BuildFactId(
			string prefix,
			string documentKey,
			int ordinal,
			string symbolName,
			int line,
			int character)
		{
			return "fact:"
				+ (prefix ?? string.Empty)
				+ ":"
				+ NormalizeDocumentKey(documentKey).ToLowerInvariant()
				+ ":"
				+ ordinal
				+ ":"
				+ (symbolName ?? string.Empty)
				+ ":"
				+ line
				+ ":"
				+ character;
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

		private static DocumentSourceTier ResolveSourceTier(DatabaseChangeEvent change)
		{
			if (change == null)
				return DocumentSourceTier.Unknown;

			if (change.Payload is DocumentChangedWithTierChangePayload tieredPayload)
				return tieredPayload.SourceTier;

			if (change.Kind == DatabaseChangeKind.DocumentOpened || change.Kind == DatabaseChangeKind.DocumentChanged)
				return DocumentSourceTier.OpenBuffer;

			if (change.Kind == DatabaseChangeKind.WatchedFilesChanged)
				return DocumentSourceTier.Watcher;

			return DocumentSourceTier.Unknown;
		}

		private static bool IsHigherPriorityTier(DocumentSourceTier currentTier, DocumentSourceTier incomingTier)
		{
			if (currentTier == DocumentSourceTier.Unknown || incomingTier == DocumentSourceTier.Unknown)
				return false;

			return (int)currentTier < (int)incomingTier;
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

		// Picks the best include-target candidate from IncludeTargetResolver output.
		// Resolver returns candidates in priority order (source-dir-relative first,
		// then each workspace-root-relative), but the first one may point to a
		// non-existent file when the include path happens to share a prefix with
		// the including file's directory (e.g. `common/syscalls` included from
		// `common/base.ffs` would naively resolve to `common/common/syscalls.ffs`).
		// Prefer the first candidate whose file actually exists; fall back to the
		// first candidate to preserve existing diagnostics for unresolved paths.
		private static string SelectBestIncludeCandidate(List<string> candidates)
		{
			if (candidates == null || candidates.Count == 0)
				return string.Empty;
			for (int i = 0; i < candidates.Count; i++)
			{
				string uri = candidates[i];
				if (string.IsNullOrWhiteSpace(uri)) continue;
				string path = WorkspacePathTool.UriToPath(uri);
				if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
					return uri;
			}
			return candidates[0] ?? string.Empty;
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
