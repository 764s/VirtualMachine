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
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using FFVM.Compiler;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;
using FFVM.Debug.Tooling;

namespace FFVM.Debug.Lsp.Integration.VsCode
{
	public sealed class DatabaseBackedVsCodeBridge : ILspVsCodeDatabaseBridge
	{
		private sealed class OpenDocumentState
		{
			public OpenDocumentState(string uri, string languageId, string text, int? version)
			{
				Uri = uri ?? string.Empty;
				LanguageId = languageId ?? string.Empty;
				Text = text ?? string.Empty;
				Version = version;
			}

			public string Uri { get; set; }
			public string LanguageId { get; set; }
			public string Text { get; set; }
			public int? Version { get; set; }
		}

		private sealed class AppliedTextEdit
		{
			public int StartIndex { get; }
			public int EndIndex { get; }
			public string NewText { get; }

			public AppliedTextEdit(int startIndex, int endIndex, string newText)
			{
				StartIndex = startIndex;
				EndIndex = endIndex;
				NewText = newText ?? string.Empty;
			}
		}

		private readonly IWorkspaceCodeDatabase _database;
		private readonly ILspQueryFacade _queryFacade;
		private readonly object _sync = new object();
		private readonly Queue<LspPublishedDiagnostics> _diagnosticsQueue = new Queue<LspPublishedDiagnostics>();
		private readonly Queue<LspClientRequest> _clientRequestQueue = new Queue<LspClientRequest>();
		private readonly Dictionary<string, OpenDocumentState> _openDocuments = new Dictionary<string, OpenDocumentState>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, string> _renameUriMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		private string _rootPath;
		private ProjectFile _projectFile;
		private bool _ffprojPromptQueued;
		private const int DatabaseWriteTimeoutSeconds = 15;
		private const int SnapshotReadTimeoutSeconds = 10;
		private const string CorrelationPrefixLspNotify = "lsp-notify-";
		private const string CorrelationLspReadSnapshot = "lsp-query-read-snapshot";
		private const string CorrelationWillRenameState = "lsp-willrename-state";
		private const string DocumentStreamPrefix = "doc:";
		private const string IncludeKeyword = "include";
		private const int IncludePathPrefixLength = 2;
		private const int DiagnosticSeverityError = 1;
		private const int DiagnosticSeverityHint = 4;
		private const string DiagnosticSourceDefault = "ffvm";
		private const string DiagnosticMessageFallback = "Unknown diagnostic.";
		private const int MessageTypeInfo = 3;
		private const string ClientMethodShowMessageRequest = "window/showMessageRequest";
		private const string ClientMethodApplyEdit = "workspace/applyEdit";
		private const string RequestTokenCreateFfprojOffer = "bootstrap-create-ffproj-offer";
		private const string ActionTitleCreate = "Create";
		private const string ActionTitleIgnore = "Ignore";
		private const string ActionTitleNeverAsk = "Don't ask again";
		private const string CreateFileOperationKind = "create";
		private const string ShowMessagePromptText = "Detected FFScript files but no project configuration. Create .ffproj?";
		private const string WorkspaceScriptSearchPattern = "*.ffs";
		private const string WorkspaceBootstrapReason = "workspace/bootstrap-index";
		private const string CorrelationWorkspaceBootstrap = "lsp-notify-workspace-bootstrap";
		private const int WorkspaceBootstrapBatchSize = 64;
		private const int WorkspaceBootstrapBudgetMilliseconds = 4000;

		private static readonly LspIntentContract IntentInitialize = LspIntentContractRegistry.Require(LspUserIntentId.IntLc01InitializeSession);
		private static readonly LspIntentContract IntentShutdown = LspIntentContractRegistry.Require(LspUserIntentId.IntLc03Shutdown);
		private static readonly LspIntentContract IntentInitialized = LspIntentContractRegistry.Require(LspUserIntentId.IntLc02Initialized);
		private static readonly LspIntentContract IntentExit = LspIntentContractRegistry.Require(LspUserIntentId.IntLc04Exit);

		private static readonly LspIntentContract IntentDidOpen = LspIntentContractRegistry.Require(LspUserIntentId.IntDs01DidOpen);
		private static readonly LspIntentContract IntentDidChange = LspIntentContractRegistry.Require(LspUserIntentId.IntDs02DidChange);
		private static readonly LspIntentContract IntentDidClose = LspIntentContractRegistry.Require(LspUserIntentId.IntDs03DidClose);
		private static readonly LspIntentContract IntentDidChangeWatchedFiles = LspIntentContractRegistry.Require(LspUserIntentId.IntDs04DidChangeWatchedFiles);

		private static readonly LspIntentContract IntentQueryDocumentSymbol = LspIntentContractRegistry.Require(LspUserIntentId.IntQr01DocumentSymbol);
		private static readonly LspIntentContract IntentQueryHover = LspIntentContractRegistry.Require(LspUserIntentId.IntQr02Hover);
		private static readonly LspIntentContract IntentQueryDefinition = LspIntentContractRegistry.Require(LspUserIntentId.IntQr03Definition);
		private static readonly LspIntentContract IntentQueryReferences = LspIntentContractRegistry.Require(LspUserIntentId.IntQr04References);
		private static readonly LspIntentContract IntentQueryCompletion = LspIntentContractRegistry.Require(LspUserIntentId.IntQr05Completion);
		private static readonly LspIntentContract IntentQuerySignatureHelp = LspIntentContractRegistry.Require(LspUserIntentId.IntQr06SignatureHelp);
		private static readonly LspIntentContract IntentQuerySemanticTokens = LspIntentContractRegistry.Require(LspUserIntentId.IntQr07SemanticTokensFull);
		private static readonly LspIntentContract IntentQueryPrepareRename = LspIntentContractRegistry.Require(LspUserIntentId.IntQr08PrepareRename);
		private static readonly LspIntentContract IntentQueryRename = LspIntentContractRegistry.Require(LspUserIntentId.IntQr09Rename);
		private static readonly LspIntentContract IntentQueryWillRenameFiles = LspIntentContractRegistry.Require(LspUserIntentId.IntQr10WillRenameFiles);

		private static readonly LspIntentContract IntentPublishDiagnostics = LspIntentContractRegistry.Require(LspUserIntentId.IntFb01PublishDiagnostics);
		private static readonly LspIntentContract IntentShowMessageRequest = LspIntentContractRegistry.Require(LspUserIntentId.IntFb02ShowMessageRequest);
		private static readonly LspIntentContract IntentApplyEdit = LspIntentContractRegistry.Require(LspUserIntentId.IntFb03ApplyEdit);
		private static readonly LspIntentContract IntentWorkspaceContextInitialization = LspIntentContractRegistry.Require(LspUserIntentId.IntBs03WorkspaceContextInitialization);

		private static class JsonFields
		{
			public const string Files = "files";
			public const string Changes = "changes";
			public const string Uri = "uri";
			public const string Type = "type";
			public const string RootUri = "rootUri";
			public const string RootPath = "rootPath";
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
			public const string Range = "range";
			public const string Start = "start";
			public const string End = "end";
			public const string NewText = "newText";
			public const string Severity = "severity";
			public const string Source = "source";
			public const string Message = "message";
			public const string Code = "code";
			public const string Tags = "tags";
			public const string Data = "data";
			public const string RelatedInformation = "relatedInformation";
			public const string Actions = "actions";
			public const string Title = "title";
			public const string Edit = "edit";
			public const string DocumentChanges = "documentChanges";
			public const string Kind = "kind";
			public const string Options = "options";
			public const string Overwrite = "overwrite";
			public const string IgnoreIfExists = "ignoreIfExists";
			public const string Edits = "edits";
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
			if (!LspIntentContractRegistry.ValidateBridgeCoverage(out string coverageError))
				throw new InvalidOperationException("LSP intent contract coverage is invalid: " + coverageError);

			_database = database ?? throw new ArgumentNullException(nameof(database));
			_queryFacade = queryFacade ?? new InMemoryLspQueryFacade();
		}

