// Responsibility:
//   Authoritative list of coverage matrix cells for T3 searchable-unit scenarios.
// Owns:
//   BuildCells() — the single source of truth for which (Unit x Position x Scenario)
//   tuples are claimed implemented, and which remain open for follow-up work.
// Inputs/Outputs:
//   In: none.
//   Out: read-only list of CoverageMatrixCell consumed by CoverageMatrixRegistry.
// Allowed Dependencies:
//   - Enums from LspCoverageMatrix.Enums.cs, POCO from LspCoverageMatrix.cs.
// Forbidden Dependencies:
//   - Parser/AST/query/protocol.
// Invariants:
//   - Every implemented cell cites a concrete TestId (prefix LSPNEW-*, DBQ-*, or LCM-*).
//   - Unimplemented cells must leave TestId empty; the validator enforces this.
// Boundary Closure:
//   Upstream: manual curation gated by executable validator.
//   Downstream: CoverageMatrixRegistry and LspCoverageMatrixTests.

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public static partial class CoverageMatrixRegistry
	{
		private static IReadOnlyList<CoverageMatrixCell> BuildCells()
		{
			var list = new List<CoverageMatrixCell>();
			AddFunctionCells(list);
			AddModuleVarCells(list);
			AddLocalAndParamCells(list);
			AddStructCells(list);
			AddEnumCells(list);
			AddIncludeAndAliasCells(list);
			AddOverrideCells(list);
			return list;
		}

		private static void AddFunctionCells(List<CoverageMatrixCell> list)
		{
			const CoverageFactKindFlag defOnly = CoverageFactKindFlag.SymbolDefinition;
			const CoverageFactKindFlag refs = CoverageFactKindFlag.SymbolReference;

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Function, SymbolKindTag.Function,
				CoveragePositionTag.Declaration, CoverageScenarioId.SameFile,
				defOnly, true, "DBQ-01A", "func decl def fact"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Function, SymbolKindTag.Function,
				CoveragePositionTag.CallSite, CoverageScenarioId.SameFile,
				refs, true, "DBQ-02A", "call-site reference fact"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.ExternalFunction, SymbolKindTag.Function,
				CoveragePositionTag.Declaration, CoverageScenarioId.CrossFileDirectInclude,
				defOnly, true, "LSPNEW-T3-EXT-01A", "external func decl resolves cross-file"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.ExternalFunction, SymbolKindTag.Function,
				CoveragePositionTag.CallSite, CoverageScenarioId.CrossFileDirectInclude,
				refs, true, "LSPNEW-T3-EXT-01B", "external func cross-file call references"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.ExternalFunction, SymbolKindTag.Function,
				CoveragePositionTag.CallSite, CoverageScenarioId.CrossFileTransitiveInclude,
				refs, true, "LSPNEW-T3-EXT-02A", "transitive include external func"));
		}

		private static void AddModuleVarCells(List<CoverageMatrixCell> list)
		{
			const CoverageFactKindFlag both = CoverageFactKindFlag.SymbolDefinition | CoverageFactKindFlag.SymbolReference;

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.ModuleVar, SymbolKindTag.Variable,
				CoveragePositionTag.Declaration, CoverageScenarioId.CFR11ModuleVar,
				both, true, "LSPNEW-T3-VAR-01A", "module var cross-file def"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.ModuleVar, SymbolKindTag.Variable,
				CoveragePositionTag.IdentifierReference, CoverageScenarioId.CFR11ModuleVar,
				CoverageFactKindFlag.SymbolReference, true, "LSPNEW-T3-VAR-01B", "module var cross-file refs"));
		}

		private static void AddLocalAndParamCells(List<CoverageMatrixCell> list)
		{
			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Parameter, SymbolKindTag.Parameter,
				CoveragePositionTag.Declaration, CoverageScenarioId.SameFile,
				CoverageFactKindFlag.SymbolDefinition, true, "LSPNEW-T3-PRM-01", "parameter def fact"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Parameter, SymbolKindTag.Parameter,
				CoveragePositionTag.TypeAnnotationParameter, CoverageScenarioId.SameFile,
				CoverageFactKindFlag.SymbolReference, false, string.Empty,
				"parameter type annotation (pending)"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.LocalVar, SymbolKindTag.Variable,
				CoveragePositionTag.Declaration, CoverageScenarioId.SameFile,
				CoverageFactKindFlag.SymbolDefinition, true, "LSPNEW-T3-LOC-01", "local var def fact"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.LocalVar, SymbolKindTag.Variable,
				CoveragePositionTag.IdentifierReference, CoverageScenarioId.SameFile,
				CoverageFactKindFlag.SymbolReference, true, "LSPNEW-T3-LOC-02", "local var ref fact"));
		}
	}
}
