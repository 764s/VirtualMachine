using System;
using System.Collections.Generic;

namespace FFVM
{
    /// <summary>
    /// DBG5: A single variable's debug info snapshot.
    /// </summary>
    public struct VariableInfo
    {
        public string Name;
        public Number Value;
        public bool IsStruct;
        public string[] FieldNames;
        public Number[] FieldValues;
    }

    /// <summary>
    /// DBG6: A single call stack frame's debug info snapshot.
    /// </summary>
    public struct CallStackEntry
    {
        public string FunctionName;
        public int SourceLine;
        public int IP;
        /// <summary>DAP-F Phase 3: source file id for this frame (index into VMProgram.SourceFiles).
        /// 0 = main / unknown when debug info is absent.</summary>
        public int SourceFileId;
    }

    /// <summary>
    /// Script-level debugger: breakpoint management + variable/callstack inspection.
    /// Attach to VMWorld.Debugger to enable. Null = no debugging (zero overhead).
    /// 
    /// Phase 2: command-line debugging (Gate 0). No external dependencies.
    /// Phase 3A: DAP adapter wraps this class for IDE integration.
    /// Phase 3B: single-step mapping (next/stepIn/stepOut) via temporary breakpoints.
    /// </summary>
    public class ScriptDebugger
    {
        /// <summary>Set of source line numbers where breakpoints are active (legacy single-module).</summary>
        private readonly HashSet<int> _breakpointLines = new HashSet<int>();

        /// <summary>Per-module breakpoint lines (moduleSlot → set of line numbers).</summary>
        private readonly Dictionary<int, HashSet<int>> _moduleBreakpoints = new Dictionary<int, HashSet<int>>();

        /// <summary>
        /// DAP-F Phase 2: per-module + per-file breakpoint lines (slot → fileId → lines).
        /// Distinct from <see cref="_moduleBreakpoints"/> so file-aware DAP requests do not
        /// disturb legacy line-only callers (e.g. DebugTests' direct <c>AddBreakpoint(line)</c>).
        /// </summary>
        private readonly Dictionary<int, Dictionary<int, HashSet<int>>> _moduleFileBreakpoints =
            new Dictionary<int, Dictionary<int, HashSet<int>>>();

        /// <summary>
        /// Temporary breakpoint IP for single-step operations.
        /// -1 = no temporary breakpoint. Automatically cleared after one hit.
        /// Used by Step Over (next), Step Into (stepIn), Step Out (stepOut).
        /// </summary>
        private int _tempBreakpointIP = -1;

        /// <summary>
        /// Temporary breakpoint by source line — for step-over in loops.
        /// When set, stops at the first instruction on ANY different source line
        /// at the same or shallower call depth (does not enter functions).
        /// -1 = inactive. Automatically cleared after one hit.
        /// </summary>
        private int _stepOverFromLine = -1;

        /// <summary>Call stack depth when step-over started. Only stop when depth &lt;= this.</summary>
        private int _stepOverFromDepth = -1;

        /// <summary>
        /// Callback when a breakpoint is hit.
        /// Parameters: (instanceId, ip, sourceLine).
        /// In tests: lambda to collect events.
        /// In production: Debugger.Break() or DAP notification.
        /// </summary>
        public Action<int, int, int> OnBreakpointHit;

        /// <summary>Tracks last hit line per-tick to avoid duplicate triggers on same line.</summary>
        private int _lastHitLine = -1;

        /// <summary>
        /// When true, the VM yields after hitting a breakpoint (for DAP mode).
        /// When false (default), breakpoints fire callbacks but don't halt execution (Phase 2 mode).
        /// </summary>
        public bool HaltOnBreakpoint { get; set; }

        /// <summary>
        /// When true, skip the next breakpoint check once (used to resume past a hit breakpoint).
        /// Automatically resets to false after one check.
        /// </summary>
        public bool SkipNextCheck { get; set; }

        // --- Breakpoint management ---

