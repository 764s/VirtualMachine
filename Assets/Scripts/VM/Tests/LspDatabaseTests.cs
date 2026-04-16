using System;
using System.Collections.Generic;
using FFVM.Debug;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Database.Paths;
using UnityEngine;

/// <summary>
/// LSP database middle-layer validation tests.
/// Focuses on lifecycle policy, conflict arbitration, default runtime wiring,
/// and high-frequency task-center behavior in scaffold mode.
/// </summary>
public static class LspDatabaseTests
{
#if UNITY_EDITOR
	[UnityEditor.MenuItem("TestVM/RunLspDatabaseTests")]
#endif
	public static void RunAll()
	{
		int passed = 0;
		int failed = 0;

		void Assert(bool condition, string testName)
		{
			if (condition)
			{
				Debug.Log($"[PASS] {testName}");
				passed++;
			}
			else
			{
				Debug.LogError($"[FAIL] {testName}");
				failed++;
			}
		}

		// ================================================================
		// DBMID-01: Lifecycle transition policy
		// ================================================================
		{
			var policy = new DefaultDatabaseCommandLifecyclePolicy();

			Assert(
				policy.CanTransition(DatabaseCommandState.Created, DatabaseCommandState.Admitted, out _),
				"DBMID-01A: Created -> Admitted allowed");

			Assert(
				policy.CanTransition(DatabaseCommandState.Executing, DatabaseCommandState.Composed, out _),
				"DBMID-01B: Executing -> Composed allowed");

			bool blocked = !policy.CanTransition(DatabaseCommandState.Completed, DatabaseCommandState.Planned, out string blockedError);
			Assert(
				blocked && !string.IsNullOrEmpty(blockedError),
				"DBMID-01C: Completed -> Planned blocked with reason");
		}

		// ================================================================
		// DBMID-02: Conflict resolver (version gate)
		// ================================================================
		{
			var resolver = new DefaultDatabaseConflictResolver();
			var incoming = CreateApplyChangesRequest(
				streamKey: "stream://doc/version",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: 7,
				createdAtUtc: DateTime.UtcNow);

			var context = new DatabaseConflictContext(
				incoming,
				existing: null,
				currentSnapshotVersion: 2,
				stage: DatabaseExecutionStage.HighFrequencyAdmission,
				scenario: HighFrequencyScenarioKind.DocumentDidChangeBurst);

			DatabaseConflictDecision decision = resolver.Resolve(context);
			Assert(
				decision != null
				&& decision.Kind == DatabaseConflictKind.VersionGateMismatch
				&& decision.Action == DatabaseConflictResolutionAction.RejectIncoming,
				"DBMID-02: version mismatch rejected");
		}

		// ================================================================
		// DBMID-03: Conflict resolver (priority preemption)
		// ================================================================
		{
			var resolver = new DefaultDatabaseConflictResolver();
			var existing = CreateApplyChangesRequest(
				streamKey: "stream://doc/priority",
				priority: DatabaseOperationPriority.Low,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow.AddSeconds(-2));

			var incoming = CreateApplyChangesRequest(
				streamKey: "stream://doc/priority",
				priority: DatabaseOperationPriority.Critical,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow);

			var context = new DatabaseConflictContext(
				incoming,
				existing,
				currentSnapshotVersion: 0,
				stage: DatabaseExecutionStage.HighFrequencyAdmission,
				scenario: HighFrequencyScenarioKind.DocumentDidChangeBurst);

			DatabaseConflictDecision decision = resolver.Resolve(context);
			Assert(
				decision != null
				&& decision.Kind == DatabaseConflictKind.PriorityPreemption
				&& decision.Action == DatabaseConflictResolutionAction.CancelExistingAndAllowIncoming
				&& decision.ExistingCommandIdToCancel == existing.CommandId,
				"DBMID-03: higher-priority incoming preempts existing");
		}

		// ================================================================
		// DBMID-04: Default runtime wiring in single entrypoint
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var lifecycleSink = new InMemoryDatabaseCommandLifecycleSink();
			var decisionSink = new InMemoryDatabaseDecisionLogSink();

			var database = new InMemoryWorkspaceCodeDatabase(
				orchestrator,
				lifecycleSink: lifecycleSink,
				decisionLogSink: decisionSink);

			var request = CreateApplyChangesRequest(
				streamKey: "stream://doc/default-wiring",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow);

			DatabaseOperationResult result = database.Execute(request);

			Assert(result != null && result.Succeeded, "DBMID-04A: Execute succeeds with defaults");
			Assert(result != null && result.FinalState == DatabaseCommandState.Completed, "DBMID-04B: final state completed");
			Assert(database.LastOutcome != null, "DBMID-04C: LastOutcome captured");
			Assert(database.LastOutcome != null && database.LastOutcome.LifecycleTrace != null, "DBMID-04D: lifecycle trace present");
			Assert(
				database.LastOutcome != null
				&& database.LastOutcome.LifecycleTrace != null
				&& database.LastOutcome.LifecycleTrace.CurrentState == DatabaseCommandState.Completed,
				"DBMID-04E: lifecycle current state completed");
			Assert(lifecycleSink.Entries.Count > 0, "DBMID-04F: lifecycle sink captured transitions");
			Assert(decisionSink.Entries.Count > 0, "DBMID-04G: decision sink captured entries");
		}

