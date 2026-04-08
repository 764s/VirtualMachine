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

### 2026-04-08 12:20 UTC — `1bc4e275235dbe616e6c2a3eb216339e7985ffe5`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 309.2 | 73.8 | 4.19x | -1163.4 (↓79%) | -1.81x ✅ |
| B02_Fibonacci | 1.2 | 0.4 | 2.82x | -0.4 (↓25%) | -2.10x ✅ |
| B03_NestedLoop | 272.3 | 59.1 | 4.61x | -242.5 (↓47%) | -12.61x ✅ |
| B04_Branching | 578.2 | 30.3 | 19.07x | -769.1 (↓57%) | +13.43x ⚠️ |
| B05_Accumulator | 942.9 | 47.6 | 19.80x | -1173.8 (↓55%) | -15.48x ✅ |
| B06_FuncCall | 260.8 | 14.9 | 17.45x | -293.8 (↓53%) | -3.03x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 16:41 UTC — `1728c9be4d8c0653083caec5a96e04bc54b4c44f`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 245.1 | 47.6 | 5.15x | -1227.5 (↓83%) | -0.85x ✅ |
| B02_Fibonacci | 0.9 | 0.6 | 1.53x | -0.7 (↓44%) | -3.39x ✅ |
| B03_NestedLoop | 206.1 | 35.9 | 5.75x | -308.7 (↓60%) | -11.47x ✅ |
| B04_Branching | 441.8 | 36.7 | 12.03x | -905.5 (↓67%) | +6.39x ⚠️ |
| B05_Accumulator | 618.7 | 57.7 | 10.72x | -1498.0 (↓71%) | -24.56x ✅ |
| B06_FuncCall | 216.4 | 12.4 | 17.39x | -338.2 (↓61%) | -3.09x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 16:21 UTC — `1ca5b9e2d218ea5f8f201ea9a558d07c9662a889`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 674.3 | 76.7 | 8.80x | -798.3 (↓54%) | +2.80x ⚠️ |
| B02_Fibonacci | 1.7 | 0.5 | 3.15x | +0.1 (↑6%) | -1.77x ✅ |
| B03_NestedLoop | 463.0 | 52.7 | 8.78x | -51.8 (↓10%) | -8.44x ✅ |
| B04_Branching | 532.2 | 15.3 | 34.76x | -815.1 (↓60%) | +29.12x ⚠️ |
| B05_Accumulator | 671.7 | 52.8 | 12.71x | -1445.0 (↓68%) | -22.57x ✅ |
| B06_FuncCall | 227.8 | 16.6 | 13.75x | -326.8 (↓59%) | -6.73x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 15:54 UTC — `ede0df1e877aea9783efda672f044d379ad3c7b2`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 296.6 | 71.7 | 4.13x | -1176.0 (↓80%) | -1.87x ✅ |
| B02_Fibonacci | 1.6 | 0.5 | 3.26x | 0 (=) | -1.66x ✅ |
| B03_NestedLoop | 253.6 | 58.6 | 4.33x | -261.2 (↓51%) | -12.89x ✅ |
| B04_Branching | 520.8 | 12.6 | 41.48x | -826.5 (↓61%) | +35.84x ⚠️ |
| B05_Accumulator | 898.6 | 46.9 | 19.16x | -1218.1 (↓58%) | -16.12x ✅ |
| B06_FuncCall | 252.1 | 15.0 | 16.77x | -302.5 (↓55%) | -3.71x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 14:59 UTC — `0b3db4ee60eda0baad5891283a0871c36344f76e`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 248.7 | 48.5 | 5.13x | -1223.9 (↓83%) | -0.87x ✅ |
| B02_Fibonacci | 0.9 | 0.6 | 1.41x | -0.7 (↓44%) | -3.51x ✅ |
| B03_NestedLoop | 211.8 | 36.9 | 5.74x | -303.0 (↓59%) | -11.48x ✅ |
| B04_Branching | 442.7 | 38.0 | 11.64x | -904.6 (↓67%) | +6.00x ⚠️ |
| B05_Accumulator | 636.9 | 57.7 | 11.04x | -1479.8 (↓70%) | -24.24x ✅ |
| B06_FuncCall | 228.8 | 12.6 | 18.22x | -325.8 (↓59%) | -2.26x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 14:45 UTC — `cf6b5ce`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 317.1 | 70.3 | 4.51x | -1155.5 (↓78%) | -1.49x ✅ |
| B02_Fibonacci | 2.0 | 0.4 | 4.45x | +0.4 (↑25%) | -0.47x ✅ |
| B03_NestedLoop | 268.5 | 58.9 | 4.56x | -246.3 (↓48%) | -12.66x ✅ |
| B04_Branching | 578.1 | 50.8 | 11.38x | -769.2 (↓57%) | +5.74x ⚠️ |
| B05_Accumulator | 968.4 | 47.4 | 20.41x | -1148.3 (↓54%) | -14.87x ✅ |
| B06_FuncCall | 269.9 | 14.9 | 18.13x | -284.7 (↓51%) | -2.35x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 14:20 UTC — `46861ce90c27b9154cc8a252f1a717fb81f59a37`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 626.2 | 147.5 | 4.25x | — (env changed) | — (env changed) |
| B02_Fibonacci | 2.2 | 0.6 | 3.44x | — (env changed) | — (env changed) |
| B03_NestedLoop | 541.2 | 43.9 | 12.32x | — (env changed) | — (env changed) |
| B04_Branching | 822.0 | 20.8 | 39.58x | — (env changed) | — (env changed) |
| B05_Accumulator | 800.0 | 47.5 | 16.84x | — (env changed) | — (env changed) |
| B06_FuncCall | 271.0 | 15.1 | 17.97x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `8.0.25|Microsoft|8` → `8.0.25|Unix|4`. Deltas reset.


