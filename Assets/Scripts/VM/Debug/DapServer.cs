using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Compiler;

namespace FFVM.Debug
{
    /// <summary>
    /// DAP (Debug Adapter Protocol) server for FFVM script debugging — launch mode.
    /// Single-threaded state machine: alternates between message-reading and VM execution.
    /// Inherits shared protocol handlers from <see cref="DapServerBase"/>.
    /// 
    /// Gate 1 target: VS Code can connect, set breakpoints, continue, and inspect state.
    /// </summary>
    public class DapServer : DapServerBase
    {
        private readonly Stream _input;
        private readonly Stream _output;

        // --- Session state ---
        private bool _running;

        /// <summary>
        /// Maximum number of ticks to execute during a single "continue" request before timing out.
        /// If the VM does not hit a breakpoint or complete within this many ticks, a "terminated"
        /// event is sent. Default: 100000. Increase for scripts with long-running loops or waits.
        /// </summary>
        public int MaxContinueTicks = 100000;

        public DapServer(Stream input, Stream output)
        {
            _input = input;
            _output = output;
        }

        /// <summary>
        /// Main loop: read DAP messages and dispatch to handlers.
        /// Blocks until disconnect or stream close.
        /// </summary>
        public void Run()
        {
            _running = true;
            _seq = 1;

            while (_running)
            {
                string messageText = ContentLengthStream.ReadMessage(_input);
                if (messageText == null)
                    break; // Stream closed

                var message = JsonObject.Parse(messageText);
                if (message == null)
                    continue;

                string type = message.GetString("type");
                if (type == "request")
                {
                    HandleRequest(message);
                }
                // Ignore other message types (events from client, etc.)
            }
        }

        protected override void WriteMessage(string json)
        {
            ContentLengthStream.WriteMessage(_output, json);
        }

