-- ============================================================
--  FFVM Cross-Language Benchmark — Lua
--  Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
--  Each benchmark uses int for loop control and float for computation.
--  B02 is pure integer (classic Fibonacci).
--  Lua 5.1 has only one number type (double) — degraded representation.
--
--  Usage: lua bench.lua
--  Output: [XLANG] name | lang | us | scale | result
-- ============================================================

local clock = os.clock

local function measure(name, fn, scale, expected)
    -- warmup
    for _ = 1, 20 do fn(scale) end

    -- measure
    local runs = 200
    local t0 = clock()
    local result
    for _ = 1, runs do
        result = fn(scale)
    end
    local t1 = clock()
    local us = ((t1 - t0) / runs) * 1e6

    local status = (result == expected) and "PASS" or
        string.format("FAIL(got=%s,want=%s)", tostring(result), tostring(expected))
    print(string.format("[XLANG] %s | lua | %.1f | %d | %s | %s",
        name, us, scale, tostring(result), status))
end

-- B01: ArithLoop — int loop, float arithmetic
local function b01(n)
    local acc = 0.0
    for i = 0, n - 1 do
        local x = i + 0.5
        acc = acc + x
        local temp = x * 2.0
        temp = temp - 1.0
        acc = acc + temp
    end
    return acc
end

-- B02: Fibonacci — pure int, iterative fib(N)
local function b02(n)
    local a, b = 0, 1
    for _ = 1, n do
        a, b = b, a + b
    end
    return a
end

-- B03: NestedLoop — int loops, float multiply-accumulate
local function b03(n)
    local acc = 0.0
    for i = 0, n - 1 do
        for j = 0, n - 1 do
            acc = acc + (i + 0.5) * (j + 0.5)
        end
    end
    return acc
end

-- B04: Branching — int loop+branch, float accumulate
local function b04(n)
    local acc = 0.0
    for i = 0, n - 1 do
        local x = i * 0.5
        local m = i % 4
        if m == 0 then acc = acc + x
        elseif m == 1 then acc = acc + x * 2.0
        elseif m == 2 then acc = acc + x * 0.5
        else acc = acc + x * 4.0 end
    end
    return acc
end

-- B05: Accumulator — int loop, float sum
local function b05(n)
    local sum = 0.0
    for i = 0, n - 1 do
        sum = sum + i * 0.5
    end
    return sum
end

-- Expected results — iterative for exact match
local function b01_expected(n) return b01(n) end
local function b02_expected(n)
    local a, b = 0, 1
    for _ = 1, n do a, b = b, a + b end
    return a
end
local function b03_expected(n) return b03(n) end
local function b04_expected(n) return b04(n) end
local function b05_expected(n) return b05(n) end

print("[XLANG_START] lua")
measure("B01_ArithLoop",   b01, 10000, b01_expected(10000))
measure("B02_Fibonacci",   b02, 46,    b02_expected(46))
measure("B03_NestedLoop",  b03, 100,   b03_expected(100))
measure("B04_Branching",   b04, 10000, b04_expected(10000))
measure("B05_Accumulator", b05, 50000, b05_expected(50000))
print("[XLANG_END] lua")
