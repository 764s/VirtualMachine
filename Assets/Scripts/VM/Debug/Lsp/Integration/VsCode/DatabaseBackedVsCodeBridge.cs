// Responsibility:
//   Default VS Code bridge implementation backed by workspace code database + query facade.
// Owns:
//   LSP param normalization and single-entry database/query invocation.
// Inputs/Outputs:
//   In: LSP JsonObject request/notification params.
//   Out: query payload objects and queued diagnostics notifications.
// Allowed Dependencies:
//   - IWorkspaceCodeDatabase
//   - ILspQueryFacade
// Forbidden Dependencies:
//   - Direct stdio framing/writing.
// Invariants:
//   - All writes go through IWorkspaceCodeDatabase.Execute.
//   - Read-side queries always run against a snapshot fetched from database.
// Boundary Closure:
//   Upstream: LspServerNew bridge callbacks.
//   Downstream: database operation/query components.

using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;

namespace FFVM.Debug.Lsp.Integration.VsCode
{
	public sealed class DatabaseBackedVsCodeBridge : ILspVsCodeDatabaseBridge
	{
		private readonly IWorkspaceCodeDatabase _database;
		private readonly ILspQueryFacade _queryFacade;
		private readonly object _sync = new object();
		private readonly Queue<LspPublishedDiagnostics> _diagnosticsQueue = new Queue<LspPublishedDiagnostics>();
		private const int DatabaseWriteTimeoutSeconds = 15;
		private const int SnapshotReadTimeoutSeconds = 10;
		private const string CorrelationPrefixLspNotify = "lsp-notify-";
		private const string CorrelationLspNotifyWatchedFiles = "lsp-notify-watched-files";
		private const string CorrelationLspReadSnapshot = "lsp-query-read-snapshot";
		private const string DocumentStreamPrefix = "doc:";

		private static class QueryOperationNames
		{
			public const string DocumentSymbol = "documentSymbol";
			public const string Hover = "hover";
			public const string Definition = "definition";
			public const string References = "references";
			public const string Completion = "completion";
			public const string SignatureHelp = "signatureHelp";
			public const string Rename = "rename";
			public const string PrepareRename = "prepareRename";
			public const string SemanticTokensFull = "semanticTokens/full";
		}

		private static class NotificationReasons
		{
			public const string DidOpen = "didOpen";
			public const string DidChange = "didChange";
			public const string DidClose = "didClose";
			public const string DidChangeWatchedFiles = "didChangeWatchedFiles";
		}

		private static class JsonFields
		{
			public const string Changes = "changes";
			public const string Uri = "uri";
			public const string Type = "type";
			public const string Context = "context";
			public const string IncludeDeclaration = "includeDeclaration";
			public const string NewName = "newName";
			public const string Position = "position";
			public const string Line = "line";
			public const string Character = "character";
			public const string TextDocument = "textDocument";
			public const string Version = "version";
			public const string LanguageId = "languageId";
			public const string Text = "text";
			public const string ContentChanges = "contentChanges";
			public const string OldUri = "oldUri";
			public const string OldPath = "oldPath";
			public const string NewUri = "newUri";
			public const string NewPath = "newPath";
		}

		private static class WatchedFileChangeTypeCodes
		{
			public const int Created = 1;
			public const int Changed = 2;
			public const int Deleted = 3;
		}

		private static class WatchedFileChangeTypeNames
		{
			public const string Created = "created";
			public const string Create = "create";
			public const string Changed = "changed";
			public const string Change = "change";
			public const string Deleted = "deleted";
			public const string Delete = "delete";
		}

		public DatabaseBackedVsCodeBridge(
			IWorkspaceCodeDatabase database,
			ILspQueryFacade queryFacade = null)
		{
			_database = database ?? throw new ArgumentNullException(nameof(database));
			_queryFacade = queryFacade ?? new InMemoryLspQueryFacade();
		}

		public void Initialize(JsonObject initializeParams)
		{
			// No-op in scaffold mode. Composition root may preload snapshot state separately.
		}

		public void Shutdown(JsonObject shutdownParams)
		{
		}

		public void Initialized(JsonObject initializedParams)
		{
		}

		public void Exit(JsonObject exitParams)
		{
		}