		public void Initialize(JsonObject initializeParams)
		{
			EnsureIntentBound(IntentInitialize);

			string rootPath = ResolveRootPath(initializeParams);
			ProjectFile project = string.IsNullOrWhiteSpace(rootPath)
				? null
				: ProjectFile.TryDiscover(rootPath);
			string normalizedRootPath = WorkspacePathTool.NormalizePath(rootPath);
			List<string> includeRoots = BuildWorkspaceScanRoots(normalizedRootPath, project);
			IncludeTargetResolver.SetWorkspaceIncludeRoots(includeRoots);

			lock (_sync)
			{
				_rootPath = rootPath;
				_projectFile = project;
				_openDocuments.Clear();
				_renameUriMap.Clear();
				_diagnosticsQueue.Clear();
				_clientRequestQueue.Clear();
				_ffprojPromptQueued = false;
			}

			BootstrapWorkspaceDocuments(rootPath, project);
		}

		public void Shutdown(JsonObject shutdownParams)
		{
			EnsureIntentBound(IntentShutdown);
		}

		public void Initialized(JsonObject initializedParams)
		{
			EnsureIntentBound(IntentInitialized);
			QueueFfprojCreationPromptIfNeeded();
		}

		public void Exit(JsonObject exitParams)
		{
			EnsureIntentBound(IntentExit);
		}

		public void DidOpen(JsonObject didOpenParams)
		{
			TrackDidOpen(didOpenParams);
			ApplySingleChange(IntentDidOpen, DatabaseChangeKind.DocumentOpened, didOpenParams);
			QueueDiagnosticsForUri(ExtractDocumentUri(didOpenParams));
		}

		public void DidChange(JsonObject didChangeParams)
		{
			JsonObject normalizedDidChange = NormalizeDidChangePayload(didChangeParams);
			TrackDidChange(normalizedDidChange);
			ApplySingleChange(IntentDidChange, DatabaseChangeKind.DocumentChanged, normalizedDidChange);
			QueueDiagnosticsForUri(ExtractDocumentUri(normalizedDidChange));
		}

		public void DidClose(JsonObject didCloseParams)
		{
			string closedUri = ExtractDocumentUri(didCloseParams);
			int? closedVersion = ExtractVersion(didCloseParams);

			TrackDidClose(didCloseParams);
			ApplySingleChange(IntentDidClose, DatabaseChangeKind.DocumentClosed, didCloseParams);
			QueueClearDiagnostics(closedUri, closedVersion);
		}

		public void DidChangeWatchedFiles(JsonObject didChangeWatchedFilesParams)
		{
			EnsureIntentBound(IntentDidChangeWatchedFiles);

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
				if (changeType == WatchedFileChangeType.Deleted
					&& uri.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase)
					&& TryGetOpenDocumentText(uri, out _))
				{
					continue;
				}

