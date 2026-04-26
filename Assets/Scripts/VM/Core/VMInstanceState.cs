using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Error flags for VM instance. Panic errors terminate the instance.
    /// Soft errors are returned from Syscalls for script-level handling.
    /// </summary>
    public enum VMError : byte
    {
        None = 0,

        // Panic errors 鈥?terminate instance immediately
        PanicStackOverflow = 1,
        PanicIllegalInstruction = 2,
        PanicDivideByZero = 3,
        PanicOutOfBounds = 4,
        PanicModuleNotLoaded = 5,
        PanicUnresolvedExtern = 6,
        PanicStepLimitExceeded = 7,

        // Lang-6: cross-instance call errors
        PanicInvalidInstanceId = 8,
        PanicExportNotFound = 9,

        // VOM3 Phase2: ReadOnly call write/yield violation
        PanicReadOnlyViolation = 10,

        // Soft errors 鈥?returned in register, script handles
        SoftSyscallFailed = 128,
        SoftContainerOverflow = 129,
    }

    /// <summary>
    /// VM instance lifecycle flags. Used by Tick() to drive execution, cleanup, and termination.
    /// </summary>
    [System.Flags]
    public enum VMStateFlags : byte
    {
        None         = 0,
        Active       = 1,
        Killed       = 2,
        InCleanup    = 4,
        Completed    = 8,
        // VOM3 Phase2: instance is executing inside a VMEngine.ReadOnlyCall.
        // Gates STORE_MVAR / XSTORE_MVAR / STORE_XREG / non-readonly XCALL /
        // WAIT / WAIT_FOR / non-readonly SYSCALL at runtime.
        ReadOnlyMode = 16,
        // VOM4: instance is currently inside a host callback from VMWorld.ExecuteInstance.
        // Used by YieldHandle to reject reentrant Release / TickOnce / ReadReturn
        // against the same instance while syscall host code is on the stack.
        HostExecuting = 32,
    }

    /// <summary>
    /// A single cleanup entry on the fixed-depth cleanup stack.
    /// Stores the bytecode IP of a cleanup (defer) block.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CleanupFrame
    {
        /// <summary>Instruction pointer of the cleanup block entry.</summary>
        public int CleanupEntryIP;
    }

    /// <summary>
    /// A single call frame on the lightweight call stack.
    /// Stored as value type for memcpy snapshot.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CallFrame
    {
        /// <summary>Instruction pointer to return to after call completes.</summary>
        public int ReturnIP;

        /// <summary>Module slot to return to (for cross-module calls).</summary>
        public int ReturnModuleSlot;

        /// <summary>Base register index for the caller's register window.</summary>
        public int RegisterBase;

        /// <summary>
        /// Caller's CleanupDepth at time of CALL (scopes function-level cleanup walk).
        /// High bit (0x80000000) stores WasInCleanup flag: set if InCleanup was active
        /// when this CALL executed. Used to restore InCleanup state after the callee's
        /// cleanup completes (DC: defer-call Level 3 support).
        /// </summary>
        public int CleanupBase;

        /// <summary>
        /// Saved return value (r0) before function-scoped cleanup execution.
        /// Stored per-frame to support nested cleanup: a cleanup block may CALL
        /// a function that itself has defer, creating nested savedR0 contexts.
        /// Only written by RET_FUNC when pending cleanups exist; read by RETURN
        /// when all function-scoped cleanups complete and the frame is popped.
        /// </summary>
        public long SavedR0;
    }

    /// <summary>
    /// [VOM8] Complete state of a single VM instance, structurally split into
    /// <see cref="CPUData"/> (execution-private: IP / Registers / CallStack /
    /// CleanupStack / leaf return frame / per-execution flags) and
    /// <see cref="VMData"/> (instance-persistent: identity / generation / wait
    /// state / active-list index / module variables).
    ///
    /// All legacy field names are preserved as ref-returning properties so that
    /// existing call sites 鈥?including <c>fixed (long* p = inst.Registers.Raw)</c>
    /// pinning patterns and <c>inst.WaitCounter = N</c> assignments 鈥?keep
    /// compiling unchanged. The wrapper is still blittable: <see cref="Cpu"/>
    /// and <see cref="Data"/> are <c>Sequential</c> structs containing only
    /// blittable fields and inline fixed buffers.
    ///
    /// VOM8 status: storage layout is now CPUData+VMData; the
    /// <see cref="InstancePool"/> still keeps a single <c>VMInstanceState[]</c>.
    /// VOM9 splits the pool into dual <c>Datas[]</c> + <c>Cpus[]</c> arrays,
    /// migrates module variables off <c>NumberRegisters[ModuleVarRegBase..]</c>
    /// onto <see cref="VMData.MVars"/>, and breaks SYSCALL signatures from
    /// <c>(ref VMInstanceState)</c> to <c>(ref VMInstanceView)</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VMInstanceState
    {
        // [VOM8] CPU-private execution storage (see CPUData in VMObjectModel.cs).
        public CPUData Cpu;

        // [VOM8] Persistent instance/identity storage (see VMData in VMObjectModel.cs).
        public VMData Data;

        // ----- Legacy field-name ref-property forwards -----
        // Each property returns a ref into the corresponding subfield of Cpu or
        // Data, so legacy code that did `inst.IP = N`, `inst.Registers.Set(...)`,
        // or `fixed (long* p = inst.Registers.Raw)` keeps working unchanged.
        // No field is duplicated: the ref-property is the only way to reach the
        // underlying storage via the legacy name.

        // Identity / persistent (VMData)
#if NET7_0_OR_GREATER
        [UnscopedRef] public ref int InstanceId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.InstanceId; }
        [UnscopedRef] public ref int ModuleSlot { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.ModuleSlot; }
        [UnscopedRef] public ref int Generation { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.Generation; }
        [UnscopedRef] public ref int ActiveListIndex { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.ActiveListIndex; }
        [UnscopedRef] public ref bool IsAlive { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.IsAlive; }
        [UnscopedRef] public ref int WaitCounter { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.WaitCounter; }
        [UnscopedRef] public ref int WaitTargetInstanceId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Data.WaitTargetInstanceId; }

        // Execution (CPUData)
        [UnscopedRef] public ref int IP { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.IP; }
        [UnscopedRef] public ref int RegisterBase { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.RegisterBase; }
        [UnscopedRef] public ref int CallStackDepth { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.CallStackDepth; }
        [UnscopedRef] public ref int LeafReturnIP { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.LeafReturnIP; }
        [UnscopedRef] public ref int LeafRegisterBase { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.LeafRegisterBase; }
        [UnscopedRef] public ref VMError ErrorFlag { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.ErrorFlag; }
        [UnscopedRef] public ref VMStateFlags StateFlags { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.StateFlags; }
        [UnscopedRef] public ref byte CleanupDepth { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.CleanupDepth; }
        [UnscopedRef] public ref NumberRegisters Registers { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.Registers; }
        [UnscopedRef] public ref CallStackFrames CallStack { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.CallStack; }
        [UnscopedRef] public ref CleanupFrames CleanupStack { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.CleanupStack; }
