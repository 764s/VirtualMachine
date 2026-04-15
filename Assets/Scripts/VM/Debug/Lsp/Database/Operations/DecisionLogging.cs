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
		public object Payload { get; }

		public DatabaseDecisionLogEntry(
			string commandId,
			string correlationId,
			DatabaseExecutionStage stage,
			DatabaseDecisionCategory category,
			DatabaseDecisionSeverity severity,
			string code,
			string message,
			DateTime timestampUtc,
			object payload)
		{
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			Stage = stage;
			Category = category;
			Severity = severity;
			Code = code ?? string.Empty;
			Message = message ?? string.Empty;
			TimestampUtc = timestampUtc;
			Payload = payload;
		}
	}

	public interface IDatabaseDecisionLogSink
	{
		void Write(DatabaseDecisionLogEntry entry);
	}
}
