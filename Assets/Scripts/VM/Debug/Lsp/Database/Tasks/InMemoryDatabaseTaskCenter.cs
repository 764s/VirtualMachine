// Responsibility:
//   In-memory task center for queue admission, supersession, and report draining.
// Owns:
//   Pending command queue and completed execution reports.
// Inputs/Outputs:
//   In: plans/requests from orchestrator.
//   Out: enqueue results, execution reports, and drainable completion stream.
// Allowed Dependencies:
//   - IDatabaseTaskCenter
// Forbidden Dependencies:
//   - Protocol wiring, semantic indexing/query logic.
// Invariants:
//   - Pending queue is stream-addressable by latest command.
// Boundary Closure:
//   Upstream: orchestrator enqueue/execute stages.
//   Downstream: execution reports consumed by compose/commit stages.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryDatabaseTaskCenter : IDatabaseTaskCenter
	{
		private sealed class PendingTaskEntry
		{
			public PendingTaskEntry(string streamKey, DatabaseTaskPlan plan, DatabaseOperationRequest request, string queueTicket)
			{
				StreamKey = streamKey;
				Plan = plan;
				Request = request;
				QueueTicket = queueTicket;
				EnqueuedAtUtc = DateTime.UtcNow;
			}

			public string StreamKey { get; }
			public DatabaseTaskPlan Plan { get; }
			public DatabaseOperationRequest Request { get; }
			public string QueueTicket { get; }
			public DateTime EnqueuedAtUtc { get; }
		}

		private readonly object _sync = new object();
		private readonly List<PendingTaskEntry> _pending = new List<PendingTaskEntry>();
		private readonly Queue<DatabaseTaskExecutionReport> _completed = new Queue<DatabaseTaskExecutionReport>();

		public bool TryGetLatestPending(string streamKey, out DatabaseOperationRequest pendingRequest)
		{
			lock (_sync)
			{
				pendingRequest = null;
				if (string.IsNullOrWhiteSpace(streamKey))
					return false;

				for (int i = _pending.Count - 1; i >= 0; i--)
				{
					PendingTaskEntry entry = _pending[i];
					if (string.Equals(entry.StreamKey, streamKey, StringComparison.Ordinal))
					{
						pendingRequest = entry.Request;
						return true;
					}
				}

				return false;
			}
		}

		public DatabaseTaskEnqueueResult Enqueue(DatabaseTaskPlan plan, DatabaseOperationRequest request)
		{
			if (plan == null || request == null)
			{
				return new DatabaseTaskEnqueueResult(
					accepted: false,
					commandId: request?.CommandId,
					streamKey: request?.StreamKey,
					queueTicket: string.Empty,
					supersededCanceledCount: 0,
					disposition: DatabaseTaskEnqueueDisposition.RejectedInvalid,
					message: "Plan and request are required for enqueue.");
			}

			if (!request.IsShapeValid(out string shapeError))
			{
				return new DatabaseTaskEnqueueResult(
					accepted: false,
					commandId: request.CommandId,
					streamKey: request.StreamKey,
					queueTicket: string.Empty,
					supersededCanceledCount: 0,
					disposition: DatabaseTaskEnqueueDisposition.RejectedInvalid,
					message: shapeError);
			}

			lock (_sync)
			{
				int canceledCount = 0;
				DatabaseTaskEnqueueDisposition disposition = DatabaseTaskEnqueueDisposition.Enqueued;

				if (!string.IsNullOrWhiteSpace(request.StreamKey)
					&& request.StreamBehavior == DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded)
				{
					canceledCount = CancelSupersededInternal(request.StreamKey, request.CommandId, "Superseded at enqueue.");
					if (canceledCount > 0)
						disposition = DatabaseTaskEnqueueDisposition.ReplacedSuperseded;
				}
				else if (!string.IsNullOrWhiteSpace(request.StreamKey)
					&& request.StreamBehavior == DatabaseOperationStreamBehavior.Coalesce
					&& HasPendingStreamInternal(request.StreamKey))
				{
					disposition = DatabaseTaskEnqueueDisposition.Coalesced;
				}

				string queueTicket = "queue-" + Guid.NewGuid().ToString("N");
				_pending.Add(new PendingTaskEntry(request.StreamKey, plan, request, queueTicket));

				return new DatabaseTaskEnqueueResult(
					accepted: true,
					commandId: request.CommandId,
					streamKey: request.StreamKey,
					queueTicket: queueTicket,
					supersededCanceledCount: canceledCount,
					disposition: disposition,
					message: "Request accepted by in-memory task center.");
			}
		}

		public DatabaseTaskExecutionReport Execute(DatabaseTaskPlan plan, DatabaseOperationRequest request)
		{
			if (plan == null || request == null)
			{
				var failedResults = new List<DatabaseTaskExecutionResult>
				{
					new DatabaseTaskExecutionResult(
						taskId: "task-execute",
						kind: DatabaseTaskKind.FinalizeOperation,
						status: DatabaseTaskExecutionStatus.Failed,
						message: "Plan/request missing for execution.",
						output: null)
				};

				return new DatabaseTaskExecutionReport(
					succeeded: false,
					commandId: request?.CommandId,
					correlationId: request?.CorrelationId,
					results: failedResults,
					output: null,
					message: "Execution rejected due to missing inputs.");
			}

			lock (_sync)
			{
				RemovePendingByCommandIdInternal(request.CommandId);
			}

			var successResults = new List<DatabaseTaskExecutionResult>
			{
				new DatabaseTaskExecutionResult(
					taskId: "task-execute",
					kind: DatabaseTaskKind.FinalizeOperation,
					status: DatabaseTaskExecutionStatus.Succeeded,
					message: "Executed via in-memory task center.",
					output: null)
			};

			var report = new DatabaseTaskExecutionReport(
				succeeded: true,
				commandId: request.CommandId,
				correlationId: request.CorrelationId,
				results: successResults,
				output: null,
				message: "Executed via in-memory task center.");

			lock (_sync)
			{
				_completed.Enqueue(report);
			}

			return report;
		}

		public int CancelSuperseded(string streamKey, string keepCommandId, string reason)
		{
			lock (_sync)
			{
				return CancelSupersededInternal(streamKey, keepCommandId, reason);
			}
		}

		public bool TryDrainOne(out DatabaseTaskExecutionReport report)
		{
			lock (_sync)
			{
				if (_completed.Count > 0)
				{
					report = _completed.Dequeue();
					return true;
				}

				report = null;
				return false;
			}
		}

		private bool HasPendingStreamInternal(string streamKey)
		{
			for (int i = 0; i < _pending.Count; i++)
			{
				if (string.Equals(_pending[i].StreamKey, streamKey, StringComparison.Ordinal))
					return true;
			}

			return false;
		}

		private int CancelSupersededInternal(string streamKey, string keepCommandId, string reason)
		{
			if (string.IsNullOrWhiteSpace(streamKey))
				return 0;

			int canceled = 0;
			for (int i = _pending.Count - 1; i >= 0; i--)
			{
				PendingTaskEntry pending = _pending[i];
				if (!string.Equals(pending.StreamKey, streamKey, StringComparison.Ordinal))
					continue;

				if (!string.IsNullOrEmpty(keepCommandId)
					&& string.Equals(pending.Request.CommandId, keepCommandId, StringComparison.Ordinal))
				{
					continue;
				}

				_pending.RemoveAt(i);
				canceled++;

				var canceledResults = new List<DatabaseTaskExecutionResult>
				{
					new DatabaseTaskExecutionResult(
						taskId: "task-cancel",
						kind: DatabaseTaskKind.FinalizeOperation,
						status: DatabaseTaskExecutionStatus.Canceled,
						message: reason ?? "Canceled by supersession policy.",
						output: null)
				};

				_completed.Enqueue(new DatabaseTaskExecutionReport(
					succeeded: false,
					commandId: pending.Request.CommandId,
					correlationId: pending.Request.CorrelationId,
					results: canceledResults,
					output: null,
					message: reason ?? "Canceled by supersession policy."));
			}

			return canceled;
		}

		private void RemovePendingByCommandIdInternal(string commandId)
		{
			if (string.IsNullOrEmpty(commandId))
				return;

			for (int i = _pending.Count - 1; i >= 0; i--)
			{
				if (string.Equals(_pending[i].Request.CommandId, commandId, StringComparison.Ordinal))
				{
					_pending.RemoveAt(i);
					return;
				}
			}
		}
	}
}
