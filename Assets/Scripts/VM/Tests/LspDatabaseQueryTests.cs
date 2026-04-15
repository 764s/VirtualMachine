using System;
using System.Collections.Generic;
using FFVM.Debug;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;
using UnityEngine;

/// <summary>
/// Query read-side tests for database-backed LSP facade.
/// Validates executable query behavior and payload shape for the
/// in-memory query facade introduced in the database architecture.
/// </summary>
public static class LspDatabaseQueryTests
{
#if UNITY_EDITOR
	[UnityEditor.MenuItem("TestVM/RunLspDatabaseQueryTests")]
#endif
	public static void RunAll()
	{
		int passed = 0;
		int failed = 0;

		void Assert(bool condition, string testName)
		{
			if (condition)
			{
				Debug.Log($"[PASS] {testName}");
				passed++;
			}
			else
			{
				Debug.LogError($"[FAIL] {testName}");
				failed++;
			}
		}

		var facade = new InMemoryLspQueryFacade();

		// ================================================================
		// DBQ-01: QueryDefinition returns definition range and payload
		// ================================================================
		{
			var doc = new PathKey("file:///query/def.ffs");
			var position = new TextPosition(2, 5);
			var symbol = new SymbolIdentity(
				SymbolKindTag.Function,
				"entry",
				string.Empty,
				string.Empty,
				doc.Value,
				new TextSpan(10, 5));

			var definitionFact = new DataFact(
				new DataFactId("def-1"),
				new DataAggregateId("agg-1"),
				DataFactKind.SymbolDefinition,
				doc,
				new TextSpan(10, 5),
				1,
				null);

			var index = BuildIndex(
				tuple: (doc, position, symbol),
				definition: (symbol, definitionFact),
				referencesIncludeDecl: new List<DataFact>(),
				referencesExcludeDecl: new List<DataFact>(),
				nameSymbols: new List<SymbolIdentity> { symbol });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);
			var request = SymbolQueryRequest.ForPosition("definition", doc.Value, position);

			SymbolQueryResult result = facade.QueryDefinition(snapshot, request);

			Assert(result != null && result.Succeeded, "DBQ-01A: definition query succeeded");
			Assert(result != null && result.Ranges.Count == 1 && result.Ranges[0].Start == 10, "DBQ-01B: definition range captured");
			Assert(result != null && result.Payload is LspDefinitionPayload, "DBQ-01C: definition payload type");
		}

		// ================================================================
		// DBQ-02: QueryReferences respects includeDeclaration
		// ================================================================
		{
			var doc = new PathKey("file:///query/refs.ffs");
			var position = new TextPosition(1, 1);
			var symbol = new SymbolIdentity(
				SymbolKindTag.Variable,
				"hp",
				"entry",
				string.Empty,
				doc.Value,
				new TextSpan(30, 2));

			var decl = new DataFact(new DataFactId("ref-decl"), new DataAggregateId("agg-2"), DataFactKind.SymbolDefinition, doc, new TextSpan(30, 2), 2, null);
			var useA = new DataFact(new DataFactId("ref-a"), new DataAggregateId("agg-2"), DataFactKind.SymbolReference, doc, new TextSpan(60, 2), 2, null);
			var useB = new DataFact(new DataFactId("ref-b"), new DataAggregateId("agg-2"), DataFactKind.SymbolReference, doc, new TextSpan(80, 2), 2, null);

			var refsWithDecl = new List<DataFact> { decl, useA, useB };
			var refsWithoutDecl = new List<DataFact> { useA, useB };

			var index = BuildIndex(
				tuple: (doc, position, symbol),
				definition: (symbol, decl),
				referencesIncludeDecl: refsWithDecl,
				referencesExcludeDecl: refsWithoutDecl,
				nameSymbols: new List<SymbolIdentity> { symbol });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);

			var reqWithDecl = new SymbolQueryRequest("references", doc.Value, position, new TextSpan(0, 0), true, string.Empty);
			var reqWithoutDecl = new SymbolQueryRequest("references", doc.Value, position, new TextSpan(0, 0), false, string.Empty);

			SymbolQueryResult resultWithDecl = facade.QueryReferences(snapshot, reqWithDecl);
			SymbolQueryResult resultWithoutDecl = facade.QueryReferences(snapshot, reqWithoutDecl);

