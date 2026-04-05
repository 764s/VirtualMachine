# ============================================================
#  Generate-CrossLang.ps1
#  Parses raw benchmark output from all languages and generates
#  a cross-language comparison markdown report.
#  Each benchmark naturally mixes int (loop control) and float
#  (computation). Baseline: C# (Number struct, same semantics as FFVM).
#  Column order: C# raw | C# (baseline) | FFVM | Lua | Node.js | Python
#
#  Usage: powershell -File Generate-CrossLang.ps1 [-RawDir <dir>] [-Output <file>]
# ============================================================
param(
    [string]$RawDir = "$env:TEMP\ffvm_xlang",
    [string]$Output = (Join-Path $PSScriptRoot "cross_lang_results.md")
)

# Unicode chars (PowerShell 5.1 safe)
$MU    = [string][char]0x03BC  # μ
$TIMES = [string][char]0x00D7  # ×
$LE    = [string][char]0x2264  # ≤
$DASH  = [string][char]0x2014  # —
$RARR  = [string][char]0x2192  # →

# ── Parse FFVM benchmark output ─────────────────────────────
$ffvmFile = Join-Path $RawDir "ffvm.txt"
$vmTimes  = @{}
$csTimes  = @{}
$rawTimes = @{}
$envLine  = ""

if (Test-Path $ffvmFile) {
    foreach ($line in [System.IO.File]::ReadAllLines($ffvmFile, [System.Text.Encoding]::UTF8)) {
        if ($line -match '\[BENCHMARK_ENV\]\s*(.+)') {
            $envLine = $Matches[1].Trim()
        }
        if ($line -match '\[BENCHMARK\]\s+(B\d+_\w+)\s*\|\s*([\d.]+)\s*\|\s*([\d.]+)') {
            $vmTimes[$Matches[1]] = [double]$Matches[2]
            $csTimes[$Matches[1]] = [double]$Matches[3]
        }
        if ($line -match '\[BENCHMARK_RAW\]\s+(B\d+_\w+)\s*\|\s*([\d.]+)') {
            $rawTimes[$Matches[1]] = [double]$Matches[2]
        }
    }
}

# ── Parse [XLANG] output ────────────────────────────────────
function Parse-XLang([string]$FilePath) {
    $data = @{}
    if (Test-Path $FilePath) {
        foreach ($line in [System.IO.File]::ReadAllLines($FilePath, [System.Text.Encoding]::UTF8)) {
            if ($line -match '\[XLANG\]\s+(B\d+_\w+)\s*\|\s*\w+\s*\|\s*([\d.]+)') {
                $data[$Matches[1]] = [double]$Matches[2]
            }
        }
    }
    return $data
}

$jsTimes  = Parse-XLang (Join-Path $RawDir "js.txt")
$luaTimes = Parse-XLang (Join-Path $RawDir "lua.txt")
$pyTimes  = Parse-XLang (Join-Path $RawDir "python.txt")

# ── Environment info ────────────────────────────────────────
$runtime = "N/A"; $os = "N/A"; $cores = "N/A"
if ($envLine -match 'runtime=([\S]+)') { $runtime = $Matches[1] }
if ($envLine -match 'os=(.+?)\s+cores') { $os = $Matches[1] }
if ($envLine -match 'cores=(\d+)') { $cores = $Matches[1] }

$nodeVer = "N/A"
try { $v = & node --version 2>$null; if ($v) { $nodeVer = $v -replace '^v','' } } catch {}

$luaVer = "N/A"
try {
    $v = & lua -v 2>&1 | Out-String
    if ($v -match '(\d+\.\d+[\.\d]*)') { $luaVer = $Matches[1] }
} catch {}

$pyVer = "N/A"
try {
    $v = & python --version 2>&1 | Out-String
    if ($v -match '(\d+\.\d+[\.\d]*)') { $pyVer = $Matches[1] }
} catch {}

# ── Helpers ──────────────────────────────────────────────────
function Fmt-Time($dict, [string]$key) {
    if ($dict -and $dict.ContainsKey($key)) {
        return "{0:F1}" -f $dict[$key]
    }
    return "${DASH}"
}

function Fmt-Ratio($dict, [string]$key, $baseline) {
    if ($dict -and $dict.ContainsKey($key) -and $baseline.ContainsKey($key)) {
        $b = $baseline[$key]
        if ($b -gt 0) {
            $r = $dict[$key] / $b
            return "{0:F2}x" -f $r
        }
    }
    return "${DASH}"
}

# ── Build markdown ───────────────────────────────────────────
$benchmarks = @("B01_ArithLoop", "B02_Fibonacci", "B03_NestedLoop", "B04_Branching", "B05_Accumulator")

