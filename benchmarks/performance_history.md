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

### 2026-04-03 12:50 UTC — `b838820f2cdf5690ab3e84919f4559117830b737`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1472.6 | 245.3 | 6.00x | +931.9 (↑172%) | -0.87x ✅ |
| B02_Fibonacci | 1.6 | 0.3 | 4.92x | +1.0 (↑167%) | -1.13x ✅ |
| B03_NestedLoop | 514.8 | 29.9 | 17.22x | +324.9 (↑171%) | +11.36x ⚠️ |
| B04_Branching | 1347.3 | 238.8 | 5.64x | +883.4 (↑190%) | -0.05x |
| B05_Accumulator | 2116.7 | 60.0 | 35.28x | +1297.7 (↑158%) | +30.28x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.

<!-- HISTORY_END -->