### 2026-04-07 11:43 UTC — `674cc07121c34e76ff07947a80da6dc8b31fe059`

> .NET 8.0.25 | Microsoft | 8 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 1188.8 | 116.4 | 10.21x | — (env changed) | — (env changed) |
| B02_Fibonacci | 5.3 | 1.1 | 4.71x | — (env changed) | — (env changed) |
| B03_NestedLoop | 992.1 | 18.5 | 53.64x | — (env changed) | — (env changed) |
| B04_Branching | 1974.7 | 33.1 | 59.74x | — (env changed) | — (env changed) |
| B05_Accumulator | 2807.1 | 84.7 | 33.13x | — (env changed) | — (env changed) |
| B06_FuncCall | 680.7 | 19.2 | 35.53x | — (env changed) | — (env changed) |

📌 **New environment baseline**: environment changed from `8.0.25|Unix|4` to `8.0.25|Microsoft|8`. Deltas reset.


### 2026-04-07 11:26 UTC — `bc9309fd827cfd53066c21ebe7f7a355659f551b`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 230.5 | 47.7 | 4.84x | -1242.1 (↓84%) | -1.16x ✅ |
| B02_Fibonacci | 0.8 | 0.6 | 1.34x | -0.8 (↓50%) | -3.58x ✅ |
| B03_NestedLoop | 200.4 | 36.9 | 5.42x | -314.4 (↓61%) | -11.80x ✅ |
| B04_Branching | 442.5 | 38.5 | 11.51x | -904.8 (↓67%) | +5.87x ⚠️ |
| B05_Accumulator | 568.7 | 57.6 | 9.87x | -1548.0 (↓73%) | -25.41x ✅ |
| B06_FuncCall | 180.5 | 12.5 | 14.42x | -374.1 (↓67%) | -6.06x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 10:40 UTC — `8890136209caa4b12d13188cd9da1fe5a2a4bb40`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 407.3 | 71.6 | 5.69x | -1065.3 (↓72%) | -0.31x ✅ |
| B02_Fibonacci | 1.2 | 0.5 | 2.30x | -0.4 (↓25%) | -2.62x ✅ |
| B03_NestedLoop | 224.0 | 58.8 | 3.81x | -290.8 (↓56%) | -13.41x ✅ |
| B04_Branching | 546.1 | 58.4 | 9.35x | -801.2 (↓59%) | +3.71x ⚠️ |
| B05_Accumulator | 666.8 | 47.8 | 13.96x | -1449.9 (↓68%) | -21.32x ✅ |
| B06_FuncCall | 218.3 | 15.3 | 14.30x | -336.3 (↓61%) | -6.18x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-07 07:00 UTC — `f82a4e490c0af2a750cff1bde5ffa9b29bc2b707`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 263.9 | 71.2 | 3.70x | -1208.7 (↓82%) | -2.30x ✅ |
| B02_Fibonacci | 1.5 | 0.5 | 3.34x | -0.1 (↓6%) | -1.58x ✅ |
| B03_NestedLoop | 227.8 | 59.1 | 3.86x | -287.0 (↓56%) | -13.36x ✅ |
| B04_Branching | 558.7 | 20.5 | 27.20x | -788.6 (↓59%) | +21.56x ⚠️ |
| B05_Accumulator | 680.8 | 47.4 | 14.36x | -1435.9 (↓68%) | -20.92x ✅ |
| B06_FuncCall | 219.3 | 15.2 | 14.45x | -335.3 (↓60%) | -6.03x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 19:18 UTC — `1500d7c2904b057b62e506fabf77e80ee2c6eb02`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 254.6 | 71.2 | 3.57x | -1218.0 (↓83%) | -2.43x ✅ |
| B02_Fibonacci | 1.1 | 0.5 | 2.35x | -0.5 (↓31%) | -2.57x ✅ |
| B03_NestedLoop | 214.9 | 58.9 | 3.65x | -299.9 (↓58%) | -13.57x ✅ |
| B04_Branching | 529.8 | 20.6 | 25.78x | -817.5 (↓61%) | +20.14x ⚠️ |
| B05_Accumulator | 610.1 | 53.2 | 11.46x | -1506.6 (↓71%) | -23.82x ✅ |
| B06_FuncCall | 213.7 | 14.8 | 14.44x | -340.9 (↓61%) | -6.04x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 19:15 UTC — `6d7b0f4afcfc1515f9aa46f07eb4853d9e3c7026`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 276.1 | 72.1 | 3.83x | -1196.5 (↓81%) | -2.17x ✅ |
| B02_Fibonacci | 1.5 | 0.5 | 3.29x | -0.1 (↓6%) | -1.63x ✅ |
| B03_NestedLoop | 222.0 | 58.6 | 3.79x | -292.8 (↓57%) | -13.43x ✅ |
| B04_Branching | 537.9 | 20.5 | 26.21x | -809.4 (↓60%) | +20.57x ⚠️ |
| B05_Accumulator | 656.6 | 47.1 | 13.95x | -1460.1 (↓69%) | -21.33x ✅ |
| B06_FuncCall | 213.1 | 15.5 | 13.79x | -341.5 (↓62%) | -6.69x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 18:48 UTC — `1426c33c7a62a3827aaa02dbe1f99117e9e7ac63`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 298.8 | 71.5 | 4.18x | -1173.8 (↓80%) | -1.82x ✅ |
| B02_Fibonacci | 0.9 | 0.5 | 1.80x | -0.7 (↓44%) | -3.12x ✅ |
| B03_NestedLoop | 223.2 | 62.8 | 3.55x | -291.6 (↓57%) | -13.67x ✅ |
| B04_Branching | 499.3 | 13.0 | 38.45x | -848.0 (↓63%) | +32.81x ⚠️ |
| B05_Accumulator | 675.9 | 47.4 | 14.26x | -1440.8 (↓68%) | -21.02x ✅ |
| B06_FuncCall | 217.5 | 15.2 | 14.32x | -337.1 (↓61%) | -6.16x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 18:43 UTC — `1ec5dc4997338a98f5a62556fc464b393ceb1fa4`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 265.2 | 71.4 | 3.71x | -1207.4 (↓82%) | -2.29x ✅ |
| B02_Fibonacci | 1.0 | 0.5 | 2.16x | -0.6 (↓38%) | -2.76x ✅ |
| B03_NestedLoop | 222.4 | 59.0 | 3.77x | -292.4 (↓57%) | -13.45x ✅ |
| B04_Branching | 534.0 | 20.4 | 26.15x | -813.3 (↓60%) | +20.51x ⚠️ |
| B05_Accumulator | 675.6 | 47.6 | 14.19x | -1441.1 (↓68%) | -21.09x ✅ |
| B06_FuncCall | 212.9 | 15.1 | 14.11x | -341.7 (↓62%) | -6.37x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 18:29 UTC — `da91b8e277db7084f769153ae7a6ac23bfe2ff99`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 217.2 | 48.0 | 4.52x | -1255.4 (↓85%) | -1.48x ✅ |
| B02_Fibonacci | 0.8 | 0.6 | 1.31x | -0.8 (↓50%) | -3.61x ✅ |
| B03_NestedLoop | 181.1 | 36.5 | 4.96x | -333.7 (↓65%) | -12.26x ✅ |
| B04_Branching | 447.4 | 15.2 | 29.44x | -899.9 (↓67%) | +23.80x ⚠️ |
| B05_Accumulator | 543.0 | 63.8 | 8.51x | -1573.7 (↓74%) | -26.77x ✅ |
| B06_FuncCall | 180.9 | 12.3 | 14.65x | -373.7 (↓67%) | -5.83x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 18:05 UTC — `1e412e4ba6697b506fff73975fcd29cce4887680`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 271.9 | 72.1 | 3.77x | -1200.7 (↓82%) | -2.23x ✅ |
| B02_Fibonacci | 1.1 | 0.4 | 2.46x | -0.5 (↓31%) | -2.46x ✅ |
| B03_NestedLoop | 228.6 | 58.9 | 3.88x | -286.2 (↓56%) | -13.34x ✅ |
| B04_Branching | 529.2 | 20.4 | 25.90x | -818.1 (↓61%) | +20.26x ⚠️ |
| B05_Accumulator | 681.5 | 47.7 | 14.30x | -1435.2 (↓68%) | -20.98x ✅ |
| B06_FuncCall | 224.3 | 15.3 | 14.62x | -330.3 (↓60%) | -5.86x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 14:42 UTC — `963a2eaeab177f58d7d90326e56342ac1b9a9099`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 217.4 | 47.8 | 4.54x | -1255.2 (↓85%) | -1.46x ✅ |
| B02_Fibonacci | 0.8 | 0.7 | 1.20x | -0.8 (↓50%) | -3.72x ✅ |
| B03_NestedLoop | 194.9 | 36.7 | 5.31x | -319.9 (↓62%) | -11.91x ✅ |
| B04_Branching | 455.8 | 37.4 | 12.20x | -891.5 (↓66%) | +6.56x ⚠️ |
| B05_Accumulator | 547.8 | 57.5 | 9.52x | -1568.9 (↓74%) | -25.76x ✅ |
| B06_FuncCall | 186.1 | 12.3 | 15.08x | -368.5 (↓66%) | -5.40x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 14:31 UTC — `e926978695615f18ef3a10343eb84ad7e47fc2f3`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 397.9 | 102.9 | 3.87x | -1074.7 (↓73%) | -2.13x ✅ |
| B02_Fibonacci | 0.9 | 0.5 | 2.00x | -0.7 (↓44%) | -2.92x ✅ |
| B03_NestedLoop | 299.2 | 59.0 | 5.07x | -215.6 (↓42%) | -12.15x ✅ |
| B04_Branching | 540.7 | 65.0 | 8.31x | -806.6 (↓60%) | +2.67x ⚠️ |
| B05_Accumulator | 673.3 | 47.3 | 14.24x | -1443.4 (↓68%) | -21.04x ✅ |
| B06_FuncCall | 219.3 | 14.9 | 14.68x | -335.3 (↓60%) | -5.80x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 13:34 UTC — `edf2f130da5992a81e1e07014e81a6caef7de171`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 278.0 | 71.8 | 3.87x | -1194.6 (↓81%) | -2.13x ✅ |
| B02_Fibonacci | 0.9 | 0.5 | 1.90x | -0.7 (↓44%) | -3.02x ✅ |
| B03_NestedLoop | 224.2 | 58.9 | 3.81x | -290.6 (↓56%) | -13.41x ✅ |
| B04_Branching | 514.6 | 12.9 | 39.99x | -832.7 (↓62%) | +34.35x ⚠️ |
| B05_Accumulator | 654.9 | 47.2 | 13.88x | -1461.8 (↓69%) | -21.40x ✅ |
| B06_FuncCall | 217.7 | 16.0 | 13.62x | -336.9 (↓61%) | -6.86x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 12:45 UTC — `e85e5403b38610a959e21c3940eefdd8efd46d90`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 265.1 | 72.1 | 3.68x | -1207.5 (↓82%) | -2.32x ✅ |
| B02_Fibonacci | 0.9 | 0.5 | 1.97x | -0.7 (↓44%) | -2.95x ✅ |
| B03_NestedLoop | 224.0 | 59.4 | 3.77x | -290.8 (↓56%) | -13.45x ✅ |
| B04_Branching | 541.7 | 20.5 | 26.48x | -805.6 (↓60%) | +20.84x ⚠️ |
| B05_Accumulator | 678.1 | 47.7 | 14.23x | -1438.6 (↓68%) | -21.05x ✅ |
| B06_FuncCall | 215.7 | 15.0 | 14.41x | -338.9 (↓61%) | -6.07x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 11:44 UTC — `97c69826a7fd2c176a7862957e8a0c117942e1a0`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 255.1 | 76.7 | 3.33x | -1217.5 (↓83%) | -2.67x ✅ |
| B02_Fibonacci | 0.9 | 0.6 | 1.49x | -0.7 (↓44%) | -3.43x ✅ |
| B03_NestedLoop | 211.6 | 53.0 | 3.99x | -303.2 (↓59%) | -13.23x ✅ |
| B04_Branching | 485.4 | 13.0 | 37.46x | -861.9 (↓64%) | +31.82x ⚠️ |
| B05_Accumulator | 633.7 | 58.4 | 10.85x | -1483.0 (↓70%) | -24.43x ✅ |
| B06_FuncCall | 196.0 | 16.6 | 11.82x | -358.6 (↓65%) | -8.66x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 11:23 UTC — `7aff54a015cb5c2d16734dd4f4a9d0651d40b4de`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 261.0 | 48.2 | 5.41x | -1211.6 (↓82%) | -0.59x ✅ |
| B02_Fibonacci | 0.7 | 0.6 | 1.24x | -0.9 (↓56%) | -3.68x ✅ |
| B03_NestedLoop | 181.3 | 37.0 | 4.90x | -333.5 (↓65%) | -12.32x ✅ |
| B04_Branching | 455.5 | 37.3 | 12.22x | -891.8 (↓66%) | +6.58x ⚠️ |
| B05_Accumulator | 541.3 | 57.6 | 9.40x | -1575.4 (↓74%) | -25.88x ✅ |
| B06_FuncCall | 183.0 | 12.5 | 14.67x | -371.6 (↓67%) | -5.81x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 10:42 UTC — `7dddf8c99d953ae20b339b3da4e2e9f343218a71`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 253.7 | 91.8 | 2.76x | -1218.9 (↓83%) | -3.24x ✅ |
| B02_Fibonacci | 1.4 | 0.5 | 3.13x | -0.2 (↓13%) | -1.79x ✅ |
| B03_NestedLoop | 214.5 | 60.9 | 3.52x | -300.3 (↓58%) | -13.70x ✅ |
| B04_Branching | 536.4 | 20.5 | 26.11x | -810.9 (↓60%) | +20.47x ⚠️ |
| B05_Accumulator | 644.3 | 47.5 | 13.55x | -1472.4 (↓70%) | -21.73x ✅ |
| B06_FuncCall | 208.4 | 14.9 | 13.95x | -346.2 (↓62%) | -6.53x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 08:50 UTC — `32550873d117fee77e9c7a54cb276e124ee76e3c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 307.1 | 92.8 | 3.31x | -1165.5 (↓79%) | -2.69x ✅ |
| B02_Fibonacci | 1.1 | 0.5 | 2.41x | -0.5 (↓31%) | -2.51x ✅ |
| B03_NestedLoop | 223.5 | 61.4 | 3.64x | -291.3 (↓57%) | -13.58x ✅ |
| B04_Branching | 491.9 | 12.8 | 38.56x | -855.4 (↓63%) | +32.92x ⚠️ |
| B05_Accumulator | 678.7 | 47.0 | 14.43x | -1438.0 (↓68%) | -20.85x ✅ |
| B06_FuncCall | 218.2 | 15.0 | 14.57x | -336.4 (↓61%) | -5.91x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 06:49 UTC — `f5c8f58214af3fd15565d795d9bb13f78af99141`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 262.7 | 75.6 | 3.47x | -1209.9 (↓82%) | -2.53x ✅ |
| B02_Fibonacci | 0.9 | 0.5 | 2.10x | -0.7 (↓44%) | -2.82x ✅ |
| B03_NestedLoop | 226.4 | 62.5 | 3.62x | -288.4 (↓56%) | -13.60x ✅ |
| B04_Branching | 555.5 | 20.5 | 27.16x | -791.8 (↓59%) | +21.52x ⚠️ |
| B05_Accumulator | 677.7 | 47.5 | 14.26x | -1439.0 (↓68%) | -21.02x ✅ |
| B06_FuncCall | 217.4 | 15.0 | 14.48x | -337.2 (↓61%) | -6.00x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 06:27 UTC — `2d4b0eda0d62040f111576b161507155bec6cee7`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 399.8 | 71.8 | 5.57x | -1072.8 (↓73%) | -0.43x ✅ |
| B02_Fibonacci | 1.0 | 0.5 | 2.28x | -0.6 (↓38%) | -2.64x ✅ |
| B03_NestedLoop | 222.1 | 59.1 | 3.76x | -292.7 (↓57%) | -13.46x ✅ |
| B04_Branching | 502.8 | 12.6 | 39.89x | -844.5 (↓63%) | +34.25x ⚠️ |
| B05_Accumulator | 656.3 | 47.8 | 13.74x | -1460.4 (↓69%) | -21.54x ✅ |
| B06_FuncCall | 217.3 | 14.8 | 14.64x | -337.3 (↓61%) | -5.84x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-06 05:00 UTC — `fd3379cf81a8e8d5cc6e691c7fce62d5c697f67c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 237.3 | 71.6 | 3.31x | -1235.3 (↓84%) | -2.69x ✅ |
| B02_Fibonacci | 1.9 | 0.5 | 3.82x | +0.3 (↑19%) | -1.10x ✅ |
| B03_NestedLoop | 233.8 | 59.0 | 3.97x | -281.0 (↓55%) | -13.25x ✅ |
| B04_Branching | 496.2 | 13.5 | 36.85x | -851.1 (↓63%) | +31.21x ⚠️ |
| B05_Accumulator | 611.5 | 54.7 | 11.17x | -1505.2 (↓71%) | -24.11x ✅ |
| B06_FuncCall | 215.6 | 15.0 | 14.39x | -339.0 (↓61%) | -6.09x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


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
