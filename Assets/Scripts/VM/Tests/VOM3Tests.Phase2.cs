using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// VOM3 Phase2: runtime ReadOnly enforcement (VMStateFlags.ReadOnlyMode +
/// opcode whitelist + ReadOnlyViolationException + SyscallTable.IsReadOnly).
/// </summary>
public static partial class VOM3Tests
{
    private static (VMWorld world, VMProgram program) CompileWithSyscalls(
        string source, string entry, Dictionary<string, int> syscalls)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(source, entry, syscalls);
        if (!result.Success)
            throw new Exception("compile failed: " + string.Join("; ", result.Errors));
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        return (world, result.Program);
    }

    private static void RunPhase2Tests()
    {
        // E.T1: SYSCALL of a non-readonly syscall inside a @readonly fn → reject.
        {
            var (world, prog) = CompileWithSyscalls(
                "@readonly func ro(): int { Mut() return 0 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int> { { "Mut", 0 } });
            world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);
            var h = prog.ResolveMethod("ro");
            Span<Number> retBuf = stackalloc Number[1];
            bool threw = false;
            try { VMEngine.ReadOnlyCall(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf)); }
            catch (ReadOnlyViolationException) { threw = true; }
            Assert(threw, "VOM3.P2_E1_SYSCALL_NonReadOnly_Rejects");
        }

        // E.T2: SYSCALL of a @readonly syscall inside a @readonly fn → pass.
        {
            var (world, prog) = CompileWithSyscalls(
                "@readonly func ro(): int { Pure() return 7 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int> { { "Pure", 0 } });
            int hitCount = 0;
            world.Syscalls.Register(0, "Pure", (ref VMInstanceState s) => { hitCount++; }, isReadOnly: true);
            var h = prog.ResolveMethod("ro");
            Span<Number> retBuf = stackalloc Number[1];
            VMEngine.ReadOnlyCall(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf));
            Assert(retBuf[0].ToInt() == 7 && hitCount == 1, "VOM3.P2_E2_SYSCALL_ReadOnly_Passes");
        }

        // E.T3: WAIT inside @readonly → ReadOnlyViolation (early reject).
        {
            var (world, prog) = CompileWithSyscalls(
                "@readonly func ro(): int { wait 1 return 0 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int>());
            var h = prog.ResolveMethod("ro");
            Span<Number> retBuf = stackalloc Number[1];
            bool threw = false;
            try { VMEngine.ReadOnlyCall(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf)); }
            catch (ReadOnlyViolationException ex)
            {
                threw = ex.OpcodeName.Contains("WAIT") && ex.FunctionName == "ro";
            }
            Assert(threw, "VOM3.P2_E3_WAIT_Rejects_WithDiagnostics");
        }

        // E.T4: Plain Call (non-readonly) executes a SYSCALL with side-effects fine.
        {
            var (world, prog) = CompileWithSyscalls(
                "func mut(): int { Mut() return 1 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int> { { "Mut", 0 } });
            int hits = 0;
            world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { hits++; }, isReadOnly: false);
            var h = prog.ResolveMethod("mut");
            Span<Number> retBuf = stackalloc Number[1];
            VMEngine.Call(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf));
            Assert(retBuf[0].ToInt() == 1 && hits == 1, "VOM3.P2_E4_PlainCall_NoReadOnlyGate");
        }

        // E.T5: After ReadOnly violation, transient pool slot is returned (capacity unchanged).
        {
            var (world, prog) = CompileWithSyscalls(
                "@readonly func ro(): int { Mut() return 0 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int> { { "Mut", 0 } });
            world.Syscalls.Register(0, "Mut", (ref VMInstanceState s) => { }, isReadOnly: false);
            var h = prog.ResolveMethod("ro");
            int capBefore = world.TransientPool.Capacity;
            for (int i = 0; i < 100; i++)
            {
                Span<Number> retBuf2 = stackalloc Number[1];
                try { VMEngine.ReadOnlyCall(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf2)); }
                catch (ReadOnlyViolationException) { }
            }
            // 100 violations all returned the slot; pool never grew past initial capacity.
            Assert(world.TransientPool.Capacity == capBefore, "VOM3.P2_E5_PoolSlotReturnedOnViolation");
        }

        // E.T6: ReadOnlyCall on Engine entry rejects non-@readonly even if the function
        // body contains no banned ops (Engine-entry guard from VOM3 Phase1, regression).
        {
            var (world, prog) = CompileWithSyscalls(
                "func plain(): int { return 5 } func main(): int { return 0 }",
                "main",
                new Dictionary<string, int>());
            var h = prog.ResolveMethod("plain");
            Span<Number> retBuf = stackalloc Number[1];
            bool threw = false;
            try { VMEngine.ReadOnlyCall(world, 0, h, Arguments.Empty, new ReturnSlot(retBuf)); }
            catch (VMABIException) { threw = true; }
            Assert(threw, "VOM3.P2_E6_EngineEntry_RejectsNonReadOnly");
        }

        RunPhase2PerfTests();
    }
}
