// Responsibility:
//   In-memory executable query facade over immutable database snapshots.
// Owns:
//   Deterministic read-side query flow for definition/references/hover/completion/signature/rename.
// Inputs/Outputs:
//   In: CodeDatabaseSnapshot + SymbolQueryRequest.
//   Out: SymbolQueryResult or lightweight query payload objects.
// Allowed Dependencies:
//   - Database contracts/models/index views.
// Forbidden Dependencies:
//   - Snapshot mutation and protocol stream writes.
// Invariants:
//   - Query operations are side-effect free.
//   - Null/invalid inputs always yield explicit failure or not-found results.
// Boundary Closure:
//   Upstream: VSCode bridge adapters and handler facades.
//   Downstream: index snapshot readers.

using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;
using FFVM.Debug.Lsp.Database.Query;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryLspQueryFacade : ILspQueryFacade
	{
		private const int CompletionLimit = 128;
		private const int DocumentSymbolLimit = 512;
		private const int SemanticTokenTypeNamespace = 0;
		private const int SemanticTokenTypeStruct = 1;
		private const int SemanticTokenTypeEnum = 2;
		private const int SemanticTokenTypeFunction = 3;
		private const int SemanticTokenTypeVariable = 4;
		private const int SemanticTokenTypeParameter = 5;
		private const int SemanticTokenTypeProperty = 6;
		private const int SemanticTokenTypeEnumMember = 7;
		private const int SemanticTokenTypeKeyword = 8;
		private const int SemanticTokenTypeUnknown = -1;

		public SymbolQueryResult QueryDefinition(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			if (index.SymbolIndex != null && index.SymbolIndex.TryGetDefinition(symbol, out DataFact definitionFact) && definitionFact != null)
			{
				var ranges = new List<TextSpan> { definitionFact.Span };
				var payload = new LspDefinitionPayload(NormalizeDocumentKey(definitionFact.DocumentKey.Value), definitionFact.Span, definitionFact.Payload);
				return SymbolQueryResult.Success(symbol, ranges, SymbolQueryPayload.ForDefinition(payload));
			}

			if (symbol.DeclarationSpan.Length > 0)
			{
				var ranges = new List<TextSpan> { symbol.DeclarationSpan };
				var payload = new LspDefinitionPayload(NormalizeDocumentKey(symbol.Origin), symbol.DeclarationSpan, null);
				return SymbolQueryResult.Success(symbol, ranges, SymbolQueryPayload.ForDefinition(payload));
			}

			return SymbolQueryResult.NotFound("Definition not found for resolved symbol.");
		}

		public SymbolQueryResult QueryReferences(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			if (index.SymbolIndex == null)
				return SymbolQueryResult.Failure("SymbolIndex is not available for references query.");

			IReadOnlyList<DataFact> references = index.SymbolIndex.GetReferences(symbol, request != null && request.IncludeDeclaration);
			List<DataFact> orderedReferences = NormalizeReferences(references);
			if (orderedReferences.Count == 0)
				return SymbolQueryResult.NotFound("No references found for resolved symbol.");

			var ranges = new List<TextSpan>(orderedReferences.Count);
			var payload = new List<LspReferenceItem>(orderedReferences.Count);
			for (int i = 0; i < orderedReferences.Count; i++)
			{
				DataFact fact = orderedReferences[i];
				ranges.Add(fact.Span);
				payload.Add(new LspReferenceItem(NormalizeDocumentKey(fact.DocumentKey.Value), fact.Span, fact.Payload));
			}

			return SymbolQueryResult.Success(symbol, ranges, SymbolQueryPayload.ForReferences(payload));
		}

		public SymbolQueryResult QueryHover(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			// Use pre-built documentation from the definition symbol when available
			string hoverContent;
			if (!string.IsNullOrWhiteSpace(symbol.Documentation))
			{
				hoverContent = symbol.Documentation;
			}
			else if (index.SymbolIndex != null
				&& index.SymbolIndex.TryGetDefinition(symbol, out DataFact defFact)
				&& defFact != null
				&& defFact.Payload is SymbolDataFactPayload defPayload
				&& defPayload.Symbol != null
				&& !string.IsNullOrWhiteSpace(defPayload.Symbol.Documentation))
			{
				hoverContent = defPayload.Symbol.Documentation;
			}
			else
			{
				hoverContent = symbol.Kind + ": " + symbol.Name;
			}
			var payload = new LspHoverPayload(hoverContent, symbol.Scope, symbol.ParentName, symbol.Origin);

			var ranges = symbol.DeclarationSpan.Length > 0
				? new List<TextSpan> { symbol.DeclarationSpan }
				: new List<TextSpan>(0);

			return SymbolQueryResult.Success(symbol, ranges, SymbolQueryPayload.ForHover(payload));
		}

		public SymbolQueryResult QueryCompletion(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (index.NameIndex == null)
				return SymbolQueryResult.Failure("NameIndex is not available for completion query.");

			// Detect completion context from document text + cursor
			CompletionContext context = request != null
				? CompletionContextDetector.Detect(request.DocumentText, request.Position.Line, request.Position.Character)
				: new CompletionContext(CompletionContextKind.Identifier, null, string.Empty, 0);

			string normalizedDocKey = request != null ? NormalizeDocumentKey(request.DocumentKey) : string.Empty;

			// Suppress completion inside comments/strings (except include paths handled separately).
			if (context.Kind == CompletionContextKind.InsideComment
				|| context.Kind == CompletionContextKind.InsideString)
			{
				return SymbolQueryResult.Success(
					SymbolIdentity.CreateUnknown("completion"),
					null,
					SymbolQueryPayload.ForCompletion(new List<LspCompletionItem>(0)));
			}

			// Snapshot of all indexed symbols (used by several branches).
			IReadOnlyList<SymbolIdentity> allSymbols = index.NameIndex.Search(string.Empty, CompletionLimit * 4);
			if (allSymbols == null) allSymbols = new List<SymbolIdentity>(0);

			// Dispatch by context kind.
			switch (context.Kind)
			{
				case CompletionContextKind.MemberAccess:
					return CompletionMemberAccess(index, request, context, allSymbols, normalizedDocKey);
				case CompletionContextKind.IncludePath:
					return CompletionIncludePath(allSymbols, context, normalizedDocKey);
				case CompletionContextKind.TypeAnnotation:
				case CompletionContextKind.NewExpression:
					return CompletionTypeContext(allSymbols, context, normalizedDocKey);
				case CompletionContextKind.Identifier:
				default:
					return CompletionIdentifier(allSymbols, context, normalizedDocKey, request);
			}
		}

		// ---- Completion branch: plain identifier (default) ----
		private SymbolQueryResult CompletionIdentifier(
			IReadOnlyList<SymbolIdentity> allSymbols,
			CompletionContext context,
			string normalizedDocKey,
			SymbolQueryRequest request)
		{
			string containingFunc = ResolveContainingFunction(allSymbols, normalizedDocKey,
				request != null ? request.Position.Line : 0,
				request != null ? request.DocumentText : string.Empty);
			var items = new List<LspCompletionItem>();

			for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
			{
				SymbolIdentity c = allSymbols[i];
				if (c == null) continue;
				// Skip nested/sub-symbols offered via dot triggers.
				if (c.Kind == SymbolKindTag.StructField) continue;
				if (c.Kind == SymbolKindTag.EnumMember) continue;
				// Scope isolation: locals/params only in their containing function
				if ((c.Kind == SymbolKindTag.Variable || c.Kind == SymbolKindTag.Parameter)
					&& !string.IsNullOrEmpty(c.ParentName))
				{
					if (!string.Equals(c.ParentName, containingFunc, System.StringComparison.Ordinal))
						continue;
				}
				// Private visibility: hide private symbols from other files
				if (c.IsPrivate && !IsSameDocument(c.Origin, normalizedDocKey))
					continue;
				items.Add(new LspCompletionItem(c.Name, c.Kind.ToString(), BuildCompletionDetail(c), c.Documentation));
			}

			// Append keywords (always useful)
			foreach (string kw in CompletionKeywords)
				items.Add(new LspCompletionItem(kw, "Keyword", "keyword", null));

			SymbolIdentity anchor = items.Count > 0
				? SymbolIdentity.CreateUnknown(context.Prefix ?? "completion")
				: SymbolIdentity.CreateUnknown("completion");
			return SymbolQueryResult.Success(anchor, null, SymbolQueryPayload.ForCompletion(items));
		}

		// ---- Completion branch: type annotation / new expression ----
		private SymbolQueryResult CompletionTypeContext(
			IReadOnlyList<SymbolIdentity> allSymbols,
			CompletionContext context,
			string normalizedDocKey)
		{
			var items = new List<LspCompletionItem>();
			for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
			{
				SymbolIdentity c = allSymbols[i];
				if (c == null) continue;
				// Only types
				bool isType = c.Kind == SymbolKindTag.Struct
					|| (context.Kind == CompletionContextKind.TypeAnnotation && c.Kind == SymbolKindTag.Enum);
				if (!isType) continue;
				if (c.IsPrivate && !IsSameDocument(c.Origin, normalizedDocKey)) continue;
				items.Add(new LspCompletionItem(c.Name, c.Kind.ToString(), BuildCompletionDetail(c), c.Documentation));
			}
			// Builtin primitives as keywords in TypeAnnotation context
			if (context.Kind == CompletionContextKind.TypeAnnotation)
			{
				foreach (string bt in BuiltinTypeKeywords)
					items.Add(new LspCompletionItem(bt, "Keyword", "builtin type", null));
			}
			return SymbolQueryResult.Success(
				SymbolIdentity.CreateUnknown(context.Prefix ?? "completion"),
				null,
				SymbolQueryPayload.ForCompletion(items));
		}

		// ---- Completion branch: member access (dot trigger) ----
		private SymbolQueryResult CompletionMemberAccess(
			IIndexSnapshot index,
			SymbolQueryRequest request,
			CompletionContext context,
			IReadOnlyList<SymbolIdentity> allSymbols,
			string normalizedDocKey)
		{
			var items = new List<LspCompletionItem>();
			if (context.ReceiverChain == null || context.ReceiverChain.Count == 0)
				return SymbolQueryResult.Success(
					SymbolIdentity.CreateUnknown("completion"), null,
					SymbolQueryPayload.ForCompletion(items));

			string first = context.ReceiverChain[0];

			// Alias path: if first segment resolves as an alias, return symbols scoped to target doc.
			if (index.AliasIndex != null
				&& !string.IsNullOrEmpty(normalizedDocKey)
				&& index.AliasIndex.TryResolveAlias(new PathKey(normalizedDocKey), first, out PathKey targetDoc)
				&& context.ReceiverChain.Count == 1)
			{
				string targetPath = NormalizeDocumentKey(targetDoc.Value ?? string.Empty);
				for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
				{
					SymbolIdentity c = allSymbols[i];
					if (c == null) continue;
					if (c.Kind == SymbolKindTag.Parameter) continue;
					if (c.Kind == SymbolKindTag.Variable && !string.IsNullOrEmpty(c.ParentName)) continue; // skip locals
					if (c.Kind == SymbolKindTag.StructField) continue;
					if (c.Kind == SymbolKindTag.EnumMember) continue;
					if (!string.Equals(NormalizeDocumentKey(c.Origin), targetPath, System.StringComparison.OrdinalIgnoreCase))
						continue;
					if (c.IsPrivate) continue;
					items.Add(new LspCompletionItem(first + "." + c.Name, c.Kind.ToString(),
						BuildCompletionDetail(c), c.Documentation));
				}
				return SymbolQueryResult.Success(
					SymbolIdentity.CreateUnknown(first), null,
					SymbolQueryPayload.ForCompletion(items));
			}

			// Enum-qualified access: first segment is an Enum name → return its members
			if (IsEnumName(allSymbols, first))
			{
				for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
				{
					SymbolIdentity c = allSymbols[i];
					if (c == null) continue;
					if (c.Kind != SymbolKindTag.EnumMember) continue;
					if (!string.Equals(c.ParentName, first, System.StringComparison.Ordinal)) continue;
					items.Add(new LspCompletionItem(c.Name, c.Kind.ToString(), BuildCompletionDetail(c), c.Documentation));
				}
				return SymbolQueryResult.Success(
					SymbolIdentity.CreateUnknown(first), null,
					SymbolQueryPayload.ForCompletion(items));
			}

			// Struct member access: resolve receiver chain to a struct type
			string containingFunc = ResolveContainingFunction(allSymbols, normalizedDocKey,
				request != null ? request.Position.Line : 0,
				request != null ? request.DocumentText : string.Empty);
			string currentType = ResolveChainBaseType(allSymbols, first, containingFunc);
			for (int k = 1; k < context.ReceiverChain.Count; k++)
			{
				if (string.IsNullOrEmpty(currentType)) break;
				string seg = context.ReceiverChain[k];
				currentType = ResolveFieldType(allSymbols, currentType, seg);
			}
			if (string.IsNullOrEmpty(currentType))
				return SymbolQueryResult.Success(
					SymbolIdentity.CreateUnknown("completion"), null,
					SymbolQueryPayload.ForCompletion(items));

			// If final type is an Enum, return its members
			if (IsEnumName(allSymbols, currentType))
			{
				for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
				{
					SymbolIdentity c = allSymbols[i];
					if (c == null) continue;
					if (c.Kind != SymbolKindTag.EnumMember) continue;
					if (!string.Equals(c.ParentName, currentType, System.StringComparison.Ordinal)) continue;
					items.Add(new LspCompletionItem(c.Name, c.Kind.ToString(), BuildCompletionDetail(c), c.Documentation));
				}
			}
			else
			{
				// Struct fields
				for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
				{
					SymbolIdentity c = allSymbols[i];
					if (c == null) continue;
					if (c.Kind != SymbolKindTag.StructField) continue;
					if (!string.Equals(c.ParentName, currentType, System.StringComparison.Ordinal)) continue;
					items.Add(new LspCompletionItem(c.Name, c.Kind.ToString(), BuildSymbolDetail(c), c.Documentation));
				}
			}

			return SymbolQueryResult.Success(
				SymbolIdentity.CreateUnknown(currentType), null,
				SymbolQueryPayload.ForCompletion(items));
		}

		// ---- Completion branch: include path (workspace file enumeration) ----
		private SymbolQueryResult CompletionIncludePath(
			IReadOnlyList<SymbolIdentity> allSymbols,
			CompletionContext context,
			string normalizedDocKey)
		{
			// Collect unique Origins from all indexed symbols — these are the known .ffs files.
			var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
			var items = new List<LspCompletionItem>();
			for (int i = 0; i < allSymbols.Count && items.Count < CompletionLimit; i++)
			{
				SymbolIdentity c = allSymbols[i];
				if (c == null || string.IsNullOrWhiteSpace(c.Origin)) continue;
				string origin = c.Origin;
				if (!seen.Add(origin)) continue;
				// Skip the current document itself
				if (IsSameDocument(origin, normalizedDocKey)) continue;
				string label = ExtractFileRelativeLabel(origin);
				if (string.IsNullOrEmpty(label)) continue;
				items.Add(new LspCompletionItem(label, "File", origin, null));
			}
			return SymbolQueryResult.Success(
				SymbolIdentity.CreateUnknown("include"), null,
				SymbolQueryPayload.ForCompletion(items));
		}

		public SymbolQueryResult QuerySignatureHelp(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			if (symbol.Kind != SymbolKindTag.Function)
			{
				return SymbolQueryResult.NotFound("Resolved symbol is not a callable function.");
			}

			var signatures = new List<LspSignatureItem>
			{
				new LspSignatureItem(symbol.Name + "(...)", symbol.Origin)
			};

			var payload = new LspSignatureHelpPayload(signatures, 0, 0);
			return SymbolQueryResult.Success(symbol, null, SymbolQueryPayload.ForSignatureHelp(payload));
		}

		public SymbolQueryResult QueryPrepareRename(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			if (!IsRenameable(symbol.Kind))
				return SymbolQueryResult.Failure("Resolved symbol kind is not renameable.");

			TextSpan range = symbol.DeclarationSpan.Length > 0
				? symbol.DeclarationSpan
				: new TextSpan(0, 0);

			var payload = new LspPrepareRenamePayload(range, symbol.Name);
			return SymbolQueryResult.Success(symbol, new List<TextSpan> { range }, SymbolQueryPayload.ForPrepareRename(payload));
		}

		public SymbolQueryResult QueryRename(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (request == null || string.IsNullOrWhiteSpace(request.NewName))
				return SymbolQueryResult.Failure("Rename requires a non-empty new name.");

			SymbolQueryResult prepare = QueryPrepareRename(snapshot, request);
			if (!prepare.Succeeded)
				return prepare;

			var referencesRequest = new SymbolQueryRequest(
				request.Operation,
				request.DocumentKey,
				request.Position,
				request.Selection,
				true,
				request.NewName);

			SymbolQueryResult references = QueryReferences(snapshot, referencesRequest);
			if (!references.Succeeded)
				return references;

			var edits = new List<LspRenameEdit>();
			IReadOnlyList<LspReferenceItem> typedItems = references.Payload != null
				? references.Payload.References
				: null;
			if (typedItems != null)
			{
				for (int i = 0; i < typedItems.Count; i++)
				{
					LspReferenceItem item = typedItems[i];
					edits.Add(new LspRenameEdit(item.DocumentKey, item.Span, request.NewName));
				}
			}

			var payload = new LspRenamePayload(request.NewName, edits);
			return SymbolQueryResult.Success(references.Symbol, references.Ranges, SymbolQueryPayload.ForRename(payload));
		}

		public IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbols(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out _))
				return new List<LspDocumentSymbolItem>(0);

			if (index.NameIndex == null)
				return new List<LspDocumentSymbolItem>(0);

			IReadOnlyList<SymbolIdentity> symbols = index.NameIndex.Search(string.Empty, DocumentSymbolLimit);
			if (symbols == null)
				return new List<LspDocumentSymbolItem>(0);

			var output = new List<LspDocumentSymbolItem>(symbols.Count);
			for (int i = 0; i < symbols.Count; i++)
			{
				SymbolIdentity symbol = symbols[i];
				output.Add(new LspDocumentSymbolItem(
					symbol.Name,
					symbol.Kind.ToString(),
					symbol.Scope,
					symbol.ParentName,
					symbol.Origin,
					symbol.DeclarationSpan));
			}

			return output;
		}

		public LspSemanticTokensPayload QuerySemanticTokensFull(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (request == null || string.IsNullOrWhiteSpace(request.DocumentKey))
			{
				return new LspSemanticTokensPayload(
					new List<int>(0),
					"DocumentKey is required for semanticTokens/full query.");
			}

			string normalizedDocumentKey = NormalizeDocumentKey(request.DocumentKey);
			if (string.IsNullOrWhiteSpace(normalizedDocumentKey))
			{
				return new LspSemanticTokensPayload(
					new List<int>(0),
					"DocumentKey is required for semanticTokens/full query.");
			}

			if (snapshot == null)
			{
				return new LspSemanticTokensPayload(
					new List<int>(0),
					"Snapshot is required for semanticTokens/full query.");
			}

			List<SemanticTokenAbsolute> absoluteTokens = CollectSemanticTokens(snapshot, normalizedDocumentKey);
			if (absoluteTokens.Count == 0)
			{
				return new LspSemanticTokensPayload(
					new List<int>(0),
					"No semantic token facts available for requested document.");
			}

			absoluteTokens.Sort(CompareSemanticTokenAbsolute);

			var encoded = new List<int>(absoluteTokens.Count * 5);
			int previousLine = 0;
			int previousStart = 0;
			bool hasPrevious = false;

			for (int i = 0; i < absoluteTokens.Count; i++)
			{
				SemanticTokenAbsolute token = absoluteTokens[i];
				if (token.Length <= 0 || token.Line < 0 || token.Start < 0)
					continue;

				int deltaLine = hasPrevious ? token.Line - previousLine : token.Line;
				if (deltaLine < 0)
					continue;

				int deltaStart = !hasPrevious || deltaLine != 0
					? token.Start
					: token.Start - previousStart;

				if (deltaStart < 0)
					continue;

				encoded.Add(deltaLine);
				encoded.Add(deltaStart);
				encoded.Add(token.Length);
				encoded.Add(token.TokenType);
				encoded.Add(token.TokenModifiers);

				previousLine = token.Line;
				previousStart = token.Start;
				hasPrevious = true;
			}

			if (encoded.Count == 0)
			{
				return new LspSemanticTokensPayload(
					encoded,
					"No valid semantic tokens were emitted after normalization.");
			}

			return new LspSemanticTokensPayload(encoded, string.Empty);
		}

		private static bool TryGetIndex(CodeDatabaseSnapshot snapshot, out IIndexSnapshot index, out SymbolQueryResult error)
		{
			index = snapshot != null ? snapshot.IndexSnapshot : null;
			if (snapshot == null)
			{
				error = SymbolQueryResult.Failure("Snapshot is required.");
				return false;
			}

			if (index == null)
			{
				error = SymbolQueryResult.Failure("Index snapshot is required.");
				return false;
			}

			error = null;
			return true;
		}

		private static bool TryResolveSymbol(
			IIndexSnapshot index,
			SymbolQueryRequest request,
			out SymbolIdentity symbol,
			out SymbolQueryResult error)
		{
			symbol = null;

			if (request == null)
			{
				error = SymbolQueryResult.Failure("Query request is required.");
				return false;
			}

			if (index == null || index.PositionIndex == null)
			{
				error = SymbolQueryResult.Failure("PositionIndex is not available for symbol resolution.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(request.DocumentKey))
			{
				error = SymbolQueryResult.Failure("DocumentKey is required for symbol resolution.");
				return false;
			}

			string normalizedDocumentKey = NormalizeDocumentKey(request.DocumentKey);
			if (string.IsNullOrWhiteSpace(normalizedDocumentKey))
			{
				error = SymbolQueryResult.Failure("DocumentKey is required for symbol resolution.");
				return false;
			}

			if (!index.PositionIndex.TryResolveSymbol(new PathKey(normalizedDocumentKey), request.Position, out symbol) || symbol == null)
			{
				error = SymbolQueryResult.NotFound("No symbol resolved at requested position.");
				return false;
			}

			error = null;
			return true;
		}

		private static bool IsRenameable(SymbolKindTag kind)
		{
			return kind == SymbolKindTag.Function
				|| kind == SymbolKindTag.Variable
				|| kind == SymbolKindTag.Struct
				|| kind == SymbolKindTag.Parameter
				|| kind == SymbolKindTag.Enum
				|| kind == SymbolKindTag.StructField
				|| kind == SymbolKindTag.EnumMember;
		}

		private static string BuildSymbolDetail(SymbolIdentity symbol)
		{
			if (symbol == null)
				return string.Empty;

			if (!string.IsNullOrWhiteSpace(symbol.Scope))
				return symbol.Kind + " in " + symbol.Scope;

			if (!string.IsNullOrWhiteSpace(symbol.ParentName))
				return symbol.Kind + " member of " + symbol.ParentName;

			return symbol.Kind.ToString();
		}

		private static string BuildCompletionDetail(SymbolIdentity symbol)
		{
			if (symbol == null)
				return string.Empty;

			string docLine = ExtractDocSummaryLine(symbol.Documentation);
			if (!string.IsNullOrWhiteSpace(docLine))
				return docLine;

			return BuildSymbolDetail(symbol);
		}

		private static string ExtractDocSummaryLine(string documentation)
		{
			if (string.IsNullOrWhiteSpace(documentation))
				return string.Empty;

			if (documentation.StartsWith("```", StringComparison.Ordinal))
			{
				int sep = documentation.IndexOf("\n\n---\n\n", StringComparison.Ordinal);
				if (sep >= 0)
				{
					string tail = documentation.Substring(sep + "\n\n---\n\n".Length).Trim();
					int end = tail.IndexOf('\n');
					return end >= 0 ? tail.Substring(0, end).Trim() : tail;
				}
				return string.Empty;
			}

			string trimmed = documentation.Trim();
			int lineEnd = trimmed.IndexOf('\n');
			return lineEnd >= 0 ? trimmed.Substring(0, lineEnd).Trim() : trimmed;
		}

		// ---------- Completion helpers ----------

		private static readonly string[] CompletionKeywords = new[]
		{
			"func", "var", "const", "if", "else", "while", "for", "return",
			"wait", "wait_for", "yield", "defer", "using",
			"true", "false", "null",
			"struct", "include", "enum",
			"public", "private", "override", "external", "new"
		};

		private static readonly string[] BuiltinTypeKeywords = new[]
		{
			"int", "bool", "float", "string"
		};

		private static bool IsSameDocument(string a, string b)
		{
			return string.Equals(NormalizeDocumentKey(a), NormalizeDocumentKey(b), System.StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsEnumName(IReadOnlyList<SymbolIdentity> symbols, string name)
		{
			if (string.IsNullOrEmpty(name) || symbols == null) return false;
			for (int i = 0; i < symbols.Count; i++)
			{
				SymbolIdentity s = symbols[i];
				if (s == null) continue;
				if (s.Kind == SymbolKindTag.Enum && string.Equals(s.Name, name, System.StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		// Finds the function whose body contains the cursor in the current document.
		// Heuristic: among Function symbols in this document sorted by DeclarationSpan.Start,
		// pick the last one whose start offset <= cursor offset. Works when functions are sequential.
		private static string ResolveContainingFunction(IReadOnlyList<SymbolIdentity> symbols, string normalizedDocKey, int cursorLine0, string documentText)
		{
			if (symbols == null || string.IsNullOrEmpty(normalizedDocKey)) return string.Empty;
			int cursorOffset = LineToOffset(documentText, cursorLine0);
			string best = string.Empty;
			int bestStart = -1;
			for (int i = 0; i < symbols.Count; i++)
			{
				SymbolIdentity s = symbols[i];
				if (s == null || s.Kind != SymbolKindTag.Function) continue;
				if (!IsSameDocument(s.Origin, normalizedDocKey)) continue;
				int startOffset = s.DeclarationSpan.Start;
				if (startOffset <= cursorOffset && startOffset > bestStart)
				{
					bestStart = startOffset;
					best = s.Name;
				}
			}
			return best;
		}

		private static int LineToOffset(string text, int line0)
		{
			if (string.IsNullOrEmpty(text) || line0 <= 0) return 0;
			int offset = 0;
			int currentLine = 0;
			while (offset < text.Length && currentLine < line0)
			{
				int nl = text.IndexOf('\n', offset);
				if (nl < 0) return text.Length;
				offset = nl + 1;
				currentLine++;
			}
			return offset;
		}

		// Look up the base type for the first segment of a receiver chain:
		// - Parameter or Variable in the containing function (scope match)
		// - Module Variable (no parent)
		// - Struct/Enum type name directly (for Enum. or new Struct{} contexts)
		private static string ResolveChainBaseType(IReadOnlyList<SymbolIdentity> symbols, string firstName, string containingFunc)
		{
			if (symbols == null || string.IsNullOrEmpty(firstName)) return string.Empty;
			SymbolIdentity moduleVar = null;
			SymbolIdentity typeDecl = null;
			for (int i = 0; i < symbols.Count; i++)
			{
				SymbolIdentity s = symbols[i];
				if (s == null || !string.Equals(s.Name, firstName, System.StringComparison.Ordinal)) continue;
				if ((s.Kind == SymbolKindTag.Parameter || s.Kind == SymbolKindTag.Variable)
					&& !string.IsNullOrEmpty(s.ParentName)
					&& string.Equals(s.ParentName, containingFunc, System.StringComparison.Ordinal))
				{
					if (!string.IsNullOrEmpty(s.TypeName)) return s.TypeName;
				}
				if (s.Kind == SymbolKindTag.Variable && string.IsNullOrEmpty(s.ParentName))
					moduleVar = s;
				if (s.Kind == SymbolKindTag.Struct || s.Kind == SymbolKindTag.Enum)
					typeDecl = s;
			}
			if (moduleVar != null && !string.IsNullOrEmpty(moduleVar.TypeName)) return moduleVar.TypeName;
			if (typeDecl != null) return typeDecl.Name;
			return string.Empty;
		}

		// For a given struct name and field name, return the field's declared type.
		private static string ResolveFieldType(IReadOnlyList<SymbolIdentity> symbols, string structName, string fieldName)
		{
			if (symbols == null || string.IsNullOrEmpty(structName) || string.IsNullOrEmpty(fieldName))
				return string.Empty;
			for (int i = 0; i < symbols.Count; i++)
			{
				SymbolIdentity s = symbols[i];
				if (s == null || s.Kind != SymbolKindTag.StructField) continue;
				if (!string.Equals(s.ParentName, structName, System.StringComparison.Ordinal)) continue;
				if (!string.Equals(s.Name, fieldName, System.StringComparison.Ordinal)) continue;
				return s.TypeName ?? string.Empty;
			}
			return string.Empty;
		}

		// Given a full URI like "file:///.../KOF98/Scripts/foo.ffs", return a repo-relative label "foo.ffs".
		private static string ExtractFileRelativeLabel(string origin)
		{
			if (string.IsNullOrEmpty(origin)) return string.Empty;
			int slash = origin.LastIndexOf('/');
			if (slash < 0) slash = origin.LastIndexOf('\\');
			string tail = slash >= 0 && slash < origin.Length - 1 ? origin.Substring(slash + 1) : origin;
			return tail;
		}

		private static List<SemanticTokenAbsolute> CollectSemanticTokens(CodeDatabaseSnapshot snapshot, string documentKey)
		{
			if (snapshot == null || snapshot.Facts == null || snapshot.Facts.Count == 0)
				return new List<SemanticTokenAbsolute>(0);

			var tokens = new List<SemanticTokenAbsolute>();
			var dedupe = new HashSet<string>(StringComparer.Ordinal);

			for (int i = 0; i < snapshot.Facts.Count; i++)
			{
				DataFact fact = snapshot.Facts[i];
				if (fact == null)
					continue;

				if (fact.Kind != DataFactKind.Token
					&& fact.Kind != DataFactKind.SymbolDefinition
					&& fact.Kind != DataFactKind.SymbolReference)
				{
					continue;
				}

				if (!string.Equals(NormalizeDocumentKey(fact.DocumentKey.Value), documentKey, StringComparison.OrdinalIgnoreCase))
					continue;

				if (!TryParseTokenFact(fact, out SemanticTokenAbsolute token))
					continue;

				string tokenKey = token.Line
					+ "|" + token.Start
					+ "|" + token.Length
					+ "|" + token.TokenType
					+ "|" + token.TokenModifiers;

				if (dedupe.Add(tokenKey))
					tokens.Add(token);
			}

			return tokens;
		}

		private static bool TryParseTokenFact(DataFact fact, out SemanticTokenAbsolute token)
		{
			token = null;
			if (fact == null || fact.Payload == null)
				return false;

			if (fact.Payload is TokenDataFactPayload tokenPayload
				&& TryParseTokenPayload(tokenPayload, out SemanticTokenAbsolute tokenFromPayload))
			{
				token = tokenFromPayload;
				return true;
			}

			if (fact.Payload is SymbolDataFactPayload symbolPayload
				&& TryParseTokenPayload(symbolPayload, out SemanticTokenAbsolute tokenFromSymbol))
			{
				token = tokenFromSymbol;
				return true;
			}

			return false;
		}

		private static bool TryParseTokenPayload(TokenDataFactPayload payload, out SemanticTokenAbsolute token)
		{
			token = null;
			if (payload == null)
				return false;

			if (payload.Line < 0
				|| payload.Start < 0
				|| payload.Length <= 0
				|| payload.TokenType < 0
				|| payload.TokenModifiers < 0)
				return false;

			token = new SemanticTokenAbsolute(
				payload.Line,
				payload.Start,
				payload.Length,
				payload.TokenType,
				payload.TokenModifiers);
			return true;
		}

		private static bool TryParseTokenPayload(SymbolDataFactPayload payload, out SemanticTokenAbsolute token)
		{
			token = null;
			if (payload == null)
				return false;

			if (payload.Symbol == null || !payload.HasRange)
				return false;

			if (payload.StartLine != payload.EndLine)
				return false;

			int length = payload.EndCharacter - payload.StartCharacter;
			if (length <= 0)
				return false;

			int tokenType = MapSymbolKindToSemanticType(payload.Symbol.Kind);
			if (tokenType < 0)
				return false;

			if (payload.StartLine < 0 || payload.StartCharacter < 0)
				return false;

			token = new SemanticTokenAbsolute(
				payload.StartLine,
				payload.StartCharacter,
				length,
				tokenType,
				0);
			return true;
		}

		private static List<DataFact> NormalizeReferences(IReadOnlyList<DataFact> references)
		{
			if (references == null || references.Count == 0)
				return new List<DataFact>(0);

			var filtered = new List<DataFact>(references.Count);
			for (int i = 0; i < references.Count; i++)
			{
				DataFact fact = references[i];
				if (fact != null)
					filtered.Add(fact);
			}

			filtered.Sort(CompareReferenceFact);

			var deduped = new List<DataFact>(filtered.Count);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < filtered.Count; i++)
			{
				DataFact fact = filtered[i];
				if (fact == null)
					continue;

				string dedupeKey = BuildReferenceDedupeKey(fact);
				if (seen.Add(dedupeKey))
					deduped.Add(fact);
			}

			return deduped;
		}

		private static int CompareReferenceFact(DataFact left, DataFact right)
		{
			if (ReferenceEquals(left, right))
				return 0;

			if (left == null)
				return -1;

			if (right == null)
				return 1;

			string leftDocument = NormalizeDocumentKey(left.DocumentKey.Value);
			string rightDocument = NormalizeDocumentKey(right.DocumentKey.Value);
			int byDocument = StringComparer.OrdinalIgnoreCase.Compare(leftDocument, rightDocument);
			if (byDocument != 0)
				return byDocument;

			ExtractReferenceStart(left, out int leftLine, out int leftCharacter);
			ExtractReferenceStart(right, out int rightLine, out int rightCharacter);

			int byLine = leftLine.CompareTo(rightLine);
			if (byLine != 0)
				return byLine;

			int byCharacter = leftCharacter.CompareTo(rightCharacter);
			if (byCharacter != 0)
				return byCharacter;

			int byLength = left.Span.Length.CompareTo(right.Span.Length);
			if (byLength != 0)
				return byLength;

			int bySpanStart = left.Span.Start.CompareTo(right.Span.Start);
			if (bySpanStart != 0)
				return bySpanStart;

			return StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value);
		}

		private static string BuildReferenceDedupeKey(DataFact fact)
		{
			if (fact == null)
				return string.Empty;

			string document = NormalizeDocumentKey(fact.DocumentKey.Value);
			ExtractReferenceStart(fact, out int line, out int character);
			return document
				+ "|" + line
				+ "|" + character
				+ "|" + fact.Span.Length;
		}

		private static void ExtractReferenceStart(DataFact fact, out int line, out int character)
		{
			line = 0;
			character = 0;

			if (fact != null && fact.Payload is SymbolDataFactPayload payload && payload.HasRange)
			{
				line = payload.StartLine;
				character = payload.StartCharacter;
				return;
			}

			if (fact == null)
				return;

			line = fact.Span.Start;
			character = 0;
		}

		private static int MapSymbolKindToSemanticType(SymbolKindTag kind)
		{
			switch (kind)
			{
				case SymbolKindTag.Function:
					return SemanticTokenTypeFunction;
				case SymbolKindTag.Variable:
					return SemanticTokenTypeVariable;
				case SymbolKindTag.Struct:
					return SemanticTokenTypeStruct;
				case SymbolKindTag.Parameter:
					return SemanticTokenTypeParameter;
				case SymbolKindTag.Enum:
					return SemanticTokenTypeEnum;
				case SymbolKindTag.StructField:
					return SemanticTokenTypeProperty;
				case SymbolKindTag.EnumMember:
					return SemanticTokenTypeEnumMember;
				case SymbolKindTag.IncludeFile:
					return SemanticTokenTypeNamespace;
				default:
					return SemanticTokenTypeUnknown;
			}
		}


		private static int CompareSemanticTokenAbsolute(SemanticTokenAbsolute left, SemanticTokenAbsolute right)
		{
			if (ReferenceEquals(left, right))
				return 0;

			if (left == null)
				return -1;

			if (right == null)
				return 1;

			int byLine = left.Line.CompareTo(right.Line);
			if (byLine != 0)
				return byLine;

			int byStart = left.Start.CompareTo(right.Start);
			if (byStart != 0)
				return byStart;

			int byLength = left.Length.CompareTo(right.Length);
			if (byLength != 0)
				return byLength;

			int byType = left.TokenType.CompareTo(right.TokenType);
			if (byType != 0)
				return byType;

			return left.TokenModifiers.CompareTo(right.TokenModifiers);
		}

		private static string NormalizeDocumentKey(string value)
		{
			return DocumentKeyNormalizer.Normalize(value);
		}

		private sealed class SemanticTokenAbsolute
		{
			public SemanticTokenAbsolute(int line, int start, int length, int tokenType, int tokenModifiers)
			{
				Line = line;
				Start = start;
				Length = length;
				TokenType = tokenType;
				TokenModifiers = tokenModifiers;
			}

			public int Line { get; }
			public int Start { get; }
			public int Length { get; }
			public int TokenType { get; }
			public int TokenModifiers { get; }
		}
	}

	public sealed class LspDefinitionPayload
	{
		public string DocumentKey { get; }
		public TextSpan Span { get; }
		public DataFactPayload SourcePayload { get; }

		public LspDefinitionPayload(string documentKey, TextSpan span, DataFactPayload sourcePayload)
		{
			DocumentKey = documentKey ?? string.Empty;
			Span = span;
			SourcePayload = sourcePayload;
		}
	}

	public sealed class LspReferenceItem
	{
		public string DocumentKey { get; }
		public TextSpan Span { get; }
		public DataFactPayload SourcePayload { get; }

		public LspReferenceItem(string documentKey, TextSpan span, DataFactPayload sourcePayload)
		{
			DocumentKey = documentKey ?? string.Empty;
			Span = span;
			SourcePayload = sourcePayload;
		}
	}

	public sealed class LspHoverPayload
	{
		public string Summary { get; }
		public string Scope { get; }
		public string ParentName { get; }
		public string Origin { get; }

		public LspHoverPayload(string summary, string scope, string parentName, string origin)
		{
			Summary = summary ?? string.Empty;
			Scope = scope ?? string.Empty;
			ParentName = parentName ?? string.Empty;
			Origin = origin ?? string.Empty;
		}
	}

	public sealed class LspCompletionItem
	{
		public string Label { get; }
		public string Kind { get; }
		public string Detail { get; }
		public string Documentation { get; }

		public LspCompletionItem(string label, string kind, string detail, string documentation = null)
		{
			Label = label ?? string.Empty;
			Kind = kind ?? string.Empty;
			Detail = detail ?? string.Empty;
			Documentation = documentation ?? string.Empty;
		}
	}

	public sealed class LspSignatureItem
	{
		public string Label { get; }
		public string Source { get; }

		public LspSignatureItem(string label, string source)
		{
			Label = label ?? string.Empty;
			Source = source ?? string.Empty;
		}
	}

	public sealed class LspSignatureHelpPayload
	{
		private static readonly IReadOnlyList<LspSignatureItem> EmptySignatures = new List<LspSignatureItem>(0);

		public IReadOnlyList<LspSignatureItem> Signatures { get; }
		public int ActiveSignature { get; }
		public int ActiveParameter { get; }

		public LspSignatureHelpPayload(IReadOnlyList<LspSignatureItem> signatures, int activeSignature, int activeParameter)
		{
			Signatures = signatures ?? EmptySignatures;
			ActiveSignature = activeSignature;
			ActiveParameter = activeParameter;
		}
	}

	public sealed class LspPrepareRenamePayload
	{
		public TextSpan Range { get; }
		public string Placeholder { get; }

		public LspPrepareRenamePayload(TextSpan range, string placeholder)
		{
			Range = range;
			Placeholder = placeholder ?? string.Empty;
		}
	}

	public sealed class LspRenameEdit
	{
		public string DocumentKey { get; }
		public TextSpan Range { get; }
		public string NewText { get; }

		public LspRenameEdit(string documentKey, TextSpan range, string newText)
		{
			DocumentKey = documentKey ?? string.Empty;
			Range = range;
			NewText = newText ?? string.Empty;
		}
	}

	public sealed class LspRenamePayload
	{
		private static readonly IReadOnlyList<LspRenameEdit> EmptyEdits = new List<LspRenameEdit>(0);

		public string NewName { get; }
		public IReadOnlyList<LspRenameEdit> Edits { get; }

		public LspRenamePayload(string newName, IReadOnlyList<LspRenameEdit> edits)
		{
			NewName = newName ?? string.Empty;
			Edits = edits ?? EmptyEdits;
		}
	}

	public sealed class LspDocumentSymbolItem
	{
		public string Name { get; }
		public string Kind { get; }
		public string Scope { get; }
		public string ParentName { get; }
		public string Origin { get; }
		public TextSpan DeclarationSpan { get; }

		public LspDocumentSymbolItem(
			string name,
			string kind,
			string scope,
			string parentName,
			string origin,
			TextSpan declarationSpan)
		{
			Name = name ?? string.Empty;
			Kind = kind ?? string.Empty;
			Scope = scope ?? string.Empty;
			ParentName = parentName ?? string.Empty;
			Origin = origin ?? string.Empty;
			DeclarationSpan = declarationSpan;
		}
	}

	public sealed class LspSemanticTokensPayload
	{
		private static readonly IReadOnlyList<int> EmptyData = new List<int>(0);

		public IReadOnlyList<int> Data { get; }
		public string Message { get; }

		public LspSemanticTokensPayload(IReadOnlyList<int> data, string message)
		{
			Data = data ?? EmptyData;
			Message = message ?? string.Empty;
		}
	}
}
