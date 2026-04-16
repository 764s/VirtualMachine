// Responsibility:
//   Unified output contract for symbol-query operations.
// Owns:
//   Resolved identity, ranges, and operation status metadata.
// Inputs/Outputs:
//   In: SymbolQueryCore service outputs.
//   Out: handler-ready data for protocol responses/edits.
// Allowed Dependencies:
//   - SymbolIdentity
//   - TextSpan
// Forbidden Dependencies:
//   - JSON-RPC response writing.
//   - Workspace mutation side effects.
// Invariants:
//   - Result carries enough data for idempotent response formatting.
//   - Empty result states are explicit, never implicit null semantics.
// Boundary Closure:
//   Upstream: Query services.
//   Downstream: handlers and protocol writer.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Database;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public enum SymbolQueryPayloadKind
	{
		None = 0,
		Definition,
		References,
		Hover,
		Completion,
		SignatureHelp,
		PrepareRename,
		Rename
	}

	public sealed class SymbolQueryPayload
	{
		private static readonly IReadOnlyList<LspReferenceItem> EmptyReferences = new List<LspReferenceItem>(0);
		private static readonly IReadOnlyList<LspCompletionItem> EmptyCompletions = new List<LspCompletionItem>(0);

		private SymbolQueryPayload(
			SymbolQueryPayloadKind kind,
			LspDefinitionPayload definition,
			IReadOnlyList<LspReferenceItem> references,
			LspHoverPayload hover,
			IReadOnlyList<LspCompletionItem> completions,
			LspSignatureHelpPayload signatureHelp,
			LspPrepareRenamePayload prepareRename,
			LspRenamePayload rename)
		{
			Kind = kind;
			Definition = definition;
			References = references ?? EmptyReferences;
			Hover = hover;
			CompletionItems = completions ?? EmptyCompletions;
			SignatureHelp = signatureHelp;
			PrepareRename = prepareRename;
			Rename = rename;
		}

		public static SymbolQueryPayload None { get; } = new SymbolQueryPayload(
			SymbolQueryPayloadKind.None,
			null,
			EmptyReferences,
			null,
			EmptyCompletions,
			null,
			null,
			null);

		public SymbolQueryPayloadKind Kind { get; }
		public LspDefinitionPayload Definition { get; }
		public IReadOnlyList<LspReferenceItem> References { get; }
		public LspHoverPayload Hover { get; }
		public IReadOnlyList<LspCompletionItem> CompletionItems { get; }
		public LspSignatureHelpPayload SignatureHelp { get; }
		public LspPrepareRenamePayload PrepareRename { get; }
		public LspRenamePayload Rename { get; }

		public static SymbolQueryPayload ForDefinition(LspDefinitionPayload payload)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.Definition, payload, null, null, null, null, null, null);
		}

		public static SymbolQueryPayload ForReferences(IReadOnlyList<LspReferenceItem> references)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.References, null, references, null, null, null, null, null);
		}

		public static SymbolQueryPayload ForHover(LspHoverPayload payload)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.Hover, null, null, payload, null, null, null, null);
		}

		public static SymbolQueryPayload ForCompletion(IReadOnlyList<LspCompletionItem> completions)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.Completion, null, null, null, completions, null, null, null);
		}

		public static SymbolQueryPayload ForSignatureHelp(LspSignatureHelpPayload payload)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.SignatureHelp, null, null, null, null, payload, null, null);
		}

		public static SymbolQueryPayload ForPrepareRename(LspPrepareRenamePayload payload)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.PrepareRename, null, null, null, null, null, payload, null);
		}

		public static SymbolQueryPayload ForRename(LspRenamePayload payload)
		{
			return new SymbolQueryPayload(SymbolQueryPayloadKind.Rename, null, null, null, null, null, null, payload);
		}
	}

	public sealed class SymbolQueryResult
	{
		private static readonly IReadOnlyList<TextSpan> EmptyRanges = new List<TextSpan>(0);

		public bool Succeeded { get; }
		public SymbolIdentity Symbol { get; }
		public IReadOnlyList<TextSpan> Ranges { get; }
		public string Message { get; }
		public SymbolQueryPayload Payload { get; }

		public SymbolQueryResult(bool succeeded, SymbolIdentity symbol, IReadOnlyList<TextSpan> ranges, string message, SymbolQueryPayload payload)
		{
			Succeeded = succeeded;
			Symbol = symbol;
			Ranges = ranges ?? EmptyRanges;
			Message = message ?? string.Empty;
			Payload = payload ?? SymbolQueryPayload.None;
		}

		public static SymbolQueryResult Success(SymbolIdentity symbol, IReadOnlyList<TextSpan> ranges = null, SymbolQueryPayload payload = null)
		{
			return new SymbolQueryResult(true, symbol, ranges, string.Empty, payload);
		}

		public static SymbolQueryResult NotFound(string message)
		{
			return new SymbolQueryResult(false, null, EmptyRanges, message, SymbolQueryPayload.None);
		}

		public static SymbolQueryResult Failure(string message)
		{
			return new SymbolQueryResult(false, null, EmptyRanges, message, SymbolQueryPayload.None);
		}
	}
}
