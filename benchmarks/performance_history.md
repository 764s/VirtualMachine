# FFVM Performance History

> 性能历史记录 — 由 CI 自动追加，记录每次提交的 Benchmark 结果变化。
> 手动运行：`bash benchmarks/update-history.sh <bench-raw.txt> [cross-lang-raw.txt]`

## Baseline (Initial)

> 初始基线来自 `benchmarks/benchmark_results.md`（.NET 6.0, Windows, 20 核）

| Benchmark | VM (μs) | C# (μs) | Ratio | Date |
|-----------|---------|---------|-------|------|
| B01_ArithLoop | 540.7 | 78.7 | 6.87x | 2026-04-01 |
| B02_Fibonacci | 0.6 | 0.1 | 6.05x | 2026-04-01 |
| B03_NestedLoop | 189.9 | 32.4 | 5.86x | 2026-04-01 |
| B04_Branching | 463.9 | 81.5 | 5.69x | 2026-04-01 |
| B05_Accumulator | 819.0 | 163.8 | 5.00x | 2026-04-01 |

## History

<!-- HISTORY_START — CI 自动追加区域，请勿手动编辑此标记 -->
<!-- HISTORY_END -->
