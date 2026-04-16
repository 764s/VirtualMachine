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

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryLspQueryFacade : ILspQueryFacade
	{
		private const int CompletionLimit = 128;
		private const int DocumentSymbolLimit = 512;
		private const int SemanticTokenTypeStruct = 5;
		private const int SemanticTokenTypeParameter = 7;
		private const int SemanticTokenTypeVariable = 8;
		private const int SemanticTokenTypeProperty = 9;
		private const int SemanticTokenTypeEnum = 10;
		private const int SemanticTokenTypeFunction = 12;
		private const int SemanticTokenTypeKeyword = 15;

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
			if (references == null || references.Count == 0)
				return SymbolQueryResult.NotFound("No references found for resolved symbol.");

			var ranges = new List<TextSpan>(references.Count);
			var payload = new List<LspReferenceItem>(references.Count);
			for (int i = 0; i < references.Count; i++)
			{
				DataFact fact = references[i];
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

			string summary = symbol.Kind + ": " + symbol.Name;
			var payload = new LspHoverPayload(summary, symbol.Scope, symbol.ParentName, symbol.Origin);

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

			string query = request != null ? request.NewName : string.Empty;
			if (string.IsNullOrWhiteSpace(query))
				query = string.Empty;

			IReadOnlyList<SymbolIdentity> candidates = index.NameIndex.Search(query, CompletionLimit);
			if (candidates == null)
				candidates = new List<SymbolIdentity>(0);

			var items = new List<LspCompletionItem>(candidates.Count);
			for (int i = 0; i < candidates.Count; i++)
			{
				SymbolIdentity candidate = candidates[i];
				items.Add(new LspCompletionItem(
					candidate.Name,
					candidate.Kind.ToString(),
					BuildSymbolDetail(candidate)));
			}

			SymbolIdentity anchor = candidates.Count > 0
				? candidates[0]
				: SymbolIdentity.CreateUnknown("completion");

			return SymbolQueryResult.Success(anchor, null, SymbolQueryPayload.ForCompletion(items));
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
					return 3;
				case SymbolKindTag.StructField:
					return SemanticTokenTypeProperty;
				case SymbolKindTag.EnumMember:
					return SemanticTokenTypeEnum;
				case SymbolKindTag.IncludeFile:
					return SemanticTokenTypeKeyword;
				default:
					return -1;
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

		public LspCompletionItem(string label, string kind, string detail)
		{
			Label = label ?? string.Empty;
			Kind = kind ?? string.Empty;
			Detail = detail ?? string.Empty;
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
