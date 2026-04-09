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

        // Panic errors — terminate instance immediately
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

        // Soft errors — returned in register, script handles
        SoftSyscallFailed = 128,
        SoftContainerOverflow = 129,
    }

    /// <summary>
    /// VM instance lifecycle flags. Used by Tick() to drive execution, cleanup, and termination.
    /// </summary>
    [System.Flags]
    public enum VMStateFlags : byte
    {
        None      = 0,
        Active    = 1,
        Killed    = 2,
        InCleanup = 4,
        Completed = 8,
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

        /// <summary>Caller's CleanupDepth at time of CALL (scopes function-level cleanup walk).</summary>
        public int CleanupBase;
    }

    /// <summary>
    /// Complete state of a single VM instance. Pure blittable struct for memcpy snapshot.
    /// Size per instance ≈ MaxRegisters*8 + CallStack + CleanupStack + fields.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VMInstanceState
    {
        // Identity
        public int InstanceId;
        public int ModuleSlot;

        // Execution state
        public int IP;
        public int RegisterBase;

        // Wait/suspend state
        public int WaitCounter;
        public int WaitTargetInstanceId;

        // Call stack
        public int CallStackDepth;

        // FO1: Leaf function call return state (avoids CallFrame push/pop)
        public int LeafReturnIP;
        public int LeafRegisterBase;

        // O9: Index in InstancePool.ActiveList for O(1) swap-remove
        public int ActiveListIndex;

        // Status flags
        public VMError ErrorFlag;
        public bool IsAlive;
        public VMStateFlags StateFlags;

        // Cleanup stack
        public byte CleanupDepth;

        // ----- Fixed-size arrays (inline for blittable memcpy) -----

        // Registers: 64 Number slots
        public NumberRegisters Registers;

        // Call stack frames: 16 max depth
        public CallStackFrames CallStack;

        // Cleanup stack frames: 8 max depth
        public CleanupFrames CleanupStack;
    }

    /// <summary>
    /// Inline fixed-size register file. MaxRegisters × Number (8 bytes each).
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
    /// Inline fixed-size call stack. 16 × CallFrame.
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
    /// Inline fixed-size cleanup stack. 8 × CleanupFrame.
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
