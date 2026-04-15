// Responsibility:
//   Single normalization service for raw path/URI inputs and conversions.
// Owns:
//   Canonicalization policy: separators, casing, escaping, URI/path conversions.
// Inputs/Outputs:
//   In: raw path/URI strings from OS, compiler, and protocol.
//   Out: PathKey and UriKey values.
// Allowed Dependencies:
//   - PathKey
//   - UriKey
// Forbidden Dependencies:
//   - Symbol semantics or diagnostic ownership rules.
//   - Protocol dispatch.
// Invariants:
//   - Same physical file always yields the same canonical key pair.
//   - No component compares raw path strings directly.
// Boundary Closure:
//   Upstream: LspServer input adapters, watched files events.
//   Downstream: index, query request normalization, diagnostics routing.

namespace FFVM.Debug.Lsp.Database.Paths
{
	public interface IPathCanonicalizer
	{
		PathKey ToPathKey(string rawPath);
		UriKey ToUriKey(string rawUri);
		UriKey ToUriKey(PathKey pathKey);
		PathKey ToPathKey(UriKey uriKey);
	}
}
