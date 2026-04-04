using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FFVM;
using FFVM.Compiler;

namespace Sandbox
{
    /// <summary>
    /// Configuration loaded from sandbox.json.
    /// </summary>
    public class SandboxConfig
    {
        public string EntryScript { get; set; } = "scripts/main.ffs";
        public string EntryFunction { get; set; } = "main";
    }

    /// <summary>
    /// Compile and run engine for the FFScript Sandbox.
    /// Supports incremental compilation and a continuous game-loop style runner.
    /// </summary>
    public class SandboxRunner
    {
        private readonly string _baseDir;
        private SandboxConfig _config;

        // Compilation state
        private VMProgram _program;
        private DateTime _lastCompileSourceTime;
        private bool _compiled;

        // Run state
        private VMWorld _world;
        private int _instanceId = -1;
        private volatile bool _stopRequested;
        private int _frameCount;

        /// <summary>Target frames per second for the run loop (default: 60).</summary>
        public int TargetFps = 60;

        /// <summary>Max bytecode instructions per tick (default: 10000000).
        /// Set high for sandbox — scripts that don't use wait should complete in one tick.
        /// Scripts using wait will yield between ticks naturally.</summary>
        public int MaxStepsPerTick = 10_000_000;

        /// <summary>Max ticks before auto-exit in non-loop mode. 0 = unlimited (loop until exit/Ctrl+C).</summary>
        public int MaxTicks = 0;

        public SandboxRunner(string baseDir)
        {
            _baseDir = baseDir;
        }

        /// <summary>
        /// Load sandbox.json configuration.
        /// </summary>
        public bool LoadConfig()
        {
            string configPath = Path.Combine(_baseDir, "sandbox.json");
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[ERROR] Configuration file not found: {configPath}");
                Console.Error.WriteLine("  Create sandbox.json with entryScript and entryFunction fields.");
                return false;
            }

            string json = File.ReadAllText(configPath);
            _config = ParseConfig(json);
            return true;
        }

        /// <summary>
        /// Compile the entry script. Returns true on success.
        /// Performs incremental check: skips if source file has not changed.
        /// </summary>
        public bool Compile(bool force = false)
        {
            if (_config == null && !LoadConfig())
                return false;

            string scriptPath = Path.Combine(_baseDir, _config.EntryScript);
            if (!File.Exists(scriptPath))
            {
                Console.Error.WriteLine($"[ERROR] Entry script not found: {scriptPath}");
                return false;
            }

            // Incremental check
            var fileTime = File.GetLastWriteTimeUtc(scriptPath);
            if (!force && _compiled && fileTime == _lastCompileSourceTime)
            {
                Console.WriteLine("[COMPILE] Source unchanged, skipping.");
                return true;
            }

            Console.WriteLine($"[COMPILE] Compiling {_config.EntryScript} ...");
            var startTime = DateTimeOffset.UtcNow;

            string source = File.ReadAllText(scriptPath);
            var compiler = new BytecodeCompiler();
            var syscalls = SandboxSyscalls.GetSyscallMap();
            var result = compiler.Compile(source, _config.EntryFunction, syscalls);

            var elapsed = DateTimeOffset.UtcNow - startTime;

            if (!result.Success)
            {
                Console.Error.WriteLine("[COMPILE] FAILED:");
                foreach (var err in result.Errors)
                    Console.Error.WriteLine($"  {err}");
                return false;
            }

            _program = result.Program;
            _lastCompileSourceTime = fileTime;
            _compiled = true;

            int instrCount = _program.InstructionCount;
            int constCount = _program.Constants.Length;
            int funcCount = _program.Functions.Length;
            Console.WriteLine($"[COMPILE] OK — {instrCount} instructions, {constCount} constants, {funcCount} functions ({elapsed.TotalMilliseconds:F1}ms)");
            return true;
        }

