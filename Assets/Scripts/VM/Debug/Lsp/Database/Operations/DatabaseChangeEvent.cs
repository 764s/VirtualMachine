// Responsibility:
//   Canonical change-event contract entering database write-side.
// Owns:
//   Event kind, target document identity, and opaque normalized payload.
// Inputs/Outputs:
//   In: protocol/document/file-watcher adapters.
//   Out: write pipeline input for snapshot regeneration.
// Allowed Dependencies:
//   - PathKey
// Forbidden Dependencies:
//   - Query and index read-side behavior.
// Invariants:
//   - Event kind is explicit and non-ambiguous.
//   - Payload is already normalized by adapters.
// Boundary Closure:
//   Upstream: LSP notification handlers.
//   Downstream: IWorkspaceCodeDatabase.Execute (ApplyChangeSet operation).

using FFVM.Debug.Lsp.Database.Paths;

namespace FFVM.Debug.Lsp.Database
{
	public enum DatabaseChangeKind
	{
		Unknown = 0,
		DocumentOpened,
		DocumentChanged,
		DocumentClosed,
		FileRenamed,
		WatchedFilesChanged,
		FullResyncRequested
	}

	public enum WatchedFileChangeType
	{
		Unknown = 0,
		Created = 1,
		Changed = 2,
		Deleted = 3
	}

	public abstract class DatabaseChangePayload
	{
		private sealed class EmptyDatabaseChangePayload : DatabaseChangePayload
		{
			public EmptyDatabaseChangePayload()
				: base(string.Empty)
			{
			}
		}

		public static DatabaseChangePayload Empty { get; } = new EmptyDatabaseChangePayload();

		public string DocumentUri { get; }

		protected DatabaseChangePayload(string documentUri)
		{
			DocumentUri = documentUri ?? string.Empty;
		}
	}

	public sealed class DocumentOpenedChangePayload : DatabaseChangePayload
	{
		public string LanguageId { get; }
		public string Text { get; }

		public DocumentOpenedChangePayload(string documentUri, string languageId, string text)
			: base(documentUri)
		{
			LanguageId = languageId ?? string.Empty;
			Text = text ?? string.Empty;
		}
	}

	public class DocumentChangedChangePayload : DatabaseChangePayload
	{
		public string Text { get; }

		public DocumentChangedChangePayload(string documentUri, string text)
			: base(documentUri)
		{
			Text = text ?? string.Empty;
		}
	}

	public sealed class DocumentMetadataChangePayload : DatabaseChangePayload
	{
		public string LanguageId { get; }
		public string Text { get; }

		public DocumentMetadataChangePayload(string documentUri, string languageId, string text)
			: base(documentUri)
		{
			LanguageId = languageId ?? string.Empty;
			Text = text ?? string.Empty;
		}
	}

	public sealed class DocumentClosedChangePayload : DatabaseChangePayload
	{
		public DocumentClosedChangePayload(string documentUri)
			: base(documentUri)
		{
		}
	}

	public sealed class FileRenamedChangePayload : DatabaseChangePayload
	{
		public string OldDocumentUri { get; }
		public string NewDocumentUri { get; }

		public FileRenamedChangePayload(string oldDocumentUri, string newDocumentUri)
			: base(newDocumentUri)
		{
			OldDocumentUri = oldDocumentUri ?? string.Empty;
			NewDocumentUri = newDocumentUri ?? string.Empty;
		}
	}

	public sealed class WatchedFileChangedChangePayload : DatabaseChangePayload
	{
		public WatchedFileChangeType ChangeType { get; }

		public WatchedFileChangedChangePayload(string documentUri, WatchedFileChangeType changeType)
			: base(documentUri)
		{
			ChangeType = changeType;
		}
	}

	public sealed class FullResyncRequestedChangePayload : DatabaseChangePayload
	{
		public FullResyncRequestedChangePayload()
			: base(string.Empty)
		{
		}
	}

	public sealed class DatabaseChangeEvent
	{
		public DatabaseChangeKind Kind { get; }
		public PathKey DocumentKey { get; }
		public int? VersionHint { get; }
		public DatabaseChangePayload Payload { get; }

		public DatabaseChangeEvent(DatabaseChangeKind kind, PathKey documentKey, int? versionHint, DatabaseChangePayload payload)
		{
			Kind = kind;
			DocumentKey = documentKey;
			VersionHint = versionHint;
			Payload = payload ?? DatabaseChangePayload.Empty;
		}
	}
}