        public void AddBreakpoint(int line) => _breakpointLines.Add(line);
        public void RemoveBreakpoint(int line) => _breakpointLines.Remove(line);
        public void ClearBreakpoints() { _breakpointLines.Clear(); _moduleBreakpoints.Clear(); _moduleFileBreakpoints.Clear(); }
        public bool HasBreakpoints => _breakpointLines.Count > 0 || _moduleBreakpoints.Count > 0 || _moduleFileBreakpoints.Count > 0 || _tempBreakpointIP >= 0 || _stepOverFromLine >= 0;

        // --- Per-module breakpoint management ---

        public void AddBreakpoint(int moduleSlot, int line)
        {
            if (!_moduleBreakpoints.TryGetValue(moduleSlot, out var set))
            {
                set = new HashSet<int>();
                _moduleBreakpoints[moduleSlot] = set;
            }
            set.Add(line);
        }

        public void ClearBreakpoints(int moduleSlot)
        {
            _moduleBreakpoints.Remove(moduleSlot);
            _moduleFileBreakpoints.Remove(moduleSlot);
        }

        // --- DAP-F Phase 2: per-module + per-file breakpoint management ---

        /// <summary>
        /// DAP-F Phase 2: register a file-aware breakpoint. Hits only when the IP's
        /// source file id matches <paramref name="fileId"/> (resolved via the program's
        /// <c>SourceFileMap</c>). Stored separately from legacy <see cref="AddBreakpoint(int, int)"/>
        /// so single-file callers are unaffected.
        /// </summary>
        public void AddBreakpoint(int moduleSlot, int fileId, int line)
        {
            if (!_moduleFileBreakpoints.TryGetValue(moduleSlot, out var byFile))
            {
                byFile = new Dictionary<int, HashSet<int>>();
                _moduleFileBreakpoints[moduleSlot] = byFile;
            }
            if (!byFile.TryGetValue(fileId, out var set))
            {
                set = new HashSet<int>();
                byFile[fileId] = set;
            }
            set.Add(line);
        }

        /// <summary>
        /// DAP-F Phase 2: clear breakpoints for a specific (slot, fileId) pair without
        /// disturbing other files within the same module. Used when a setBreakpoints request
        /// re-declares breakpoints for a single source file.
        /// </summary>
        public void ClearBreakpoints(int moduleSlot, int fileId)
        {
            if (_moduleFileBreakpoints.TryGetValue(moduleSlot, out var byFile))
            {
                byFile.Remove(fileId);
                if (byFile.Count == 0)
                    _moduleFileBreakpoints.Remove(moduleSlot);
            }
        }

        /// <summary>Set a temporary breakpoint at a specific IP (single-shot, auto-cleared on hit).</summary>
        public void SetTempBreakpoint(int ip) => _tempBreakpointIP = ip;

        /// <summary>Set step-over mode: stop at the first instruction on a different line
        /// at the same or shallower call depth. Handles all control flow including loops.</summary>
        public void SetStepOverFromLine(int line, int callDepth)
        {
            _stepOverFromLine = line;
            _stepOverFromDepth = callDepth;
        }

        /// <summary>Clear the temporary breakpoint without triggering.</summary>
        public void ClearTempBreakpoint()
        {
            _tempBreakpointIP = -1;
            _stepOverFromLine = -1;
            _stepOverFromDepth = -1;
        }

        /// <summary>
        /// Called at the start of each Tick to reset per-tick state.
        /// Allows the same line to trigger again on the next Tick.
        /// </summary>
        public void ResetTickState()
        {
            _lastHitLine = -1;
        }

        /// <summary>
        /// Check if the current IP hits a breakpoint. Called from VMWorld.ExecuteInstance.
        /// Returns true if a breakpoint was triggered (caller may want to yield).
        /// Checks temporary breakpoint (IP-exact) first, then line breakpoints.
        /// </summary>
        public bool CheckBreakpoint(int instanceId, int ip, int[] sourceMap, int callDepth = 0, int moduleSlot = -1)
        {
            return CheckBreakpoint(instanceId, ip, sourceMap, null, callDepth, moduleSlot);
        }

