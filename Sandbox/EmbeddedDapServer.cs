using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FFVM;
using FFVM.Compiler;
using FFVM.Debug;

namespace Sandbox
{
    /// <summary>
    /// Embedded DAP server for Sandbox.
    /// Listens on TCP, attaches to the Sandbox's own VMWorld.
    /// Designed for the workflow: Sandbox runs → VS Code attaches → breakpoints work.
    ///
    /// Threading model:
    ///   Main thread  — Sandbox game loop (Tick), pauses on breakpoint via ManualResetEvent.
    ///   DAP thread   — Reads DAP messages from TCP, handles inspect/continue/step requests.
    ///   When paused, main thread is blocked; DAP thread reads VM state safely.
    /// </summary>
    public class EmbeddedDapServer : IDisposable
    {
        private readonly int _port;
        private TcpListener _listener;
        private TcpClient _client;
        private Stream _stream;
        private Thread _thread;

        private volatile bool _listening;
        private volatile bool _connected;
        private volatile bool _running;
        private int _seq;

        // --- Synchronisation with main thread ---
        private readonly ManualResetEventSlim _resumeEvent = new ManualResetEventSlim(true); // starts signalled
        private readonly ManualResetEventSlim _configDoneEvent = new ManualResetEventSlim(false); // wait for configurationDone
        private volatile bool _paused;

        // --- VM references (set by Sandbox before Run, or on attach) ---
        private VMWorld _world;
        private VMProgram _program;
        private ScriptDebugger _debugger;
        private int _instanceId = -1;
        private string _scriptPath;

        // --- Breakpoint state ---
        private volatile bool _hitBreakpoint;
        private int _hitLine;
        private int _hitIP;
        private string _pendingStopReason;

        // --- Buffered breakpoints (set before program is compiled) ---
        private readonly List<int> _bufferedBreakpointLines = new List<int>();

        // --- Variables reference management ---
        private List<(string[] fieldNames, Number[] fieldValues)> _structExpansions;

        // --- Stop on entry ---
        private volatile bool _stopOnEntry;

        public bool IsConnected => _connected;
        public bool IsPaused => _paused;

        public EmbeddedDapServer(int port = 4711)
        {
            _port = port;
        }

        // ============================================================
        // Lifecycle
        // ============================================================

        /// <summary>Start listening on TCP. Call once at Sandbox startup.</summary>
        public void StartListening()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Server.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.Socket,
                System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            try
            {
                _listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.Error.WriteLine($"[DEBUG] Port {_port} is already in use. Is another Sandbox running?");
                Console.Error.WriteLine("[DEBUG] Close the other instance first, then retry.");
                throw;
            }
            _listening = true;

            _thread = new Thread(DapThreadMain)
            {
                Name = "EmbeddedDAP",
                IsBackground = true
            };
            _thread.Start();
        }

        /// <summary>
        /// Block until VS Code connects. Call from main thread before running the script.
        /// </summary>
        public void WaitForConnection()
        {
            Console.WriteLine("[DEBUG] Waiting for VS Code to attach... (F5 → \"Attach to Sandbox\")");
            while (!_connected && _listening)
                Thread.Sleep(100);
            if (_connected)
                Console.WriteLine("[DEBUG] VS Code attached. Starting script...");
        }

        /// <summary>
        /// Pause at the first instruction. Call after AttachToWorld, before the game loop.
        /// Sends "stopped on entry" and blocks until VS Code sends continue.
        /// </summary>
        public void StopOnEntry()
        {
            if (!_connected) return;

            // DAP spec: must wait for configurationDone before sending stopped
            Console.WriteLine("[DAP] Waiting for configurationDone...");
            _configDoneEvent.Wait();
            Console.WriteLine("[DAP] configurationDone received, sending stopped(entry)");

            _paused = true;
            _stopOnEntry = true;

            var body = new JsonObject();
            body.Set("reason", "entry");
            body.Set("threadId", 1);
            SendEvent("stopped", body);

            // Block main thread until continue/step
            _resumeEvent.Reset();
            _resumeEvent.Wait();
            _paused = false;
            _stopOnEntry = false;
        }

