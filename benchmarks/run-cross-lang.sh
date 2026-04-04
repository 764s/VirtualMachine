#!/usr/bin/env bash
# ============================================================
#  FFVM Cross-Language Benchmark Runner
#  Runs B01–B05 in FFVM, C# (native Number), Lua, Python, JS
#  and generates a comparison markdown report.
#
#  Usage: bash benchmarks/run-cross-lang.sh
#  Output: benchmarks/cross_lang_results.md
# ============================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
OUTPUT="$SCRIPT_DIR/cross_lang_results.md"
RAW_DIR="/tmp/ffvm_xlang"
mkdir -p "$RAW_DIR"

echo "[*] FFVM Cross-Language Benchmark Suite"
echo "    Date: $(date -u '+%Y-%m-%d %H:%M:%S UTC')"

# ── 1. Run FFVM benchmarks ──────────────────────────────────
echo "[*] Running FFVM (dotnet) benchmarks..."
CSPROJ="$ROOT_DIR/StandaloneRunner/StandaloneRunner.csproj"
if [ ! -f "$CSPROJ" ]; then
    cat > "$CSPROJ" << 'PROJ'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="**/*.cs" />
    <Compile Include="../Assets/Scripts/VM/**/*.cs" />
  </ItemGroup>
</Project>
PROJ
fi

dotnet run --project "$CSPROJ" -c Release -- --bench > "$RAW_DIR/ffvm.txt" 2>&1
echo "    Done."

# ── 2. Run Lua benchmarks ───────────────────────────────────
echo "[*] Running Lua benchmarks..."
if command -v lua5.4 &>/dev/null; then
    lua5.4 "$SCRIPT_DIR/lua/bench.lua" > "$RAW_DIR/lua.txt" 2>&1
    echo "    Done."
elif command -v lua &>/dev/null; then
    lua "$SCRIPT_DIR/lua/bench.lua" > "$RAW_DIR/lua.txt" 2>&1
    echo "    Done."
else
    echo "    SKIPPED (lua not found)"
    echo "[XLANG_START] lua" > "$RAW_DIR/lua.txt"
    echo "[XLANG_END] lua" >> "$RAW_DIR/lua.txt"
fi

# ── 3. Run Python benchmarks ────────────────────────────────
echo "[*] Running Python benchmarks..."
if command -v python3 &>/dev/null; then
    python3 "$SCRIPT_DIR/python/bench.py" > "$RAW_DIR/python.txt" 2>&1
    echo "    Done."
else
    echo "    SKIPPED (python3 not found)"
    echo "[XLANG_START] python" > "$RAW_DIR/python.txt"
    echo "[XLANG_END] python" >> "$RAW_DIR/python.txt"
fi

# ── 4. Run Node.js benchmarks ───────────────────────────────
echo "[*] Running Node.js benchmarks..."
if command -v node &>/dev/null; then
    node "$SCRIPT_DIR/js/bench.js" > "$RAW_DIR/js.txt" 2>&1
    echo "    Done."
else
    echo "    SKIPPED (node not found)"
    echo "[XLANG_START] js" > "$RAW_DIR/js.txt"
    echo "[XLANG_END] js" >> "$RAW_DIR/js.txt"
fi

# ── 5. Parse and generate report ────────────────────────────
echo "[*] Generating cross-language report..."

# Collect all FFVM benchmark data
declare -A FFVM_VM FFVM_CS FFVM_RATIO

while IFS= read -r line; do
    data="${line#*\[BENCHMARK\] }"
    name=$(echo "$data" | cut -d'|' -f1 | xargs)
    vm=$(echo "$data" | cut -d'|' -f2 | xargs)
    cs=$(echo "$data" | cut -d'|' -f3 | xargs)
    ratio=$(echo "$data" | cut -d'|' -f4 | xargs)
    FFVM_VM["$name"]="$vm"
    FFVM_CS["$name"]="$cs"
    FFVM_RATIO["$name"]="$ratio"
done < <(grep '\[BENCHMARK\] B' "$RAW_DIR/ffvm.txt" 2>/dev/null || true)

# Collect cross-language data: [XLANG] name | lang | us | scale | result | status
declare -A XLANG_LUA XLANG_PY XLANG_JS

