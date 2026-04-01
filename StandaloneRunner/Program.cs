public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--bench")
        {
            BenchmarkRunner.RunAll();
            return;
        }

        TreeWalkerTests.RunAll();
        CompilerTests.RunAll();
        PerformanceTests.RunAll();
        SkillScriptTests.RunAll();
    }
}