        /// <summary>
        /// Attach the debugger to a live VMWorld. Called by SandboxRunner after compile + world setup.
        /// </summary>
        public void AttachToWorld(VMWorld world, VMProgram program, int instanceId, string scriptPath)
        {
            _world = world;
            _program = program;
            _instanceId = instanceId;
            _scriptPath = scriptPath;

            // Set up ScriptDebugger
            _debugger = new ScriptDebugger();
            _debugger.OnBreakpointHit = OnBreakpointHitCallback;
            _debugger.HaltOnBreakpoint = true;
            _world.Debugger = _debugger;

            // Apply buffered breakpoints
            lock (_bufferedBreakpointLines)
            {
                foreach (int line in _bufferedBreakpointLines)
                    _debugger.AddBreakpoint(line);
            }
        }

        /// <summary>
        /// Detach from the current VMWorld. Called after script finishes.
        /// </summary>
        public void DetachFromWorld()
        {
            if (_world != null)
                _world.Debugger = null;
            _world = null;
            _program = null;
            _debugger = null;
            _instanceId = -1;

            // Send terminated event if connected
            if (_connected)
                SendEvent("terminated", null);

            // Ensure main thread is not stuck
            _paused = false;
            _resumeEvent.Set();
        }

        /// <summary>
        /// Called by the main thread after each Tick.
        /// If a breakpoint was hit, blocks until the DAP client sends continue/step.
        /// </summary>
        public void CheckBreakpointAndWait()
        {
            if (!_hitBreakpoint || !_connected)
                return;

            Console.WriteLine($"[DAP] CheckBreakpointAndWait: pausing at ip={_hitIP} line={_hitLine}");
            _hitBreakpoint = false;
            _paused = true;

            // Notify VS Code that we stopped
            var body = new JsonObject();
            body.Set("reason", _pendingStopReason ?? "breakpoint");
            body.Set("threadId", 1);
            SendEvent("stopped", body);

            // Block main thread until continue/step/disconnect
            _resumeEvent.Reset();
            _resumeEvent.Wait();
            _paused = false;
        }

        public void Dispose()
        {
            _listening = false;
            _running = false;
            _resumeEvent.Set(); // unblock main thread if paused

            try { _client?.Close(); } catch { }
            try { _listener?.Stop(); } catch { }
        }

        // ============================================================
        // DAP thread
        // ============================================================

        private void DapThreadMain()
        {
            while (_listening)
            {
                try
                {
                    // Wait for VS Code to connect
                    _client = _listener.AcceptTcpClient();
                    _stream = _client.GetStream();
                    _connected = true;
                    _running = true;
                    _seq = 1;
                    _configDoneEvent.Reset();

                    Console.WriteLine("[DAP] VS Code TCP connected.");

                    // Message loop
                    while (_running && _connected)
                    {
                        string messageText = ContentLengthStream.ReadMessage(_stream);
                        if (messageText == null)
                        {
                            Console.WriteLine("[DAP] Stream closed (null message).");
                            break;
                        }

                        var message = JsonObject.Parse(messageText);
                        if (message == null)
                            continue;

                        string type = message.GetString("type");
                        if (type == "request")
                            HandleRequest(message);
                    }
                }
                catch (SocketException) { Console.WriteLine("[DAP] SocketException (listener stopped)."); }
                catch (IOException ex) { Console.WriteLine($"[DAP] IOException: {ex.Message}"); }
                catch (ObjectDisposedException) { Console.WriteLine("[DAP] ObjectDisposedException (shutdown)."); }
                catch (Exception ex) { Console.WriteLine($"[DAP] Unexpected error: {ex}"); }
                finally
                {
                    _connected = false;
                    _running = false;
                    _paused = false;
                    _resumeEvent.Set(); // unblock main thread
                    _configDoneEvent.Set(); // unblock StopOnEntry if waiting

                    try { _client?.Close(); } catch { }
                    _client = null;
                    _stream = null;

                    Console.WriteLine("[DAP] VS Code disconnected.");
                }
            }
        }

