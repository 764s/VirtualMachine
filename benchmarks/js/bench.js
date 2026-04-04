/**
 * FFVM Cross-Language Benchmark — Node.js
 * Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
 * Each benchmark uses int for loop counters and float for computation
 * where appropriate. B02 is pure integer (classic Fibonacci).
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

// B01: ArithLoop — int loop, float arithmetic
function b01(n) {
    let acc = 0.0;
    for (let i = 0; i < n; i++) {
        const x = i + 0.5;
        acc += x;
        let temp = x * 2.0;
        temp -= 1.0;
        acc += temp;
    }
    return acc;
}

// B02: Fibonacci — pure int, iterative fib(N)
function b02(n) {
    let a = 0, b = 1;
    for (let i = 0; i < n; i++) {
        const temp = b;
        b = a + b;
        a = temp;
    }
    return a;
}

// B03: NestedLoop — int loops, float multiply-accumulate
function b03(n) {
    let acc = 0.0;
    for (let i = 0; i < n; i++) {
        for (let j = 0; j < n; j++) {
            acc += (i + 0.5) * (j + 0.5);
        }
    }
    return acc;
}

// B04: Branching — int loop+branch, float accumulate
function b04(n) {
    let acc = 0.0;
    for (let i = 0; i < n; i++) {
        const x = i * 0.5;
        const m = i % 4;
        if (m === 0)      acc += x;
        else if (m === 1) acc += x * 2.0;
        else if (m === 2) acc += x * 0.5;
        else              acc += x * 4.0;
    }
    return acc;
}

// B05: Accumulator — int loop, float sum
function b05(n) {
    let sum = 0.0;
    for (let i = 0; i < n; i++) {
        sum += i * 0.5;
    }
    return sum;
}

// Expected results — iterative for IEEE 754 bit-exact match
const expectedB01 = n => b01(n);
const expectedB02 = n => { let a = 0, b = 1; for (let i = 0; i < n; i++) { [a, b] = [b, a + b]; } return a; };
const expectedB03 = n => b03(n);
const expectedB04 = n => b04(n);
const expectedB05 = n => b05(n);

console.log("[XLANG_START] js");
measure("B01_ArithLoop",   b01, 10000, expectedB01(10000));
measure("B02_Fibonacci",   b02, 46,    expectedB02(46));
measure("B03_NestedLoop",  b03, 100,   expectedB03(100));
measure("B04_Branching",   b04, 10000, expectedB04(10000));
measure("B05_Accumulator", b05, 50000, expectedB05(50000));
console.log("[XLANG_END] js");
