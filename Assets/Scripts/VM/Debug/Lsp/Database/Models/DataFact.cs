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
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;

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

	public abstract class DataFactPayload
	{
		private sealed class EmptyDataFactPayload : DataFactPayload
		{
		}

		public static DataFactPayload Empty { get; } = new EmptyDataFactPayload();
	}

	public sealed class SymbolDataFactPayload : DataFactPayload
	{
		public SymbolIdentity Symbol { get; }
		public bool HasRange { get; }
		public int StartLine { get; }
		public int StartCharacter { get; }
		public int EndLine { get; }
		public int EndCharacter { get; }

		public SymbolDataFactPayload(SymbolIdentity symbol)
		{
			Symbol = symbol;
			HasRange = false;
			StartLine = 0;
			StartCharacter = 0;
			EndLine = 0;
			EndCharacter = 0;
		}

		public SymbolDataFactPayload(
			SymbolIdentity symbol,
			int startLine,
			int startCharacter,
			int endLine,
			int endCharacter)
		{
			Symbol = symbol;

			if (startLine < 0)
				startLine = 0;

			if (startCharacter < 0)
				startCharacter = 0;

			if (endLine < startLine)
				endLine = startLine;

			if (endCharacter < 0)
				endCharacter = 0;

			if (endLine == startLine && endCharacter <= startCharacter)
				endCharacter = startCharacter + 1;

			StartLine = startLine;
			StartCharacter = startCharacter;
			EndLine = endLine;
			EndCharacter = endCharacter;
			HasRange = true;
		}
	}

	public sealed class IncludeEdgeDataFactPayload : DataFactPayload
	{
		public string TargetDocumentUri { get; }

		public IncludeEdgeDataFactPayload(string targetDocumentUri)
		{
			TargetDocumentUri = targetDocumentUri ?? string.Empty;
		}
	}

	public sealed class AliasBindingDataFactPayload : DataFactPayload
	{
		public string AliasName { get; }
		public string TargetDocumentUri { get; }

		public AliasBindingDataFactPayload(string aliasName, string targetDocumentUri)
		{
			AliasName = aliasName ?? string.Empty;
			TargetDocumentUri = targetDocumentUri ?? string.Empty;
		}
	}

	public sealed class TokenDataFactPayload : DataFactPayload
	{
		public int Line { get; }
		public int Start { get; }
		public int Length { get; }
		public int TokenType { get; }
		public int TokenModifiers { get; }

		public TokenDataFactPayload(int line, int start, int length, int tokenType, int tokenModifiers)
		{
			Line = line;
			Start = start;
			Length = length;
			TokenType = tokenType;
			TokenModifiers = tokenModifiers;
		}
	}

	public sealed class DataFact
	{
		public DataFactId Id { get; }
		public DataAggregateId AggregateId { get; }
		public DataFactKind Kind { get; }
		public PathKey DocumentKey { get; }
		public TextSpan Span { get; }
		public long SnapshotVersion { get; }
		public DataFactPayload Payload { get; }

		public DataFact(
			DataFactId id,
			DataAggregateId aggregateId,
			DataFactKind kind,
			PathKey documentKey,
			TextSpan span,
			long snapshotVersion,
			DataFactPayload payload)
		{
			Id = id;
			AggregateId = aggregateId;
			Kind = kind;
			DocumentKey = documentKey;
			Span = span;
			SnapshotVersion = snapshotVersion;
			Payload = payload ?? DataFactPayload.Empty;
		}
	}
}