        // ============================================================
        // Request dispatch
        // ============================================================

        private void HandleRequest(JsonObject request)
        {
            string command = request.GetString("command");
            int requestSeq = request.GetInt("seq");
            var arguments = request.GetObject("arguments");

            Console.WriteLine($"[DAP] ← {command} (seq={requestSeq})");

            JsonObject responseBody = null;
            bool success = true;
            string errorMessage = null;

            try
            {
                switch (command)
                {
                    case "initialize":
                        responseBody = HandleInitialize();
                        break;
                    case "attach":
                        responseBody = HandleAttach(arguments);
                        break;
                    case "setBreakpoints":
                        responseBody = HandleSetBreakpoints(arguments);
                        break;
                    case "setFunctionBreakpoints":
                        // VS Code sends this during config — return empty list
                        responseBody = new JsonObject();
                        responseBody.Set("breakpoints", new List<object>());
                        break;
                    case "setExceptionBreakpoints":
                        // VS Code ALWAYS sends this during config — must succeed
                        break;
                    case "configurationDone":
                        Console.WriteLine("[DAP]   configurationDone — signalling main thread");
                        _configDoneEvent.Set();
                        break;
                    case "threads":
                        responseBody = HandleThreads();
                        break;
                    case "continue":
                        responseBody = HandleContinue();
                        break;
                    case "next":
                        responseBody = HandleNext();
                        break;
                    case "stepIn":
                        responseBody = HandleStepIn();
                        break;
                    case "stepOut":
                        responseBody = HandleStepOut();
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
                    case "evaluate":
                        responseBody = HandleEvaluate(arguments);
                        break;
                    case "disconnect":
                        HandleDisconnect();
                        break;
                    default:
                        // DAP: unknown requests should succeed silently
                        Console.WriteLine($"[DAP]   (unhandled command '{command}', returning success)");
                        break;
                }
            }
            catch (Exception ex)
            {
                success = false;
                errorMessage = ex.Message;
                Console.WriteLine($"[DAP]   ERROR: {ex.Message}");
            }

            Console.WriteLine($"[DAP] → response {command} success={success}");
            SendResponse(command, requestSeq, success, responseBody, errorMessage);

            // DAP spec: initialized event must be sent AFTER the initialize response
            if (command == "initialize" && success)
            {
                Console.WriteLine($"[DAP] → event initialized");
                SendEvent("initialized", null);
            }
        }

        // ============================================================
        // Handlers
        // ============================================================

        private JsonObject HandleInitialize()
        {
            var body = new JsonObject();
            body.Set("supportsConfigurationDoneRequest", true);
            body.Set("supportsFunctionBreakpoints", false);
            body.Set("supportsConditionalBreakpoints", false);
            body.Set("supportsEvaluateForHovers", true);
            body.Set("supportsStepBack", false);
            body.Set("supportsSetVariable", false);
            return body;
        }

        private JsonObject HandleAttach(JsonObject arguments)
        {
            // Nothing to do — the VM is managed by Sandbox, not by us.
            // Breakpoints may already be buffered from setBreakpoints.
            return null;
        }

        private JsonObject HandleSetBreakpoints(JsonObject arguments)
        {
            var body = new JsonObject();
            var breakpointsList = new List<object>();

            // Clear existing breakpoints
            _debugger?.ClearBreakpoints();
            lock (_bufferedBreakpointLines) { _bufferedBreakpointLines.Clear(); }

            var source = arguments?.GetObject("source");
            string sourcePath = source?.GetString("path");

            var breakpointsArr = arguments?.GetArray("breakpoints");
            if (breakpointsArr != null)
            {
                foreach (var bpObj in breakpointsArr)
                {
                    int line = 0;
                    if (bpObj is JsonObject bpJson)
                        line = bpJson.GetInt("line");

                    bool verified = false;

                    if (_program?.SourceMap != null && line > 0)
                    {
                        // Program is compiled — verify against source map
                        for (int ip = 0; ip < _program.SourceMap.Length; ip++)
                        {
                            if (_program.SourceMap[ip] == line)
                            {
                                verified = true;
                                break;
                            }
                        }
                        if (verified)
                            _debugger?.AddBreakpoint(line);
                    }
                    else if (line > 0)
                    {
                        // Program not compiled yet — buffer the breakpoint, mark as verified optimistically
                        lock (_bufferedBreakpointLines) { _bufferedBreakpointLines.Add(line); }
                        verified = true;
                    }

                    var bp = new JsonObject();
                    bp.Set("verified", verified);
                    bp.Set("line", line);
                    breakpointsList.Add(bp);
                }
            }

            body.Set("breakpoints", breakpointsList);
            return body;
        }

