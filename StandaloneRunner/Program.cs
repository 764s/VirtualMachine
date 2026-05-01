public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench")
        {
            BenchmarkRunner.RunAll();
            return;
        }

        if (args.Length > 0 && args[0] == "--dap")
        {
            // DAP server mode: communicate via stdin/stdout
            var input = System.Console.OpenStandardInput();
            var output = System.Console.OpenStandardOutput();
            var server = new FFVM.Debug.DapServer(input, output);
            server.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--lsp")
        {
            // LSP server mode: communicate via stdin/stdout
            var input = System.Console.OpenStandardInput();
            var output = System.Console.OpenStandardOutput();
            var database = new FFVM.Debug.Lsp.Database.InMemoryWorkspaceCodeDatabase(
                new FFVM.Debug.Lsp.Database.InMemoryDatabaseExecutionOrchestrator());
            var bridge = new FFVM.Debug.Lsp.Integration.VsCode.DatabaseBackedVsCodeBridge(database);
            var server = new FFVM.Debug.LspServerNew(input, output, bridge);
            server.Run();
            return;
        }

        if (args.Length > 0 && args[0] == "--lsp-new-tests")
        {
            LspServerNewTests.RunAll();
            return;
        }

        TreeWalkerTests.RunAll();
        CompilerTests.RunAll();
        PerformanceTests.RunAll();
        FFScriptTests.RunAll();
        DebugTests.RunAll();
        DapTests.RunAll();
        LspDatabaseTests.RunAll();
        LspDatabaseQueryTests.RunAll();
        LspServerNewTests.RunAll();
        LspCoverageMatrixTests.RunAll();
        VOM1Tests.RunAll();
        VOM2Tests.RunAll();
        VOM3Tests.RunAll();
        VOM4Tests.RunAll();
        VOM5Tests.RunAll();
        VOM6Tests.RunAll();
        VOM7Tests.RunAll();
        VOM11Tests.RunAll();

        // Centralized fail-fast: any [FAIL] always goes through
        // UnityEngine.Debug.LogError (legacy suites and TestHarness alike),
        // so LogErrorCount is the single source of truth. TestHarness.TotalFailed
        // is exposed for diagnostic logging only.
        int failed = UnityEngine.Debug.LogErrorCount;
        int harnessPassed = TestHarness.TotalPassed;
        int harnessFailed = TestHarness.TotalFailed;
        if (failed > 0)
        {
            System.Console.Error.WriteLine(
                $"===== ALL TESTS: failed={failed} (harness passed={harnessPassed} failed={harnessFailed}) =====");
            System.Console.Error.WriteLine(
                "::error::One or more tests FAILED (exit code 1)");
            System.Environment.Exit(1);
        }
        System.Console.WriteLine(
            $"===== ALL TESTS: failed=0 (harness passed={harnessPassed}) =====");
    }
}
