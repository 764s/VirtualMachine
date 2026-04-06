using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace FFVM.Debug
{
    /// <summary>
    /// Embeddable DAP server for attach-mode debugging — public API for FFVM consumers.
    /// Listens on TCP, attaches to a host application's VMWorld.
    /// Designed for the workflow: Host app runs → VS Code attaches → breakpoints work.
    ///
    /// Threading model:
    ///   Main thread  — Host game loop (Tick), pauses on breakpoint via ManualResetEvent.
    ///   DAP thread   — Reads DAP messages from TCP, handles inspect/continue/step requests.
    ///   When paused, main thread is blocked; DAP thread reads VM state safely.
    ///
    /// Usage:
    ///   var dap = new EmbeddableDapServer(port: 4711);
    ///   dap.StartListening();
    ///   // ... compile program, create world, spawn instance ...
    ///   dap.AttachToWorld(world, program, instanceId, "script.ffs");
    ///   // Normal attach (no blocking): debugger connects whenever, VM keeps running
    ///   // Custom attach (sandbox mode): dap.WaitForConnection(); dap.StopOnEntry();
    ///   // Game loop: world.Tick(); dap.CheckBreakpointAndWait();
    ///   dap.DetachFromWorld();
    ///   dap.Dispose();
    /// </summary>
    public class EmbeddableDapServer : DapServerBase, IDisposable
    {
        private readonly int _port;
        private TcpListener _listener;
        private TcpClient _client;
        private Stream _stream;
        private Thread _thread;

        private volatile bool _listening;
        private volatile bool _connected;
        private volatile bool _running;

        // --- Synchronisation with main thread ---
        private readonly ManualResetEventSlim _resumeEvent = new ManualResetEventSlim(true); // starts signalled
        private readonly ManualResetEventSlim _configDoneEvent = new ManualResetEventSlim(false); // wait for configurationDone
        private volatile bool _paused;

        // --- Breakpoint state ---
        private string _pendingStopReason;

        // --- Buffered breakpoints (set before program is compiled) ---
        private readonly List<int> _bufferedBreakpointLines = new List<int>();

        // --- Stop on entry ---
        private volatile bool _stopOnEntry;

        /// <summary>Whether a VS Code client is currently connected.</summary>
        public bool IsConnected => _connected;

        /// <summary>Whether the VM is currently paused at a breakpoint.</summary>
        public bool IsPaused => _paused;

        public EmbeddableDapServer(int port = 4711)
        {
            _port = port;
        }

        // ============================================================
        // Lifecycle
        // ============================================================

        /// <summary>Start listening on TCP. Call once at host startup.</summary>
        public void StartListening()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Server.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress, true);
            try
            {
                _listener.Start();
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                Console.Error.WriteLine($"[DEBUG] Port {_port} is already in use. Is another instance running?");
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
        /// This is an optional call for "custom attach" (sandbox mode).
        /// Normal attach does not require this — the debugger connects whenever it's ready.
        /// </summary>
        public void WaitForConnection()
        {
            Console.WriteLine("[DEBUG] Waiting for VS Code to attach...");
            while (!_connected && _listening)
                Thread.Sleep(100);
            if (_connected)
                Console.WriteLine("[DEBUG] VS Code attached.");
        }

        /// <summary>
        /// Pause at the first instruction. Call after AttachToWorld, before the game loop.
        /// Sends "stopped on entry" and blocks until VS Code sends continue.
        /// This is an optional call for "custom attach" (sandbox mode).
        /// Normal attach does not require this.
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
        /// Attach the debugger to a live VMWorld. Called by host after compile + world setup.
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
        // DapServerBase overrides
        // ============================================================

        protected override void WriteMessage(string json)
        {
            if (_stream == null) return;

            lock (_stream)
            {
                ContentLengthStream.WriteMessage(_stream, json);
            }
        }

        protected override bool OnBreakpointNotVerifiable(int line)
        {
            // Program not compiled yet — buffer the breakpoint, mark as verified optimistically
            lock (_bufferedBreakpointLines) { _bufferedBreakpointLines.Add(line); }
            return true;
        }

        protected override void OnClearBufferedBreakpoints()
        {
            lock (_bufferedBreakpointLines) { _bufferedBreakpointLines.Clear(); }
        }

        protected override void OnBreakpointHitCallback(int instanceId, int ip, int line)
        {
            Console.WriteLine($"[DAP] Breakpoint hit: ip={ip} line={line}");
            base.OnBreakpointHitCallback(instanceId, ip, line);
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
                        responseBody = HandleInitialize(true);
                        break;
                    case "attach":
                        // Nothing to do — the VM is managed by the host, not by us.
                        // Breakpoints may already be buffered from setBreakpoints.
                        break;
                    case "setBreakpoints":
                        responseBody = HandleSetBreakpointsCore(arguments);
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
                        responseBody = HandleContinueAttach();
                        break;
                    case "next":
                        responseBody = HandleNextAttach();
                        break;
                    case "stepIn":
                        responseBody = HandleStepInAttach();
                        break;
                    case "stepOut":
                        responseBody = HandleStepOutAttach();
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
        // Attach-mode specific handlers
        // ============================================================

        private JsonObject HandleContinueAttach()
        {
            ResumeExecution("breakpoint");
            return new JsonObject();
        }

        private JsonObject HandleNextAttach()
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

        private JsonObject HandleStepInAttach()
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

        private JsonObject HandleStepOutAttach()
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

        private void HandleDisconnect()
        {
            _running = false;
            _paused = false;
            _resumeEvent.Set();
        }
    }
}
