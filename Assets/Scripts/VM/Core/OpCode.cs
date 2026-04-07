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

        // --- P5: fused compare-and-branch (B-ε2) ---
        JUMP_IF_EQ   = 32,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] == Reg[C] then IP = A
        JUMP_IF_NEQ  = 33,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] != Reg[C] then IP = A
        JUMP_IF_LT   = 34,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] <  Reg[C] then IP = A
        JUMP_IF_LTE  = 35,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] <= Reg[C] then IP = A
        JUMP_IF_GT   = 36,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] >  Reg[C] then IP = A
        JUMP_IF_GTE  = 37,  // A=targetIP, B=lhsReg, C=rhsReg → if Reg[B] >= Reg[C] then IP = A

        // --- B-ε4: FORLOOP super-instruction ---
        FORLOOP      = 38,  // A=loopTopIP, B=counterReg, C=limitReg → Reg[B]+=1; if Reg[B]<Reg[C] IP=A

        // --- B-ζ2: fused constant-compare-and-branch ---
        JUMP_IF_EQ_K  = 39,  // A=targetIP, B=reg, C=constIndex → if Reg[B] == Constants[C] then IP = A
        JUMP_IF_NEQ_K = 40,  // A=targetIP, B=reg, C=constIndex → if Reg[B] != Constants[C] then IP = A
        JUMP_IF_LT_K  = 41,  // A=targetIP, B=reg, C=constIndex → if Reg[B] <  Constants[C] then IP = A
        JUMP_IF_LTE_K = 42,  // A=targetIP, B=reg, C=constIndex → if Reg[B] <= Constants[C] then IP = A
        JUMP_IF_GT_K  = 43,  // A=targetIP, B=reg, C=constIndex → if Reg[B] >  Constants[C] then IP = A
        JUMP_IF_GTE_K = 44,  // A=targetIP, B=reg, C=constIndex → if Reg[B] >= Constants[C] then IP = A

        // --- B-ζ3: SWITCH jump table ---
        SWITCH       = 45,  // A=defaultIP, B=testReg, C=jumpTableIdx → val=Reg[B].ToInt(); if 0≤val<len then JumpTables[C][val] else IP=A

        // --- O8: wide IP prefix (instruction compression) ---
        EXTEND_AX    = 46,  // A=hi_byte → next instruction's A operand is extended to (hi<<8 | lo)

        // --- Lang-1: module variable access (absolute addressing, bypasses Reg()) ---
        LOAD_MVAR    = 47,  // A=destReg, B=mvarSlot → Reg[A] = ModuleVars[ModuleVarRegBase + B]
        STORE_MVAR   = 48,  // A=mvarSlot, B=srcReg → ModuleVars[ModuleVarRegBase + A] = Reg[B]

        // --- Lang-1.1b: extended register access (heap-allocated, dedicated opcodes) ---
        LOAD_XREG    = 49,  // A=destReg, B=xidx_lo, C=xidx_hi → Reg[A] = ExtRegs[B | (C<<8)]
        STORE_XREG   = 50,  // A=xidx_lo, B=srcReg, C=xidx_hi → ExtRegs[A | (C<<8)] = Reg[B]

        // --- O15: sentinel (never emitted by compiler) ---
        SENTINEL     = 51,  // appended by VMProgram ctor; replaces per-instruction boundary check
    }

    /// <summary>
    /// Fixed-width bytecode instruction. Value type for array storage.
    /// O8: 4 bytes per instruction (1 byte opcode + 3×1 byte operand).
    /// Constructor accepts int for convenience; values are truncated to byte.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 4)]
    public struct Instruction
    {
        [FieldOffset(0)] public OpCode Code;  // 1 byte
        [FieldOffset(1)] public byte A;       // 1 byte
        [FieldOffset(2)] public byte B;       // 1 byte
        [FieldOffset(3)] public byte C;       // 1 byte

        public Instruction(OpCode code, int a = 0, int b = 0, int c = 0)
        {
            Code = code;
            A = (byte)a;
            B = (byte)b;
            C = (byte)c;
        }
    }
}