        /// <summary>
        /// DAP-F Phase 2: file-aware breakpoint check. When <paramref name="sourceFileMap"/> is non-null
        /// and per-file breakpoints exist for the current module slot, the file id at the current IP
        /// is required to match. Falls back to legacy line-only matching when no file-aware breakpoints
        /// are registered for the slot, preserving prior behaviour for single-file callers.
        /// </summary>
        public bool CheckBreakpoint(int instanceId, int ip, int[] sourceMap, int[] sourceFileMap, int callDepth, int moduleSlot)
        {
            if (SkipNextCheck)
            {
                SkipNextCheck = false;
                return false;
            }

            // --- Temporary breakpoint check (IP-exact, single-shot) ---
            if (_tempBreakpointIP >= 0 && ip == _tempBreakpointIP)
            {
                _tempBreakpointIP = -1; // Auto-clear after hit
                _stepOverFromLine = -1; // Clear companion step-over too
                int tempLine = (sourceMap != null && ip >= 0 && ip < sourceMap.Length) ? sourceMap[ip] : 0;
                _lastHitLine = tempLine;
                OnBreakpointHit?.Invoke(instanceId, ip, tempLine);
                return true;
            }

            // --- Step-over check: stop on any different line at same/shallower call depth ---
            if (_stepOverFromLine >= 0 && sourceMap != null && ip >= 0 && ip < sourceMap.Length)
            {
                int srcLine = sourceMap[ip];
                if (srcLine > 0 && srcLine != _stepOverFromLine && callDepth <= _stepOverFromDepth)
                {
                    _tempBreakpointIP = -1;
                    _stepOverFromLine = -1;
                    _stepOverFromDepth = -1;
                    _lastHitLine = srcLine;
                    OnBreakpointHit?.Invoke(instanceId, ip, srcLine);
                    return true;
                }
            }

            // --- When a temp breakpoint is active (step in progress), skip line breakpoints ---
            // Otherwise the user's line breakpoint on the current line re-fires before
            // the VM reaches the step target (e.g., re-hits line 47 before entering add()).
            if (_tempBreakpointIP >= 0 || _stepOverFromLine >= 0)
                return false;

            // --- Line breakpoint check ---
            if (sourceMap == null)
                return false;

            if (ip < 0 || ip >= sourceMap.Length)
                return false;

            int line = sourceMap[ip];
            if (line <= 0)
                return false;

            if (line == _lastHitLine)
                return false; // Already triggered for this line in this tick

            // DAP-F Phase 2: file-aware match takes priority over legacy line-only match
            // for the same module slot. This keeps multi-file requests from interfering with
            // each other while still letting legacy callers (DebugTests etc.) work unchanged.
            bool isBreakpoint = false;
            if (moduleSlot >= 0 && _moduleFileBreakpoints.TryGetValue(moduleSlot, out var byFile))
            {
                int fileId = (sourceFileMap != null && ip >= 0 && ip < sourceFileMap.Length)
                    ? sourceFileMap[ip] : 0;
                if (fileId >= 0 && byFile.TryGetValue(fileId, out var fileSet))
                    isBreakpoint = fileSet.Contains(line);
            }

            // Check module-specific breakpoints first, then legacy flat set
            if (!isBreakpoint && moduleSlot >= 0 && _moduleBreakpoints.TryGetValue(moduleSlot, out var moduleSet))
                isBreakpoint = moduleSet.Contains(line);
            if (!isBreakpoint)
                isBreakpoint = _breakpointLines.Contains(line);
            if (!isBreakpoint)
                return false;

            _lastHitLine = line;
            OnBreakpointHit?.Invoke(instanceId, ip, line);
            return true;
        }

        // --- DBG4: Single-Step Mapping ---

