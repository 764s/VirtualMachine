// Responsibility:
//   Canonical file-system path key contract for equality and map lookup.
// Owns:
//   Normalized path value and equality semantics.
// Inputs/Outputs:
//   In: raw file-system path strings.
//   Out: canonical key for index/query/diagnostics maps.
// Allowed Dependencies:
//   - None outside Infrastructure.Paths contracts.
// Forbidden Dependencies:
//   - Query semantics, handlers, protocol objects.
// Invariants:
//   - Equivalent paths collapse to the same key under policy.
//   - Key representation is stable across one process execution.
// Boundary Closure:
//   Upstream: PathCanonicalizer.
//   Downstream: Workspace index, diagnostics router, document store.

using System;

namespace FFVM.Debug.Lsp.Infrastructure.Paths
{
	public struct PathKey : IEquatable<PathKey>
	{
		private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

		public string Value { get; }

		public PathKey(string value)
		{
			Value = value ?? string.Empty;
		}

		public bool Equals(PathKey other)
		{
			return Comparer.Equals(Value, other.Value);
		}

		public override bool Equals(object obj)
		{
			return obj is PathKey other && Equals(other);
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