$sb = [System.Text.StringBuilder]::new()

[void]$sb.AppendLine("# FFVM Cross-Language Performance Comparison")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> Auto-generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("> .NET $runtime | $os | $cores cores")

$verParts = @()
if ($nodeVer -ne "N/A") { $verParts += "Node.js $nodeVer" }
if ($luaVer  -ne "N/A") { $verParts += "Lua $luaVer" }
if ($pyVer   -ne "N/A") { $verParts += "Python $pyVer" }
if ($verParts.Count -gt 0) {
    [void]$sb.AppendLine("> $($verParts -join ' | ')")
}
[void]$sb.AppendLine("> 200 runs after warmup. All times in ${MU}s.")
[void]$sb.AppendLine("")

# ── Language profiles ────────────────────────────────────────
[void]$sb.AppendLine("## Language Profiles")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Each benchmark uses ``int`` for loop control and ``float`` for computation (where appropriate).")
[void]$sb.AppendLine("Languages that don't distinguish int/float degrade to their unified type.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Language | Execution Model | Loop / Index | Computation | Notes |")
[void]$sb.AppendLine("|----------|----------------|-------------|-------------|-------|")
[void]$sb.AppendLine("| **C# raw** | .NET RyuJIT (tiered JIT) | ``int`` | ``double`` | Baseline. ``int`` for loop counters, ``double`` for float arithmetic. RyuJIT compiles to native x86-64 with SIMD, inlining, and loop unrolling. |")
[void]$sb.AppendLine("| **C#** | .NET RyuJIT | ``int`` | ``Number`` (double wrapper) | ``int`` for loops (same as raw), ``Number`` struct for float computation. Operator overloads add method-call overhead vs raw ``double``. |")
[void]$sb.AppendLine("| **FFVM** | C# bytecode interpreter | ``Number`` (degraded) | ``Number`` (degraded) | Stack-based VM. All values are ``Number`` (double) internally. ``var x: int`` is nominal typing only ${DASH} no int/float distinction at runtime. |")
[void]$sb.AppendLine("| **Node.js** | V8 multi-tier JIT | Smi (int31) | HeapNumber (double) | V8 uses Smi (tagged pointer) for small ints, HeapNumber for doubles. TurboFan speculates type feedback for int32/float64 fast paths. |")
[void]$sb.AppendLine("| **Lua $luaVer** | PUC-Rio register-based interpreter | ``number`` (degraded) | ``number`` (degraded) | **No integer type in Lua 5.1** ${DASH} all numbers are C ``double``. No int/float distinction. Lua 5.3+ added integer subtype. |")
[void]$sb.AppendLine("| **Python $pyVer** | CPython stack-based interpreter | ``int`` (boxed) | ``float`` (boxed C double) | ``int`` is arbitrary-precision (heap-allocated). ``float`` is a boxed C ``double``. Both paths go through ``ceval.c`` dispatch. |")
[void]$sb.AppendLine("")

# ── Benchmark descriptions ───────────────────────────────────
[void]$sb.AppendLine("## Benchmarks")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Each algorithm uses int for loop/index variables and float for computation where natural.")
[void]$sb.AppendLine("B02 is pure integer (classic Fibonacci). Each language uses its own supported types.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| ID | Name | Int vars | Float vars | Scale |")
[void]$sb.AppendLine("|---|---|---|---|---|")
[void]$sb.AppendLine("| B01 | ArithLoop | ``i``, ``limit`` | ``acc``, ``x``, ``temp`` (+0.5, ${TIMES}2.0, ${DASH}1.0) | 10,000 |")
[void]$sb.AppendLine("| B02 | Fibonacci | ``i``, ``a``, ``b`` (pure int) | ${DASH} | 46 |")
[void]$sb.AppendLine("| B03 | NestedLoop | ``i``, ``j`` | ``acc`` ((i+0.5)${TIMES}(j+0.5)) | 100 |")
[void]$sb.AppendLine("| B04 | Branching | ``i``, ``m``=i%4 | ``acc``, ``x``=i${TIMES}0.5 | 10,000 |")
[void]$sb.AppendLine("| B05 | Accumulator | ``i`` | ``sum`` (i${TIMES}0.5) | 50,000 |")
[void]$sb.AppendLine("")

# ══════════════════════════════════════════════════════════════
#  RESULTS
# ══════════════════════════════════════════════════════════════
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Results")
[void]$sb.AppendLine("")

# Absolute times
[void]$sb.AppendLine("### Absolute Times (${MU}s)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Benchmark | C# raw | C# | FFVM | Lua | Node.js | Python |")
[void]$sb.AppendLine("|-----------|-------:|---:|-----:|----:|--------:|-------:|")

