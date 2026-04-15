// Responsibility:
//   Single source of truth for offset-to-line mapping of one document snapshot.
// Owns:
//   Line-start index table and lookup policy.
// Inputs/Outputs:
//   In: document text snapshot.
//   Out: deterministic line start/offset lookup results.
// Allowed Dependencies:
//   - Contracts.TextPosition
//   - Contracts.TextSpan
// Forbidden Dependencies:
//   - Symbol semantics.
//   - LSP handler or protocol dispatch logic.
// Invariants:
//   - Mapping must be deterministic for a fixed snapshot version.
//   - Newline policy is explicit and tested.
// Boundary Closure:
//   Upstream: WorkspaceDocumentStore snapshots.
//   Downstream: SpanConverter, diagnostics formatting.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Infrastructure.Text
{
	public interface ILineMap
	{
		int LineCount { get; }

		bool TryGetOffset(TextPosition position, out int offset);

		TextPosition GetPosition(int offset);

		int GetLineStartOffset(int line);
	}
}