			Assert(resultWithDecl != null && resultWithDecl.Succeeded && resultWithDecl.Ranges.Count == 3, "DBQ-02A: includeDeclaration returns 3 ranges");
			Assert(resultWithoutDecl != null && resultWithoutDecl.Succeeded && resultWithoutDecl.Ranges.Count == 2, "DBQ-02B: excludeDeclaration returns 2 ranges");
		}

		// ================================================================
		// DBQ-03: PrepareRename blocks non-renameable kinds
		// ================================================================
		{
			var doc = new PathKey("file:///query/rename-block.ffs");
			var position = new TextPosition(0, 0);
			var includeSymbol = new SymbolIdentity(
				SymbolKindTag.IncludeFile,
				"lib/common.ffs",
				string.Empty,
				string.Empty,
				doc.Value,
				new TextSpan(0, 8));

			var index = BuildIndex(
				tuple: (doc, position, includeSymbol),
				definition: (includeSymbol, null),
				referencesIncludeDecl: new List<DataFact>(),
				referencesExcludeDecl: new List<DataFact>(),
				nameSymbols: new List<SymbolIdentity> { includeSymbol });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);
			var request = SymbolQueryRequest.ForPosition("prepareRename", doc.Value, position);

			SymbolQueryResult result = facade.QueryPrepareRename(snapshot, request);
			Assert(result != null && !result.Succeeded, "DBQ-03: include-file symbol is not renameable");
		}

		// ================================================================
		// DBQ-04: Rename produces edits from references
		// ================================================================
		{
			var doc = new PathKey("file:///query/rename.ffs");
			var position = new TextPosition(3, 2);
			var symbol = new SymbolIdentity(
				SymbolKindTag.Variable,
				"score",
				"entry",
				string.Empty,
				doc.Value,
				new TextSpan(20, 5));

			var decl = new DataFact(new DataFactId("rename-decl"), new DataAggregateId("agg-4"), DataFactKind.SymbolDefinition, doc, new TextSpan(20, 5), 4, null);
			var use = new DataFact(new DataFactId("rename-ref"), new DataAggregateId("agg-4"), DataFactKind.SymbolReference, doc, new TextSpan(70, 5), 4, null);

			var index = BuildIndex(
				tuple: (doc, position, symbol),
				definition: (symbol, decl),
				referencesIncludeDecl: new List<DataFact> { decl, use },
				referencesExcludeDecl: new List<DataFact> { use },
				nameSymbols: new List<SymbolIdentity> { symbol });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);
			var request = new SymbolQueryRequest("rename", doc.Value, position, new TextSpan(0, 0), true, "totalScore");

			SymbolQueryResult result = facade.QueryRename(snapshot, request);

			Assert(result != null && result.Succeeded, "DBQ-04A: rename query succeeded");
			Assert(result != null && result.Payload is LspRenamePayload, "DBQ-04B: rename payload type");
			if (result != null && result.Payload is LspRenamePayload renamePayload)
			{
				Assert(renamePayload.Edits.Count == 2, "DBQ-04C: rename generated two edits");
				Assert(renamePayload.NewName == "totalScore", "DBQ-04D: rename target propagated");
			}
		}

		// ================================================================
		// DBQ-05: Completion returns indexed symbols
		// ================================================================
		{
			var doc = new PathKey("file:///query/completion.ffs");
			var position = new TextPosition(5, 1);
			var symbolA = new SymbolIdentity(SymbolKindTag.Function, "Attack", string.Empty, string.Empty, doc.Value, new TextSpan(100, 6));
			var symbolB = new SymbolIdentity(SymbolKindTag.Function, "Assist", string.Empty, string.Empty, doc.Value, new TextSpan(120, 6));
			var anchor = new SymbolIdentity(SymbolKindTag.Variable, "a", "entry", string.Empty, doc.Value, new TextSpan(50, 1));

			var index = BuildIndex(
				tuple: (doc, position, anchor),
				definition: (anchor, null),
				referencesIncludeDecl: new List<DataFact>(),
				referencesExcludeDecl: new List<DataFact>(),
				nameSymbols: new List<SymbolIdentity> { symbolA, symbolB });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);
			var request = new SymbolQueryRequest("completion", doc.Value, position, new TextSpan(0, 0), false, "Ass");

			SymbolQueryResult result = facade.QueryCompletion(snapshot, request);
			Assert(result != null && result.Succeeded, "DBQ-05A: completion query succeeded");
			Assert(result != null && result.Payload is List<LspCompletionItem>, "DBQ-05B: completion payload type");
			if (result != null && result.Payload is List<LspCompletionItem> items)
			{
				Assert(items.Count == 1 && items[0].Label == "Assist", "DBQ-05C: completion filtered by query token");
			}
		}

		// ================================================================
		// DBQ-06: SemanticTokens encodes sorted/filtered token facts
		// ================================================================
		{
			var doc = new PathKey("file:///query/semantic.ffs");
			var otherDoc = new PathKey("file:///query/other.ffs");

			var jsonToken = new JsonObject();
			jsonToken.Set("line", 2);
			jsonToken.Set("character", 4);
			jsonToken.Set("length", 3);
			jsonToken.Set("tokenType", "function");
			jsonToken.Set("tokenModifiers", 1);

			var mapToken = new Dictionary<string, object>(StringComparer.Ordinal)
			{
				["line"] = 1,
				["start"] = 1,
				["length"] = 2,
				["kind"] = "Variable",
				["tokenModifiers"] = 0,
			};

			var facts = new List<DataFact>
			{
				new DataFact(new DataFactId("tok-json"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, jsonToken),
				new DataFact(new DataFactId("tok-map"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, mapToken),
				new DataFact(new DataFactId("tok-array"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new[] { 2, 10, 5, 15, 0 }),
				new DataFact(new DataFactId("tok-dup"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new[] { 2, 10, 5, 15, 0 }),
				new DataFact(new DataFactId("tok-invalid"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new[] { -1, 10, 5, 15, 0 }),
				new DataFact(new DataFactId("tok-other-doc"), new DataAggregateId("agg-sem"), DataFactKind.Token, otherDoc, new TextSpan(0, 0), 1, new[] { 0, 0, 3, 8, 0 }),
			};

			CodeDatabaseSnapshot snapshot = BuildSnapshot(indexSnapshot: null, facts: facts);
			var request = new SymbolQueryRequest("semanticTokens/full", doc.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, string.Empty);

			object rawPayload = facade.QuerySemanticTokensFull(snapshot, request);
			Assert(rawPayload is LspSemanticTokensPayload, "DBQ-06A: semantic token payload type");

			if (rawPayload is LspSemanticTokensPayload payload)
			{
				Assert(payload.Data.Count == 15, "DBQ-06B: semantic token payload has 3 encoded tokens");
				Assert(payload.Message == string.Empty, "DBQ-06C: semantic token success message empty");

				int[] expected = { 1, 1, 2, 8, 0, 1, 4, 3, 12, 1, 0, 6, 5, 15, 0 };
				bool matched = payload.Data.Count == expected.Length;
				for (int i = 0; matched && i < expected.Length; i++)
				{
					if (payload.Data[i] != expected[i])
						matched = false;
				}

				Assert(matched, "DBQ-06D: semantic token delta encoding is deterministic");
			}
		}

		// ================================================================
		// DBQ-07: SemanticTokens requires document key
		// ================================================================
		{
			CodeDatabaseSnapshot snapshot = BuildSnapshot(indexSnapshot: null, facts: new List<DataFact>(0));
			var request = new SymbolQueryRequest("semanticTokens/full", string.Empty, new TextPosition(0, 0), new TextSpan(0, 0), false, string.Empty);

			object rawPayload = facade.QuerySemanticTokensFull(snapshot, request);
			Assert(rawPayload is LspSemanticTokensPayload, "DBQ-07A: semantic token payload type for invalid request");

			if (rawPayload is LspSemanticTokensPayload payload)
			{
				Assert(payload.Data.Count == 0, "DBQ-07B: invalid semantic token request returns empty data");
				Assert(payload.Message.IndexOf("DocumentKey", StringComparison.OrdinalIgnoreCase) >= 0, "DBQ-07C: invalid semantic token request returns reason message");
			}
		}

		Debug.Log($"[LspDatabaseQueryTests] Completed. Passed={passed}, Failed={failed}");
	}

	private static CodeDatabaseSnapshot BuildSnapshot(IIndexSnapshot indexSnapshot, IReadOnlyList<DataFact> facts = null)
	{
		return new CodeDatabaseSnapshot(
			version: 1,
			capturedAtUtc: DateTime.UtcNow,
			aggregates: new List<DataAggregate>(0),
			facts: facts ?? new List<DataFact>(0),
			indexSnapshot: indexSnapshot);
	}

	private static IIndexSnapshot BuildIndex(
		(PathKey doc, TextPosition pos, SymbolIdentity symbol) tuple,
		(SymbolIdentity symbol, DataFact fact) definition,
		IReadOnlyList<DataFact> referencesIncludeDecl,
		IReadOnlyList<DataFact> referencesExcludeDecl,
		IReadOnlyList<SymbolIdentity> nameSymbols)
	{
		var positionIndex = new FakePositionIndex();
		positionIndex.Add(tuple.doc, tuple.pos, tuple.symbol);

		var symbolIndex = new FakeSymbolIndex();
		if (definition.fact != null)
			symbolIndex.SetDefinition(definition.symbol, definition.fact);

		symbolIndex.SetReferences(definition.symbol, referencesIncludeDecl, referencesExcludeDecl);

		var includeIndex = new FakeIncludeGraphIndex();
		var nameIndex = new FakeNameIndex(nameSymbols);

		return new FakeIndexSnapshot(
			snapshotVersion: 1,
			positionIndex: positionIndex,
			symbolIndex: symbolIndex,
			includeGraphIndex: includeIndex,
			nameIndex: nameIndex);
	}

	private sealed class FakeIndexSnapshot : IIndexSnapshot
	{
		public FakeIndexSnapshot(
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

	private sealed class FakePositionIndex : IPositionIndex
	{
		private readonly Dictionary<string, SymbolIdentity> _map = new Dictionary<string, SymbolIdentity>(StringComparer.Ordinal);

		public void Add(PathKey documentKey, TextPosition position, SymbolIdentity symbol)
		{
			_map[BuildKey(documentKey, position)] = symbol;
		}

		public bool TryResolveSymbol(PathKey documentKey, TextPosition position, out SymbolIdentity symbol)
		{
			return _map.TryGetValue(BuildKey(documentKey, position), out symbol);
		}

		private static string BuildKey(PathKey key, TextPosition position)
		{
			return key.Value + "@" + position.Line + ":" + position.Character;
		}
	}

	private sealed class FakeSymbolIndex : ISymbolIndex
	{
		private readonly Dictionary<string, DataFact> _definitions = new Dictionary<string, DataFact>(StringComparer.Ordinal);
		private readonly Dictionary<string, IReadOnlyList<DataFact>> _refsWithDecl = new Dictionary<string, IReadOnlyList<DataFact>>(StringComparer.Ordinal);
		private readonly Dictionary<string, IReadOnlyList<DataFact>> _refsWithoutDecl = new Dictionary<string, IReadOnlyList<DataFact>>(StringComparer.Ordinal);

		public void SetDefinition(SymbolIdentity symbol, DataFact fact)
		{
			if (symbol == null || fact == null)
				return;

			_definitions[BuildSymbolKey(symbol)] = fact;
		}

		public void SetReferences(SymbolIdentity symbol, IReadOnlyList<DataFact> withDecl, IReadOnlyList<DataFact> withoutDecl)
		{
			if (symbol == null)
				return;

			string key = BuildSymbolKey(symbol);
			_refsWithDecl[key] = withDecl ?? new List<DataFact>(0);
			_refsWithoutDecl[key] = withoutDecl ?? new List<DataFact>(0);
		}

		public bool TryGetDefinition(SymbolIdentity symbol, out DataFact definitionFact)
		{
			if (symbol == null)
			{
				definitionFact = null;
				return false;
			}

			return _definitions.TryGetValue(BuildSymbolKey(symbol), out definitionFact);
		}

		public IReadOnlyList<DataFact> GetReferences(SymbolIdentity symbol, bool includeDeclaration)
		{
			if (symbol == null)
				return new List<DataFact>(0);

			string key = BuildSymbolKey(symbol);
			if (includeDeclaration)
			{
				if (_refsWithDecl.TryGetValue(key, out IReadOnlyList<DataFact> refsWithDecl))
					return refsWithDecl;
			}
			else
			{
				if (_refsWithoutDecl.TryGetValue(key, out IReadOnlyList<DataFact> refsWithoutDecl))
					return refsWithoutDecl;
			}

			return new List<DataFact>(0);
		}

		private static string BuildSymbolKey(SymbolIdentity symbol)
		{
			return symbol.Kind
				+ "|" + symbol.Name
				+ "|" + symbol.Scope
				+ "|" + symbol.ParentName
				+ "|" + symbol.Origin
				+ "|" + symbol.DeclarationSpan.Start
				+ "|" + symbol.DeclarationSpan.Length;
		}
	}

	private sealed class FakeIncludeGraphIndex : IIncludeGraphIndex
	{
		private static readonly IReadOnlyList<PathKey> Empty = new List<PathKey>(0);

		public IReadOnlyList<PathKey> GetIncludes(PathKey documentKey)
		{
			return Empty;
		}

		public IReadOnlyList<PathKey> GetDependents(PathKey documentKey)
		{
			return Empty;
		}
	}

	private sealed class FakeNameIndex : INameIndex
	{
		private readonly IReadOnlyList<SymbolIdentity> _symbols;

		public FakeNameIndex(IReadOnlyList<SymbolIdentity> symbols)
		{
			_symbols = symbols ?? new List<SymbolIdentity>(0);
		}

		public IReadOnlyList<SymbolIdentity> Search(string query, int limit)
		{
			string token = query ?? string.Empty;
			if (limit <= 0)
				return new List<SymbolIdentity>(0);

			var output = new List<SymbolIdentity>();
			for (int i = 0; i < _symbols.Count; i++)
			{
				SymbolIdentity symbol = _symbols[i];
				if (string.IsNullOrEmpty(token)
					|| symbol.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					output.Add(symbol);
					if (output.Count >= limit)
						break;
				}
			}

			return output;
		}
	}
}
