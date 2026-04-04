using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Compiler;

namespace FFVM.Debug
{
    /// <summary>
    /// DAP (Debug Adapter Protocol) server for FFVM script debugging.
    /// Single-threaded state machine: alternates between message-reading and VM execution.
    /// 
    /// Implements 12 DAP messages (D-09 MVP):
    ///   initialize, launch, setBreakpoints, configurationDone, threads,
    ///   continue, stackTrace, scopes, variables, disconnect
    /// Events: initialized, stopped, terminated
    /// 
    /// Gate 1 target: VS Code can connect, set breakpoints, continue, and inspect state.
    /// </summary>
    public class DapServer
    {
        private readonly Stream _input;
        private readonly Stream _output;

        // --- Session state ---
        private bool _running;
        private int _seq; // outgoing message sequence number

        // --- VM state (single-session lifecycle) ---
        private VMWorld _world;
        private VMProgram _program;
        private ScriptDebugger _debugger;
        private int _instanceId = -1;
        private string _scriptPath;

        // --- Execution control ---
        private bool _hitBreakpoint;
        private int _hitInstanceId;
        private int _hitIP;
        private int _hitLine;

        // --- Variables reference management ---
        // variablesReference 1 = locals scope
        // variablesReference 1000+ = struct expansion (1000 + index in _structExpansions)
        private List<(string[] fieldNames, Number[] fieldValues)> _structExpansions;

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
                        responseBody = HandleInitialize(arguments);
                        break;
                    case "launch":
                        responseBody = HandleLaunch(arguments);
                        break;
                    case "setBreakpoints":
                        responseBody = HandleSetBreakpoints(arguments);
                        break;
                    case "configurationDone":
                        HandleConfigurationDone(arguments);
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
                        responseBody = HandleStackTrace(arguments);
                        break;
                    case "scopes":
                        responseBody = HandleScopes(arguments);
                        break;
                    case "variables":
                        responseBody = HandleVariables(arguments);
                        break;
                    case "disconnect":
                        HandleDisconnect(arguments);
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
        // Handlers
        // ============================================================

