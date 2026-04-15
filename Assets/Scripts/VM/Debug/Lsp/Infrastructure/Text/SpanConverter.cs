// Responsibility:
//   Only conversion point between offset spans and line/character ranges.
// Owns:
//   Conversion algorithm and boundary clamping policy.
// Inputs/Outputs:
//   In: TextSpan/TextPosition plus LineMap.
//   Out: normalized ranges used by all query handlers.
// Allowed Dependencies:
//   - Contracts.TextSpan
//   - Contracts.TextPosition
//   - Infrastructure.Text.LineMap
// Forbidden Dependencies:
//   - Any symbol-resolution or rename decision logic.
//   - Path/URI normalization rules.
// Invariants:
//   - Conversion is round-trip safe within snapshot bounds.
//   - All LSP ranges are generated through this component.
// Boundary Closure:
//   Upstream: Query services and diagnostics.
//   Downstream: handlers and response writer formatting.

using FFVM.Debug.Lsp.Contracts;

namespace FFVM.Debug.Lsp.Infrastructure.Text
{
	public interface ISpanConverter
	{
		bool TryToSpan(TextPosition start, TextPosition end, ILineMap lineMap, out TextSpan span);

		bool TryToRange(TextSpan span, ILineMap lineMap, out TextPosition start, out TextPosition end);
	}
}
