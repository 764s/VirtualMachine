namespace FFVM
{
    /// <summary>
    /// Lang-7: Runtime configuration for VMWorld.
    /// Controls XCALL nesting depth limits and depth policy.
    /// </summary>
    public enum XCallDepthPolicy : byte
    {
        /// <summary>Log a warning when depth exceeds MaxXCallDepth, but allow execution to continue.</summary>
        Warn = 0,
        /// <summary>No depth limit — no warnings, no enforcement.</summary>
        Unlimited = 1,
    }

    /// <summary>
    /// Lang-7: VM runtime configuration.
    /// Pass to VMWorld constructor or set properties before Tick().
    /// </summary>
    public class VMConfig
    {
        /// <summary>
        /// Maximum XCALL nesting depth before triggering the depth policy.
        /// Default: 4. Only used when XCallPolicy is Warn.
        /// </summary>
        public int MaxXCallDepth { get; set; } = 4;

        /// <summary>
        /// Policy when XCALL nesting depth exceeds MaxXCallDepth.
        /// Warn (default): invoke OnXCallDepthWarning callback but allow execution.
        /// Unlimited: no depth checking at all.
        /// </summary>
        public XCallDepthPolicy XCallPolicy { get; set; } = XCallDepthPolicy.Warn;
    }
}
