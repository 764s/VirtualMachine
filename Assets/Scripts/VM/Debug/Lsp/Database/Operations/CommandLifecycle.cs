// Responsibility:
//   Command lifecycle state-machine contracts for database operation execution.
// Owns:
//   State taxonomy, transition records, and lifecycle trace envelope.
// Inputs/Outputs:
//   In: execution-stage transitions from orchestrator.
//   Out: lifecycle trace consumable by observability/debug tooling.
// Allowed Dependencies:
//   - DatabaseExecutionStage
// Forbidden Dependencies:
//   - Protocol transport and feature-specific execution logic.
// Invariants:
//   - Lifecycle states are explicit and monotonic by policy.
//   - Transition records are append-only for one command execution.
// Boundary Closure:
//   Upstream: execution orchestrator.
//   Downstream: operation result projection and decision logs.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseCommandState
	{
		Unknown = 0,
		Created,
		Admitted,
		Planned,
		Enqueued,
		Executing,
		Composed,
		Committed,
		Completed,
		Rejected,
		Canceled,
		TimedOut,
		Failed
	}

	public enum DatabaseCommandTransitionReason
	{
		Unknown = 0,
		ValidationPassed,
		ValidationFailed,
		VersionGatePassed,
		VersionGateFailed,
		AdmissionPassed,
		AdmissionRejected,
		Planned,
		Enqueued,
		ExecutionStarted,
		ExecutionSucceeded,
		ExecutionFailed,
		ComposeSucceeded,
		ComposeFailed,
		CommitSucceeded,
		CommitFailed,
		Completed,
		Rejected,
		Canceled,
		TimedOut,
		Failed
	}

	public sealed class DatabaseCommandStateTransition
	{
		public string CommandId { get; }
		public string CorrelationId { get; }
		public DatabaseExecutionStage Stage { get; }
		public DatabaseCommandState FromState { get; }
		public DatabaseCommandState ToState { get; }
		public DatabaseCommandTransitionReason Reason { get; }
		public string Message { get; }
		public DateTime TimestampUtc { get; }

		public DatabaseCommandStateTransition(
			string commandId,
			string correlationId,
			DatabaseExecutionStage stage,
			DatabaseCommandState fromState,
			DatabaseCommandState toState,
			DatabaseCommandTransitionReason reason,
			string message,
			DateTime timestampUtc)
		{
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			Stage = stage;
			FromState = fromState;
			ToState = toState;
			Reason = reason;
			Message = message ?? string.Empty;
			TimestampUtc = timestampUtc;
		}
	}

	public sealed class DatabaseCommandLifecycleTrace
	{
		private static readonly IReadOnlyList<DatabaseCommandStateTransition> EmptyTransitions
			= new List<DatabaseCommandStateTransition>(0);

		public string CommandId { get; }
		public string CorrelationId { get; }
		public DatabaseCommandState CurrentState { get; }
		public IReadOnlyList<DatabaseCommandStateTransition> Transitions { get; }

		public DatabaseCommandLifecycleTrace(
			string commandId,
			string correlationId,
			DatabaseCommandState currentState,
			IReadOnlyList<DatabaseCommandStateTransition> transitions)
		{
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			CurrentState = currentState;
			Transitions = transitions ?? EmptyTransitions;
		}
	}

	public interface IDatabaseCommandLifecyclePolicy
	{
		bool CanTransition(DatabaseCommandState fromState, DatabaseCommandState toState, out string error);
	}

	public interface IDatabaseCommandLifecycleSink
	{
		void Record(DatabaseCommandStateTransition transition);
	}
}