		// ================================================================
		// DBMID-05: In-memory task center supersession behavior
		// ================================================================
		{
			var planner = new PassThroughDatabaseTaskPlanner();
			var center = new InMemoryDatabaseTaskCenter();

			var older = CreateApplyChangesRequest(
				streamKey: "stream://doc/task-center",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow.AddSeconds(-2));

			var newer = CreateApplyChangesRequest(
				streamKey: "stream://doc/task-center",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow);

			var planOlder = planner.Plan(CodeDatabaseSnapshot.Empty(), older);
			var planNewer = planner.Plan(CodeDatabaseSnapshot.Empty(), newer);

			DatabaseTaskEnqueueResult enqueueOlder = center.Enqueue(planOlder, older);
			DatabaseTaskEnqueueResult enqueueNewer = center.Enqueue(planNewer, newer);

			Assert(enqueueOlder.Accepted, "DBMID-05A: older command enqueued");
			Assert(enqueueNewer.Accepted, "DBMID-05B: newer command enqueued");
			Assert(
				enqueueNewer.SupersededCanceledCount >= 1,
				"DBMID-05C: newer enqueue canceled superseded pending command");

			bool hasLatest = center.TryGetLatestPending("stream://doc/task-center", out DatabaseOperationRequest latest);
			Assert(hasLatest && latest != null && latest.CommandId == newer.CommandId, "DBMID-05D: latest pending is newer command");

			DatabaseTaskExecutionReport execution = center.Execute(planNewer, newer);
			Assert(execution != null && execution.Succeeded, "DBMID-05E: execute returns success report");
			Assert(center.TryDrainOne(out _), "DBMID-05F: drain exposes completed report");
		}

		// ================================================================
		// DBMID-06: High-frequency default policies
		// ================================================================
		{
			var coalescer = new DefaultDatabaseOperationCoalescer();
			var supersession = new DefaultDatabaseSupersessionPolicy();

			var existing = CreateApplyChangesRequest(
				streamKey: "stream://doc/policies",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow.AddSeconds(-1));

			var incoming = CreateApplyChangesRequest(
				streamKey: "stream://doc/policies",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow);

			Assert(coalescer.CanCoalesce(existing, incoming), "DBMID-06A: can coalesce same stream apply-change commands");
			DatabaseCoalesceResult coalesce = coalescer.Coalesce(existing, incoming);
			Assert(
				coalesce != null && coalesce.Decision == DatabaseCoalesceDecision.MergeIntoNew,
				"DBMID-06B: equal-priority coalesce defaults to MergeIntoNew");

			Assert(
				supersession.ShouldCancelExisting(existing, incoming),
				"DBMID-06C: supersession cancels older equal-priority command");
		}