foreach ($b in $benchmarks) {
    $raw = Fmt-Time $rawTimes $b
    $cs  = Fmt-Time $csTimes $b
    $vm  = Fmt-Time $vmTimes $b
    $lua = Fmt-Time $luaTimes $b
    $js  = Fmt-Time $jsTimes $b
    $py  = Fmt-Time $pyTimes $b
    [void]$sb.AppendLine("| $b | $raw | $cs | $vm | $lua | $js | $py |")
}

[void]$sb.AppendLine("")

# Ratios — baseline is C# (Number struct)
[void]$sb.AppendLine("### Relative to C# (1.00x)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> Baseline = **C#** (uses ``Number`` struct for computation, ``int`` for loop control).")
[void]$sb.AppendLine("> This is the fairest comparison target: same .NET runtime, same ``Number`` arithmetic.")
[void]$sb.AppendLine("> FFVM adds interpretation overhead on top of the same ``Number`` cost.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Benchmark | C# raw | C# | FFVM | Lua | Node.js | Python |")
[void]$sb.AppendLine("|-----------|-------:|---:|-----:|----:|--------:|-------:|")

foreach ($b in $benchmarks) {
    $rawR = Fmt-Ratio $rawTimes $b $csTimes
    $csR  = "1.00x"
    $vmR  = Fmt-Ratio $vmTimes $b $csTimes
    $luaR = Fmt-Ratio $luaTimes $b $csTimes
    $jsR  = Fmt-Ratio $jsTimes $b $csTimes
    $pyR  = Fmt-Ratio $pyTimes $b $csTimes
    [void]$sb.AppendLine("| $b | $rawR | $csR | $vmR | $luaR | $jsR | $pyR |")
}

[void]$sb.AppendLine("")

# ══════════════════════════════════════════════════════════════
#  INTERPRETATION
# ══════════════════════════════════════════════════════════════
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("## Interpretation")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("### Key Takeaways")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("- **C# raw** ${DASH} Theoretical maximum. RyuJIT: native ``int`` loops + ``double`` arithmetic. Hardware-level optimization ceiling.")
[void]$sb.AppendLine("- **C#** ${DASH} **Baseline.** Uses ``Number`` struct (double wrapper) for computation, same as FFVM runtime type. Isolates interpretation overhead from arithmetic cost.")
[void]$sb.AppendLine("- **FFVM** ${DASH} Register-based bytecode interpreter. All values are ``Number`` (double). FORLOOP super-instruction optimizes canonical for-loops.")
[void]$sb.AppendLine("- **Lua (PUC-Rio)** ${DASH} Register-based interpreter in C. All numbers are C ``double``. FORLOOP instruction. **Primary comparison target** ${DASH} closest architectural peer.")
[void]$sb.AppendLine("- **Node.js (V8)** ${DASH} Multi-tier JIT. Approaches native speed. Not a realistic comparison target for an interpreter.")
[void]$sb.AppendLine("- **Python (CPython)** ${DASH} Stack-based interpreter. Boxed types + arbitrary-precision ``int``. Substantially slower than both FFVM and Lua.")
[void]$sb.AppendLine("")

# ── B02 Measurement Caveat ──
[void]$sb.AppendLine("### B02 Measurement Caveat")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("B02_Fibonacci (scale=46, only 46 iterations) runs in sub-microsecond time across all languages.")
[void]$sb.AppendLine("At this scale, **timer resolution and harness overhead dominate the measurement**:")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("- FFVM reports ~0.3${MU}s, but this includes ``SpawnInstance`` + ``Tick`` + ``DestroyInstance`` harness overhead per iteration.")
[void]$sb.AppendLine("- C# raw / C# report ~0.4${MU}s ${DASH} the marginal difference is pure noise at this resolution.")
[void]$sb.AppendLine("- Lua reports 0.0${MU}s ${DASH} ``os.clock()`` resolution is too coarse for sub-${MU}s measurement.")
[void]$sb.AppendLine("- Any apparent FFVM advantage over C# in B02 is a **measurement artifact**, not a real performance win.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> **Conclusion**: B02 at scale=46 is below the reliable measurement threshold.")
[void]$sb.AppendLine("> Cross-language ratios for B02 should be disregarded. To fix, increase scale to ~10,000+ iterations.")
[void]$sb.AppendLine("")

