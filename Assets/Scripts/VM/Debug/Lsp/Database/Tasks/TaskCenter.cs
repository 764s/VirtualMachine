// Responsibility:
//   Internal task execution center for database command processing.
// Owns:
//   Task execution boundary, execution status reporting, and plan result envelope.
// Inputs/Outputs:
//   In: DatabaseTaskPlan + triggering operation request.
//   Out: DatabaseTaskExecutionReport consumed by database orchestrator.
// Allowed Dependencies:
//   - DatabaseTaskPlan
//   - DatabaseOperationRequest
// Forbidden Dependencies:
//   - External protocol adapters as execution callers.
//   - Direct write access bypassing IWorkspaceCodeDatabase.Execute.
// Invariants:
//   - Executes only planned tasks for the given command.
//   - Report command identity must match triggering request.
// Boundary Closure:
//   Upstream: IDatabaseExecutionOrchestrator.
//   Downstream: snapshot compose/commit orchestration.

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseTaskExecutionStatus
	{
		Unknown = 0,
		Succeeded,
		Failed,
		Canceled,
		TimedOut,
		Skipped
	}

	public enum DatabaseTaskEnqueueDisposition
	{
		Unknown = 0,
		RejectedInvalid,
		Enqueued,
		Coalesced,
		ReplacedSuperseded,
		SkippedSuperseded
	}

	public sealed class DatabaseTaskExecutionResult
	{
		public string TaskId { get; }
		public DatabaseTaskKind Kind { get; }
		public DatabaseTaskExecutionStatus Status { get; }
		public string Message { get; }
		public object Output { get; }

		public DatabaseTaskExecutionResult(
			string taskId,
			DatabaseTaskKind kind,
			DatabaseTaskExecutionStatus status,
			string message,
			object output)
		{
			TaskId = taskId ?? string.Empty;
			Kind = kind;
			Status = status;
			Message = message ?? string.Empty;
			Output = output;
		}
	}

	public sealed class DatabaseTaskExecutionReport
	{
		private static readonly IReadOnlyList<DatabaseTaskExecutionResult> EmptyResults = new List<DatabaseTaskExecutionResult>(0);

		public bool Succeeded { get; }
		public string CommandId { get; }
		public string CorrelationId { get; }
		public IReadOnlyList<DatabaseTaskExecutionResult> Results { get; }
		public object Output { get; }
		public string Message { get; }

		public DatabaseTaskExecutionReport(
			bool succeeded,
			string commandId,
			string correlationId,
			IReadOnlyList<DatabaseTaskExecutionResult> results,
			object output,
			string message)
		{
			Succeeded = succeeded;
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			Results = results ?? EmptyResults;
			Output = output;
			Message = message ?? string.Empty;
		}
	}

	public sealed class DatabaseTaskEnqueueResult
	{
		public bool Accepted { get; }
		public string CommandId { get; }
		public string StreamKey { get; }
		public string QueueTicket { get; }
		public int SupersededCanceledCount { get; }
		public DatabaseTaskEnqueueDisposition Disposition { get; }
		public string Message { get; }

		public DatabaseTaskEnqueueResult(
			bool accepted,
			string commandId,
			string streamKey,
			string queueTicket,
			int supersededCanceledCount,
			DatabaseTaskEnqueueDisposition disposition,
			string message)
		{
			Accepted = accepted;
			CommandId = commandId ?? string.Empty;
			StreamKey = streamKey ?? string.Empty;
			QueueTicket = queueTicket ?? string.Empty;
			SupersededCanceledCount = supersededCanceledCount;
			Disposition = disposition;
			Message = message ?? string.Empty;
		}
	}

	public interface IDatabaseTaskCenter
	{
		bool TryGetLatestPending(string streamKey, out DatabaseOperationRequest pendingRequest);

		DatabaseTaskEnqueueResult Enqueue(DatabaseTaskPlan plan, DatabaseOperationRequest request);

		DatabaseTaskExecutionReport Execute(DatabaseTaskPlan plan, DatabaseOperationRequest request);

		int CancelSuperseded(string streamKey, string keepCommandId, string reason);

		bool TryDrainOne(out DatabaseTaskExecutionReport report);
	}
}
