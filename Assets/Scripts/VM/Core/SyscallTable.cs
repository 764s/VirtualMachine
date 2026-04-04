using System;
using System.Collections.Generic;

namespace FFVM
{
    /// <summary>
    /// Delegate type for Syscall functions.
    /// Syscalls read/write registers directly on the instance state.
    /// Convention: arguments in absolute registers r0, r1, r2, ... (NOT relative to RegisterBase).
    /// The compiler moves arguments to r0..rN before the SYSCALL instruction.
    /// Return value is written to r0.
    /// </summary>
    public delegate void SyscallHandler(ref VMInstanceState instance);

    /// <summary>
    /// E002: Convenience wrapper for type-safe syscall parameter access.
    /// Abstracts the absolute register convention so handlers don't need to know register details.
    /// Usage: var args = new SyscallArgs(ref s); int val = args.GetInt(0);
    /// </summary>
    public ref struct SyscallArgs
    {
        private ref VMInstanceState _state;

        public SyscallArgs(ref VMInstanceState state) { _state = ref state; }

        /// <summary>Get the Nth argument as an integer (reads absolute register N).</summary>
        public int GetInt(int index) => _state.Registers.Get(index).ToInt();

        /// <summary>Get the Nth argument as a float (reads absolute register N).</summary>
        public float GetFloat(int index) => _state.Registers.Get(index).ToFloat();

        /// <summary>Get the Nth argument as a raw Number value (reads absolute register N).</summary>
        public Number GetNumber(int index) => _state.Registers.Get(index);

        /// <summary>Set the return value (writes absolute register 0).</summary>
        public void SetReturn(Number value) => _state.Registers.Set(0, value);

        /// <summary>Set the return value as an integer (writes absolute register 0).</summary>
        public void SetReturnInt(int value) => _state.Registers.Set(0, Number.FromInt(value));

        /// <summary>Set the return value as a float (writes absolute register 0).</summary>
        public void SetReturnFloat(float value) => _state.Registers.Set(0, Number.FromFloat(value));
    }

    /// <summary>
    /// Describes a single syscall parameter (name + type).
    /// </summary>
    public sealed class SyscallParamInfo
    {
        public readonly string Name;
        public readonly string TypeName;

        public SyscallParamInfo(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }
    }

    /// <summary>
    /// Full signature metadata for a syscall (parameters, return type, description).
    /// Used by LSP6 Syscall Declaration Protocol to provide editor completion / signature help.
    /// </summary>
    public sealed class SyscallSignature
    {
        public readonly SyscallParamInfo[] Parameters;
        public readonly string ReturnType;
        public readonly string Description;

        public SyscallSignature(SyscallParamInfo[] parameters, string returnType, string description)
        {
            Parameters = parameters ?? Array.Empty<SyscallParamInfo>();
            ReturnType = returnType;
            Description = description;
        }

        /// <summary>
        /// Format as human-readable signature string: "(paramName: type, ...) : returnType"
        /// </summary>
        public string Format(string name)
        {
            var parts = new List<string>();
            foreach (var p in Parameters)
                parts.Add($"{p.Name}: {p.TypeName}");
            string ret = !string.IsNullOrEmpty(ReturnType) && ReturnType != "void"
                ? $": {ReturnType}" : "";
            return $"(syscall) {name}({string.Join(", ", parts)}){ret}";
        }
    }

    /// <summary>
    /// Fixed-size syscall dispatch table.
    /// Slot replacement at frame boundary for hot-swap.
    /// Zero overhead when no override: caller checks delegate != null.
    /// </summary>
    public class SyscallTable
    {
        private readonly SyscallHandler[] _handlers;
        private readonly string[] _names; // Debug only
        private readonly int[] _pairedSlots; // Paired (release) slot for each syscall, -1 = no pair
        private readonly bool[] _requiresCleanup; // True if syscall must be wrapped in using/defer
        private readonly SyscallSignature[] _signatures; // LSP6: parameter/return/description metadata

        public SyscallTable()
        {
            _handlers = new SyscallHandler[VMConstants.MaxSyscalls];
            _names = new string[VMConstants.MaxSyscalls];
            _pairedSlots = new int[VMConstants.MaxSyscalls];
            _requiresCleanup = new bool[VMConstants.MaxSyscalls];
            _signatures = new SyscallSignature[VMConstants.MaxSyscalls];
            for (int i = 0; i < VMConstants.MaxSyscalls; i++)
                _pairedSlots[i] = -1;
        }

        /// <summary>
        /// Register a syscall handler at a specific slot.
        /// E002: Throws if slot is already occupied by a different syscall (collision detection).
        /// Re-registering the same name is allowed (acts as Replace for hot-swap).
        /// </summary>
        public void Register(int slot, string name, SyscallHandler handler)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                throw new ArgumentOutOfRangeException(nameof(slot));
            if (_handlers[slot] != null && _names[slot] != name)
                throw new InvalidOperationException(
                    $"Syscall slot {slot} collision: '{name}' cannot overwrite existing '{_names[slot]}'. " +
                    $"Choose a different slot.");
            _handlers[slot] = handler;
            _names[slot] = name;
        }