		public void DidOpen(JsonObject didOpenParams)
		{
			ApplySingleChange(DatabaseChangeKind.DocumentOpened, didOpenParams, NotificationReasons.DidOpen);
		}

		public void DidChange(JsonObject didChangeParams)
		{
			ApplySingleChange(DatabaseChangeKind.DocumentChanged, didChangeParams, NotificationReasons.DidChange);
		}

		public void DidClose(JsonObject didCloseParams)
		{
			ApplySingleChange(DatabaseChangeKind.DocumentClosed, didCloseParams, NotificationReasons.DidClose);
		}

		public void DidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams)
		{
			if (didChangeWatchedFilesParams == null)
				return;

			List<object> rawChanges = didChangeWatchedFilesParams.GetArray(JsonFields.Changes);
			if (rawChanges == null || rawChanges.Count == 0)
				return;

			var changes = new List<DatabaseChangeEvent>(rawChanges.Count);
			for (int i = 0; i < rawChanges.Count; i++)
			{
				if (!(rawChanges[i] is JsonObject item))
					continue;

				string uri = DocumentKeyNormalizer.Normalize(item.GetString(JsonFields.Uri));
				if (string.IsNullOrWhiteSpace(uri))
					continue;
				WatchedFileChangeType changeType = ParseWatchedFileChangeType(item.Get(JsonFields.Type));
				changes.Add(new DatabaseChangeEvent(
					DatabaseChangeKind.WatchedFilesChanged,
					new PathKey(uri),
					null,
					new WatchedFileChangedChangePayload(uri, changeType)));
			}

			if (changes.Count == 0)
				return;

			_database.Execute(DatabaseOperationRequest.ApplyChanges(
				changes,
				reason: NotificationReasons.DidChangeWatchedFiles,
				correlationId: CorrelationLspNotifyWatchedFiles,
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.Empty,
				streamBehavior: DatabaseOperationStreamBehavior.None));
		}

		public IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbol(JsonObject requestParams)
		{
			if (!TryReadSnapshot(out CodeDatabaseSnapshot snapshot))
				return new List<LspDocumentSymbolItem>(0);

			SymbolQueryRequest query = BuildQueryRequest(QueryOperationNames.DocumentSymbol, requestParams, false, string.Empty);
			return NormalizeDocumentSymbols(_queryFacade.QueryDocumentSymbols(snapshot, query));
		}

