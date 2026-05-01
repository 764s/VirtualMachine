using UnityEngine;

/// <summary>
/// Centralized test harness for FFVM test suites.
///
/// All ~18 standalone test suites under <c>Assets/Scripts/VM/Tests/</c> route
/// their pass/fail counters through this harness via thin <c>Assert</c>
/// shims that delegate to <see cref="Assert(bool, string)"/>. Suites bracket
/// their work with <see cref="BeginSuite"/> / <see cref="EndSuite"/> so we get:
///
///   1. structured per-suite summaries (Passed/Failed counts + begin/end banners),
///   2. cross-suite aggregate totals visible to <c>Program.Main</c> for the
///      final exit-code decision (single source of truth: any
///      <see cref="Debug.LogError"/> still trips fail-fast independently),
///   3. consistent <c>[PASS]</c>/<c>[FAIL]</c> log format usable by CI grep
///      tools as a belt-and-suspenders fallback.
///
/// New suites should follow the same convention: call <c>BeginSuite</c> at the
/// top of <c>RunAll()</c>, <c>EndSuite</c> at the bottom, and route every
/// assertion through <see cref="Assert(bool, string)"/> (directly or via a
/// thin local shim that also maintains a local pass/fail counter for the
/// suite's own end-of-run summary line).
///
/// Typical usage:
/// <code>
/// public static class MyTests
/// {
///     public static void RunAll()
///     {
///         TestHarness.BeginSuite("MyTests");
///         TestHarness.Assert(1 + 1 == 2, "addition");
///         TestHarness.Assert(true, "tautology");
///         TestHarness.EndSuite();
///     }
/// }
/// </code>
/// </summary>
public static class TestHarness
{
    private static int s_totalPassed;
    private static int s_totalFailed;

    private static string s_currentSuite;
    private static int s_suitePassed;
    private static int s_suiteFailed;
    private static bool s_suiteOpen;

    /// <summary>Aggregate count of passing assertions across all suites.</summary>
    public static int TotalPassed => s_totalPassed;

    /// <summary>Aggregate count of failing assertions across all suites.</summary>
    public static int TotalFailed => s_totalFailed;

    /// <summary>Begin a named suite. Must be paired with <see cref="EndSuite"/>.</summary>
    public static void BeginSuite(string suiteName)
    {
        if (s_suiteOpen)
        {
            // Auto-close the previous suite to keep the harness robust against
            // test files that forget to call EndSuite.
            EndSuite();
        }
        s_currentSuite = suiteName ?? "<unnamed>";
        s_suitePassed = 0;
        s_suiteFailed = 0;
        s_suiteOpen = true;
        Debug.Log($"===== Suite begin: {s_currentSuite} =====");
    }

    /// <summary>
    /// Record a single assertion. Failing assertions emit
    /// <c>[FAIL]</c> via <see cref="Debug.LogError"/>, which both increments
    /// <c>UnityEngine.Debug.LogErrorCount</c> in the StandaloneRunner stub and
    /// matches the legacy CI grep pattern.
    /// </summary>
    public static void Assert(bool condition, string testName)
    {
        if (condition)
        {
            s_suitePassed++;
            System.Threading.Interlocked.Increment(ref s_totalPassed);
            Debug.Log($"[PASS] {testName}");
        }
        else
        {
            s_suiteFailed++;
            System.Threading.Interlocked.Increment(ref s_totalFailed);
            Debug.LogError($"[FAIL] {testName}");
        }
    }

    /// <summary>
    /// End the current suite and emit a summary line.</summary>
    public static void EndSuite()
    {
        if (!s_suiteOpen) return;
        Debug.Log(
            $"===== Suite end: {s_currentSuite} | passed={s_suitePassed} failed={s_suiteFailed} =====");
        s_suiteOpen = false;
        s_currentSuite = null;
    }

    /// <summary>
    /// Record an already-logged passing assertion in the harness counters.
    /// Use this for cases where the test wants to print a custom log line
    /// (e.g. <c>[PASS-WARN]</c> with perf metrics) but still wants the result
    /// counted by the centralized harness for the final ALL TESTS summary.
    /// Does NOT itself emit any <c>[PASS]</c> output.
    /// </summary>
    public static void RecordPass()
    {
        s_suitePassed++;
        System.Threading.Interlocked.Increment(ref s_totalPassed);
    }
}
