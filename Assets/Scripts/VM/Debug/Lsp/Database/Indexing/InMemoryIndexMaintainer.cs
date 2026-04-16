// Responsibility:
//   Default in-memory implementation for building immutable index snapshots from facts.
// Owns:
//   Deterministic index materialization for position/symbol/include/name queries.
// Inputs/Outputs:
//   In: CodeDatabaseSnapshot fact rows.
//   Out: IIndexSnapshot with non-null index views.
// Allowed Dependencies:
//   - DataFact / CodeDatabaseSnapshot contracts.
// Forbidden Dependencies:
//   - Protocol transport and write-side orchestration.
// Invariants:
//   - Rebuild/Update are deterministic for the same snapshot.
//   - Returned index snapshot version matches source snapshot version.
// Boundary Closure:
//   Upstream: execution orchestrator compose stage.
//   Downstream: query facade read-side operations.

using System;
using System.Collections.Generic;
using FFVM.Debug;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryIndexMaintainer : IIndexMaintainer
	{
		private static readonly IReadOnlyList<DataFact> EmptyFacts = new List<DataFact>(0);

		public IIndexSnapshot Rebuild(CodeDatabaseSnapshot snapshot)
		{
			return Build(snapshot);
		}

		public IIndexSnapshot Update(
			IIndexSnapshot previous,
			CodeDatabaseSnapshot snapshot,
			IReadOnlyList<PathKey> changedDocuments)
		{
			return Build(snapshot);
		}

		private static IIndexSnapshot Build(CodeDatabaseSnapshot snapshot)
		{
			CodeDatabaseSnapshot source = snapshot ?? CodeDatabaseSnapshot.Empty();
			IReadOnlyList<DataFact> facts = source.Facts ?? EmptyFacts;

			var positionEntriesByDocument = new Dictionary<string, List<PositionEntry>>(StringComparer.OrdinalIgnoreCase);
			var definitionsBySymbol = new Dictionary<string, DataFact>(StringComparer.Ordinal);
			var referencesBySymbol = new Dictionary<string, List<DataFact>>(StringComparer.Ordinal);
			var symbolsByKey = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			var includesByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
			var dependentsByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < facts.Count; i++)
			{
				DataFact fact = facts[i];
				if (fact == null)
					continue;

				if (fact.Kind == DataFactKind.IncludeEdge)
				{
					if (TryResolveIncludeTarget(fact, out PathKey includeTarget))
					{
						AddIncludeEdge(includesByDocument, dependentsByDocument, fact.DocumentKey, includeTarget);
					}

					continue;
				}

				if (fact.Kind != DataFactKind.SymbolDefinition
					&& fact.Kind != DataFactKind.SymbolReference)
				{
					continue;
				}

				if (!TryResolveSymbolFromFact(fact, out SymbolIdentity symbol))
					continue;

				string symbolKey = BuildSymbolKey(symbol);
				if (!symbolsByKey.ContainsKey(symbolKey))
					symbolsByKey[symbolKey] = symbol;

				if (fact.Kind == DataFactKind.SymbolDefinition)
				{
					if (!definitionsBySymbol.ContainsKey(symbolKey))
						definitionsBySymbol[symbolKey] = fact;
				}
				else
				{
					if (!referencesBySymbol.TryGetValue(symbolKey, out List<DataFact> references))
					{
						references = new List<DataFact>();
						referencesBySymbol[symbolKey] = references;
					}

					references.Add(fact);
				}

				if (TryResolvePositionRange(fact, out PositionRange range))
				{
					AddPositionEntry(positionEntriesByDocument, fact.DocumentKey, range, symbol);
				}
			}

			foreach (KeyValuePair<string, List<PositionEntry>> pair in positionEntriesByDocument)
			{
				pair.Value.Sort(ComparePositionEntry);
			}

			var positionIndex = new InMemoryPositionIndex(positionEntriesByDocument);
			var symbolIndex = new InMemorySymbolIndex(definitionsBySymbol, referencesBySymbol);
			var includeGraphIndex = new InMemoryIncludeGraphIndex(includesByDocument, dependentsByDocument);
			var nameIndex = new InMemoryNameIndex(symbolsByKey);

			return new InMemoryBuiltIndexSnapshot(
				source.Version,
				positionIndex,
				symbolIndex,
				includeGraphIndex,
				nameIndex);
		}

		private static int ComparePositionEntry(PositionEntry left, PositionEntry right)
		{
			if (left == null && right == null)
				return 0;
+
			if (left == null)
				return -1;
+
			if (right == null)
				return 1;

			int byStartLine = left.Range.StartLine.CompareTo(right.Range.StartLine);
			if (byStartLine != 0)
				return byStartLine;

			int byStartCharacter = left.Range.StartCharacter.CompareTo(right.Range.StartCharacter);
			if (byStartCharacter != 0)
				return byStartCharacter;

			int byEndLine = left.Range.EndLine.CompareTo(right.Range.EndLine);
			if (byEndLine != 0)
				return byEndLine;

			return left.Range.EndCharacter.CompareTo(right.Range.EndCharacter);
		}

		private static void AddPositionEntry(
			Dictionary<string, List<PositionEntry>> positionEntriesByDocument,
			PathKey documentKey,
			PositionRange range,
			SymbolIdentity symbol)
		{
			string normalizedDocument = NormalizeDocumentKey(documentKey.Value);
			if (string.IsNullOrWhiteSpace(normalizedDocument) || symbol == null)
				return;

			if (!positionEntriesByDocument.TryGetValue(normalizedDocument, out List<PositionEntry> entries))
			{
				entries = new List<PositionEntry>();
				positionEntriesByDocument[normalizedDocument] = entries;
			}

			entries.Add(new PositionEntry(range, symbol));
		}

		private static void AddIncludeEdge(
			Dictionary<string, HashSet<string>> includesByDocument,
			Dictionary<string, HashSet<string>> dependentsByDocument,
			PathKey source,
			PathKey target)
		{
			string sourceKey = NormalizeDocumentKey(source.Value);
			string targetKey = NormalizeDocumentKey(target.Value);
			if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
				return;

			if (!includesByDocument.TryGetValue(sourceKey, out HashSet<string> includes))
			{
				includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				includesByDocument[sourceKey] = includes;
			}

			if (!dependentsByDocument.TryGetValue(targetKey, out HashSet<string> dependents))
			{
				dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				dependentsByDocument[targetKey] = dependents;
			}

			includes.Add(targetKey);
			dependents.Add(sourceKey);
		}

		private static bool TryResolveIncludeTarget(DataFact fact, out PathKey includeTarget)
		{
			includeTarget = new PathKey(string.Empty);
			if (fact == null)
				return false;

			string targetValue = string.Empty;
			if (fact.Payload is string payloadString)
			{
				targetValue = payloadString;
			}
			else if (fact.Payload is JsonObject payload)
			{
				targetValue = ReadString(payload, "includeUri");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "targetUri");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "toUri");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "target");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "include");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "path");
				if (string.IsNullOrWhiteSpace(targetValue)) targetValue = ReadString(payload, "to");
			}

			targetValue = NormalizeDocumentKey(targetValue);
			if (string.IsNullOrWhiteSpace(targetValue))
				return false;

			includeTarget = new PathKey(targetValue);
			return true;
		}

		private static bool TryResolveSymbolFromFact(DataFact fact, out SymbolIdentity symbol)
		{
			symbol = null;
			if (fact == null)
				return false;

			if (fact.Payload is SymbolIdentity directSymbol)
			{
				symbol = EnsureSymbolDefaults(directSymbol, fact);
				return true;
			}

			if (!(fact.Payload is JsonObject payload))
				return false;

			if (TryResolveSymbolFromJson(payload, fact, out SymbolIdentity payloadSymbol))
			{
				symbol = EnsureSymbolDefaults(payloadSymbol, fact);
				return true;
			}

			JsonObject nestedSymbol = payload.GetObject("symbol");
			if (nestedSymbol != null && TryResolveSymbolFromJson(nestedSymbol, fact, out SymbolIdentity nested))
			{
				symbol = EnsureSymbolDefaults(nested, fact);
				return true;
			}

			return false;
		}

		private static SymbolIdentity EnsureSymbolDefaults(SymbolIdentity symbol, DataFact fact)
		{
			if (symbol == null)
				return null;

			string origin = string.IsNullOrWhiteSpace(symbol.Origin)
				? fact.DocumentKey.Value
				: symbol.Origin;

			TextSpan declaration = symbol.DeclarationSpan.Length > 0
				? symbol.DeclarationSpan
				: fact.Span;

			return new SymbolIdentity(
				symbol.Kind,
				symbol.Name,
				symbol.Scope,
				symbol.ParentName,
				origin,
				declaration);
		}

		private static bool TryResolveSymbolFromJson(JsonObject payload, DataFact fact, out SymbolIdentity symbol)
		{
			symbol = null;
			if (payload == null)
				return false;

			string name = ReadString(payload, "name");
			if (string.IsNullOrWhiteSpace(name))
				name = ReadString(payload, "symbolName");
			if (string.IsNullOrWhiteSpace(name))
				name = ReadString(payload, "identifier");
			if (string.IsNullOrWhiteSpace(name))
				return false;

			SymbolKindTag kind = SymbolKindTag.Unknown;
			if (payload.ContainsKey("kind"))
				TryResolveKind(payload.Get("kind"), out kind);

			string scope = ReadString(payload, "scope");
			string parentName = ReadString(payload, "parentName");
			if (string.IsNullOrWhiteSpace(parentName))
				parentName = ReadString(payload, "parent");

			string origin = ReadString(payload, "origin");
			if (string.IsNullOrWhiteSpace(origin))
				origin = fact.DocumentKey.Value;

			TextSpan declarationSpan = fact.Span;
			JsonObject declaration = payload.GetObject("declarationSpan");
			if (declaration != null)
			{
				if (TryGetInt(declaration, "start", out int start)
					&& TryGetInt(declaration, "length", out int length)
					&& length >= 0)
				{
					declarationSpan = new TextSpan(start, length);
				}
			}
			else if (TryGetInt(payload, "declarationStart", out int declarationStart)
				&& TryGetInt(payload, "declarationLength", out int declarationLength)
				&& declarationLength >= 0)
			{
				declarationSpan = new TextSpan(declarationStart, declarationLength);
			}

			symbol = new SymbolIdentity(kind, name, scope, parentName, origin, declarationSpan);
			return true;
		}

		private static bool TryResolveKind(object rawKind, out SymbolKindTag kind)
		{
			kind = SymbolKindTag.Unknown;
			if (rawKind == null)
				return false;

			if (rawKind is string kindString)
			{
				if (Enum.TryParse(kindString, true, out SymbolKindTag parsed))
				{
					kind = parsed;
					return true;
				}

				return false;
			}

			if (TryConvertInt(rawKind, out int kindValue)
				&& Enum.IsDefined(typeof(SymbolKindTag), kindValue))
			{
				kind = (SymbolKindTag)kindValue;
				return true;
			}

			return false;
		}

		private static bool TryResolvePositionRange(DataFact fact, out PositionRange range)
		{
			range = PositionRange.Empty;
			if (fact == null)
				return false;

			if (!(fact.Payload is JsonObject payload))
				return false;

			if (TryResolvePositionRangeFromJson(payload, out range))
				return true;

			JsonObject nestedSymbol = payload.GetObject("symbol");
			if (nestedSymbol != null && TryResolvePositionRangeFromJson(nestedSymbol, out range))
				return true;

			return false;
		}

		private static bool TryResolvePositionRangeFromJson(JsonObject payload, out PositionRange range)
		{
			range = PositionRange.Empty;
			if (payload == null)
				return false;

			JsonObject rangeObject = payload.GetObject("range");
			if (rangeObject != null && TryParseRangeObject(rangeObject, out range))
				return true;

			JsonObject location = payload.GetObject("location");
			if (location != null)
			{
				JsonObject locationRange = location.GetObject("range");
				if (locationRange != null && TryParseRangeObject(locationRange, out range))
					return true;
			}

			if (TryGetInt(payload, "line", out int line))
			{
				int startCharacter;
				if (!TryGetInt(payload, "character", out startCharacter)
					&& !TryGetInt(payload, "start", out startCharacter))
				{
					startCharacter = 0;
				}

				if (TryGetInt(payload, "length", out int length) && length > 0)
				{
					range = PositionRange.Create(line, startCharacter, line, startCharacter + length);
					return true;
				}
			}

			if (TryGetInt(payload, "startLine", out int startLine)
				&& TryGetInt(payload, "startCharacter", out int startChar)
				&& TryGetInt(payload, "endLine", out int endLine)
				&& TryGetInt(payload, "endCharacter", out int endChar))
			{
				range = PositionRange.Create(startLine, startChar, endLine, endChar);
				return true;
			}

			return false;
		}

		private static bool TryParseRangeObject(JsonObject rangeObject, out PositionRange range)
		{
			range = PositionRange.Empty;
			if (rangeObject == null)
				return false;

			JsonObject start = rangeObject.GetObject("start");
			JsonObject end = rangeObject.GetObject("end");
			if (start == null || end == null)
				return false;

			if (!TryGetInt(start, "line", out int startLine)
				|| !TryGetInt(start, "character", out int startCharacter)
				|| !TryGetInt(end, "line", out int endLine)
				|| !TryGetInt(end, "character", out int endCharacter))
			{
				return false;
			}

			range = PositionRange.Create(startLine, startCharacter, endLine, endCharacter);
			return true;
		}

		private static bool TryGetInt(JsonObject payload, string key, out int value)
		{
			value = 0;
			if (payload == null || string.IsNullOrWhiteSpace(key) || !payload.ContainsKey(key))
				return false;

			return TryConvertInt(payload.Get(key), out value);
		}

		private static bool TryConvertInt(object raw, out int value)
		{
			value = 0;
			if (raw == null)
				return false;

			if (raw is int intValue)
			{
				value = intValue;
				return true;
			}

			if (raw is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
			{
				value = (int)longValue;
				return true;
			}

			if (raw is double doubleValue)
			{
				value = (int)doubleValue;
				return true;
			}

			if (raw is float floatValue)
			{
				value = (int)floatValue;
				return true;
			}

			if (raw is string stringValue)
				return int.TryParse(stringValue, out value);

			return false;
		}

		private static string ReadString(JsonObject payload, string key)
		{
			if (payload == null || string.IsNullOrWhiteSpace(key) || !payload.ContainsKey(key))
				return string.Empty;

			string value = payload.GetString(key);
			return value ?? string.Empty;
		}

		private static string NormalizeDocumentKey(string value)
		{
			return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
		}

		private static string BuildSymbolKey(SymbolIdentity symbol)
		{
			if (symbol == null)
				return string.Empty;

			return symbol.Kind
				+ "|" + (symbol.Name ?? string.Empty)
				+ "|" + (symbol.Scope ?? string.Empty)
				+ "|" + (symbol.ParentName ?? string.Empty)
				+ "|" + (symbol.Origin ?? string.Empty)
				+ "|" + symbol.DeclarationSpan.Start
				+ "|" + symbol.DeclarationSpan.Length;
		}

		private sealed class InMemoryBuiltIndexSnapshot : IIndexSnapshot
		{
			public InMemoryBuiltIndexSnapshot(
				long snapshotVersion,
				IPositionIndex positionIndex,
				ISymbolIndex symbolIndex,
				IIncludeGraphIndex includeGraphIndex,
				INameIndex nameIndex)
			{
				SnapshotVersion = snapshotVersion;
				PositionIndex = positionIndex;
				SymbolIndex = symbolIndex;
				IncludeGraphIndex = includeGraphIndex;
				NameIndex = nameIndex;
			}

			public long SnapshotVersion { get; }
			public IPositionIndex PositionIndex { get; }
			public ISymbolIndex SymbolIndex { get; }
			public IIncludeGraphIndex IncludeGraphIndex { get; }
			public INameIndex NameIndex { get; }
		}

		private sealed class InMemoryPositionIndex : IPositionIndex
		{
			private static readonly IReadOnlyList<PositionEntry> EmptyEntries = new List<PositionEntry>(0);
			private readonly Dictionary<string, IReadOnlyList<PositionEntry>> _entriesByDocument;

			public InMemoryPositionIndex(Dictionary<string, List<PositionEntry>> entriesByDocument)
			{
				_entriesByDocument = new Dictionary<string, IReadOnlyList<PositionEntry>>(StringComparer.OrdinalIgnoreCase);
				if (entriesByDocument == null)
					return;

				foreach (KeyValuePair<string, List<PositionEntry>> pair in entriesByDocument)
				{
					_entriesByDocument[pair.Key] = pair.Value ?? EmptyEntries;
				}
			}

			public bool TryResolveSymbol(PathKey documentKey, TextPosition position, out SymbolIdentity symbol)
			{
				symbol = null;
				string key = NormalizeDocumentKey(documentKey.Value);
				if (string.IsNullOrWhiteSpace(key)
					|| !_entriesByDocument.TryGetValue(key, out IReadOnlyList<PositionEntry> entries)
					|| entries == null
					|| entries.Count == 0)
				{
					return false;
				}

				PositionEntry best = null;
				long bestWidth = long.MaxValue;
				for (int i = 0; i < entries.Count; i++)
				{
					PositionEntry candidate = entries[i];
					if (candidate == null || candidate.Symbol == null)
						continue;

					if (!candidate.Range.Contains(position))
						continue;

					long width = candidate.Range.WidthScore;
					if (best == null || width < bestWidth)
					{
						best = candidate;
						bestWidth = width;
					}
				}

				if (best == null)
					return false;

				symbol = best.Symbol;
				return true;
			}
		}

		private sealed class InMemorySymbolIndex : ISymbolIndex
		{
			private static readonly IReadOnlyList<DataFact> EmptyFacts = new List<DataFact>(0);

			private readonly Dictionary<string, DataFact> _definitions;
			private readonly Dictionary<string, IReadOnlyList<DataFact>> _references;

			public InMemorySymbolIndex(
				Dictionary<string, DataFact> definitions,
				Dictionary<string, List<DataFact>> references)
			{
				_definitions = definitions ?? new Dictionary<string, DataFact>(StringComparer.Ordinal);
				_references = new Dictionary<string, IReadOnlyList<DataFact>>(StringComparer.Ordinal);

				if (references == null)
					return;

				foreach (KeyValuePair<string, List<DataFact>> pair in references)
				{
					_references[pair.Key] = pair.Value ?? EmptyFacts;
				}
			}

			public bool TryGetDefinition(SymbolIdentity symbol, out DataFact definitionFact)
			{
				definitionFact = null;
				if (symbol == null)
					return false;

				return _definitions.TryGetValue(BuildSymbolKey(symbol), out definitionFact);
			}

			public IReadOnlyList<DataFact> GetReferences(SymbolIdentity symbol, bool includeDeclaration)
			{
				if (symbol == null)
					return EmptyFacts;

				string key = BuildSymbolKey(symbol);
				_references.TryGetValue(key, out IReadOnlyList<DataFact> references);
				references = references ?? EmptyFacts;

				if (!includeDeclaration)
					return references;

				var output = new List<DataFact>(references.Count + 1);
				if (_definitions.TryGetValue(key, out DataFact definition) && definition != null)
					output.Add(definition);

				for (int i = 0; i < references.Count; i++)
				{
					DataFact fact = references[i];
					if (fact == null)
						continue;

					if (definition != null && fact.Id.Equals(definition.Id))
						continue;

					output.Add(fact);
				}

				return output;
			}
		}

		private sealed class InMemoryIncludeGraphIndex : IIncludeGraphIndex
		{
			private static readonly IReadOnlyList<PathKey> EmptyPaths = new List<PathKey>(0);
			private readonly Dictionary<string, IReadOnlyList<PathKey>> _includesByDocument;
			private readonly Dictionary<string, IReadOnlyList<PathKey>> _dependentsByDocument;

			public InMemoryIncludeGraphIndex(
				Dictionary<string, HashSet<string>> includesByDocument,
				Dictionary<string, HashSet<string>> dependentsByDocument)
			{
				_includesByDocument = BuildPathIndex(includesByDocument);
				_dependentsByDocument = BuildPathIndex(dependentsByDocument);
			}

			public IReadOnlyList<PathKey> GetIncludes(PathKey documentKey)
			{
				string key = NormalizeDocumentKey(documentKey.Value);
				if (string.IsNullOrWhiteSpace(key)
					|| !_includesByDocument.TryGetValue(key, out IReadOnlyList<PathKey> includes))
				{
					return EmptyPaths;
				}

				return includes;
			}

			public IReadOnlyList<PathKey> GetDependents(PathKey documentKey)
			{
				string key = NormalizeDocumentKey(documentKey.Value);
				if (string.IsNullOrWhiteSpace(key)
					|| !_dependentsByDocument.TryGetValue(key, out IReadOnlyList<PathKey> dependents))
				{
					return EmptyPaths;
				}

				return dependents;
			}

			private static Dictionary<string, IReadOnlyList<PathKey>> BuildPathIndex(
				Dictionary<string, HashSet<string>> source)
			{
				var output = new Dictionary<string, IReadOnlyList<PathKey>>(StringComparer.OrdinalIgnoreCase);
				if (source == null)
					return output;

				foreach (KeyValuePair<string, HashSet<string>> pair in source)
				{
					var values = new List<PathKey>();
					if (pair.Value != null)
					{
						foreach (string item in pair.Value)
						{
							if (!string.IsNullOrWhiteSpace(item))
								values.Add(new PathKey(item));
						}
					}

					values.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Value, right.Value));
					output[pair.Key] = values;
				}

				return output;
			}
		}

		private sealed class InMemoryNameIndex : INameIndex
		{
			private static readonly IReadOnlyList<SymbolIdentity> EmptySymbols = new List<SymbolIdentity>(0);
			private readonly IReadOnlyList<SymbolIdentity> _symbols;

			public InMemoryNameIndex(Dictionary<string, SymbolIdentity> symbolsByKey)
			{
				if (symbolsByKey == null || symbolsByKey.Count == 0)
				{
					_symbols = EmptySymbols;
					return;
				}

				var output = new List<SymbolIdentity>(symbolsByKey.Count);
				foreach (KeyValuePair<string, SymbolIdentity> pair in symbolsByKey)
				{
					if (pair.Value != null)
						output.Add(pair.Value);
				}

				output.Sort(CompareSymbolIdentity);
				_symbols = output;
			}

			public IReadOnlyList<SymbolIdentity> Search(string query, int limit)
			{
				if (limit <= 0)
					return EmptySymbols;

				string token = query ?? string.Empty;
				if (token.Length == 0)
				{
					if (_symbols.Count <= limit)
						return _symbols;

					var trimmed = new List<SymbolIdentity>(limit);
					for (int i = 0; i < limit; i++)
						trimmed.Add(_symbols[i]);
					return trimmed;
				}

				var matches = new List<SymbolIdentity>();
				for (int i = 0; i < _symbols.Count; i++)
				{
					SymbolIdentity symbol = _symbols[i];
					if (symbol == null)
						continue;

					if (symbol.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						matches.Add(symbol);
						if (matches.Count >= limit)
							break;
					}
				}

				return matches;
			}

			private static int CompareSymbolIdentity(SymbolIdentity left, SymbolIdentity right)
			{
				if (left == null && right == null)
					return 0;

				if (left == null)
					return -1;

				if (right == null)
					return 1;

				int byName = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
				if (byName != 0)
					return byName;

				int byScope = StringComparer.OrdinalIgnoreCase.Compare(left.Scope, right.Scope);
				if (byScope != 0)
					return byScope;

				return StringComparer.OrdinalIgnoreCase.Compare(left.ParentName, right.ParentName);
			}
		}

		private sealed class PositionEntry
		{
			public PositionEntry(PositionRange range, SymbolIdentity symbol)
			{
				Range = range;
				Symbol = symbol;
			}

			public PositionRange Range { get; }
			public SymbolIdentity Symbol { get; }
		}

		private struct PositionRange
		{
			public static readonly PositionRange Empty = new PositionRange(0, 0, 0, 0);

			public int StartLine { get; }
			public int StartCharacter { get; }
			public int EndLine { get; }
			public int EndCharacter { get; }

			public long WidthScore
			{
				get
				{
					long lineWidth = EndLine - StartLine;
					long charWidth = EndCharacter - StartCharacter;
					return (lineWidth * 1000000L) + charWidth;
				}
			}

			public PositionRange(int startLine, int startCharacter, int endLine, int endCharacter)
			{
				StartLine = startLine;
				StartCharacter = startCharacter;
				EndLine = endLine;
				EndCharacter = endCharacter;
			}

			public bool Contains(TextPosition position)
			{
				if (position.Line < StartLine || position.Line > EndLine)
					return false;

				if (position.Line == StartLine && position.Character < StartCharacter)
					return false;

				if (position.Line == EndLine && position.Character >= EndCharacter)
					return false;

				return true;
			}

			public static PositionRange Create(int startLine, int startCharacter, int endLine, int endCharacter)
			{
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

				return new PositionRange(startLine, startCharacter, endLine, endCharacter);
			}
		}
	}
}
