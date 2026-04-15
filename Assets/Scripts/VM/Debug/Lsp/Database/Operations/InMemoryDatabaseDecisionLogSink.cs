// Responsibility:
//   In-memory sink for structured decision logs.
// Owns:
//   Append-only decision-entry collection.
// Inputs/Outputs:
//   In: decision entries from orchestrator.
//   Out: queryable in-memory decision log history.
// Allowed Dependencies:
//   - IDatabaseDecisionLogSink
// Forbidden Dependencies:
//   - Protocol/log transport outside process boundary.
// Invariants:
//   - Entries are preserved in insertion order.
// Boundary Closure:
//   Upstream: orchestrator decision writes.
//   Downstream: diagnostics/debug consumers.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public sealed class InMemoryDatabaseDecisionLogSink : IDatabaseDecisionLogSink
	{
		private readonly object _sync = new object();
		private readonly List<DatabaseDecisionLogEntry> _entries
			= new List<DatabaseDecisionLogEntry>();

		public IReadOnlyList<DatabaseDecisionLogEntry> Entries
		{
			get
			{
				lock (_sync)
				{
					return _entries.ToArray();
				}
			}
		}

		public void Write(DatabaseDecisionLogEntry entry)
		{
			if (entry == null)
				return;

			lock (_sync)
			{
				_entries.Add(entry);
			}
		}

		public IReadOnlyList<DatabaseDecisionLogEntry> GetByCommandId(string commandId)
		{
			lock (_sync)
			{
				if (string.IsNullOrEmpty(commandId))
					return Array.Empty<DatabaseDecisionLogEntry>();

				var result = new List<DatabaseDecisionLogEntry>();
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
