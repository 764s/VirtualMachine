// Responsibility:
//   Canonical file URI key contract for equality and map lookup.
// Owns:
//   URI normalization shape and equality semantics.
// Inputs/Outputs:
//   In: raw URI strings from protocol payloads.
//   Out: canonical key used by protocol/query bridge.
// Allowed Dependencies:
//   - None outside Infrastructure.Paths contracts.
// Forbidden Dependencies:
//   - Symbol query decisions.
//   - Text position conversion logic.
// Invariants:
//   - Escaping and casing policy are deterministic.
//   - Equivalent URIs map to the same key.
// Boundary Closure:
//   Upstream: PathCanonicalizer and protocol adapters.
//   Downstream: Request parsing and workspace keying.

using System;

namespace FFVM.Debug.Lsp.Infrastructure.Paths
{
	public struct UriKey : IEquatable<UriKey>
	{
		private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

		public string Value { get; }

		public UriKey(string value)
		{
			Value = value ?? string.Empty;
		}

		public bool Equals(UriKey other)
		{
			return Comparer.Equals(Value, other.Value);
		}

		public override bool Equals(object obj)
		{
			return obj is UriKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return Comparer.GetHashCode(Value);
		}

		public override string ToString()
		{
			return Value;
		}
	}
}