#else
        // Unity / C# < 11: [UnscopedRef] on ref-returning struct properties requires C# 11.
        // Provide value get/set for primitive fields so inst.X = v and v = inst.X keep working
        // when inst is accessed via a ref local. Complex struct fields (Registers, CallStack,
        // CleanupStack) are get-only — mutation must go through Cpu/Data sub-fields directly.
        public int InstanceId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.InstanceId; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.InstanceId = value; }
        public int ModuleSlot { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.ModuleSlot; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.ModuleSlot = value; }
        public int Generation { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.Generation; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.Generation = value; }
        public int ActiveListIndex { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.ActiveListIndex; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.ActiveListIndex = value; }
        public bool IsAlive { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.IsAlive; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.IsAlive = value; }
        public int WaitCounter { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.WaitCounter; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.WaitCounter = value; }
        public int WaitTargetInstanceId { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Data.WaitTargetInstanceId; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Data.WaitTargetInstanceId = value; }
        public int IP { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.IP; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.IP = value; }
        public int RegisterBase { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.RegisterBase; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.RegisterBase = value; }
        public int CallStackDepth { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.CallStackDepth; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.CallStackDepth = value; }
        public int LeafReturnIP { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.LeafReturnIP; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.LeafReturnIP = value; }
        public int LeafRegisterBase { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.LeafRegisterBase; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.LeafRegisterBase = value; }
        public VMError ErrorFlag { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.ErrorFlag; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.ErrorFlag = value; }
        public VMStateFlags StateFlags { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.StateFlags; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.StateFlags = value; }
        public byte CleanupDepth { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.CleanupDepth; [MethodImpl(MethodImplOptions.AggressiveInlining)] set => Cpu.CleanupDepth = value; }
        public NumberRegisters Registers { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.Registers; }
        public CallStackFrames CallStack { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.CallStack; }
        public CleanupFrames CleanupStack { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => Cpu.CleanupStack; }
#endif
    }

    /// <summary>
    /// Inline fixed-size register file. MaxRegisters 脳 Number (8 bytes each).
    /// Uses fixed-size buffer so size auto-follows VMConstants.MaxRegisters.
    /// Number is LayoutKind.Explicit, Size=8 with long Raw at offset 0,
    /// so long* and Number* are safely interchangeable via reinterpret cast.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NumberRegisters
    {
        public fixed long Raw[VMConstants.MaxRegisters];

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
    /// Inline fixed-size call stack. 16 脳 CallFrame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CallStackFrames
    {
        public CallFrame F00, F01, F02, F03, F04, F05, F06, F07;
        public CallFrame F08, F09, F10, F11, F12, F13, F14, F15;

        public unsafe CallFrame Get(int index)
        {
            fixed (CallFrame* ptr = &F00)
            {
                return ptr[index];
            }
        }

        public unsafe void Set(int index, CallFrame value)
        {
            fixed (CallFrame* ptr = &F00)
            {
                ptr[index] = value;
            }
        }
    }

    /// <summary>
    /// Inline fixed-size cleanup stack. 8 脳 CleanupFrame.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CleanupFrames
    {
        public CleanupFrame F00, F01, F02, F03, F04, F05, F06, F07;

        public unsafe CleanupFrame Get(int index)
        {
            fixed (CleanupFrame* ptr = &F00)
            {
                return ptr[index];
            }
        }

        public unsafe void Set(int index, CleanupFrame value)
        {
            fixed (CleanupFrame* ptr = &F00)
            {
                ptr[index] = value;
            }
        }
    }
}
