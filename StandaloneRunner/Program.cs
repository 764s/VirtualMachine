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
            var server = new FFVM.Debug.LspServer(input, output);
            server.Run();
            return;
        }

        TreeWalkerTests.RunAll();
        CompilerTests.RunAll();
        PerformanceTests.RunAll();
        SkillScriptTests.RunAll();
        DebugTests.RunAll();
        DapTests.RunAll();
        LspTests.RunAll();
    }
}
