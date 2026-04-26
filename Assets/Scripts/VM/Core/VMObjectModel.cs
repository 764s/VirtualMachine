using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// [VOM7] Inline fixed-size module-variable register file.
    /// Length = <see cref="VMConstants.ModuleVarSlots"/> × Number (8 bytes each).
    /// Mirror of <see cref="NumberRegisters"/> shape so MVar can be relocated
    /// out of <c>NumberRegisters[ModuleVarRegBase..]</c> in VOM8.
    ///
    /// VOM7 status: type defined but NOT YET WIRED. VOM8 routes
    /// <c>LOAD_MVAR/STORE_MVAR</c> through <see cref="VMData.MVars"/> and
    /// VOM9 deletes the legacy <c>regs[ModuleVarRegBase+B]</c> path.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MVarRegisters
    {
        public fixed long Raw[VMConstants.ModuleVarSlots];

        public Number Get(int index)
        {
            fixed (long* ptr = Raw)
            {
                return ((Number*)ptr)[index];
            }
        }

        public void Set(int index, Number value)
        {
            fixed (long* ptr = Raw)
            {
                ((Number*)ptr)[index] = value;
            }
        }
    }

    /// <summary>
    /// [VOM7] CPU-private execution state of a single VM instance.
    /// Holds fields that are local to the active call's IP/register window:
    /// not persistent across yields except when paired with the owning
    /// <see cref="VMData"/> (yield handle pins both halves together).
    ///
    /// VOM7 status: type defined but NOT YET POOLED. <see cref="VMInstanceState"/>
    /// remains the on-disk and in-pool layout. VOM8 splits
    /// <see cref="InstancePool"/> into dual <c>Datas[]</c> + <c>Cpus[]</c>
    /// arrays and switches <see cref="VMWorld.ExecuteInstance"/> to the
    /// <c>(ref CPUData, ref VMData)</c> signature.
    ///
    /// Field order intentionally mirrors the corresponding subset of
    /// <see cref="VMInstanceState"/> to keep mental-model parity, but is NOT
    /// expected to share byte layout with the legacy struct (legacy snapshot
    /// path stays on <see cref="VMInstanceState"/> until VOM9).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CPUData
    {
        // Execution state
        public int IP;
        public int RegisterBase;

        // Call stack
        public int CallStackDepth;

        // FO1: Leaf function fast path
        public int LeafReturnIP;
        public int LeafRegisterBase;

        // Status flags (CPU-scoped subset; see VMData for instance-scoped flags)
        public VMError ErrorFlag;
        public VMStateFlags StateFlags;

        // Cleanup stack depth (frames live in CleanupStack below)
        public byte CleanupDepth;

        // ----- Fixed-size arrays (inline for blittable memcpy) -----

        public NumberRegisters Registers;
        public CallStackFrames CallStack;
        public CleanupFrames CleanupStack;
    }

    /// <summary>
    /// [VOM7] Persistent VM-instance identity and lifecycle data.
    /// Survives across yields and host calls. Module variables (MVars) live
    /// here in their own <see cref="MVarRegisters"/> fixed buffer rather than
    /// at the high end of <see cref="NumberRegisters"/>.
    ///
    /// VOM7 status: type defined but NOT YET POOLED. See <see cref="CPUData"/>
    /// for staging notes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VMData
    {
        // Identity
        public int InstanceId;
        public int ModuleSlot;

        // VOM4 ABA defense; mirrored from VMInstanceState semantics.
        public int Generation;

        // Wait/suspend state (persistent across yields)
        public int WaitCounter;
        public int WaitTargetInstanceId;

        // O9: Index in InstancePool.ActiveList for O(1) swap-remove
        public int ActiveListIndex;

        // Lifecycle
        public bool IsAlive;

        // Module variables (independent fixed buffer; replaces
        // NumberRegisters[ModuleVarRegBase..MaxRegisters-1] starting in VOM8).
        public MVarRegisters MVars;
    }

    /// <summary>
    /// [VOM7] Aggregated boundary view onto a VM instance's CPUData and VMData.
    /// Zero-copy ref struct: callers obtain it from façade or pool helpers and
    /// pass it across SYSCALL / host-binding boundaries in VOM9. VOM7/VOM8
    /// expose this only to internal scaffolding; SYSCALL signatures stay
    /// <c>(ref VMInstanceState s)</c> until VOM9 hard-breaks them.
    /// </summary>
    public readonly ref struct VMInstanceView
    {
        private readonly System.Span<CPUData> _cpu;
        private readonly System.Span<VMData> _data;

        public VMInstanceView(ref CPUData cpu, ref VMData data)
        {
            _cpu = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref cpu, 1);
            _data = System.Runtime.InteropServices.MemoryMarshal.CreateSpan(ref data, 1);
        }

        public ref CPUData Cpu { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0]; }
        public ref VMData Data { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0]; }

        // ----- [VOM9 Phase 1] Legacy-name pass-through ref properties -----
        // Mirrors the 18 ref-property forwards on VMInstanceState so that handler
        // bodies written against `(ref VMInstanceState s)` can compile unchanged
        // when their parameter type is swapped to `(ref VMInstanceView s)` in
        // VOM9 Phase 4. No field is duplicated; each property dereferences the
        // underlying CPUData/VMData via the single-element Span.

        // Identity / persistent (VMData)
        public ref int InstanceId { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].InstanceId; }
        public ref int ModuleSlot { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].ModuleSlot; }
        public ref int Generation { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].Generation; }
        public ref int ActiveListIndex { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].ActiveListIndex; }
        public ref bool IsAlive { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].IsAlive; }
        public ref int WaitCounter { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].WaitCounter; }
        public ref int WaitTargetInstanceId { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _data[0].WaitTargetInstanceId; }

        // Execution (CPUData)
        public ref int IP { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].IP; }
        public ref int RegisterBase { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].RegisterBase; }
        public ref int CallStackDepth { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].CallStackDepth; }
        public ref int LeafReturnIP { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].LeafReturnIP; }
        public ref int LeafRegisterBase { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].LeafRegisterBase; }
        public ref VMError ErrorFlag { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].ErrorFlag; }
        public ref VMStateFlags StateFlags { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].StateFlags; }
        public ref byte CleanupDepth { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].CleanupDepth; }
        public ref NumberRegisters Registers { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].Registers; }
        public ref CallStackFrames CallStack { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].CallStack; }
        public ref CleanupFrames CleanupStack { [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)] get => ref _cpu[0].CleanupStack; }
    }
}
