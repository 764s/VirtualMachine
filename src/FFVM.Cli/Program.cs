using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FFVM;
using FFVM.Compiler;
using FFVM.Debug;

namespace FFVM.Cli
{
    public static class Program
    {
        private const string Version = "0.1.0";

        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            string command = args[0].ToLowerInvariant();
            string[] subArgs = args.Length > 1 ? args[1..] : Array.Empty<string>();

            switch (command)
            {
                case "run":
                    return CmdRun(subArgs);
                case "compile":
                    return CmdCompile(subArgs);
                case "lsp":
                    return CmdLsp();
                case "dap":
                    return CmdDap(subArgs);
                case "version":
                case "--version":
                case "-v":
                    Console.WriteLine($"ffvm {Version}");
                    return 0;
                case "help":
                case "--help":
                case "-h":
                    PrintUsage();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown command: '{command}'");
                    PrintUsage();
                    return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine($"ffvm {Version} — FFScript Virtual Machine");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  ffvm run <script.ffs> [--entry <func>]     Compile and run a script");
            Console.WriteLine("  ffvm compile <script.ffs> [--entry <func>] Compile a script (check only)");
            Console.WriteLine("  ffvm lsp                                   Start LSP server (stdio)");
            Console.WriteLine("  ffvm dap [--port <N>]                      Start DAP server (stdio or TCP)");
            Console.WriteLine("  ffvm version                               Show version");
            Console.WriteLine("  ffvm help                                  Show this help");
        }

        // ─── run ──────────────────────────────────────────────────────────

        private static int CmdRun(string[] args)
        {
            string scriptPath = null;
            string entryFunc = "main";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--entry" && i + 1 < args.Length)
                    entryFunc = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
                }
                else
                    scriptPath = args[i];
            }

            if (scriptPath == null)
            {
                Console.Error.WriteLine("Error: no script file specified.");
                Console.Error.WriteLine("Usage: ffvm run <script.ffs> [--entry <func>]");
                return 1;
            }

            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Error: file not found: {scriptPath}");
                return 1;
            }

            // Compile
            string source = File.ReadAllText(scriptPath);
            var compiler = new BytecodeCompiler();
            var syscalls = CliSyscalls.GetSyscallMap();
            var result = compiler.Compile(source, entryFunc, syscalls);

            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine(err);
                return 1;
            }

            // Run
            var world = new VMWorld();
            world.MaxStepsPerTick = 10_000_000;
            CliSyscalls.RegisterAll(world.Syscalls);
            world.Modules.Load(0, result.Program);
            int instanceId = world.SpawnInstance(0, 0);

            if (instanceId < 0)
            {
                Console.Error.WriteLine("Error: failed to spawn VM instance.");
                return 1;
            }

            CliSyscalls.Reset();
            bool stopRequested = false;
            int frameCount = 0;

            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stopRequested = true;
            };

            while (!stopRequested && !CliSyscalls.ExitRequested)
            {
                frameCount++;
                CliSyscalls.BeginTick(frameCount);
                world.Tick();

                ref VMInstanceState inst = ref world.Pool.Instances[instanceId];

                if (inst.ErrorFlag != VMError.None)
                {
                    Console.Error.WriteLine($"VM error: {inst.ErrorFlag}");
                    return 1;
                }

                if ((inst.StateFlags & VMStateFlags.Completed) != 0)
                    break;
            }

            return 0;
        }

        // ─── compile ──────────────────────────────────────────────────────

        private static int CmdCompile(string[] args)
        {
            string scriptPath = null;
            string entryFunc = "main";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--entry" && i + 1 < args.Length)
                    entryFunc = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
                }
                else
                    scriptPath = args[i];
            }

            if (scriptPath == null)
            {
                Console.Error.WriteLine("Error: no script file specified.");
                Console.Error.WriteLine("Usage: ffvm compile <script.ffs> [--entry <func>]");
                return 1;
            }

            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Error: file not found: {scriptPath}");
                return 1;
            }

            string source = File.ReadAllText(scriptPath);
            var compiler = new BytecodeCompiler();
            var syscalls = CliSyscalls.GetSyscallMap();
            var result = compiler.Compile(source, entryFunc, syscalls);

            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine(err);
                return 1;
            }

            int instrCount = result.Program.InstructionCount;
            int constCount = result.Program.Constants.Length;
            int funcCount = result.Program.Functions.Length;
            Console.WriteLine($"OK — {instrCount} instructions, {constCount} constants, {funcCount} functions");
            return 0;
        }

        // ─── lsp ──────────────────────────────────────────────────────────

        private static int CmdLsp()
        {
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            var server = new LspServer(input, output);
            server.Run();
            return 0;
        }

        // ─── dap ──────────────────────────────────────────────────────────

        private static int CmdDap(string[] args)
        {
            // Default: stdio mode (same as StandaloneRunner --dap)
            var input = Console.OpenStandardInput();
            var output = Console.OpenStandardOutput();
            var server = new DapServer(input, output);
            server.Run();
            return 0;
        }
    }
}