        /// <summary>
        /// Step Over: find the IP of the next source line after currentIP.
        /// Scans SourceMap from currentIP+1 for the first IP with a different (non-zero) line number.
        /// Returns -1 if no next line found (end of function/program).
        /// </summary>
        public static int FindNextLineIP(VMProgram program, int currentIP)
        {
            if (program.SourceMap == null || currentIP < 0 || currentIP >= program.SourceMap.Length)
                return -1;

            int currentLine = program.SourceMap[currentIP];

            for (int ip = currentIP + 1; ip < program.SourceMap.Length; ip++)
            {
                int line = program.SourceMap[ip];
                if (line > 0 && line != currentLine)
                    return ip;
            }

            return -1; // No next line found
        }

        /// <summary>
        /// Step Into: scan instructions on the current line for a CALL instruction.
        /// If found, return the CALL target IP. Otherwise, fall back to Step Over.
        /// SYSCALL is not steppable (host code, not script).
        /// </summary>
        public static int FindStepIntoIP(VMProgram program, int currentIP)
        {
            if (program.Instructions == null || program.SourceMap == null ||
                currentIP < 0 || currentIP >= program.Instructions.Length ||
                currentIP >= program.SourceMap.Length)
                return FindNextLineIP(program, currentIP);

            int currentLine = program.SourceMap[currentIP];

            // Scan forward within the same source line to find a CALL instruction
            for (int ip = currentIP; ip < program.Instructions.Length && ip < program.SourceMap.Length; ip++)
            {
                int line = program.SourceMap[ip];
                if (line > 0 && line != currentLine)
                    break; // Past current line, no CALL found

                if (program.Instructions[ip].Code == OpCode.CALL ||
                    program.Instructions[ip].Code == OpCode.CALL_LEAF)
                {
                    // O8: check for EXTEND_AX prefix to reconstruct wide IP
                    int targetIP = program.Instructions[ip].A;
                    if (ip > 0 && program.Instructions[ip - 1].Code == OpCode.EXTEND_AX)
                        targetIP |= program.Instructions[ip - 1].A << 8;
                    return targetIP;
                }
            }

            // No CALL on this line — degrade to Step Over
            return FindNextLineIP(program, currentIP);
        }

        /// <summary>
        /// Step Out: return the ReturnIP from the top CallStack frame.
        /// Returns -1 if at the top-level function (no call stack to return from).
        /// </summary>
        public static int FindStepOutIP(ref VMInstanceState inst)
        {
            if (inst.CallStackDepth <= 0)
                return -1; // Already at top level

            var frame = inst.CallStack.Get(inst.CallStackDepth - 1);
            return frame.ReturnIP;
        }

        // --- DBG5: Variable Display Adapter ---

        /// <summary>
        /// Get all variables visible at the current execution point.
        /// Filters by the current function scope using SymbolTable.ScopeFunctionName.
        /// </summary>
        public List<VariableInfo> GetVariables(VMProgram program, ref VMInstanceState inst, Number[] extendedRegs = null)
        {
            var result = new List<VariableInfo>();
            if (program.SymbolTable == null)
                return result;

            string currentFunc = FindFunctionByIP(program, inst.IP);

            for (int i = 0; i < program.SymbolTable.Length; i++)
            {
                ref readonly SymbolEntry sym = ref program.SymbolTable[i];

                // Filter to current function scope
                if (sym.ScopeFunctionName != currentFunc)
                    continue;

                var info = new VariableInfo
                {
                    Name = sym.Name,
                    IsStruct = sym.FieldCount > 0
                };

                // Lang-1.1b: Extended registers are stored in heap array
                if (sym.Register >= VMConstants.MaxRegisters)
                {
                    int xidx = sym.Register - VMConstants.MaxRegisters;
                    if (extendedRegs != null && xidx < extendedRegs.Length)
                    {
                        if (sym.FieldCount > 0 && sym.FieldNames != null)
                        {
                            info.Value = extendedRegs[xidx];
                            info.FieldNames = sym.FieldNames;
                            info.FieldValues = new Number[sym.FieldCount];
                            for (int f = 0; f < sym.FieldCount; f++)
                                info.FieldValues[f] = extendedRegs[xidx + f];
                        }
                        else
                        {
                            info.Value = extendedRegs[xidx];
                        }
                    }
                    result.Add(info);
                    continue;
                }

                // Scratch zone registers are absolute; windowed registers offset by RegisterBase
                int physReg = sym.Register < VMConstants.ScratchZoneSize ? sym.Register : sym.Register + inst.RegisterBase;

                if (sym.FieldCount > 0 && sym.FieldNames != null)
                {
                    // Struct variable: read each field from consecutive registers
                    info.Value = inst.Registers.Get(physReg);
                    info.FieldNames = sym.FieldNames;
                    info.FieldValues = new Number[sym.FieldCount];
                    for (int f = 0; f < sym.FieldCount; f++)
                    {
                        info.FieldValues[f] = inst.Registers.Get(physReg + f);
                    }
                }
                else
                {
                    // Scalar variable
                    info.Value = inst.Registers.Get(physReg);
                }

                result.Add(info);
            }

            return result;
        }

