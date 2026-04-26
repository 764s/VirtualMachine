namespace FFVM
{
    /// <summary>
    /// VOM5: per-instance host service binding. Replaces the global
    /// <c>VMWorld.Syscalls</c> assumption — each spawned instance carries
    /// its own <see cref="HostBindings"/> reference, which the SYSCALL
    /// dispatch path resolves at runtime.
    ///
    /// This step ships with a single field (<see cref="Syscalls"/>); future
    /// host hooks (blackboard, log, time provider, RNG) can be added
    /// incrementally as needs arise.
    ///
    /// Compatibility: <c>VMWorld.DefaultBindings</c> wraps the original
    /// world-scoped <see cref="SyscallTable"/>; <c>VMWorld.Syscalls</c> is
    /// a get-only passthrough so existing register / replace call sites
    /// keep working unchanged.
    /// </summary>
    public sealed class HostBindings
    {
        public SyscallTable Syscalls { get; }

        public HostBindings(SyscallTable syscalls)
        {
            Syscalls = syscalls ?? new SyscallTable();
        }

        public static HostBindings CreateDefault() => new HostBindings(new SyscallTable());
    }
}
