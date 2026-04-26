using System;

namespace FFVM
{
    /// <summary>
    /// VOM6: which call shape each row of a <see cref="BatchPlan"/> uses.
    /// </summary>
    public enum BatchKind
    {
        /// <summary>Unrestricted call. Module-state writes permitted.</summary>
        Call = 0,
        /// <summary>Read-only call. Function MUST be declared <c>@readonly</c>.</summary>
        ReadOnlyCall = 1,
    }

    /// <summary>
    /// VOM6: row-batched dispatch plan over a single <see cref="MethodHandle"/>.
    ///
    /// Layout: arguments and return values are flat row-major matrices. Row <c>r</c>
    /// occupies <c>Args[r*ParamCount .. r*ParamCount + ParamCount)</c> and
    /// <c>Returns[r*ReturnCount .. r*ReturnCount + ReturnCount)</c>.
    ///
    /// Error handling:
    /// - If <see cref="Errors"/>.Length == <see cref="Count"/>, per-row failures are
    ///   recorded into Errors[row] and the batch continues.
    /// - If <see cref="Errors"/> is empty (default), the first failure throws
    ///   immediately.
    ///
    /// Construct via the validating constructor; it checks the spans against the
    /// handle's arity at the boundary so the inner loop can stay branch-light.
    /// </summary>
    public readonly ref struct BatchPlan
    {
        public readonly MethodHandle Handle;
        public readonly int Count;
        public readonly ReadOnlySpan<Number> Args;
        public readonly Span<Number> Returns;
        public readonly Span<VMError> Errors;

        /// <summary>
        /// Build a BatchPlan. Throws <see cref="VMABIException"/> if span lengths
        /// don't match the handle's arity.
        /// </summary>
        public BatchPlan(
            MethodHandle handle,
            int count,
            ReadOnlySpan<Number> args,
            Span<Number> returns,
            Span<VMError> errors = default)
        {
            if (!handle.IsResolved)
                throw new VMABIException("BatchPlan: MethodHandle is unresolved");
            if (count < 0)
                throw new VMABIException($"BatchPlan: count {count} < 0");

            int expectedArgs = count * handle.ParamCount;
            int expectedRets = count * handle.ReturnCount;
            if (args.Length != expectedArgs)
                throw new VMABIException(
                    $"BatchPlan: Args.Length {args.Length} != Count*ParamCount {expectedArgs}");
            if (returns.Length != expectedRets)
                throw new VMABIException(
                    $"BatchPlan: Returns.Length {returns.Length} != Count*ReturnCount {expectedRets}");
            if (errors.Length != 0 && errors.Length != count)
                throw new VMABIException(
                    $"BatchPlan: Errors.Length {errors.Length} must be 0 or equal to Count {count}");

            Handle = handle;
            Count = count;
            Args = args;
            Returns = returns;
            Errors = errors;
        }

        /// <summary>Argument slice for row <paramref name="row"/>.</summary>
        public ReadOnlySpan<Number> ArgsAt(int row)
            => Args.Slice(row * Handle.ParamCount, Handle.ParamCount);

        /// <summary>Return slice for row <paramref name="row"/>.</summary>
        public Span<Number> ReturnsAt(int row)
            => Returns.Slice(row * Handle.ReturnCount, Handle.ReturnCount);

        /// <summary>True if the caller provided a per-row error sink.</summary>
        public bool HasErrorSink => Errors.Length == Count && Count > 0;
    }
}
