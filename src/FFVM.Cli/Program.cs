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
                case "init":
                    return CmdInit(subArgs);
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
            Console.WriteLine("  ffvm init [--host <preset>]                Create a .ffproj project file");
            Console.WriteLine("  ffvm run <script.ffs> [options]            Compile and run a script");
            Console.WriteLine("  ffvm compile <script.ffs> [options]        Compile a script (check only)");
            Console.WriteLine("  ffvm lsp                                   Start LSP server (stdio)");
            Console.WriteLine("  ffvm dap [--port <N>]                      Start DAP server (stdio or TCP)");
            Console.WriteLine("  ffvm version                               Show version");
            Console.WriteLine("  ffvm help                                  Show this help");
            Console.WriteLine();
            Console.WriteLine("Options for run/compile:");
            Console.WriteLine("  --entry <func>          Entry function name (default: main)");
            Console.WriteLine("  --project <path.ffproj> Use project file for include paths, host");
            Console.WriteLine("                          declarations, and compile options");
        }

        // ─── init ─────────────────────────────────────────────────────────

        /// <summary>
        /// DX4-P2: Scaffold a .ffproj project file in the current directory.
        /// Generates a commented JSON template. Optionally fills hostDeclarations
        /// for a known --host preset.
        /// </summary>
        internal static int CmdInit(string[] args)
        {
            string hostPreset = null;
            string outputDir = Directory.GetCurrentDirectory();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--host" && i + 1 < args.Length)
                    hostPreset = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
                }
            }

            string fileName = "project.ffproj";
            string outputPath = Path.Combine(outputDir, fileName);

            if (File.Exists(outputPath))
            {
                Console.Error.WriteLine($"Error: {fileName} already exists in the current directory.");
                return 1;
            }

            string content = GenerateProjectTemplate(hostPreset);
            File.WriteAllText(outputPath, content);
            Console.WriteLine($"Created {fileName}");

            if (hostPreset != null)
                Console.WriteLine($"  Host preset: {hostPreset}");

            return 0;
        }

        /// <summary>
        /// DX4-P2: Generate the JSON content for a .ffproj project template.
        /// </summary>
        internal static string GenerateProjectTemplate(string hostPreset)
        {
            return ProjectFile.GenerateTemplate(hostPreset);
        }

        // ─── run ──────────────────────────────────────────────────────────

        private static int CmdRun(string[] args)
        {
            string scriptPath = null;
            string entryFunc = null;
            string projectPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--entry" && i + 1 < args.Length)
                    entryFunc = args[++i];
                else if (args[i] == "--project" && i + 1 < args.Length)
                    projectPath = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
                }
                else
                    scriptPath = args[i];
            }

            // DX4-P2: When --project is specified, load .ffproj for defaults
            ProjectFile project = null;
            if (projectPath != null)
            {
                project = LoadProject(projectPath);
                if (project == null) return 1;
            }

            // Resolve script path from project entry if not specified explicitly
            if (scriptPath == null && project != null && project.Entry != null)
                scriptPath = project.ResolvePath(project.Entry);

            // Entry function: CLI flag > default "main"
            if (entryFunc == null)
                entryFunc = "main";

            if (scriptPath == null)
            {
                Console.Error.WriteLine("Error: no script file specified.");
                Console.Error.WriteLine("Usage: ffvm run <script.ffs> [--entry <func>] [--project <path.ffproj>]");
                return 1;
            }

            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Error: file not found: {scriptPath}");
                return 1;
            }

            // Build compilation context
            var ctx = BuildCompileContext(project, scriptPath);

            // Compile
            string source = File.ReadAllText(scriptPath);
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, entryFunc, ctx.Syscalls, null,
                ctx.FileResolver, ctx.FilePath, null, ctx.Options);

            if (result.Warnings != null)
            {
                foreach (var warn in result.Warnings)
                    Console.Error.WriteLine($"warning: {warn}");
            }

            if (!result.Success)
            {
                foreach (var err in result.Errors)
                    Console.Error.WriteLine(err);
                return 1;
            }

            // Run
            var world = new VMWorld();
            world.MaxStepsPerTick = 10_000_000;
            CliSyscalls.RegisterAll(world.Syscalls, result.Program.StringConstants);
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
            string entryFunc = null;
            string projectPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--entry" && i + 1 < args.Length)
                    entryFunc = args[++i];
                else if (args[i] == "--project" && i + 1 < args.Length)
                    projectPath = args[++i];
                else if (args[i].StartsWith("-"))
                {
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 1;
                }
                else
                    scriptPath = args[i];
            }

            // DX4-P2: When --project is specified, load .ffproj for defaults
            ProjectFile project = null;
            if (projectPath != null)
            {
                project = LoadProject(projectPath);
                if (project == null) return 1;
            }

            // Resolve script path from project entry if not specified explicitly
            if (scriptPath == null && project != null && project.Entry != null)
                scriptPath = project.ResolvePath(project.Entry);

            // Entry function: CLI flag > default "main"
            if (entryFunc == null)
                entryFunc = "main";

            if (scriptPath == null)
            {
                Console.Error.WriteLine("Error: no script file specified.");
                Console.Error.WriteLine("Usage: ffvm compile <script.ffs> [--entry <func>] [--project <path.ffproj>]");
                return 1;
            }

            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"Error: file not found: {scriptPath}");
                return 1;
            }

            // Build compilation context
            var ctx = BuildCompileContext(project, scriptPath);

            string source = File.ReadAllText(scriptPath);
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, entryFunc, ctx.Syscalls, null,
                ctx.FileResolver, ctx.FilePath, null, ctx.Options);

            if (result.Warnings != null)
            {
                foreach (var warn in result.Warnings)
                    Console.Error.WriteLine($"warning: {warn}");
            }

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

        // ─── project helpers ──────────────────────────────────────────────

        /// <summary>
        /// DX4-P2: Load and validate a .ffproj project file.
        /// Returns null and writes error to stderr on failure.
        /// </summary>
        private static ProjectFile LoadProject(string projectPath)
        {
            if (!File.Exists(projectPath))
            {
                Console.Error.WriteLine($"Error: project file not found: {projectPath}");
                return null;
            }

            string json = File.ReadAllText(projectPath);
            string dirName = Path.GetDirectoryName(projectPath);
            string projectDir = string.IsNullOrEmpty(dirName)
                ? Directory.GetCurrentDirectory()
                : Path.GetFullPath(dirName);
            var project = ProjectFile.Parse(json, projectDir);

            if (project == null)
            {
                Console.Error.WriteLine($"Error: failed to parse project file: {projectPath}");
                return null;
            }

            return project;
        }

        /// <summary>
        /// DX4-P2: Compilation context holding syscalls, file resolver, and options
        /// assembled from a ProjectFile and/or script path.
        /// </summary>
        internal class CompileContext
        {
            public Dictionary<string, int> Syscalls;
            public IFileResolver FileResolver;
            public string FilePath;
            public CompileOptions Options;
        }

        /// <summary>
        /// DX4-P2: Build compilation context from an optional ProjectFile and a script path.
        /// When a project is provided, its includePaths → FileResolver, hostDeclarations → syscalls,
        /// and compileOptions are used. CLI built-in syscalls are always included.
        /// </summary>
        internal static CompileContext BuildCompileContext(ProjectFile project, string scriptPath)
        {
            var ctx = new CompileContext();

            // Start with CLI built-in syscalls
            ctx.Syscalls = CliSyscalls.GetSyscallMap();

            // File path relative name for diagnostics
            ctx.FilePath = scriptPath;

            if (project != null)
            {
                // Build file resolver from project includePaths
                ctx.FileResolver = project.BuildFileResolver();

                // Load host declarations → merge into syscall map
                project.LoadHostDeclarations(ctx.Syscalls);

                // Use project compile options
                ctx.Options = project.CompileOptions;
            }
            else
            {
                // Without a project, use the script's directory as include root
                string scriptDir = Path.GetDirectoryName(Path.GetFullPath(scriptPath));
                if (!string.IsNullOrEmpty(scriptDir))
                    ctx.FileResolver = new FileSystemFileResolver(scriptDir);
            }

            return ctx;
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
