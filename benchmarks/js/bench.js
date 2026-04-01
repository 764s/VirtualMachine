/**
 * FFVM Cross-Language Benchmark — Node.js 20
 * Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
 * Uses integer arithmetic (Number, no BigInt) for fair comparison.
 *
 * Usage: node bench.js
 * Output: [XLANG] name | lang | us | scale | result
 */
"use strict";

function measure(name, fn, scale, expected) {
    // warmup
    for (let w = 0; w < 20; w++) fn(scale);

    // measure
    const runs = 200;
    const t0 = performance.now();
    let result;
    for (let r = 0; r < runs; r++) {
        result = fn(scale);
    }
    const t1 = performance.now();
    const us = ((t1 - t0) / runs) * 1000;

    const status = result === expected
        ? "PASS"
        : `FAIL(got=${result},want=${expected})`;
    console.log(`[XLANG] ${name} | js | ${us.toFixed(1)} | ${scale} | ${result} | ${status}`);
}

// B01: ArithLoop — sum + multiply + sub + modulo + branch
function b01(n) {
    let acc = 0;
    for (let i = 0; i < n; i++) {
        acc += i;
        let temp = i * 1;
        temp -= 1;
        acc += temp;
        // branch: if (i % 3 === 0) { noop }
    }
    return acc;
}

// B02: Fibonacci — iterative fib(N)
function b02(n) {
    let a = 0, b = 1;
    for (let i = 0; i < n; i++) {
        const temp = b;
        b = a + b;
        a = temp;
    }
    return a;
}

// B03: NestedLoop — O(n^2) with inner accumulator
function b03(n) {
    let acc = 0;
    for (let i = 0; i < n; i++) {
        for (let j = 0; j < n; j++) {
            acc += i * j;
        }
    }
    return acc;
}

// B04: Branching — if/else chain every iteration
function b04(n) {
    let count = 0;
    for (let i = 0; i < n; i++) {
        const m = i % 4;
        if (m === 0)      count += 1;
        else if (m === 1) count += 2;
        else if (m === 2) count += 3;
        else              count += 4;
    }
    return count;
}

// B05: Accumulator — pure add loop
function b05(n) {
    let sum = 0;
    for (let i = 0; i < n; i++) {
        sum += i;
    }
    return sum;
}

// Expected results (identical to FFVM/C#)
const expectedB01 = n => n * (n - 1) - n;
const expectedB02 = n => { let a = 0, b = 1; for (let i = 0; i < n; i++) { [a, b] = [b, a + b]; } return a; };
const expectedB03 = n => Math.pow(n * (n - 1) / 2, 2);
const expectedB04 = n => Math.floor(n / 4) * 10;
const expectedB05 = n => n * (n - 1) / 2;

console.log("[XLANG_START] js");
measure("B01_ArithLoop",   b01, 10000, expectedB01(10000));
measure("B02_Fibonacci",   b02, 25,    expectedB02(25));
measure("B03_NestedLoop",  b03, 100,   expectedB03(100));
measure("B04_Branching",   b04, 10000, expectedB04(10000));
measure("B05_Accumulator", b05, 50000, expectedB05(50000));
console.log("[XLANG_END] js");
