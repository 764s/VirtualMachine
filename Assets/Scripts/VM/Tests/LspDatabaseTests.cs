using System;
using System.Collections.Generic;
using FFVM.Debug.Lsp.Database;
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

		Debug.Log($"[LspDatabaseTests] Completed. Passed={passed}, Failed={failed}");
	}

	private static DatabaseOperationRequest CreateApplyChangesRequest(
		string streamKey,
		DatabaseOperationPriority priority,
		long? expectedVersion,
		DateTime createdAtUtc)
	{
		var changes = new List<DatabaseChangeEvent>
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
			changes,
			expectedVersion: expectedVersion,
			reason: "test",
			correlationId: "corr-lspdb",
			priority: priority,
			timeout: TimeSpan.FromSeconds(15),
			streamKey: streamKey,
			streamBehavior: behavior,
			createdAtUtc: createdAtUtc);
	}
}