# ── FFVM vs Lua Gap Analysis ──
[void]$sb.AppendLine("### FFVM vs Lua: Gap Analysis")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("FFVM and Lua are the closest architectural peers: both are register-based interpreters")
[void]$sb.AppendLine("with a unified ``double`` number type and a FORLOOP super-instruction.")
[void]$sb.AppendLine("Remaining gap factors:")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Factor | Lua (C) | FFVM (C#) | Impact |")
[void]$sb.AppendLine("|--------|---------|-----------|--------|")
[void]$sb.AppendLine("| **Instruction encoding** | 4 bytes (packed uint32) | 16 bytes (OpCode+3${TIMES}int) | FFVM 4${TIMES} larger instructions ${RARR} worse L1 icache utilization. A tight 10-instruction loop fits in 40B (Lua) vs 160B (FFVM). |")
[void]$sb.AppendLine("| **Dispatch mechanism** | C ``switch`` on native enum (computed goto on GCC) | C# ``switch`` on byte, JIT compiles to jump table | Lua with GCC uses threaded dispatch (label-as-value); FFVM uses standard switch. ~10-20% dispatch overhead difference. |")
[void]$sb.AppendLine("| **Value representation** | C ``double`` (raw 8-byte IEEE 754) | ``Number`` struct (readonly wrapper around ``double``) | FFVM's ``Number`` operator overloads add method-call overhead. JIT may inline but not guaranteed for all operators. |")
[void]$sb.AppendLine("| **Register access** | ``lua_Number`` array, direct C pointer indexing | ``Number*`` pinned pointer + ``Reg()`` helper with register window offset | FFVM's ``Reg(op.X, rb)`` adds an ``op.X + rb`` addition per register access for multi-instance support. |")
[void]$sb.AppendLine("| **Branching overhead** | Branchless compare (C operators map to single x86 cmov/jcc) | ``Number`` comparison ${RARR} operator overload ${RARR} ``double`` compare | B04 shows the largest gap: heavy branching amplifies per-comparison overhead. |")
[void]$sb.AppendLine("| **Self-limiting design** | General-purpose language | Skill scripting VM with safety constraints | FFVM enforces register windows, cleanup chains, MaxSteps budgets, debugger hooks ${DASH} these add per-instruction overhead that Lua doesn't have. |")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> **Key insight**: The largest single factor is **instruction encoding size** (4x).")
[void]$sb.AppendLine("> FFVM uses 16-byte instructions (1-byte opcode + 3${TIMES}4-byte int operands)")
[void]$sb.AppendLine("> vs Lua's 4-byte packed instructions. This directly impacts L1 instruction cache")
[void]$sb.AppendLine("> hit rate and memory bandwidth in tight loops.")
[void]$sb.AppendLine("")

# ── Potential Optimization Outlook ──
[void]$sb.AppendLine("### Potential Optimizations")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Despite self-limiting design choices, several optimization paths remain:")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| ID | Optimization | Expected Impact | Complexity | Notes |")
[void]$sb.AppendLine("|----|-------------|----------------|------------|-------|")
[void]$sb.AppendLine("| **O8** | Instruction compression 16B${RARR}4B | 10-20% (cache) | High | Pack opcode+3 operands into uint32. Biggest single win. Requires full opcode encoding redesign. |")
[void]$sb.AppendLine("| **O11** | Syscall ``delegate*`` (function pointer) | ~30% syscall path | Medium | Replace virtual dispatch with unmanaged function pointers. |")
[void]$sb.AppendLine("| **O12** | ``Number`` raw field comparison | ~10% compare | Low | Bypass operator overload, compare raw ``double`` fields directly in VM dispatch. |")
[void]$sb.AppendLine("| **FO3** | Small function inlining | Up to -80% for tiny helpers | High | Inline functions ${LE}N instructions at call site. Eliminates CALL/RET overhead. |")
[void]$sb.AppendLine("| **P${DASH}F1** | ``while`` loop FORLOOP recognition | ~5-10% for while-heavy code | Medium | Pattern-match ``while(i<N) { ... i=i+1 }`` to FORLOOP. |")
[void]$sb.AppendLine("| ${DASH} | Eliminate ``Reg()`` offset in single-instance fast path | ~5% dispatch | Low | When no call stack, skip register window offset. |")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> **Realistic target**: With O8 (instruction compression) + O12 (raw comparison),")
[void]$sb.AppendLine("> FFVM could reach **parity or better than Lua** on compute-heavy benchmarks.")
[void]$sb.AppendLine("> The self-limiting design overhead (register windows, cleanup chains, debugger hooks)")
[void]$sb.AppendLine("> is a conscious trade-off for safety and debuggability in a game scripting context.")

# Write file with UTF-8 (no BOM)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($Output, $sb.ToString(), $utf8NoBom)

Write-Host "[*] Report generated: $Output"