        // --- DBG6: Call Stack Inspection ---

        /// <summary>
        /// Get the current call stack. Top of stack (current function) is at index 0.
        /// </summary>
        public List<CallStackEntry> GetCallStack(VMProgram program, ref VMInstanceState inst)
        {
            var result = new List<CallStackEntry>();

            // Current frame (top of stack)
            result.Add(new CallStackEntry
            {
                FunctionName = FindFunctionByIP(program, inst.IP),
                SourceLine = GetSourceLine(program, inst.IP),
                IP = inst.IP,
                SourceFileId = GetSourceFileId(program, inst.IP)
            });

            // Walk call stack frames from top to bottom
            for (int i = inst.CallStackDepth - 1; i >= 0; i--)
            {
                var frame = inst.CallStack.Get(i);
                // ReturnIP points to the instruction AFTER the CALL.
                // To show the call site, use ReturnIP - 1 (the CALL instruction itself).
                int callSiteIP = frame.ReturnIP > 0 ? frame.ReturnIP - 1 : 0;
                result.Add(new CallStackEntry
                {
                    FunctionName = FindFunctionByIP(program, callSiteIP),
                    SourceLine = GetSourceLine(program, callSiteIP),
                    IP = callSiteIP,
                    SourceFileId = GetSourceFileId(program, callSiteIP)
                });
            }

            return result;
        }

        /// <summary>
        /// DAP-F Phase 3: read the source file id at a given IP from <c>SourceFileMap</c>.
        /// Returns 0 (main / unknown) when debug info is absent or the IP is out of range.
        /// </summary>
        private static int GetSourceFileId(VMProgram program, int ip)
        {
            if (program == null || program.SourceFileMap == null) return 0;
            if (ip < 0 || ip >= program.SourceFileMap.Length) return 0;
            int id = program.SourceFileMap[ip];
            return id < 0 ? 0 : id;
        }

        // --- Helpers ---

        /// <summary>
        /// Find which function contains the given IP.
        /// Functions are sorted by EntryIP (compilation order).
        /// Returns the function whose EntryIP ≤ ip and is the closest.
        /// </summary>
        public static string FindFunctionByIP(VMProgram program, int ip)
        {
            if (program.Functions == null || program.Functions.Length == 0)
                return "<unknown>";

            string bestName = program.Functions[0].Name;
            for (int i = 0; i < program.Functions.Length; i++)
            {
                if (program.Functions[i].EntryIP <= ip)
                    bestName = program.Functions[i].Name;
                else
                    break; // Past our IP, stop
            }

            return bestName;
        }

        /// <summary>Get source line for an IP, or 0 if no source map.</summary>
        private static int GetSourceLine(VMProgram program, int ip)
        {
            if (program.SourceMap == null || ip < 0 || ip >= program.SourceMap.Length)
                return 0;
            return program.SourceMap[ip];
        }
    }
}
