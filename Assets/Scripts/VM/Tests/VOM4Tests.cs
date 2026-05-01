using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

/// <summary>
/// VOM4 validation: <see cref="VMEngine.YieldCall"/> + <see cref="YieldHandle"/>
/// API; reentrancy reject; Generation-based stale-handle defense.
/// </summary>
public static partial class VOM4Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) _passed++; else _failed++;
        TestHarness.Assert(cond, name);
    }

    /// <summary>
    /// Compile a multi-function script with a designated entry; load it into a fresh
    /// VMWorld at slot 0. Returns world + program. Optional syscalls map registers
    /// names → slot indices (default empty).
    /// </summary>
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
        TestHarness.BeginSuite("VOM4Tests");

        RunBasicTests();
        RunHandleTests();
        RunReentrancyTests();
        RunPerfTests();

        Debug.Log($"[VOM4Tests] {_passed} passed, {_failed} failed");
        TestHarness.EndSuite();
        if (_failed > 0)
            throw new Exception($"VOM4Tests failed: {_failed} assertion(s)");
    }
}
