// Responsibility:
//   Serialize responses/notifications to JSON-RPC wire format.
// Owns:
//   Envelope writing, id compatibility, and output framing policy.
// Inputs/Outputs:
//   In: handler/query/diagnostic result models.
//   Out: JSON-RPC compliant payload bytes/strings.
// Allowed Dependencies:
//   - Contracts result models and protocol DTOs.
// Forbidden Dependencies:
//   - Business semantics for symbol resolution.
//   - Workspace mutation logic.
// Invariants:
//   - Wire formatting is pure and side-effect free except write IO.
//   - Request id types are preserved by compatibility policy.
// Boundary Closure:
//   Upstream: request/notification dispatchers.
//   Downstream: stream writer in LspServer.

namespace FFVM.Debug.Lsp.Protocol
{
	public interface ILspResponseWriter
	{
		string WriteSuccess(object id, object result);

		string WriteError(object id, int code, string message, object data);

		string WriteNotification(string method, object notificationParams);
	}
}
