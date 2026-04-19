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
			if (!(previous is InMemoryBuiltIndexSnapshot previousSnapshot))
				return Build(snapshot);

			HashSet<string> changedSet = BuildChangedDocumentSet(changedDocuments);
			if (changedSet.Count == 0)
				return Build(snapshot);

			var mergedContributions = CloneContributions(previousSnapshot.ContributionsByDocument);
			foreach (string changed in changedSet)
				mergedContributions.Remove(changed);

			Dictionary<string, DocumentIndexContribution> changedContributions = BuildContributions(snapshot, changedSet);
			foreach (KeyValuePair<string, DocumentIndexContribution> pair in changedContributions)
				mergedContributions[pair.Key] = pair.Value;

			long nextVersion = snapshot != null ? snapshot.Version : previousSnapshot.SnapshotVersion;
			return BuildFromContributions(nextVersion, mergedContributions);
		}

		private static IIndexSnapshot Build(CodeDatabaseSnapshot snapshot)
		{
			CodeDatabaseSnapshot source = snapshot ?? CodeDatabaseSnapshot.Empty();
			Dictionary<string, DocumentIndexContribution> contributions = BuildContributions(source, includeDocuments: null);
			return BuildFromContributions(source.Version, contributions);
		}

		private static HashSet<string> BuildChangedDocumentSet(IReadOnlyList<PathKey> changedDocuments)
		{
			var changedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (changedDocuments == null)
				return changedSet;

			for (int i = 0; i < changedDocuments.Count; i++)
			{
				string key = NormalizeDocumentKey(changedDocuments[i].Value);
				if (!string.IsNullOrWhiteSpace(key))
					changedSet.Add(key);
			}

			return changedSet;
		}

		private static Dictionary<string, DocumentIndexContribution> BuildContributions(
			CodeDatabaseSnapshot snapshot,
			HashSet<string> includeDocuments)
		{
			CodeDatabaseSnapshot source = snapshot ?? CodeDatabaseSnapshot.Empty();
			IReadOnlyList<DataFact> facts = source.Facts ?? EmptyFacts;

			var contributions = new Dictionary<string, DocumentIndexContribution>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < facts.Count; i++)
			{
				DataFact fact = facts[i];
				if (fact == null)
					continue;

				string documentKey = NormalizeDocumentKey(fact.DocumentKey.Value);
				if (string.IsNullOrWhiteSpace(documentKey))
					continue;

				if (includeDocuments != null && !includeDocuments.Contains(documentKey))
					continue;

				if (!contributions.TryGetValue(documentKey, out DocumentIndexContribution contribution))
				{
					contribution = new DocumentIndexContribution(documentKey);
					contributions[documentKey] = contribution;
				}

				if (fact.Kind == DataFactKind.IncludeEdge)
				{
					if (TryResolveIncludeTarget(fact, out PathKey includeTarget))
					{
						string includeTargetKey = NormalizeDocumentKey(includeTarget.Value);
						if (!string.IsNullOrWhiteSpace(includeTargetKey))
							contribution.IncludesTargets.Add(includeTargetKey);
					}

					continue;
				}

				if (fact.Kind == DataFactKind.AliasBinding)
				{
					if (fact.Payload is AliasBindingDataFactPayload aliasPayload
						&& !string.IsNullOrEmpty(aliasPayload.AliasName))
					{
						string targetKey = NormalizeDocumentKey(aliasPayload.TargetDocumentUri);
						if (!string.IsNullOrWhiteSpace(targetKey))
							contribution.AliasBindings[aliasPayload.AliasName] = targetKey;
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
				if (!contribution.SymbolsByKey.ContainsKey(symbolKey))
					contribution.SymbolsByKey[symbolKey] = symbol;

				if (fact.Kind == DataFactKind.SymbolDefinition)
				{
					if (!contribution.DefinitionsBySymbol.ContainsKey(symbolKey))
						contribution.DefinitionsBySymbol[symbolKey] = fact;
				}
				else
				{
					if (!contribution.ReferencesBySymbol.TryGetValue(symbolKey, out List<DataFact> references))
					{
						references = new List<DataFact>();
						contribution.ReferencesBySymbol[symbolKey] = references;
					}

					references.Add(fact);
				}

				if (TryResolvePositionRange(fact, out PositionRange range))
					contribution.PositionEntries.Add(new PositionEntry(range, symbol));
			}

			foreach (KeyValuePair<string, DocumentIndexContribution> pair in contributions)
				pair.Value.PositionEntries.Sort(ComparePositionEntry);

			return contributions;
		}

		private static IIndexSnapshot BuildFromContributions(
			long snapshotVersion,
			Dictionary<string, DocumentIndexContribution> contributionsByDocument)
		{
			var contributions = contributionsByDocument
				?? new Dictionary<string, DocumentIndexContribution>(StringComparer.OrdinalIgnoreCase);

			var orderedDocuments = new List<string>(contributions.Keys);
			orderedDocuments.Sort(StringComparer.OrdinalIgnoreCase);

			var positionEntriesByDocument = new Dictionary<string, List<PositionEntry>>(StringComparer.OrdinalIgnoreCase);
			var definitionsBySymbol = new Dictionary<string, DataFact>(StringComparer.Ordinal);
			var referencesBySymbol = new Dictionary<string, List<DataFact>>(StringComparer.Ordinal);
			var symbolsByKey = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
			var includesByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
			var dependentsByDocument = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
			var aliasByDocument = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < orderedDocuments.Count; i++)
			{
				string documentKey = orderedDocuments[i];
				if (!contributions.TryGetValue(documentKey, out DocumentIndexContribution contribution)
					|| contribution == null)
				{
					continue;
				}

				if (contribution.PositionEntries.Count > 0)
					positionEntriesByDocument[documentKey] = new List<PositionEntry>(contribution.PositionEntries);

				if (contribution.IncludesTargets.Count > 0)
				{
					var includeTargets = new HashSet<string>(contribution.IncludesTargets, StringComparer.OrdinalIgnoreCase);
					includesByDocument[documentKey] = includeTargets;

					foreach (string includeTarget in includeTargets)
					{
						if (!dependentsByDocument.TryGetValue(includeTarget, out HashSet<string> dependents))
						{
							dependents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
							dependentsByDocument[includeTarget] = dependents;
						}

						dependents.Add(documentKey);
					}
				}

				if (contribution.AliasBindings.Count > 0)
					aliasByDocument[documentKey] = new Dictionary<string, string>(contribution.AliasBindings, StringComparer.OrdinalIgnoreCase);

				foreach (KeyValuePair<string, SymbolIdentity> symbolPair in contribution.SymbolsByKey)
				{
					if (!symbolsByKey.ContainsKey(symbolPair.Key) && symbolPair.Value != null)
						symbolsByKey[symbolPair.Key] = symbolPair.Value;
				}

				foreach (KeyValuePair<string, DataFact> definitionPair in contribution.DefinitionsBySymbol)
				{
					if (!definitionsBySymbol.ContainsKey(definitionPair.Key) && definitionPair.Value != null)
						definitionsBySymbol[definitionPair.Key] = definitionPair.Value;

					if (!symbolsByKey.ContainsKey(definitionPair.Key)
						&& definitionPair.Value != null
						&& TryResolveSymbolFromFact(definitionPair.Value, out SymbolIdentity resolvedDefinitionSymbol)
						&& resolvedDefinitionSymbol != null)
					{
						symbolsByKey[definitionPair.Key] = resolvedDefinitionSymbol;
					}
				}

				foreach (KeyValuePair<string, List<DataFact>> referencePair in contribution.ReferencesBySymbol)
				{
					if (!referencesBySymbol.TryGetValue(referencePair.Key, out List<DataFact> references))
					{
						references = new List<DataFact>();
						referencesBySymbol[referencePair.Key] = references;
					}

					if (referencePair.Value != null)
					{
						for (int refIndex = 0; refIndex < referencePair.Value.Count; refIndex++)
						{
							DataFact referenceFact = referencePair.Value[refIndex];
							if (referenceFact != null)
								references.Add(referenceFact);
						}
					}

					if (!symbolsByKey.ContainsKey(referencePair.Key) && referencePair.Value != null)
					{
						for (int refIndex = 0; refIndex < referencePair.Value.Count; refIndex++)
						{
							DataFact referenceFact = referencePair.Value[refIndex];
							if (referenceFact != null
								&& TryResolveSymbolFromFact(referenceFact, out SymbolIdentity resolvedReferenceSymbol)
								&& resolvedReferenceSymbol != null)
							{
								symbolsByKey[referencePair.Key] = resolvedReferenceSymbol;
								break;
							}
						}
					}
				}
			}

			foreach (KeyValuePair<string, List<PositionEntry>> pair in positionEntriesByDocument)
				pair.Value.Sort(ComparePositionEntry);

			foreach (KeyValuePair<string, List<DataFact>> pair in referencesBySymbol)
				pair.Value.Sort(CompareReferenceFact);

			var danglingSymbolKeys = new List<string>();
			foreach (KeyValuePair<string, SymbolIdentity> pair in symbolsByKey)
			{
				bool hasDefinition = definitionsBySymbol.ContainsKey(pair.Key);
				bool hasReferences = referencesBySymbol.TryGetValue(pair.Key, out List<DataFact> refs)
					&& refs != null
					&& refs.Count > 0;

				if (!hasDefinition && !hasReferences)
					danglingSymbolKeys.Add(pair.Key);
			}

			for (int i = 0; i < danglingSymbolKeys.Count; i++)
				symbolsByKey.Remove(danglingSymbolKeys[i]);

			var positionIndex = new InMemoryPositionIndex(positionEntriesByDocument);
			var symbolIndex = new InMemorySymbolIndex(definitionsBySymbol, referencesBySymbol);
			var includeGraphIndex = new InMemoryIncludeGraphIndex(includesByDocument, dependentsByDocument);
			var nameIndex = new InMemoryNameIndex(symbolsByKey);
			var aliasIndex = new InMemoryAliasIndex(aliasByDocument);

			return new InMemoryBuiltIndexSnapshot(
				snapshotVersion,
				positionIndex,
				symbolIndex,
				includeGraphIndex,
				nameIndex,
				aliasIndex,
				CloneContributions(contributions));
		}

		private static Dictionary<string, DocumentIndexContribution> CloneContributions(
			IReadOnlyDictionary<string, DocumentIndexContribution> source)
		{
			var clone = new Dictionary<string, DocumentIndexContribution>(StringComparer.OrdinalIgnoreCase);
			if (source == null)
				return clone;

			foreach (KeyValuePair<string, DocumentIndexContribution> pair in source)
			{
				if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
					clone[pair.Key] = pair.Value.Clone();
			}

			return clone;
		}

		private static int CompareReferenceFact(DataFact left, DataFact right)
		{
			if (left == null && right == null)
				return 0;

			if (left == null)
				return -1;

			if (right == null)
				return 1;

			int byDocument = StringComparer.OrdinalIgnoreCase.Compare(left.DocumentKey.Value, right.DocumentKey.Value);
			if (byDocument != 0)
				return byDocument;

			int bySpanStart = left.Span.Start.CompareTo(right.Span.Start);
			if (bySpanStart != 0)
				return bySpanStart;

			int bySpanLength = left.Span.Length.CompareTo(right.Span.Length);
			if (bySpanLength != 0)
				return bySpanLength;

			return StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
		}

		private static int ComparePositionEntry(PositionEntry left, PositionEntry right)
		{
			if (left == null && right == null)
				return 0;

			if (left == null)
				return -1;

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

		private static bool TryResolveIncludeTarget(DataFact fact, out PathKey includeTarget)
		{
			includeTarget = new PathKey(string.Empty);
			if (fact == null || !(fact.Payload is IncludeEdgeDataFactPayload payload))
				return false;

			string targetValue = payload.TargetDocumentUri;
			targetValue = NormalizeDocumentKey(targetValue);
			if (string.IsNullOrWhiteSpace(targetValue))
				return false;

			includeTarget = new PathKey(targetValue);
			return true;
		}

		private static bool TryResolveSymbolFromFact(DataFact fact, out SymbolIdentity symbol)
		{
			symbol = null;
			if (fact == null || !(fact.Payload is SymbolDataFactPayload payload) || payload.Symbol == null)
				return false;

			symbol = EnsureSymbolDefaults(payload.Symbol, fact);
			return true;
		}

		private static SymbolIdentity EnsureSymbolDefaults(SymbolIdentity symbol, DataFact fact)
		{
			if (symbol == null)
				return null;

			string origin = string.IsNullOrWhiteSpace(symbol.Origin)
				? fact.DocumentKey.Value
				: symbol.Origin;
			origin = NormalizeDocumentKey(origin);
			if (string.IsNullOrWhiteSpace(origin))
				origin = symbol.Origin ?? string.Empty;

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

		private static bool TryResolvePositionRange(DataFact fact, out PositionRange range)
		{
			range = PositionRange.Empty;
			if (fact == null || !(fact.Payload is SymbolDataFactPayload payload))
				return false;

			if (!payload.HasRange)
				return false;

			range = PositionRange.Create(
				payload.StartLine,
				payload.StartCharacter,
				payload.EndLine,
				payload.EndCharacter);
			return true;
		}

		private static string NormalizeDocumentKey(string value)
		{
			return DocumentKeyNormalizer.Normalize(value);
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

		private sealed class DocumentIndexContribution
		{
			public DocumentIndexContribution(string documentKey)
			{
				DocumentKey = documentKey ?? string.Empty;
				PositionEntries = new List<PositionEntry>();
				SymbolsByKey = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);
				DefinitionsBySymbol = new Dictionary<string, DataFact>(StringComparer.Ordinal);
				ReferencesBySymbol = new Dictionary<string, List<DataFact>>(StringComparer.Ordinal);
				IncludesTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				AliasBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			}

			public string DocumentKey { get; }
			public List<PositionEntry> PositionEntries { get; }
			public Dictionary<string, SymbolIdentity> SymbolsByKey { get; }
			public Dictionary<string, DataFact> DefinitionsBySymbol { get; }
			public Dictionary<string, List<DataFact>> ReferencesBySymbol { get; }
			public HashSet<string> IncludesTargets { get; }
			public Dictionary<string, string> AliasBindings { get; }

			public DocumentIndexContribution Clone()
			{
				var clone = new DocumentIndexContribution(DocumentKey);

				for (int i = 0; i < PositionEntries.Count; i++)
					clone.PositionEntries.Add(PositionEntries[i]);

				foreach (KeyValuePair<string, SymbolIdentity> pair in SymbolsByKey)
					clone.SymbolsByKey[pair.Key] = pair.Value;

				foreach (KeyValuePair<string, DataFact> pair in DefinitionsBySymbol)
					clone.DefinitionsBySymbol[pair.Key] = pair.Value;

				foreach (KeyValuePair<string, List<DataFact>> pair in ReferencesBySymbol)
				{
					clone.ReferencesBySymbol[pair.Key] = pair.Value != null
						? new List<DataFact>(pair.Value)
						: new List<DataFact>(0);
				}

				foreach (string includeTarget in IncludesTargets)
					clone.IncludesTargets.Add(includeTarget);

				foreach (KeyValuePair<string, string> alias in AliasBindings)
					clone.AliasBindings[alias.Key] = alias.Value;

				return clone;
			}
		}

		private sealed class InMemoryBuiltIndexSnapshot : IIndexSnapshot
		{
			public InMemoryBuiltIndexSnapshot(
				long snapshotVersion,
				IPositionIndex positionIndex,
				ISymbolIndex symbolIndex,
				IIncludeGraphIndex includeGraphIndex,
				INameIndex nameIndex,
				IAliasIndex aliasIndex,
				IReadOnlyDictionary<string, DocumentIndexContribution> contributionsByDocument)
			{
				SnapshotVersion = snapshotVersion;
				PositionIndex = positionIndex;
				SymbolIndex = symbolIndex;
				IncludeGraphIndex = includeGraphIndex;
				NameIndex = nameIndex;
				AliasIndex = aliasIndex;
				ContributionsByDocument = contributionsByDocument
					?? new Dictionary<string, DocumentIndexContribution>(StringComparer.OrdinalIgnoreCase);
			}

			public long SnapshotVersion { get; }
			public IPositionIndex PositionIndex { get; }
			public ISymbolIndex SymbolIndex { get; }
			public IIncludeGraphIndex IncludeGraphIndex { get; }
			public INameIndex NameIndex { get; }
			public IAliasIndex AliasIndex { get; }
			public IReadOnlyDictionary<string, DocumentIndexContribution> ContributionsByDocument { get; }
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
					_entriesByDocument[pair.Key] = pair.Value ?? EmptyEntries;
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

				PositionEntry best = FindNarrowestEntry(entries, position);

				// Fallback: when the cursor sits exactly at the end of a symbol
				// (e.g. double-click selection places the cursor one past the last character),
				// retry with the preceding character position.
				if (best == null && position.Character > 0)
					best = FindNarrowestEntry(entries, new TextPosition(position.Line, position.Character - 1));

				if (best == null)
					return false;

				symbol = best.Symbol;
				return true;
			}

			private static PositionEntry FindNarrowestEntry(IReadOnlyList<PositionEntry> entries, TextPosition position)
			{
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

				return best;
			}
		}

		private sealed class InMemorySymbolIndex : ISymbolIndex
		{
			private static readonly IReadOnlyList<DataFact> EmptyReferences = new List<DataFact>(0);

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
					_references[pair.Key] = pair.Value ?? EmptyReferences;
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
					return EmptyReferences;

				string key = BuildSymbolKey(symbol);
				_references.TryGetValue(key, out IReadOnlyList<DataFact> references);
				references = references ?? EmptyReferences;

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

		private sealed class InMemoryAliasIndex : IAliasIndex
		{
			private static readonly IReadOnlyDictionary<string, PathKey> EmptyAliases =
				new Dictionary<string, PathKey>(0);

			private readonly Dictionary<string, Dictionary<string, string>> _aliasByDocument;

			public InMemoryAliasIndex(Dictionary<string, Dictionary<string, string>> aliasByDocument)
			{
				_aliasByDocument = aliasByDocument
					?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
			}

			public bool TryResolveAlias(PathKey documentKey, string aliasName, out PathKey targetDocument)
			{
				targetDocument = new PathKey(string.Empty);
				string key = NormalizeDocumentKey(documentKey.Value);
				if (string.IsNullOrWhiteSpace(key) || string.IsNullOrEmpty(aliasName))
					return false;

				if (!_aliasByDocument.TryGetValue(key, out Dictionary<string, string> aliases))
					return false;

				if (!aliases.TryGetValue(aliasName, out string target) || string.IsNullOrWhiteSpace(target))
					return false;

				targetDocument = new PathKey(target);
				return true;
			}

			public IReadOnlyDictionary<string, PathKey> GetAliases(PathKey documentKey)
			{
				string key = NormalizeDocumentKey(documentKey.Value);
				if (string.IsNullOrWhiteSpace(key))
					return EmptyAliases;

				if (!_aliasByDocument.TryGetValue(key, out Dictionary<string, string> aliases)
					|| aliases == null || aliases.Count == 0)
				{
					return EmptyAliases;
				}

				var result = new Dictionary<string, PathKey>(aliases.Count, StringComparer.OrdinalIgnoreCase);
				foreach (KeyValuePair<string, string> pair in aliases)
				{
					if (!string.IsNullOrWhiteSpace(pair.Value))
						result[pair.Key] = new PathKey(pair.Value);
				}

				return result;
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
