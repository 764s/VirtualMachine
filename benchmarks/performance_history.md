# FFVM Performance History

> 性能历史记录 — 由 CI 自动追加，记录每次提交的 Benchmark 结果变化。
> 手动运行：`bash benchmarks/update-history.sh <bench-raw.txt> [cross-lang-raw.txt]`

## ⚠️ 跨环境对比说明

> **初始基线**捕获于 **Windows / .NET 6.0 / 20 核**（开发机），CI 运行于 **Unix / .NET 8.0 / 2 核**（GitHub Actions runner）。
> 由于 CPU 核数、JIT 版本、OS 调度策略差异，**跨环境 Δ 值不可直接比较**。
> 自 BM1 (B-γ3) 起，`update-history.sh` 使用环境指纹检测：环境变化时 Δ 列标记 `(env changed)` 并重置为新环境基线。
> 仅同环境内的趋势比较具有可信度。

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

### 2026-04-06 00:05 UTC — `136970bcba65b207d348c357cea821951f3781bc`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 265.6 | 71.5 | 3.71x | — (env changed) | — (env changed) |
| B02_Fibonacci | 0.9 | 0.5 | 1.89x | — (env changed) | — (env changed) |
| B03_NestedLoop | 222.6 | 58.9 | 3.78x | — (env changed) | — (env changed) |
| B04_Branching | 490.0 | 13.1 | 37.38x | — (env changed) | — (env changed) |
| B05_Accumulator | 692.7 | 46.9 | 14.77x | — (env changed) | — (env changed) |
| B06_FuncCall | 249.9 | 15.3 | 16.36x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `10.0.5|Microsoft|20` → `8.0.25|Unix|4`. Deltas reset.


### 2026-04-05 12:38 UTC — `8e674a8037be0236f246e648d2d84789721c6209`

> .NET 10.0.5 | Microsoft | 20 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 108.5 | 21.4 | 5.07x | — (env changed) | — (env changed) |
| B02_Fibonacci | 0.4 | 0.4 | 0.98x | — (env changed) | — (env changed) |
| B03_NestedLoop | 92.1 | 20.7 | 4.45x | — (env changed) | — (env changed) |
| B04_Branching | 255.5 | 18.9 | 13.52x | — (env changed) | — (env changed) |
| B05_Accumulator | 284.2 | 27.6 | 10.29x | — (env changed) | — (env changed) |
| B06_FuncCall | 97.1 | 6.2 | 15.73x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `8.0.25|Microsoft|8` to `10.0.5|Microsoft|20`. Deltas reset.


### 2026-04-05 09:45 UTC — `a76c56ea7f098cca9488486868ddf42cbda25542`

> .NET 8.0.25 | Microsoft | 8 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1006.5 | 91.8 | 10.96x | -518.8 (↓34%) | +1.34x ⚠️ |
| B02_Fibonacci | 4.2 | 1.1 | 3.76x | +0.2 (↑5%) | +0.91x ⚠️ |
| B03_NestedLoop | 1003.2 | 59.8 | 16.77x | +56.5 (↑6%) | -39.63x ✅ |
| B04_Branching | 3004 | 104.7 | 28.68x | +1261.6 (↑72%) | +10.66x ⚠️ |
| B05_Accumulator | 3100.2 | 147.7 | 20.99x | +386.3 (↑14%) | -4.07x ✅ |
| B06_FuncCall | 857.7 | 38.4 | 22.31x | -190.0 (↓18%) | +1.48x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-05 09:34 UTC — `a76c56ea7f098cca9488486868ddf42cbda25542`

> .NET 8.0.25 | Microsoft | 8 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1525.3 | 158.6 | 9.62x | -312.5 (↓17%) | -4.08x ✅ |
| B02_Fibonacci | 4 | 1.4 | 2.85x | -0.1 (↓2%) | -2.22x ✅ |
| B03_NestedLoop | 946.7 | 16.8 | 56.4x | -251.8 (↓21%) | +1.90x ⚠️ |
| B04_Branching | 1742.4 | 96.7 | 18.02x | -848.0 (↓33%) | -9.25x ✅ |
| B05_Accumulator | 2713.9 | 108.3 | 25.06x | -1730.2 (↓39%) | -13.17x ✅ |
| B06_FuncCall | 1047.7 | 50.3 | 20.83x | +148.9 (↑17%) | +6.96x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-05 09:22 UTC — `a76c56ea7f098cca9488486868ddf42cbda25542`

