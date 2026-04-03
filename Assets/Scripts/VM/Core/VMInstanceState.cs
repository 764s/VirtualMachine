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
    /// Size per instance ≈ 8*64 + 12*16 + overhead ≈ 740 bytes.
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
    /// Inline fixed-size register file. 64 × Number (8 bytes each) = 512 bytes.
    /// Using explicit struct to keep blittable without unsafe.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NumberRegisters
    {
        // We use a flat approach: fields r0..r63
        // For indexed access, use the Get/Set methods via unsafe pointer or switch.
        public Number R00, R01, R02, R03, R04, R05, R06, R07;
        public Number R08, R09, R10, R11, R12, R13, R14, R15;
        public Number R16, R17, R18, R19, R20, R21, R22, R23;
        public Number R24, R25, R26, R27, R28, R29, R30, R31;
        public Number R32, R33, R34, R35, R36, R37, R38, R39;
        public Number R40, R41, R42, R43, R44, R45, R46, R47;
        public Number R48, R49, R50, R51, R52, R53, R54, R55;
        public Number R56, R57, R58, R59, R60, R61, R62, R63;

        public unsafe Number Get(int index)
        {
            fixed (Number* ptr = &R00)
            {
                return ptr[index];
            }
        }

        public unsafe void Set(int index, Number value)
        {
            fixed (Number* ptr = &R00)
            {
                ptr[index] = value;
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
