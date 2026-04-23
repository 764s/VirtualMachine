// Responsibility:
//   Cell record type and registry shell for the executable T3 searchable-unit coverage matrix.
// Owns:
//   CoverageMatrixCell POCO and the partial CoverageMatrixRegistry entry points (lookup surface).
// Inputs/Outputs:
//   In: immutable CoverageMatrixCell list built in CoverageMatrixRegistry.Cells partial file.
//   Out: read-only registry access (All, Count, lookup helpers).
// Allowed Dependencies:
//   - System collections and the enums from LspCoverageMatrix.Enums.cs.
//   - SymbolKindTag (for mapping rows to the kind taxonomy).
// Forbidden Dependencies:
//   - Parser/AST/query layers.
// Invariants:
//   - Cell identity = (UnitId, PositionTag, ScenarioId). Duplicates are rejected at build time.
//   - Implemented cells must carry a non-empty TestId.
// Boundary Closure:
//   Upstream: matrix authoring (Cells partial).
//   Downstream: validator (Registry partial) and coverage tests.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public sealed class CoverageMatrixCell
	{
		public CoverageUnitId UnitId { get; }
		public SymbolKindTag KindTag { get; }
		public CoveragePositionTag PositionTag { get; }
		public CoverageScenarioId ScenarioId { get; }
		public CoverageFactKindFlag RequiredFactKinds { get; }
		public bool Implemented { get; }
		public string TestId { get; }
		public string Notes { get; }

		public CoverageMatrixCell(
			CoverageUnitId unitId,
			SymbolKindTag kindTag,
			CoveragePositionTag positionTag,
			CoverageScenarioId scenarioId,
			CoverageFactKindFlag requiredFactKinds,
			bool implemented,
			string testId,
			string notes)
		{
			UnitId = unitId;
			KindTag = kindTag;
			PositionTag = positionTag;
			ScenarioId = scenarioId;
			RequiredFactKinds = requiredFactKinds;
			Implemented = implemented;
			TestId = testId ?? string.Empty;
			Notes = notes ?? string.Empty;
		}

		public string CellKey
		{
			get
			{
				return UnitId + "|" + PositionTag + "|" + ScenarioId;
			}
		}
	}

	public static partial class CoverageMatrixRegistry
	{
		private static readonly IReadOnlyList<CoverageMatrixCell> CellsList = BuildCells();
		private static readonly Dictionary<string, CoverageMatrixCell> CellsByKey = BuildByKey();

		public static IReadOnlyList<CoverageMatrixCell> All
		{
			get { return CellsList; }
		}

		public static int Count
		{
			get { return CellsList.Count; }
		}

		public static bool TryGet(CoverageUnitId unitId, CoveragePositionTag positionTag, CoverageScenarioId scenarioId, out CoverageMatrixCell cell)
		{
			string key = unitId + "|" + positionTag + "|" + scenarioId;
			return CellsByKey.TryGetValue(key, out cell);
		}

		public static CoverageMatrixCell Require(CoverageUnitId unitId, CoveragePositionTag positionTag, CoverageScenarioId scenarioId)
		{
			if (!TryGet(unitId, positionTag, scenarioId, out CoverageMatrixCell cell) || cell == null)
				throw new InvalidOperationException("Coverage matrix cell is not registered: " + unitId + "|" + positionTag + "|" + scenarioId + ".");
			return cell;
		}

		private static Dictionary<string, CoverageMatrixCell> BuildByKey()
		{
			var map = new Dictionary<string, CoverageMatrixCell>(StringComparer.Ordinal);
			for (int i = 0; i < CellsList.Count; i++)
			{
				CoverageMatrixCell cell = CellsList[i];
				if (cell == null)
					continue;
				string key = cell.CellKey;
				if (map.ContainsKey(key))
					throw new InvalidOperationException("Duplicate coverage matrix cell key: " + key + ".");
				map[key] = cell;
			}
			return map;
		}
	}
}
