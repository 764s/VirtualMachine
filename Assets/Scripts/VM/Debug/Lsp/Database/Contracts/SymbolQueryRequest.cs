// Responsibility:
//   Unified input contract for all symbol-query operations.
// Owns:
//   Query intent, source identity, cursor/range context.
// Inputs/Outputs:
//   In: handler-parsed protocol parameters.
//   Out: normalized request for SymbolQueryCore and sub-services.
// Allowed Dependencies:
//   - TextPosition
//   - TextSpan
//   - Path/URI keys only through canonicalized values.
// Forbidden Dependencies:
//   - Direct protocol serialization.
//   - Diagnostics routing.
// Invariants:
//   - Request is self-sufficient and immutable at query time.
//   - Source identity must be canonicalized before entering core.
// Boundary Closure:
//   Upstream: handlers.
//   Downstream: SymbolQueryCore, Definition/References/Rename services.

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public sealed class SymbolQueryRequest
	{
		public string Operation { get; }
		public string DocumentKey { get; }
		public TextPosition Position { get; }
		public TextSpan Selection { get; }
		public bool IncludeDeclaration { get; }
		public string NewName { get; }
		/// <summary>Current document text at request time. Populated by the bridge for context-sensitive queries (completion, signature help). Empty when unavailable.</summary>
		public string DocumentText { get; }

		public SymbolQueryRequest(
			string operation,
			string documentKey,
			TextPosition position,
			TextSpan selection,
			bool includeDeclaration,
			string newName,
			string documentText = null)
		{
			Operation = operation ?? string.Empty;
			DocumentKey = documentKey ?? string.Empty;
			Position = position;
			Selection = selection;
			IncludeDeclaration = includeDeclaration;
			NewName = newName ?? string.Empty;
			DocumentText = documentText ?? string.Empty;
		}

		public static SymbolQueryRequest ForPosition(string operation, string documentKey, TextPosition position)
		{
			return new SymbolQueryRequest(operation, documentKey, position, new TextSpan(0, 0), false, string.Empty);
		}
	}
}
