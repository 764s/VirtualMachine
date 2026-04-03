#!/usr/bin/env bash
# ============================================================
#  FFVM Performance History Updater
#  Parses benchmark raw output and appends a new entry to
#  benchmarks/performance_history.md with comparison to the
#  previous run.
#
#  Usage: update-history.sh <bench-raw.txt> [commit-sha]
#  Environment:
#    CROSS_LANG_FILE  — optional path to cross-lang raw output
# ============================================================
set -euo pipefail

RAW="${1:?Usage: update-history.sh <bench-raw.txt> [commit-sha]}"
COMMIT="${2:-$(git rev-parse --short HEAD 2>/dev/null || echo 'unknown')}"
CROSS_LANG_FILE="${CROSS_LANG_FILE:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HISTORY="$SCRIPT_DIR/performance_history.md"

# Ratio increase threshold for regression detection (absolute delta)
REGRESSION_THRESHOLD=0.1

DATE=$(date -u '+%Y-%m-%d %H:%M UTC')

# ── Extract environment info ────────────────────────────────
ENV_LINE=$(grep '\[BENCHMARK_ENV\]' "$RAW" || echo "")
RUNTIME=$(echo "$ENV_LINE" | grep -oP 'runtime=\K[^ ]+' || echo "unknown")
OS_NAME=$(echo "$ENV_LINE" | grep -oP 'os=\K\S+' || echo "unknown")
CORES=$(echo "$ENV_LINE" | grep -oP 'cores=\K[^ ]+' || echo "?")

# ── Parse current benchmark results ────────────────────────
declare -A CUR_VM CUR_CS CUR_RATIO
BENCHMARKS=()

while IFS= read -r line; do
    data="${line#*\[BENCHMARK\] }"
    if echo "$data" | grep -qE 'COMPILE_ERROR|MISMATCH'; then
        continue
    fi
    name=$(echo "$data" | cut -d'|' -f1 | xargs)
    vm=$(echo "$data" | cut -d'|' -f2 | xargs)
    cs=$(echo "$data" | cut -d'|' -f3 | xargs)
    ratio=$(echo "$data" | cut -d'|' -f4 | xargs)
    CUR_VM["$name"]="$vm"
    CUR_CS["$name"]="$cs"
    CUR_RATIO["$name"]="$ratio"
    BENCHMARKS+=("$name")
done < <(grep '\[BENCHMARK\] B' "$RAW")

