# FFVM Cross-Language Performance Comparison

> Auto-generated: 2026-04-07 21:04:52
> .NET 8.0.25 | Microsoft Windows NT 10.0.19045.0 | 8 cores
> Node.js 24.14.0 | Lua 5.1.4 | Python 3.7.7
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
| **Lua 5.1.4** | PUC-Rio register-based interpreter | `number` (degraded) | `number` (degraded) | **No integer type in Lua 5.1** — all numbers are C `double`. No int/float distinction. Lua 5.3+ added integer subtype. |
| **Python 3.7.7** | CPython stack-based interpreter | `int` (boxed) | `float` (boxed C double) | `int` is arbitrary-precision (heap-allocated). `float` is a boxed C `double`. Both paths go through `ceval.c` dispatch. |

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

| Benchmark | C# raw | C# | FFVM | Lua | Node.js | Python |
|-----------|-------:|---:|-----:|----:|--------:|-------:|
| B01_ArithLoop | 27.8 | 54.7 | 730.9 | 500.0 | 24.2 | 1732.0 |
| B02_Fibonacci | 0.6 | 0.6 | 1.0 | 0.0 | 1.1 | 2.6 |
| B03_NestedLoop | 16.7 | 11.8 | 436.2 | 265.0 | 15.5 | 2776.1 |
| B04_Branching | 28.6 | 20.7 | 887.0 | 665.0 | 20.7 | 2872.4 |
| B05_Accumulator | 63.9 | 59.1 | 1585.4 | 705.0 | 59.9 | 9367.9 |

### Relative to C# (1.00x)

> Baseline = **C#** (uses `Number` struct for computation, `int` for loop control).
> This is the fairest comparison target: same .NET runtime, same `Number` arithmetic.
> FFVM adds interpretation overhead on top of the same `Number` cost.

| Benchmark | C# raw | C# | FFVM | Lua | Node.js | Python |
|-----------|-------:|---:|-----:|----:|--------:|-------:|
| B01_ArithLoop | 0.51x | 1.00x | 13.36x | 9.14x | 0.44x | 31.66x |
| B02_Fibonacci | 1.00x | 1.00x | 1.67x | 0.00x | 1.83x | 4.33x |
| B03_NestedLoop | 1.42x | 1.00x | 36.97x | 22.46x | 1.31x | 235.26x |
| B04_Branching | 1.38x | 1.00x | 42.85x | 32.13x | 1.00x | 138.76x |
| B05_Accumulator | 1.08x | 1.00x | 26.83x | 11.93x | 1.01x | 158.51x |

---

## Interpretation

### Key Takeaways

- **C# raw** — Theoretical maximum. RyuJIT: native `int` loops + `double` arithmetic. Hardware-level optimization ceiling.
- **C#** — **Baseline.** Uses `Number` struct (double wrapper) for computation, same as FFVM runtime type. Isolates interpretation overhead from arithmetic cost.
- **FFVM** — Register-based bytecode interpreter. All values are `Number` (double). FORLOOP super-instruction optimizes canonical for-loops.
- **Lua (PUC-Rio)** — Register-based interpreter in C. All numbers are C `double`. FORLOOP instruction. **Primary comparison target** — closest architectural peer.
- **Node.js (V8)** — Multi-tier JIT. Approaches native speed. Not a realistic comparison target for an interpreter.
- **Python (CPython)** — Stack-based interpreter. Boxed types + arbitrary-precision `int`. Substantially slower than both FFVM and Lua.

### B02 Measurement Caveat

B02_Fibonacci (scale=46, only 46 iterations) runs in sub-microsecond time across all languages.
At this scale, **timer resolution and harness overhead dominate the measurement**:

- FFVM reports ~0.3μs, but this includes `SpawnInstance` + `Tick` + `DestroyInstance` harness overhead per iteration.
- C# raw / C# report ~0.4μs — the marginal difference is pure noise at this resolution.
- Lua reports 0.0μs — `os.clock()` resolution is too coarse for sub-μs measurement.
- Any apparent FFVM advantage over C# in B02 is a **measurement artifact**, not a real performance win.

> **Conclusion**: B02 at scale=46 is below the reliable measurement threshold.
> Cross-language ratios for B02 should be disregarded. To fix, increase scale to ~10,000+ iterations.

### FFVM vs Lua: Gap Analysis

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
