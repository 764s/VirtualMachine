// Responsibility:
//   Declares the row/column/scenario taxonomy for the executable T3 searchable-unit
//   coverage matrix. These enums are the mechanical schema that replaces hand-written
//   markdown matrices and drives CI validation.
// Owns:
//   CoverageUnitId (rows), CoveragePositionTag (columns), CoverageScenarioId (cases),
//   CoverageFactKindFlag (required DataFact signals per cell).
// Inputs/Outputs:
//   In: none (pure taxonomy).
//   Out: stable enum symbols consumed by CoverageMatrixRegistry and tests.
// Allowed Dependencies:
//   - None outside Contracts.
// Forbidden Dependencies:
//   - Parser/AST/query/protocol/transport.
// Invariants:
//   - Enum values are append-only and intentionally versioned.
//   - Each cell in the registry references one UnitId x PositionTag x ScenarioId tuple.
// Boundary Closure:
//   Upstream: matrix authoring (this registry).
//   Downstream: CoverageMatrixRegistry validation + coverage tests + report emitters.

using System;

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public enum CoverageUnitId
	{
		Unknown = 0,
		ModuleVar,
		LocalVar,
		Parameter,
		Function,
		ExternalFunction,
		Struct,
		StructField,
		Enum,
		EnumMember,
		IncludeFile,
		AliasImport,
		OverrideDecl,
	}

	public enum CoveragePositionTag
	{
		Unknown = 0,
		Declaration,
		TypeAnnotationModuleVar,
		TypeAnnotationParameter,
		TypeAnnotationLocalVar,
		TypeAnnotationStructField,
		StructLiteralType,
		FieldAccess,
		EnumMemberAccess,
		CallSite,
		IdentifierReference,
		IncludePath,
		AliasMember,
	}

	public enum CoverageScenarioId
	{
		Unknown = 0,
		SameFile,
		CrossFileDirectInclude,
		CrossFileTransitiveInclude,
		AliasedInclude,
		CFR11ModuleVar,
		CFR12Struct,
		CFR13StructField,
		CFR14EnumType,
		CFR15FieldDisambiguation,
		CFR16EnumMember,
		CFR17ExternalFunc,
	}

	[Flags]
	public enum CoverageFactKindFlag
	{
		None = 0,
		SymbolDefinition = 1 << 0,
		SymbolReference = 1 << 1,
		IncludeEdge = 1 << 2,
		AliasBinding = 1 << 3,
		TypeHint = 1 << 4,
		Token = 1 << 5,
	}
}
