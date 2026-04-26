namespace FFVM
{
    /// <summary>
    /// VOM1: A resolved, cached handle to a VMProgram function entry.
    /// readonly struct — zero allocation, safe to pass by value.
    /// Validity is bound to the owning VMProgram's Version (hot-reload safe).
    ///
    /// Foundation type for the VOM2 host-side call ABI
    /// (VMEngine.StaticReadOnlyCall / Call / ReadOnlyCall / YieldCall).
    /// NOT yet wired into the existing XCALL opcode path — XCALL keeps using
    /// ExportTable index access (already O(1)). See Step_VOM1_StateSplit.md §零.
    /// </summary>
    public readonly struct MethodHandle
    {
        /// <summary>Index into VMProgram.Functions[]. -1 if unresolved.</summary>
        public readonly int FunctionIndex;

        /// <summary>VMProgram.Version snapshot at resolve time. Used for invalidation.</summary>
        public readonly int Version;

        /// <summary>Cached: bytecode entry IP for the function.</summary>
        public readonly int EntryIP;

        /// <summary>Cached: parameter count (mirrors FunctionEntry.ParamCount).</summary>
        public readonly int ParamCount;

        /// <summary>Cached: return value count. FFS currently always 1; reserved for FF4 multi-return.</summary>
        public readonly int ReturnCount;

        /// <summary>True if this handle holds a resolved function reference.</summary>
        public bool IsResolved => FunctionIndex >= 0;

        public MethodHandle(int functionIndex, int version, int entryIP, int paramCount, int returnCount)
        {
            FunctionIndex = functionIndex;
            Version = version;
            EntryIP = entryIP;
            ParamCount = paramCount;
            ReturnCount = returnCount;
        }

        /// <summary>The unresolved sentinel. IsResolved == false, IsValid(...) == false.</summary>
        public static readonly MethodHandle Invalid = new MethodHandle(-1, 0, 0, 0, 0);

        /// <summary>
        /// Check whether this handle is still valid against a program's current Version.
        /// Returns false after hot-reload (caller must re-resolve via VMProgram.ResolveMethod).
        /// </summary>
        public bool IsValid(VMProgram program)
        {
            return IsResolved
                && program != null
                && program.Version == Version
                && FunctionIndex < program.Functions.Length;
        }
    }
}
