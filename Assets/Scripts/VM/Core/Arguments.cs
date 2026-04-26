using System;

namespace FFVM
{
    /// <summary>
    /// VOM2: Read-only argument vector for <see cref="VMEngine"/> calls.
    /// Wraps a caller-owned <see cref="Span{T}"/> of <see cref="Number"/> slots
    /// (typically <c>stackalloc Number[N]</c>) — zero allocation per call.
    /// </summary>
    public readonly ref struct Arguments
    {
        private readonly Span<Number> _data;

        /// <summary>Number of valid argument slots in this vector.</summary>
        public int Count => _data.Length;

        public Arguments(Span<Number> data)
        {
            _data = data;
        }

        public Arguments(Number[] data)
        {
            _data = data == null ? Span<Number>.Empty : data.AsSpan();
        }

        /// <summary>Read i-th argument. Bounds-checked by Span itself.</summary>
        public Number this[int i] => _data[i];

        public static Arguments Empty => new Arguments(Span<Number>.Empty);
    }

    /// <summary>
    /// VOM2: Write-only return slot for <see cref="VMEngine"/> calls.
    /// Wraps a caller-owned <see cref="Span{T}"/> of <see cref="Number"/> slots — zero allocation.
    /// </summary>
    public readonly ref struct ReturnSlot
    {
        private readonly Span<Number> _data;

        /// <summary>Maximum number of return slots the callee may write.</summary>
        public int Capacity => _data.Length;

        public ReturnSlot(Span<Number> data)
        {
            _data = data;
        }

        public ReturnSlot(Number[] data)
        {
            _data = data == null ? Span<Number>.Empty : data.AsSpan();
        }

        /// <summary>Write the i-th return value. Bounds-checked by Span itself.</summary>
        public void Set(int i, Number value) => _data[i] = value;

        /// <summary>Read back what was written at index i (post-call inspection).</summary>
        public Number Get(int i) => _data[i];
    }

    /// <summary>
    /// VOM2: Thrown when a <see cref="VMEngine"/> call violates the ABI contract
    /// (arg count mismatch, unresolved/stale handle, return slot too small, etc.).
    /// </summary>
    public class VMABIException : Exception
    {
        public VMABIException(string message) : base(message) { }
    }

    /// <summary>
    /// VOM3 Phase2: thrown when a VMEngine.ReadOnlyCall attempts a write,
    /// yield, or non-readonly syscall/XCALL. Carries the offending opcode +
    /// IP + function name to make diagnosis cheap.
    /// </summary>
    public sealed class ReadOnlyViolationException : VMABIException
    {
        public string OpcodeName { get; }
        public int IP { get; }
        public string FunctionName { get; }

        public ReadOnlyViolationException(string opcodeName, int ip, string functionName)
            : base("ReadOnly violation: opcode=" + opcodeName + " ip=" + ip + " fn=" + (functionName ?? "<unknown>"))
        {
            OpcodeName = opcodeName;
            IP = ip;
            FunctionName = functionName;
        }
    }
}
