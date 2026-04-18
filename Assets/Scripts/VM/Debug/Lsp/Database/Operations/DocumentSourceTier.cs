// Responsibility:
//   Declares the document-source tier contract so that consumers can reason about
//   precedence when multiple sources (open buffer, watcher, disk) describe the same document.
// Owns:
//   DocumentSourceTier enum and a thin tier-aware variant of DocumentChangedChangePayload.
// Inputs/Outputs:
//   In: bridge adapters producing change events.
//   Out: orchestrator can inspect SourceTier when resolving conflicts.
// Allowed Dependencies:
//   - DocumentChangedChangePayload (same namespace).
// Forbidden Dependencies:
//   - Query facade, parser, path utilities.
// Invariants:
//   - Lower numeric value means higher priority.
//   - Default tier is Unknown (ignored by legacy code paths).

namespace FFVM.Debug.Lsp.Database
{
	public enum DocumentSourceTier
	{
		Unknown = 0,
		OpenBuffer = 1,
		Watcher = 2,
		Disk = 3,
	}

	public sealed class DocumentChangedWithTierChangePayload : DocumentChangedChangePayload
	{
		public DocumentSourceTier SourceTier { get; }

		public DocumentChangedWithTierChangePayload(string documentUri, string text, DocumentSourceTier sourceTier)
			: base(documentUri, text)
		{
			SourceTier = sourceTier;
		}
	}
}
