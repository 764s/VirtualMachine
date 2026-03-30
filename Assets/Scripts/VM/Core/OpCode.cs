using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Bytecode opcodes for the tracer-bullet VM.
    /// Minimal set: 7 instructions to validate the full execution loop.
    /// </summary>
    public enum OpCode : byte
    {
        NOP          = 0,
        LOAD_CONST   = 1,   // A=destReg, B=constIndex
        SYSCALL      = 2,   // A=syscallSlot, B=argStartReg, C=argCount
        WAIT         = 3,   // A=frameCount
        PUSH_CLEANUP = 4,   // A=cleanupEntryIP
        POP_CLEANUP  = 5,
        RETURN       = 6,
    }

    /// <summary>
    /// Fixed-width bytecode instruction. Value type for array storage.
    /// 16 bytes per instruction (1 byte opcode + 3 padding + 3×4 int).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Instruction
    {
        public OpCode Code;
        public int A;
        public int B;
        public int C;

        public Instruction(OpCode code, int a = 0, int b = 0, int c = 0)
        {
            Code = code;
            A = a;
            B = b;
            C = c;
        }
    }
}