        /// <summary>
        /// Replace a syscall handler (hot-swap). Call at frame boundary only.
        /// </summary>
        public void Replace(int slot, SyscallHandler handler)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                throw new ArgumentOutOfRangeException(nameof(slot));
            _handlers[slot] = handler;
        }

        /// <summary>
        /// Invoke syscall by slot index.
        /// </summary>
        public void Invoke(int slot, ref VMInstanceState instance)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls || _handlers[slot] == null)
            {
                instance.ErrorFlag = VMError.PanicIllegalInstruction;
                return;
            }
            _handlers[slot](ref instance);
        }

        public string GetName(int slot)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                return null;
            return _names[slot];
        }

        /// <summary>
        /// Register a paired acquire/release syscall pair.
        /// When 'using AcquireName(args) { ... }' is compiled, the compiler auto-emits
        /// a cleanup block calling the release syscall.
        /// </summary>
        public void RegisterPaired(int acquireSlot, string acquireName, SyscallHandler acquireHandler,
                                   int releaseSlot, string releaseName, SyscallHandler releaseHandler)
        {
            Register(acquireSlot, acquireName, acquireHandler);
            Register(releaseSlot, releaseName, releaseHandler);
            _pairedSlots[acquireSlot] = releaseSlot;
            _requiresCleanup[acquireSlot] = true; // Acquire end always requires cleanup
        }

        /// <summary>
        /// Get the paired release slot for a given acquire slot. Returns -1 if no pair.
        /// </summary>
        public int GetPairedSlot(int slot)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                return -1;
            return _pairedSlots[slot];
        }

        /// <summary>
        /// Check if a syscall has a paired release slot.
        /// </summary>
        public bool HasPair(int slot)
        {
            return GetPairedSlot(slot) >= 0;
        }

        /// <summary>
        /// Mark a syscall as requiring cleanup (must be used inside 'using' block).
        /// Called automatically for acquire slots in RegisterPaired().
        /// Can also be called manually for non-paired syscalls that need cleanup.
        /// </summary>
        public void MarkRequiresCleanup(int slot)
        {
            if (slot >= 0 && slot < VMConstants.MaxSyscalls)
                _requiresCleanup[slot] = true;
        }

        /// <summary>
        /// Check if a syscall requires cleanup wrapping (using/defer).
        /// </summary>
        public bool RequiresCleanup(int slot)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                return false;
            return _requiresCleanup[slot];
        }

        /// <summary>
        /// Register signature metadata for a syscall slot (LSP6).
        /// </summary>
        public void RegisterSignature(int slot, SyscallSignature signature)
        {
            if (slot >= 0 && slot < VMConstants.MaxSyscalls)
                _signatures[slot] = signature;
        }

        /// <summary>
        /// Get signature metadata for a syscall slot. Returns null if not registered.
        /// </summary>
        public SyscallSignature GetSignature(int slot)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                return null;
            return _signatures[slot];
        }

        /// <summary>
        /// E002: Parse a .ffvm.d.json declaration file and return the name→slot mapping
        /// plus registered no-op placeholder handlers and signatures on this table.
        /// Auto-assigns slot numbers starting from 0 in JSON key order.
        /// </summary>
        /// <param name="json">The raw JSON content of the declaration file.</param>
        /// <returns>Dictionary mapping syscall names to auto-assigned slot numbers.</returns>
        public Dictionary<string, int> LoadDeclarationJson(string json)
        {
            var map = new Dictionary<string, int>();
            var root = FFVM.Debug.JsonObject.Parse(json);
            if (root == null) return map;

            var syscallsObj = root.GetObject("syscalls");
            if (syscallsObj == null) return map;

            int slot = 0;
            foreach (var name in syscallsObj.Keys)
            {
                if (slot >= VMConstants.MaxSyscalls)
                    break;

                map[name] = slot;

                // Register no-op placeholder handler (does nothing at runtime)
                if (_handlers[slot] == null)
                {
                    string capturedName = name;
                    _handlers[slot] = (ref VMInstanceState s) => { /* no-op placeholder for '{capturedName}' */ };
                    _names[slot] = name;
                }

                // Parse and register signature metadata
                var entry = syscallsObj.GetObject(name);
                if (entry != null)
                {
                    var paramsList = entry.GetArray("params");
                    var paramInfos = new List<SyscallParamInfo>();
                    if (paramsList != null)
                    {
                        foreach (var p in paramsList)
                        {
                            if (p is FFVM.Debug.JsonObject pObj)
                            {
                                string pName = pObj.GetString("name") ?? "";
                                string pType = pObj.GetString("type") ?? "number";
                                paramInfos.Add(new SyscallParamInfo(pName, pType));
                            }
                        }
                    }
                    string returnType = entry.GetString("returnType") ?? "void";
                    string description = entry.GetString("description") ?? "";
                    RegisterSignature(slot, new SyscallSignature(paramInfos.ToArray(), returnType, description));
                }

                slot++;
            }

            return map;
        }
    }
}
