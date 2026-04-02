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
    }

    /// <summary>
    /// Script-level debugger: breakpoint management + variable/callstack inspection.
    /// Attach to VMWorld.Debugger to enable. Null = no debugging (zero overhead).
    /// 
    /// Phase 2: command-line debugging (Gate 0). No external dependencies.
    /// Phase 3: DAP adapter will wrap this class for IDE integration.
    /// </summary>
    public class ScriptDebugger
    {
        /// <summary>Set of source line numbers where breakpoints are active.</summary>
        private readonly HashSet<int> _breakpointLines = new HashSet<int>();

        /// <summary>
        /// Callback when a breakpoint is hit.
        /// Parameters: (instanceId, ip, sourceLine).
        /// In tests: lambda to collect events.
        /// In production: Debugger.Break() or DAP notification.
        /// </summary>
        public Action<int, int, int> OnBreakpointHit;

        /// <summary>Tracks last hit line per-tick to avoid duplicate triggers on same line.</summary>
        private int _lastHitLine = -1;

        // --- Breakpoint management ---

        public void AddBreakpoint(int line) => _breakpointLines.Add(line);
        public void RemoveBreakpoint(int line) => _breakpointLines.Remove(line);
        public void ClearBreakpoints() => _breakpointLines.Clear();
        public bool HasBreakpoints => _breakpointLines.Count > 0;

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
        /// </summary>
        public bool CheckBreakpoint(int instanceId, int ip, int[] sourceMap)
        {
            if (sourceMap == null || _breakpointLines.Count == 0)
                return false;

            if (ip < 0 || ip >= sourceMap.Length)
                return false;

            int line = sourceMap[ip];
            if (line <= 0)
                return false;

            if (line == _lastHitLine)
                return false; // Already triggered for this line in this tick

            if (!_breakpointLines.Contains(line))
                return false;

            _lastHitLine = line;
            OnBreakpointHit?.Invoke(instanceId, ip, line);
            return true;
        }

        // --- DBG5: Variable Display Adapter ---

        /// <summary>
        /// Get all variables visible at the current execution point.
        /// Filters by the current function scope using SymbolTable.ScopeFunctionName.
        /// </summary>
        public List<VariableInfo> GetVariables(VMProgram program, ref VMInstanceState inst)
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

                int physReg = sym.Register < 16 ? sym.Register : sym.Register + inst.RegisterBase;

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
                IP = inst.IP
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
                    IP = callSiteIP
                });
            }

            return result;
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
