// Responsibility:
//   Closed symbol-kind taxonomy for the LSP semantic layer.
// Owns:
//   Kind tags and compatibility mapping rules only.
// Inputs/Outputs:
//   In: parser/index inferred symbol category.
//   Out: normalized kind tag used across all LSP features.
// Allowed Dependencies:
//   - None outside Contracts.
// Forbidden Dependencies:
//   - Any query, protocol, or diagnostics behavior.
// Invariants:
//   - Kind values are stable and versioned intentionally.
//   - New kinds require explicit mapping tests.
// Boundary Closure:
//   Upstream: parser/index classification.
//   Downstream: SymbolIdentity, completion, hover, semantic tokens.

namespace FFVM.Debug.Lsp.Database.Contracts
{
	public enum SymbolKindTag
	{
		Unknown = 0,
		Function,
		Variable,
		Struct,
		Parameter,
		Enum,
		IncludeFile,
		StructField,
		EnumMember
	}
}
