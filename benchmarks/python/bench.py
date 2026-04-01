"""
FFVM Cross-Language Benchmark — Python 3.12
Implements B01–B05 with IDENTICAL logic to FFVM/C# benchmarks.
Uses integer arithmetic for fair comparison.

Usage: python3 bench.py
Output: [XLANG] name | lang | us | scale | result
"""
import time


def measure(name: str, fn, scale: int, expected: int) -> None:
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


def b01_arith_loop(n: int) -> int:
    acc = 0
    i = 0
    while i < n:
        acc += i
        temp = i * 1
        temp -= 1
        acc += temp
        # branch: if i % 3 == 0 then noop
        i += 1
    return acc


def b02_fibonacci(n: int) -> int:
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a


def b03_nested_loop(n: int) -> int:
    acc = 0
    i = 0
    while i < n:
        j = 0
        while j < n:
            acc += i * j
            j += 1
        i += 1
    return acc


def b04_branching(n: int) -> int:
    count = 0
    i = 0
    while i < n:
        m = i % 4
        if m == 0:
            count += 1
        elif m == 1:
            count += 2
        elif m == 2:
            count += 3
        else:
            count += 4
        i += 1
    return count


def b05_accumulator(n: int) -> int:
    s = 0
    i = 0
    while i < n:
        s += i
        i += 1
    return s


def expected_b01(n: int) -> int:
    return n * (n - 1) - n


def expected_b02(n: int) -> int:
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a


def expected_b03(n: int) -> int:
    return (n * (n - 1) // 2) ** 2


def expected_b04(n: int) -> int:
    return (n // 4) * 10


def expected_b05(n: int) -> int:
    return n * (n - 1) // 2


if __name__ == "__main__":
    print("[XLANG_START] python")
    measure("B01_ArithLoop",   b01_arith_loop,  10000, expected_b01(10000))
    measure("B02_Fibonacci",   b02_fibonacci,   25,    expected_b02(25))
    measure("B03_NestedLoop",  b03_nested_loop, 100,   expected_b03(100))
    measure("B04_Branching",   b04_branching,   10000, expected_b04(10000))
    measure("B05_Accumulator", b05_accumulator, 50000, expected_b05(50000))
    print("[XLANG_END] python")
