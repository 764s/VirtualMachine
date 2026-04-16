// Responsibility:
//   Decision logging contracts for command lifecycle and conflict observability.
// Owns:
//   Decision category/severity taxonomy and log entry shape.
// Inputs/Outputs:
//   In: stage-level decisions from orchestrator.
//   Out: structured decision records for diagnostics and audits.
// Allowed Dependencies:
//   - DatabaseExecutionStage
// Forbidden Dependencies:
//   - Protocol serialization and external transport behavior.
// Invariants:
//   - Each decision entry is immutable and timestamped.
//   - Log sink is append-only by contract.
// Boundary Closure:
//   Upstream: execution orchestrator.
//   Downstream: diagnostic/audit consumers.

using System;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseDecisionCategory
	{
		Unknown = 0,
		Lifecycle,
		Conflict,
		Admission,
		Planning,
		Queueing,
		Execution,
		Compose,
		Commit,
		Result
	}

	public enum DatabaseDecisionSeverity
	{
		Debug = 0,
		Info,
		Warning,
		Error,
		Critical
	}

	public enum DatabaseDecisionPayloadKind
	{
		None = 0,
		VersionGate,
		PlanIdentity,
		QueueAcceptance,
		SnapshotVersion,
		ResultState,
		ConflictDecision,
		StreamCancellation
	}

	public sealed class DatabaseDecisionPayload
	{
		private DatabaseDecisionPayload(
			DatabaseDecisionPayloadKind kind,
			long? expectedVersion,
			long? currentVersion,
			string planId,
			DatabaseTaskEnqueueDisposition? enqueueDisposition,
			string queueTicket,
			long? snapshotVersion,
			DatabaseCommandState? finalState,
			bool? canProceed,
			int? canceledCount,
			string streamKey,
			DatabaseConflictDecision conflictDecision)
		{
			Kind = kind;
			ExpectedVersion = expectedVersion;
			CurrentVersion = currentVersion;
			PlanId = planId ?? string.Empty;
			EnqueueDisposition = enqueueDisposition;
			QueueTicket = queueTicket ?? string.Empty;
			SnapshotVersion = snapshotVersion;
			FinalState = finalState;
			CanProceed = canProceed;
			CanceledCount = canceledCount;
			StreamKey = streamKey ?? string.Empty;
			ConflictDecision = conflictDecision;
		}

		public static DatabaseDecisionPayload None { get; } = new DatabaseDecisionPayload(
			DatabaseDecisionPayloadKind.None,
			null,
			null,
			string.Empty,
			null,
			string.Empty,
			null,
			null,
			null,
			null,
			string.Empty,
			null);

		public DatabaseDecisionPayloadKind Kind { get; }
		public long? ExpectedVersion { get; }
		public long? CurrentVersion { get; }
		public string PlanId { get; }
		public DatabaseTaskEnqueueDisposition? EnqueueDisposition { get; }
		public string QueueTicket { get; }
		public long? SnapshotVersion { get; }
		public DatabaseCommandState? FinalState { get; }
		public bool? CanProceed { get; }
		public int? CanceledCount { get; }
		public string StreamKey { get; }
		public DatabaseConflictDecision ConflictDecision { get; }

		public static DatabaseDecisionPayload ForVersionGate(long? expectedVersion, long currentVersion)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.VersionGate,
				expectedVersion,
				currentVersion,
				string.Empty,
				null,
				string.Empty,
				null,
				null,
				null,
				null,
				string.Empty,
				null);
		}

		public static DatabaseDecisionPayload ForPlanIdentity(string planId)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.PlanIdentity,
				null,
				null,
				planId,
				null,
				string.Empty,
				null,
				null,
				null,
				null,
				string.Empty,
				null);
		}

		public static DatabaseDecisionPayload ForQueueAcceptance(DatabaseTaskEnqueueDisposition disposition, string queueTicket)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.QueueAcceptance,
				null,
				null,
				string.Empty,
				disposition,
				queueTicket,
				null,
				null,
				null,
				null,
				string.Empty,
				null);
		}

		public static DatabaseDecisionPayload ForSnapshotVersion(long snapshotVersion)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.SnapshotVersion,
				null,
				null,
				string.Empty,
				null,
				string.Empty,
				snapshotVersion,
				null,
				null,
				null,
				string.Empty,
				null);
		}

		public static DatabaseDecisionPayload ForResultState(DatabaseCommandState finalState, bool canProceed)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.ResultState,
				null,
				null,
				string.Empty,
				null,
				string.Empty,
				null,
				finalState,
				canProceed,
				null,
				string.Empty,
				null);
		}

		public static DatabaseDecisionPayload ForConflictDecision(DatabaseConflictDecision conflictDecision)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.ConflictDecision,
				null,
				null,
				string.Empty,
				null,
				string.Empty,
				null,
				null,
				null,
				null,
				string.Empty,
				conflictDecision);
		}

		public static DatabaseDecisionPayload ForStreamCancellation(int canceledCount, string streamKey)
		{
			return new DatabaseDecisionPayload(
				DatabaseDecisionPayloadKind.StreamCancellation,
				null,
				null,
				string.Empty,
				null,
				string.Empty,
				null,
				null,
				null,
				canceledCount,
				streamKey,
				null);
		}
	}

	public sealed class DatabaseDecisionLogEntry
	{
		public string CommandId { get; }
		public string CorrelationId { get; }
		public DatabaseExecutionStage Stage { get; }
		public DatabaseDecisionCategory Category { get; }
		public DatabaseDecisionSeverity Severity { get; }
		public string Code { get; }
		public string Message { get; }
		public DateTime TimestampUtc { get; }
		public DatabaseDecisionPayload Payload { get; }

		public DatabaseDecisionLogEntry(
			string commandId,
			string correlationId,
			DatabaseExecutionStage stage,
			DatabaseDecisionCategory category,
			DatabaseDecisionSeverity severity,
			string code,
			string message,
			DateTime timestampUtc,
			DatabaseDecisionPayload payload)
		{
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			Stage = stage;
			Category = category;
			Severity = severity;
			Code = code ?? string.Empty;
			Message = message ?? string.Empty;
			TimestampUtc = timestampUtc;
			Payload = payload ?? DatabaseDecisionPayload.None;
		}
	}

	public interface IDatabaseDecisionLogSink
	{
		void Write(DatabaseDecisionLogEntry entry);
	}
}
