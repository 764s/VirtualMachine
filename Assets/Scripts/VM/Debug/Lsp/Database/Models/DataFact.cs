// Responsibility:
//   Normalized data-element contract (immutable fact row).
// Owns:
//   Fact identity, owner aggregate, semantic kind, span, and payload metadata.
// Inputs/Outputs:
//   In: fact extractor.
//   Out: snapshot fact table and derived index builders.
// Allowed Dependencies:
//   - TextSpan
//   - PathKey
// Forbidden Dependencies:
//   - Feature-specific query decisions.
//   - Mutable graph updates.
// Invariants:
//   - Fact is immutable and version-scoped.
//   - Fact kind is explicit and stable.
// Boundary Closure:
//   Upstream: IFactExtractor.
//   Downstream: index views and query facade.

using System;
using FFVM.Debug.Lsp.Contracts;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public struct DataFactId : IEquatable<DataFactId>
	{
		public string Value { get; }

		public DataFactId(string value)
		{
			Value = value ?? string.Empty;
		}

		public bool Equals(DataFactId other)
		{
			return string.Equals(Value, other.Value, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is DataFactId other && Equals(other);
		}

		public override int GetHashCode()
		{
			return Value == null ? 0 : Value.GetHashCode();
		}

		public override string ToString()
		{
			return Value;
		}
	}

	public enum DataFactKind
	{
		Unknown = 0,
		SymbolDefinition,
		SymbolReference,
		IncludeEdge,
		AliasBinding,
		TypeHint,
		Diagnostic,
		Token
	}

	public sealed class DataFact
	{
		public DataFactId Id { get; }
		public DataAggregateId AggregateId { get; }
		public DataFactKind Kind { get; }
		public PathKey DocumentKey { get; }
		public TextSpan Span { get; }
		public long SnapshotVersion { get; }
		public object Payload { get; }

		public DataFact(
			DataFactId id,
			DataAggregateId aggregateId,
			DataFactKind kind,
			PathKey documentKey,
			TextSpan span,
			long snapshotVersion,
			object payload)
		{
			Id = id;
			AggregateId = aggregateId;
			Kind = kind;
			DocumentKey = documentKey;
			Span = span;
			SnapshotVersion = snapshotVersion;
			Payload = payload;
		}
	}
}
