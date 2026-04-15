// Responsibility:
//   Decide diagnostic ownership routing rules across current/origin/hybrid file policies.
// Owns:
//   Ownership decision matrix and policy configuration surface.
// Inputs/Outputs:
//   In: diagnostic facts plus source/include provenance metadata.
//   Out: target file key(s) for publication.
// Allowed Dependencies:
//   - Infrastructure.Paths.PathKey
//   - Contracts.TextSpan
// Forbidden Dependencies:
//   - Compilation execution.
//   - Protocol publish transport.
// Invariants:
//   - Error and warning categories use one ownership policy pipeline.
//   - Ownership decision is deterministic for a fixed snapshot.
// Boundary Closure:
//   Upstream: compiler/semantic diagnostic producers.
//   Downstream: DiagnosticRouter.

using System.Collections.Generic;
using FFVM.Debug.Lsp.Contracts;
using FFVM.Debug.Lsp.Infrastructure.Paths;

namespace FFVM.Debug.Lsp.Diagnostics
{
	public enum DiagnosticOwnershipMode
	{
		CurrentFileOnly = 0,
		OriginFile = 1,
		Hybrid = 2
	}

	public sealed class DiagnosticItem
	{
		public PathKey CurrentDocumentKey { get; }
		public PathKey OriginDocumentKey { get; }
		public TextSpan Span { get; }
		public string Severity { get; }
		public string Message { get; }

		public DiagnosticItem(PathKey currentDocumentKey, PathKey originDocumentKey, TextSpan span, string severity, string message)
		{
			CurrentDocumentKey = currentDocumentKey;
			OriginDocumentKey = originDocumentKey;
			Span = span;
			Severity = severity ?? string.Empty;
			Message = message ?? string.Empty;
		}
	}

	public interface IDiagnosticOwnershipPolicy
	{
		DiagnosticOwnershipMode Mode { get; }

		IReadOnlyList<PathKey> ResolveOwners(DiagnosticItem item);
	}
}