		// ================================================================
		// DBMID-07: ApplyChangeSet materializes document aggregate
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator);

			var didOpen = CreateDidOpenPayload(
				"file:///tests/materialize.ffs",
				"ffscript",
				1,
				"func entry() { wait 1 }");

			DatabaseOperationResult openResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/materialize",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentOpened,
						new PathKey("file:///tests/materialize.ffs"),
						1,
						didOpen),
				}));

			Assert(openResult != null && openResult.Succeeded, "DBMID-07A: didOpen apply succeeded");
			Assert(openResult != null && openResult.CurrentVersion == 1, "DBMID-07B: version incremented to 1");
			Assert(openResult?.Snapshot != null && openResult.Snapshot.Aggregates.Count == 1, "DBMID-07C: one aggregate materialized");

			DataAggregate aggregate = openResult?.Snapshot != null && openResult.Snapshot.Aggregates.Count > 0
				? openResult.Snapshot.Aggregates[0]
				: null;

			Assert(aggregate != null && aggregate.DocumentKey.Value == "file:///tests/materialize.ffs", "DBMID-07D: aggregate document key captured");
			Assert(aggregate != null && aggregate.LanguageId == "ffscript", "DBMID-07E: languageId captured");
			Assert(aggregate != null && aggregate.SourceVersion == 1, "DBMID-07F: source version captured");
			Assert(aggregate != null && !string.IsNullOrWhiteSpace(aggregate.TextHash), "DBMID-07G: text hash captured");
		}

		// ================================================================
		// DBMID-08: DocumentChanged updates aggregate metadata
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator);

			DatabaseOperationResult openResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/change",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentOpened,
						new PathKey("file:///tests/change.ffs"),
						1,
						CreateDidOpenPayload("file:///tests/change.ffs", "ffscript", 1, "func entry() { wait 1 }")),
				}));

			string oldHash = openResult?.Snapshot != null && openResult.Snapshot.Aggregates.Count > 0
				? openResult.Snapshot.Aggregates[0].TextHash
				: string.Empty;

			DatabaseOperationResult changeResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/change",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentChanged,
						new PathKey("file:///tests/change.ffs"),
						2,
						CreateDidChangePayload("file:///tests/change.ffs", 2, "func entry() { wait 2 }") ),
				}));

			DataAggregate changedAggregate = changeResult?.Snapshot != null && changeResult.Snapshot.Aggregates.Count > 0
				? changeResult.Snapshot.Aggregates[0]
				: null;

			Assert(changeResult != null && changeResult.Succeeded, "DBMID-08A: didChange apply succeeded");
			Assert(changeResult != null && changeResult.CurrentVersion == 2, "DBMID-08B: version incremented to 2");
			Assert(changedAggregate != null && changedAggregate.SourceVersion == 2, "DBMID-08C: source version updated");
			Assert(changedAggregate != null && changedAggregate.TextHash != oldHash, "DBMID-08D: text hash updated after change");
		}

		// ================================================================
		// DBMID-09: DocumentClosed removes aggregate
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator);

			database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/close",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentOpened,
						new PathKey("file:///tests/close.ffs"),
						1,
						CreateDidOpenPayload("file:///tests/close.ffs", "ffscript", 1, "func entry() { wait 1 }")),
				}));

			DatabaseOperationResult closeResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/close",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentClosed,
						new PathKey("file:///tests/close.ffs"),
						2,
						CreateDidClosePayload("file:///tests/close.ffs", 2)),
				}));

			Assert(closeResult != null && closeResult.Succeeded, "DBMID-09A: didClose apply succeeded");
			Assert(closeResult != null && closeResult.CurrentVersion == 2, "DBMID-09B: version incremented on close");
			Assert(closeResult?.Snapshot != null && closeResult.Snapshot.Aggregates.Count == 0, "DBMID-09C: aggregate removed on close");
		}

		// ================================================================
		// DBMID-10: ReplaceSnapshot rebuilds indexes for query facade
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator);

			var document = new PathKey("file:///tests/indexed.ffs");
			var aggregateId = new DataAggregateId("agg:indexed");

			JsonObject definitionPayload = CreateSymbolFactPayload(document.Value, "Function", "entry", 0, 0, 0, 5, 0, 5);
			JsonObject referencePayload = CreateSymbolFactPayload(document.Value, "Function", "entry", 1, 2, 1, 7, 0, 5);

			var definitionFact = new DataFact(
				new DataFactId("indexed-def"),
				aggregateId,
				DataFactKind.SymbolDefinition,
				document,
				new TextSpan(0, 5),
				snapshotVersion: 1,
				payload: definitionPayload);

			var referenceFact = new DataFact(
				new DataFactId("indexed-ref"),
				aggregateId,
				DataFactKind.SymbolReference,
				document,
				new TextSpan(10, 5),
				snapshotVersion: 1,
				payload: referencePayload);

			var replacementAggregate = new DataAggregate(
				aggregateId,
				DataAggregateKind.Document,
				document,
				"ffscript",
				"HASH",
				1,
				new List<DataFact> { definitionFact, referenceFact });

			var replacementSnapshot = new CodeDatabaseSnapshot(
				version: 99,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { replacementAggregate },
				facts: new List<DataFact> { definitionFact, referenceFact },
				indexSnapshot: null);

			DatabaseOperationResult replaceResult = database.Execute(DatabaseOperationRequest.ReplaceSnapshot(
				replacementSnapshot,
				reason: "index-bootstrap",
				correlationId: "corr-index-bootstrap",
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(15)));

			Assert(replaceResult != null && replaceResult.Succeeded, "DBMID-10A: replace snapshot succeeded");
			Assert(replaceResult != null && replaceResult.CurrentVersion == 1, "DBMID-10B: replace snapshot normalized version progression");
			Assert(replaceResult?.Snapshot != null && replaceResult.Snapshot.IndexSnapshot != null, "DBMID-10C: index snapshot rebuilt on replace");

			var facade = new InMemoryLspQueryFacade();
			var definitionRequest = SymbolQueryRequest.ForPosition("definition", document.Value, new TextPosition(0, 1));
			SymbolQueryResult definitionResult = facade.QueryDefinition(replaceResult?.Snapshot, definitionRequest);
			Assert(definitionResult != null && definitionResult.Succeeded, "DBMID-10D: query definition succeeds on rebuilt index");

			var referencesRequest = new SymbolQueryRequest("references", document.Value, new TextPosition(0, 1), new TextSpan(0, 0), false, string.Empty);
			SymbolQueryResult referencesResult = facade.QueryReferences(replaceResult?.Snapshot, referencesRequest);
			Assert(referencesResult != null && referencesResult.Succeeded && referencesResult.Ranges.Count == 1, "DBMID-10E: references query returns non-declaration usage");

			var completionRequest = new SymbolQueryRequest("completion", document.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, "ent");
			SymbolQueryResult completionResult = facade.QueryCompletion(replaceResult?.Snapshot, completionRequest);
			Assert(completionResult != null && completionResult.Succeeded, "DBMID-10F: completion query succeeds on rebuilt index");
		}

		// ================================================================
		// DBMID-11: Index maintainer builds include/dependent graph
		// ================================================================
		{
			var maintainer = new InMemoryIndexMaintainer();
			var sourceDocument = new PathKey("file:///tests/include-a.ffs");
			var targetDocument = new PathKey("file:///tests/include-b.ffs");

			var includePayload = new JsonObject();
			includePayload.Set("includeUri", targetDocument.Value);

			var includeFact = new DataFact(
				new DataFactId("include-edge"),
				new DataAggregateId("agg-include-a"),
				DataFactKind.IncludeEdge,
				sourceDocument,
				new TextSpan(0, 0),
				snapshotVersion: 5,
				payload: includePayload);

			var sourceAggregate = new DataAggregate(
				new DataAggregateId("agg-include-a"),
				DataAggregateKind.Document,
				sourceDocument,
				"ffscript",
				string.Empty,
				1,
				new List<DataFact> { includeFact });

			var snapshot = new CodeDatabaseSnapshot(
				version: 5,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { sourceAggregate },
				facts: new List<DataFact> { includeFact },
				indexSnapshot: null);

			IIndexSnapshot indexSnapshot = maintainer.Rebuild(snapshot);
			Assert(indexSnapshot != null && indexSnapshot.SnapshotVersion == 5, "DBMID-11A: index snapshot rebuilt at source version");

			IReadOnlyList<PathKey> includes = indexSnapshot != null
				? indexSnapshot.IncludeGraphIndex.GetIncludes(sourceDocument)
				: new List<PathKey>(0);

			IReadOnlyList<PathKey> dependents = indexSnapshot != null
				? indexSnapshot.IncludeGraphIndex.GetDependents(targetDocument)
				: new List<PathKey>(0);

			Assert(includes.Count == 1 && includes[0].Value == targetDocument.Value, "DBMID-11B: include graph contains target document");
			Assert(dependents.Count == 1 && dependents[0].Value == sourceDocument.Value, "DBMID-11C: dependent graph contains source document");
		}

		Debug.Log($"[LspDatabaseTests] Completed. Passed={passed}, Failed={failed}");
	}

	private static DatabaseOperationRequest CreateApplyChangesRequest(
		string streamKey,
		DatabaseOperationPriority priority,
		long? expectedVersion,
		DateTime createdAtUtc,
		IReadOnlyList<DatabaseChangeEvent> changes = null)
	{
		IReadOnlyList<DatabaseChangeEvent> effectiveChanges = changes ?? new List<DatabaseChangeEvent>
		{
			new DatabaseChangeEvent(
				DatabaseChangeKind.DocumentChanged,
				new PathKey("file:///tests/virtual.ffs"),
				versionHint: 1,
				payload: "content")
		};

		DatabaseOperationStreamBehavior behavior = string.IsNullOrWhiteSpace(streamKey)
			? DatabaseOperationStreamBehavior.None
			: DatabaseOperationStreamBehavior.CoalesceAndCancelSuperseded;

		return DatabaseOperationRequest.ApplyChanges(
			effectiveChanges,
			expectedVersion: expectedVersion,
			reason: "test",
			correlationId: "corr-lspdb",
			priority: priority,
			timeout: TimeSpan.FromSeconds(15),
			streamKey: streamKey,
			streamBehavior: behavior,
			createdAtUtc: createdAtUtc);
	}

	private static JsonObject CreateDidOpenPayload(string uri, string languageId, int version, string text)
	{
		var payload = new JsonObject();
		var textDocument = new JsonObject();
		textDocument.Set("uri", uri);
		textDocument.Set("languageId", languageId);
		textDocument.Set("version", version);
		textDocument.Set("text", text);
		payload.Set("textDocument", textDocument);
		return payload;
	}

	private static JsonObject CreateDidChangePayload(string uri, int version, string text)
	{
		var payload = new JsonObject();
		var textDocument = new JsonObject();
		textDocument.Set("uri", uri);
		textDocument.Set("version", version);
		payload.Set("textDocument", textDocument);

		var change = new JsonObject();
		change.Set("text", text);
		payload.Set("contentChanges", new List<object> { change });
		return payload;
	}

	private static JsonObject CreateDidClosePayload(string uri, int version)
	{
		var payload = new JsonObject();
		var textDocument = new JsonObject();
		textDocument.Set("uri", uri);
		textDocument.Set("version", version);
		payload.Set("textDocument", textDocument);
		return payload;
	}

	private static JsonObject CreateSymbolFactPayload(
		string origin,
		string kind,
		string name,
		int rangeStartLine,
		int rangeStartCharacter,
		int rangeEndLine,
		int rangeEndCharacter,
		int declarationStart,
		int declarationLength)
	{
		var payload = new JsonObject();

		var symbol = new JsonObject();
		symbol.Set("kind", kind);
		symbol.Set("name", name);
		symbol.Set("scope", string.Empty);
		symbol.Set("parentName", string.Empty);
		symbol.Set("origin", origin);

		var declaration = new JsonObject();
		declaration.Set("start", declarationStart);
		declaration.Set("length", declarationLength);
		symbol.Set("declarationSpan", declaration);
		payload.Set("symbol", symbol);

		var range = new JsonObject();
		var start = new JsonObject();
		start.Set("line", rangeStartLine);
		start.Set("character", rangeStartCharacter);

		var end = new JsonObject();
		end.Set("line", rangeEndLine);
		end.Set("character", rangeEndCharacter);

		range.Set("start", start);
		range.Set("end", end);
		payload.Set("range", range);

		return payload;
	}
}