        private JsonObject HandleThreads()
        {
            var thread = new JsonObject();
            thread.Set("id", 1);
            thread.Set("name", "FFVM Main Thread");

            var body = new JsonObject();
            body.Set("threads", new List<object> { thread });
            return body;
        }

        private JsonObject HandleContinue()
        {
            ResumeExecution("breakpoint");
            return new JsonObject();
        }

        private JsonObject HandleNext()
        {
            if (_debugger != null && _program != null && _instanceId >= 0)
            {
                ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
                int currentLine = (_program.SourceMap != null && inst.IP >= 0 && inst.IP < _program.SourceMap.Length)
                    ? _program.SourceMap[inst.IP] : -1;
                if (currentLine > 0)
                    _debugger.SetStepOverFromLine(currentLine, inst.CallStackDepth);
            }
            ResumeExecution("step");
            return new JsonObject();
        }

        private JsonObject HandleStepIn()
        {
            if (_debugger != null && _program != null && _instanceId >= 0)
            {
                ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
                int currentIP = inst.IP;
                int currentLine = (_program.SourceMap != null && currentIP >= 0 && currentIP < _program.SourceMap.Length)
                    ? _program.SourceMap[currentIP] : -1;

                // Dump nearby instructions to diagnose step-into issues
                int dumpStart = Math.Max(0, currentIP - 2);
                int dumpEnd = Math.Min(_program.Instructions.Length - 1, currentIP + 15);
                Console.WriteLine($"[DAP] === Bytecode dump IP {dumpStart}..{dumpEnd} (currentIP={currentIP}, line={currentLine}) ===");
                for (int i = dumpStart; i <= dumpEnd; i++)
                {
                    int srcLine = (i < _program.SourceMap.Length) ? _program.SourceMap[i] : -1;
                    var instr = _program.Instructions[i];
                    string marker = (i == currentIP) ? " >>>" : "    ";
                    Console.WriteLine($"[DAP]{marker} IP {i,3}: {instr.Code,-16} A={instr.A,4} B={instr.B,4}  (line {srcLine})");
                }
                Console.WriteLine("[DAP] === end dump ===");

                int targetIP = ScriptDebugger.FindStepIntoIP(_program, currentIP);
                int targetLine = (targetIP >= 0 && _program.SourceMap != null && targetIP < _program.SourceMap.Length)
                    ? _program.SourceMap[targetIP] : -1;
                Console.WriteLine($"[DAP] StepIn: currentIP={currentIP} line={currentLine} → targetIP={targetIP} targetLine={targetLine}");
                if (targetIP >= 0)
                    _debugger.SetTempBreakpoint(targetIP);
                else
                    Console.WriteLine("[DAP]   WARNING: targetIP < 0, no temp breakpoint set!");
            }
            ResumeExecution("step");
            return new JsonObject();
        }

        private JsonObject HandleStepOut()
        {
            if (_debugger != null && _program != null && _instanceId >= 0)
            {
                ref VMInstanceState inst = ref _world.Pool.Instances[_instanceId];
                int targetIP = ScriptDebugger.FindStepOutIP(ref inst);
                if (targetIP >= 0)
                    _debugger.SetTempBreakpoint(targetIP);
            }
            ResumeExecution("step");
            return new JsonObject();
        }

