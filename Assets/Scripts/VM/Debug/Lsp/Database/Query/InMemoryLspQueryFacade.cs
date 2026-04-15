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

		public SymbolQueryResult QueryDefinition(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			if (!TryGetIndex(snapshot, out IIndexSnapshot index, out SymbolQueryResult indexError))
				return indexError;

			if (!TryResolveSymbol(index, request, out SymbolIdentity symbol, out SymbolQueryResult resolveError))
				return resolveError;

			if (index.SymbolIndex != null && index.SymbolIndex.TryGetDefinition(symbol, out DataFact definitionFact) && definitionFact != null)
			{
				var ranges = new List<TextSpan> { definitionFact.Span };
				var payload = new LspDefinitionPayload(definitionFact.DocumentKey.Value, definitionFact.Span, definitionFact.Payload);
				return SymbolQueryResult.Success(symbol, ranges, payload);
			}

			if (symbol.DeclarationSpan.Length > 0)
			{
				var ranges = new List<TextSpan> { symbol.DeclarationSpan };
				var payload = new LspDefinitionPayload(symbol.Origin, symbol.DeclarationSpan, null);
				return SymbolQueryResult.Success(symbol, ranges, payload);
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
				payload.Add(new LspReferenceItem(fact.DocumentKey.Value, fact.Span, fact.Payload));
			}

			return SymbolQueryResult.Success(symbol, ranges, payload);
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

			return SymbolQueryResult.Success(symbol, ranges, payload);
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

			return SymbolQueryResult.Success(anchor, null, items);
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
			return SymbolQueryResult.Success(symbol, null, payload);
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
			return SymbolQueryResult.Success(symbol, new List<TextSpan> { range }, payload);
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
			if (references.Payload is IReadOnlyList<LspReferenceItem> typedItems)
			{
				for (int i = 0; i < typedItems.Count; i++)
				{
					LspReferenceItem item = typedItems[i];
					edits.Add(new LspRenameEdit(item.DocumentKey, item.Span, request.NewName));
				}
			}

			var payload = new LspRenamePayload(request.NewName, edits);
			return SymbolQueryResult.Success(references.Symbol, references.Ranges, payload);
		}

		public object QueryDocumentSymbols(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
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

		public object QuerySemanticTokensFull(CodeDatabaseSnapshot snapshot, SymbolQueryRequest request)
		{
			return new LspSemanticTokensPayload(new List<int>(0), "Semantic token extraction is not implemented yet.");
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

			if (!index.PositionIndex.TryResolveSymbol(new PathKey(request.DocumentKey), request.Position, out symbol) || symbol == null)
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
	}

	public sealed class LspDefinitionPayload
	{
		public string DocumentKey { get; }
		public TextSpan Span { get; }
		public object SourcePayload { get; }

		public LspDefinitionPayload(string documentKey, TextSpan span, object sourcePayload)
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
		public object SourcePayload { get; }

		public LspReferenceItem(string documentKey, TextSpan span, object sourcePayload)
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
