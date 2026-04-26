using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// VOM5 validation: per-instance <see cref="HostBindings"/> + <see cref="VMInstance"/> façade.
/// </summary>
public static partial class VOM5Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) { Debug.Log($"[PASS] {name}"); _passed++; }
        else { Debug.LogError($"[FAIL] {name}"); _failed++; }
    }

    private static (VMWorld world, VMProgram program) Compile(
        string source, string entryName, Dictionary<string, int> syscalls = null)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(source, entryName, syscalls ?? new Dictionary<string, int>());
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

        RunBindingsTests();
        RunFacadeTests();
        RunCompatTests();
        RunSnapshotTests();

        Debug.Log($"[VOM5Tests] {_passed} passed, {_failed} failed");
        if (_failed > 0)
            throw new Exception($"VOM5Tests failed: {_failed} assertion(s)");
    }
}
