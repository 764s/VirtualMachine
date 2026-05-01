using FFVM;
using UnityEngine;

/// <summary>
/// VOM1 validation: MethodHandle resolve / cache / version invalidation,
/// plus CPUDataView / VMDataView field grouping.
/// </summary>
public static class VOM1Tests
{
    public static void RunAll()
    {
        int passed = 0;
        int failed = 0;
        TestHarness.BeginSuite("VOM1Tests");

        void Assert(bool cond, string name)
        {
            if (cond) passed++; else failed++;
            TestHarness.Assert(cond, name);
        }

        // ----- MethodHandle.Invalid -----
        var invalid = MethodHandle.Invalid;
        Assert(!invalid.IsResolved, "VOM1.MH01_InvalidIsNotResolved");
        Assert(invalid.FunctionIndex == -1, "VOM1.MH02_InvalidIndexIsMinusOne");

        // ----- Build a minimal program with two functions -----
        var fns = new[]
        {
            new FunctionEntry("foo", entryIP: 10, paramCount: 2, localRegCount: 4, isLeaf: true),
            new FunctionEntry("bar", entryIP: 30, paramCount: 0, localRegCount: 1, isLeaf: false),
        };
        var program = new VMProgram(
            instructions: new Instruction[] { new Instruction(OpCode.NOP) },
            constants: new Number[0],
            requiredRegisters: 16,
            functions: fns);

        Assert(program.Version == 1, "VOM1.MH03_InitialVersionIsOne");

        // ----- Resolve hit -----
        var hFoo = program.ResolveMethod("foo");
        Assert(hFoo.IsResolved, "VOM1.MH04_ResolveFooIsResolved");
        Assert(hFoo.FunctionIndex == 0, "VOM1.MH05_ResolveFooIndexZero");
        Assert(hFoo.EntryIP == 10, "VOM1.MH06_ResolveFooEntryIP10");
        Assert(hFoo.ParamCount == 2, "VOM1.MH07_ResolveFooParamCount2");
        Assert(hFoo.ReturnCount == 1, "VOM1.MH08_ResolveFooReturnCount1");
        Assert(hFoo.Version == 1, "VOM1.MH09_ResolveFooVersionMatches");
        Assert(hFoo.IsValid(program), "VOM1.MH10_ResolveFooIsValid");

        var hBar = program.ResolveMethod("bar");
        Assert(hBar.IsResolved && hBar.FunctionIndex == 1 && hBar.EntryIP == 30,
            "VOM1.MH11_ResolveBarBasic");

        // ----- Resolve miss -----
        var hMiss = program.ResolveMethod("nope");
        Assert(!hMiss.IsResolved, "VOM1.MH12_ResolveMissIsUnresolved");
        Assert(!hMiss.IsValid(program), "VOM1.MH13_ResolveMissIsNotValid");

        var hNull = program.ResolveMethod(null);
        Assert(!hNull.IsResolved, "VOM1.MH14_ResolveNullIsUnresolved");

        var hEmpty = program.ResolveMethod("");
        Assert(!hEmpty.IsResolved, "VOM1.MH15_ResolveEmptyIsUnresolved");

        // ----- Resolve cache: second call returns equivalent handle -----
        var hFoo2 = program.ResolveMethod("foo");
        Assert(hFoo2.FunctionIndex == hFoo.FunctionIndex
            && hFoo2.Version == hFoo.Version
            && hFoo2.EntryIP == hFoo.EntryIP,
            "VOM1.MH16_ResolveCacheStable");

        // ----- Invalidate -----
        int versionBefore = program.Version;
        program.Invalidate();
        Assert(program.Version == versionBefore + 1, "VOM1.MH17_InvalidateBumpsVersion");
        Assert(!hFoo.IsValid(program), "VOM1.MH18_OldHandleNotValidAfterInvalidate");

        // ----- Re-resolve after invalidation -----
        var hFoo3 = program.ResolveMethod("foo");
        Assert(hFoo3.IsValid(program), "VOM1.MH19_NewHandleValidAfterInvalidate");
        Assert(hFoo3.Version == program.Version, "VOM1.MH20_NewHandleVersionMatches");
        Assert(hFoo3.FunctionIndex == 0, "VOM1.MH21_NewHandleStillIndexZero");

        // ----- IsValid against null program -----
        Assert(!hFoo3.IsValid(null), "VOM1.MH22_IsValidNullProgramFalse");

        // [VOM9 Phase 4] CPUDataView / VMDataView field-grouping tests removed.
        // Legacy partial view types in VMInstanceStateViews.cs were superseded by
        // VOM7's CPUData/VMData direct fields on VMInstanceState (Cpu/Data) and
        // VOM7's VMInstanceView ref struct. The view types and their tests are
        // dead weight; deleting both as part of VOM9 ABI cleanup.

        // ----- Summary -----
        Debug.Log($"[VOM1Tests] {passed} passed, {failed} failed");
        TestHarness.EndSuite();
        if (failed > 0)
        {
            throw new System.Exception($"VOM1Tests failed: {failed} assertion(s)");
        }
    }
}
