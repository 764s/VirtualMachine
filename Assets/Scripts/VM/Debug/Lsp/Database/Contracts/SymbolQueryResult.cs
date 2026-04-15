// Responsibility:
//   Unified output contract for symbol-query operations.
// Owns:
//   Resolved identity, ranges, and operation status metadata.
// Inputs/Outputs:
//   In: SymbolQueryCore service outputs.
//   Out: handler-ready data for protocol responses/edits.
// Allowed Dependencies:
//   - SymbolIdentity
//   - TextSpan
// Forbidden Dependencies:
//   - JSON-RPC response writing.
//   - Workspace mutation side effects.
// Invariants:
//   - Result carries enough data for idempotent response formatting.
//   - Empty result states are explicit, never implicit null semantics.
// Boundary Closure:
//   Upstream: Query services.
//   Downstream: handlers and protocol writer.

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public sealed class SymbolQueryResult
	{
		private static readonly IReadOnlyList<TextSpan> EmptyRanges = new List<TextSpan>(0);

		public bool Succeeded { get; }
		public SymbolIdentity Symbol { get; }
		public IReadOnlyList<TextSpan> Ranges { get; }
		public string Message { get; }
		public object Payload { get; }

		public SymbolQueryResult(bool succeeded, SymbolIdentity symbol, IReadOnlyList<TextSpan> ranges, string message, object payload)
		{
			Succeeded = succeeded;
			Symbol = symbol;
			Ranges = ranges ?? EmptyRanges;
			Message = message ?? string.Empty;
			Payload = payload;
		}

		public static SymbolQueryResult Success(SymbolIdentity symbol, IReadOnlyList<TextSpan> ranges = null, object payload = null)
		{
			return new SymbolQueryResult(true, symbol, ranges, string.Empty, payload);
		}

		public static SymbolQueryResult NotFound(string message)
		{
			return new SymbolQueryResult(false, null, EmptyRanges, message, null);
		}

		public static SymbolQueryResult Failure(string message)
		{
			return new SymbolQueryResult(false, null, EmptyRanges, message, null);
		}
	}
}
