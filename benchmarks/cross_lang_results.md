# FFVM Cross-Language Performance Comparison

> Auto-generated: 2026-04-09 10:40
> .NET 8.0.25 | Linux 6.17.0.1008 | 4 cores
> Node.js v24.14.1 | Lua N/A (not installed) | Python 3.12.3
> 200 runs after warmup. All times in μs.

## Language Profiles

Each benchmark uses `int` for loop control and `float` for computation (where appropriate).
Languages that don't distinguish int/float degrade to their unified type.

| Language | Execution Model | Loop / Index | Computation | Notes |
|----------|----------------|-------------|-------------|-------|
| **C# raw** | .NET RyuJIT (tiered JIT) | `int` | `double` | Baseline. `int` for loop counters, `double` for float arithmetic. RyuJIT compiles to native x86-64 with SIMD, inlining, and loop unrolling. |
| **C#** | .NET RyuJIT | `int` | `Number` (double wrapper) | `int` for loops (same as raw), `Number` struct for float computation. Operator overloads add method-call overhead vs raw `double`. |
| **FFVM** | C# bytecode interpreter | `Number` (degraded) | `Number` (degraded) | Register-based VM. All values are `Number` (double) internally. `var x: int` is nominal typing only — no int/float distinction at runtime. |
| **Node.js** | V8 multi-tier JIT | Smi (int31) | HeapNumber (double) | V8 uses Smi (tagged pointer) for small ints, HeapNumber for doubles. TurboFan speculates type feedback for int32/float64 fast paths. |
| **Python 3.12** | CPython stack-based interpreter | `int` (boxed) | `float` (boxed C double) | `int` is arbitrary-precision (heap-allocated). `float` is a boxed C `double`. Both paths go through `ceval.c` dispatch. |

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

| Benchmark | C# raw | C# | FFVM | Node.js | Python |
|-----------|-------:|---:|-----:|--------:|-------:|
| B01_ArithLoop | 25.1 | 70.8 | 422.2 | 19.5 | 985.1 |
| B02_Fibonacci | 0.6 | 0.4 | 1.2 | 0.8 | 1.1 |
| B03_NestedLoop | 15.2 | 58.8 | 318.6 | 22.9 | 801.9 |
| B04_Branching | 38.8 | 30.8 | 554.9 | 23.6 | 1097.2 |
| B05_Accumulator | 52.9 | 47.4 | 811.5 | 46.9 | 2933.7 |

### Relative to C# (1.00x)

> Baseline = **C#** (uses `Number` struct for computation, `int` for loop control).
> This is the fairest comparison target: same .NET runtime, same `Number` arithmetic.
> FFVM adds interpretation overhead on top of the same `Number` cost.

| Benchmark | C# raw | C# | FFVM | Node.js | Python |
|-----------|-------:|---:|-----:|--------:|-------:|
| B01_ArithLoop | 0.35x | 1.00x | 5.96x | 0.28x | 13.91x |
| B02_Fibonacci | 1.50x | 1.00x | 3.00x | 2.00x | 2.75x |
| B03_NestedLoop | 0.26x | 1.00x | 5.42x | 0.39x | 13.64x |
| B04_Branching | 1.26x | 1.00x | 18.01x | 0.77x | 35.62x |
| B05_Accumulator | 1.12x | 1.00x | 17.12x | 0.99x | 61.89x |

### FFVM vs Python — Direct Comparison

| Benchmark | FFVM (μs) | Python (μs) | Ratio | Verdict |
|-----------|----------:|------------:|------:|---------|
| B01_ArithLoop | 422.2 | 985.1 | 0.43x | ✅ FFVM **2.3×** faster |
| B02_Fibonacci | 1.2 | 1.1 | 1.09x | ⚠️ Below timer resolution |
| B03_NestedLoop | 318.6 | 801.9 | 0.40x | ✅ FFVM **2.5×** faster |
| B04_Branching | 554.9 | 1097.2 | 0.51x | ✅ FFVM **2.0×** faster |
| B05_Accumulator | 811.5 | 2933.7 | 0.28x | ✅ FFVM **3.6×** faster |

---

## Interpretation

### Key Takeaways

- **C# raw** — Theoretical maximum. RyuJIT: native `int` loops + `double` arithmetic. Hardware-level optimization ceiling.
- **C#** — **Baseline.** Uses `Number` struct (double wrapper) for computation, same as FFVM runtime type. Isolates interpretation overhead from arithmetic cost.
- **FFVM** — Register-based bytecode interpreter. All values are `Number` (double). FORLOOP super-instruction optimizes canonical for-loops.
- **Node.js (V8)** — Multi-tier JIT. Approaches native speed. Not a realistic comparison target for an interpreter.
- **Python (CPython)** — Stack-based interpreter. Boxed types + arbitrary-precision `int`. Substantially slower than FFVM on all meaningful benchmarks.

