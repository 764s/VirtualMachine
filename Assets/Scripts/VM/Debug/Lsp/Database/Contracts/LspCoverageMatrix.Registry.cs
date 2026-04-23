// Responsibility:
//   Mechanical shape validation for the T3 searchable-unit coverage matrix.
// Owns:
//   ValidateCoverage, ValidateCellShape, and discovery helpers used by tests and CI.
// Inputs/Outputs:
//   In: the statically-built registry from BuildCells.
//   Out: boolean health signal + first-failure error message (mirrors
//   LspIntentContractRegistry.ValidateBridgeCoverage style).
// Allowed Dependencies:
//   - System collections, sibling partial files, and SymbolKindTag taxonomy.
// Forbidden Dependencies:
//   - Parser/AST/query/protocol (validation is closed over taxonomy only).
// Invariants:
//   - Implemented cells carry non-empty TestId with an allowed prefix.
//   - Every declared UnitId has at least one Declaration cell registered.
//   - Required fact-kind flags are non-None.
// Boundary Closure:
//   Upstream: test harness and CI gate.
//   Downstream: none.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public static partial class CoverageMatrixRegistry
	{
		private static readonly string[] AllowedTestIdPrefixes = new string[]
		{
			"LSPNEW-",
			"DBQ-",
			"LCM-",
		};

		public static bool ValidateCoverage(out string error)
		{
			error = null;

			if (CellsList == null || CellsList.Count == 0)
			{
				error = "Coverage matrix is empty.";
				return false;
			}

			var declaredUnits = new HashSet<CoverageUnitId>();
			for (int i = 0; i < CellsList.Count; i++)
			{
				CoverageMatrixCell cell = CellsList[i];
				if (!ValidateCellShape(cell, i, out error))
					return false;
				declaredUnits.Add(cell.UnitId);
			}

			foreach (CoverageUnitId unit in Enum.GetValues(typeof(CoverageUnitId)))
			{
				if (unit == CoverageUnitId.Unknown)
					continue;
				if (!declaredUnits.Contains(unit))
				{
					error = "Coverage matrix is missing cells for unit: " + unit + ".";
					return false;
				}
			}

			return true;
		}

		private static bool ValidateCellShape(CoverageMatrixCell cell, int index, out string error)
		{
			error = null;
			if (cell == null)
			{
				error = "Coverage matrix contains null cell at index " + index + ".";
				return false;
			}

			if (cell.UnitId == CoverageUnitId.Unknown)
			{
				error = "Coverage cell uses Unknown unit id at index " + index + ".";
				return false;
			}

			if (cell.PositionTag == CoveragePositionTag.Unknown)
			{
				error = "Coverage cell uses Unknown position tag: " + cell.CellKey + ".";
				return false;
			}

			if (cell.ScenarioId == CoverageScenarioId.Unknown)
			{
				error = "Coverage cell uses Unknown scenario id: " + cell.CellKey + ".";
				return false;
			}

			if (cell.RequiredFactKinds == CoverageFactKindFlag.None)
			{
				error = "Coverage cell declares no required fact kinds: " + cell.CellKey + ".";
				return false;
			}

			if (cell.Implemented)
			{
				if (string.IsNullOrWhiteSpace(cell.TestId))
				{
					error = "Implemented coverage cell has no TestId: " + cell.CellKey + ".";
					return false;
				}

				if (!HasAllowedPrefix(cell.TestId))
				{
					error = "Implemented coverage cell TestId uses unknown prefix: " + cell.CellKey + " -> " + cell.TestId + ".";
					return false;
				}
			}
			else
			{
				if (!string.IsNullOrWhiteSpace(cell.TestId))
				{
					error = "Unimplemented coverage cell must leave TestId empty: " + cell.CellKey + ".";
					return false;
				}
			}

			return true;
		}

		private static bool HasAllowedPrefix(string testId)
		{
			for (int i = 0; i < AllowedTestIdPrefixes.Length; i++)
			{
				if (testId.StartsWith(AllowedTestIdPrefixes[i], StringComparison.Ordinal))
					return true;
			}
			return false;
		}

		public static int ImplementedCount()
		{
			int count = 0;
			for (int i = 0; i < CellsList.Count; i++)
			{
				if (CellsList[i] != null && CellsList[i].Implemented)
					count++;
			}
			return count;
		}
	}
}
