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

        public SyscallTable()
        {
            _handlers = new SyscallHandler[VMConstants.MaxSyscalls];
            _names = new string[VMConstants.MaxSyscalls];
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
    }
}
