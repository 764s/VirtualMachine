using System;

namespace Sandbox
{
    public static class Program
    {
        public static void Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            // When running from bin/Release/net10.0, resolve back to project root
            // Look for sandbox.json to find the correct base directory
            string projectDir = FindProjectDir(baseDir);
            if (projectDir == null)
            {
                // Fallback: use current working directory
                projectDir = Environment.CurrentDirectory;
            }

            var runner = new SandboxRunner(projectDir);

            // Check for --debug flag (can appear anywhere in args)
            bool debugMode = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--debug")
                {
                    debugMode = true;
                    break;
                }
            }

            if (debugMode)
                runner.EnableDebug();

            // Find the first non-debug arg to determine mode
            string modeArg = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] != "--debug")
                {
                    modeArg = args[i];
                    break;
                }
            }

            if (modeArg != null)
            {
                switch (modeArg)
                {
                    case "--compile":
                        if (!runner.Compile(force: true))
                            Environment.Exit(1);
                        runner.StopDebug();
                        return;

                    case "--run":
                        if (!runner.Compile())
                            Environment.Exit(1);
                        if (!runner.Run())
                            Environment.Exit(1);
                        runner.StopDebug();
                        return;

                    case "--help":
                        PrintHelp();
                        return;

                    default:
                        Console.Error.WriteLine($"Unknown option: {modeArg}");
                        PrintHelp();
                        Environment.Exit(1);
                        return;
                }
            }

            // Default: interactive mode (compile FFS → menu → play mode → menu)
            RunInteractive(runner);
            runner.StopDebug();
        }

        private static void RunInteractive(SandboxRunner runner)
        {
            Console.WriteLine("╔════════════════════════════════════════════╗");
            Console.WriteLine("║     FFScript Sandbox v1.0                 ║");
            Console.WriteLine("╠════════════════════════════════════════════╣");
            Console.WriteLine("║  [R] Run (play mode)    [Q] Quit          ║");
            Console.WriteLine("║  [C] Compile            [H] Help          ║");
            Console.WriteLine("╚════════════════════════════════════════════╝");
            Console.WriteLine();

            // Auto-compile FFS on startup
            runner.Compile();
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
                        Console.WriteLine("  ► Play mode: Ctrl+C = stop script, Ctrl+C again = exit play mode");
                        runner.RunContinuous();
                        Console.WriteLine("  ■ Play mode ended.");
                        break;

                    case "q":
                    case "quit":
                    case "exit":
                        return;

                    case "h":
                    case "help":
                        Console.WriteLine("  [R]un      — Enter play mode (continuous execution, auto-reload on save)");
                        Console.WriteLine("  [C]ompile  — Compile the entry script");
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
            Console.WriteLine("  Sandbox.exe                  Interactive mode (compile → menu → play)");
            Console.WriteLine("  Sandbox.exe --debug          Interactive mode + DAP debugger (port 4711)");
            Console.WriteLine("  Sandbox.exe --compile        Compile the entry script once");
            Console.WriteLine("  Sandbox.exe --run            Compile and run once, then exit");
            Console.WriteLine("  Sandbox.exe --help           Show this help");
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
