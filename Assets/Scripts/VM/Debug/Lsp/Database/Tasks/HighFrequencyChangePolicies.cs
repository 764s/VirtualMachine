// Responsibility:
//   High-frequency change policy contracts for bursty document/watcher events.
// Owns:
//   Scenario taxonomy, coalescing decisions, and supersession policy boundaries.
// Inputs/Outputs:
//   In: existing/incoming database operation requests.
//   Out: coalescing and supersession decisions for task admission.
// Allowed Dependencies:
//   - DatabaseOperationRequest
// Forbidden Dependencies:
//   - Protocol transport and feature query logic.
// Invariants:
//   - Policies are deterministic for the same pair of requests.
//   - Coalescing never changes operation kind across streams.
// Boundary Closure:
//   Upstream: task admission orchestration.
//   Downstream: IDatabaseTaskCenter enqueue/cancel behavior.

namespace FFVM.Debug.Lsp.Database
{
	public enum HighFrequencyScenarioKind
	{
		Unknown = 0,
		DocumentDidChangeBurst,
		WatchedFilesBurst,
		MixedFileSystemBurst
	}

	public enum DatabaseCoalesceDecision
	{
		None = 0,
		KeepExisting,
		ReplaceExisting,
		MergeIntoNew
	}

	public sealed class DatabaseCoalesceResult
	{
		public DatabaseCoalesceDecision Decision { get; }
		public DatabaseOperationRequest MergedRequest { get; }
		public string Message { get; }

		public DatabaseCoalesceResult(
			DatabaseCoalesceDecision decision,
			DatabaseOperationRequest mergedRequest,
			string message)
		{
			Decision = decision;
			MergedRequest = mergedRequest;
			Message = message ?? string.Empty;
		}
	}

	public interface IDatabaseOperationCoalescer
	{
		bool CanCoalesce(DatabaseOperationRequest existing, DatabaseOperationRequest incoming);

		DatabaseCoalesceResult Coalesce(DatabaseOperationRequest existing, DatabaseOperationRequest incoming);
	}

	public interface IDatabaseSupersessionPolicy
	{
		bool ShouldCancelExisting(DatabaseOperationRequest existing, DatabaseOperationRequest incoming);
	}
}