        private void HandleRequest(JsonObject request)
        {
            string command = request.GetString("command");
            int requestSeq = request.GetInt("seq");
            var arguments = request.GetObject("arguments");

            JsonObject responseBody = null;
            bool success = true;
            string errorMessage = null;

            try
            {
                switch (command)
                {
                    case "initialize":
                        responseBody = HandleInitialize(false);
                        // Send "initialized" event after responding
                        SendEvent("initialized", null);
                        break;
                    case "launch":
                        responseBody = HandleLaunch(arguments);
                        break;
                    case "setBreakpoints":
                        responseBody = HandleSetBreakpointsLaunch(arguments);
                        break;
                    case "configurationDone":
                        // Configuration phase complete. No state change needed —
                        // the server processes requests sequentially.
                        break;
                    case "threads":
                        responseBody = HandleThreads();
                        break;
                    case "continue":
                        responseBody = HandleContinue(arguments);
                        break;
                    case "next":
                        responseBody = HandleNext(arguments);
                        break;
                    case "stepIn":
                        responseBody = HandleStepIn(arguments);
                        break;
                    case "stepOut":
                        responseBody = HandleStepOut(arguments);
                        break;
                    case "stackTrace":
                        responseBody = HandleStackTrace();
                        break;
                    case "scopes":
                        responseBody = HandleScopes();
                        break;
                    case "variables":
                        responseBody = HandleVariables(arguments);
                        break;
                    case "disconnect":
                        HandleDisconnect();
                        break;
                    default:
                        success = false;
                        errorMessage = $"Unknown command: {command}";
                        break;
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
            }

            SendResponse(command, requestSeq, success, responseBody, errorMessage);
        }

        // ============================================================
        // Launch-mode specific handlers
        // ============================================================

        /// <summary>
        /// Launch-mode setBreakpoints with <c>source.path</c> + file-id routing (DAP-F Phase 2).
        ///
        /// <list type="bullet">
        /// <item>If <c>source.path</c> matches the launched <c>_scriptPath</c>, route to slot 0,
        /// fileId 0 — the main compilation unit.</item>
        /// <item>If <c>source.path</c> matches an included file recorded in
        /// <see cref="VMProgram.SourceFiles"/> (via <see cref="VMProgram.TryFindFileId"/>),
        /// route to slot 0 with that fileId so breakpoints in headers actually verify and hit.</item>
        /// <item>If <c>source.path</c> is provided but cannot be resolved to a known compilation unit,
        /// return all entries as <c>verified:false</c> without touching debugger state.</item>
        /// <item>If <c>source.path</c> is missing, fall back to the legacy line-only path for back-compat.</item>
        /// </list>
        /// </summary>
        private JsonObject HandleSetBreakpointsLaunch(JsonObject arguments)
        {
            var source = arguments?.GetObject("source");
            string sourcePath = source?.GetString("path");

            if (string.IsNullOrEmpty(sourcePath))
            {
                // No path provided — preserve legacy single-file behaviour.
                return HandleSetBreakpointsCore(arguments);
            }

            if (!string.IsNullOrEmpty(_scriptPath) && PathsEqual(sourcePath, _scriptPath))
            {
                // Main script — slot 0, fileId 0.
                return HandleSetBreakpointsForModule(arguments, 0, _program, 0);
            }

            // DAP-F Phase 2: try resolving against included files recorded in SourceFiles.
            int fileId = _program != null ? _program.TryFindFileId(sourcePath) : -1;
            if (fileId > 0)
            {
                return HandleSetBreakpointsForModule(arguments, 0, _program, fileId);
            }

            // Foreign / unknown file — return unverified, do not mutate any debugger state.
            return BuildAllUnverifiedResponse(arguments);
        }

        private static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            try
            {
                string fa = Path.GetFullPath(a);
                string fb = Path.GetFullPath(b);
                return string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static JsonObject BuildAllUnverifiedResponse(JsonObject arguments)
        {
            var body = new JsonObject();
            var breakpointsList = new List<object>();
            var breakpointsArr = arguments?.GetArray("breakpoints");
            if (breakpointsArr != null)
            {
                foreach (var bpObj in breakpointsArr)
                {
                    int line = 0;
                    if (bpObj is JsonObject bpJson)
                        line = bpJson.GetInt("line");
                    var bp = new JsonObject();
                    bp.Set("verified", false);
                    bp.Set("line", line);
                    breakpointsList.Add(bp);
                }
            }
            body.Set("breakpoints", breakpointsList);
            return body;
        }

        private JsonObject HandleLaunch(JsonObject arguments)
        {
            _scriptPath = arguments?.GetString("program");
            if (string.IsNullOrEmpty(_scriptPath))
                throw new InvalidOperationException("launch: 'program' argument is required");

            if (!File.Exists(_scriptPath))
                throw new InvalidOperationException($"launch: file not found: {_scriptPath}");

            string source = File.ReadAllText(_scriptPath);

            // E002: Load syscall declarations from .ffvm.d.json if specified
            var syscalls = new Dictionary<string, int>();
            var syscallTable = new SyscallTable();
            string syscallDeclPath = arguments?.GetString("syscallDecl");
            if (!string.IsNullOrEmpty(syscallDeclPath))
            {
                // Resolve relative path against script directory
                if (!Path.IsPathRooted(syscallDeclPath))
                {
                    string scriptDir = Path.GetDirectoryName(_scriptPath) ?? ".";
                    syscallDeclPath = Path.Combine(scriptDir, syscallDeclPath);
                }
                if (File.Exists(syscallDeclPath))
                {
                    string declJson = File.ReadAllText(syscallDeclPath);
                    syscalls = syscallTable.LoadDeclarationJson(declJson);
                }
            }

            // Compile the script. DAP-F Phase 2: enable include resolution so cross-file
            // breakpoints (set on included files) can be verified against SourceFileMap.
            var compiler = new BytecodeCompiler();
            string scriptDirForIncludes = Path.GetDirectoryName(Path.GetFullPath(_scriptPath)) ?? ".";
            var fileResolver = new FileSystemFileResolver(scriptDirForIncludes);
            string mainAbsPath = Path.GetFullPath(_scriptPath);
            var result = compiler.Compile(source, "main", syscalls, syscallTable, fileResolver, mainAbsPath);

            if (!result.Success)
                throw new InvalidOperationException($"launch: compilation failed: {string.Join("; ", result.Errors)}");

            _program = result.Program;

            // Create VM world
            _world = new VMWorld();
            _world.Modules.Load(0, _program);

            // Copy syscall table state (no-op handlers + signatures) to world
            // E002: Load declarations into the world's syscall table
            if (!string.IsNullOrEmpty(syscallDeclPath) && File.Exists(syscallDeclPath))
            {
                string declJson = File.ReadAllText(syscallDeclPath);
                _world.Syscalls.LoadDeclarationJson(declJson);
            }

            // Attach debugger
            _debugger = new ScriptDebugger();
            _debugger.OnBreakpointHit = OnBreakpointHitCallback;
            _debugger.HaltOnBreakpoint = true; // DAP mode: VM yields at breakpoints
            _world.Debugger = _debugger;

            // Spawn instance at entry point
            _instanceId = _world.SpawnInstance(0, 0);

            return null;
        }

        private JsonObject HandleContinue(JsonObject arguments)
        {
            if (_world == null || _instanceId < 0)
                throw new InvalidOperationException("continue: no active session");

            // Only skip the first breakpoint check when resuming from a breakpoint hit.
            // On initial continue (after launch), do not skip — we want to catch breakpoints
            // even at the very first instruction (IP=0).
            return RunUntilBreakpoint("breakpoint", skipFirstCheck: _hitBreakpoint);
        }

        private JsonObject HandleNext(JsonObject arguments)
        {
            if (_world == null || _instanceId < 0 || _program == null || _debugger == null)
                throw new InvalidOperationException("next: no active session");

            ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
            int currentLine = (_program.SourceMap != null && inst.IP >= 0 && inst.IP < _program.SourceMap.Length)
                ? _program.SourceMap[inst.IP] : -1;
            if (currentLine > 0)
                _debugger.SetStepOverFromLine(currentLine, inst.CallStackDepth);

            return RunUntilBreakpoint("step", skipFirstCheck: true);
        }

        private JsonObject HandleStepIn(JsonObject arguments)
        {
            if (_world == null || _instanceId < 0 || _program == null || _debugger == null)
                throw new InvalidOperationException("stepIn: no active session");

            ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
            int targetIP = ScriptDebugger.FindStepIntoIP(_program, inst.IP);
            if (targetIP >= 0)
                _debugger.SetTempBreakpoint(targetIP);

            return RunUntilBreakpoint("step", skipFirstCheck: true);
        }

        private JsonObject HandleStepOut(JsonObject arguments)
        {
            if (_world == null || _instanceId < 0 || _debugger == null)
                throw new InvalidOperationException("stepOut: no active session");

            ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
            int targetIP = ScriptDebugger.FindStepOutIP(ref inst);
            if (targetIP >= 0)
                _debugger.SetTempBreakpoint(targetIP);

            return RunUntilBreakpoint("step", skipFirstCheck: true);
        }

        /// <summary>
        /// Shared execution loop: tick the VM until a breakpoint is hit, the instance completes, or timeout.
        /// Used by continue, next, stepIn, and stepOut handlers.
        /// </summary>
        /// <param name="stoppedReason">The reason string for the stopped event ("breakpoint" or "step").</param>
        /// <param name="skipFirstCheck">If true, sets SkipNextCheck to avoid re-triggering the current breakpoint.</param>
        private JsonObject RunUntilBreakpoint(string stoppedReason, bool skipFirstCheck)
        {
            _hitBreakpoint = false;

            if (skipFirstCheck && _debugger != null)
                _debugger.SkipNextCheck = true;

            // Execute ticks until breakpoint or completion
            for (int t = 0; t < MaxContinueTicks; t++)
            {
                ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];

                // Check if instance has finished
                if ((inst.StateFlags & VMStateFlags.Completed) != 0 || inst.ErrorFlag != VMError.None)
                {
                    _debugger?.ClearTempBreakpoint();
                    SendEvent("terminated", null);
                    return new JsonObject();
                }

                _world.Tick();

                // Check if breakpoint was hit during this tick
                if (_hitBreakpoint)
                {
                    var stoppedBody = new JsonObject();
                    stoppedBody.Set("reason", stoppedReason);
                    stoppedBody.Set("threadId", 1);
                    SendEvent("stopped", stoppedBody);
                    return new JsonObject();
                }

                // Re-check completion after tick
                inst = ref _world.Pool.Instances[_instanceId];
                if ((inst.StateFlags & VMStateFlags.Completed) != 0 || inst.ErrorFlag != VMError.None)
                {
                    _debugger?.ClearTempBreakpoint();
                    SendEvent("terminated", null);
                    return new JsonObject();
                }
            }

            // Timeout — send terminated
            _debugger?.ClearTempBreakpoint();
            SendEvent("terminated", null);
            return new JsonObject();
        }

        private void HandleDisconnect()
        {
            _world = null;
            _program = null;
            _debugger = null;
            _instanceId = -1;
            _running = false;
        }
    }
}