> .NET 8.0.25 | Microsoft | 8 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1837.8 | 134.1 | 13.7x | — (env changed) | — (env changed) |
| B02_Fibonacci | 4.1 | 0.8 | 5.07x | — (env changed) | — (env changed) |
| B03_NestedLoop | 1198.5 | 22 | 54.5x | — (env changed) | — (env changed) |
| B04_Branching | 2590.4 | 95 | 27.27x | — (env changed) | — (env changed) |
| B05_Accumulator | 4444.1 | 116.2 | 38.23x | — (env changed) | — (env changed) |
| B06_FuncCall | 898.8 | 64.8 | 13.87x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `10.0.5|Microsoft|20` to `8.0.25|Microsoft|8`. Deltas reset.


### 2026-04-05 07:02 UTC — `3ed5d2ebc7059619216bc6953b5821ec2fa89c65`

> .NET 10.0.5 | Microsoft | 20 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 131 | 21.8 | 6.02x | -164.8 (↓56%) | +0.89x ⚠️ |
| B02_Fibonacci | 0.3 | 0.4 | 0.79x | -3.3 (↓92%) | -1.15x ✅ |
| B03_NestedLoop | 107.9 | 19.3 | 5.58x | -11.9 (↓10%) | -3.41x ✅ |
| B04_Branching | 269.6 | 17.4 | 15.53x | -42.1 (↓14%) | +10.50x ⚠️ |
| B05_Accumulator | 304.3 | 28 | 10.87x | -160.3 (↓35%) | -7.29x ✅ |
| B06_FuncCall | 80.2 | 6.1 | 13.08x | -46.9 (↓37%) | +2.36x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 15:55 UTC — `ddb8bb0dc2348c3c42a9939d21b28c2dc49ebadf`

> .NET 10.0.5 | Microsoft | 20 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 295.8 | 57.7 | 5.13x | — (env changed) | — (env changed) |
| B02_Fibonacci | 3.6 | 1.8 | 1.94x | — (env changed) | — (env changed) |
| B03_NestedLoop | 119.8 | 13.3 | 8.99x | — (env changed) | — (env changed) |
| B04_Branching | 311.7 | 61.9 | 5.03x | — (env changed) | — (env changed) |
| B05_Accumulator | 464.6 | 25.6 | 18.16x | — (env changed) | — (env changed) |
| B06_FuncCall | 127.1 | 11.9 | 10.72x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `8.0.25|Unix|2` to `10.0.5|Microsoft|20`. Deltas reset.