for lang_file in lua.txt python.txt js.txt; do
    file="$RAW_DIR/$lang_file"
    [ -f "$file" ] || continue
    while IFS= read -r line; do
        data="${line#*\[XLANG\] }"
        name=$(echo "$data" | cut -d'|' -f1 | xargs)
        lang=$(echo "$data" | cut -d'|' -f2 | xargs)
        us=$(echo "$data" | cut -d'|' -f3 | xargs)
        case "$lang" in
            lua)    XLANG_LUA["$name"]="$us" ;;
            python) XLANG_PY["$name"]="$us" ;;
            js)     XLANG_JS["$name"]="$us" ;;
        esac
    done < <(grep '\[XLANG\] B' "$file" 2>/dev/null || true)
done

# Generate markdown
cat > "$OUTPUT" << HEADER
# FFVM Cross-Language Performance Comparison

> Auto-generated on $(date -u '+%Y-%m-%d %H:%M:%S UTC')
> All times in microseconds (μs). Lower is better.
> Each benchmark runs 200 iterations after 20 warmup rounds.
> All languages execute **identical algorithms** with integer arithmetic.

## Benchmark Descriptions

| ID | Name | Description | Scale |
|---|---|---|---|
| B01 | ArithLoop | ADD/MUL/SUB/MOD + conditional branch | 10,000 |
| B02 | Fibonacci | Iterative fib(N): swap loop | 25 |
| B03 | NestedLoop | N×N nested loop with multiply-accumulate | 100 |
| B04 | Branching | 4-way if/else-if chain per iteration | 10,000 |
| B05 | Accumulator | Pure ADD loop (minimal overhead) | 50,000 |

## Results

| Benchmark | FFVM (μs) | C# Number (μs) | Lua (μs) | Python (μs) | Node.js (μs) | FFVM/C# | FFVM/Lua | FFVM/Python | FFVM/JS |
|-----------|-----------|----------------|----------|-------------|--------------|---------|----------|-------------|---------|
HEADER

BENCHMARKS=("B01_ArithLoop" "B02_Fibonacci" "B03_NestedLoop" "B04_Branching" "B05_Accumulator")

for b in "${BENCHMARKS[@]}"; do
    vm="${FFVM_VM[$b]:-N/A}"
    cs="${FFVM_CS[$b]:-N/A}"
    lua="${XLANG_LUA[$b]:-N/A}"
    py="${XLANG_PY[$b]:-N/A}"
    js="${XLANG_JS[$b]:-N/A}"

    # compute ratios where possible
    ratio_cs="${FFVM_RATIO[$b]:-N/A}"

    ratio_lua="N/A"
    if [[ "$vm" != "N/A" && "$lua" != "N/A" ]]; then
        ratio_lua=$(awk "BEGIN { if ($lua > 0) printf \"%.2f\", $vm / $lua; else print \"N/A\" }")
    fi

    ratio_py="N/A"
    if [[ "$vm" != "N/A" && "$py" != "N/A" ]]; then
        ratio_py=$(awk "BEGIN { if ($py > 0) printf \"%.2f\", $vm / $py; else print \"N/A\" }")
    fi

    ratio_js="N/A"
    if [[ "$vm" != "N/A" && "$js" != "N/A" ]]; then
        ratio_js=$(awk "BEGIN { if ($js > 0) printf \"%.2f\", $vm / $js; else print \"N/A\" }")
    fi

    echo "| ${b} | ${vm} | ${cs} | ${lua} | ${py} | ${js} | ${ratio_cs}x | ${ratio_lua}x | ${ratio_py}x | ${ratio_js}x |" >> "$OUTPUT"
done

cat >> "$OUTPUT" << 'FOOTER'

## How to Read

- **FFVM/C#** — overhead of VM interpretation vs native C# (both use `Number` struct).
  Target: < 10x.
- **FFVM/Lua** — FFVM vs Lua 5.4 (standard interpreter).
  Ratio < 1.0 means FFVM is faster than Lua.
- **FFVM/Python** — FFVM vs CPython 3.12.
  FFVM should be significantly faster than CPython for numeric loops.
- **FFVM/JS** — FFVM vs Node.js V8 (JIT-compiled).
  V8 JIT will typically be faster; this shows the JIT advantage gap.

## Notes

- FFVM runs as a **bytecode interpreter** (no JIT), so it's expected to be:
  - ~5-10x slower than native C# (same data type)
  - Competitive with or faster than Lua 5.4 (both are bytecode interpreters)
  - Faster than CPython for tight numeric loops
  - Slower than V8 (which has a multi-tier JIT compiler)
- The comparison is **not** about raw language speed but about **where FFVM sits
  in the interpreter performance spectrum** for the game logic it targets.
FOOTER

echo "[*] Report saved to: $OUTPUT"
echo ""
cat "$OUTPUT"
