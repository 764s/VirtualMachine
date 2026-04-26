using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// VOM2 Phase1 validation: Arguments / ReturnSlot ABI types + VMEngine.StaticReadOnlyCall.
/// Phase2 (compiler @readonly + opcode whitelist + perf gate) lives in VOM2Phase2Tests.
/// </summary>
public static partial class VOM2Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) { Debug.Log($"[PASS] {name}"); _passed++; }
        else { Debug.LogError($"[FAIL] {name}"); _failed++; }
    }

    /// <summary>
    /// Compile a single-function script with the given entry name, load it into a fresh
    /// VMWorld at slot 0, and return both. The named function is the entry — its body
    /// ends with RETURN (Completed) so StaticReadOnlyCall can run it directly.
    /// </summary>
    private static (VMWorld world, VMProgram program) CompileEntry(string source, string entryName)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(source, entryName, new Dictionary<string, int>());
        if (!result.Success)
        {
            throw new Exception($"compile failed: {string.Join("; ", result.Errors)}");
        }
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        return (world, result.Program);
    }

    public static void RunAll()
    {
        _passed = 0;
        _failed = 0;

        RunAbiTypeTests();
        RunStaticReadOnlyCallTests();
        RunAbiViolationTests();
        RunPhase2ReadOnlyTests();

        Debug.Log($"[VOM2Tests] {_passed} passed, {_failed} failed");
        if (_failed > 0)
        {
            throw new Exception($"VOM2Tests failed: {_failed} assertion(s)");
        }
    }
}
