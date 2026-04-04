# ============================================================
#  Generate-CrossLang.ps1
#  Parses raw benchmark output from all languages and generates
#  a cross-language comparison markdown report.
#  Each benchmark naturally mixes int (loop control) and float
#  (computation). Baseline: C# raw (native int + double).
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
[void]$sb.AppendLine("| Benchmark | C# raw | C# | FFVM | Node.js | Lua | Python |")
[void]$sb.AppendLine("|-----------|-------:|---:|-----:|--------:|----:|-------:|")

foreach ($b in $benchmarks) {
    $raw = Fmt-Time $rawTimes $b
    $cs  = Fmt-Time $csTimes $b
    $vm  = Fmt-Time $vmTimes $b
    $js  = Fmt-Time $jsTimes $b
    $lua = Fmt-Time $luaTimes $b
    $py  = Fmt-Time $pyTimes $b
    [void]$sb.AppendLine("| $b | $raw | $cs | $vm | $js | $lua | $py |")
}

[void]$sb.AppendLine("")

# Ratios
[void]$sb.AppendLine("### Relative to C# raw (1.00x)")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("| Benchmark | C# raw | C# | FFVM | Node.js | Lua | Python |")
[void]$sb.AppendLine("|-----------|-------:|---:|-----:|--------:|----:|-------:|")

foreach ($b in $benchmarks) {
    $rawR = "1.00x"
    $csR  = Fmt-Ratio $csTimes $b $rawTimes
    $vmR  = Fmt-Ratio $vmTimes $b $rawTimes
    $jsR  = Fmt-Ratio $jsTimes $b $rawTimes
    $luaR = Fmt-Ratio $luaTimes $b $rawTimes
    $pyR  = Fmt-Ratio $pyTimes $b $rawTimes
    [void]$sb.AppendLine("| $b | $rawR | $csR | $vmR | $jsR | $luaR | $pyR |")
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
[void]$sb.AppendLine("- **C# raw** ${DASH} Theoretical maximum. RyuJIT: native ``int`` for loops, ``double`` for computation.")
[void]$sb.AppendLine("- **C#** ${DASH} Uses ``Number`` struct for float computation. B02 (pure int) matches raw; others show ``Number`` overhead.")
[void]$sb.AppendLine("- **FFVM** ${DASH} Bytecode interpreter. All values degraded to ``Number`` (double). No type specialization. Target: ${LE} 10x of C#.")
[void]$sb.AppendLine("- **Node.js (V8)** ${DASH} Multi-tier JIT. V8 uses Smi for int loop counters, HeapNumber for doubles. Approaches native speed.")
[void]$sb.AppendLine("- **Lua (PUC-Rio)** ${DASH} Register-based interpreter. All numbers are C ``double`` (degraded). Competitive with FFVM.")
[void]$sb.AppendLine("- **Python (CPython)** ${DASH} Slowest. ``int`` is boxed arbitrary-precision, ``float`` is boxed C ``double``. Both heap-allocated.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("> **FFVM** is a pure bytecode interpreter (no JIT). It competes with Lua (PUC-Rio)")
[void]$sb.AppendLine("> and substantially outperforms CPython. The gap to Node.js/C# raw reflects the")
[void]$sb.AppendLine("> fundamental cost of interpretation vs JIT compilation.")

# Write file with UTF-8 (no BOM)
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($Output, $sb.ToString(), $utf8NoBom)

Write-Host "[*] Report generated: $Output"
