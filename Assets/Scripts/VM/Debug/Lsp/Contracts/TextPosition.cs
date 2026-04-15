// Responsibility:
//   Line/character position contract used at API boundaries.
// Owns:
//   Line and character shape only.
// Inputs/Outputs:
//   In: protocol layer position values.
//   Out: normalized position consumed by query and converters.
// Allowed Dependencies:
//   - None outside Contracts.
// Forbidden Dependencies:
//   - Text buffer storage.
//   - File path or URI normalization.
// Invariants:
//   - Line and character are zero-based by policy.
//   - Position does not encode source identity.
// Boundary Closure:
//   Upstream: protocol request payloads.
//   Downstream: SymbolQueryRequest, SpanConverter.

using System;

namespace FFVM.Debug.Lsp.Contracts
{
	public struct TextPosition : IEquatable<TextPosition>
	{
		public int Line { get; }
		public int Character { get; }

		public TextPosition(int line, int character)
		{
			Line = line < 0 ? 0 : line;
			Character = character < 0 ? 0 : character;
		}

		public bool Equals(TextPosition other)
		{
			return Line == other.Line && Character == other.Character;
		}

		public override bool Equals(object obj)
		{
			return obj is TextPosition other && Equals(other);
		}

		public override int GetHashCode()
		{
			return (Line * 397) ^ Character;
		}

		public override string ToString()
		{
			return Line + ":" + Character;
		}
	}
}
