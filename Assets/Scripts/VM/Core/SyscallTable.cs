using System;

namespace FFVM
{
    /// <summary>
    /// Delegate type for Syscall functions.
    /// Syscalls read/write registers directly on the instance state.
    /// Convention: arguments in registers starting at RegisterBase+0,
    /// return value in RegisterBase+0.
    /// </summary>
    public delegate void SyscallHandler(ref VMInstanceState instance);

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

        public SyscallTable()
        {
            _handlers = new SyscallHandler[VMConstants.MaxSyscalls];
            _names = new string[VMConstants.MaxSyscalls];
            _pairedSlots = new int[VMConstants.MaxSyscalls];
            for (int i = 0; i < VMConstants.MaxSyscalls; i++)
                _pairedSlots[i] = -1;
        }

        /// <summary>
        /// Register a syscall handler at a specific slot.
        /// </summary>
        public void Register(int slot, string name, SyscallHandler handler)
        {
            if (slot < 0 || slot >= VMConstants.MaxSyscalls)
                throw new ArgumentOutOfRangeException(nameof(slot));
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
    }
}