        private JsonObject HandleInitialize(JsonObject arguments)
        {
            // Return capabilities
            var body = new JsonObject();
            body.Set("supportsConfigurationDoneRequest", true);
            body.Set("supportsFunctionBreakpoints", false);
            body.Set("supportsConditionalBreakpoints", false);
            body.Set("supportsEvaluateForHovers", false);
            body.Set("supportsStepBack", false);
            body.Set("supportsSetVariable", false);

            // Send "initialized" event after responding
            SendEvent("initialized", null);

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

            // Compile the script
            var compiler = new BytecodeCompiler();
            var result = compiler.Compile(source, "main", syscalls, syscallTable);

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

        private JsonObject HandleSetBreakpoints(JsonObject arguments)
        {
            _debugger?.ClearBreakpoints();

            var body = new JsonObject();
            var breakpointsList = new List<object>();

            var breakpointsArr = arguments?.GetArray("breakpoints");
            if (breakpointsArr != null && _debugger != null)
            {
                foreach (var bpObj in breakpointsArr)
                {
                    int line = 0;
                    if (bpObj is JsonObject bpJson)
                        line = bpJson.GetInt("line");

                    bool verified = false;
                    int actualLine = line;

                    // Verify that this line exists in the source map
                    if (_program?.SourceMap != null && line > 0)
                    {
                        for (int ip = 0; ip < _program.SourceMap.Length; ip++)
                        {
                            if (_program.SourceMap[ip] == line)
                            {
                                verified = true;
                                break;
                            }
                        }
                    }

                    if (verified)
                        _debugger.AddBreakpoint(line);

                    var bp = new JsonObject();
                    bp.Set("verified", verified);
                    bp.Set("line", actualLine);
                    breakpointsList.Add(bp);
                }
            }

            body.Set("breakpoints", breakpointsList);
            return body;
        }

        private void HandleConfigurationDone(JsonObject arguments)
        {
            // Configuration phase complete. No state change needed —
            // the server processes requests sequentially.
        }

        private JsonObject HandleThreads()
        {
            // FFVM is single-threaded — report one thread
            var thread = new JsonObject();
            thread.Set("id", 1);
            thread.Set("name", "FFVM Main Thread");

            var body = new JsonObject();
            body.Set("threads", new List<object> { thread });
            return body;
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

        private JsonObject HandleStackTrace(JsonObject arguments)
        {
            var body = new JsonObject();
            var stackFrames = new List<object>();

            if (_debugger != null && _program != null && _instanceId >= 0)
            {
                var callStack = _debugger.GetCallStack(_program, ref _world.Pool.Instances[_instanceId]);
                for (int i = 0; i < callStack.Count; i++)
                {
                    var entry = callStack[i];
                    var frame = new JsonObject();
                    frame.Set("id", i);
                    frame.Set("name", entry.FunctionName);
                    frame.Set("line", entry.SourceLine);
                    frame.Set("column", 1); // FFVM source map only tracks line numbers

                    if (!string.IsNullOrEmpty(_scriptPath))
                    {
                        var source = new JsonObject();
                        source.Set("name", Path.GetFileName(_scriptPath));
                        source.Set("path", Path.GetFullPath(_scriptPath));
                        frame.Set("source", source);
                    }

                    stackFrames.Add(frame);
                }
            }

            body.Set("stackFrames", stackFrames);
            body.Set("totalFrames", stackFrames.Count);
            return body;
        }

        private JsonObject HandleScopes(JsonObject arguments)
        {
            // Single scope: Locals (all register-based variables)
            _structExpansions = new List<(string[], Number[])>();

            var scope = new JsonObject();
            scope.Set("name", "Locals");
            scope.Set("variablesReference", 1);
            scope.Set("expensive", false);

            var body = new JsonObject();
            body.Set("scopes", new List<object> { scope });
            return body;
        }

        private JsonObject HandleVariables(JsonObject arguments)
        {
            int varRef = arguments?.GetInt("variablesReference") ?? 0;
            var variablesList = new List<object>();

            if (varRef == 1)
            {
                // Locals scope — get all variables
                if (_debugger != null && _program != null && _instanceId >= 0)
                {
                    _structExpansions = _structExpansions ?? new List<(string[], Number[])>();
                    var vars = _debugger.GetVariables(_program, ref _world.Pool.Instances[_instanceId]);

                    foreach (var v in vars)
                    {
                        var variable = new JsonObject();
                        variable.Set("name", v.Name);
                        variable.Set("value", FormatNumber(v.Value));
                        variable.Set("type", v.IsStruct ? "struct" : "int");

                        if (v.IsStruct && v.FieldNames != null && v.FieldValues != null)
                        {
                            // Register a struct expansion reference
                            int refId = 1000 + _structExpansions.Count;
                            _structExpansions.Add((v.FieldNames, v.FieldValues));
                            variable.Set("variablesReference", refId);
                        }
                        else
                        {
                            variable.Set("variablesReference", 0);
                        }

                        variablesList.Add(variable);
                    }
                }
            }
            else if (varRef >= 1000 && _structExpansions != null)
            {
                // Struct expansion — return fields
                int index = varRef - 1000;
                if (index >= 0 && index < _structExpansions.Count)
                {
                    var (fieldNames, fieldValues) = _structExpansions[index];
                    for (int i = 0; i < fieldNames.Length; i++)
                    {
                        var variable = new JsonObject();
                        variable.Set("name", fieldNames[i]);
                        variable.Set("value", FormatNumber(fieldValues[i]));
                        variable.Set("type", "int");
                        variable.Set("variablesReference", 0);
                        variablesList.Add(variable);
                    }
                }
            }

            var body = new JsonObject();
            body.Set("variables", variablesList);
            return body;
        }

        private void HandleDisconnect(JsonObject arguments)
        {
            _world = null;
            _program = null;
            _debugger = null;
            _instanceId = -1;
            _running = false;
        }

        // ============================================================
        // Helpers
        // ============================================================

        private void OnBreakpointHitCallback(int instanceId, int ip, int line)
        {
            _hitBreakpoint = true;
            _hitInstanceId = instanceId;
            _hitIP = ip;
            _hitLine = line;
        }

        private void SendResponse(string command, int requestSeq, bool success, JsonObject body, string errorMessage)
        {
            var response = new JsonObject();
            response.Set("seq", _seq++);
            response.Set("type", "response");
            response.Set("request_seq", requestSeq);
            response.Set("success", success);
            response.Set("command", command);

            if (body != null)
                response.Set("body", body);

            if (!success && errorMessage != null)
                response.Set("message", errorMessage);

            ContentLengthStream.WriteMessage(_output, response.ToJson());
        }

        private void SendEvent(string eventName, JsonObject body)
        {
            var evt = new JsonObject();
            evt.Set("seq", _seq++);
            evt.Set("type", "event");
            evt.Set("event", eventName);

            if (body != null)
                evt.Set("body", body);

            ContentLengthStream.WriteMessage(_output, evt.ToJson());
        }

        private static string FormatNumber(Number n)
        {
            // Use integer representation if it's a whole number
            int intVal = n.ToInt();
            if (Number.FromInt(intVal) == n)
                return intVal.ToString();
            return n.ToString();
        }
    }
}
