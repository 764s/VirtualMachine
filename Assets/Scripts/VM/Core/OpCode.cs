using System.Runtime.InteropServices;

namespace FFVM
{
    /// <summary>
    /// Bytecode opcodes for the VM.
    /// Continuous numbering 0-31 for JIT jump table optimization (O2).
    /// Phase 1 (tracer bullet): 8 instructions for execution loop.
    /// Phase 2 (step 5): data movement, control flow, arithmetic, comparison, boolean/unary.
    /// Phase 3 (step 8): function calls.
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
        WAIT_FOR     = 7,   // A=srcReg → WaitTargetInstanceId = Reg[A]

        // --- Phase 2: data movement ---
        MOVE         = 8,   // A=destReg, B=srcReg        → Reg[A] = Reg[B]

        // --- Phase 2: control flow ---
        JUMP             = 9,   // A=targetIP              → IP = A
        JUMP_IF_ZERO     = 10,  // A=targetIP, B=testReg   → if Reg[B] == 0 then IP = A
        JUMP_IF_NOT_ZERO = 11,  // A=targetIP, B=testReg   → if Reg[B] != 0 then IP = A

        // --- Phase 2: arithmetic (dest = A, lhs = B, rhs = C) ---
        ADD          = 12,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] + Reg[C]
        SUB          = 13,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] - Reg[C]
        MUL          = 14,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] * Reg[C]
        DIV          = 15,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] / Reg[C]
        MOD          = 16,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = Reg[B] % Reg[C]

        // --- Phase 2: comparison (result: 1 if true, 0 if false) ---
        CMP_EQ       = 17,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] == Reg[C]) ? 1 : 0
        CMP_NEQ      = 18,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] != Reg[C]) ? 1 : 0
        CMP_LT       = 19,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] <  Reg[C]) ? 1 : 0
        CMP_LTE      = 20,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] <= Reg[C]) ? 1 : 0
        CMP_GT       = 21,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] >  Reg[C]) ? 1 : 0
        CMP_GTE      = 22,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B] >= Reg[C]) ? 1 : 0

        // --- Phase 2: boolean / unary ---
        AND          = 23,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B]!=0 && Reg[C]!=0) ? 1 : 0
        OR           = 24,  // A=destReg, B=lhsReg, C=rhsReg → Reg[A] = (Reg[B]!=0 || Reg[C]!=0) ? 1 : 0
        NOT          = 25,  // A=destReg, B=srcReg            → Reg[A] = (Reg[B]==0) ? 1 : 0
        NEG          = 26,  // A=destReg, B=srcReg            → Reg[A] = -Reg[B]

        // --- Phase 3: function calls ---
        CALL         = 27,  // A=targetEntryIP, B=callerWindowSize → push CallFrame + jump
        RET_FUNC     = 28,  // pop CallFrame, restore IP + RegisterBase

        // --- FO1: leaf function optimization ---
        CALL_LEAF    = 29,  // A=targetEntryIP, B=callerWindowSize → skip CallFrame, save to inst fields
        RET_LEAF     = 30,  // restore IP + RegisterBase from inst fields (no CallFrame pop)

        // --- SO1: struct block copy ---
        COPY_BLOCK   = 31,  // A=destReg, B=srcReg, C=count → Reg[A..A+C-1] = Reg[B..B+C-1]

        // --- O15: sentinel (never emitted by compiler) ---
        SENTINEL     = 32,  // appended by VMProgram ctor; replaces per-instruction boundary check
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
