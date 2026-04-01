-- ============================================================
--  FFVM Cross-Language Benchmark — Lua 5.4
--  Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
--  Uses integer arithmetic (Lua 5.3+ has native integers).
--
--  Usage: lua5.4 bench.lua
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

-- B01: ArithLoop — sum + multiply + sub + modulo + branch
local function b01(n)
    local acc = 0
    for i = 0, n - 1 do
        acc = acc + i
        local temp = i * 1
        temp = temp - 1
        acc = acc + temp
        -- branch: if i % 3 == 0 then noop
    end
    return acc
end

-- B02: Fibonacci — iterative fib(N)
local function b02(n)
    local a, b = 0, 1
    for _ = 1, n do
        a, b = b, a + b
    end
    return a
end

-- B03: NestedLoop — O(n^2) with inner accumulator
local function b03(n)
    local acc = 0
    for i = 0, n - 1 do
        for j = 0, n - 1 do
            acc = acc + i * j
        end
    end
    return acc
end

-- B04: Branching — if/else chain every iteration
local function b04(n)
    local count = 0
    for i = 0, n - 1 do
        local m = i % 4
        if m == 0 then count = count + 1
        elseif m == 1 then count = count + 2
        elseif m == 2 then count = count + 3
        else count = count + 4 end
    end
    return count
end

-- B05: Accumulator — pure add loop
local function b05(n)
    local sum = 0
    for i = 0, n - 1 do
        sum = sum + i
    end
    return sum
end

-- Compute expected results (must match FFVM/C# exactly)
-- B01: acc = sum(0..n-1) + sum((0..n-1)*1 - 1) = sum(i) + sum(i-1)
--      = n*(n-1)/2 + n*(n-1)/2 - n = n*(n-1) - n
local function b01_expected(n) return n*(n-1) - n end
-- B02: fib(25) = 75025
local function b02_expected(n)
    local a, b = 0, 1
    for _ = 1, n do a, b = b, a + b end
    return a
end
-- B03: sum_{i=0}^{n-1} sum_{j=0}^{n-1} i*j = (n*(n-1)/2)^2
local function b03_expected(n) return (n*(n-1)//2)^2 end
-- B04: (n/4)*10
local function b04_expected(n) return (n//4)*10 end
-- B05: n*(n-1)/2
local function b05_expected(n) return n*(n-1)//2 end

print("[XLANG_START] lua")
measure("B01_ArithLoop",   b01, 10000, b01_expected(10000))
measure("B02_Fibonacci",   b02, 25,    b02_expected(25))
measure("B03_NestedLoop",  b03, 100,   b03_expected(100))
measure("B04_Branching",   b04, 10000, b04_expected(10000))
measure("B05_Accumulator", b05, 50000, b05_expected(50000))
print("[XLANG_END] lua")