### B02 Measurement Caveat

B02_Fibonacci (scale=46, only 46 iterations) runs in sub-microsecond time across all languages.
At this scale, **timer resolution and harness overhead dominate the measurement**:

- FFVM reports ~1.2μs, but this includes `SpawnInstance` + `Tick` + `DestroyInstance` harness overhead per iteration.
- C# raw / C# report ~0.4-0.6μs — the marginal difference is pure noise at this resolution.
- Any apparent differences in B02 are **measurement artifacts**, not real performance differences.

> **Conclusion**: B02 at scale=46 is below the reliable measurement threshold.
> Cross-language ratios for B02 should be disregarded. To fix, increase scale to ~10,000+ iterations.

### FFVM vs Lua: Gap Analysis

> Note: Lua was not available in this test environment. Previous results (Windows, Lua 5.1.4)
> showed FFVM at 1.1–1.3× Lua speed on most benchmarks. The analysis below is preserved
> from prior runs.

FFVM and Lua are the closest architectural peers: both are register-based interpreters
with a unified `double` number type and a FORLOOP super-instruction.
Remaining gap factors:

| Factor | Lua (C) | FFVM (C#) | Impact |
|--------|---------|-----------|--------|
| **Instruction encoding** | 4 bytes (packed uint32) | 16 bytes (OpCode+3×int) | FFVM 4× larger instructions → worse L1 icache utilization. A tight 10-instruction loop fits in 40B (Lua) vs 160B (FFVM). |
| **Dispatch mechanism** | C `switch` on native enum (computed goto on GCC) | C# `switch` on byte, JIT compiles to jump table | Lua with GCC uses threaded dispatch (label-as-value); FFVM uses standard switch. ~10-20% dispatch overhead difference. |
| **Value representation** | C `double` (raw 8-byte IEEE 754) | `Number` struct (readonly wrapper around `double`) | FFVM's `Number` operator overloads add method-call overhead. JIT may inline but not guaranteed for all operators. |
| **Register access** | `lua_Number` array, direct C pointer indexing | `Number*` pinned pointer + `Reg()` helper with register window offset | FFVM's `Reg(op.X, rb)` adds an `op.X + rb` addition per register access for multi-instance support. |
| **Branching overhead** | Branchless compare (C operators map to single x86 cmov/jcc) | `Number` comparison → operator overload → `double` compare | B04 shows the largest gap: heavy branching amplifies per-comparison overhead. |
| **Self-limiting design** | General-purpose language | Skill scripting VM with safety constraints | FFVM enforces register windows, cleanup chains, MaxSteps budgets, debugger hooks — these add per-instruction overhead that Lua doesn't have. |

> **Key insight**: The largest single factor is **instruction encoding size** (4x).
> FFVM uses 16-byte instructions (1-byte opcode + 3×4-byte int operands)
> vs Lua's 4-byte packed instructions. This directly impacts L1 instruction cache
> hit rate and memory bandwidth in tight loops.

### Potential Optimizations

Despite self-limiting design choices, several optimization paths remain:

| ID | Optimization | Expected Impact | Complexity | Notes |
|----|-------------|----------------|------------|-------|
| **O8** | Instruction compression 16B→4B | 10-20% (cache) | High | Pack opcode+3 operands into uint32. Biggest single win. Requires full opcode encoding redesign. |
| **O11** | Syscall `delegate*` (function pointer) | ~30% syscall path | Medium | Replace virtual dispatch with unmanaged function pointers. |
| **O12** | `Number` raw field comparison | ~10% compare | Low | Bypass operator overload, compare raw `double` fields directly in VM dispatch. |
| **FO3** | Small function inlining | Up to -80% for tiny helpers | High | Inline functions ≤N instructions at call site. Eliminates CALL/RET overhead. |
| **P—F1** | `while` loop FORLOOP recognition | ~5-10% for while-heavy code | Medium | Pattern-match `while(i<N) { ... i=i+1 }` to FORLOOP. |
| — | Eliminate `Reg()` offset in single-instance fast path | ~5% dispatch | Low | When no call stack, skip register window offset. |

> **Realistic target**: With O8 (instruction compression) + O12 (raw comparison),
> FFVM could reach **parity or better than Lua** on compute-heavy benchmarks.
> The self-limiting design overhead (register windows, cleanup chains, debugger hooks)
> is a conscious trade-off for safety and debuggability in a game scripting context.
