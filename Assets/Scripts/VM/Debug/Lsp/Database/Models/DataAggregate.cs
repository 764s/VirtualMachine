// Responsibility:
//   Normalized aggregate unit contract (document/module grain).
// Owns:
//   Aggregate identity, source metadata, and aggregate-contained facts.
// Inputs/Outputs:
//   In: aggregate builders.
//   Out: database snapshot aggregate collection.
// Allowed Dependencies:
//   - PathKey
//   - DataFact
// Forbidden Dependencies:
//   - Protocol details.
//   - Direct query execution.
// Invariants:
//   - Aggregate id is stable for same logical owner.
//   - Aggregate facts are immutable in one snapshot.
// Boundary Closure:
//   Upstream: IAggregateBuilder.
//   Downstream: IFactExtractor and query/index readers.

using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public struct DataAggregateId : IEquatable<DataAggregateId>
	{
		public string Value { get; }

		public DataAggregateId(string value)
		{
			Value = value ?? string.Empty;
		}

		public bool Equals(DataAggregateId other)
		{
			return string.Equals(Value, other.Value, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is DataAggregateId other && Equals(other);
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

	public enum DataAggregateKind
	{
		Unknown = 0,
		Document,
		Module
	}

	public sealed class DataAggregate
	{
		private static readonly IReadOnlyList<DataFact> EmptyFacts = new List<DataFact>(0);

		public DataAggregateId Id { get; }
		public DataAggregateKind Kind { get; }
		public PathKey DocumentKey { get; }
		public string LanguageId { get; }
		public string TextHash { get; }
		public int? SourceVersion { get; }
		public IReadOnlyList<DataFact> Facts { get; }

		public DataAggregate(
			DataAggregateId id,
			DataAggregateKind kind,
			PathKey documentKey,
			string languageId,
			string textHash,
			int? sourceVersion,
			IReadOnlyList<DataFact> facts)
		{
			Id = id;
			Kind = kind;
			DocumentKey = documentKey;
			LanguageId = languageId ?? string.Empty;
			TextHash = textHash ?? string.Empty;
			SourceVersion = sourceVersion;
			Facts = facts ?? EmptyFacts;
		}
	}
}