        /// <summary>
        /// Run the last compiled program. Returns true if completed without errors.
        /// The run loop simulates a game loop at TargetFps, calling Tick() each frame.
        /// Scripts that don't use wait/yield will complete in a single tick.
        /// </summary>
        public bool Run()
        {
            if (_program == null)
            {
                Console.Error.WriteLine("[RUN] No compiled program. Compile first.");
                return false;
            }

            Console.WriteLine("[RUN] Starting...");
            SandboxSyscalls.Reset();
            _stopRequested = false;
            _frameCount = 0;

            // Set up VM world
            _world = new VMWorld();
            _world.MaxStepsPerTick = MaxStepsPerTick;
            SandboxSyscalls.RegisterAll(_world.Syscalls);
            _world.Modules.Load(0, _program);
            _instanceId = _world.SpawnInstance(0, 0);

            if (_instanceId < 0)
            {
                Console.Error.WriteLine("[RUN] Failed to spawn VM instance.");
                return false;
            }

            int tickIntervalMs = 1000 / TargetFps;
            var runStart = DateTimeOffset.UtcNow;
            bool completed = false;
            bool errored = false;

            // Ctrl+C handler
            Console.CancelKeyPress += OnCancelKey;

            try
            {
                while (!_stopRequested && !SandboxSyscalls.ExitRequested)
                {
                    _frameCount++;
                    SandboxSyscalls.BeginTick(_frameCount);
                    _world.Tick();

                    ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];

                    // Check for errors
                    if (inst.ErrorFlag != VMError.None)
                    {
                        Console.Error.WriteLine($"[RUN] VM error at frame {_frameCount}: {inst.ErrorFlag}");
                        errored = true;
                        break;
                    }

                    // Check if instance completed
                    if ((inst.StateFlags & VMStateFlags.Completed) != 0)
                    {
                        completed = true;
                        break;
                    }

                    // Max ticks limit
                    if (MaxTicks > 0 && _frameCount >= MaxTicks)
                    {
                        Console.WriteLine($"[RUN] Max ticks reached ({MaxTicks}).");
                        break;
                    }

                    // Sleep to maintain target FPS
                    Thread.Sleep(tickIntervalMs);
                }
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKey;
            }

            var runElapsed = DateTimeOffset.UtcNow - runStart;

            if (_stopRequested)
                Console.WriteLine($"[RUN] Interrupted after {_frameCount} frames ({runElapsed.TotalSeconds:F2}s).");
            else if (SandboxSyscalls.ExitRequested)
                Console.WriteLine($"[RUN] Exit requested by script after {_frameCount} frames ({runElapsed.TotalSeconds:F2}s).");
            else if (completed)
                Console.WriteLine($"[RUN] Completed in {_frameCount} frames ({runElapsed.TotalSeconds:F2}s).");
            else if (errored)
                Console.WriteLine($"[RUN] Stopped due to error after {_frameCount} frames.");

            return completed || SandboxSyscalls.ExitRequested;
        }

        /// <summary>
        /// Request the run loop to stop (thread-safe).
        /// </summary>
        public void RequestStop()
        {
            _stopRequested = true;
        }

        private void OnCancelKey(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true; // Prevent process termination
            _stopRequested = true;
        }

        // --- Simple JSON config parser (no dependencies) ---

        private static SandboxConfig ParseConfig(string json)
        {
            var config = new SandboxConfig();

            string entryScript = ExtractJsonString(json, "entryScript");
            if (entryScript != null)
                config.EntryScript = entryScript;

            string entryFunction = ExtractJsonString(json, "entryFunction");
            if (entryFunction != null)
                config.EntryFunction = entryFunction;

            return config;
        }

        private static string ExtractJsonString(string json, string key)
        {
            string pattern = $"\"{key}\"";
            int idx = json.IndexOf(pattern, StringComparison.Ordinal);
            if (idx < 0) return null;

            idx = json.IndexOf(':', idx + pattern.Length);
            if (idx < 0) return null;

            idx = json.IndexOf('"', idx + 1);
            if (idx < 0) return null;

            int start = idx + 1;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;

            return json.Substring(start, end - start);
        }
    }
}
