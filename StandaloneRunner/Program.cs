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
    }
}
