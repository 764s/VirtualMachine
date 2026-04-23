// Responsibility:
//   Struct/field and enum/member coverage cells for the searchable-unit matrix.
// Owns:
//   AddStructCells and AddEnumCells partial helpers used by BuildCells.
// Inputs/Outputs:
//   In: none.
//   Out: cells appended to the registry list.
// Allowed Dependencies:
//   - Enums and POCO from the sibling partial files.
// Forbidden Dependencies:
//   - Parser/AST/query/protocol.
// Invariants:
//   - Any implemented cell must cite a concrete test id.
// Boundary Closure:
//   Upstream: CoverageMatrixRegistry.BuildCells.
//   Downstream: CoverageMatrixRegistry.ValidateCoverage + LspCoverageMatrixTests.

using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public static partial class CoverageMatrixRegistry
	{
		private static void AddStructCells(List<CoverageMatrixCell> list)
		{
			const CoverageFactKindFlag def = CoverageFactKindFlag.SymbolDefinition;
			const CoverageFactKindFlag refs = CoverageFactKindFlag.SymbolReference;

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Struct, SymbolKindTag.Struct,
				CoveragePositionTag.Declaration, CoverageScenarioId.CFR12Struct,
				def, true, "LSPNEW-T3-STR-01A", "struct decl cross-file def"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Struct, SymbolKindTag.Struct,
				CoveragePositionTag.TypeAnnotationModuleVar, CoverageScenarioId.CFR12Struct,
				refs, true, "LSPNEW-T3-STR-01B", "struct type annotation reference"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Struct, SymbolKindTag.Struct,
				CoveragePositionTag.StructLiteralType, CoverageScenarioId.SameFile,
				refs, false, string.Empty, "struct literal type usage (pending)"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.StructField, SymbolKindTag.StructField,
				CoveragePositionTag.Declaration, CoverageScenarioId.SameFile,
				def, true, "LSPNEW-T3-FLD-01A", "struct field decl fact"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.StructField, SymbolKindTag.StructField,
				CoveragePositionTag.FieldAccess, CoverageScenarioId.CFR15FieldDisambiguation,
				refs, true, "LSPNEW-T3-FLD-01B", "same-name fields stay disjoint"));
		}

		private static void AddEnumCells(List<CoverageMatrixCell> list)
		{
			const CoverageFactKindFlag def = CoverageFactKindFlag.SymbolDefinition;
			const CoverageFactKindFlag refs = CoverageFactKindFlag.SymbolReference;

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.Enum, SymbolKindTag.Enum,
				CoveragePositionTag.Declaration, CoverageScenarioId.CFR14EnumType,
				def, true, "LSPNEW-T3-ENM-01C", "enum type decl + type annotation refs"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.EnumMember, SymbolKindTag.EnumMember,
				CoveragePositionTag.Declaration, CoverageScenarioId.CFR16EnumMember,
				def, true, "LSPNEW-T3-ENM-01A", "enum member cross-file def"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.EnumMember, SymbolKindTag.EnumMember,
				CoveragePositionTag.EnumMemberAccess, CoverageScenarioId.CFR16EnumMember,
				refs, true, "LSPNEW-T3-ENM-01B", "enum member cross-file refs"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.EnumMember, SymbolKindTag.EnumMember,
				CoveragePositionTag.EnumMemberAccess, CoverageScenarioId.CFR15FieldDisambiguation,
				refs, true, "LSPNEW-T3-ENM-02A", "same-name enum members stay disjoint"));
		}

		private static void AddIncludeAndAliasCells(List<CoverageMatrixCell> list)
		{
			list.Add(new CoverageMatrixCell(
				CoverageUnitId.IncludeFile, SymbolKindTag.IncludeFile,
				CoveragePositionTag.IncludePath, CoverageScenarioId.CrossFileDirectInclude,
				CoverageFactKindFlag.IncludeEdge, true, "DBQ-03", "include edge + non-renameable"));

			list.Add(new CoverageMatrixCell(
				CoverageUnitId.AliasImport, SymbolKindTag.IncludeFile,
				CoveragePositionTag.AliasMember, CoverageScenarioId.AliasedInclude,
				CoverageFactKindFlag.AliasBinding | CoverageFactKindFlag.SymbolReference,
				false, string.Empty, "alias member resolution (tracked via T4)"));
		}

		private static void AddOverrideCells(List<CoverageMatrixCell> list)
		{
			list.Add(new CoverageMatrixCell(
				CoverageUnitId.OverrideDecl, SymbolKindTag.Function,
				CoveragePositionTag.Declaration, CoverageScenarioId.SameFile,
				CoverageFactKindFlag.SymbolDefinition, false, string.Empty,
				"override decl coverage pending dedicated test"));
		}
	}
}
