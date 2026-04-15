// Responsibility:
//   Canonical workspace symbol and reference index with incremental updates.
// Owns:
//   Declaration table, reference table, include graph hooks, alias graph hooks.
// Inputs/Outputs:
//   In: WorkspaceSnapshot and compiler/parser semantic facts.
//   Out: queryable symbol/reference datasets for Query services.
// Allowed Dependencies:
//   - Contracts (SymbolIdentity, TextSpan)
//   - Infrastructure (Paths, Workspace)
//   - IncludeGraph
//   - AliasGraph
// Forbidden Dependencies:
//   - LSP protocol response generation.
//   - Handler-specific branching behavior.
// Invariants:
//   - Index updates are atomic per snapshot transition.
//   - All keys are canonicalized before insert/lookup.
// Boundary Closure:
//   Upstream: document store snapshots and metadata adapters.
//   Downstream: SymbolResolver, Definition/References/Rename/Hover/Completion.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Contracts;
using FFVM.Debug.Lsp.Infrastructure.Paths;
using FFVM.Debug.Lsp.Infrastructure.Workspace;

namespace FFVM.Debug.Lsp.Index
{
	public struct SymbolLocation
	{
		public PathKey DocumentKey { get; }
		public TextSpan Span { get; }

		public SymbolLocation(PathKey documentKey, TextSpan span)
		{
			DocumentKey = documentKey;
			Span = span;
		}
	}

	public interface IWorkspaceSymbolIndex
	{
		void Rebuild(IWorkspaceSnapshot snapshot);

		void UpdateDocuments(IEnumerable<PathKey> changedDocumentKeys, IWorkspaceSnapshot snapshot);

		bool TryResolveAt(PathKey documentKey, TextPosition position, out SymbolIdentity symbol);

		bool TryFindDefinition(SymbolIdentity symbol, out SymbolLocation definition);

		IReadOnlyList<SymbolLocation> FindReferences(SymbolIdentity symbol, bool includeDeclaration);
	}
}
