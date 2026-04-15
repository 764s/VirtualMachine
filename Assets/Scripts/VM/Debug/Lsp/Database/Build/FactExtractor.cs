// Responsibility:
//   Extract normalized facts from one aggregate.
// Owns:
//   Fact extraction boundary and output normalization contract.
// Inputs/Outputs:
//   In: DataAggregate + target snapshot version.
//   Out: immutable fact rows.
// Allowed Dependencies:
//   - DataAggregate
//   - DataFact
// Forbidden Dependencies:
//   - Index update orchestration.
//   - Protocol serialization.
// Invariants:
//   - Same aggregate yields same facts under same extractor version.
// Boundary Closure:
//   Upstream: IAggregateBuilder.
//   Downstream: snapshot fact table and index maintainer.

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public interface IFactExtractor
	{
		IReadOnlyList<DataFact> Extract(DataAggregate aggregate, long snapshotVersion);
	}
}
