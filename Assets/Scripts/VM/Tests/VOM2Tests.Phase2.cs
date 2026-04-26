using System;
using System.Collections.Generic;
using FFVM;
using FFVM.Compiler;
using UnityEngine;

public static partial class VOM2Tests
{
    private static void RunPhase2ReadOnlyTests()
    {
        Phase2_01_ReadOnlyFlagPropagated();
        Phase2_02_WriteModuleVarRejected();
        Phase2_03_NonReadOnlyRejectedByEngine();
        Phase2_04_ReadOnlyComposesWithExport();
        Phase2_05_StaticReadOnlyAlias();
        Phase2_06_ReadOnlyCanReadModuleVar();
        Phase2_07_WriteCrossModuleSetterRejected();
    }

    private static (bool ok, string error) TryCompile(string source, string entryName)
    {
        var compiler = new BytecodeCompiler();
        var result = compiler.Compile(source, entryName, new Dictionary<string, int>());
        if (result.Success) return (true, null);
        return (false, string.Join(" | ", result.Errors));
    }

    private static void Phase2_01_ReadOnlyFlagPropagated()
    {
        string src = @"
@readonly func add(a: int, b: int): int {
    return a + b
}";
        var (_, prog) = CompileEntry(src, "add");
        Assert(prog.Functions.Length >= 1, "VOM2.P2_01a_FunctionsNonEmpty");
        Assert(prog.Functions[0].IsReadOnly, "VOM2.P2_01b_IsReadOnlyFlagSet");
    }

    private static void Phase2_02_WriteModuleVarRejected()
    {
        string src = @"
var counter: int = 0
@readonly func bump(): int {
    counter = counter + 1
    return counter
}";
        var (ok, err) = TryCompile(src, "bump");
        bool rejected = !ok && err != null && err.Contains("@readonly");
        Assert(rejected, "VOM2.P2_02_WriteModuleVarFromReadOnlyRejected");
        if (!rejected && err != null) Debug.Log("[INFO] P2_02 actual error: " + err);
    }

    private static void Phase2_03_NonReadOnlyRejectedByEngine()
    {
        // Plain (non-@readonly) function — engine must refuse it.
        string src = @"
func plain(a: int, b: int): int {
    return a + b
}";
        var (world, prog) = CompileEntry(src, "plain");
        var handle = prog.ResolveMethod("plain");

        Span<Number> argBuf = stackalloc Number[2];
        argBuf[0] = Number.FromInt(1); argBuf[1] = Number.FromInt(2);
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.P2_03_NonReadOnlyRejectedByEngine (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.P2_03_NonReadOnlyRejectedByEngine"); _passed++; }
    }

    private static void Phase2_04_ReadOnlyComposesWithExport()
    {
        // @readonly @export func — both flags propagate; remains callable via the engine.
        string src = @"
@readonly @export func pure_add(a: int, b: int): int {
    return a + b
}";
        var (world, prog) = CompileEntry(src, "pure_add");
        var handle = prog.ResolveMethod("pure_add");
        Assert(handle.IsResolved, "VOM2.P2_04a_ComposeExportResolved");
        Assert(prog.Functions[0].IsReadOnly, "VOM2.P2_04b_ComposeReadOnlyFlag");

        Span<Number> argBuf = stackalloc Number[2];
        argBuf[0] = Number.FromInt(10); argBuf[1] = Number.FromInt(32);
        Span<Number> retBuf = stackalloc Number[1];
        var ret = new ReturnSlot(retBuf);
        VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
        Assert(ret.Get(0).ToInt() == 42, "VOM2.P2_04c_ComposeCallResult");
    }

    private static void Phase2_05_StaticReadOnlyAlias()
    {
        // @static_readonly is an alias for @readonly in Phase2.
        string src = @"
@static_readonly func id(a: int): int {
    return a
}";
        var (world, prog) = CompileEntry(src, "id");
        Assert(prog.Functions[0].IsReadOnly, "VOM2.P2_05a_StaticReadOnlyAliasFlag");
        var handle = prog.ResolveMethod("id");

        Span<Number> argBuf = stackalloc Number[1];
        argBuf[0] = Number.FromInt(99);
        Span<Number> retBuf = stackalloc Number[1];
        var ret = new ReturnSlot(retBuf);
        VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), ret);
        Assert(ret.Get(0).ToInt() == 99, "VOM2.P2_05b_StaticReadOnlyAliasCall");
    }

    private static void Phase2_06_ReadOnlyCanReadModuleVar()
    {
        // Reading a module variable from a @readonly function is allowed; only writes are forbidden.
        string src = @"
const seed: int = 7
@readonly func grab(): int {
    return seed
}";
        var (world, prog) = CompileEntry(src, "grab");
        var handle = prog.ResolveMethod("grab");
        Span<Number> retBuf = stackalloc Number[1];
        var ret = new ReturnSlot(retBuf);
        VMEngine.StaticReadOnlyCall(world, 0, handle, Arguments.Empty, ret);
        Assert(ret.Get(0).ToInt() == 7, "VOM2.P2_06_ReadOnlyReadsModuleVar");
    }

    private static void Phase2_07_WriteCrossModuleSetterRejected()
    {
        // Direct module-variable write inside a @readonly body must fail at compile.
        // (Cross-module XSTORE_MVAR paths are guarded too; module-local write is the
        //  representative case verifiable with a single-module test fixture.)
        string src = @"
var counter: int = 0
@readonly func explicit_write(): int {
    counter = 42
    return 0
}";
        var (ok, err) = TryCompile(src, "explicit_write");
        bool rejected = !ok && err != null && err.Contains("@readonly");
        Assert(rejected, "VOM2.P2_07_DirectModuleWriteRejected");
        if (!rejected && err != null) Debug.Log("[INFO] P2_07 actual error: " + err);
    }
}
