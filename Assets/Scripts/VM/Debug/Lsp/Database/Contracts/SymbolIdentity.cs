// Responsibility:
//   Canonical immutable identity contract for one semantic symbol.
// Owns:
//   Identity shape only: kind, name, scope, parent, origin, declaration span.
// Inputs/Outputs:
//   In: parser/index symbol facts.
//   Out: a stable identity consumed by query services and handlers.
// Allowed Dependencies:
//   - SymbolKindTag
//   - TextSpan
// Forbidden Dependencies:
//   - Protocol dispatch and JSON-RPC payload logic.
//   - File URI/path canonicalization details.
// Invariants:
//   - Same semantic symbol maps to the same identity fields.
//   - Identity does not depend on the triggering LSP method.
// Boundary Closure:
//   Upstream: parser and workspace index producers.
//   Downstream: Query core, rename/references/definition, diagnostics.

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public sealed class SymbolIdentity
	{
		public SymbolKindTag Kind { get; }
		public string Name { get; }
		public string Scope { get; }
		public string ParentName { get; }
		public string Origin { get; }
		public TextSpan DeclarationSpan { get; }
		public string Documentation { get; }
		/// <summary>Type name for Parameter/Variable/StructField (used by completion to resolve member access). Empty for other kinds.</summary>
		public string TypeName { get; }
		/// <summary>Whether the symbol is marked private (skip cross-file suggestions).</summary>
		public bool IsPrivate { get; }

		public SymbolIdentity(
			SymbolKindTag kind,
			string name,
			string scope,
			string parentName,
			string origin,
			TextSpan declarationSpan,
			string documentation = null,
			string typeName = null,
			bool isPrivate = false)
		{
			Kind = kind;
			Name = name ?? string.Empty;
			Scope = scope ?? string.Empty;
			ParentName = parentName ?? string.Empty;
			Origin = origin ?? string.Empty;
			DeclarationSpan = declarationSpan;
			Documentation = documentation ?? string.Empty;
			TypeName = typeName ?? string.Empty;
			IsPrivate = isPrivate;
		}

		public static SymbolIdentity CreateUnknown(string name)
		{
			return new SymbolIdentity(SymbolKindTag.Unknown, name, string.Empty, string.Empty, string.Empty, new TextSpan(0, 0));
		}
	}
}
