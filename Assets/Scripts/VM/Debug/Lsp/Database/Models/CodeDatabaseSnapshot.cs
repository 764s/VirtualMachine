// Responsibility:
//   Immutable workspace-wide read model at one version.
// Owns:
//   Aggregates, facts, and derived index view for a single version.
// Inputs/Outputs:
//   In: builders/extractors/index maintainers.
//   Out: stable snapshot consumed by all query operations.
// Allowed Dependencies:
//   - DataAggregate
//   - DataFact
//   - IIndexSnapshot
// Forbidden Dependencies:
//   - Mutable state transitions.
//   - Live protocol/event side effects.
// Invariants:
//   - Snapshot content is immutable after creation.
//   - Version + content pair is deterministic.
// Boundary Closure:
//   Upstream: WorkspaceCodeDatabase write pipeline.
//   Downstream: Query facade and read-only index consumers.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class CodeDatabaseSnapshot
	{
		private static readonly IReadOnlyList<DataAggregate> EmptyAggregates = new List<DataAggregate>(0);
		private static readonly IReadOnlyList<DataFact> EmptyFacts = new List<DataFact>(0);

		public long Version { get; }
		public DateTime CapturedAtUtc { get; }
		public IReadOnlyList<DataAggregate> Aggregates { get; }
		public IReadOnlyList<DataFact> Facts { get; }
		public IIndexSnapshot IndexSnapshot { get; }

		public CodeDatabaseSnapshot(
			long version,
			DateTime capturedAtUtc,
			IReadOnlyList<DataAggregate> aggregates,
			IReadOnlyList<DataFact> facts,
			IIndexSnapshot indexSnapshot)
		{
			Version = version;
			CapturedAtUtc = capturedAtUtc;
			Aggregates = aggregates ?? EmptyAggregates;
			Facts = facts ?? EmptyFacts;
			IndexSnapshot = indexSnapshot;
		}

		public static CodeDatabaseSnapshot Empty()
		{
			return new CodeDatabaseSnapshot(0, DateTime.UtcNow, EmptyAggregates, EmptyFacts, null);
		}
	}
}
