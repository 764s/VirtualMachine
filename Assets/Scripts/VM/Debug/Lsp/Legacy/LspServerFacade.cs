// Responsibility:
//   Transitional facade that bridges existing LspServer composition to new modules.
// Owns:
//   Backward-compatible wiring and phased migration seams.
// Inputs/Outputs:
//   In: existing LspServer dependencies and runtime callbacks.
//   Out: calls into Protocol/Handlers/Query/Diagnostics modules.
// Allowed Dependencies:
//   - Protocol dispatchers
//   - Handlers
//   - Query and diagnostics services
// Forbidden Dependencies:
//   - New semantic logic not shared by modular services.
//   - Duplicate implementations of migrated features.
// Invariants:
//   - Behavior parity is maintained during migration phases.
//   - Facade shrinks as legacy code is removed.
// Boundary Closure:
//   Upstream: LspServer entrypoint.
//   Downstream: all new modular components.

using FFVM.Debug.Lsp.Protocol;

namespace FFVM.Debug.Lsp.Legacy
{
	public interface ILspServerFacade
	{
		void Bind(
			ILspRequestDispatcher requestDispatcher,
			ILspNotificationDispatcher notificationDispatcher,
			ILspResponseWriter responseWriter);

		bool TryHandleRequest(string method, object id, object requestParams, out string responseJson);

		bool TryHandleNotification(string method, object notificationParams, out string errorMessage);
	}
}
