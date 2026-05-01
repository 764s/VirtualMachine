using UnityEngine;

/// <summary>
/// Optional centralized test harness for FFVM test suites.
///
/// Existing legacy suites (TreeWalkerTests, CompilerTests, VOM*Tests, ...) keep
/// their local <c>int passed/failed</c> + <c>Assert</c>-lambda pattern and are
/// fully captured by <see cref="UnityEngine.Debug"/>'s LogErrorCount in
/// StandaloneRunner. New suites SHOULD opt in to this harness so we get:
///
///   1. structured per-suite summaries (Passed/Failed counts),
///   2. cross-suite aggregate totals visible to <c>Program.Main</c> for the
///      final exit-code decision,
///   3. consistent <c>[PASS]</c>/<c>[FAIL]</c> log format usable by CI grep
///      tools as a belt-and-suspenders fallback.
///
/// Migration is incremental and OPT-IN — there is no requirement to convert
/// legacy suites in a single change. See Docs/Plan for the rollout plan.
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

    /// <summary>End the current suite and emit a summary line.</summary>
    public static void EndSuite()
    {
        if (!s_suiteOpen) return;
        Debug.Log(
            $"===== Suite end: {s_currentSuite} | passed={s_suitePassed} failed={s_suiteFailed} =====");
        s_suiteOpen = false;
        s_currentSuite = null;
    }
}
