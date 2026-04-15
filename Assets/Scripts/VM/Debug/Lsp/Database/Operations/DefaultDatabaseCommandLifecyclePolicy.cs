// Responsibility:
//   Default lifecycle transition policy for command-state state machine.
// Owns:
//   Allowed transition matrix between command states.
// Inputs/Outputs:
//   In: from/to command states.
//   Out: transition allowance and error reason.
// Allowed Dependencies:
//   - IDatabaseCommandLifecyclePolicy
// Forbidden Dependencies:
//   - Feature-level query or protocol concerns.
// Invariants:
//   - Terminal states cannot transition to non-terminal states.
// Boundary Closure:
//   Upstream: execution orchestrator.
//   Downstream: lifecycle transition recording.

namespace FFVM.Debug.Lsp.Database
{
	public sealed class DefaultDatabaseCommandLifecyclePolicy : IDatabaseCommandLifecyclePolicy
	{
		public bool CanTransition(DatabaseCommandState fromState, DatabaseCommandState toState, out string error)
		{
			error = null;

			if (toState == DatabaseCommandState.Unknown)
			{
				error = "Target state cannot be Unknown.";
				return false;
			}

			if (fromState == toState)
				return true;

			if (IsTerminal(fromState))
			{
				error = "Terminal state cannot transition to another state.";
				return false;
			}

			bool allowed;
			switch (fromState)
			{
				case DatabaseCommandState.Created:
					allowed =
						toState == DatabaseCommandState.Admitted ||
						toState == DatabaseCommandState.Rejected ||
						toState == DatabaseCommandState.Canceled ||
						toState == DatabaseCommandState.TimedOut ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Admitted:
					allowed =
						toState == DatabaseCommandState.Planned ||
						toState == DatabaseCommandState.Rejected ||
						toState == DatabaseCommandState.Canceled ||
						toState == DatabaseCommandState.TimedOut ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Planned:
					allowed =
						toState == DatabaseCommandState.Enqueued ||
						toState == DatabaseCommandState.Canceled ||
						toState == DatabaseCommandState.TimedOut ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Enqueued:
					allowed =
						toState == DatabaseCommandState.Executing ||
						toState == DatabaseCommandState.Canceled ||
						toState == DatabaseCommandState.TimedOut ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Executing:
					allowed =
						toState == DatabaseCommandState.Composed ||
						toState == DatabaseCommandState.Canceled ||
						toState == DatabaseCommandState.TimedOut ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Composed:
					allowed =
						toState == DatabaseCommandState.Committed ||
						toState == DatabaseCommandState.Failed;
					break;

				case DatabaseCommandState.Committed:
					allowed =
						toState == DatabaseCommandState.Completed ||
						toState == DatabaseCommandState.Failed;
					break;

				default:
					allowed = false;
					break;
			}

			if (!allowed)
				error = "Transition is not allowed by default lifecycle policy.";

			return allowed;
		}

		private static bool IsTerminal(DatabaseCommandState state)
		{
			return state == DatabaseCommandState.Completed
				|| state == DatabaseCommandState.Rejected
				|| state == DatabaseCommandState.Canceled
				|| state == DatabaseCommandState.TimedOut
				|| state == DatabaseCommandState.Failed;
		}
	}
}