### 2026-04-04 07:30 UTC — `3e659ffe8ab2d49fef22996847509dcb6a5ce49d`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1252.3 | 268.2 | 4.67x | -220.3 (↓15%) | -1.33x ✅ |
| B02_Fibonacci | 14.5 | 7.1 | 2.05x | +12.9 (↑806%) | -2.87x ✅ |
| B03_NestedLoop | 585.0 | 12.8 | 45.77x | +70.2 (↑14%) | +28.55x ⚠️ |
| B04_Branching | 1422.7 | 245.3 | 5.80x | +75.4 (↑6%) | +0.16x ⚠️ |
| B05_Accumulator | 2097.5 | 52.6 | 39.84x | -19.2 (↓1%) | +4.56x ⚠️ |
| B06_FuncCall | 372.8 | 26.8 | 13.89x | -181.8 (↓33%) | -6.59x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 06:58 UTC — `0aad4d84da915bb3933b0af972738f6957e81574`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1340.7 | 253.1 | 5.30x | -131.9 (↓9%) | -0.70x ✅ |
| B02_Fibonacci | 16.0 | 4.3 | 3.70x | +14.4 (↑900%) | -1.22x ✅ |
| B03_NestedLoop | 678.9 | 15.6 | 43.46x | +164.1 (↑32%) | +26.24x ⚠️ |
| B04_Branching | 1466.8 | 335.2 | 4.38x | +119.5 (↑9%) | -1.26x ✅ |
| B05_Accumulator | 2475.0 | 51.9 | 47.72x | +358.3 (↑17%) | +12.44x ⚠️ |
| B06_FuncCall | 554.6 | 27.1 | 20.48x | — | — |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 06:37 UTC — `2451d5ee2f415230be862ccb6ab92e76c7d421ee`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1600.6 | 262.8 | 6.09x | +128.0 (↑9%) | +0.09x |
| B02_Fibonacci | 1.8 | 0.4 | 5.15x | +0.2 (↑12%) | +0.23x ⚠️ |
| B03_NestedLoop | 582.5 | 61.1 | 9.54x | +67.7 (↑13%) | -7.68x ✅ |
| B04_Branching | 1572.9 | 241.8 | 6.51x | +225.6 (↑17%) | +0.87x ⚠️ |
| B05_Accumulator | 2479.4 | 60.8 | 40.77x | +362.7 (↑17%) | +5.49x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 05:51 UTC — `cb8d142298edc86c8ad86d38440326f40f508d4e`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1328.6 | 249.8 | 5.32x | -144.0 (↓10%) | -0.68x ✅ |
| B02_Fibonacci | 1.5 | 0.3 | 4.99x | -0.1 (↓6%) | +0.07x |
| B03_NestedLoop | 552.0 | 29.6 | 18.65x | +37.2 (↑7%) | +1.43x ⚠️ |
| B04_Branching | 1393.4 | 513.9 | 2.71x | +46.1 (↑3%) | -2.93x ✅ |
| B05_Accumulator | 2383.0 | 59.0 | 40.40x | +266.3 (↑13%) | +5.12x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 04:07 UTC — `49ed0338c5773b91ec294bbdfda2755b18687ce5`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1378.7 | 251.1 | 5.49x | -93.9 (↓6%) | -0.51x ✅ |
| B02_Fibonacci | 1.7 | 0.3 | 5.17x | +0.1 (↑6%) | +0.25x ⚠️ |
| B03_NestedLoop | 667.3 | 60.6 | 11.02x | +152.5 (↑30%) | -6.20x ✅ |
| B04_Branching | 1549.0 | 236.0 | 6.56x | +201.7 (↑15%) | +0.92x ⚠️ |
| B05_Accumulator | 2517.8 | 62.7 | 40.17x | +401.1 (↑19%) | +4.89x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 04:04 UTC — `71c1379749ba3650bd0c9f69039e8cf37c42c670`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1284.8 | 253.8 | 5.06x | -187.8 (↓13%) | -0.94x ✅ |
| B02_Fibonacci | 12.2 | 0.4 | 27.35x | +10.6 (↑662%) | +22.43x ⚠️ |
| B03_NestedLoop | 646.7 | 57.1 | 11.33x | +131.9 (↑26%) | -5.89x ✅ |
| B04_Branching | 1378.2 | 243.4 | 5.66x | +30.9 (↑2%) | +0.02x |
| B05_Accumulator | 2049.3 | 62.1 | 32.98x | -67.4 (↓3%) | -2.30x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-04 03:19 UTC — `7bcf53db5812f9087b7a48db0c016d23fefd5f63`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1376.6 | 247.8 | 5.56x | -96.0 (↓7%) | -0.44x ✅ |
| B02_Fibonacci | 1.9 | 0.3 | 5.58x | +0.3 (↑19%) | +0.66x ⚠️ |
| B03_NestedLoop | 585.0 | 46.3 | 12.64x | +70.2 (↑14%) | -4.58x ✅ |
| B04_Branching | 1565.2 | 241.7 | 6.48x | +217.9 (↑16%) | +0.84x ⚠️ |
| B05_Accumulator | 2478.1 | 60.8 | 40.75x | +361.4 (↑17%) | +5.47x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-03 19:26 UTC — `2ae1bd9b2b5ced1d1cd478fec8256a5c75c996ec`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1298.9 | 246.9 | 5.26x | -173.7 (↓12%) | -0.74x ✅ |
| B02_Fibonacci | 17.3 | 0.3 | 51.54x | +15.7 (↑981%) | +46.62x ⚠️ |
| B03_NestedLoop | 529.5 | 32.5 | 16.28x | +14.7 (↑3%) | -0.94x ✅ |
| B04_Branching | 1448.2 | 234.3 | 6.18x | +100.9 (↑7%) | +0.54x ⚠️ |
| B05_Accumulator | 3557.2 | 59.0 | 60.33x | +1440.5 (↑68%) | +25.05x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-03 19:25 UTC — `0fa81da42e63846e6840bcbe3db2551c7b194077`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1214.9 | 246.3 | 4.93x | -257.7 (↓17%) | -1.07x ✅ |
| B02_Fibonacci | 1.3 | 0.3 | 3.94x | -0.3 (↓19%) | -0.98x ✅ |
| B03_NestedLoop | 442.9 | 30.5 | 14.53x | -71.9 (↓14%) | -2.69x ✅ |
| B04_Branching | 1291.4 | 247.2 | 5.22x | -55.9 (↓4%) | -0.42x ✅ |
| B05_Accumulator | 1932.4 | 61.0 | 31.69x | -184.3 (↓9%) | -3.59x ✅ |


### 2026-04-03 17:45 UTC — `365591c6959f7e29978e4e9d8f1cbef391b19694`

> .NET 8.0.25 | Unix | 2 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1269.2 | 246.9 | 5.14x | -203.4 (↓14%) | -0.86x ✅ |
| B02_Fibonacci | 7.3 | 0.3 | 20.95x | +5.7 (↑356%) | +16.03x ⚠️ |
| B03_NestedLoop | 879.6 | 22.2 | 39.55x | +364.8 (↑71%) | +22.33x ⚠️ |
| B04_Branching | 1440.9 | 249.9 | 5.77x | +93.6 (↑7%) | +0.13x ⚠️ |
| B05_Accumulator | 2795.8 | 65.4 | 42.77x | +679.1 (↑32%) | +7.49x ⚠️ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


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
