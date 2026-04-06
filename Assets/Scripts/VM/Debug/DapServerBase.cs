using System;
using System.Collections.Generic;
using System.IO;
using FFVM.Compiler;

namespace FFVM.Debug
{
    /// <summary>
    /// Abstract base class for DAP (Debug Adapter Protocol) servers.
    /// Provides shared protocol handling (initialize, threads, stackTrace, scopes, variables,
    /// evaluate, setBreakpoints, sendResponse, sendEvent) used by both launch-mode
    /// (<see cref="DapServer"/>) and attach-mode (<see cref="EmbeddableDapServer"/>).
    /// </summary>
    public abstract class DapServerBase
    {
        // --- VM state ---
        protected VMWorld _world;
        protected VMProgram _program;
        protected ScriptDebugger _debugger;
        protected int _instanceId = -1;
        protected string _scriptPath;

        // --- Breakpoint hit state ---
        protected volatile bool _hitBreakpoint;
        protected int _hitIP;
        protected int _hitLine;

        // --- Variables reference management ---
        // variablesReference 1 = locals scope
        // variablesReference 1000+ = struct expansion (1000 + index in _structExpansions)
        protected List<(string[] fieldNames, Number[] fieldValues)> _structExpansions;

        // --- Outgoing message sequence number ---
        protected int _seq;

        // ============================================================
        // Abstract: subclass-specific output
        // ============================================================

        /// <summary>
        /// Write a DAP message (JSON) to the output channel.
        /// Subclasses provide the transport (stdio stream, TCP stream with lock, etc.).
        /// </summary>
        protected abstract void WriteMessage(string json);

        // ============================================================
        // Shared handlers
        // ============================================================

        /// <summary>
        /// Handle the "initialize" request. Returns capabilities body.
        /// </summary>
        /// <param name="supportsEvaluateForHovers">Whether to advertise evaluate-for-hovers support.</param>
        protected JsonObject HandleInitialize(bool supportsEvaluateForHovers)
        {
            var body = new JsonObject();
            body.Set("supportsConfigurationDoneRequest", true);
            body.Set("supportsFunctionBreakpoints", false);
            body.Set("supportsConditionalBreakpoints", false);
            body.Set("supportsEvaluateForHovers", supportsEvaluateForHovers);
            body.Set("supportsStepBack", false);
            body.Set("supportsSetVariable", false);
            return body;
        }

        /// <summary>
        /// Handle the "threads" request. FFVM is single-threaded — reports one thread.
        /// </summary>
        protected JsonObject HandleThreads()
        {
            var thread = new JsonObject();
            thread.Set("id", 1);
            thread.Set("name", "FFVM Main Thread");

            var body = new JsonObject();
            body.Set("threads", new List<object> { thread });
            return body;
        }

        /// <summary>
        /// Handle the "stackTrace" request. Returns call stack frames with source info.
        /// </summary>
        protected JsonObject HandleStackTrace()
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

        /// <summary>
        /// Handle the "scopes" request. Returns a single "Locals" scope.
        /// </summary>
        protected JsonObject HandleScopes()
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

        /// <summary>
        /// Handle the "variables" request. Returns locals or struct field expansions.
        /// </summary>
        protected JsonObject HandleVariables(JsonObject arguments)
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

        /// <summary>
        /// Handle the "evaluate" request. Supports variable lookup and struct field access.
        /// </summary>
        protected JsonObject HandleEvaluate(JsonObject arguments)
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

            throw new Exception($"Expression evaluation not supported: '{expression}'");
        }

        /// <summary>
        /// Handle "setBreakpoints" core logic: verify breakpoints against source map and add them.
        /// Calls <see cref="OnBreakpointNotVerifiable"/> for lines that cannot be verified
        /// (e.g., program not yet compiled in attach mode).
        /// </summary>
        protected JsonObject HandleSetBreakpointsCore(JsonObject arguments)
        {
            _debugger?.ClearBreakpoints();
            OnClearBufferedBreakpoints();

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

                    bool verified = false;

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
                        if (verified)
                            _debugger?.AddBreakpoint(line);
                    }
                    else if (line > 0)
                    {
                        // Program not compiled yet — let subclass decide (buffer or ignore)
                        verified = OnBreakpointNotVerifiable(line);
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

        /// <summary>
        /// Called when a breakpoint line cannot be verified because the program is not yet compiled.
        /// Override to buffer the breakpoint for later application.
        /// Returns true if the breakpoint should be reported as "verified" (optimistic).
        /// Default: returns false (no buffering).
        /// </summary>
        protected virtual bool OnBreakpointNotVerifiable(int line)
        {
            return false;
        }

        /// <summary>
        /// Called at the start of setBreakpoints to clear any buffered breakpoints.
        /// Override if the subclass buffers breakpoints before program compilation.
        /// </summary>
        protected virtual void OnClearBufferedBreakpoints()
        {
        }

        // ============================================================
        // Shared helpers
        // ============================================================

        /// <summary>
        /// Callback for <see cref="ScriptDebugger.OnBreakpointHit"/>.
        /// </summary>
        protected virtual void OnBreakpointHitCallback(int instanceId, int ip, int line)
        {
            _hitBreakpoint = true;
            _hitIP = ip;
            _hitLine = line;
        }

        /// <summary>
        /// Send a DAP response message.
        /// </summary>
        protected void SendResponse(string command, int requestSeq, bool success, JsonObject body, string errorMessage)
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

            WriteMessage(response.ToJson());
        }

        /// <summary>
        /// Send a DAP event message.
        /// </summary>
        protected void SendEvent(string eventName, JsonObject body)
        {
            var evt = new JsonObject();
            evt.Set("seq", _seq++);
            evt.Set("type", "event");
            evt.Set("event", eventName);

            if (body != null)
                evt.Set("body", body);

            WriteMessage(evt.ToJson());
        }

        /// <summary>
        /// Format a Number value for DAP display (integer if whole, otherwise decimal).
        /// </summary>
        protected static string FormatNumber(Number n)
        {
            int intVal = n.ToInt();
            if (Number.FromInt(intVal) == n)
                return intVal.ToString();
            return n.ToString();
        }
    }
}
