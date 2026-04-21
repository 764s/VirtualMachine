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
			Assert(
				result != null
				&& result.Payload != null
				&& result.Payload.Kind == SymbolQueryPayloadKind.Definition
				&& result.Payload.Definition != null,
				"DBQ-01C: definition payload type");
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
			Assert(
				result != null
				&& result.Payload != null
				&& result.Payload.Kind == SymbolQueryPayloadKind.Rename
				&& result.Payload.Rename != null,
				"DBQ-04B: rename payload type");
			if (result != null && result.Payload != null && result.Payload.Rename != null)
			{
				LspRenamePayload renamePayload = result.Payload.Rename;
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
			var symbolC = new SymbolIdentity(SymbolKindTag.Variable, "totalFrames", string.Empty, string.Empty, doc.Value, new TextSpan(140, 11), documentation: "Total animation frames");
			var anchor = new SymbolIdentity(SymbolKindTag.Variable, "a", "entry", string.Empty, doc.Value, new TextSpan(50, 1));

			var index = BuildIndex(
				tuple: (doc, position, anchor),
				definition: (anchor, null),
				referencesIncludeDecl: new List<DataFact>(),
				referencesExcludeDecl: new List<DataFact>(),
				nameSymbols: new List<SymbolIdentity> { symbolA, symbolB, symbolC });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);
			var request = new SymbolQueryRequest("completion", doc.Value, position, new TextSpan(0, 0), false, "Ass");

			SymbolQueryResult result = facade.QueryCompletion(snapshot, request);
			Assert(result != null && result.Succeeded, "DBQ-05A: completion query succeeded");
			Assert(
				result != null
				&& result.Payload != null
				&& result.Payload.Kind == SymbolQueryPayloadKind.Completion,
				"DBQ-05B: completion payload type");
			IReadOnlyList<LspCompletionItem> items = result != null && result.Payload != null
				? result.Payload.CompletionItems
				: null;
			if (items != null)
			{
				bool hasAssist = false;
				bool hasAttack = false;
				bool hasKeyword = false;
				bool hasDocDetail = false;
				for (int i = 0; i < items.Count; i++)
				{
					if (items[i].Label == "Assist") hasAssist = true;
					if (items[i].Label == "Attack") hasAttack = true;
					if (items[i].Label == "func") hasKeyword = true;
					if (items[i].Label == "totalFrames" && items[i].Detail == "Total animation frames") hasDocDetail = true;
				}
				Assert(hasAssist && hasAttack, "DBQ-05C: completion returns all indexed functions");
				Assert(hasKeyword, "DBQ-05D: completion includes language keywords");
				Assert(hasDocDetail, "DBQ-05E: completion detail prefers documentation summary");
			}
		}

		// ================================================================
		// DBQ-05F: InMemoryIndexMaintainer preserves Documentation / TypeName / IsPrivate
		// ================================================================
		// Regression: EnsureSymbolDefaults previously used the 6-arg SymbolIdentity
		// constructor, silently dropping Documentation/TypeName/IsPrivate. That made
		// every indexed symbol emerge with empty docs, so completion items never
		// showed the triple-slash comment summary even though extraction populated it.
		{
			var doc = new PathKey("file:///query/pipeline.ffs");
			var originalSymbol = new SymbolIdentity(
				SymbolKindTag.Function,
				"DocumentedFunc",
				string.Empty,
				string.Empty,
				doc.Value,
				new TextSpan(10, 14),
				documentation: "```ffvm\nfunc DocumentedFunc(): void\n```\n\n---\n\nDoes the thing.",
				typeName: "void",
				isPrivate: true);

			var definitionFact = new DataFact(
				new DataFactId("pipeline-def"),
				new DataAggregateId("pipeline-agg"),
				DataFactKind.SymbolDefinition,
				doc,
				new TextSpan(10, 14),
				1,
				new SymbolDataFactPayload(originalSymbol, 0, 5, 0, 19));

			var rawSnapshot = new CodeDatabaseSnapshot(
				1,
				DateTime.UtcNow,
				new List<DataAggregate>(0),
				new List<DataFact> { definitionFact },
				indexSnapshot: null);

			var maintainer = new InMemoryIndexMaintainer();
			IIndexSnapshot builtIndex = maintainer.Rebuild(rawSnapshot);

			Assert(builtIndex != null && builtIndex.NameIndex != null,
				"DBQ-05F-A: InMemoryIndexMaintainer produced a NameIndex");

			SymbolIdentity resolved = null;
			if (builtIndex != null && builtIndex.NameIndex != null)
			{
				IReadOnlyList<SymbolIdentity> all = builtIndex.NameIndex.Search(string.Empty, 32);
				if (all != null)
				{
					for (int i = 0; i < all.Count; i++)
					{
						if (all[i] != null && all[i].Name == "DocumentedFunc")
						{
							resolved = all[i];
							break;
						}
					}
				}
			}

			Assert(resolved != null, "DBQ-05F-B: indexed symbol resolvable by name");
			Assert(resolved != null && !string.IsNullOrEmpty(resolved.Documentation)
				&& resolved.Documentation.Contains("Does the thing."),
				"DBQ-05F-C: maintainer preserves SymbolIdentity.Documentation through the index");
			Assert(resolved != null && resolved.TypeName == "void",
				"DBQ-05F-D: maintainer preserves SymbolIdentity.TypeName through the index");
			Assert(resolved != null && resolved.IsPrivate,
				"DBQ-05F-E: maintainer preserves SymbolIdentity.IsPrivate through the index");

			// End-to-end: feed the built index through the facade and verify
			// completion items expose the doc-derived detail + documentation fields.
			var facadeSnapshot = new CodeDatabaseSnapshot(
				1,
				DateTime.UtcNow,
				new List<DataAggregate>(0),
				new List<DataFact> { definitionFact },
				builtIndex);
			var completionRequest = new SymbolQueryRequest(
				"completion", doc.Value, new TextPosition(0, 5),
				new TextSpan(0, 0), false, "Doc");
			SymbolQueryResult completionResult = facade.QueryCompletion(facadeSnapshot, completionRequest);

			LspCompletionItem pipelineItem = null;
			if (completionResult != null
				&& completionResult.Payload != null
				&& completionResult.Payload.CompletionItems != null)
			{
				for (int i = 0; i < completionResult.Payload.CompletionItems.Count; i++)
				{
					LspCompletionItem candidate = completionResult.Payload.CompletionItems[i];
					if (candidate != null && candidate.Label == "DocumentedFunc")
					{
						pipelineItem = candidate;
						break;
					}
				}
			}

			Assert(pipelineItem != null, "DBQ-05F-F: completion surfaces the indexed symbol");
			Assert(pipelineItem != null && pipelineItem.Detail == "Does the thing.",
				"DBQ-05F-G: completion Detail is the first line of the doc comment");
			Assert(pipelineItem != null && !string.IsNullOrEmpty(pipelineItem.Documentation)
				&& pipelineItem.Documentation.Contains("Does the thing."),
				"DBQ-05F-H: completion Documentation carries the full markdown doc");
		}

		// ================================================================
		// DBQ-06: SemanticTokens encodes sorted/filtered token facts
		// ================================================================
		{
			var doc = new PathKey("file:///query/semantic.ffs");
			var otherDoc = new PathKey("file:///query/other.ffs");

			var facts = new List<DataFact>
			{
				new DataFact(new DataFactId("tok-func"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new TokenDataFactPayload(2, 4, 3, 3, 1)),
				new DataFact(new DataFactId("tok-var"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new TokenDataFactPayload(1, 1, 2, 4, 0)),
				new DataFact(new DataFactId("tok-keyword"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new TokenDataFactPayload(2, 10, 5, 8, 0)),
				new DataFact(new DataFactId("tok-dup"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new TokenDataFactPayload(2, 10, 5, 8, 0)),
				new DataFact(new DataFactId("tok-invalid"), new DataAggregateId("agg-sem"), DataFactKind.Token, doc, new TextSpan(0, 0), 1, new TokenDataFactPayload(-1, 10, 5, 8, 0)),
				new DataFact(new DataFactId("tok-other-doc"), new DataAggregateId("agg-sem"), DataFactKind.Token, otherDoc, new TextSpan(0, 0), 1, new TokenDataFactPayload(0, 0, 3, 4, 0)),
			};

			CodeDatabaseSnapshot snapshot = BuildSnapshot(indexSnapshot: null, facts: facts);
			var request = new SymbolQueryRequest("semanticTokens/full", doc.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, string.Empty);

			LspSemanticTokensPayload payload = facade.QuerySemanticTokensFull(snapshot, request);
			Assert(payload != null, "DBQ-06A: semantic token payload type");

			if (payload != null)
			{
				Assert(payload.Data.Count == 15, "DBQ-06B: semantic token payload has 3 encoded tokens");
				Assert(payload.Message == string.Empty, "DBQ-06C: semantic token success message empty");

				int[] expected = { 1, 1, 2, 4, 0, 1, 4, 3, 3, 1, 0, 6, 5, 8, 0 };
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

			LspSemanticTokensPayload payload = facade.QuerySemanticTokensFull(snapshot, request);
			Assert(payload != null, "DBQ-07A: semantic token payload type for invalid request");

			if (payload != null)
			{
				Assert(payload.Data.Count == 0, "DBQ-07B: invalid semantic token request returns empty data");
				Assert(payload.Message.IndexOf("DocumentKey", StringComparison.OrdinalIgnoreCase) >= 0, "DBQ-07C: invalid semantic token request returns reason message");
			}
		}

		// ================================================================
		// DBQ-08: QueryReferences is deduped and stable-ordered
		// ================================================================
		{
			var docA = new PathKey("file:///query/refs-sort-a.ffs");
			var docB = new PathKey("file:///query/refs-sort-b.ffs");
			var position = new TextPosition(0, 2);

			var symbol = new SymbolIdentity(
				SymbolKindTag.Function,
				"helper",
				string.Empty,
				string.Empty,
				docA.Value,
				new TextSpan(0, 6));

			var decl = new DataFact(
				new DataFactId("dbq8-decl"),
				new DataAggregateId("agg-dbq8"),
				DataFactKind.SymbolDefinition,
				docA,
				new TextSpan(0, 6),
				snapshotVersion: 8,
				payload: new SymbolDataFactPayload(symbol, 0, 0, 0, 6));

			var refA = new DataFact(
				new DataFactId("dbq8-ref-a"),
				new DataAggregateId("agg-dbq8"),
				DataFactKind.SymbolReference,
				docA,
				new TextSpan(40, 6),
				snapshotVersion: 8,
				payload: new SymbolDataFactPayload(symbol, 3, 4, 3, 10));

			var refADuplicate = new DataFact(
				new DataFactId("dbq8-ref-a-dup"),
				new DataAggregateId("agg-dbq8"),
				DataFactKind.SymbolReference,
				docA,
				new TextSpan(41, 6),
				snapshotVersion: 8,
				payload: new SymbolDataFactPayload(symbol, 3, 4, 3, 10));

			var refB = new DataFact(
				new DataFactId("dbq8-ref-b"),
				new DataAggregateId("agg-dbq8"),
				DataFactKind.SymbolReference,
				docB,
				new TextSpan(10, 6),
				snapshotVersion: 8,
				payload: new SymbolDataFactPayload(symbol, 1, 2, 1, 8));

			var refsWithDecl = new List<DataFact> { refB, refADuplicate, decl, refA };
			var refsWithoutDecl = new List<DataFact> { refB, refADuplicate, refA };

			var index = BuildIndex(
				tuple: (docA, position, symbol),
				definition: (symbol, decl),
				referencesIncludeDecl: refsWithDecl,
				referencesExcludeDecl: refsWithoutDecl,
				nameSymbols: new List<SymbolIdentity> { symbol });

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);

			SymbolQueryResult withDecl = facade.QueryReferences(
				snapshot,
				new SymbolQueryRequest("references", docA.Value, position, new TextSpan(0, 0), true, string.Empty));

			SymbolQueryResult withoutDecl = facade.QueryReferences(
				snapshot,
				new SymbolQueryRequest("references", docA.Value, position, new TextSpan(0, 0), false, string.Empty));

			Assert(withDecl != null && withDecl.Succeeded && withDecl.Ranges.Count == 3, "DBQ-08A: includeDeclaration result is deduped");
			Assert(withoutDecl != null && withoutDecl.Succeeded && withoutDecl.Ranges.Count == 2, "DBQ-08B: excludeDeclaration result is deduped");

			IReadOnlyList<LspReferenceItem> withDeclItems = withDecl != null && withDecl.Payload != null
				? withDecl.Payload.References
				: null;

			bool withDeclSorted = withDeclItems != null
				&& withDeclItems.Count == 3
				&& string.Equals(withDeclItems[0].DocumentKey, docA.Value, StringComparison.OrdinalIgnoreCase)
				&& ((withDeclItems[0].SourcePayload as SymbolDataFactPayload)?.StartLine ?? -1) == 0
				&& string.Equals(withDeclItems[1].DocumentKey, docA.Value, StringComparison.OrdinalIgnoreCase)
				&& ((withDeclItems[1].SourcePayload as SymbolDataFactPayload)?.StartLine ?? -1) == 3
				&& string.Equals(withDeclItems[2].DocumentKey, docB.Value, StringComparison.OrdinalIgnoreCase)
				&& ((withDeclItems[2].SourcePayload as SymbolDataFactPayload)?.StartLine ?? -1) == 1;

			Assert(withDeclSorted, "DBQ-08C: includeDeclaration references are stable-ordered by doc/line");

			IReadOnlyList<LspReferenceItem> withoutDeclItems = withoutDecl != null && withoutDecl.Payload != null
				? withoutDecl.Payload.References
				: null;

			bool withoutDeclSorted = withoutDeclItems != null
				&& withoutDeclItems.Count == 2
				&& string.Equals(withoutDeclItems[0].DocumentKey, docA.Value, StringComparison.OrdinalIgnoreCase)
				&& ((withoutDeclItems[0].SourcePayload as SymbolDataFactPayload)?.StartLine ?? -1) == 3
				&& string.Equals(withoutDeclItems[1].DocumentKey, docB.Value, StringComparison.OrdinalIgnoreCase)
				&& ((withoutDeclItems[1].SourcePayload as SymbolDataFactPayload)?.StartLine ?? -1) == 1;

			Assert(withoutDeclSorted, "DBQ-08D: excludeDeclaration references are stable-ordered by doc/line");
		}

		// ================================================================
		// DBQ-09: Completion dispatches on context (member access / type / include / identifier)
		// ================================================================
		{
			var doc = new PathKey("file:///query/completion-ctx.ffs");

			// Document text shaped so offsets align with line structure:
			string docText = string.Join("\n", new[]
			{
				"struct Player {",          // L0
				"  hp: int;",                // L1
				"  name: string;",           // L2
				"}",                         // L3
				"enum Color { Red, Blue }", // L4
				"var gPlayer: Player;",     // L5
				"",                          // L6
				"func entry() {",           // L7
				"  var localP: Player;",    // L8
				"  localP.",                 // L9  -> test member access here
				"}",                         // L10
				"",                          // L11
				"func other() {",           // L12
				"  var temp: int;",         // L13
				"  ",                        // L14  -> identifier-context here
				"}",                         // L15
			});

			// Helper to find a token's start offset in docText
			int OffsetOf(string token) => docText.IndexOf(token, StringComparison.Ordinal);

			// Build symbols whose DeclarationSpan.Start matches the real offset in docText,
			// so that ResolveContainingFunction(offset-based) works correctly.
			var structPlayer = new SymbolIdentity(SymbolKindTag.Struct, "Player", string.Empty, string.Empty, doc.Value, new TextSpan(OffsetOf("struct Player"), 6));
			var fieldHp = new SymbolIdentity(SymbolKindTag.StructField, "hp", string.Empty, "Player", doc.Value, new TextSpan(OffsetOf("hp:"), 2), null, "int");
			var fieldName = new SymbolIdentity(SymbolKindTag.StructField, "name", string.Empty, "Player", doc.Value, new TextSpan(OffsetOf("name:"), 4), null, "string");
			var enumColor = new SymbolIdentity(SymbolKindTag.Enum, "Color", string.Empty, string.Empty, doc.Value, new TextSpan(OffsetOf("enum Color"), 5));
			var enumRed = new SymbolIdentity(SymbolKindTag.EnumMember, "Red", string.Empty, "Color", doc.Value, new TextSpan(OffsetOf("Red,"), 3));
			var enumBlue = new SymbolIdentity(SymbolKindTag.EnumMember, "Blue", string.Empty, "Color", doc.Value, new TextSpan(OffsetOf("Blue }"), 4));
			var moduleVar = new SymbolIdentity(SymbolKindTag.Variable, "gPlayer", string.Empty, string.Empty, doc.Value, new TextSpan(OffsetOf("gPlayer:"), 7), null, "Player");
			var funcEntry = new SymbolIdentity(SymbolKindTag.Function, "entry", string.Empty, string.Empty, doc.Value, new TextSpan(OffsetOf("func entry"), 5));
			var localP = new SymbolIdentity(SymbolKindTag.Variable, "localP", "entry", "entry", doc.Value, new TextSpan(OffsetOf("localP:"), 6), null, "Player");
			var funcOther = new SymbolIdentity(SymbolKindTag.Function, "other", string.Empty, string.Empty, doc.Value, new TextSpan(OffsetOf("func other"), 5));
			var otherTemp = new SymbolIdentity(SymbolKindTag.Variable, "temp", "other", "other", doc.Value, new TextSpan(OffsetOf("temp:"), 4), null, "int");

			var allSymbols = new List<SymbolIdentity>
			{
				structPlayer, fieldHp, fieldName, enumColor, enumRed, enumBlue,
				moduleVar, funcEntry, localP, funcOther, otherTemp,
			};

			var index = BuildIndex(
				tuple: (doc, new TextPosition(0, 0), structPlayer),
				definition: (structPlayer, null),
				referencesIncludeDecl: new List<DataFact>(),
				referencesExcludeDecl: new List<DataFact>(),
				nameSymbols: allSymbols);

			CodeDatabaseSnapshot snapshot = BuildSnapshot(index);

			// --- DBQ-09A: Member access on local variable typed Player -> StructFields ---
			{
				var req = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(9, 9),
					new TextSpan(0, 0), false, string.Empty, docText);
				SymbolQueryResult r = facade.QueryCompletion(snapshot, req);
				var items = r != null && r.Payload != null ? r.Payload.CompletionItems : null;
				bool hasHp = false, hasName = false, hasLocalP = false, hasKeyword = false;
				if (items != null)
				{
					for (int i = 0; i < items.Count; i++)
					{
						if (items[i].Label == "hp") hasHp = true;
						if (items[i].Label == "name") hasName = true;
						if (items[i].Label == "localP") hasLocalP = true;
						if (items[i].Label == "func") hasKeyword = true;
					}
				}
				Assert(hasHp && hasName, "DBQ-09A: member access on Player returns its fields");
				Assert(!hasLocalP && !hasKeyword, "DBQ-09A2: member access excludes non-members");
			}

			// --- DBQ-09B: Identifier context in `other()` excludes locals of `entry()` ---
			{
				var req = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(14, 2),
					new TextSpan(0, 0), false, string.Empty, docText);
				SymbolQueryResult r = facade.QueryCompletion(snapshot, req);
				var items = r != null && r.Payload != null ? r.Payload.CompletionItems : null;
				bool hasLocalP = false, hasTemp = false, hasEntry = false, hasKeyword = false;
				if (items != null)
				{
					for (int i = 0; i < items.Count; i++)
					{
						if (items[i].Label == "localP") hasLocalP = true;
						if (items[i].Label == "temp") hasTemp = true;
						if (items[i].Label == "entry") hasEntry = true;
						if (items[i].Label == "return") hasKeyword = true;
					}
				}
				Assert(!hasLocalP, "DBQ-09B: locals of another function are not suggested");
				Assert(hasTemp, "DBQ-09B2: locals of current function are suggested");
				Assert(hasEntry, "DBQ-09B3: peer functions are suggested");
				Assert(hasKeyword, "DBQ-09B4: keywords appear in identifier context");
			}

			// --- DBQ-09C: Member access on enum name returns enum members ---
			{
				// Place cursor after "Color." on an ad-hoc line — inject into docText
				string docText2 = docText + "\nvar c = Color.";
				int line = docText2.Split('\n').Length - 1;
				int col = "var c = Color.".Length;
				var req = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(line, col),
					new TextSpan(0, 0), false, string.Empty, docText2);
				SymbolQueryResult r = facade.QueryCompletion(snapshot, req);
				var items = r != null && r.Payload != null ? r.Payload.CompletionItems : null;
				bool hasRed = false, hasBlue = false;
				if (items != null)
				{
					for (int i = 0; i < items.Count; i++)
					{
						if (items[i].Label == "Red") hasRed = true;
						if (items[i].Label == "Blue") hasBlue = true;
					}
				}
				Assert(hasRed && hasBlue, "DBQ-09C: member access on enum returns enum members");
			}

			// --- DBQ-09D: Type annotation context prefers Structs + builtin types ---
			{
				string docText3 = "var x: ";
				var req = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(0, docText3.Length),
					new TextSpan(0, 0), false, string.Empty, docText3);
				SymbolQueryResult r = facade.QueryCompletion(snapshot, req);
				var items = r != null && r.Payload != null ? r.Payload.CompletionItems : null;
				bool hasPlayer = false, hasInt = false, hasFunc = false;
				if (items != null)
				{
					for (int i = 0; i < items.Count; i++)
					{
						if (items[i].Label == "Player") hasPlayer = true;
						if (items[i].Label == "int") hasInt = true;
						if (items[i].Label == "func") hasFunc = true;
					}
				}
				Assert(hasPlayer, "DBQ-09D: type annotation offers struct types");
				Assert(hasInt, "DBQ-09D2: type annotation offers builtin types");
				Assert(!hasFunc, "DBQ-09D3: type annotation excludes keywords/functions");
			}

			// --- DBQ-09E: Inside comments/strings returns empty ---
			{
				string docText4 = "// this is a ";
				var reqComment = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(0, docText4.Length),
					new TextSpan(0, 0), false, string.Empty, docText4);
				SymbolQueryResult rc = facade.QueryCompletion(snapshot, reqComment);
				var ci = rc != null && rc.Payload != null ? rc.Payload.CompletionItems : null;
				Assert(ci != null && ci.Count == 0, "DBQ-09E: completion inside a comment is suppressed");

				string docText5 = "var s = \"hello ";
				var reqString = new SymbolQueryRequest(
					"completion", doc.Value, new TextPosition(0, docText5.Length),
					new TextSpan(0, 0), false, string.Empty, docText5);
				SymbolQueryResult rs = facade.QueryCompletion(snapshot, reqString);
				var si = rs != null && rs.Payload != null ? rs.Payload.CompletionItems : null;
				Assert(si != null && si.Count == 0, "DBQ-09E2: completion inside a string is suppressed");
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
			AliasIndex = new EmptyAliasIndex();
		}

		public long SnapshotVersion { get; }
		public IPositionIndex PositionIndex { get; }
		public ISymbolIndex SymbolIndex { get; }
		public IIncludeGraphIndex IncludeGraphIndex { get; }
		public INameIndex NameIndex { get; }
		public IAliasIndex AliasIndex { get; }
	}

	private sealed class EmptyAliasIndex : IAliasIndex
	{
		private static readonly IReadOnlyDictionary<string, PathKey> Empty =
			new Dictionary<string, PathKey>(0);

		public bool TryResolveAlias(PathKey documentKey, string aliasName, out PathKey targetDocument)
		{
			targetDocument = new PathKey(string.Empty);
			return false;
		}

		public IReadOnlyDictionary<string, PathKey> GetAliases(PathKey documentKey)
		{
			return Empty;
		}
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
