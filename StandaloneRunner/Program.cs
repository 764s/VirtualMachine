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

        if (args.Length > 0 && args[0] == "--lsp-legacy")
        {
            // Legacy LSP server mode retained for fallback diagnostics
            var input = System.Console.OpenStandardInput();
            var output = System.Console.OpenStandardOutput();
            var server = new FFVM.Debug.LspServer(input, output);
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
        LspTests.RunAll();
        LspDatabaseTests.RunAll();
        LspDatabaseQueryTests.RunAll();
        LspServerNewTests.RunAll();
    }
}
