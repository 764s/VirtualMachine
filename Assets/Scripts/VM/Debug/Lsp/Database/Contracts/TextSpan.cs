// Responsibility:
//   Offset-first text span contract for source ranges.
// Owns:
//   Start offset and length semantics only.
// Inputs/Outputs:
//   In: lexer/parser/index offsets.
//   Out: source span consumed by SpanConverter and query results.
// Allowed Dependencies:
//   - None outside Contracts.
// Forbidden Dependencies:
//   - LSP line/character conversion logic.
//   - Symbol resolution logic.
// Invariants:
//   - Span is zero-based and half-open by policy.
//   - Span never stores line/column state.
// Boundary Closure:
//   Upstream: parser/token producers.
//   Downstream: SpanConverter, SymbolIdentity, diagnostics.

using System;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public struct TextSpan : IEquatable<TextSpan>
	{
		public int Start { get; }
		public int Length { get; }
		public int End => Start + Length;

		public TextSpan(int start, int length)
		{
			Start = start < 0 ? 0 : start;
			Length = length < 0 ? 0 : length;
		}

		public bool Contains(int offset)
		{
			return offset >= Start && offset < End;
		}

		public bool Equals(TextSpan other)
		{
			return Start == other.Start && Length == other.Length;
		}

		public override bool Equals(object obj)
		{
			return obj is TextSpan other && Equals(other);
		}

		public override int GetHashCode()
		{
			return (Start * 397) ^ Length;
		}

		public override string ToString()
		{
			return "[" + Start + "," + End + ")";
		}
	}
}
