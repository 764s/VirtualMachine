"""
FFVM Cross-Language Benchmark — Python
Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
Each benchmark uses int for loop control and float for computation.
B02 is pure integer (classic Fibonacci).

Usage: python3 bench.py
Output: [XLANG] name | lang | us | scale | result
"""
import time


def measure(name: str, fn, scale: int, expected) -> None:
    # warmup
    for _ in range(20):
        fn(scale)

    # measure
    runs = 200
    t0 = time.perf_counter()
    result = 0
    for _ in range(runs):
        result = fn(scale)
    t1 = time.perf_counter()
    us = ((t1 - t0) / runs) * 1e6

    status = "PASS" if result == expected else f"FAIL(got={result},want={expected})"
    print(f"[XLANG] {name} | python | {us:.1f} | {scale} | {result} | {status}")


# B01: ArithLoop — int loop, float arithmetic
def b01_arith_loop(n: int) -> float:
    acc = 0.0
    for i in range(n):
        x = i + 0.5
        acc += x
        temp = x * 2.0
        temp -= 1.0
        acc += temp
    return acc


# B02: Fibonacci — pure int
def b02_fibonacci(n: int) -> int:
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a


# B03: NestedLoop — int loops, float multiply-accumulate
def b03_nested_loop(n: int) -> float:
    acc = 0.0
    i = 0
    while i < n:
        j = 0
        while j < n:
            acc += (i + 0.5) * (j + 0.5)
            j += 1
        i += 1
    return acc


# B04: Branching — int loop+branch, float accumulate
def b04_branching(n: int) -> float:
    acc = 0.0
    i = 0
    while i < n:
        x = i * 0.5
        m = i % 4
        if m == 0:
            acc += x
        elif m == 1:
            acc += x * 2.0
        elif m == 2:
            acc += x * 0.5
        else:
            acc += x * 4.0
        i += 1
    return acc


# B05: Accumulator — int loop, float sum
def b05_accumulator(n: int) -> float:
    s = 0.0
    i = 0
    while i < n:
        s += i * 0.5
        i += 1
    return s


if __name__ == "__main__":
    print("[XLANG_START] python")
    measure("B01_ArithLoop",   b01_arith_loop,  10000, b01_arith_loop(10000))
    measure("B02_Fibonacci",   b02_fibonacci,   46,    b02_fibonacci(46))
    measure("B03_NestedLoop",  b03_nested_loop, 100,   b03_nested_loop(100))
    measure("B04_Branching",   b04_branching,   10000, b04_branching(10000))
    measure("B05_Accumulator", b05_accumulator, 50000, b05_accumulator(50000))
    print("[XLANG_END] python")
