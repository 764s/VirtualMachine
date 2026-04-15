// Responsibility:
//   Single operation contract surface for the workspace database equivalent.
// Owns:
//   Operation kind taxonomy and request/result envelopes.
// Inputs/Outputs:
//   In: normalized operation requests from write/read-side orchestrators.
//   Out: operation result with snapshot and version transition metadata.
// Allowed Dependencies:
//   - DatabaseChangeEvent
//   - CodeDatabaseSnapshot
// Forbidden Dependencies:
//   - Protocol layer and JSON-RPC payload formatting.
//   - Feature-specific query/semantic behaviors.
// Invariants:
//   - Each operation request has one explicit kind.
//   - Result always carries operation kind and final snapshot visibility.
//   - External callers interact only via IWorkspaceCodeDatabase.Execute.
//   - Task planner/task center are internal execution details, not extra public write entrypoints.
// Boundary Closure:
//   Upstream: handlers, adapters, and composition root orchestration.
//   Downstream: IWorkspaceCodeDatabase.Execute.

using System;
using System.Collections.Generic;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseOperationKind
	{
		Unknown = 0,
		ReadSnapshot,
		ApplyChangeSet,
		ReplaceSnapshot,
		ResetDatabase
	}

	public enum DatabaseOperationPriority
	{
		Low = 0,
		Normal = 1,
		High = 2,
		Critical = 3
	}

	public enum DatabaseOperationStreamBehavior
	{
		None = 0,
		Coalesce,
		CoalesceAndCancelSuperseded
	}

	public sealed class DatabaseOperationRequest
	{
		private static readonly IReadOnlyList<DatabaseChangeEvent> EmptyChanges = new List<DatabaseChangeEvent>(0);

		public string CommandId { get; }
		public string CorrelationId { get; }
		public DatabaseOperationPriority Priority { get; }
		public TimeSpan? Timeout { get; }
		public DateTime CreatedAtUtc { get; }
		public string StreamKey { get; }
		public DatabaseOperationStreamBehavior StreamBehavior { get; }
		public DatabaseOperationKind Kind { get; }
		public IReadOnlyList<DatabaseChangeEvent> Changes { get; }
		public CodeDatabaseSnapshot ReplacementSnapshot { get; }
		public long? ExpectedVersion { get; }
		public string Reason { get; }

		private DatabaseOperationRequest(
			string commandId,
			string correlationId,
			DatabaseOperationPriority priority,
			TimeSpan? timeout,
			DateTime createdAtUtc,
			string streamKey,
			DatabaseOperationStreamBehavior streamBehavior,
			DatabaseOperationKind kind,
			IReadOnlyList<DatabaseChangeEvent> changes,
			CodeDatabaseSnapshot replacementSnapshot,
			long? expectedVersion,
			string reason)
		{
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			Priority = priority;
			Timeout = timeout;
			CreatedAtUtc = createdAtUtc;
			StreamKey = streamKey ?? string.Empty;
			StreamBehavior = streamBehavior;
			Kind = kind;
			Changes = changes ?? EmptyChanges;
			ReplacementSnapshot = replacementSnapshot;
			ExpectedVersion = expectedVersion;
			Reason = reason ?? string.Empty;
		}

		public static DatabaseOperationRequest ReadSnapshot(
			long? expectedVersion = null,
			string correlationId = null,
			DatabaseOperationPriority priority = DatabaseOperationPriority.Normal,
			TimeSpan? timeout = null)
		{
			return new DatabaseOperationRequest(
				CreateCommandId(),
				correlationId,
				priority,
				timeout,
				DateTime.UtcNow,
				string.Empty,
				DatabaseOperationStreamBehavior.None,
				DatabaseOperationKind.ReadSnapshot,
				EmptyChanges,
				null,
				expectedVersion,
				string.Empty);
		}

		public static DatabaseOperationRequest ApplyChanges(
			IReadOnlyList<DatabaseChangeEvent> changes,
			long? expectedVersion = null,
			string reason = null,
			string correlationId = null,
			DatabaseOperationPriority priority = DatabaseOperationPriority.Normal,
			TimeSpan? timeout = null,
			string streamKey = null,
			DatabaseOperationStreamBehavior streamBehavior = DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded,
			DateTime? createdAtUtc = null)
		{
			return new DatabaseOperationRequest(
				CreateCommandId(),
				correlationId,
				priority,
				timeout,
				createdAtUtc ?? DateTime.UtcNow,
				streamKey,
				streamBehavior,
				DatabaseOperationKind.ApplyChangeSet,
				changes,
				null,
				expectedVersion,
				reason);
		}

		public static DatabaseOperationRequest ReplaceSnapshot(
			CodeDatabaseSnapshot snapshot,
			long? expectedVersion = null,
			string reason = null,
			string correlationId = null,
			DatabaseOperationPriority priority = DatabaseOperationPriority.Normal,
			TimeSpan? timeout = null)
		{
			return new DatabaseOperationRequest(
				CreateCommandId(),
				correlationId,
				priority,
				timeout,
				DateTime.UtcNow,
				string.Empty,
				DatabaseOperationStreamBehavior.None,
				DatabaseOperationKind.ReplaceSnapshot,
				EmptyChanges,
				snapshot,
				expectedVersion,
				reason);
		}

		public static DatabaseOperationRequest Reset(
			string reason = null,
			string correlationId = null,
			DatabaseOperationPriority priority = DatabaseOperationPriority.Normal,
			TimeSpan? timeout = null)
		{
			return new DatabaseOperationRequest(
				CreateCommandId(),
				correlationId,
				priority,
				timeout,
				DateTime.UtcNow,
				string.Empty,
				DatabaseOperationStreamBehavior.None,
				DatabaseOperationKind.ResetDatabase,
				EmptyChanges,
				null,
				null,
				reason);
		}

		private static string CreateCommandId()
		{
			return Guid.NewGuid().ToString("N");
		}

		public bool IsShapeValid(out string error)
		{
			error = null;

			if (string.IsNullOrWhiteSpace(CommandId))
			{
				error = "CommandId is required.";
				return false;
			}

			if (Timeout.HasValue && Timeout.Value <= TimeSpan.Zero)
			{
				error = "Timeout must be a positive duration when specified.";
				return false;
			}

			if (CreatedAtUtc == default(DateTime))
			{
				error = "CreatedAtUtc is required.";
				return false;
			}

			if (Kind != DatabaseOperationKind.ApplyChangeSet)
			{
				if (StreamBehavior != DatabaseOperationStreamBehavior.None || !string.IsNullOrEmpty(StreamKey))
				{
					error = "Only ApplyChangeSet supports stream behavior metadata.";
					return false;
				}
			}
			else
			{
				if (StreamBehavior != DatabaseOperationStreamBehavior.None && string.IsNullOrWhiteSpace(StreamKey))
				{
					error = "ApplyChangeSet with stream behavior requires StreamKey.";
					return false;
				}
			}

			switch (Kind)
			{
				case DatabaseOperationKind.ReadSnapshot:
				case DatabaseOperationKind.ResetDatabase:
					if (Changes.Count != 0 || ReplacementSnapshot != null)
					{
						error = "ReadSnapshot/ResetDatabase must not carry changes or replacement snapshot.";
						return false;
					}
					return true;

				case DatabaseOperationKind.ApplyChangeSet:
					if (Changes.Count == 0)
					{
						error = "ApplyChangeSet requires at least one change event.";
						return false;
					}
					if (ReplacementSnapshot != null)
					{
						error = "ApplyChangeSet must not carry a replacement snapshot.";
						return false;
					}
					return true;

				case DatabaseOperationKind.ReplaceSnapshot:
					if (ReplacementSnapshot == null)
					{
						error = "ReplaceSnapshot requires a replacement snapshot.";
						return false;
					}
					if (Changes.Count != 0)
					{
						error = "ReplaceSnapshot must not carry change events.";
						return false;
					}
					return true;

				default:
					error = "Unknown operation kind.";
					return false;
			}
		}
	}

	public sealed class DatabaseOperationResult
	{
		public bool Succeeded { get; }
		public string CommandId { get; }
		public string CorrelationId { get; }
		public string StreamKey { get; }
		public DatabaseOperationPriority Priority { get; }
		public DatabaseOperationKind Kind { get; }
		public DatabaseCommandState FinalState { get; }
		public long PreviousVersion { get; }
		public long CurrentVersion { get; }
		public CodeDatabaseSnapshot Snapshot { get; }
		public string Message { get; }

		public DatabaseOperationResult(
			bool succeeded,
			string commandId,
			string correlationId,
			string streamKey,
			DatabaseOperationPriority priority,
			DatabaseOperationKind kind,
			DatabaseCommandState finalState,
			long previousVersion,
			long currentVersion,
			CodeDatabaseSnapshot snapshot,
			string message)
		{
			Succeeded = succeeded;
			CommandId = commandId ?? string.Empty;
			CorrelationId = correlationId ?? string.Empty;
			StreamKey = streamKey ?? string.Empty;
			Priority = priority;
			Kind = kind;
			FinalState = finalState;
			PreviousVersion = previousVersion;
			CurrentVersion = currentVersion;
			Snapshot = snapshot;
			Message = message ?? string.Empty;
		}

		public static DatabaseOperationResult Success(
			DatabaseOperationRequest request,
			long previousVersion,
			long currentVersion,
			CodeDatabaseSnapshot snapshot,
			string message = null,
			DatabaseCommandState finalState = DatabaseCommandState.Completed)
		{
			return new DatabaseOperationResult(
				true,
				request?.CommandId,
				request?.CorrelationId,
				request?.StreamKey,
				request != null ? request.Priority : DatabaseOperationPriority.Normal,
				request != null ? request.Kind : DatabaseOperationKind.Unknown,
				finalState,
				previousVersion,
				currentVersion,
				snapshot,
				message);
		}

		public static DatabaseOperationResult Failure(
			DatabaseOperationRequest request,
			long previousVersion,
			CodeDatabaseSnapshot snapshot,
			string message,
			DatabaseCommandState finalState = DatabaseCommandState.Failed)
		{
			return new DatabaseOperationResult(
				false,
				request?.CommandId,
				request?.CorrelationId,
				request?.StreamKey,
				request != null ? request.Priority : DatabaseOperationPriority.Normal,
				request != null ? request.Kind : DatabaseOperationKind.Unknown,
				finalState,
				previousVersion,
				previousVersion,
				snapshot,
				message);
		}
	}
}
