// Responsibility:
//   In-memory sink for recording lifecycle state transitions.
// Owns:
//   Append-only lifecycle transition collection.
// Inputs/Outputs:
//   In: lifecycle transitions from orchestrator.
//   Out: queryable in-memory transition history.
// Allowed Dependencies:
//   - IDatabaseCommandLifecycleSink
// Forbidden Dependencies:
//   - Protocol output and semantic feature logic.
// Invariants:
//   - Records are preserved in insertion order.
// Boundary Closure:
//   Upstream: orchestrator lifecycle transitions.
//   Downstream: diagnostics/debug consumers.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryDatabaseCommandLifecycleSink : IDatabaseCommandLifecycleSink
	{
		private readonly object _sync = new object();
		private readonly List<DatabaseCommandStateTransition> _entries
			= new List<DatabaseCommandStateTransition>();

		public IReadOnlyList<DatabaseCommandStateTransition> Entries
		{
			get
			{
				lock (_sync)
				{
					return _entries.ToArray();
				}
			}
		}

		public void Record(DatabaseCommandStateTransition transition)
		{
			if (transition == null)
				return;

			lock (_sync)
			{
				_entries.Add(transition);
			}
		}

		public IReadOnlyList<DatabaseCommandStateTransition> GetByCommandId(string commandId)
		{
			lock (_sync)
			{
				if (string.IsNullOrEmpty(commandId))
					return Array.Empty<DatabaseCommandStateTransition>();

				var result = new List<DatabaseCommandStateTransition>();
				for (int i = 0; i < _entries.Count; i++)
				{
					if (string.Equals(_entries[i].CommandId, commandId, StringComparison.Ordinal))
						result.Add(_entries[i]);
				}

				return result;
			}
		}

		public void Clear()
		{
			lock (_sync)
			{
				_entries.Clear();
			}
		}
	}
}
