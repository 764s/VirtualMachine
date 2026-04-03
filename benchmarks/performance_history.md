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

### 2026-04-03 16:59 UTC — `09817e186d11a8b34d52ff1995874feff8f43d61`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 902.4 | 170.4 | 5.30x | -570.2 (↓39%) | -0.70x ✅ |
| B02_Fibonacci | 6.2 | 0.3 | 24.63x | +4.6 (↑287%) | +19.71x ⚠️ |
| B03_NestedLoop | 491.4 | 23.0 | 21.39x | -23.4 (↓5%) | +4.17x ⚠️ |
| B04_Branching | 1001.1 | 163.0 | 6.14x | -346.2 (↓26%) | +0.50x ⚠️ |
| B05_Accumulator | 1245.1 | 47.5 | 26.21x | -871.6 (↓41%) | -9.07x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-03 16:04 UTC — `72a618932e173b3a60a1a04892cccc5857c65e4a`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1121.3 | 246.7 | 4.54x | -351.3 (↓24%) | -1.46x ✅ |
| B02_Fibonacci | 1.5 | 0.3 | 4.48x | -0.1 (↓6%) | -0.44x ✅ |
| B03_NestedLoop | 501.5 | 30.7 | 16.32x | -13.3 (↓3%) | -0.90x ✅ |
| B04_Branching | 1421.8 | 249.2 | 5.71x | +74.5 (↑6%) | +0.07x |
| B05_Accumulator | 2303.6 | 61.0 | 37.74x | +186.9 (↑9%) | +2.46x ⚠️ |


### 2026-04-03 15:58 UTC — `ad198d4581114d89d10c88079982bd5a9fc21d41`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1341.9 | 346.9 | 3.87x | -130.7 (↓9%) | -2.13x ✅ |
| B02_Fibonacci | 1.4 | 0.3 | 4.14x | -0.2 (↓13%) | -0.78x ✅ |
| B03_NestedLoop | 477.6 | 30.3 | 15.76x | -37.2 (↓7%) | -1.46x ✅ |
| B04_Branching | 1383.4 | 248.9 | 5.56x | +36.1 (↑3%) | -0.08x |
| B05_Accumulator | 2292.0 | 60.5 | 37.89x | +175.3 (↑8%) | +2.61x ⚠️ |


### 2026-04-03 13:38 UTC — `a56e171a40efa43ea6fac2a096f05fff740b3eda`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1230.4 | 246.5 | 4.99x | -242.2 (↓16%) | -1.01x ✅ |
| B02_Fibonacci | 6.7 | 0.3 | 19.63x | +5.1 (↑319%) | +14.71x ⚠️ |
| B03_NestedLoop | 512.0 | 29.8 | 17.16x | -2.8 (↓1%) | -0.06x |
| B04_Branching | 1456.5 | 241.6 | 6.03x | +109.2 (↑8%) | +0.39x ⚠️ |
| B05_Accumulator | 3025.1 | 61.3 | 49.38x | +908.4 (↑43%) | +14.10x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


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
