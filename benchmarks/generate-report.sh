#!/usr/bin/env bash
# ============================================================
#  FFVM Benchmark Report Generator (CI-compatible)
#  Usage: generate-report.sh <raw-output.txt> [output.md]
# ============================================================
set -euo pipefail

RAW="${1:?Usage: generate-report.sh <raw-output.txt> [output.md]}"
OUTPUT="${2:-benchmarks/benchmark_ci.md}"

mkdir -p "$(dirname "$OUTPUT")"

# extract env line
ENV_LINE=$(grep '\[BENCHMARK_ENV\]' "$RAW" || echo "")
RUNTIME=$(echo "$ENV_LINE" | grep -oP 'runtime=\K[^ ]+' || echo "unknown")
OS_NAME=$(echo "$ENV_LINE" | grep -oP 'os=\K[^ ]+' || echo "unknown")
CORES=$(echo "$ENV_LINE" | grep -oP 'cores=\K[^ ]+' || echo "?")

cat > "$OUTPUT" << HEADER
# FFVM Benchmark Results (CI)

> Auto-generated on $(date -u '+%Y-%m-%d %H:%M:%S UTC')
> Runtime: .NET ${RUNTIME} | OS: ${OS_NAME} | CPU cores: ${CORES}
> Warmup: 20 runs, Measure: 200 runs, Release build

## Results

| Benchmark | VM (μs) | C# (μs) | Ratio | Scale | Instrs | Status |
|-----------|---------|---------|-------|-------|--------|--------|
HEADER

PASS=0
FAIL=0

while IFS= read -r line; do
    # parse: [BENCHMARK] name | vm_us | cs_us | ratio | scale | instrs
    data="${line#*\[BENCHMARK\] }"

    if echo "$data" | grep -qE 'COMPILE_ERROR|MISMATCH'; then
        name=$(echo "$data" | cut -d'|' -f1 | xargs)
        echo "| ${name} | - | - | - | - | - | FAIL |" >> "$OUTPUT"
        FAIL=$((FAIL + 1))
    else
        name=$(echo "$data" | cut -d'|' -f1 | xargs)
        vm=$(echo "$data" | cut -d'|' -f2 | xargs)
        cs=$(echo "$data" | cut -d'|' -f3 | xargs)
        ratio=$(echo "$data" | cut -d'|' -f4 | xargs)
        scale=$(echo "$data" | cut -d'|' -f5 | xargs)
        instrs=$(echo "$data" | cut -d'|' -f6 | xargs)
        echo "| ${name} | ${vm} | ${cs} | ${ratio}x | ${scale} | ${instrs} | PASS |" >> "$OUTPUT"
        PASS=$((PASS + 1))
    fi
done < <(grep '\[BENCHMARK\] B' "$RAW")

cat >> "$OUTPUT" << FOOTER

## Summary

- **${PASS}** benchmarks passed, **${FAIL}** failed
- All values in microseconds (μs). Lower is better.
- Ratio = VM / C#. Closer to 1.0x is better.
- Both sides use \`Number\` struct for fair data-type comparison.
FOOTER

echo "[*] Report: ${PASS} passed, ${FAIL} failed → ${OUTPUT}"
