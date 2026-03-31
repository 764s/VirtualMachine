using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Bytecode opcodes for the VM.
    /// Phase 1 (tracer bullet): 7 instructions for execution loop.
    /// Phase 2 (step 5): data movement, control flow, arithmetic, comparison, boolean/unary.
    /// </summary>
    public enum OpCode : byte
    {
        // --- Phase 1: tracer bullet core ---
        NOP          = 0,
        LOAD_CONST   = 1,   // A=destReg, B=constIndex
        SYSCALL      = 2,   // A=syscallSlot, B=argStartReg, C=argCount
        WAIT         = 3,   // A=frameCount
        PUSH_CLEANUP = 4,   // A=cleanupEntryIP
        POP_CLEANUP  = 5,
        RETURN       = 6,

        // --- Phase 2: data movement ---
        MOVE         = 10,  // A=destReg, B=srcReg        → Reg[A] = Reg[B]

        // --- Phase 2: control flow ---
        JUMP             = 20,  // A=targetIP              → IP = A
        JUMP_IF_ZERO     = 21,  // A=targetIP, B=testReg   → if Reg[B] == 0 then IP = A
        JUMP_IF_NOT_ZERO = 22,  // A=targetIP, B=testReg   → if Reg[B] != 0 then IP = A

        // --- Phase 2: arithmetic (dest = A, lhs = B, rhs = C) ---
        ADD          = 30,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] + Reg[C]
        SUB          = 31,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] - Reg[C]
        MUL          = 32,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] * Reg[C]
        DIV          = 33,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] / Reg[C]
        MOD          = 34,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] % Reg[C]

        // --- Phase 2: comparison (result: 1 if true, 0 if false) ---
        CMP_EQ       = 40,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] == Reg[C]) ? 1 : 0
        CMP_NEQ      = 41,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] != Reg[C]) ? 1 : 0
        CMP_LT       = 42,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] <  Reg[C]) ? 1 : 0
        CMP_LTE      = 43,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] <= Reg[C]) ? 1 : 0
        CMP_GT       = 44,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] >  Reg[C]) ? 1 : 0
        CMP_GTE      = 45,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] >= Reg[C]) ? 1 : 0

        // --- Phase 2: boolean / unary ---
        AND          = 50,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B]!=0 && Reg[C]!=0) ? 1 : 0
        OR           = 51,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B]!=0 || Reg[C]!=0) ? 1 : 0
        NOT          = 52,  // A=destReg, B=srcReg            → Reg[A] = (Reg[B]==0) ? 1 : 0
        NEG          = 53,  // A=destReg, B=srcReg            → Reg[A] = -Reg[B]
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
