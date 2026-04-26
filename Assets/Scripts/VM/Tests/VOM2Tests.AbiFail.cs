using System;
using FFVM;
using UnityEngine;

public static partial class VOM2Tests
{
    private static void RunAbiViolationTests()
    {
        string src = @"
@readonly func add(a: int, b: int): int {
    return a + b
}";
        var (world, prog) = CompileEntry(src, "add");
        var handle = prog.ResolveMethod("add");

        AbiViolation_ArgCountTooFew(world, handle);
        AbiViolation_ArgCountTooMany(world, handle);
        AbiViolation_ReturnSlotTooSmall(world, handle);
        AbiViolation_UnresolvedHandle(world);
        AbiViolation_StaleHandle();
        AbiViolation_ModuleNotLoaded(world, handle);
        AbiViolation_WorldNull(handle);
    }

    private static void AbiViolation_ArgCountTooFew(VMWorld world, MethodHandle handle)
    {
        Span<Number> argBuf = stackalloc Number[1];
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL01_ArgCountTooFew (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL01_ArgCountTooFew"); _passed++; }
    }

    private static void AbiViolation_ArgCountTooMany(VMWorld world, MethodHandle handle)
    {
        Span<Number> argBuf = stackalloc Number[3];
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL02_ArgCountTooMany (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL02_ArgCountTooMany"); _passed++; }
    }

    private static void AbiViolation_ReturnSlotTooSmall(VMWorld world, MethodHandle handle)
    {
        Span<Number> argBuf = stackalloc Number[2];
        argBuf[0] = Number.FromInt(1); argBuf[1] = Number.FromInt(2);
        try
        {
            VMEngine.StaticReadOnlyCall(world, 0, handle, new Arguments(argBuf), new ReturnSlot(Span<Number>.Empty));
            Debug.LogError("[FAIL] VOM2.FAIL03_ReturnSlotTooSmall (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL03_ReturnSlotTooSmall"); _passed++; }
    }

    private static void AbiViolation_UnresolvedHandle(VMWorld world)
    {
        Span<Number> argBuf = stackalloc Number[2];
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(world, 0, MethodHandle.Invalid, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL04_UnresolvedHandle (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL04_UnresolvedHandle"); _passed++; }
    }

    private static void AbiViolation_StaleHandle()
    {
        string src = @"
@readonly func add(a: int, b: int): int {
    return a + b
}";
        var (worldS, progS) = CompileEntry(src, "add");
        var staleHandle = progS.ResolveMethod("add");
        progS.Invalidate();

        Span<Number> argBuf = stackalloc Number[2];
        argBuf[0] = Number.FromInt(1); argBuf[1] = Number.FromInt(2);
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(worldS, 0, staleHandle, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL05_StaleHandle (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL05_StaleHandle"); _passed++; }
    }

    private static void AbiViolation_ModuleNotLoaded(VMWorld world, MethodHandle handle)
    {
        Span<Number> argBuf = stackalloc Number[2];
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(world, 7, handle, new Arguments(argBuf), new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL06_ModuleNotLoaded (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL06_ModuleNotLoaded"); _passed++; }
    }

    private static void AbiViolation_WorldNull(MethodHandle handle)
    {
        Span<Number> retBuf = stackalloc Number[1];
        try
        {
            VMEngine.StaticReadOnlyCall(null, 0, handle, Arguments.Empty, new ReturnSlot(retBuf));
            Debug.LogError("[FAIL] VOM2.FAIL07_WorldNull (expected VMABIException)"); _failed++;
        }
        catch (VMABIException) { Debug.Log("[PASS] VOM2.FAIL07_WorldNull"); _passed++; }
    }
}