        private void ResumeExecution(string nextStopReason)
        {
            _pendingStopReason = nextStopReason;
            if (_debugger != null)
                _debugger.SkipNextCheck = true;
            _resumeEvent.Set(); // unblock main thread
        }

        private JsonObject HandleStackTrace()
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
                    frame.Set("column", 1);

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

        private JsonObject HandleScopes()
        {
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

            if (varRef == 1 && _debugger != null && _program != null && _instanceId >= 0)
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
            else if (varRef >= 1000 && _structExpansions != null)
            {
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

        private JsonObject HandleEvaluate(JsonObject arguments)
        {
            string expression = arguments?.GetString("expression")?.Trim();
            if (string.IsNullOrEmpty(expression))
                throw new Exception("Empty expression");

            if (_debugger == null || _program == null || _instanceId < 0)
                throw new Exception("Not paused");

            var vars = _debugger.GetVariables(_program, ref _world.Pool.Instances[_instanceId]);

            // Field access: "varName.fieldName"
            int dotIdx = expression.IndexOf('.');
            if (dotIdx > 0)
            {
                string varName = expression.Substring(0, dotIdx);
                string fieldName = expression.Substring(dotIdx + 1);
                int vi = vars.FindIndex(x => x.Name == varName);
                if (vi < 0)
                    throw new Exception($"Unknown variable '{varName}'");
                var v = vars[vi];
                if (!v.IsStruct || v.FieldNames == null)
                    throw new Exception($"'{varName}' is not a struct");
                for (int i = 0; i < v.FieldNames.Length; i++)
                {
                    if (v.FieldNames[i] == fieldName)
                    {
                        var body = new JsonObject();
                        body.Set("result", FormatNumber(v.FieldValues[i]));
                        body.Set("variablesReference", 0);
                        return body;
                    }
                }
                throw new Exception($"'{varName}' has no field '{fieldName}'");
            }

            // Simple variable name lookup
            int fi = vars.FindIndex(x => x.Name == expression);
            if (fi >= 0)
            {
                var found = vars[fi];
                var body = new JsonObject();
                body.Set("result", FormatNumber(found.Value));
                body.Set("type", found.IsStruct ? "struct" : "int");

                if (found.IsStruct && found.FieldNames != null && found.FieldValues != null)
                {
                    _structExpansions = _structExpansions ?? new List<(string[], Number[])>();
                    int refId = 1000 + _structExpansions.Count;
                    _structExpansions.Add((found.FieldNames, found.FieldValues));
                    body.Set("variablesReference", refId);
                }
                else
                {
                    body.Set("variablesReference", 0);
                }
                return body;
            }

            // Unsupported expression
            throw new Exception($"Expression evaluation not supported: '{expression}'");
        }

        private void HandleDisconnect()
        {
            _running = false;
            _paused = false;
            _resumeEvent.Set();
        }

        // ============================================================
        // Helpers
        // ============================================================

        private void OnBreakpointHitCallback(int instanceId, int ip, int line)
        {
            Console.WriteLine($"[DAP] Breakpoint hit: ip={ip} line={line}");
            _hitBreakpoint = true;
            _hitIP = ip;
            _hitLine = line;
        }

        private void SendResponse(string command, int requestSeq, bool success, JsonObject body, string errorMessage)
        {
            if (_stream == null) return;

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

            lock (_stream)
            {
                ContentLengthStream.WriteMessage(_stream, response.ToJson());
            }
        }

        private void SendEvent(string eventName, JsonObject body)
        {
            if (_stream == null) return;

            var evt = new JsonObject();
            evt.Set("seq", _seq++);
            evt.Set("type", "event");
            evt.Set("event", eventName);

            if (body != null)
                evt.Set("body", body);

            lock (_stream)
            {
                ContentLengthStream.WriteMessage(_stream, evt.ToJson());
            }
        }

        private static string FormatNumber(Number n)
        {
            int intVal = n.ToInt();
            if (Number.FromInt(intVal) == n)
                return intVal.ToString();
            return n.ToString();
        }
    }
}
