using System;

namespace Sandbox
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // When running from bin/Release/net8.0, resolve back to project root
            // Look for sandbox.json to find the correct base directory
            string projectDir = FindProjectDir(baseDir);
            if (projectDir == null)
            {
                // Fallback: use current working directory
                projectDir = Environment.CurrentDirectory;
            }

            var runner = new SandboxRunner(projectDir);

            if (args.Length > 0)
            {
                switch (args[0])
                {
                    case "--compile":
                        if (!runner.Compile(force: true))
                            Environment.Exit(1);
                        return;

                    case "--run":
                        if (!runner.Compile())
                            Environment.Exit(1);
                        if (!runner.Run())
                            Environment.Exit(1);
                        return;

                    case "--help":
                        PrintHelp();
                        return;

                    default:
                        Console.Error.WriteLine($"Unknown option: {args[0]}");
                        PrintHelp();
                        Environment.Exit(1);
                        return;
                }
            }

            // Interactive mode
            RunInteractive(runner);
        }

        private static void RunInteractive(SandboxRunner runner)
        {
            Console.WriteLine("╔══════════════════════════════════════╗");
            Console.WriteLine("║     FFScript Sandbox v1.0           ║");
            Console.WriteLine("╠══════════════════════════════════════╣");
            Console.WriteLine("║  [C] Compile    [R] Run    [Q] Quit ║");
            Console.WriteLine("╚══════════════════════════════════════╝");
            Console.WriteLine();

            while (true)
            {
                Console.Write("sandbox> ");
                string input = Console.ReadLine();
                if (input == null) break; // EOF

                input = input.Trim().ToLowerInvariant();

                switch (input)
                {
                    case "c":
                    case "compile":
                        runner.Compile(force: true);
                        break;

                    case "r":
                    case "run":
                        if (!runner.Compile())
                        {
                            Console.WriteLine("  Fix compilation errors first.");
                            break;
                        }
                        Console.WriteLine("  (Press Ctrl+C to stop a running script)");
                        runner.Run();
                        break;

                    case "q":
                    case "quit":
                    case "exit":
                        return;

                    case "h":
                    case "help":
                        Console.WriteLine("  [C]ompile  — Compile the entry script");
                        Console.WriteLine("  [R]un      — Compile (if needed) and run");
                        Console.WriteLine("  [Q]uit     — Exit the sandbox");
                        Console.WriteLine("  [H]elp     — Show this help");
                        break;

                    case "":
                        break;

                    default:
                        Console.WriteLine($"  Unknown command: '{input}'. Type 'h' for help.");
                        break;
                }
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("FFScript Sandbox — Script testing and debugging environment");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  dotnet run                   Interactive mode (compile/run menu)");
            Console.WriteLine("  dotnet run -- --compile      Compile the entry script");
            Console.WriteLine("  dotnet run -- --run          Compile and run the entry script");
            Console.WriteLine("  dotnet run -- --help         Show this help");
        }

        private static string FindProjectDir(string startDir)
        {
            string dir = startDir;
            for (int i = 0; i < 8; i++)
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(dir, "sandbox.json")))
                    return dir;
                string parent = System.IO.Path.GetDirectoryName(dir);
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            return null;
        }
    }
}