				if ((changeType == WatchedFileChangeType.Created || changeType == WatchedFileChangeType.Changed)
					&& TryBuildWatcherReindexChange(uri, out DatabaseChangeEvent reindexChange))
				{
					changes.Add(reindexChange);
					continue;
				}

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
				reason: IntentDidChangeWatchedFiles.WriteReason,
				correlationId: BuildNotifyCorrelation(IntentDidChangeWatchedFiles),
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.Empty,
				streamBehavior: DatabaseOperationStreamBehavior.None,
				intentCode: IntentDidChangeWatchedFiles.IntentCode));
		}

		public IReadOnlyList<LspDocumentSymbolItem> QueryDocumentSymbol(JsonObject requestParams)
		{
			if (!TryReadSnapshot(IntentQueryDocumentSymbol, out CodeDatabaseSnapshot snapshot))
				return new List<LspDocumentSymbolItem>(0);

			SymbolQueryRequest query = BuildQueryRequest(IntentQueryDocumentSymbol, requestParams, false, string.Empty);
			return NormalizeDocumentSymbols(_queryFacade.QueryDocumentSymbols(snapshot, query));
		}

		public LspHoverPayload QueryHover(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryHover, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryHover(snapshot, query));
			return NormalizeHover(payload != null ? payload.Hover : null);
		}

		public LspDefinitionPayload QueryDefinition(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryDefinition, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryDefinition(snapshot, query));
			return NormalizeDefinition(payload != null ? payload.Definition : null);
		}

		public IReadOnlyList<LspReferenceItem> QueryReferences(JsonObject requestParams)
		{
			bool includeDeclaration = requestParams?.GetObject(JsonFields.Context)?.GetBool(JsonFields.IncludeDeclaration) == true;
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryReferences, requestParams, includeDeclaration, string.Empty, (snapshot, query)
				=> _queryFacade.QueryReferences(snapshot, query));
			return NormalizeReferences(payload != null ? payload.References : null);
		}

		public IReadOnlyList<LspCompletionItem> QueryCompletion(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryCompletion, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryCompletion(snapshot, query));
			return payload != null && payload.CompletionItems != null
				? payload.CompletionItems
				: new List<LspCompletionItem>(0);
		}

		public LspSignatureHelpPayload QuerySignatureHelp(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQuerySignatureHelp, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QuerySignatureHelp(snapshot, query));
			return NormalizeSignatureHelp(payload != null ? payload.SignatureHelp : null);
		}

		public LspRenamePayload QueryRename(JsonObject requestParams)
		{
			string newName = requestParams != null ? requestParams.GetString(JsonFields.NewName) : string.Empty;
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryRename, requestParams, true, newName, (snapshot, query)
				=> _queryFacade.QueryRename(snapshot, query));
			return NormalizeRename(payload != null ? payload.Rename : null);
		}

		public LspPrepareRenamePayload QueryPrepareRename(JsonObject requestParams)
		{
			SymbolQueryPayload payload = ExecutePayloadQuery(IntentQueryPrepareRename, requestParams, false, string.Empty, (snapshot, query)
				=> _queryFacade.QueryPrepareRename(snapshot, query));
			return payload != null ? payload.PrepareRename : null;
		}

		public LspSemanticTokensPayload QuerySemanticTokensFull(JsonObject requestParams)
		{
			if (!TryReadSnapshot(IntentQuerySemanticTokens, out CodeDatabaseSnapshot snapshot))
				return new LspSemanticTokensPayload(new List<int>(0), "Snapshot is unavailable.");

			SymbolQueryRequest query = BuildQueryRequest(IntentQuerySemanticTokens, requestParams, false, string.Empty);
			return _queryFacade.QuerySemanticTokensFull(snapshot, query)
				?? new LspSemanticTokensPayload(new List<int>(0), string.Empty);
		}

		public JsonObject QueryWillRenameFiles(JsonObject requestParams)
		{
			EnsureIntentBound(IntentQueryWillRenameFiles);

			string rootPath;
			ProjectFile project;
			lock (_sync)
			{
				rootPath = _rootPath;
				project = _projectFile;
			}

			if (string.IsNullOrWhiteSpace(rootPath) || requestParams == null)
				return new JsonObject();

			List<object> files = requestParams.GetArray(JsonFields.Files);
			if (files == null || files.Count == 0)
				return new JsonObject();

			var editsByUri = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
			var fileRenames = new List<(string oldUri, string newUri)>();

			for (int i = 0; i < files.Count; i++)
			{
				if (!(files[i] is JsonObject renameItem))
					continue;

				if (!TryExtractRenamePayload(renameItem, out string oldUri, out string newUri))
					continue;

				fileRenames.Add((oldUri, newUri));

				string oldAbsPath = WorkspacePathTool.UriToPath(oldUri);
				string newAbsPath = WorkspacePathTool.UriToPath(newUri);
				if (string.IsNullOrWhiteSpace(oldAbsPath) || string.IsNullOrWhiteSpace(newAbsPath))
					continue;

				List<(string includePath, string basePath)> oldIncludePaths = ResolveFileToIncludePaths(oldAbsPath, rootPath, project);
				List<(string includePath, string basePath)> newIncludePaths = ResolveFileToIncludePaths(newAbsPath, rootPath, project);
				if (oldIncludePaths.Count == 0)
					continue;

				ScanWorkspaceForRenames(rootPath, oldIncludePaths, newIncludePaths, editsByUri);
			}

			var workspaceEdit = new JsonObject();
			if (editsByUri.Count > 0)
			{
				var changes = new JsonObject();
				foreach (KeyValuePair<string, List<object>> pair in editsByUri)
					changes.Set(pair.Key, pair.Value);

				workspaceEdit.Set(JsonFields.Changes, changes);
			}

			if (editsByUri.Count > 0 || fileRenames.Count > 0)
				ApplyRenameState(editsByUri, fileRenames);

			return workspaceEdit;
		}

		public bool TryDequeueDiagnostics(out LspPublishedDiagnostics diagnostics)
		{
			EnsureIntentBound(IntentPublishDiagnostics);

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

		public bool TryDequeueClientRequest(out LspClientRequest request)
		{
			lock (_sync)
			{
				if (_clientRequestQueue.Count > 0)
				{
					request = _clientRequestQueue.Dequeue();
					return true;
				}

				request = null;
				return false;
			}
		}

		public void HandleClientRequestResponse(string method, string requestToken, object result, JsonObject error)
		{
			string normalizedMethod = method ?? string.Empty;
			if (string.Equals(normalizedMethod, ClientMethodShowMessageRequest, StringComparison.Ordinal))
			{
				EnsureIntentBound(IntentShowMessageRequest);
				HandleShowMessageRequestResponse(requestToken, result, error);
				return;
			}

			if (string.Equals(normalizedMethod, ClientMethodApplyEdit, StringComparison.Ordinal))
			{
				EnsureIntentBound(IntentApplyEdit);
			}
		}

		private void ApplySingleChange(LspIntentContract intent, DatabaseChangeKind kind, JsonObject payload)
		{
			EnsureIntentBound(intent);

			string uri = ExtractDocumentUri(payload);
			int? version = ExtractVersion(payload);

			var change = new DatabaseChangeEvent(
				kind,
				new PathKey(uri),
				version,
				BuildChangePayload(kind, payload, uri));

			_database.Execute(DatabaseOperationRequest.ApplyChanges(
				new[] { change },
				reason: intent.WriteReason,
				correlationId: BuildNotifyCorrelation(intent),
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.IsNullOrWhiteSpace(uri) ? string.Empty : DocumentStreamPrefix + uri,
				streamBehavior: string.IsNullOrWhiteSpace(uri)
					? DatabaseOperationStreamBehavior.None
					: DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded,
				intentCode: intent.IntentCode));
		}

		private SymbolQueryPayload ExecutePayloadQuery(
			LspIntentContract intent,
			JsonObject requestParams,
			bool includeDeclaration,
			string newName,
			Func<CodeDatabaseSnapshot, SymbolQueryRequest, SymbolQueryResult> query)
		{
			if (query == null)
				return null;

			EnsureIntentBound(intent);

			if (!TryReadSnapshot(intent, out CodeDatabaseSnapshot snapshot))
				return null;

			SymbolQueryRequest normalized = BuildQueryRequest(intent, requestParams, includeDeclaration, newName);
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			SymbolQueryResult result = query(snapshot, normalized);
			stopwatch.Stop();

			int resultCount = result != null && result.Ranges != null ? result.Ranges.Count : 0;
			bool succeeded = result != null && result.Succeeded;
			WriteQueryMetrics(intent, resultCount, stopwatch.ElapsedMilliseconds, succeeded);

			if (result == null)
				return null;

			return result.Succeeded ? result.Payload : null;
		}

		private static void WriteQueryMetrics(LspIntentContract intent, int results, long elapsedMs, bool succeeded)
		{
			try
			{
				string intentCode = intent != null ? intent.IntentCode : string.Empty;
				System.Console.Error.WriteLine(
					"[ffvm][lsp] query intent=" + (intentCode ?? string.Empty)
					+ " results=" + results
					+ " succeeded=" + (succeeded ? 1 : 0)
					+ " elapsedMs=" + elapsedMs);
			}
			catch
			{
				// Never let observability disturb the protocol loop.
			}
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

		private bool TryReadSnapshot(LspIntentContract intent, out CodeDatabaseSnapshot snapshot)
		{
			EnsureIntentBound(intent);
			snapshot = null;

			DatabaseOperationResult readResult = _database.Execute(DatabaseOperationRequest.ReadSnapshot(
				correlationId: BuildReadCorrelation(intent),
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(SnapshotReadTimeoutSeconds),
				intentCode: intent.IntentCode));

			if (readResult == null || !readResult.Succeeded || readResult.Snapshot == null)
				return false;

			snapshot = readResult.Snapshot;
			return true;
		}

		private static SymbolQueryRequest BuildQueryRequest(
			LspIntentContract intent,
			JsonObject requestParams,
			bool includeDeclaration,
			string newName)
		{
			EnsureIntentBound(intent);

			string uri = ExtractDocumentUri(requestParams);

			JsonObject position = requestParams != null ? requestParams.GetObject(JsonFields.Position) : null;
			int line = position != null ? position.GetInt(JsonFields.Line) : 0;
			int character = position != null ? position.GetInt(JsonFields.Character) : 0;

			var normalizedPosition = new TextPosition(line, character);
			return new SymbolQueryRequest(
				intent.QueryOperationName,
				uri,
				normalizedPosition,
				new TextSpan(0, 0),
				includeDeclaration,
				newName);
		}

		private string ResolveRootPath(JsonObject initializeParams)
		{
			if (initializeParams == null)
				return string.Empty;

			string rootUri = initializeParams.GetString(JsonFields.RootUri);
			if (!string.IsNullOrWhiteSpace(rootUri))
			{
				string resolvedFromUri = WorkspacePathTool.UriToPath(rootUri);
				if (!string.IsNullOrWhiteSpace(resolvedFromUri))
					return resolvedFromUri;
			}

			string rootPath = initializeParams.GetString(JsonFields.RootPath);
			if (!string.IsNullOrWhiteSpace(rootPath))
				return WorkspacePathTool.NormalizePath(rootPath) ?? string.Empty;

			return string.Empty;
		}

		private void BootstrapWorkspaceDocuments(string rootPath, ProjectFile project)
		{
			EnsureIntentBound(IntentWorkspaceContextInitialization);
			if (string.IsNullOrWhiteSpace(rootPath))
				return;

			var metrics = new WorkspaceScanMetrics();
			var stopwatch = System.Diagnostics.Stopwatch.StartNew();
			List<string> scriptFiles = EnumerateWorkspaceScriptFiles(rootPath, project, metrics);
			int ingested = 0;
			int failed = 0;

			if (scriptFiles.Count == 0)
			{
				stopwatch.Stop();
				metrics.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
				WriteScanMetrics(rootPath, metrics, ingested, failed);
				return;
			}

			var batch = new List<DatabaseChangeEvent>(WorkspaceBootstrapBatchSize);
			int appliedDocuments = 0;
			int deferredDocuments = 0;
			int batchCount = 0;
			int applyFailures = 0;
			bool budgetExceeded = false;
			bool applyFailed = false;

			for (int i = 0; i < scriptFiles.Count; i++)
			{
				if (batchCount > 0 && stopwatch.ElapsedMilliseconds >= WorkspaceBootstrapBudgetMilliseconds)
				{
					budgetExceeded = true;
					deferredDocuments = scriptFiles.Count - i;
					break;
				}

				string filePath = scriptFiles[i];
				if (!TryReadWorkspaceFile(filePath, out string text))
				{
					failed++;
					continue;
				}

				string uri = DocumentKeyNormalizer.Normalize(WorkspacePathTool.PathToFileUri(filePath));
				if (string.IsNullOrWhiteSpace(uri))
				{
					failed++;
					continue;
				}

				batch.Add(new DatabaseChangeEvent(
					DatabaseChangeKind.DocumentChanged,
					new PathKey(uri),
					null,
					new DocumentChangedWithTierChangePayload(uri, text, DocumentSourceTier.Disk)));
				ingested++;

				if (batch.Count < WorkspaceBootstrapBatchSize)
					continue;

				if (!ApplyWorkspaceBootstrapBatch(batch, batchCount))
				{
					applyFailures++;
					failed += batch.Count;
					batch.Clear();
					deferredDocuments = scriptFiles.Count - (i + 1);
					applyFailed = true;
					break;
				}

				appliedDocuments += batch.Count;
				batch.Clear();
				batchCount++;
			}

			if (!applyFailed && batch.Count > 0)
			{
				if (!ApplyWorkspaceBootstrapBatch(batch, batchCount))
				{
					applyFailures++;
					failed += batch.Count;
				}
				else
				{
					appliedDocuments += batch.Count;
					batchCount++;
				}

				batch.Clear();
			}

			metrics.BootstrapBatchCount = batchCount;
			metrics.BootstrapAppliedDocuments = appliedDocuments;
			metrics.BootstrapDeferredDocuments = deferredDocuments;
			metrics.BootstrapApplyFailures = applyFailures;
			metrics.BootstrapBudgetExceeded = budgetExceeded;

			stopwatch.Stop();
			metrics.ElapsedMilliseconds = stopwatch.ElapsedMilliseconds;
			WriteScanMetrics(rootPath, metrics, ingested, failed);

			if (appliedDocuments == 0)
				return;
		}

		private bool ApplyWorkspaceBootstrapBatch(IReadOnlyList<DatabaseChangeEvent> changes, int batchIndex)
		{
			if (changes == null || changes.Count == 0)
				return true;

			DatabaseOperationResult result = _database.Execute(DatabaseOperationRequest.ApplyChanges(
				changes,
				reason: WorkspaceBootstrapReason,
				correlationId: CorrelationWorkspaceBootstrap + "-batch-" + batchIndex,
				priority: DatabaseOperationPriority.Low,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.Empty,
				streamBehavior: DatabaseOperationStreamBehavior.None,
				intentCode: IntentWorkspaceContextInitialization.IntentCode));

			return result != null && result.Succeeded;
		}

		private static void WriteScanMetrics(string rootPath, WorkspaceScanMetrics metrics, int ingested, int failed)
		{
			if (metrics == null)
				return;

			try
			{
				System.Console.Error.WriteLine(
					"[ffvm][lsp] workspace-scan root=" + (rootPath ?? string.Empty)
					+ " dirs=" + metrics.DirectoriesVisited
					+ " skipped=" + metrics.DirectoriesSkipped
					+ " files=" + metrics.FilesFound
					+ " ingested=" + ingested
					+ " failed=" + failed
					+ " errors=" + metrics.FileErrors
					+ " batches=" + metrics.BootstrapBatchCount
					+ " applied=" + metrics.BootstrapAppliedDocuments
					+ " deferred=" + metrics.BootstrapDeferredDocuments
					+ " applyFailures=" + metrics.BootstrapApplyFailures
					+ " budgetExceeded=" + (metrics.BootstrapBudgetExceeded ? 1 : 0)
					+ " elapsedMs=" + metrics.ElapsedMilliseconds);
			}
			catch
			{
				// Never let observability disturb the protocol loop.
			}
		}

		private List<string> EnumerateWorkspaceScriptFiles(string rootPath, ProjectFile project)
		{
			return EnumerateWorkspaceScriptFiles(rootPath, project, metrics: null);
		}

		private List<string> EnumerateWorkspaceScriptFiles(string rootPath, ProjectFile project, WorkspaceScanMetrics metrics)
		{
			var output = new List<string>();
			string normalizedRoot = WorkspacePathTool.NormalizePath(rootPath);
			if (string.IsNullOrWhiteSpace(normalizedRoot))
				return output;

			List<string> scanRoots = BuildWorkspaceScanRoots(normalizedRoot, project);
			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			for (int i = 0; i < scanRoots.Count; i++)
			{
				string scanRoot = scanRoots[i];
				if (string.IsNullOrWhiteSpace(scanRoot))
					continue;

				List<string> files = WorkspaceScanFilter.EnumerateFfsFiles(scanRoot, metrics);
				for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
				{
					string normalizedFile = files[fileIndex];
					if (string.IsNullOrWhiteSpace(normalizedFile))
						continue;

					if (seen.Add(normalizedFile))
						output.Add(normalizedFile);
				}
			}

			output.Sort(StringComparer.OrdinalIgnoreCase);
			return output;
		}

		private static List<string> BuildWorkspaceScanRoots(string normalizedRoot, ProjectFile project)
		{
			var roots = new List<string>();
			if (string.IsNullOrWhiteSpace(normalizedRoot))
				return roots;

			roots.Add(normalizedRoot);
			if (project == null || project.IncludePaths == null)
				return roots;

			for (int i = 0; i < project.IncludePaths.Length; i++)
			{
				string includePath = project.IncludePaths[i];
				string resolved = WorkspacePathTool.ResolvePath(normalizedRoot, includePath);
				if (string.IsNullOrWhiteSpace(resolved))
					continue;

				bool exists = false;
				for (int j = 0; j < roots.Count; j++)
				{
					if (string.Equals(roots[j], resolved, StringComparison.OrdinalIgnoreCase))
					{
						exists = true;
						break;
					}
				}

				if (!exists)
					roots.Add(resolved);
			}

			return roots;
		}

		private bool TryBuildWatcherReindexChange(string uri, out DatabaseChangeEvent change)
		{
			change = default;
			string normalizedUri = DocumentKeyNormalizer.Normalize(uri);
			if (string.IsNullOrWhiteSpace(normalizedUri)
				|| !normalizedUri.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			if (TryGetOpenDocumentText(normalizedUri, out string openText)
				&& openText != null)
			{
				change = new DatabaseChangeEvent(
					DatabaseChangeKind.DocumentChanged,
					new PathKey(normalizedUri),
					null,
					new DocumentChangedWithTierChangePayload(normalizedUri, openText, DocumentSourceTier.OpenBuffer));
				return true;
			}

			string filePath = WorkspacePathTool.UriToPath(normalizedUri);
			if (!TryReadWorkspaceFile(filePath, out string text))
				return false;

			change = new DatabaseChangeEvent(
				DatabaseChangeKind.DocumentChanged,
				new PathKey(normalizedUri),
				null,
				new DocumentChangedWithTierChangePayload(normalizedUri, text, DocumentSourceTier.Watcher));
			return true;
		}

		private static bool TryReadWorkspaceFile(string filePath, out string text)
		{
			text = string.Empty;
			if (string.IsNullOrWhiteSpace(filePath))
				return false;

			try
			{
				if (!File.Exists(filePath))
					return false;

				text = File.ReadAllText(filePath);
				return true;
			}
			catch
			{
				text = string.Empty;
				return false;
			}
		}

		private void TrackDidOpen(JsonObject didOpenParams)
		{
			string uri = ExtractDocumentUri(didOpenParams);
			if (string.IsNullOrWhiteSpace(uri))
				return;

			string text = ExtractText(didOpenParams);
			string languageId = ExtractLanguageId(didOpenParams);
			int? version = ExtractVersion(didOpenParams);

			lock (_sync)
			{
				string resolvedUri = ResolveRenamedUriLocked(uri);
				_openDocuments[resolvedUri] = new OpenDocumentState(resolvedUri, languageId, text, version);
			}
		}

		private void TrackDidChange(JsonObject didChangeParams)
		{
			string uri = ExtractDocumentUri(didChangeParams);
			if (string.IsNullOrWhiteSpace(uri))
				return;

			string text = ExtractText(didChangeParams);
			int? incomingVersion = ExtractVersion(didChangeParams);

			lock (_sync)
			{
				string resolvedUri = ResolveRenamedUriLocked(uri);

				if (_openDocuments.TryGetValue(resolvedUri, out OpenDocumentState existing))
				{
					existing.Text = text;
					if (incomingVersion.HasValue)
						existing.Version = incomingVersion;
					else if (existing.Version.HasValue)
						existing.Version = existing.Version.Value + 1;
					return;
				}

				_openDocuments[resolvedUri] = new OpenDocumentState(resolvedUri, string.Empty, text, incomingVersion);
			}
		}

		private JsonObject NormalizeDidChangePayload(JsonObject didChangeParams)
		{
			if (didChangeParams == null)
				return new JsonObject();

			string uri = ExtractDocumentUri(didChangeParams);
			int? version = ExtractVersion(didChangeParams);
			string languageId = ExtractLanguageId(didChangeParams);

			string baseText = string.Empty;
			if (!string.IsNullOrWhiteSpace(uri))
			{
				lock (_sync)
				{
					string resolvedUri = ResolveRenamedUriLocked(uri);
					if (_openDocuments.TryGetValue(resolvedUri, out OpenDocumentState existing) && existing != null)
						baseText = existing.Text ?? string.Empty;
				}
			}

			string normalizedText = ResolveDidChangeText(didChangeParams, baseText);
			return BuildDidChangePayload(uri, version, languageId, normalizedText);
		}

		private static JsonObject BuildDidChangePayload(string uri, int? version, string languageId, string text)
		{
			string normalizedUri = DocumentKeyNormalizer.Normalize(uri);
			string normalizedText = text ?? string.Empty;

			var payload = new JsonObject();
			var textDocument = new JsonObject();

			if (!string.IsNullOrWhiteSpace(normalizedUri))
				textDocument.Set(JsonFields.Uri, normalizedUri);

			if (version.HasValue)
				textDocument.Set(JsonFields.Version, version.Value);

			if (!string.IsNullOrWhiteSpace(languageId))
				textDocument.Set(JsonFields.LanguageId, languageId);

			textDocument.Set(JsonFields.Text, normalizedText);
			payload.Set(JsonFields.TextDocument, textDocument);

			if (!string.IsNullOrWhiteSpace(normalizedUri))
				payload.Set(JsonFields.Uri, normalizedUri);

			if (version.HasValue)
				payload.Set(JsonFields.Version, version.Value);

			payload.Set(JsonFields.Text, normalizedText);
			return payload;
		}

		private static string ResolveDidChangeText(JsonObject payload, string baseText)
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
			if (contentChanges == null || contentChanges.Count == 0)
				return ExtractText(payload);

			bool hasRangedChanges = false;
			for (int i = 0; i < contentChanges.Count; i++)
			{
				if (contentChanges[i] is JsonObject changed && changed.GetObject(JsonFields.Range) != null)
				{
					hasRangedChanges = true;
					break;
				}
			}

			if (!hasRangedChanges)
				return ExtractText(payload);

			string working = baseText ?? string.Empty;
			bool applied = false;

			for (int i = 0; i < contentChanges.Count; i++)
			{
				if (!(contentChanges[i] is JsonObject changed))
					continue;

				string changeText = changed.GetString(JsonFields.Text) ?? string.Empty;
				JsonObject range = changed.GetObject(JsonFields.Range);
				if (range == null)
				{
					working = changeText;
					applied = true;
					continue;
				}

				if (!TryExtractRange(range, out int startLine, out int startCharacter, out int endLine, out int endCharacter))
					continue;

				if (!TryGetTextOffset(working, startLine, startCharacter, out int startOffset)
					|| !TryGetTextOffset(working, endLine, endCharacter, out int endOffset))
				{
					continue;
				}

				if (endOffset < startOffset)
					endOffset = startOffset;

				var builder = new StringBuilder(working);
				builder.Remove(startOffset, endOffset - startOffset);
				builder.Insert(startOffset, changeText);
				working = builder.ToString();
				applied = true;
			}

			if (applied)
				return working;

			return ExtractText(payload);
		}

		private static bool TryExtractRange(
			JsonObject range,
			out int startLine,
			out int startCharacter,
			out int endLine,
			out int endCharacter)
		{
			startLine = 0;
			startCharacter = 0;
			endLine = 0;
			endCharacter = 0;

			if (range == null)
				return false;

			JsonObject start = range.GetObject(JsonFields.Start);
			JsonObject end = range.GetObject(JsonFields.End);
			if (start == null || end == null)
				return false;

			startLine = start.GetInt(JsonFields.Line);
			startCharacter = start.GetInt(JsonFields.Character);
			endLine = end.GetInt(JsonFields.Line);
			endCharacter = end.GetInt(JsonFields.Character);

			if (startLine < 0)
				startLine = 0;

			if (startCharacter < 0)
				startCharacter = 0;

			if (endLine < 0)
				endLine = 0;

			if (endCharacter < 0)
				endCharacter = 0;

			return true;
		}

		private void TrackDidClose(JsonObject didCloseParams)
		{
			string uri = ExtractDocumentUri(didCloseParams);
			if (string.IsNullOrWhiteSpace(uri))
				return;

			lock (_sync)
			{
				string resolvedUri = ResolveRenamedUriLocked(uri);
				_openDocuments.Remove(resolvedUri);
				_openDocuments.Remove(uri);
			}
		}

		private void QueueFfprojCreationPromptIfNeeded()
		{
			string rootPath;
			bool alreadyQueued;

			lock (_sync)
			{
				rootPath = _rootPath;
				alreadyQueued = _ffprojPromptQueued || _projectFile != null;
			}

			if (alreadyQueued || string.IsNullOrWhiteSpace(rootPath))
				return;

			bool hasFfsFiles;
			try
			{
				hasFfsFiles = Directory.Exists(rootPath)
					&& Directory.GetFiles(rootPath, "*.ffs", SearchOption.AllDirectories).Length > 0;
			}
			catch
			{
				hasFfsFiles = false;
			}

			if (!hasFfsFiles)
				return;

			var parameters = new JsonObject();
			parameters.Set(JsonFields.Type, MessageTypeInfo);
			parameters.Set(JsonFields.Message, ShowMessagePromptText);

			var actions = new List<object>();
			actions.Add(MakeMessageAction(ActionTitleCreate));
			actions.Add(MakeMessageAction(ActionTitleIgnore));
			actions.Add(MakeMessageAction(ActionTitleNeverAsk));
			parameters.Set(JsonFields.Actions, actions);

			lock (_sync)
			{
				if (_ffprojPromptQueued)
					return;

				_ffprojPromptQueued = true;
			}

			EnqueueClientRequest(IntentShowMessageRequest, ClientMethodShowMessageRequest, parameters, RequestTokenCreateFfprojOffer);
		}

		private static JsonObject MakeMessageAction(string title)
		{
			var action = new JsonObject();
			action.Set(JsonFields.Title, title ?? string.Empty);
			return action;
		}

		private void HandleShowMessageRequestResponse(string requestToken, object result, JsonObject error)
		{
			if (error != null)
				return;

			if (!string.Equals(requestToken, RequestTokenCreateFfprojOffer, StringComparison.Ordinal))
				return;

			if (!(result is JsonObject response))
				return;

			string title = response.GetString(JsonFields.Title);
			if (!string.Equals(title, ActionTitleCreate, StringComparison.Ordinal))
				return;

			JsonObject applyEditParameters = BuildCreateFfprojApplyEditParameters();
			if (applyEditParameters == null)
				return;

			EnqueueClientRequest(IntentApplyEdit, ClientMethodApplyEdit, applyEditParameters, RequestTokenCreateFfprojOffer + "/applyEdit");
		}

		private JsonObject BuildCreateFfprojApplyEditParameters()
		{
			string rootPath;
			lock (_sync)
				rootPath = _rootPath;

			if (string.IsNullOrWhiteSpace(rootPath))
				return null;

			string folderName = Path.GetFileName(rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
			string ffprojFileName = !string.IsNullOrWhiteSpace(folderName)
				? folderName + ".ffproj"
				: "project.ffproj";

			string ffprojPath = Path.Combine(rootPath, ffprojFileName);
			string ffprojUri = DocumentKeyNormalizer.Normalize(WorkspacePathTool.PathToFileUri(ffprojPath));
			if (string.IsNullOrWhiteSpace(ffprojUri))
				return null;

			string template = ProjectFile.GenerateTemplate(null);

			var createFile = new JsonObject();
			createFile.Set(JsonFields.Kind, CreateFileOperationKind);
			createFile.Set(JsonFields.Uri, ffprojUri);

			var createOptions = new JsonObject();
			createOptions.Set(JsonFields.Overwrite, false);
			createOptions.Set(JsonFields.IgnoreIfExists, true);
			createFile.Set(JsonFields.Options, createOptions);

			var textDocument = new JsonObject();
			textDocument.Set(JsonFields.Uri, ffprojUri);
			textDocument.Set(JsonFields.Version, null);

			var start = new JsonObject();
			start.Set(JsonFields.Line, 0);
			start.Set(JsonFields.Character, 0);

			var end = new JsonObject();
			end.Set(JsonFields.Line, 0);
			end.Set(JsonFields.Character, 0);

			var range = new JsonObject();
			range.Set(JsonFields.Start, start);
			range.Set(JsonFields.End, end);

			var textEdit = new JsonObject();
			textEdit.Set(JsonFields.Range, range);
			textEdit.Set(JsonFields.NewText, template ?? string.Empty);

			var edits = new List<object> { textEdit };

			var textDocumentEdit = new JsonObject();
			textDocumentEdit.Set(JsonFields.TextDocument, textDocument);
			textDocumentEdit.Set(JsonFields.Edits, edits);

			var documentChanges = new List<object> { createFile, textDocumentEdit };

			var edit = new JsonObject();
			edit.Set(JsonFields.DocumentChanges, documentChanges);

			var parameters = new JsonObject();
			parameters.Set(JsonFields.Edit, edit);
			return parameters;
		}

		private void EnqueueClientRequest(LspIntentContract intent, string method, JsonObject parameters, string requestToken)
		{
			if (string.IsNullOrWhiteSpace(method))
				return;

			EnsureIntentBound(intent);

			lock (_sync)
			{
				_clientRequestQueue.Enqueue(new LspClientRequest(method, parameters, requestToken));
			}
		}

		private void QueueDiagnosticsForUri(string uri)
		{
			if (string.IsNullOrWhiteSpace(uri))
				return;

			string text;
			int? version;
			string normalizedUri;

			lock (_sync)
			{
				normalizedUri = ResolveRenamedUriLocked(uri);
				if (!_openDocuments.TryGetValue(normalizedUri, out OpenDocumentState state)
					|| state == null)
				{
					return;
				}

				normalizedUri = state.Uri;
				text = state.Text;
				version = state.Version;
			}

			IReadOnlyList<object> diagnostics = NormalizeDiagnostics(BuildParseDiagnostics(text));

			lock (_sync)
			{
				_diagnosticsQueue.Enqueue(new LspPublishedDiagnostics(normalizedUri, diagnostics, version));
			}
		}

		private void QueueClearDiagnostics(string uri, int? version)
		{
			string normalizedUri = DocumentKeyNormalizer.Normalize(uri);
			if (string.IsNullOrWhiteSpace(normalizedUri))
				return;

			IReadOnlyList<object> diagnostics = NormalizeDiagnostics(new List<object>(0));

			lock (_sync)
			{
				_diagnosticsQueue.Enqueue(new LspPublishedDiagnostics(normalizedUri, diagnostics, version));
			}
		}

		private static IReadOnlyList<object> BuildParseDiagnostics(string source)
		{
			if (string.IsNullOrEmpty(source))
				return new List<object>(0);

			var parser = new Parser();
			parser.Parse(source, out List<string> parseErrors);

			if (parseErrors == null || parseErrors.Count == 0)
				return new List<object>(0);

			var diagnostics = new List<object>(parseErrors.Count);
			for (int i = 0; i < parseErrors.Count; i++)
			{
				string parseError = parseErrors[i];
				if (string.IsNullOrWhiteSpace(parseError))
					continue;

				diagnostics.Add(ParseErrorToDiagnostic(parseError));
			}

			return diagnostics;
		}

		private static JsonObject ParseErrorToDiagnostic(string error)
		{
			int line = 0;
			int character = 0;
			string message = error ?? string.Empty;

			Match lineMatch = Regex.Match(message, @"\(line\s+(\d+)\)\s*$", RegexOptions.IgnoreCase);
			if (lineMatch.Success)
			{
				if (int.TryParse(lineMatch.Groups[1].Value, out int parsedLine))
					line = parsedLine - 1;

				message = message.Substring(0, lineMatch.Index).TrimEnd();
			}
			else
			{
				Match atMatch = Regex.Match(message, @"at\s+(\d+):(\d+)", RegexOptions.IgnoreCase);
				if (atMatch.Success)
				{
					if (int.TryParse(atMatch.Groups[1].Value, out int parsedLine))
						line = parsedLine - 1;

					if (int.TryParse(atMatch.Groups[2].Value, out int parsedCharacter))
						character = parsedCharacter - 1;
				}
			}

			if (line < 0)
				line = 0;

			if (character < 0)
				character = 0;

			var start = new JsonObject();
			start.Set(JsonFields.Line, line);
			start.Set(JsonFields.Character, character);

			var end = new JsonObject();
			end.Set(JsonFields.Line, line);
			end.Set(JsonFields.Character, character + 1);

			var range = new JsonObject();
			range.Set(JsonFields.Start, start);
			range.Set(JsonFields.End, end);

			var diagnostic = new JsonObject();
			diagnostic.Set(JsonFields.Range, range);
			diagnostic.Set(JsonFields.Severity, DiagnosticSeverityError);
			diagnostic.Set(JsonFields.Source, DiagnosticSourceDefault);
			diagnostic.Set(JsonFields.Message, message);
			return NormalizeDiagnostic(diagnostic);
		}

		private static IReadOnlyList<object> NormalizeDiagnostics(IReadOnlyList<object> diagnostics)
		{
			var normalized = new List<object>();
			if (diagnostics == null || diagnostics.Count == 0)
				return normalized;

			for (int i = 0; i < diagnostics.Count; i++)
			{
				object raw = diagnostics[i];
				if (raw is JsonObject diagnosticObject)
				{
					normalized.Add(NormalizeDiagnostic(diagnosticObject));
					continue;
				}

				if (raw is string diagnosticText && !string.IsNullOrWhiteSpace(diagnosticText))
				{
					var fallback = new JsonObject();
					fallback.Set(JsonFields.Message, diagnosticText);
					normalized.Add(NormalizeDiagnostic(fallback));
				}
			}

			return normalized;
		}

		private static JsonObject NormalizeDiagnostic(JsonObject diagnostic)
		{
			string message = diagnostic != null ? diagnostic.GetString(JsonFields.Message) : string.Empty;
			if (string.IsNullOrWhiteSpace(message))
				message = DiagnosticMessageFallback;

			string source = diagnostic != null ? diagnostic.GetString(JsonFields.Source) : string.Empty;
			if (string.IsNullOrWhiteSpace(source))
				source = DiagnosticSourceDefault;

			int severity = diagnostic != null
				? diagnostic.GetInt(JsonFields.Severity, DiagnosticSeverityError)
				: DiagnosticSeverityError;
			severity = ClampDiagnosticSeverity(severity);

			JsonObject range = NormalizeRange(diagnostic != null ? diagnostic.GetObject(JsonFields.Range) : null);

			var normalized = new JsonObject();
			normalized.Set(JsonFields.Range, range);
			normalized.Set(JsonFields.Severity, severity);
			normalized.Set(JsonFields.Source, source);
			normalized.Set(JsonFields.Message, message);

			if (diagnostic != null)
			{
				object code = diagnostic.Get(JsonFields.Code);
				if (code != null)
					normalized.Set(JsonFields.Code, code);

				if (diagnostic.Get(JsonFields.Tags) is List<object> tags)
					normalized.Set(JsonFields.Tags, new List<object>(tags));

				object data = diagnostic.Get(JsonFields.Data);
				if (data != null)
					normalized.Set(JsonFields.Data, data);

				if (diagnostic.Get(JsonFields.RelatedInformation) is List<object> relatedInformation)
					normalized.Set(JsonFields.RelatedInformation, new List<object>(relatedInformation));
			}

			return normalized;
		}

		private static JsonObject NormalizeRange(JsonObject range)
		{
			JsonObject start = NormalizePosition(
				range != null ? range.GetObject(JsonFields.Start) : null,
				defaultLine: 0,
				defaultCharacter: 0);

			int startLine = start.GetInt(JsonFields.Line);
			int startCharacter = start.GetInt(JsonFields.Character);

			JsonObject end = NormalizePosition(
				range != null ? range.GetObject(JsonFields.End) : null,
				defaultLine: startLine,
				defaultCharacter: startCharacter + 1);

			int endLine = end.GetInt(JsonFields.Line);
			int endCharacter = end.GetInt(JsonFields.Character);

			if (endLine < startLine || (endLine == startLine && endCharacter <= startCharacter))
			{
				endLine = startLine;
				endCharacter = startCharacter + 1;

				end = new JsonObject();
				end.Set(JsonFields.Line, endLine);
				end.Set(JsonFields.Character, endCharacter);
			}

			var normalizedRange = new JsonObject();
			normalizedRange.Set(JsonFields.Start, start);
			normalizedRange.Set(JsonFields.End, end);
			return normalizedRange;
		}

		private static JsonObject NormalizePosition(JsonObject position, int defaultLine, int defaultCharacter)
		{
			int line = position != null ? position.GetInt(JsonFields.Line, defaultLine) : defaultLine;
			int character = position != null ? position.GetInt(JsonFields.Character, defaultCharacter) : defaultCharacter;

			if (line < 0)
				line = 0;

			if (character < 0)
				character = 0;

			var normalized = new JsonObject();
			normalized.Set(JsonFields.Line, line);
			normalized.Set(JsonFields.Character, character);
			return normalized;
		}

		private static int ClampDiagnosticSeverity(int severity)
		{
			if (severity < DiagnosticSeverityError)
				return DiagnosticSeverityError;

			if (severity > DiagnosticSeverityHint)
				return DiagnosticSeverityHint;

			return severity;
		}

		private string ResolveRenamedUriLocked(string uri)
		{
			string current = DocumentKeyNormalizer.Normalize(uri);
			if (string.IsNullOrWhiteSpace(current))
				return string.Empty;

			for (int i = 0; i < 16; i++)
			{
				if (!_renameUriMap.TryGetValue(current, out string next)
					|| string.IsNullOrWhiteSpace(next)
					|| string.Equals(current, next, StringComparison.OrdinalIgnoreCase))
				{
					break;
				}

				current = next;
			}

			return current;
		}

		private List<(string includePath, string basePath)> ResolveFileToIncludePaths(string absPath, string rootPath, ProjectFile project)
		{
			var result = new List<(string includePath, string basePath)>();
			if (string.IsNullOrWhiteSpace(absPath) || string.IsNullOrWhiteSpace(rootPath))
				return result;

			string normalizedAbsolute = WorkspacePathTool.NormalizePath(absPath);
			string normalizedRoot = WorkspacePathTool.NormalizePath(rootPath);
			if (string.IsNullOrWhiteSpace(normalizedAbsolute) || string.IsNullOrWhiteSpace(normalizedRoot))
				return result;

			var baseDirs = new List<string> { normalizedRoot };
			if (project != null && project.IncludePaths != null)
			{
				for (int i = 0; i < project.IncludePaths.Length; i++)
				{
					string includePath = project.IncludePaths[i];
					string basePath = WorkspacePathTool.ResolvePath(normalizedRoot, includePath);
					if (string.IsNullOrWhiteSpace(basePath))
						continue;

					bool exists = false;
					for (int j = 0; j < baseDirs.Count; j++)
					{
						if (string.Equals(baseDirs[j], basePath, StringComparison.OrdinalIgnoreCase))
						{
							exists = true;
							break;
						}
					}

					if (!exists)
						baseDirs.Add(basePath);
				}
			}

			for (int i = 0; i < baseDirs.Count; i++)
			{
				string baseDir = baseDirs[i];
				if (string.IsNullOrWhiteSpace(baseDir))
					continue;

				string rootedBase = baseDir.EndsWith("/", StringComparison.Ordinal)
					? baseDir
					: baseDir + "/";

				if (!normalizedAbsolute.StartsWith(rootedBase, StringComparison.OrdinalIgnoreCase))
					continue;

				string relative = normalizedAbsolute.Substring(rootedBase.Length);
				string withoutExt = relative;
				if (withoutExt.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase))
					withoutExt = withoutExt.Substring(0, withoutExt.Length - 4);

				result.Add((withoutExt, baseDir));
				if (!string.Equals(relative, withoutExt, StringComparison.OrdinalIgnoreCase))
					result.Add((relative, baseDir));
			}

			return result;
		}

		private void ScanWorkspaceForRenames(
			string rootPath,
			List<(string includePath, string basePath)> oldPaths,
			List<(string includePath, string basePath)> newPaths,
			Dictionary<string, List<object>> editsByUri)
		{
			if (string.IsNullOrWhiteSpace(rootPath)
				|| oldPaths == null
				|| newPaths == null
				|| oldPaths.Count == 0
				|| newPaths.Count == 0)
			{
				return;
			}

			var renameMap = new Dictionary<string, string>(StringComparer.Ordinal);
			for (int i = 0; i < oldPaths.Count; i++)
			{
				(string includePath, string basePath) oldEntry = oldPaths[i];
				for (int j = 0; j < newPaths.Count; j++)
				{
					(string includePath, string basePath) newEntry = newPaths[j];
					if (!string.Equals(oldEntry.basePath, newEntry.basePath, StringComparison.OrdinalIgnoreCase))
						continue;

					bool extensionMatches = oldEntry.includePath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase)
						== newEntry.includePath.EndsWith(".ffs", StringComparison.OrdinalIgnoreCase);
					if (!extensionMatches)
						continue;

					renameMap[oldEntry.includePath] = newEntry.includePath;
				}
			}

			if (renameMap.Count == 0)
				return;

			string[] files;
			try
			{
				files = Directory.GetFiles(rootPath, "*.ffs", SearchOption.AllDirectories);
			}
			catch
			{
				return;
			}

			var parser = new Parser();
			for (int i = 0; i < files.Length; i++)
			{
				string filePath = files[i];
				string source;

				try
				{
					source = File.ReadAllText(filePath);
				}
				catch
				{
					continue;
				}

				string fileUri = DocumentKeyNormalizer.Normalize(WorkspacePathTool.PathToFileUri(filePath));
				if (TryGetOpenDocumentText(fileUri, out string cachedText))
					source = cachedText;

				var ast = parser.Parse(source, out _);
				if (ast == null)
					continue;

				for (int importIndex = 0; importIndex < ast.Imports.Count; importIndex++)
				{
					var importNode = ast.Imports[importIndex];
					if (!renameMap.TryGetValue(importNode.ModulePath, out string newPath))
						continue;

					int lspLine = importNode.Line - 1;
					int pathStart = importNode.Column - 1 + IncludeKeyword.Length + IncludePathPrefixLength;
					int pathLength = importNode.ModulePath.Length;

					JsonObject edit = MakeTextEdit(lspLine, pathStart, lspLine, pathStart + pathLength, newPath);
					if (!editsByUri.TryGetValue(fileUri, out List<object> edits))
					{
						edits = new List<object>();
						editsByUri[fileUri] = edits;
					}

					edits.Add(edit);
				}
			}
		}

		private void ApplyRenameState(
			Dictionary<string, List<object>> editsByUri,
			List<(string oldUri, string newUri)> fileRenames)
		{
			var databaseChanges = new List<DatabaseChangeEvent>();

			lock (_sync)
			{
				if (editsByUri != null)
				{
					foreach (KeyValuePair<string, List<object>> pair in editsByUri)
					{
						string normalizedUri = DocumentKeyNormalizer.Normalize(pair.Key);
						if (string.IsNullOrWhiteSpace(normalizedUri))
							continue;

						string resolvedUri = ResolveRenamedUriLocked(normalizedUri);
						if (!_openDocuments.TryGetValue(resolvedUri, out OpenDocumentState state)
							|| state == null)
						{
							continue;
						}

						if (!TryApplyTextEdits(state.Text, pair.Value, out string updatedText)
							|| string.Equals(state.Text, updatedText, StringComparison.Ordinal))
						{
							continue;
						}

						state.Text = updatedText;
						if (state.Version.HasValue)
							state.Version = state.Version.Value + 1;

						databaseChanges.Add(new DatabaseChangeEvent(
							DatabaseChangeKind.DocumentChanged,
							new PathKey(state.Uri),
							state.Version,
							new DocumentChangedChangePayload(state.Uri, state.Text)));
					}
				}

				if (fileRenames != null)
				{
					for (int i = 0; i < fileRenames.Count; i++)
					{
						(string oldUri, string newUri) renamePair = fileRenames[i];
						string oldUri = DocumentKeyNormalizer.Normalize(renamePair.oldUri);
						string newUri = DocumentKeyNormalizer.Normalize(renamePair.newUri);
						if (string.IsNullOrWhiteSpace(oldUri) || string.IsNullOrWhiteSpace(newUri))
							continue;

						_renameUriMap[oldUri] = newUri;

						if (_openDocuments.TryGetValue(oldUri, out OpenDocumentState openState) && openState != null)
						{
							_openDocuments.Remove(oldUri);
							openState.Uri = newUri;
							_openDocuments[newUri] = openState;
						}

						databaseChanges.Add(new DatabaseChangeEvent(
							DatabaseChangeKind.FileRenamed,
							new PathKey(oldUri),
							null,
							new FileRenamedChangePayload(oldUri, newUri)));
					}
				}
			}

			if (databaseChanges.Count == 0)
				return;

			_database.Execute(DatabaseOperationRequest.ApplyChanges(
				databaseChanges,
				reason: "willRenameFiles/preApply",
				correlationId: CorrelationWillRenameState,
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(DatabaseWriteTimeoutSeconds),
				streamKey: string.Empty,
				streamBehavior: DatabaseOperationStreamBehavior.None,
				intentCode: IntentQueryWillRenameFiles.IntentCode));
		}

		private bool TryGetOpenDocumentText(string uri, out string text)
		{
			lock (_sync)
			{
				string resolvedUri = ResolveRenamedUriLocked(uri);
				if (_openDocuments.TryGetValue(resolvedUri, out OpenDocumentState state)
					&& state != null)
				{
					text = state.Text;
					return true;
				}
			}

			text = string.Empty;
			return false;
		}

		private static JsonObject MakeTextEdit(int startLine, int startChar, int endLine, int endChar, string newText)
		{
			var start = new JsonObject();
			start.Set(JsonFields.Line, startLine < 0 ? 0 : startLine);
			start.Set(JsonFields.Character, startChar < 0 ? 0 : startChar);

			var end = new JsonObject();
			end.Set(JsonFields.Line, endLine < 0 ? 0 : endLine);
			end.Set(JsonFields.Character, endChar < 0 ? 0 : endChar);

			var range = new JsonObject();
			range.Set(JsonFields.Start, start);
			range.Set(JsonFields.End, end);

			var edit = new JsonObject();
			edit.Set(JsonFields.Range, range);
			edit.Set(JsonFields.NewText, newText ?? string.Empty);
			return edit;
		}

		private static bool TryApplyTextEdits(string source, IReadOnlyList<object> edits, out string updated)
		{
			string original = source ?? string.Empty;
			updated = original;

			if (edits == null || edits.Count == 0)
				return false;

			var parsedEdits = new List<AppliedTextEdit>(edits.Count);
			for (int i = 0; i < edits.Count; i++)
			{
				if (TryParseTextEdit(original, edits[i], out AppliedTextEdit parsed))
					parsedEdits.Add(parsed);
			}

			if (parsedEdits.Count == 0)
				return false;

			parsedEdits.Sort((left, right) =>
			{
				int startOrder = right.StartIndex.CompareTo(left.StartIndex);
				if (startOrder != 0)
					return startOrder;
				return right.EndIndex.CompareTo(left.EndIndex);
			});

			var builder = new StringBuilder(original);
			for (int i = 0; i < parsedEdits.Count; i++)
			{
				AppliedTextEdit edit = parsedEdits[i];
				int startIndex = Math.Max(0, Math.Min(edit.StartIndex, builder.Length));
				int endIndex = Math.Max(startIndex, Math.Min(edit.EndIndex, builder.Length));
				builder.Remove(startIndex, endIndex - startIndex);
				builder.Insert(startIndex, edit.NewText);
			}

			updated = builder.ToString();
			return !string.Equals(updated, original, StringComparison.Ordinal);
		}

		private static bool TryParseTextEdit(string source, object rawEdit, out AppliedTextEdit edit)
		{
			edit = null;
			if (!(rawEdit is JsonObject editObject))
				return false;

			JsonObject range = editObject.GetObject(JsonFields.Range);
			JsonObject start = range != null ? range.GetObject(JsonFields.Start) : null;
			JsonObject end = range != null ? range.GetObject(JsonFields.End) : null;
			if (start == null || end == null)
				return false;

			int startLine = start.GetInt(JsonFields.Line);
			int startCharacter = start.GetInt(JsonFields.Character);
			int endLine = end.GetInt(JsonFields.Line);
			int endCharacter = end.GetInt(JsonFields.Character);

			if (!TryGetTextOffset(source, startLine, startCharacter, out int startOffset))
				return false;

			if (!TryGetTextOffset(source, endLine, endCharacter, out int endOffset))
				return false;

			if (endOffset < startOffset)
				endOffset = startOffset;

			string newText = editObject.GetString(JsonFields.NewText) ?? string.Empty;
			edit = new AppliedTextEdit(startOffset, endOffset, newText);
			return true;
		}

		private static bool TryGetTextOffset(string text, int line, int character, out int offset)
		{
			string source = text ?? string.Empty;
			int targetLine = line < 0 ? 0 : line;
			int targetCharacter = character < 0 ? 0 : character;

			int currentLine = 0;
			int index = 0;
			while (index < source.Length && currentLine < targetLine)
			{
				char c = source[index++];
				if (c == '\r')
				{
					if (index < source.Length && source[index] == '\n')
						index++;
					currentLine++;
				}
				else if (c == '\n')
				{
					currentLine++;
				}
			}

			if (currentLine < targetLine)
			{
				offset = source.Length;
				return true;
			}

			int lineEnd = index;
			while (lineEnd < source.Length && source[lineEnd] != '\r' && source[lineEnd] != '\n')
				lineEnd++;

			offset = Math.Min(index + targetCharacter, lineEnd);
			return true;
		}

		private static string BuildNotifyCorrelation(LspIntentContract intent)
		{
			return CorrelationPrefixLspNotify + intent.IntentCode;
		}

		private static string BuildReadCorrelation(LspIntentContract intent)
		{
			return CorrelationLspReadSnapshot + "-" + intent.IntentCode;
		}

		private static void EnsureIntentBound(LspIntentContract intent)
		{
			if (intent == null)
				throw new InvalidOperationException("LSP intent contract must not be null.");

			if (string.IsNullOrWhiteSpace(intent.IntentCode))
				throw new InvalidOperationException("LSP intent contract has empty intent code.");

			if (string.IsNullOrWhiteSpace(intent.BridgeMember))
				throw new InvalidOperationException("LSP intent contract is missing bridge binding: " + intent.IntentCode + ".");
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
					return new DocumentChangedWithTierChangePayload(uri, ExtractText(payload), DocumentSourceTier.OpenBuffer);

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
