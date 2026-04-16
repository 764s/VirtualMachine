using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;

namespace FFVM.Debug.Lsp.Protocol
{
    internal static class LspProtocolPayloadProjector
    {
        public static JsonObject CreateInitializeResult()
        {
            var result = new JsonObject();

            // Minimal capability payload to complete VS Code initialize handshake.
            var capabilities = new JsonObject();
            result.Set(LspFields.Capabilities, capabilities);

            var serverInfo = new JsonObject();
            serverInfo.Set(LspFields.Name, LspValues.ServerName);
            serverInfo.Set(LspFields.Version, LspValues.ServerVersion);
            result.Set(LspFields.ServerInfo, serverInfo);

            return result;
        }

        public static List<object> ConvertDocumentSymbols(IReadOnlyList<LspDocumentSymbolItem> symbols)
        {
            if (symbols == null || symbols.Count == 0)
                return new List<object>(0);

            var output = new List<object>(symbols.Count);
            for (int i = 0; i < symbols.Count; i++)
            {
                LspDocumentSymbolItem symbol = symbols[i];
                if (symbol == null)
                    continue;

                JsonObject range = MakeRangeFromSpan(symbol.DeclarationSpan);
                var item = new JsonObject();
                item.Set(LspFields.Name, symbol.Name ?? string.Empty);
                item.Set(LspFields.Kind, MapDocumentSymbolKind(symbol.Kind));
                item.Set(LspFields.Range, range);
                item.Set(LspFields.SelectionRange, range);
                output.Add(item);
            }

            return output;
        }

        public static JsonObject ConvertHover(LspHoverPayload payload)
        {
            if (payload == null)
                return null;

            string summary = payload.Summary ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(payload.Scope))
                summary += "\n\nScope: " + payload.Scope;
            if (!string.IsNullOrWhiteSpace(payload.ParentName))
                summary += "\nParent: " + payload.ParentName;

            var contents = new JsonObject();
            contents.Set(LspFields.Kind, LspValues.Markdown);
            contents.Set(LspFields.Value, summary);

            var result = new JsonObject();
            result.Set(LspFields.Contents, contents);
            return result;
        }

        public static JsonObject ConvertDefinition(LspDefinitionPayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set(LspFields.Uri, DocumentKeyNormalizer.Normalize(payload.DocumentKey));
            result.Set(LspFields.Range, MakeRangeFromPayload(payload.SourcePayload, payload.Span));
            return result;
        }

        public static List<object> ConvertReferences(IReadOnlyList<LspReferenceItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<object>(0);

            var output = new List<object>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                LspReferenceItem item = items[i];
                if (item == null)
                    continue;

                var location = new JsonObject();
                location.Set(LspFields.Uri, DocumentKeyNormalizer.Normalize(item.DocumentKey));
                location.Set(LspFields.Range, MakeRangeFromPayload(item.SourcePayload, item.Span));
                output.Add(location);
            }

