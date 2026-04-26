using System;
using System.Reflection;
using System.Runtime.InteropServices;
using FFVM;
using UnityEngine;

/// <summary>
/// VOM7 validation: type identity & blittable layout for the new
/// <see cref="CPUData"/> / <see cref="VMData"/> / <see cref="MVarRegisters"/>
/// scaffolding.
///
/// Scope (intentionally narrow): VOM7 is pure additive — types exist but
/// are NOT yet wired into the VM execution path. These tests guard against
/// type-identity drift and confirm that the new structs are size-stable
/// against <see cref="VMConstants"/>.
/// </summary>
public static class VOM7Tests
{
    private static int _passed;
    private static int _failed;

    private static void Assert(bool cond, string name)
    {
        if (cond) { Debug.Log($"[PASS] {name}"); _passed++; }
        else { Debug.LogError($"[FAIL] {name}"); _failed++; }
    }

    private static void AssertEq(int actual, int expected, string name)
    {
        Assert(actual == expected, $"{name} (actual={actual}, expected={expected})");
    }

    public static void RunAll()
    {
        _passed = 0;
        _failed = 0;

        Test_MVarRegisters_Size();
        Test_VMData_Blittable();
        Test_CPUData_Blittable();
        Test_VMInstanceState_Unchanged();
        Test_FieldCoverage();
        Test_VMInstanceView_Roundtrip();

        Debug.Log($"[VOM7Tests] {_passed} passed, {_failed} failed");
        if (_failed > 0)
            throw new Exception($"VOM7Tests failed: {_failed} assertion(s)");
    }

    private static unsafe void Test_MVarRegisters_Size()
    {
        // 8 bytes per slot × ModuleVarSlots. Sequential layout, no padding.
        AssertEq(sizeof(MVarRegisters), VMConstants.ModuleVarSlots * 8, "MVarRegisters.Size");
    }

    private static void Test_VMData_Blittable()
    {
        // Marshal.SizeOf throws if the type is not blittable. Pinning a default
        // value confirms it is layout-stable for snapshot use later.
        var size = Marshal.SizeOf<VMData>();
        Assert(size > 0, "VMData.Marshal.SizeOf > 0");
        var data = default(VMData);
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        Assert(true, "VMData pinnable (blittable)");
        handle.Free();
    }

    private static void Test_CPUData_Blittable()
    {
        var size = Marshal.SizeOf<CPUData>();
        Assert(size > 0, "CPUData.Marshal.SizeOf > 0");
        var cpu = default(CPUData);
        var handle = GCHandle.Alloc(cpu, GCHandleType.Pinned);
        Assert(true, "CPUData pinnable (blittable)");
        handle.Free();
    }

    private static void Test_VMInstanceState_Unchanged()
    {
        // VOM7 hard invariant: legacy struct byte layout untouched.
        // The legacy snapshot suite already covers exact byte equality for
        // round-trip; here we assert size stays large enough to hold all
        // documented fields (registers + call stack + cleanup stack + scalars).
        var size = Marshal.SizeOf<VMInstanceState>();
        var min = VMConstants.MaxRegisters * 8
                + VMConstants.MaxCallDepth * Marshal.SizeOf<CallFrame>()
                + VMConstants.MaxCleanupDepth * Marshal.SizeOf<CleanupFrame>();
        Assert(size >= min, $"VMInstanceState.Size >= min (size={size}, min={min})");
    }

    /// <summary>
    /// Guards against contributors adding a field directly to VMInstanceState
    /// in VOM8/VOM9 work without routing it through CPUData or VMData.
    ///
    /// VOM8 invariant: VMInstanceState's only public fields are
    /// {Cpu : CPUData, Data : VMData}. Every legacy name (IP, Registers, …)
    /// is exposed as a ref-property forwarding into one of these two.
    /// </summary>
    private static void Test_FieldCoverage()
    {
        var fields = typeof(VMInstanceState).GetFields(BindingFlags.Public | BindingFlags.Instance);
        Assert(fields.Length == 2, $"VMInstanceState has exactly 2 public fields (got {fields.Length})");

        bool hasCpu = false, hasData = false;
        string extra = null;
        foreach (var f in fields)
        {
            if (f.Name == "Cpu" && f.FieldType == typeof(CPUData)) { hasCpu = true; continue; }
            if (f.Name == "Data" && f.FieldType == typeof(VMData)) { hasData = true; continue; }
            extra = $"{f.Name}:{f.FieldType.Name}";
        }
        Assert(hasCpu, "VMInstanceState.Cpu is CPUData");
        Assert(hasData, "VMInstanceState.Data is VMData");
        Assert(extra == null, $"VMInstanceState has no extra fields (extra: {extra ?? "<none>"})");
    }

    private static void Test_VMInstanceView_Roundtrip()
    {
        var cpu = default(CPUData);
        var data = default(VMData);
        cpu.IP = 42;
        data.InstanceId = 7;
        var view = new VMInstanceView(ref cpu, ref data);
        Assert(view.Cpu.IP == 42, "VMInstanceView.Cpu reads through");
        Assert(view.Data.InstanceId == 7, "VMInstanceView.Data reads through");

        // Mutating through the view must propagate back to the originals.
        view.Cpu.IP = 99;
        view.Data.InstanceId = 11;
        Assert(cpu.IP == 99, "VMInstanceView.Cpu writes through");
        Assert(data.InstanceId == 11, "VMInstanceView.Data writes through");
    }
}
