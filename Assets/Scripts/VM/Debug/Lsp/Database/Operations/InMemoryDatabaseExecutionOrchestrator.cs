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
			var functionSymbols = new Dictionary<string, SymbolIdentity>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, SymbolIdentity> importedFunctionSymbols = BuildImportedFunctionSymbols(module, normalizedDocument);
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedStructFieldSymbols = BuildImportedStructFieldSymbols(module, normalizedDocument);

			int definitionOrdinal = 0;
			for (int i = 0; i < module.Functions.Count; i++)
			{
				FuncDecl function = module.Functions[i];
				if (function == null || string.IsNullOrWhiteSpace(function.Name))
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
					span);

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
			var symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.OrdinalIgnoreCase);
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
					fieldsByName = new Dictionary<string, StructFieldDescriptor>(StringComparer.OrdinalIgnoreCase);
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
							span);

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
			var symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.OrdinalIgnoreCase);
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

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0)
					continue;

				for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[candidateIndex]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;

					if (!TryReadImportedStructFieldSymbols(candidateUri, out Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedSymbols))
						continue;

					MergeStructFieldSymbolsInto(symbolsByStruct, importedSymbols);
					break;
				}
			}

			return symbolsByStruct;
		}

		private static bool TryReadImportedStructFieldSymbols(
			string importedDocumentUri,
			out Dictionary<string, Dictionary<string, StructFieldDescriptor>> symbolsByStruct)
		{
			symbolsByStruct = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.OrdinalIgnoreCase);
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
			if (module == null || module.Structs == null || module.Structs.Count == 0)
				return true;

			for (int i = 0; i < module.Structs.Count; i++)
			{
				StructDecl structDecl = module.Structs[i];
				if (structDecl == null || string.IsNullOrWhiteSpace(structDecl.Name) || structDecl.Fields == null)
					continue;

				if (!symbolsByStruct.TryGetValue(structDecl.Name, out Dictionary<string, StructFieldDescriptor> fieldsByName))
				{
					fieldsByName = new Dictionary<string, StructFieldDescriptor>(StringComparer.OrdinalIgnoreCase);
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
							span),
						field.TypeName);
				}
			}

			return true;
		}

		private static Dictionary<string, Dictionary<string, StructFieldDescriptor>> MergeStructFieldSymbols(
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> importedSymbols,
			Dictionary<string, Dictionary<string, StructFieldDescriptor>> localSymbols)
		{
			var merged = new Dictionary<string, Dictionary<string, StructFieldDescriptor>>(StringComparer.OrdinalIgnoreCase);
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
					destinationFields = new Dictionary<string, StructFieldDescriptor>(StringComparer.OrdinalIgnoreCase);
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

					var variableTypes = new Dictionary<string, string>(moduleVariableTypes, StringComparer.OrdinalIgnoreCase);
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

			var moduleInitializerTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
				var scopedVariables = new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase);
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
					new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase),
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
					new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase),
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
					new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase),
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
				var scopedVariables = new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase);
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
					new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase),
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
					new Dictionary<string, string>(variableTypes, StringComparer.OrdinalIgnoreCase),
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
				if (structLiteral.Fields != null)
				{
					for (int i = 0; i < structLiteral.Fields.Count; i++)
					{
						ResolveExpressionTypeAndEmitFieldReferences(
							structLiteral.Fields[i].Value,
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

				return GetBaseTypeName(structLiteral.TypeName);
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
			var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
			var symbols = new Dictionary<string, SymbolIdentity>(StringComparer.OrdinalIgnoreCase);
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

				List<string> candidates = IncludeTargetResolver.ResolveCandidates(normalizedDocument, import.ModulePath);
				if (candidates == null || candidates.Count == 0)
					continue;

				for (int candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
				{
					string candidateUri = NormalizeDocumentKey(candidates[candidateIndex]);
					if (string.IsNullOrWhiteSpace(candidateUri) || !visitedTargets.Add(candidateUri))
						continue;

					if (!TryReadImportedFunctionSymbols(candidateUri, out Dictionary<string, SymbolIdentity> importedSymbols))
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
			out Dictionary<string, SymbolIdentity> symbols)
		{
			symbols = new Dictionary<string, SymbolIdentity>(StringComparer.OrdinalIgnoreCase);
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
			if (module == null || module.Functions == null || module.Functions.Count == 0)
				return true;

			for (int i = 0; i < module.Functions.Count; i++)
			{
				FuncDecl function = module.Functions[i];
				if (function == null || string.IsNullOrWhiteSpace(function.Name))
					continue;

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
						span);
				}
			}

			return true;
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
				string targetUri = candidates != null && candidates.Count > 0 ? candidates[0] : string.Empty;

				var payload = new IncludeEdgeDataFactPayload(targetUri);
				output.Add(new DataFact(
					new DataFactId(BuildFactId("inc", normalizedDocument, includeOrdinal++, import.ModulePath, startLine, startCharacter)),
					aggregateId,
					DataFactKind.IncludeEdge,
					documentPath,
					span,
					snapshotVersion,
					payload));
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
