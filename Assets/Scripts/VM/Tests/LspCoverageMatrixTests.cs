// Responsibility:
//   Executable guard for the T3 searchable-unit coverage matrix.
// Owns:
//   LspCoverageMatrixTests.RunAll — validates matrix shape, implemented-cell TestId
//   cross-reference against on-disk test files, and emits a deterministic report.
// Inputs/Outputs:
//   In: CoverageMatrixRegistry (static), repository test source files on disk.
//   Out: [PASS]/[FAIL] log lines compatible with existing LspDatabaseQueryTests harness.
// Allowed Dependencies:
//   - System IO/collections and CoverageMatrixRegistry.
// Forbidden Dependencies:
//   - Parser/AST/query internals (kept at contract boundary).
// Invariants:
//   - LCM-01 fails closed when ValidateCoverage reports any shape error.
//   - LCM-02 fails closed if any implemented cell's TestId is not found in test sources.
// Boundary Closure:
//   Upstream: StandaloneRunner entry.
//   Downstream: none.

using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Debug.Lsp.Database.Contracts;
using UnityEngine;

public static class LspCoverageMatrixTests
{
#if UNITY_EDITOR
	[UnityEditor.MenuItem("TestVM/RunLspCoverageMatrixTests")]
#endif
	public static void RunAll()
	{
		int passed = 0;
		int failed = 0;
		TestHarness.BeginSuite("LspCoverageMatrixTests");

		void Assert(bool condition, string testName)
		{
			if (condition) passed++; else failed++;
			TestHarness.Assert(condition, testName);
		}

		// LCM-01: registry shape is valid.
		bool ok = CoverageMatrixRegistry.ValidateCoverage(out string error);
		Assert(ok, "LCM-01: CoverageMatrixRegistry.ValidateCoverage == true" + (ok ? string.Empty : " (" + error + ")"));

		// LCM-02: every implemented TestId appears in the repository's test sources.
		string testsRoot = ResolveTestsRoot();
		string haystack = LoadHaystack(testsRoot);
		int missing = 0;
		for (int i = 0; i < CoverageMatrixRegistry.All.Count; i++)
		{
			CoverageMatrixCell cell = CoverageMatrixRegistry.All[i];
			if (cell == null || !cell.Implemented)
				continue;
			// Test ids authored inside this very file are a valid source too (e.g. LCM-*).
			if (cell.TestId.StartsWith("LCM-", StringComparison.Ordinal))
				continue;
			if (haystack.IndexOf(cell.TestId, StringComparison.Ordinal) < 0)
			{
				Debug.LogError("[FAIL] LCM-02: TestId not found in sources: " + cell.TestId + " for " + cell.CellKey);
				missing++;
			}
		}
		Assert(missing == 0, "LCM-02: all implemented cells have discoverable TestIds");

		// LCM-03: deterministic report summary.
		int total = CoverageMatrixRegistry.Count;
		int impl = CoverageMatrixRegistry.ImplementedCount();
		Debug.Log("[REPORT] LCM: cells=" + total + " implemented=" + impl + " pending=" + (total - impl));
		Assert(total > 0 && impl > 0, "LCM-03: report emits non-empty matrix");

		Debug.Log("LspCoverageMatrixTests: " + passed + " passed, " + failed + " failed.");
		TestHarness.EndSuite();
	}

	private static string ResolveTestsRoot()
	{
		string cwd = Directory.GetCurrentDirectory();
		string candidate = Path.Combine(cwd, "Assets", "Scripts", "VM", "Tests");
		if (Directory.Exists(candidate))
			return candidate;
		// Fallback: walk up a few levels (useful when run from bin/Debug output).
		DirectoryInfo dir = new DirectoryInfo(cwd);
		for (int i = 0; i < 6 && dir != null; i++)
		{
			string probe = Path.Combine(dir.FullName, "Assets", "Scripts", "VM", "Tests");
			if (Directory.Exists(probe))
				return probe;
			dir = dir.Parent;
		}
		return candidate;
	}

	private static string LoadHaystack(string testsRoot)
	{
		if (!Directory.Exists(testsRoot))
			return string.Empty;
		string[] files = Directory.GetFiles(testsRoot, "*.cs", SearchOption.TopDirectoryOnly);
		var parts = new List<string>(files.Length);
		for (int i = 0; i < files.Length; i++)
		{
			try
			{
				parts.Add(File.ReadAllText(files[i]));
			}
			catch (IOException)
			{
			}
		}
		return string.Join("\n", parts);
	}
}