if [ ${#BENCHMARKS[@]} -eq 0 ]; then
    echo "[!] No benchmark data found in $RAW"
    exit 1
fi

# ── Parse previous entry from history for delta calculation ─
declare -A PREV_VM PREV_RATIO
if [ -f "$HISTORY" ]; then
    # Find the last table entry block before HISTORY_END
    # Look for lines like "| B01_ArithLoop | 540.7 | 78.7 | 6.87x |"
    LAST_BLOCK=""
    IN_BLOCK=false
    while IFS= read -r hline; do
        if echo "$hline" | grep -qP '^\| B\d'; then
            IN_BLOCK=true
            LAST_BLOCK+="$hline"$'\n'
        elif $IN_BLOCK; then
            IN_BLOCK=false
        fi
    done < "$HISTORY"

    if [ -n "$LAST_BLOCK" ]; then
        while IFS= read -r pline; do
            [ -z "$pline" ] && continue
            pname=$(echo "$pline" | cut -d'|' -f2 | xargs)
            pvm=$(echo "$pline" | cut -d'|' -f3 | xargs)
            pratio=$(echo "$pline" | cut -d'|' -f5 | xargs | tr -d 'x')
            PREV_VM["$pname"]="$pvm"
            PREV_RATIO["$pname"]="$pratio"
        done <<< "$LAST_BLOCK"
    fi
fi

# ── Build the new history entry ─────────────────────────────
ENTRY=""
ENTRY+="### ${DATE} — \`${COMMIT}\`"$'\n'
ENTRY+=""$'\n'
ENTRY+="> .NET ${RUNTIME} | ${OS_NAME} | ${CORES} cores"$'\n'
ENTRY+=""$'\n'
ENTRY+="| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |"$'\n'
ENTRY+="|-----------|---------|---------|-------|------|---------|"$'\n'

ANY_REGRESSION=false

for b in "${BENCHMARKS[@]}"; do
    vm="${CUR_VM[$b]}"
    cs="${CUR_CS[$b]}"
    ratio="${CUR_RATIO[$b]}"

    # Calculate deltas
    delta_vm="—"
    delta_ratio="—"
    if [ -n "${PREV_VM[$b]:-}" ]; then
        delta_vm=$(awk "BEGIN {
            d = $vm - ${PREV_VM[$b]};
            pct = (${PREV_VM[$b]} > 0) ? (d / ${PREV_VM[$b]}) * 100 : 0;
            if (d > 0) printf \"+%.1f (↑%.0f%%)\", d, pct;
            else if (d < 0) printf \"%.1f (↓%.0f%%)\", d, -pct;
            else printf \"0 (=)\";
        }")
    fi
    if [ -n "${PREV_RATIO[$b]:-}" ]; then
        delta_ratio=$(awk -v thresh="$REGRESSION_THRESHOLD" "BEGIN {
            d = $ratio - ${PREV_RATIO[$b]};
            if (d > thresh) printf \"+%.2fx ⚠️\", d;
            else if (d > 0) printf \"+%.2fx\", d;
            else if (d < -thresh) printf \"%.2fx ✅\", d;
            else if (d < 0) printf \"%.2fx\", d;
            else printf \"= \";
        }")
        # Check for regression (ratio increased by > 10%)
        is_regress=$(awk "BEGIN { print ($ratio > ${PREV_RATIO[$b]} * 1.1) ? 1 : 0 }")
        if [ "$is_regress" = "1" ]; then
            ANY_REGRESSION=true
        fi
    fi

    ENTRY+="| ${b} | ${vm} | ${cs} | ${ratio}x | ${delta_vm} | ${delta_ratio} |"$'\n'
done

# ── Add cross-language summary if available ─────────────────
if [ -n "$CROSS_LANG_FILE" ] && [ -f "$CROSS_LANG_FILE" ]; then
    declare -A XL_LUA XL_PY XL_JS
    while IFS= read -r xline; do
        data="${xline#*\[XLANG\] }"
        xname=$(echo "$data" | cut -d'|' -f1 | xargs)
        xlang=$(echo "$data" | cut -d'|' -f2 | xargs)
        xus=$(echo "$data" | cut -d'|' -f3 | xargs)
        case "$xlang" in
            lua)    XL_LUA["$xname"]="$xus" ;;
            python) XL_PY["$xname"]="$xus" ;;
            js)     XL_JS["$xname"]="$xus" ;;
        esac
    done < <(grep '\[XLANG\] B' "$CROSS_LANG_FILE" 2>/dev/null || true)

    if [ ${#XL_LUA[@]} -gt 0 ] || [ ${#XL_PY[@]} -gt 0 ] || [ ${#XL_JS[@]} -gt 0 ]; then
        ENTRY+=""$'\n'
        ENTRY+="<details><summary>Cross-language (Lua / Python / Node.js)</summary>"$'\n'
        ENTRY+=""$'\n'
        ENTRY+="| Benchmark | FFVM (μs) | Lua (μs) | Python (μs) | JS (μs) |"$'\n'
        ENTRY+="|-----------|-----------|----------|-------------|---------|"$'\n'
        for b in "${BENCHMARKS[@]}"; do
            vm="${CUR_VM[$b]}"
            lua="${XL_LUA[$b]:-N/A}"
            py="${XL_PY[$b]:-N/A}"
            js="${XL_JS[$b]:-N/A}"
            ENTRY+="| ${b} | ${vm} | ${lua} | ${py} | ${js} |"$'\n'
        done
        ENTRY+=""$'\n'
        ENTRY+="</details>"$'\n'
    fi
fi

ENTRY+=""$'\n'

# ── Regression summary ──────────────────────────────────────
if $ANY_REGRESSION; then
    ENTRY+="⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks."$'\n'
    ENTRY+=""$'\n'
fi

# ── Insert into history file ────────────────────────────────
if [ ! -f "$HISTORY" ]; then
    echo "[!] History file not found: $HISTORY"
    exit 1
fi

# Insert the new entry between HISTORY_START and HISTORY_END markers
# New entries go at the top (newest first)
TMPFILE=$(mktemp)
awk -v entry="$ENTRY" '
    /<!-- HISTORY_START/ {
        print
        printf "\n%s", entry
        next
    }
    { print }
' "$HISTORY" > "$TMPFILE"
mv "$TMPFILE" "$HISTORY"

echo "[*] History updated: ${#BENCHMARKS[@]} benchmarks recorded"
echo "    Commit: ${COMMIT}"
echo "    Date: ${DATE}"
if $ANY_REGRESSION; then
    echo "    ⚠️  REGRESSION DETECTED"
fi
