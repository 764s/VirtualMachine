// Responsibility:
//   Read-only derived index contracts over snapshot facts.
// Owns:
//   Index view boundary for position/symbol/include/name queries.
// Inputs/Outputs:
//   In: snapshot facts from database write pipeline.
//   Out: stable query acceleration interfaces.
// Allowed Dependencies:
//   - PathKey
//   - TextPosition
//   - SymbolIdentity
//   - DataFact
// Forbidden Dependencies:
//   - Protocol-specific payload formatting.
//   - Snapshot mutation operations.
// Invariants:
//   - Index views are version-bound and immutable.
// Boundary Closure:
//   Upstream: IndexMaintainer.
//   Downstream: query facade and handler-facing services.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Contracts;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public interface IIndexSnapshot
	{
		long SnapshotVersion { get; }

		IPositionIndex PositionIndex { get; }

		ISymbolIndex SymbolIndex { get; }

		IIncludeGraphIndex IncludeGraphIndex { get; }

		INameIndex NameIndex { get; }
	}

	public interface IPositionIndex
	{
		bool TryResolveSymbol(PathKey documentKey, TextPosition position, out SymbolIdentity symbol);
	}

	public interface ISymbolIndex
	{
		bool TryGetDefinition(SymbolIdentity symbol, out DataFact definitionFact);

		IReadOnlyList<DataFact> GetReferences(SymbolIdentity symbol, bool includeDeclaration);
	}

	public interface IIncludeGraphIndex
	{
		IReadOnlyList<PathKey> GetIncludes(PathKey documentKey);

		IReadOnlyList<PathKey> GetDependents(PathKey documentKey);
	}

	public interface INameIndex
	{
		IReadOnlyList<SymbolIdentity> Search(string query, int limit);
	}
}
