# FFVM Cross-Language Performance Comparison

> Auto-generated: 2026-04-05 05:36:37
> .NET 10.0.5 | Microsoft Windows NT 10.0.26200.0 | 20 cores
> Node.js 24.14.1 | Lua 5.1.5 | Python 3.13.2
> 200 runs after warmup. All times in μs.

## Language Profiles

Each benchmark uses `int` for loop control and `float` for computation (where appropriate).
Languages that don't distinguish int/float degrade to their unified type.

| Language | Execution Model | Loop / Index | Computation | Notes |
|----------|----------------|-------------|-------------|-------|
| **C# raw** | .NET RyuJIT (tiered JIT) | `int` | `double` | Baseline. `int` for loop counters, `double` for float arithmetic. RyuJIT compiles to native x86-64 with SIMD, inlining, and loop unrolling. |
| **C#** | .NET RyuJIT | `int` | `Number` (double wrapper) | `int` for loops (same as raw), `Number` struct for float computation. Operator overloads add method-call overhead vs raw `double`. |
| **FFVM** | C# bytecode interpreter | `Number` (degraded) | `Number` (degraded) | Stack-based VM. All values are `Number` (double) internally. `var x: int` is nominal typing only — no int/float distinction at runtime. |
| **Node.js** | V8 multi-tier JIT | Smi (int31) | HeapNumber (double) | V8 uses Smi (tagged pointer) for small ints, HeapNumber for doubles. TurboFan speculates type feedback for int32/float64 fast paths. |
| **Lua 5.1.5** | PUC-Rio register-based interpreter | `number` (degraded) | `number` (degraded) | **No integer type in Lua 5.1** — all numbers are C `double`. No int/float distinction. Lua 5.3+ added integer subtype. |
| **Python 3.13.2** | CPython stack-based interpreter | `int` (boxed) | `float` (boxed C double) | `int` is arbitrary-precision (heap-allocated). `float` is a boxed C `double`. Both paths go through `ceval.c` dispatch. |

## Benchmarks

Each algorithm uses int for loop/index variables and float for computation where natural.
B02 is pure integer (classic Fibonacci). Each language uses its own supported types.

| ID | Name | Int vars | Float vars | Scale |
|---|---|---|---|---|
| B01 | ArithLoop | `i`, `limit` | `acc`, `x`, `temp` (+0.5, ×2.0, —1.0) | 10,000 |
| B02 | Fibonacci | `i`, `a`, `b` (pure int) | — | 46 |
| B03 | NestedLoop | `i`, `j` | `acc` ((i+0.5)×(j+0.5)) | 100 |
| B04 | Branching | `i`, `m`=i%4 | `acc`, `x`=i×0.5 | 10,000 |
| B05 | Accumulator | `i` | `sum` (i×0.5) | 50,000 |

---

## Results

### Absolute Times (μs)

| Benchmark | C# raw | C# | FFVM | Node.js | Lua | Python |
|-----------|-------:|---:|-----:|--------:|----:|-------:|
| B01_ArithLoop | 11.3 | 22.9 | 181.8 | 8.0 | 115.0 | 500.7 |
| B02_Fibonacci | 0.4 | 0.4 | 0.7 | 0.4 | 0.0 | 0.7 |
| B03_NestedLoop | 8.3 | 19.9 | 175.7 | 8.4 | 90.0 | 362.0 |
| B04_Branching | 11.3 | 18.2 | 371.3 | 9.6 | 205.0 | 681.2 |
| B05_Accumulator | 22.7 | 20.5 | 666.3 | 19.7 | 245.0 | 1377.4 |

### Relative to C# raw (1.00x)

| Benchmark | C# raw | C# | FFVM | Node.js | Lua | Python |
|-----------|-------:|---:|-----:|--------:|----:|-------:|
| B01_ArithLoop | 1.00x | 2.03x | 16.09x | 0.71x | 10.18x | 44.31x |
| B02_Fibonacci | 1.00x | 1.00x | 1.75x | 1.00x | 0.00x | 1.75x |
| B03_NestedLoop | 1.00x | 2.40x | 21.17x | 1.01x | 10.84x | 43.61x |
| B04_Branching | 1.00x | 1.61x | 32.86x | 0.85x | 18.14x | 60.28x |
| B05_Accumulator | 1.00x | 0.90x | 29.35x | 0.87x | 10.79x | 60.68x |

---

## Interpretation

### Key Takeaways

- **C# raw** — Theoretical maximum. RyuJIT: native `int` for loops, `double` for computation.
- **C#** — Uses `Number` struct for float computation. B02 (pure int) matches raw; others show `Number` overhead.
- **FFVM** — Bytecode interpreter. All values degraded to `Number` (double). No type specialization. Target: ≤ 10x of C#.
- **Node.js (V8)** — Multi-tier JIT. V8 uses Smi for int loop counters, HeapNumber for doubles. Approaches native speed.
- **Lua (PUC-Rio)** — Register-based interpreter. All numbers are C `double` (degraded). Competitive with FFVM.
- **Python (CPython)** — Slowest. `int` is boxed arbitrary-precision, `float` is boxed C `double`. Both heap-allocated.

> **FFVM** is a pure bytecode interpreter (no JIT). It competes with Lua (PUC-Rio)
> and substantially outperforms CPython. The gap to Node.js/C# raw reflects the
> fundamental cost of interpretation vs JIT compilation.
