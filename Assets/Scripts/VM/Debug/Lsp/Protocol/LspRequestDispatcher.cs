// Responsibility:
//   Route request methods to typed handlers.
// Owns:
//   Request-method registry and invocation contract.
// Inputs/Outputs:
//   In: parsed JSON-RPC request envelope.
//   Out: handler invocation result envelope.
// Allowed Dependencies:
//   - Handlers namespace abstractions.
//   - LspResponseWriter
// Forbidden Dependencies:
//   - Symbol query semantics.
//   - Diagnostic policy decisions.
// Invariants:
//   - Unknown methods are handled through one explicit fallback path.
// Boundary Closure:
//   Upstream: LspServer message loop.
//   Downstream: operation handlers and response writer.

namespace FFVM.Debug.Lsp.Protocol
{
	public interface ILspRequestDispatcher
	{
		bool TryDispatch(string method, object requestParams, out object result, out string errorMessage);
	}
}
