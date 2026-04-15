// Responsibility:
//   Build one normalized aggregate from source document input.
// Owns:
//   Aggregate construction boundary and build input contract.
// Inputs/Outputs:
//   In: aggregate build request (document identity + source text metadata).
//   Out: immutable DataAggregate.
// Allowed Dependencies:
//   - PathKey
//   - DataAggregate
// Forbidden Dependencies:
//   - Global snapshot orchestration.
//   - Query-serving concerns.
// Invariants:
//   - Build result must be deterministic for same request.
// Boundary Closure:
//   Upstream: WorkspaceCodeDatabase write pipeline.
//   Downstream: Fact extractor.

using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class AggregateBuildRequest
	{
		public PathKey DocumentKey { get; }
		public string LanguageId { get; }
		public string Text { get; }
		public int? SourceVersion { get; }

		public AggregateBuildRequest(PathKey documentKey, string languageId, string text, int? sourceVersion)
		{
			DocumentKey = documentKey;
			LanguageId = languageId ?? string.Empty;
			Text = text ?? string.Empty;
			SourceVersion = sourceVersion;
		}
	}

	public interface IAggregateBuilder
	{
		DataAggregate Build(AggregateBuildRequest request);
	}
}
