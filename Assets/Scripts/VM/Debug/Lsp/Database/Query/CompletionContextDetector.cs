// Responsibility:
//   Lexical context detection for completion requests.
// Owns:
//   Classification of cursor position into completion kinds:
//   plain identifier, member access (struct/enum dot), type annotation,
//   new expression, include path, comment, string literal.
// Inputs/Outputs:
//   In: document text + cursor line/character (0-based LSP convention).
//   Out: CompletionContext value object.
// Allowed Dependencies:
//   - None (pure lexical scan).
// Forbidden Dependencies:
//   - Parser / AST / snapshot state.
// Invariants:
//   - Pure function; no side effects.
//   - Tolerates incomplete/invalid buffers.
// Boundary Closure:
//   Upstream: InMemoryLspQueryFacade.QueryCompletion.
//   Downstream: (none).

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Query
{
	public enum CompletionContextKind
	{
		Identifier,
		MemberAccess,
		TypeAnnotation,
		NewExpression,
		IncludePath,
		InsideString,
		InsideComment
	}

	public sealed class CompletionContext
	{
		public CompletionContextKind Kind { get; }
		/// <summary>For MemberAccess: the receiver chain tokens (e.g. ["player","stats"] for `player.stats.`).</summary>
		public IReadOnlyList<string> ReceiverChain { get; }
		/// <summary>Prefix already typed after the trigger (e.g. "hp" for `player.hp`).</summary>
		public string Prefix { get; }
		/// <summary>Column (0-based) of the start of the current word/prefix.</summary>
		public int PrefixStartColumn { get; }

		public CompletionContext(
			CompletionContextKind kind,
			IReadOnlyList<string> receiverChain,
			string prefix,
			int prefixStartColumn)
		{
			Kind = kind;
			ReceiverChain = receiverChain ?? new List<string>(0);
			Prefix = prefix ?? string.Empty;
			PrefixStartColumn = prefixStartColumn;
		}
	}

	public static class CompletionContextDetector
	{
		public static CompletionContext Detect(string documentText, int line, int character)
		{
			if (string.IsNullOrEmpty(documentText))
				return new CompletionContext(CompletionContextKind.Identifier, null, string.Empty, character);

			string lineText = ExtractLine(documentText, line);
			if (lineText == null) lineText = string.Empty;

			int cursor = character;
			if (cursor > lineText.Length) cursor = lineText.Length;
			if (cursor < 0) cursor = 0;

			// Check for line comment: if `//` occurs before cursor on this line → inside comment
			int commentIdx = FindLineCommentStart(lineText, cursor);
			if (commentIdx >= 0)
			{
				// Exception: `///` doc comments are handled client-side; still treat as comment here.
				return new CompletionContext(CompletionContextKind.InsideComment, null, string.Empty, cursor);
			}

			// Check for string literal: count unescaped quotes before cursor (odd = inside string)
			bool insideString = IsInsideStringLiteral(lineText, cursor);
			if (insideString)
			{
				// Special-case: inside an `include "..."` string — classify as IncludePath
				if (IsIncludeLine(lineText, cursor))
				{
					string partial = ExtractIncludePathPrefix(lineText, cursor);
					return new CompletionContext(CompletionContextKind.IncludePath, null, partial, cursor);
				}
				return new CompletionContext(CompletionContextKind.InsideString, null, string.Empty, cursor);
			}

			// Extract current word (identifier prefix) and column where it starts
			int wordStart = cursor;
			while (wordStart > 0 && IsIdentifierChar(lineText[wordStart - 1])) wordStart--;
			string prefix = lineText.Substring(wordStart, cursor - wordStart);

			// Walk backwards skipping whitespace to find the trigger char before the word
			int p = wordStart - 1;
			while (p >= 0 && (lineText[p] == ' ' || lineText[p] == '\t')) p--;

			// Check member access: `receiver.prefix` or `a.b.prefix`
			if (p >= 0 && lineText[p] == '.')
			{
				var chain = new List<string>();
				int cp = p;
				while (cp >= 0 && lineText[cp] == '.')
				{
					int segEnd = cp - 1;
					while (segEnd >= 0 && (lineText[segEnd] == ' ' || lineText[segEnd] == '\t')) segEnd--;
					int segStart = segEnd;
					while (segStart >= 0 && IsIdentifierChar(lineText[segStart])) segStart--;
					segStart++;
					if (segStart > segEnd) break;
					string seg = lineText.Substring(segStart, segEnd - segStart + 1);
					if (string.IsNullOrEmpty(seg)) break;
					chain.Insert(0, seg);
					cp = segStart - 1;
					while (cp >= 0 && (lineText[cp] == ' ' || lineText[cp] == '\t')) cp--;
				}
				if (chain.Count > 0)
					return new CompletionContext(CompletionContextKind.MemberAccess, chain, prefix, wordStart);
			}

			// Check `new <TypeName>` — keyword `new` immediately before the identifier prefix
			if (HasKeywordBefore(lineText, wordStart, "new"))
				return new CompletionContext(CompletionContextKind.NewExpression, null, prefix, wordStart);

			// Check type annotation: `: TypeName` or `)` `:` `TypeName` (return type) or `var x : TypeName`
			if (p >= 0 && lineText[p] == ':')
				return new CompletionContext(CompletionContextKind.TypeAnnotation, null, prefix, wordStart);

			// Check include path outside string (user typed `include ` but not yet `"`)
			if (IsIncludeLine(lineText, cursor) && !insideString)
			{
				// Treat as identifier context (user will open quote before path completion kicks in)
			}

			return new CompletionContext(CompletionContextKind.Identifier, null, prefix, wordStart);
		}

		private static string ExtractLine(string text, int line)
		{
			if (text == null) return string.Empty;
			int cur = 0;
			int lineIdx = 0;
			while (cur < text.Length)
			{
				int nl = text.IndexOf('\n', cur);
				if (nl < 0)
				{
					return lineIdx == line ? StripCR(text.Substring(cur)) : string.Empty;
				}
				if (lineIdx == line)
					return StripCR(text.Substring(cur, nl - cur));
				cur = nl + 1;
				lineIdx++;
			}
			return string.Empty;
		}

		private static string StripCR(string s)
		{
			if (s != null && s.Length > 0 && s[s.Length - 1] == '\r')
				return s.Substring(0, s.Length - 1);
			return s ?? string.Empty;
		}

		private static bool IsIdentifierChar(char c)
		{
			return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
		}

		private static int FindLineCommentStart(string lineText, int cursor)
		{
			bool inStr = false;
			for (int i = 0; i < cursor - 1 && i < lineText.Length - 1; i++)
			{
				char c = lineText[i];
				if (c == '"' && (i == 0 || lineText[i - 1] != '\\')) inStr = !inStr;
				if (!inStr && c == '/' && lineText[i + 1] == '/') return i;
			}
			return -1;
		}

		private static bool IsInsideStringLiteral(string lineText, int cursor)
		{
			int count = 0;
			for (int i = 0; i < cursor && i < lineText.Length; i++)
			{
				if (lineText[i] == '"' && (i == 0 || lineText[i - 1] != '\\'))
					count++;
			}
			return (count & 1) == 1;
		}

		private static bool IsIncludeLine(string lineText, int cursor)
		{
			// Find first non-space token on the line — must be `include`
			int i = 0;
			while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t')) i++;
			const string Kw = "include";
			if (i + Kw.Length > lineText.Length) return false;
			for (int k = 0; k < Kw.Length; k++)
				if (lineText[i + k] != Kw[k]) return false;
			// Next char must be whitespace or `"`
			int after = i + Kw.Length;
			if (after >= lineText.Length) return false;
			char c = lineText[after];
			return c == ' ' || c == '\t' || c == '"';
		}

		private static string ExtractIncludePathPrefix(string lineText, int cursor)
		{
			// Inside string — walk back to the opening quote
			int q = cursor - 1;
			while (q >= 0 && lineText[q] != '"') q--;
			if (q < 0) return string.Empty;
			return lineText.Substring(q + 1, cursor - q - 1);
		}

		private static bool HasKeywordBefore(string lineText, int wordStart, string keyword)
		{
			int p = wordStart - 1;
			while (p >= 0 && (lineText[p] == ' ' || lineText[p] == '\t')) p--;
			int kwEnd = p;
			int kwStart = kwEnd;
			while (kwStart >= 0 && IsIdentifierChar(lineText[kwStart])) kwStart--;
			kwStart++;
			if (kwStart > kwEnd) return false;
			if (kwEnd - kwStart + 1 != keyword.Length) return false;
			for (int i = 0; i < keyword.Length; i++)
				if (lineText[kwStart + i] != keyword[i]) return false;
			// Ensure it's not part of a larger identifier
			if (kwStart > 0 && IsIdentifierChar(lineText[kwStart - 1])) return false;
			return true;
		}
	}
}
