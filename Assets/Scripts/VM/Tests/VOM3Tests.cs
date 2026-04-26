using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// VOM3 Phase1 validation: TransientInstancePool + sentinel CallFrame, so non-entry
/// functions are callable; <see cref="VMEngine.Call"/> writes mvars (no readonly check);
/// <see cref="VMEngine.ReadOnlyCall"/> rejects non-readonly callees and the
/// <see cref="VMEngine.StaticReadOnlyCall"/> alias still works.
/// </summary>
public static partial class VOM3Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) { Debug.Log($"[PASS] {name}"); _passed++; }
        else { Debug.LogError($"[FAIL] {name}"); _failed++; }
    }

    /// <summary>
    /// Compile a multi-function script with a designated entry; load it into a fresh
    /// VMWorld at slot 0. The entry function is whatever the caller passes (commonly
    /// a tiny throwaway "main"); other functions are callable via VMEngine.* without
    /// being the entry.
    /// </summary>
    private static (VMWorld world, VMProgram program) Compile(string source, string entryName)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(source, entryName, new Dictionary<string, int>());
        if (!result.Success)
            throw new Exception("compile failed: " + string.Join("; ", result.Errors));
        var world = new VMWorld();
        world.Modules.Load(0, result.Program);
        return (world, result.Program);
    }

    public static void RunAll()
    {
        _passed = 0;
        _failed = 0;

        RunNonEntryTests();
        RunCallTests();
        RunPoolTests();
        RunPhase2Tests();

        Debug.Log($"[VOM3Tests] {_passed} passed, {_failed} failed");
        if (_failed > 0)
            throw new Exception($"VOM3Tests failed: {_failed} assertion(s)");
    }
}
