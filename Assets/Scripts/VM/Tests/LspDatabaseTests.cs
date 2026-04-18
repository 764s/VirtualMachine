using System;
using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using FFVM.Debug;
using FFVM.Compiler;
using FFVM.Debug.Lsp.Database;
using FFVM.Debug.Lsp.Database.Contracts;
using FFVM.Debug.Lsp.Integration.VsCode;
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

			ValidateOperationTaskPayload olderValidatePayload = planOlder != null && planOlder.Tasks.Count > 0
				? planOlder.Tasks[0].Payload as ValidateOperationTaskPayload
				: null;
			FinalizeOperationTaskPayload newerFinalizePayload = planNewer != null && planNewer.Tasks.Count > 1
				? planNewer.Tasks[1].Payload as FinalizeOperationTaskPayload
				: null;

			Assert(
				olderValidatePayload != null
				&& olderValidatePayload.OperationKind == DatabaseOperationKind.ApplyChangeSet
				&& olderValidatePayload.StreamKey == older.StreamKey,
				"DBMID-05A: planner emits typed validate payload");
			Assert(
				newerFinalizePayload != null
				&& !string.IsNullOrWhiteSpace(newerFinalizePayload.FinalizationReason),
				"DBMID-05B: planner emits typed finalize payload");

			DatabaseTaskEnqueueResult enqueueOlder = center.Enqueue(planOlder, older);
			DatabaseTaskEnqueueResult enqueueNewer = center.Enqueue(planNewer, newer);

			Assert(enqueueOlder.Accepted, "DBMID-05C: older command enqueued");
			Assert(enqueueNewer.Accepted, "DBMID-05D: newer command enqueued");
			Assert(
				enqueueNewer.SupersededCanceledCount >= 1,
				"DBMID-05E: newer enqueue canceled superseded pending command");

			bool hasLatest = center.TryGetLatestPending("stream://doc/task-center", out DatabaseOperationRequest latest);
			Assert(hasLatest && latest != null && latest.CommandId == newer.CommandId, "DBMID-05F: latest pending is newer command");

			DatabaseTaskExecutionReport execution = center.Execute(planNewer, newer);
			Assert(execution != null && execution.Succeeded, "DBMID-05G: execute returns success report");
			Assert(center.TryDrainOne(out _), "DBMID-05H: drain exposes completed report");
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

			SymbolDataFactPayload definitionPayload = CreateSymbolFactPayload(document.Value, "Function", "entry", 0, 0, 0, 5, 0, 5);
			SymbolDataFactPayload referencePayload = CreateSymbolFactPayload(document.Value, "Function", "entry", 1, 2, 1, 7, 0, 5);

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

			IncludeEdgeDataFactPayload includePayload = CreateIncludePayload(targetDocument.Value);

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

		// ================================================================
		// DBMID-12: ApplyChangeSet prefers incremental index Update
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var trackingMaintainer = new TrackingIndexMaintainer();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator, indexMaintainer: trackingMaintainer);

			var documentA = new PathKey("file:///tests/incremental-a.ffs");
			var documentB = new PathKey("file:///tests/incremental-b.ffs");

			var definitionA = new DataFact(
				new DataFactId("inc-a-def"),
				new DataAggregateId("agg-inc-a"),
				DataFactKind.SymbolDefinition,
				documentA,
				new TextSpan(0, 5),
				snapshotVersion: 1,
				payload: CreateSymbolFactPayload(documentA.Value, "Function", "alpha", 0, 0, 0, 5, 0, 5));

			var definitionB = new DataFact(
				new DataFactId("inc-b-def"),
				new DataAggregateId("agg-inc-b"),
				DataFactKind.SymbolDefinition,
				documentB,
				new TextSpan(0, 6),
				snapshotVersion: 1,
				payload: CreateSymbolFactPayload(documentB.Value, "Function", "betaFn", 0, 0, 0, 6, 0, 6));

			var aggregateA = new DataAggregate(
				new DataAggregateId("agg-inc-a"),
				DataAggregateKind.Document,
				documentA,
				"ffscript",
				"HASH-A",
				1,
				new List<DataFact> { definitionA });

			var aggregateB = new DataAggregate(
				new DataAggregateId("agg-inc-b"),
				DataAggregateKind.Document,
				documentB,
				"ffscript",
				"HASH-B",
				1,
				new List<DataFact> { definitionB });

			var replacementSnapshot = new CodeDatabaseSnapshot(
				version: 9,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { aggregateA, aggregateB },
				facts: new List<DataFact> { definitionA, definitionB },
				indexSnapshot: null);

			DatabaseOperationResult replaceResult = database.Execute(DatabaseOperationRequest.ReplaceSnapshot(
				replacementSnapshot,
				reason: "incremental-bootstrap",
				correlationId: "corr-incremental-bootstrap",
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(15)));

			Assert(replaceResult != null && replaceResult.Succeeded, "DBMID-12A: replace snapshot bootstrap succeeded");
			Assert(trackingMaintainer.RebuildCallCount >= 1, "DBMID-12B: bootstrap triggered rebuild");

			DatabaseOperationResult applyResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/incremental-a",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentChanged,
						documentA,
						2,
						CreateDidChangePayload(documentA.Value, 2, "func alpha() { wait 2 }")),
				}));

			Assert(applyResult != null && applyResult.Succeeded, "DBMID-12C: apply change succeeded");
			Assert(trackingMaintainer.UpdateCallCount >= 1, "DBMID-12D: apply change used incremental update");
			Assert(
				trackingMaintainer.LastUpdatedDocuments != null
				&& trackingMaintainer.LastUpdatedDocuments.Count > 0
				&& trackingMaintainer.LastUpdatedDocuments[0].Value == documentA.Value,
				"DBMID-12E: changed document hint forwarded to index update");

			var facade = new InMemoryLspQueryFacade();
			var completionRequest = new SymbolQueryRequest("completion", documentB.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, "beta");
			SymbolQueryResult completionResult = facade.QueryCompletion(applyResult?.Snapshot, completionRequest);
			Assert(completionResult != null && completionResult.Succeeded, "DBMID-12F: query still works after incremental update");
		}

		// ================================================================
		// DBMID-13: Incremental Update matches full Rebuild results
		// ================================================================
		{
			var maintainer = new InMemoryIndexMaintainer();
			var facade = new InMemoryLspQueryFacade();

			var documentA = new PathKey("file:///tests/inc13-a.ffs");
			var documentB = new PathKey("file:///tests/inc13-b.ffs");
			var documentC = new PathKey("file:///tests/inc13-c.ffs");

			var baseDefinitionA = new DataFact(
				new DataFactId("inc13-a-def-v1"),
				new DataAggregateId("agg-inc13-a"),
				DataFactKind.SymbolDefinition,
				documentA,
				new TextSpan(0, 5),
				snapshotVersion: 20,
				payload: CreateSymbolFactPayload(documentA.Value, "Function", "alpha", 0, 0, 0, 5, 0, 5));

			var baseReferenceA = new DataFact(
				new DataFactId("inc13-a-ref-v1"),
				new DataAggregateId("agg-inc13-a"),
				DataFactKind.SymbolReference,
				documentA,
				new TextSpan(20, 5),
				snapshotVersion: 20,
				payload: CreateSymbolFactPayload(documentA.Value, "Function", "alpha", 1, 0, 1, 5, 0, 5));

			var baseIncludeAtoB = new DataFact(
				new DataFactId("inc13-a-include-v1"),
				new DataAggregateId("agg-inc13-a"),
				DataFactKind.IncludeEdge,
				documentA,
				new TextSpan(0, 0),
				snapshotVersion: 20,
				payload: CreateIncludePayload(documentB.Value));

			var baseDefinitionB = new DataFact(
				new DataFactId("inc13-b-def-v1"),
				new DataAggregateId("agg-inc13-b"),
				DataFactKind.SymbolDefinition,
				documentB,
				new TextSpan(0, 4),
				snapshotVersion: 20,
				payload: CreateSymbolFactPayload(documentB.Value, "Function", "beta", 0, 0, 0, 4, 0, 4));

			var baselineSnapshot = new CodeDatabaseSnapshot(
				version: 20,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate>
				{
					new DataAggregate(
						new DataAggregateId("agg-inc13-a"),
						DataAggregateKind.Document,
						documentA,
						"ffscript",
						"HASH-INC13-A-V1",
						1,
						new List<DataFact> { baseDefinitionA, baseReferenceA, baseIncludeAtoB }),
					new DataAggregate(
						new DataAggregateId("agg-inc13-b"),
						DataAggregateKind.Document,
						documentB,
						"ffscript",
						"HASH-INC13-B-V1",
						1,
						new List<DataFact> { baseDefinitionB }),
				},
				facts: new List<DataFact> { baseDefinitionA, baseReferenceA, baseIncludeAtoB, baseDefinitionB },
				indexSnapshot: null);

			IIndexSnapshot baselineIndex = maintainer.Rebuild(baselineSnapshot);
			Assert(baselineIndex != null, "DBMID-13A: baseline rebuild succeeded");

			var nextDefinitionA = new DataFact(
				new DataFactId("inc13-a-def-v2"),
				new DataAggregateId("agg-inc13-a"),
				DataFactKind.SymbolDefinition,
				documentA,
				new TextSpan(0, 6),
				snapshotVersion: 21,
				payload: CreateSymbolFactPayload(documentA.Value, "Function", "alpha2", 0, 0, 0, 6, 0, 6));

			var nextReferenceA = new DataFact(
				new DataFactId("inc13-a-ref-v2"),
				new DataAggregateId("agg-inc13-a"),
				DataFactKind.SymbolReference,
				documentA,
				new TextSpan(30, 6),
				snapshotVersion: 21,
				payload: CreateSymbolFactPayload(documentA.Value, "Function", "alpha2", 2, 1, 2, 7, 0, 6));

			var nextDefinitionC = new DataFact(
				new DataFactId("inc13-c-def-v1"),
				new DataAggregateId("agg-inc13-c"),
				DataFactKind.SymbolDefinition,
				documentC,
				new TextSpan(0, 5),
				snapshotVersion: 21,
				payload: CreateSymbolFactPayload(documentC.Value, "Function", "gamma", 0, 0, 0, 5, 0, 5));

			var includeCtoA = new DataFact(
				new DataFactId("inc13-c-include-v1"),
				new DataAggregateId("agg-inc13-c"),
				DataFactKind.IncludeEdge,
				documentC,
				new TextSpan(0, 0),
				snapshotVersion: 21,
				payload: CreateIncludePayload(documentA.Value));

			var nextSnapshot = new CodeDatabaseSnapshot(
				version: 21,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate>
				{
					new DataAggregate(
						new DataAggregateId("agg-inc13-a"),
						DataAggregateKind.Document,
						documentA,
						"ffscript",
						"HASH-INC13-A-V2",
						2,
						new List<DataFact> { nextDefinitionA, nextReferenceA }),
					new DataAggregate(
						new DataAggregateId("agg-inc13-b"),
						DataAggregateKind.Document,
						documentB,
						"ffscript",
						"HASH-INC13-B-V1",
						1,
						new List<DataFact> { baseDefinitionB }),
					new DataAggregate(
						new DataAggregateId("agg-inc13-c"),
						DataAggregateKind.Document,
						documentC,
						"ffscript",
						"HASH-INC13-C-V1",
						1,
						new List<DataFact> { nextDefinitionC, includeCtoA }),
				},
				facts: new List<DataFact> { nextDefinitionA, nextReferenceA, baseDefinitionB, nextDefinitionC, includeCtoA },
				indexSnapshot: null);

			Stopwatch incrementalStopwatch = Stopwatch.StartNew();
			IIndexSnapshot incrementalIndex = maintainer.Update(
				baselineIndex,
				nextSnapshot,
				new List<PathKey> { documentA, documentC });
			incrementalStopwatch.Stop();

			Stopwatch rebuildStopwatch = Stopwatch.StartNew();
			IIndexSnapshot rebuiltIndex = maintainer.Rebuild(nextSnapshot);
			rebuildStopwatch.Stop();

			Assert(incrementalIndex != null && rebuiltIndex != null, "DBMID-13B: incremental and rebuild snapshots produced");
			Debug.Log($"[DBMID-13] IncrementalMs={incrementalStopwatch.Elapsed.TotalMilliseconds:F3}, RebuildMs={rebuildStopwatch.Elapsed.TotalMilliseconds:F3}");

			var incrementalSnapshot = new CodeDatabaseSnapshot(
				nextSnapshot.Version,
				nextSnapshot.CapturedAtUtc,
				nextSnapshot.Aggregates,
				nextSnapshot.Facts,
				incrementalIndex);

			var rebuiltSnapshot = new CodeDatabaseSnapshot(
				nextSnapshot.Version,
				nextSnapshot.CapturedAtUtc,
				nextSnapshot.Aggregates,
				nextSnapshot.Facts,
				rebuiltIndex);

			var definitionRequest = SymbolQueryRequest.ForPosition("definition", documentA.Value, new TextPosition(0, 1));
			SymbolQueryResult incrementalDefinition = facade.QueryDefinition(incrementalSnapshot, definitionRequest);
			SymbolQueryResult rebuiltDefinition = facade.QueryDefinition(rebuiltSnapshot, definitionRequest);
			Assert(AreQueryResultsEquivalent(incrementalDefinition, rebuiltDefinition), "DBMID-13C: definition query equivalent between update and rebuild");

			var referencesRequest = new SymbolQueryRequest("references", documentA.Value, new TextPosition(0, 1), new TextSpan(0, 0), false, string.Empty);
			SymbolQueryResult incrementalReferences = facade.QueryReferences(incrementalSnapshot, referencesRequest);
			SymbolQueryResult rebuiltReferences = facade.QueryReferences(rebuiltSnapshot, referencesRequest);
			Assert(AreQueryResultsEquivalent(incrementalReferences, rebuiltReferences), "DBMID-13D: references query equivalent between update and rebuild");

			var completionAlphaRequest = new SymbolQueryRequest("completion", documentA.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, "alpha2");
			SymbolQueryResult incrementalCompletionAlpha = facade.QueryCompletion(incrementalSnapshot, completionAlphaRequest);
			SymbolQueryResult rebuiltCompletionAlpha = facade.QueryCompletion(rebuiltSnapshot, completionAlphaRequest);
			Assert(
				BuildCompletionLabelSignature(incrementalCompletionAlpha) == BuildCompletionLabelSignature(rebuiltCompletionAlpha)
				&& ContainsCompletionLabel(incrementalCompletionAlpha, "alpha2"),
				"DBMID-13E: completion labels equivalent and include updated symbol");

			var completionBetaRequest = new SymbolQueryRequest("completion", documentB.Value, new TextPosition(0, 0), new TextSpan(0, 0), false, "beta");
			SymbolQueryResult incrementalCompletionBeta = facade.QueryCompletion(incrementalSnapshot, completionBetaRequest);
			SymbolQueryResult rebuiltCompletionBeta = facade.QueryCompletion(rebuiltSnapshot, completionBetaRequest);
			Assert(
				BuildCompletionLabelSignature(incrementalCompletionBeta) == BuildCompletionLabelSignature(rebuiltCompletionBeta)
				&& ContainsCompletionLabel(incrementalCompletionBeta, "beta"),
				"DBMID-13F: unchanged document symbol remains queryable");

			string includesAIncremental = BuildPathListSignature(incrementalIndex.IncludeGraphIndex.GetIncludes(documentA));
			string includesARebuilt = BuildPathListSignature(rebuiltIndex.IncludeGraphIndex.GetIncludes(documentA));
			Assert(includesAIncremental == includesARebuilt && string.IsNullOrEmpty(includesAIncremental), "DBMID-13G: removed include edge is consistently absent");

			string includesCIncremental = BuildPathListSignature(incrementalIndex.IncludeGraphIndex.GetIncludes(documentC));
			string includesCRebuilt = BuildPathListSignature(rebuiltIndex.IncludeGraphIndex.GetIncludes(documentC));
			Assert(includesCIncremental == includesCRebuilt && includesCIncremental == documentA.Value, "DBMID-13H: new include edge is consistently present");

			string dependentsAIncremental = BuildPathListSignature(incrementalIndex.IncludeGraphIndex.GetDependents(documentA));
			string dependentsARebuilt = BuildPathListSignature(rebuiltIndex.IncludeGraphIndex.GetDependents(documentA));
			Assert(dependentsAIncremental == dependentsARebuilt && dependentsAIncremental == documentC.Value, "DBMID-13I: dependents graph is consistent");
		}

		// ================================================================
		// DBMID-14: DocumentKey normalization collapses URI/path aliases
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator);

			string canonicalUri = "file:///tests/key%20norm.ffs";
			string uriAlias = "  FILE:///tests/key%20norm.ffs  ";
			string pathAlias = "/tests/key norm.ffs";

			DatabaseOperationResult openResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/key-normalize",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentOpened,
						new PathKey(uriAlias),
						1,
						CreateDidOpenPayload(uriAlias, "ffscript", 1, "func entry() { wait 1 }")),
				}));

			DatabaseOperationResult changeResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/key-normalize",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentChanged,
						new PathKey(pathAlias),
						2,
						CreateDidChangePayload(pathAlias, 2, "func entry() { wait 2 }")),
				}));

			DataAggregate normalizedAggregate = changeResult?.Snapshot != null && changeResult.Snapshot.Aggregates.Count > 0
				? changeResult.Snapshot.Aggregates[0]
				: null;

			Assert(openResult != null && openResult.Succeeded, "DBMID-14A: didOpen with URI alias succeeded");
			Assert(changeResult != null && changeResult.Succeeded, "DBMID-14B: didChange with path alias succeeded");
			Assert(changeResult?.Snapshot != null && changeResult.Snapshot.Aggregates.Count == 1, "DBMID-14C: aliases collapsed to one aggregate");
			Assert(normalizedAggregate != null && normalizedAggregate.DocumentKey.Value == canonicalUri, "DBMID-14D: aggregate key normalized to canonical file URI");
			Assert(normalizedAggregate != null && normalizedAggregate.SourceVersion == 2, "DBMID-14E: latest alias update applied on canonical aggregate");
		}

		// ================================================================
		// DBMID-15: Query resolves symbols with normalized DocumentKey
		// ================================================================
		{
			var maintainer = new InMemoryIndexMaintainer();
			var facade = new InMemoryLspQueryFacade();

			string canonicalUri = "file:///tests/query%20norm.ffs";
			string pathAlias = "/tests/query norm.ffs";
			string uriAlias = " FILE:///tests/query%20norm.ffs ";

			var definitionFact = new DataFact(
				new DataFactId("norm-def"),
				new DataAggregateId("agg-norm"),
				DataFactKind.SymbolDefinition,
				new PathKey(pathAlias),
				new TextSpan(0, 5),
				snapshotVersion: 1,
				payload: CreateSymbolFactPayload(pathAlias, "Function", "entry", 0, 0, 0, 5, 0, 5));

			var aggregate = new DataAggregate(
				new DataAggregateId("agg-norm"),
				DataAggregateKind.Document,
				new PathKey(pathAlias),
				"ffscript",
				"HASH-NORM",
				1,
				new List<DataFact> { definitionFact });

			var snapshot = new CodeDatabaseSnapshot(
				version: 1,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { aggregate },
				facts: new List<DataFact> { definitionFact },
				indexSnapshot: null);

			IIndexSnapshot index = maintainer.Rebuild(snapshot);
			var indexedSnapshot = new CodeDatabaseSnapshot(
				snapshot.Version,
				snapshot.CapturedAtUtc,
				snapshot.Aggregates,
				snapshot.Facts,
				index);

			SymbolQueryResult result = facade.QueryDefinition(
				indexedSnapshot,
				SymbolQueryRequest.ForPosition("definition", uriAlias, new TextPosition(0, 1)));

			LspDefinitionPayload payload = result?.Payload != null
				? result.Payload.Definition
				: null;

			Assert(result != null && result.Succeeded, "DBMID-15A: definition query resolves with URI alias document key");
			Assert(payload != null && payload.DocumentKey == canonicalUri, "DBMID-15B: definition payload document key normalized to canonical URI");
			Assert(result != null && result.Symbol != null && result.Symbol.Origin == canonicalUri, "DBMID-15C: resolved symbol origin normalized to canonical URI");
		}

		// ================================================================
		// DBMID-16: Intent contract coverage and request intent propagation
		// ================================================================
		{
			Assert(LspIntentContractRegistry.Count == 24, "DBMID-16A: intent registry has complete 24-item protocol surface");

			bool coverageValid = LspIntentContractRegistry.ValidateBridgeCoverage(out string coverageError);
			Assert(coverageValid && string.IsNullOrEmpty(coverageError), "DBMID-16B: bridge-routed intents have valid contract coverage");

			LspIntentContract didOpenIntent = LspIntentContractRegistry.Require(LspUserIntentId.IntDs01DidOpen);
			Assert(
				didOpenIntent.OperationKind == DatabaseOperationKind.ApplyChangeSet
				&& didOpenIntent.RequiresWriteOperation
				&& didOpenIntent.WriteReason == "didOpen",
				"DBMID-16C: didOpen intent contract is write-routed with expected reason");

			LspIntentContract referencesIntent = LspIntentContractRegistry.Require(LspUserIntentId.IntQr04References);
			Assert(
				referencesIntent.OperationKind == DatabaseOperationKind.ReadSnapshot
				&& referencesIntent.RequiresReadSnapshot
				&& referencesIntent.QueryOperationName == "references",
				"DBMID-16D: references intent contract is read-routed with expected query operation");

			var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
			DatabaseOperationResult result = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/intent-code",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				intentCode: didOpenIntent.IntentCode,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentOpened,
						new PathKey("file:///tests/intent-code.ffs"),
						1,
						CreateDidOpenPayload("file:///tests/intent-code.ffs", "ffscript", 1, "func entry() { wait 1 }")),
				}));

			Assert(result != null && result.Succeeded, "DBMID-16E: intent-tagged operation succeeds");
			Assert(result != null && result.IntentCode == didOpenIntent.IntentCode, "DBMID-16F: operation result preserves intent code metadata");
		}

		// ================================================================
		// DBMID-17: willRenameFiles bridge planning + consecutive rename state
		// ================================================================
		{
			string tmpDir = Path.Combine(Path.GetTempPath(), "dbmid17_" + Guid.NewGuid().ToString("N").Substring(0, 8));
			try
			{
				Directory.CreateDirectory(tmpDir);
				File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 1 }");
				string mainSource = "include \"lib\"\nfunc main() {\n  wait 1\n}";
				File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
				File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), "{ \"includePaths\": [\".\"] }");

				string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
				string mainUri = rootUri + "/main.ffs";
				string oldUri = rootUri + "/lib.ffs";
				string renamed1Uri = rootUri + "/lib_new.ffs";
				string renamed2Uri = rootUri + "/lib_newer.ffs";

				var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
				var bridge = new DatabaseBackedVsCodeBridge(database);

				var initializeParams = new JsonObject();
				initializeParams.Set("rootUri", rootUri);
				bridge.Initialize(initializeParams);

				var didOpenParams = new JsonObject();
				var textDocument = new JsonObject();
				textDocument.Set("uri", mainUri);
				textDocument.Set("languageId", "ffscript");
				textDocument.Set("version", 1);
				textDocument.Set("text", mainSource);
				didOpenParams.Set("textDocument", textDocument);
				bridge.DidOpen(didOpenParams);

				var firstRenameParams = new JsonObject();
				var firstFiles = new List<object>();
				var firstRenameItem = new JsonObject();
				firstRenameItem.Set("oldUri", oldUri);
				firstRenameItem.Set("newUri", renamed1Uri);
				firstFiles.Add(firstRenameItem);
				firstRenameParams.Set("files", firstFiles);

				JsonObject firstResult = bridge.QueryWillRenameFiles(firstRenameParams);
				JsonObject firstChanges = firstResult != null ? firstResult.GetObject("changes") : null;
				List<object> firstMainEdits = firstChanges != null ? firstChanges.GetArray(mainUri) : null;
				bool firstRenameHasEdit = firstMainEdits != null && firstMainEdits.Count >= 1;
				string firstNewText = string.Empty;
				if (firstRenameHasEdit && firstMainEdits[0] is JsonObject firstEdit)
					firstNewText = firstEdit.GetString("newText") ?? string.Empty;

				Assert(firstRenameHasEdit, "DBMID-17A: first willRenameFiles returns workspace edits");
				Assert(firstNewText == "lib_new", "DBMID-17B: first willRenameFiles rewrites include to lib_new");

				var secondRenameParams = new JsonObject();
				var secondFiles = new List<object>();
				var secondRenameItem = new JsonObject();
				secondRenameItem.Set("oldUri", renamed1Uri);
				secondRenameItem.Set("newUri", renamed2Uri);
				secondFiles.Add(secondRenameItem);
				secondRenameParams.Set("files", secondFiles);

				JsonObject secondResult = bridge.QueryWillRenameFiles(secondRenameParams);
				JsonObject secondChanges = secondResult != null ? secondResult.GetObject("changes") : null;
				List<object> secondMainEdits = secondChanges != null ? secondChanges.GetArray(mainUri) : null;
				bool secondRenameHasEdit = secondMainEdits != null && secondMainEdits.Count >= 1;
				bool foundSecondText = false;
				if (secondMainEdits != null)
				{
					for (int i = 0; i < secondMainEdits.Count; i++)
					{
						if (secondMainEdits[i] is JsonObject secondEdit)
						{
							string candidate = secondEdit.GetString("newText") ?? string.Empty;
							if (candidate == "lib_newer")
							{
								foundSecondText = true;
								break;
							}
						}
					}
				}

				Assert(secondRenameHasEdit, "DBMID-17C: second consecutive willRenameFiles still returns edits");
				Assert(foundSecondText, "DBMID-17D: second willRenameFiles rewrites include to lib_newer");
			}
			finally
			{
				try { Directory.Delete(tmpDir, true); } catch { }
			}
		}

		// ================================================================
		// DBMID-18: diagnostics enqueue/dequeue behavior in DB-backed bridge
		// ================================================================
		{
			string uri = "file:///tests/dbmid18_diag.ffs";
			var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
			var bridge = new DatabaseBackedVsCodeBridge(database);

			var didOpenParams = new JsonObject();
			var openDoc = new JsonObject();
			openDoc.Set("uri", uri);
			openDoc.Set("languageId", "ffscript");
			openDoc.Set("version", 1);
			openDoc.Set("text", "func broken(");
			didOpenParams.Set("textDocument", openDoc);
			bridge.DidOpen(didOpenParams);

			bool hasOpenDiagnostics = bridge.TryDequeueDiagnostics(out LspPublishedDiagnostics openDiagnostics)
				&& openDiagnostics != null
				&& openDiagnostics.Uri == uri
				&& openDiagnostics.Diagnostics != null
				&& openDiagnostics.Diagnostics.Count > 0;
			Assert(hasOpenDiagnostics, "DBMID-18A: didOpen with invalid source enqueues diagnostics");

			var didChangeParams = new JsonObject();
			var changeDoc = new JsonObject();
			changeDoc.Set("uri", uri);
			changeDoc.Set("version", 2);
			didChangeParams.Set("textDocument", changeDoc);
			var changes = new List<object>();
			var fullTextChange = new JsonObject();
			fullTextChange.Set("text", "func entry() { wait 1 }");
			changes.Add(fullTextChange);
			didChangeParams.Set("contentChanges", changes);
			bridge.DidChange(didChangeParams);

			bool hasCleanDiagnostics = bridge.TryDequeueDiagnostics(out LspPublishedDiagnostics changedDiagnostics)
				&& changedDiagnostics != null
				&& changedDiagnostics.Uri == uri
				&& changedDiagnostics.Diagnostics != null
				&& changedDiagnostics.Diagnostics.Count == 0
				&& changedDiagnostics.Version == 2;
			Assert(hasCleanDiagnostics, "DBMID-18B: didChange with valid source emits empty diagnostics for clear");

			var didCloseParams = new JsonObject();
			var closeDoc = new JsonObject();
			closeDoc.Set("uri", uri);
			didCloseParams.Set("textDocument", closeDoc);
			bridge.DidClose(didCloseParams);

			bool hasCloseDiagnostics = bridge.TryDequeueDiagnostics(out LspPublishedDiagnostics closedDiagnostics)
				&& closedDiagnostics != null
				&& closedDiagnostics.Uri == uri
				&& closedDiagnostics.Diagnostics != null
				&& closedDiagnostics.Diagnostics.Count == 0;
			Assert(hasCloseDiagnostics, "DBMID-18C: didClose emits empty diagnostics to clear stale entries");

			Assert(!bridge.TryDequeueDiagnostics(out _), "DBMID-18D: diagnostics queue is drained after expected events");
		}

		// ================================================================
		// DBMID-19: diagnostics normalization contract
		// ================================================================
		{
			string uri = "file:///tests/dbmid19_diag_norm.ffs";
			var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
			var bridge = new DatabaseBackedVsCodeBridge(database);

			var didOpenParams = new JsonObject();
			var openDoc = new JsonObject();
			openDoc.Set("uri", uri);
			openDoc.Set("languageId", "ffscript");
			openDoc.Set("version", 1);
			openDoc.Set("text", "func broken(");
			didOpenParams.Set("textDocument", openDoc);
			bridge.DidOpen(didOpenParams);

			bool received = bridge.TryDequeueDiagnostics(out LspPublishedDiagnostics diagnosticsPacket)
				&& diagnosticsPacket != null
				&& diagnosticsPacket.Uri == uri
				&& diagnosticsPacket.Diagnostics != null
				&& diagnosticsPacket.Diagnostics.Count > 0;
			Assert(received, "DBMID-19A: diagnostics packet is produced for invalid source");

			JsonObject firstDiagnostic = received ? diagnosticsPacket.Diagnostics[0] as JsonObject : null;
			JsonObject range = firstDiagnostic != null ? firstDiagnostic.GetObject("range") : null;
			JsonObject start = range != null ? range.GetObject("start") : null;
			JsonObject end = range != null ? range.GetObject("end") : null;

			int severity = firstDiagnostic != null ? firstDiagnostic.GetInt("severity", 0) : 0;
			string source = firstDiagnostic != null ? firstDiagnostic.GetString("source") : null;
			string message = firstDiagnostic != null ? firstDiagnostic.GetString("message") : null;

			int startLine = start != null ? start.GetInt("line", -1) : -1;
			int startCharacter = start != null ? start.GetInt("character", -1) : -1;
			int endLine = end != null ? end.GetInt("line", -1) : -1;
			int endCharacter = end != null ? end.GetInt("character", -1) : -1;

			bool normalizedRange = startLine >= 0
				&& startCharacter >= 0
				&& endLine >= startLine
				&& (endLine > startLine || endCharacter > startCharacter);

			bool normalizedDiagnostic = firstDiagnostic != null
				&& severity >= 1
				&& severity <= 4
				&& source == "ffvm"
				&& !string.IsNullOrWhiteSpace(message)
				&& range != null
				&& normalizedRange;

			Assert(normalizedDiagnostic, "DBMID-19B: diagnostics are normalized (range/source/severity/message)");
		}

		// ================================================================
		// DBMID-20: workspace context initialization via rootPath
		// ================================================================
		{
			string tmpDir = Path.Combine(Path.GetTempPath(), "dbmid20_" + Guid.NewGuid().ToString("N").Substring(0, 8));
			try
			{
				Directory.CreateDirectory(tmpDir);
				File.WriteAllText(Path.Combine(tmpDir, "lib.ffs"), "func helper(): int { return 1 }");
				string mainSource = "include \"lib\"\nfunc main() {\n  wait 1\n}";
				File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), mainSource);
				File.WriteAllText(Path.Combine(tmpDir, "project.ffproj"), "{ \"includePaths\": [\".\"] }");

				string rootUri = "file:///" + tmpDir.TrimStart('/').Replace("\\", "/");
				string mainUri = rootUri + "/main.ffs";
				string oldUri = rootUri + "/lib.ffs";
				string newUri = rootUri + "/lib_new.ffs";

				var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
				var bridge = new DatabaseBackedVsCodeBridge(database);

				var initializeParams = new JsonObject();
				initializeParams.Set("rootPath", tmpDir);
				bridge.Initialize(initializeParams);

				var requestParams = new JsonObject();
				var files = new List<object>();
				var renameItem = new JsonObject();
				renameItem.Set("oldUri", oldUri);
				renameItem.Set("newUri", newUri);
				files.Add(renameItem);
				requestParams.Set("files", files);

				JsonObject result = bridge.QueryWillRenameFiles(requestParams);
				JsonObject changes = result != null ? result.GetObject("changes") : null;
				List<object> edits = changes != null ? changes.GetArray(mainUri) : null;
				bool hasEdit = edits != null && edits.Count > 0;

				string rewritten = string.Empty;
				if (hasEdit)
				{
					for (int i = 0; i < edits.Count; i++)
					{
						if (edits[i] is JsonObject edit)
						{
							string candidate = edit.GetString("newText") ?? string.Empty;
							if (candidate == "lib_new")
							{
								rewritten = candidate;
								break;
							}
						}
					}
				}

				Assert(hasEdit, "DBMID-20A: initialize(rootPath) enables workspace rename planning");
				Assert(rewritten == "lib_new", "DBMID-20B: rootPath initialization uses workspace/project context correctly");
			}
			finally
			{
				try { Directory.Delete(tmpDir, true); } catch { }
			}
		}

		// ================================================================
		// DBMID-21: feedback bridge flow (showMessageRequest -> applyEdit)
		// ================================================================
		{
			string tmpDir = Path.Combine(Path.GetTempPath(), "dbmid21_" + Guid.NewGuid().ToString("N").Substring(0, 8));
			try
			{
				Directory.CreateDirectory(tmpDir);
				File.WriteAllText(Path.Combine(tmpDir, "main.ffs"), "func main() { wait 1 }");

				var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
				var bridge = new DatabaseBackedVsCodeBridge(database);

				var initializeParams = new JsonObject();
				initializeParams.Set("rootPath", tmpDir);
				bridge.Initialize(initializeParams);
				bridge.Initialized(new JsonObject());

				bool hasShowMessageRequest = bridge.TryDequeueClientRequest(out LspClientRequest showRequest)
					&& showRequest != null
					&& showRequest.Method == "window/showMessageRequest"
					&& showRequest.Parameters != null
					&& showRequest.Parameters.GetArray("actions") != null
					&& showRequest.Parameters.GetArray("actions").Count == 3;
				Assert(hasShowMessageRequest, "DBMID-21A: initialized workspace without ffproj enqueues showMessageRequest");

				var createResult = new JsonObject();
				createResult.Set("title", "Create");
				bridge.HandleClientRequestResponse("window/showMessageRequest", showRequest != null ? showRequest.RequestToken : string.Empty, createResult, null);

				bool hasApplyEditRequest = bridge.TryDequeueClientRequest(out LspClientRequest applyEditRequest)
					&& applyEditRequest != null
					&& applyEditRequest.Method == "workspace/applyEdit";
				Assert(hasApplyEditRequest, "DBMID-21B: Create action response enqueues workspace/applyEdit request");

				JsonObject edit = applyEditRequest != null ? applyEditRequest.Parameters.GetObject("edit") : null;
				List<object> documentChanges = edit != null ? edit.GetArray("documentChanges") : null;
				JsonObject createFile = documentChanges != null && documentChanges.Count > 0 ? documentChanges[0] as JsonObject : null;
				JsonObject textDocumentEdit = documentChanges != null && documentChanges.Count > 1 ? documentChanges[1] as JsonObject : null;
				JsonObject textDocument = textDocumentEdit != null ? textDocumentEdit.GetObject("textDocument") : null;
				List<object> edits = textDocumentEdit != null ? textDocumentEdit.GetArray("edits") : null;
				JsonObject firstTextEdit = edits != null && edits.Count > 0 ? edits[0] as JsonObject : null;

				string createUri = createFile != null ? createFile.GetString("uri") : string.Empty;
				string textDocUri = textDocument != null ? textDocument.GetString("uri") : string.Empty;
				string newText = firstTextEdit != null ? firstTextEdit.GetString("newText") : string.Empty;
				string expectedTemplate = ProjectFile.GenerateTemplate(null);

				bool applyEditStructureValid = createFile != null
					&& createFile.GetString("kind") == "create"
					&& !string.IsNullOrWhiteSpace(createUri)
					&& createUri.EndsWith(".ffproj", StringComparison.OrdinalIgnoreCase)
					&& textDocument != null
					&& string.Equals(createUri, textDocUri, StringComparison.OrdinalIgnoreCase)
					&& firstTextEdit != null
					&& newText == expectedTemplate;
				Assert(applyEditStructureValid, "DBMID-21C: applyEdit payload contains create+template text edits for .ffproj");

				File.WriteAllText(Path.Combine(tmpDir, "existing.ffproj"), "{\n  \"includePaths\": [\".\"],\n  \"hostDeclarations\": [],\n  \"entry\": null,\n  \"compileOptions\": {}\n}\n");

				var bridgeWithExistingProject = new DatabaseBackedVsCodeBridge(new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator()));
				bridgeWithExistingProject.Initialize(initializeParams);
				bridgeWithExistingProject.Initialized(new JsonObject());

				Assert(!bridgeWithExistingProject.TryDequeueClientRequest(out _), "DBMID-21D: existing ffproj suppresses showMessageRequest bootstrap prompt");
			}
			finally
			{
				try { Directory.Delete(tmpDir, true); } catch { }
			}
		}

		// ================================================================
		// DBMID-22: implemented-intent contract consistency hardening
		// ================================================================
		{
			IReadOnlyList<LspIntentContract> contracts = LspIntentContractRegistry.All;
			bool allImplemented = true;
			bool implementedShapeConsistent = true;

			for (int i = 0; i < contracts.Count; i++)
			{
				LspIntentContract contract = contracts[i];
				if (contract == null)
				{
					allImplemented = false;
					implementedShapeConsistent = false;
					continue;
				}

				if (!contract.Implemented)
				{
					allImplemented = false;
					continue;
				}

				bool basicFieldsValid = contract.IntentId != LspUserIntentId.Unknown
					&& !string.IsNullOrWhiteSpace(contract.IntentCode)
					&& !string.IsNullOrWhiteSpace(contract.ProtocolMethod)
					&& !string.IsNullOrWhiteSpace(contract.BridgeMember)
					&& contract.Shape != LspIntentExecutionShape.Unknown;

				if (!basicFieldsValid)
				{
					implementedShapeConsistent = false;
					continue;
				}

				if (contract.RequiresWriteOperation)
				{
					if (contract.OperationKind != DatabaseOperationKind.ApplyChangeSet
						|| string.IsNullOrWhiteSpace(contract.WriteReason))
					{
						implementedShapeConsistent = false;
					}
				}

				if (contract.RequiresReadSnapshot)
				{
					if (contract.OperationKind != DatabaseOperationKind.ReadSnapshot)
						implementedShapeConsistent = false;
				}

				if (contract.Shape == LspIntentExecutionShape.DocumentWrite && !contract.RequiresWriteOperation)
					implementedShapeConsistent = false;

				if (contract.Shape == LspIntentExecutionShape.QueryRead
					&& (contract.OperationKind == DatabaseOperationKind.Unknown
						|| string.IsNullOrWhiteSpace(contract.QueryOperationName)))
				{
					implementedShapeConsistent = false;
				}
			}

			Assert(allImplemented, "DBMID-22A: all registered intents are implemented");
			Assert(implementedShapeConsistent, "DBMID-22B: implemented intents satisfy shape and operation invariants");

			bool coverageValid = LspIntentContractRegistry.ValidateBridgeCoverage(out string coverageError);
			Assert(coverageValid && string.IsNullOrWhiteSpace(coverageError), "DBMID-22C: hardened bridge coverage validation passes for implemented intents");
		}

		// ================================================================
		// DBMID-23: changedDocuments expands to include dependent closure
		// ================================================================
		{
			var orchestrator = new InMemoryDatabaseExecutionOrchestrator();
			var trackingMaintainer = new TrackingIndexMaintainer();
			var database = new InMemoryWorkspaceCodeDatabase(orchestrator, indexMaintainer: trackingMaintainer);

			var documentA = new PathKey("file:///tests/dbmid23-a.ffs");
			var documentB = new PathKey("file:///tests/dbmid23-b.ffs");
			var documentC = new PathKey("file:///tests/dbmid23-c.ffs");

			var includeAtoB = new DataFact(
				new DataFactId("dbmid23-a-include-b"),
				new DataAggregateId("agg-dbmid23-a"),
				DataFactKind.IncludeEdge,
				documentA,
				new TextSpan(0, 0),
				snapshotVersion: 30,
				payload: CreateIncludePayload(documentB.Value));

			var includeBtoC = new DataFact(
				new DataFactId("dbmid23-b-include-c"),
				new DataAggregateId("agg-dbmid23-b"),
				DataFactKind.IncludeEdge,
				documentB,
				new TextSpan(0, 0),
				snapshotVersion: 30,
				payload: CreateIncludePayload(documentC.Value));

			var aggregateA = new DataAggregate(
				new DataAggregateId("agg-dbmid23-a"),
				DataAggregateKind.Document,
				documentA,
				"ffscript",
				"HASH-DBMID23-A",
				1,
				new List<DataFact> { includeAtoB });

			var aggregateB = new DataAggregate(
				new DataAggregateId("agg-dbmid23-b"),
				DataAggregateKind.Document,
				documentB,
				"ffscript",
				"HASH-DBMID23-B",
				1,
				new List<DataFact> { includeBtoC });

			var aggregateC = new DataAggregate(
				new DataAggregateId("agg-dbmid23-c"),
				DataAggregateKind.Document,
				documentC,
				"ffscript",
				"HASH-DBMID23-C",
				1,
				new List<DataFact>());

			var replacementSnapshot = new CodeDatabaseSnapshot(
				version: 30,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { aggregateA, aggregateB, aggregateC },
				facts: new List<DataFact> { includeAtoB, includeBtoC },
				indexSnapshot: null);

			DatabaseOperationResult replaceResult = database.Execute(DatabaseOperationRequest.ReplaceSnapshot(
				replacementSnapshot,
				reason: "dbmid23-bootstrap",
				correlationId: "corr-dbmid23-bootstrap",
				priority: DatabaseOperationPriority.Normal,
				timeout: TimeSpan.FromSeconds(15)));

			Assert(replaceResult != null && replaceResult.Succeeded, "DBMID-23A: replace snapshot bootstrap succeeded");

			DatabaseOperationResult applyResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/dbmid23-c",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentChanged,
						documentC,
						2,
						new DocumentChangedWithTierChangePayload(documentC.Value, "func leaf(): int { return 2 }", DocumentSourceTier.OpenBuffer)),
				}));

			string changedSignature = BuildPathListSignature(trackingMaintainer.LastUpdatedDocuments);
			bool hasA = changedSignature.IndexOf(documentA.Value, StringComparison.OrdinalIgnoreCase) >= 0;
			bool hasB = changedSignature.IndexOf(documentB.Value, StringComparison.OrdinalIgnoreCase) >= 0;
			bool hasC = changedSignature.IndexOf(documentC.Value, StringComparison.OrdinalIgnoreCase) >= 0;

			Assert(applyResult != null && applyResult.Succeeded, "DBMID-23B: apply change on leaf document succeeded");
			Assert(trackingMaintainer.UpdateCallCount >= 1, "DBMID-23C: apply change used incremental index update");
			Assert(hasA && hasB && hasC, "DBMID-23D: changedDocuments includes transitive dependent closure (A/B/C)");
		}

		// ================================================================
		// DBMID-24: open-buffer tier wins against watcher delete in same batch
		// ================================================================
		{
			var database = new InMemoryWorkspaceCodeDatabase(new InMemoryDatabaseExecutionOrchestrator());
			var document = new PathKey("file:///tests/dbmid24-tier.ffs");
			string openBufferText = "func kept(): int { return 1 }\nfunc main() {\n    var v: int = kept()\n    wait v\n}";

			DatabaseOperationResult applyResult = database.Execute(CreateApplyChangesRequest(
				streamKey: "stream://doc/dbmid24-tier",
				priority: DatabaseOperationPriority.Normal,
				expectedVersion: null,
				createdAtUtc: DateTime.UtcNow,
				changes: new[]
				{
					new DatabaseChangeEvent(
						DatabaseChangeKind.DocumentChanged,
						document,
						2,
						new DocumentChangedWithTierChangePayload(document.Value, openBufferText, DocumentSourceTier.OpenBuffer)),
					new DatabaseChangeEvent(
						DatabaseChangeKind.WatchedFilesChanged,
						document,
						null,
						new WatchedFileChangedChangePayload(document.Value, WatchedFileChangeType.Deleted)),
				}));

			bool hasDocumentAggregate = false;
			IReadOnlyList<DataAggregate> aggregates = applyResult != null && applyResult.Snapshot != null
				? applyResult.Snapshot.Aggregates
				: null;
			if (aggregates != null)
			{
				for (int i = 0; i < aggregates.Count; i++)
				{
					DataAggregate aggregate = aggregates[i];
					if (aggregate != null
						&& string.Equals(aggregate.DocumentKey.Value, document.Value, StringComparison.OrdinalIgnoreCase))
					{
						hasDocumentAggregate = true;
						break;
					}
				}
			}

			var facade = new InMemoryLspQueryFacade();
			SymbolQueryResult definitionResult = facade.QueryDefinition(
				applyResult != null ? applyResult.Snapshot : null,
				SymbolQueryRequest.ForPosition("definition", document.Value, new TextPosition(0, 6)));

			SymbolQueryResult referencesResult = facade.QueryReferences(
				applyResult != null ? applyResult.Snapshot : null,
				new SymbolQueryRequest("references", document.Value, new TextPosition(0, 6), new TextSpan(0, 0), true, string.Empty));

			Assert(applyResult != null && applyResult.Succeeded, "DBMID-24A: mixed open-buffer+watcher-delete apply succeeded");
			Assert(hasDocumentAggregate, "DBMID-24B: watcher delete did not remove higher-tier open-buffer aggregate");
			Assert(definitionResult != null && definitionResult.Succeeded, "DBMID-24C: definition query remains available after tier arbitration");
			Assert(referencesResult != null && referencesResult.Succeeded && referencesResult.Ranges.Count >= 2, "DBMID-24D: references still include declaration and usage after watcher delete conflict");
		}

		// ================================================================
		// DBMID-25 / LSPNEW-T2-00: AliasBinding fact consumed by index,
		//   alias→target mapping queryable from snapshot
		// ================================================================
		{
			var maintainer = new InMemoryIndexMaintainer();
			var sourceDocument = new PathKey("file:///tests/alias-source.ffs");
			var targetDocument = new PathKey("file:///tests/alias-target.ffs");

			var includePayload = new IncludeEdgeDataFactPayload(targetDocument.Value);
			var includeFact = new DataFact(
				new DataFactId("inc-alias-src"),
				new DataAggregateId("agg-alias-src"),
				DataFactKind.IncludeEdge,
				sourceDocument,
				new TextSpan(0, 10),
				snapshotVersion: 1,
				payload: includePayload);

			var aliasPayload = new AliasBindingDataFactPayload("Math", targetDocument.Value);
			var aliasFact = new DataFact(
				new DataFactId("alias-math"),
				new DataAggregateId("agg-alias-src"),
				DataFactKind.AliasBinding,
				sourceDocument,
				new TextSpan(0, 10),
				snapshotVersion: 1,
				payload: aliasPayload);

			var sourceAggregate = new DataAggregate(
				new DataAggregateId("agg-alias-src"),
				DataAggregateKind.Document,
				sourceDocument,
				"ffscript",
				string.Empty,
				1,
				new List<DataFact> { includeFact, aliasFact });

			var snapshot = new CodeDatabaseSnapshot(
				version: 1,
				capturedAtUtc: DateTime.UtcNow,
				aggregates: new List<DataAggregate> { sourceAggregate },
				facts: new List<DataFact> { includeFact, aliasFact },
				indexSnapshot: null);

			IIndexSnapshot indexSnapshot = maintainer.Rebuild(snapshot);
			Assert(indexSnapshot != null, "DBMID-25A: index snapshot rebuilt with alias facts");

			bool aliasResolved = indexSnapshot.AliasIndex.TryResolveAlias(sourceDocument, "Math", out PathKey resolvedTarget);
			Assert(aliasResolved && resolvedTarget.Value == targetDocument.Value, "DBMID-25B: TryResolveAlias returns correct target for alias 'Math'");

			bool unknownNotResolved = !indexSnapshot.AliasIndex.TryResolveAlias(sourceDocument, "Unknown", out _);
			Assert(unknownNotResolved, "DBMID-25C: TryResolveAlias returns false for unknown alias");

			IReadOnlyDictionary<string, PathKey> aliases = indexSnapshot.AliasIndex.GetAliases(sourceDocument);
			Assert(aliases != null && aliases.Count == 1, "DBMID-25D: GetAliases returns exactly one binding");
			Assert(aliases.ContainsKey("Math"), "DBMID-25E: GetAliases contains 'Math' key");
		}

		Debug.Log($"[LspDatabaseTests] Completed. Passed={passed}, Failed={failed}");
	}

	private static DatabaseOperationRequest CreateApplyChangesRequest(
		string streamKey,
		DatabaseOperationPriority priority,
		long? expectedVersion,
		DateTime createdAtUtc,
		string intentCode = null,
		IReadOnlyList<DatabaseChangeEvent> changes = null)
	{
		IReadOnlyList<DatabaseChangeEvent> effectiveChanges = changes ?? new List<DatabaseChangeEvent>
		{
			new DatabaseChangeEvent(
				DatabaseChangeKind.DocumentChanged,
				new PathKey("file:///tests/virtual.ffs"),
				versionHint: 1,
				payload: CreateDidChangePayload("file:///tests/virtual.ffs", 1, "content"))
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
			createdAtUtc: createdAtUtc,
			intentCode: intentCode);
	}

	private static DocumentOpenedChangePayload CreateDidOpenPayload(string uri, string languageId, int version, string text)
	{
		return new DocumentOpenedChangePayload(uri, languageId, text);
	}

	private static DocumentChangedChangePayload CreateDidChangePayload(string uri, int version, string text)
	{
		return new DocumentChangedChangePayload(uri, text);
	}

	private static DocumentClosedChangePayload CreateDidClosePayload(string uri, int version)
	{
		return new DocumentClosedChangePayload(uri);
	}

	private static SymbolDataFactPayload CreateSymbolFactPayload(
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
		SymbolKindTag resolvedKind = SymbolKindTag.Unknown;
		if (!string.IsNullOrWhiteSpace(kind))
			Enum.TryParse(kind, true, out resolvedKind);

		var symbol = new SymbolIdentity(
			resolvedKind,
			name,
			string.Empty,
			string.Empty,
			origin,
			new TextSpan(declarationStart, declarationLength));

		return new SymbolDataFactPayload(
			symbol,
			rangeStartLine,
			rangeStartCharacter,
			rangeEndLine,
			rangeEndCharacter);
	}

	private static IncludeEdgeDataFactPayload CreateIncludePayload(string includeUri)
	{
		return new IncludeEdgeDataFactPayload(includeUri);
	}

	private static bool AreQueryResultsEquivalent(SymbolQueryResult left, SymbolQueryResult right)
	{
		if (left == null || right == null)
			return left == right;

		return left.Succeeded == right.Succeeded
			&& BuildSymbolSignature(left.Symbol) == BuildSymbolSignature(right.Symbol)
			&& BuildRangesSignature(left.Ranges) == BuildRangesSignature(right.Ranges);
	}

	private static string BuildSymbolSignature(SymbolIdentity symbol)
	{
		if (symbol == null)
			return string.Empty;

		return symbol.Kind
			+ "|" + (symbol.Name ?? string.Empty)
			+ "|" + (symbol.Scope ?? string.Empty)
			+ "|" + (symbol.ParentName ?? string.Empty)
			+ "|" + (symbol.Origin ?? string.Empty)
			+ "|" + symbol.DeclarationSpan.Start
			+ "|" + symbol.DeclarationSpan.Length;
	}

	private static string BuildRangesSignature(IReadOnlyList<TextSpan> ranges)
	{
		if (ranges == null || ranges.Count == 0)
			return string.Empty;

		var parts = new List<string>(ranges.Count);
		for (int i = 0; i < ranges.Count; i++)
		{
			TextSpan range = ranges[i];
			parts.Add(range.Start + ":" + range.Length);
		}

		return string.Join("|", parts);
	}

	private static string BuildCompletionLabelSignature(SymbolQueryResult result)
	{
		if (result == null)
			return string.Empty;

		IReadOnlyList<LspCompletionItem> items = result.Payload != null
			? result.Payload.CompletionItems
			: null;
		if (items == null || items.Count == 0)
			return string.Empty;

		var labels = new List<string>(items.Count);
		for (int i = 0; i < items.Count; i++)
		{
			LspCompletionItem item = items[i];
			if (item == null)
				continue;

			labels.Add(item.Label ?? string.Empty);
		}

		return string.Join("|", labels);
	}

	private static bool ContainsCompletionLabel(SymbolQueryResult result, string expectedLabel)
	{
		if (result == null || string.IsNullOrWhiteSpace(expectedLabel))
			return false;

		IReadOnlyList<LspCompletionItem> items = result.Payload != null
			? result.Payload.CompletionItems
			: null;
		if (items == null)
			return false;

		for (int i = 0; i < items.Count; i++)
		{
			LspCompletionItem item = items[i];
			if (item != null && string.Equals(item.Label, expectedLabel, StringComparison.OrdinalIgnoreCase))
				return true;
		}

		return false;
	}

	private static string BuildPathListSignature(IReadOnlyList<PathKey> paths)
	{
		if (paths == null || paths.Count == 0)
			return string.Empty;

		var values = new List<string>(paths.Count);
		for (int i = 0; i < paths.Count; i++)
		{
			string value = paths[i].Value;
			if (!string.IsNullOrWhiteSpace(value))
				values.Add(value);
		}

		values.Sort(StringComparer.OrdinalIgnoreCase);
		return string.Join("|", values);
	}

	private sealed class TrackingIndexMaintainer : IIndexMaintainer
	{
		private readonly InMemoryIndexMaintainer _inner = new InMemoryIndexMaintainer();

		public int RebuildCallCount { get; private set; }
		public int UpdateCallCount { get; private set; }
		public IReadOnlyList<PathKey> LastUpdatedDocuments { get; private set; } = new List<PathKey>(0);

		public IIndexSnapshot Rebuild(CodeDatabaseSnapshot snapshot)
		{
			RebuildCallCount++;
			return _inner.Rebuild(snapshot);
		}

		public IIndexSnapshot Update(
			IIndexSnapshot previous,
			CodeDatabaseSnapshot snapshot,
			IReadOnlyList<PathKey> changedDocuments)
		{
			UpdateCallCount++;
			LastUpdatedDocuments = changedDocuments ?? new List<PathKey>(0);
			return _inner.Update(previous, snapshot, changedDocuments);
		}
	}
}