		public LspHoverPayload QueryHover(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.Hover, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryHover(snapshot, query));
			return NormalizeHover(payload != null ? payload.Hover : null);
		}

		public LspDefinitionPayload QueryDefinition(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.Definition, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryDefinition(snapshot, query));
			return NormalizeDefinition(payload != null ? payload.Definition : null);
		}

		public IReadOnlyList<LspReferenceItem> QueryReferences(JsonObject requestParams)
		{
			bool includeDeclaration = requestParams?.GetObject(JsonFields.Context)?.GetBool(JsonFields.IncludeDeclaration) == true;
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.References, requestParams, includeDeclaration, string.Empty, (snapshot, query)
				=> _queryFacade.QueryReferences(snapshot, query));
			return NormalizeReferences(payload != null ? payload.References : null);
		}

		public IReadOnlyList<LspCompletionItem> QueryCompletion(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.Completion, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryCompletion(snapshot, query));
			return payload != null && payload.CompletionItems != null
				? payload.CompletionItems
				: new List<LspCompletionItem>(0);
		}

		public LspSignatureHelpPayload QuerySignatureHelp(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.SignatureHelp, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QuerySignatureHelp(snapshot, query));
			return NormalizeSignatureHelp(payload != null ? payload.SignatureHelp : null);
		}

		public LspRenamePayload QueryRename(JsonObject requestParams)
		{
			string newName = requestParams != null ? requestParams.GetString(JsonFields.NewName) : string.Empty;
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.Rename, requestParams, true, newName, (snapshot, query)
				=> _queryFacade.QueryRename(snapshot, query));
			return NormalizeRename(payload != null ? payload.Rename : null);
		}

		public LspPrepareRenamePayload QueryPrepareRename(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(QueryOperationNames.PrepareRename, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryPrepareRename(snapshot, query));
			return payload != null ? payload.PrepareRename : null;
		}

		public LspSemanticTokensPayload QuerySemanticTokensFull(JsonObject requestParams)
		{
			if (!TryReadSnapshot(out CodeDatabaseSnapshot snapshot))
				return new LspSemanticTokensPayload(new List<int>(0), "Snapshot is unavailable.");

			SymbolQueryRequest query = BuildQueryRequest(QueryOperationNames.SemanticTokensFull, requestParams, false, string.Empty);
			return _queryFacade.QuerySemanticTokensFull(snapshot, query)
				?? new LspSemanticTokensPayload(new List<int>(0), string.Empty);
		}

		public JsonObject QueryWillRenameFiles(JsonObject requestParams)
		{
			// Workspace file-rename rewrite planning is intentionally deferred.
			return null;
		}

		public bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics)
		{
			lock (_sync)
			{
				if (_diagnosticsQueue.Count > 0)
				{
					diagnostics = _diagnosticsQueue.Dequeue();
					return true;
				}

				diagnostics = null;
				return false;
			}
		}

		private void ApplySingleChange(DatabaseChangeKind kind, JsonObject payload, string reason)
		{
			string uri = ExtractDocumentUri(payload);
			int? version = ExtractVersion(payload);

			var change = new DatabaseChangeEvent(
				kind,
				new PathKey(uri),
				version,
				BuildChangePayload(kind, payload, uri));

			_database.Execute(DatabaseOperationRequest.ApplyChanges(
				new[] { change },
				reason: reason,
				correlationId: CorrelationPrefixLspNotify + reason,
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.IsNullOrWhiteSpace(uri) ? string.Empty : DocumentStreamPrefix + uri,
				streamBehavior: string.IsNullOrWhiteSpace(uri)
					? DatabaseOperationStreamBehavior.None
					: DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded));
		}

		private SymbolQueryPayload ExecutePayloadQuery(
			string operation,
			JsonObject requestParams,
			bool includeDeclaration,
			string newName,
			Func<CodeDatabaseSnapshot, SymbolQueryRequest, SymbolQueryResult> query)
		{
			if (query == null)
				return null;

			if (!TryReadSnapshot(out CodeDatabaseSnapshot snapshot))
				return null;

			SymbolQueryRequest normalized = BuildQueryRequest(operation, requestParams, includeDeclaration, newName);
			SymbolQueryResult result = query(snapshot, normalized);

			if (result == null)
				return null;

			return result.Succeeded ? result.Payload : null;
		}

		private static LspDefinitionPayload NormalizeDefinition(LspDefinitionPayload payload)
		{
			if (payload == null)
				return null;

			return new LspDefinitionPayload(
				DocumentKeyNormalizer.Normalize(payload.DocumentKey),
				payload.Span,
				payload.SourcePayload);
		}

		private static IReadOnlyList<LspReferenceItem> NormalizeReferences(IReadOnlyList<LspReferenceItem> items)
		{
			if (items == null || items.Count == 0)
				return new List<LspReferenceItem>(0);

			var output = new List<LspReferenceItem>(items.Count);
			for (int i = 0; i < items.Count; i++)
			{
				LspReferenceItem item = items[i];
				if (item == null)
					continue;

				output.Add(new LspReferenceItem(
					DocumentKeyNormalizer.Normalize(item.DocumentKey),
					item.Span,
					item.SourcePayload));
			}

			return output;
		}

		private static LspHoverPayload NormalizeHover(LspHoverPayload payload)
		{
			if (payload == null)
				return null;

			return new LspHoverPayload(
				payload.Summary,
				payload.Scope,
				payload.ParentName,
				DocumentKeyNormalizer.Normalize(payload.Origin));
		}

		private static LspSignatureHelpPayload NormalizeSignatureHelp(LspSignatureHelpPayload payload)
		{
			if (payload == null)
				return null;

			IReadOnlyList<LspSignatureItem> signatures = payload.Signatures;
			if (signatures == null || signatures.Count == 0)
				return new LspSignatureHelpPayload(new List<LspSignatureItem>(0), payload.ActiveSignature, payload.ActiveParameter);

			var output = new List<LspSignatureItem>(signatures.Count);
			for (int i = 0; i < signatures.Count; i++)
			{
				LspSignatureItem item = signatures[i];
				if (item == null)
					continue;

				output.Add(new LspSignatureItem(item.Label, DocumentKeyNormalizer.Normalize(item.Source)));
			}

			return new LspSignatureHelpPayload(output, payload.ActiveSignature, payload.ActiveParameter);
		}

		private static LspRenamePayload NormalizeRename(LspRenamePayload payload)
		{
			if (payload == null)
				return null;

			IReadOnlyList<LspRenameEdit> edits = payload.Edits;
			if (edits == null || edits.Count == 0)
				return new LspRenamePayload(payload.NewName, new List<LspRenameEdit>(0));

			var output = new List<LspRenameEdit>(edits.Count);
			for (int i = 0; i < edits.Count; i++)
			{
				LspRenameEdit edit = edits[i];
				if (edit == null)
					continue;

				output.Add(new LspRenameEdit(
					DocumentKeyNormalizer.Normalize(edit.DocumentKey),
					edit.Range,
					edit.NewText));
			}

			return new LspRenamePayload(payload.NewName, output);
		}

		private static IReadOnlyList<LspDocumentSymbolItem> NormalizeDocumentSymbols(IReadOnlyList<LspDocumentSymbolItem> symbols)
		{
			if (symbols == null || symbols.Count == 0)
				return new List<LspDocumentSymbolItem>(0);

			var output = new List<LspDocumentSymbolItem>(symbols.Count);
			for (int i = 0; i < symbols.Count; i++)
			{
				LspDocumentSymbolItem symbol = symbols[i];
				if (symbol == null)
					continue;

				output.Add(new LspDocumentSymbolItem(
					symbol.Name,
					symbol.Kind,
					symbol.Scope,
					symbol.ParentName,
					DocumentKeyNormalizer.Normalize(symbol.Origin),
					symbol.DeclarationSpan));
			}

			return output;
		}

		private bool TryReadSnapshot(out CodeDatabaseSnapshot snapshot)
		{
			snapshot = null;

			DatabaseOperationResult readResult = _database.Execute(DatabaseOperationRequest.ReadSnapshot(
				correlationId: CorrelationLspReadSnapshot,
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(SnapshotReadTimeoutSeconds)));

			if (readResult == null || !readResult.Succeeded || readResult.Snapshot == null)
				return false;

			snapshot = readResult.Snapshot;
			return true;
		}

		private static SymbolQueryRequest BuildQueryRequest(
			string operation,
			JsonObject requestParams,
			bool includeDeclaration,
			string newName)
		{
			string uri = ExtractDocumentUri(requestParams);

			JsonObject position = requestParams != null ? requestParams.GetObject(JsonFields.Position) : null;
			int line = position != null ? position.GetInt(JsonFields.Line) : 0;
			int character = position != null ? position.GetInt(JsonFields.Character) : 0;

			var normalizedPosition = new TextPosition(line, character);
			return new SymbolQueryRequest(
				operation,
				uri,
				normalizedPosition,
				new TextSpan(0, 0),
				includeDeclaration,
				newName);
		}

		private static string ExtractDocumentUri(JsonObject payload)
		{
			if (payload == null)
				return string.Empty;

			JsonObject textDocument = payload.GetObject(JsonFields.TextDocument);
			if (textDocument != null)
			{
				string uri = DocumentKeyNormalizer.Normalize(textDocument.GetString(JsonFields.Uri));
				if (!string.IsNullOrWhiteSpace(uri))
					return uri;
			}

			return DocumentKeyNormalizer.Normalize(payload.GetString(JsonFields.Uri));
		}

		private static int? ExtractVersion(JsonObject payload)
		{
			JsonObject textDocument = payload != null ? payload.GetObject(JsonFields.TextDocument) : null;
			if (textDocument != null && textDocument.ContainsKey(JsonFields.Version))
				return textDocument.GetInt(JsonFields.Version);

			if (payload != null && payload.ContainsKey(JsonFields.Version))
				return payload.GetInt(JsonFields.Version);

			return null;
		}

		private static DatabaseChangePayload BuildChangePayload(DatabaseChangeKind kind, JsonObject payload, string uri)
		{
			switch (kind)
			{
				case DatabaseChangeKind.DocumentOpened:
					return new DocumentOpenedChangePayload(uri, ExtractLanguageId(payload), ExtractText(payload));

				case DatabaseChangeKind.DocumentChanged:
					return new DocumentChangedChangePayload(uri, ExtractText(payload));

				case DatabaseChangeKind.DocumentClosed:
					return new DocumentClosedChangePayload(uri);

				case DatabaseChangeKind.FileRenamed:
					if (TryExtractRenamePayload(payload, out string oldUri, out string newUri))
						return new FileRenamedChangePayload(oldUri, newUri);
					return DatabaseChangePayload.Empty;

				case DatabaseChangeKind.FullResyncRequested:
					return new FullResyncRequestedChangePayload();

				default:
					return new DocumentMetadataChangePayload(uri, ExtractLanguageId(payload), ExtractText(payload));
			}
		}

		private static string ExtractLanguageId(JsonObject payload)
		{
			JsonObject textDocument = payload != null ? payload.GetObject(JsonFields.TextDocument) : null;
			if (textDocument == null)
				return string.Empty;

			string languageId = textDocument.GetString(JsonFields.LanguageId);
			return languageId ?? string.Empty;
		}

		private static string ExtractText(JsonObject payload)
		{
			if (payload == null)
				return string.Empty;

			JsonObject textDocument = payload.GetObject(JsonFields.TextDocument);
			if (textDocument != null)
			{
				string embeddedText = textDocument.GetString(JsonFields.Text);
				if (embeddedText != null)
					return embeddedText;
			}

			List<object> contentChanges = payload.GetArray(JsonFields.ContentChanges);
			if (contentChanges != null && contentChanges.Count > 0)
			{
				for (int i = contentChanges.Count - 1; i >= 0; i--)
				{
					if (!(contentChanges[i] is JsonObject changed))
						continue;

					string candidate = changed.GetString(JsonFields.Text);
					if (candidate != null)
						return candidate;
				}
			}

			string directText = payload.GetString(JsonFields.Text);
			return directText ?? string.Empty;
		}

		private static bool TryExtractRenamePayload(JsonObject payload, out string oldUri, out string newUri)
		{
			oldUri = string.Empty;
			newUri = string.Empty;
			if (payload == null)
				return false;

			oldUri = DocumentKeyNormalizer.Normalize(payload.GetString(JsonFields.OldUri) ?? payload.GetString(JsonFields.OldPath));
			newUri = DocumentKeyNormalizer.Normalize(payload.GetString(JsonFields.NewUri) ?? payload.GetString(JsonFields.NewPath));
			return !string.IsNullOrWhiteSpace(oldUri) && !string.IsNullOrWhiteSpace(newUri);
		}

		private static WatchedFileChangeType ParseWatchedFileChangeType(object rawType)
		{
			if (rawType is int intType)
				return MapWatchedFileChangeType(intType);

			if (rawType is long longType)
				return MapWatchedFileChangeType((int)longType);

			if (rawType is double doubleType)
				return MapWatchedFileChangeType((int)doubleType);

			if (rawType is string textType)
			{
				if (int.TryParse(textType, out int parsed))
					return MapWatchedFileChangeType(parsed);

				if (string.Equals(textType, WatchedFileChangeTypeNames.Created, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(textType, WatchedFileChangeTypeNames.Create, StringComparison.OrdinalIgnoreCase))
				{
					return WatchedFileChangeType.Created;
				}

				if (string.Equals(textType, WatchedFileChangeTypeNames.Changed, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(textType, WatchedFileChangeTypeNames.Change, StringComparison.OrdinalIgnoreCase))
				{
					return WatchedFileChangeType.Changed;
				}

				if (string.Equals(textType, WatchedFileChangeTypeNames.Deleted, StringComparison.OrdinalIgnoreCase)
					|| string.Equals(textType, WatchedFileChangeTypeNames.Delete, StringComparison.OrdinalIgnoreCase))
				{
					return WatchedFileChangeType.Deleted;
				}
			}

			return WatchedFileChangeType.Unknown;
		}

		private static WatchedFileChangeType MapWatchedFileChangeType(int type)
		{
			switch (type)
			{
				case WatchedFileChangeTypeCodes.Created:
					return WatchedFileChangeType.Created;
				case WatchedFileChangeTypeCodes.Changed:
					return WatchedFileChangeType.Changed;
				case WatchedFileChangeTypeCodes.Deleted:
					return WatchedFileChangeType.Deleted;
				default:
					return WatchedFileChangeType.Unknown;
			}
		}
	}
}