            return output;
        }

        public static List<object> ConvertCompletionItems(IReadOnlyList<LspCompletionItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<object>(0);

            var output = new List<object>(items.Count);
            for (int i = 0; i < items.Count; i++)
            {
                LspCompletionItem item = items[i];
                if (item == null)
                    continue;

                var projected = new JsonObject();
                projected.Set(LspFields.Label, item.Label ?? string.Empty);
                projected.Set(LspFields.Kind, MapCompletionKind(item.Kind));
                if (!string.IsNullOrWhiteSpace(item.Detail))
                    projected.Set(LspFields.Detail, item.Detail);
                output.Add(projected);
            }

            return output;
        }

        public static JsonObject ConvertSignatureHelp(LspSignatureHelpPayload payload)
        {
            if (payload == null)
                return null;

            var signatures = new List<object>();
            if (payload.Signatures != null)
            {
                for (int i = 0; i < payload.Signatures.Count; i++)
                {
                    LspSignatureItem signature = payload.Signatures[i];
                    if (signature == null)
                        continue;

                    var projected = new JsonObject();
                    projected.Set(LspFields.Label, signature.Label ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(signature.Source))
                    {
                        var doc = new JsonObject();
                        doc.Set(LspFields.Kind, LspValues.Markdown);
                        doc.Set(LspFields.Value, "Source: " + DocumentKeyNormalizer.Normalize(signature.Source));
                        projected.Set(LspFields.Documentation, doc);
                    }

                    signatures.Add(projected);
                }
            }

            var result = new JsonObject();
            result.Set(LspFields.Signatures, signatures);
            result.Set(LspFields.ActiveSignature, payload.ActiveSignature);
            result.Set(LspFields.ActiveParameter, payload.ActiveParameter);
            return result;
        }

        public static JsonObject ConvertPrepareRename(LspPrepareRenamePayload payload)
        {
            if (payload == null)
                return null;

            var result = new JsonObject();
            result.Set(LspFields.Range, MakeRangeFromSpan(payload.Range));
            result.Set(LspFields.Placeholder, payload.Placeholder ?? string.Empty);
            return result;
        }

        public static JsonObject ConvertRename(LspRenamePayload payload)
        {
            if (payload == null || payload.Edits == null || payload.Edits.Count == 0)
                return null;

            var changes = new JsonObject();
            var grouped = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < payload.Edits.Count; i++)
            {
                LspRenameEdit edit = payload.Edits[i];
                if (edit == null)
                    continue;

                string key = DocumentKeyNormalizer.Normalize(edit.DocumentKey);
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                if (!grouped.TryGetValue(key, out List<object> edits))
                {
                    edits = new List<object>();
                    grouped[key] = edits;
                }

                var textEdit = new JsonObject();
                textEdit.Set(LspFields.Range, MakeRangeFromSpan(edit.Range));
                textEdit.Set(LspFields.NewText, edit.NewText ?? string.Empty);
                edits.Add(textEdit);
            }

            foreach (KeyValuePair<string, List<object>> pair in grouped)
                changes.Set(pair.Key, pair.Value);

            var result = new JsonObject();
            result.Set(LspFields.Changes, changes);
            return result;
        }

        public static JsonObject ConvertSemanticTokens(LspSemanticTokensPayload payload)
        {
            if (payload == null)
                return null;

            var data = new List<object>();
            if (payload.Data != null)
            {
                for (int i = 0; i < payload.Data.Count; i++)
                    data.Add(payload.Data[i]);
            }

            var result = new JsonObject();
            result.Set(LspFields.Data, data);
            return result;
        }

        private static JsonObject MakeRangeFromPayload(DataFactPayload payload, TextSpan fallbackSpan)
        {
            if (payload is SymbolDataFactPayload symbol && symbol.HasRange)
            {
                return MakeRange(
                    symbol.StartLine,
                    symbol.StartCharacter,
                    symbol.EndLine,
                    symbol.EndCharacter);
            }

            return MakeRangeFromSpan(fallbackSpan);
        }

        private static JsonObject MakeRangeFromSpan(TextSpan span)
        {
            int start = span.Start < 0 ? 0 : span.Start;
            int length = span.Length <= 0 ? 1 : span.Length;
            return MakeRange(0, start, 0, start + length);
        }

        private static JsonObject MakeRange(int startLine, int startCharacter, int endLine, int endCharacter)
        {
            var start = new JsonObject();
            start.Set(LspFields.Line, startLine < 0 ? 0 : startLine);
            start.Set(LspFields.Character, startCharacter < 0 ? 0 : startCharacter);

            var end = new JsonObject();
            end.Set(LspFields.Line, endLine < 0 ? 0 : endLine);
            end.Set(LspFields.Character, endCharacter < 0 ? 0 : endCharacter);

            var range = new JsonObject();
            range.Set(LspFields.Start, start);
            range.Set(LspFields.End, end);
            return range;
        }

        private static int MapDocumentSymbolKind(string kind)
        {
            if (string.Equals(kind, LspSymbolKindNames.Function, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Function;
            if (string.Equals(kind, LspSymbolKindNames.Struct, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Struct;
            if (string.Equals(kind, LspSymbolKindNames.Enum, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.EnumType;
            if (string.Equals(kind, LspSymbolKindNames.Variable, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Variable;
            if (string.Equals(kind, LspSymbolKindNames.Parameter, StringComparison.OrdinalIgnoreCase))
                return (int)LspDocumentSymbolKindCode.Parameter;
            return (int)LspDocumentSymbolKindCode.Variable;
        }

        private static int MapCompletionKind(string kind)
        {
            if (string.Equals(kind, LspSymbolKindNames.Function, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Function;
            if (string.Equals(kind, LspSymbolKindNames.Struct, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Struct;
            if (string.Equals(kind, LspSymbolKindNames.Enum, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.EnumType;
            if (string.Equals(kind, LspSymbolKindNames.StructField, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.Field;
            if (string.Equals(kind, LspSymbolKindNames.EnumMember, StringComparison.OrdinalIgnoreCase))
                return (int)LspCompletionItemKindCode.EnumMember;
            if (string.Equals(kind, LspSymbolKindNames.Variable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kind, LspSymbolKindNames.Parameter, StringComparison.OrdinalIgnoreCase))
            {
                return (int)LspCompletionItemKindCode.Variable;
            }

            return (int)LspCompletionItemKindCode.Text;
        }
    }
}