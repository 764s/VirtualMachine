using System;
using FFVM;

public static partial class VOM2Tests
{
    private static void RunAbiTypeTests()
    {
        // ABI01: Empty Arguments
        var empty = Arguments.Empty;
        Assert(empty.Count == 0, "VOM2.ABI01_ArgumentsEmptyCountZero");

        // ABI02: Arguments over array
        var arr = new[] { Number.FromInt(11), Number.FromInt(22), Number.FromInt(33) };
        var argsA = new Arguments(arr);
        Assert(argsA.Count == 3, "VOM2.ABI02_ArgumentsArrayCount");
        Assert(argsA[0].ToInt() == 11 && argsA[1].ToInt() == 22 && argsA[2].ToInt() == 33,
            "VOM2.ABI03_ArgumentsArrayIndex");

        // ABI03: Arguments over Span (stackalloc)
        Span<Number> span = stackalloc Number[2];
        span[0] = Number.FromInt(100);
        span[1] = Number.FromInt(200);
        var argsS = new Arguments(span);
        Assert(argsS.Count == 2 && argsS[0].ToInt() == 100 && argsS[1].ToInt() == 200,
            "VOM2.ABI04_ArgumentsSpanIndex");

        // ABI04: ReturnSlot round-trip (Span)
        Span<Number> retBuf = stackalloc Number[2];
        var ret = new ReturnSlot(retBuf);
        Assert(ret.Capacity == 2, "VOM2.ABI05_ReturnSlotCapacity");
        ret.Set(0, Number.FromInt(7));
        ret.Set(1, Number.FromInt(9));
        Assert(ret.Get(0).ToInt() == 7 && ret.Get(1).ToInt() == 9,
            "VOM2.ABI06_ReturnSlotRoundTrip");
        // Span backing observed external write
        Assert(retBuf[0].ToInt() == 7 && retBuf[1].ToInt() == 9,
            "VOM2.ABI07_ReturnSlotWritesThroughSpan");

        // ABI05: ReturnSlot over array
        var retArr = new Number[1];
        var retOverArr = new ReturnSlot(retArr);
        retOverArr.Set(0, Number.FromInt(42));
        Assert(retArr[0].ToInt() == 42, "VOM2.ABI08_ReturnSlotArrayWriteThrough");

        // ABI06: null array → empty (no crash)
        var argsNull = new Arguments((Number[])null);
        Assert(argsNull.Count == 0, "VOM2.ABI09_ArgumentsNullArrayEmpty");
        var retNull = new ReturnSlot((Number[])null);
        Assert(retNull.Capacity == 0, "VOM2.ABI10_ReturnSlotNullArrayEmpty");
    }
}
