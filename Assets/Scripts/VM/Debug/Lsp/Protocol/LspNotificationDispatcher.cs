// Responsibility:
//   Route notification methods to side-effect handlers.
// Owns:
//   Notification-method registry and delivery contract.
// Inputs/Outputs:
//   In: parsed JSON-RPC notification envelope.
//   Out: workspace state updates and publish actions.
// Allowed Dependencies:
//   - Handlers namespace abstractions.
// Forbidden Dependencies:
//   - Query computation internals.
//   - Response-id formatting.
// Invariants:
//   - Notification handling is idempotent where protocol requires.
// Boundary Closure:
//   Upstream: LspServer message loop.
//   Downstream: document/index update handlers and diagnostics publication.

namespace FFVM.Debug.Lsp.Protocol
{
	public interface ILspNotificationDispatcher
	{
		bool TryDispatch(string method, object notificationParams, out string errorMessage);
	}
}
