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

### 2026-04-12 17:40 UTC — `6edb77f54c97f1374e638ec8a087aed2bf6e6809`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 554.1 | 77.4 | 7.16x | -918.5 (↓62%) | +1.16x ⚠️ |
| B02_Fibonacci | 1.7 | 0.4 | 4.18x | +0.1 (↑6%) | -0.74x ✅ |
| B03_NestedLoop | 371.3 | 13.6 | 27.35x | -143.5 (↓28%) | +10.13x ⚠️ |
| B04_Branching | 624.7 | 47.9 | 13.04x | -722.6 (↓54%) | +7.40x ⚠️ |
| B05_Accumulator | 1154.4 | 53.3 | 21.67x | -962.3 (↓45%) | -13.61x ✅ |
| B06_FuncCall | 124.3 | 14.8 | 8.41x | -430.3 (↓78%) | -12.07x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 16:24 UTC — `1f93ead6b1ac625ccebdc117125f00829a8f52b6`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 414.5 | 76.6 | 5.41x | -1058.1 (↓72%) | -0.59x ✅ |
| B02_Fibonacci | 1.8 | 0.5 | 3.97x | +0.2 (↑12%) | -0.95x ✅ |
| B03_NestedLoop | 379.8 | 53.0 | 7.17x | -135.0 (↓26%) | -10.05x ✅ |
| B04_Branching | 666.0 | 15.4 | 43.23x | -681.3 (↓51%) | +37.59x ⚠️ |
| B05_Accumulator | 1029.4 | 52.8 | 19.49x | -1087.3 (↓51%) | -15.79x ✅ |
| B06_FuncCall | 117.7 | 17.8 | 6.62x | -436.9 (↓79%) | -13.86x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 15:52 UTC — `bce6d9c2d428e4ced1a1ffb2eb76ae2b2333d8bd`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 399.4 | 72.1 | 5.54x | -1073.2 (↓73%) | -0.46x ✅ |
| B02_Fibonacci | 3.4 | 0.5 | 7.53x | +1.8 (↑112%) | +2.61x ⚠️ |
| B03_NestedLoop | 351.9 | 58.8 | 5.99x | -162.9 (↓32%) | -11.23x ✅ |
| B04_Branching | 621.7 | 20.5 | 30.39x | -725.6 (↓54%) | +24.75x ⚠️ |
| B05_Accumulator | 1057.3 | 47.0 | 22.48x | -1059.4 (↓50%) | -12.80x ✅ |
| B06_FuncCall | 129.5 | 15.2 | 8.52x | -425.1 (↓77%) | -11.96x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 13:59 UTC — `c7f06d7601ace89e08b32b610610e2bfb83caa40`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 398.1 | 62.4 | 6.38x | -1074.5 (↓73%) | +0.38x ⚠️ |
| B02_Fibonacci | 1.5 | 0.5 | 3.34x | -0.1 (↓6%) | -1.58x ✅ |
| B03_NestedLoop | 351.9 | 58.6 | 6.00x | -162.9 (↓32%) | -11.22x ✅ |
| B04_Branching | 671.9 | 20.8 | 32.30x | -675.4 (↓50%) | +26.66x ⚠️ |
| B05_Accumulator | 1055.1 | 47.5 | 22.23x | -1061.6 (↓50%) | -13.05x ✅ |
| B06_FuncCall | 121.2 | 15.0 | 8.07x | -433.4 (↓78%) | -12.41x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 13:40 UTC — `29380862ecd10fcd19612dd24d5d75ae85c92d7a`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 411.9 | 76.1 | 5.41x | -1060.7 (↓72%) | -0.59x ✅ |
| B02_Fibonacci | 1.7 | 0.5 | 3.37x | +0.1 (↑6%) | -1.55x ✅ |
| B03_NestedLoop | 370.3 | 55.5 | 6.68x | -144.5 (↓28%) | -10.54x ✅ |
| B04_Branching | 700.0 | 15.0 | 46.68x | -647.3 (↓48%) | +41.04x ⚠️ |
| B05_Accumulator | 1008.4 | 52.7 | 19.12x | -1108.3 (↓52%) | -16.16x ✅ |
| B06_FuncCall | 118.3 | 18.0 | 6.57x | -436.3 (↓79%) | -13.91x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 13:15 UTC — `465fd3b4e43bcfacd46f7043601f2902ad3b8944`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 458.7 | 63.3 | 7.24x | -1013.9 (↓69%) | +1.24x ⚠️ |
| B02_Fibonacci | 1.6 | 0.6 | 2.65x | 0 (=) | -2.27x ✅ |
| B03_NestedLoop | 397.4 | 58.8 | 6.76x | -117.4 (↓23%) | -10.46x ✅ |
| B04_Branching | 671.5 | 20.5 | 32.82x | -675.8 (↓50%) | +27.18x ⚠️ |
| B05_Accumulator | 1122.9 | 47.5 | 23.66x | -993.8 (↓47%) | -11.62x ✅ |
| B06_FuncCall | 137.9 | 15.3 | 9.02x | -416.7 (↓75%) | -11.46x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 12:26 UTC — `999aab67be0edb850e9fc97cf711ba4a41b84bd1`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 449.3 | 70.2 | 6.40x | -1023.3 (↓69%) | +0.40x ⚠️ |
| B02_Fibonacci | 1.7 | 0.4 | 4.19x | +0.1 (↑6%) | -0.73x ✅ |
| B03_NestedLoop | 495.2 | 18.4 | 26.94x | -19.6 (↓4%) | +9.72x ⚠️ |
| B04_Branching | 634.4 | 54.0 | 11.74x | -712.9 (↓53%) | +6.10x ⚠️ |
| B05_Accumulator | 1176.3 | 52.8 | 22.27x | -940.4 (↓44%) | -13.01x ✅ |
| B06_FuncCall | 126.8 | 16.2 | 7.82x | -427.8 (↓77%) | -12.66x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 12:10 UTC — `130edf7bb0b0338dbc7ed7e69a3054dda9ed0650`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 573.1 | 71.1 | 8.06x | -899.5 (↓61%) | +2.06x ⚠️ |
| B02_Fibonacci | 1.7 | 0.4 | 4.22x | +0.1 (↑6%) | -0.70x ✅ |
| B03_NestedLoop | 383.8 | 56.1 | 6.84x | -131.0 (↓25%) | -10.38x ✅ |
| B04_Branching | 709.0 | 15.2 | 46.71x | -638.3 (↓47%) | +41.07x ⚠️ |
| B05_Accumulator | 1048.2 | 53.1 | 19.72x | -1068.5 (↓50%) | -15.56x ✅ |
| B06_FuncCall | 118.1 | 16.1 | 7.34x | -436.5 (↓79%) | -13.14x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 11:05 UTC — `a01bff6084b14784abf6f95f00946789fe36983d`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 554.4 | 41.2 | 13.47x | -918.2 (↓62%) | +7.47x ⚠️ |
| B02_Fibonacci | 1.3 | 0.5 | 2.85x | -0.3 (↓19%) | -2.07x ✅ |
| B03_NestedLoop | 312.1 | 29.2 | 10.68x | -202.7 (↓39%) | -6.54x ✅ |
| B04_Branching | 590.1 | 43.3 | 13.64x | -757.2 (↓56%) | +8.00x ⚠️ |
| B05_Accumulator | 1077.5 | 47.4 | 22.72x | -1039.2 (↓49%) | -12.56x ✅ |
| B06_FuncCall | 138.4 | 15.0 | 9.25x | -416.2 (↓75%) | -11.23x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 08:28 UTC — `92764dc5c01522982a21d6632ced7e9bac2d1aee`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 440.3 | 72.7 | 6.06x | -1032.3 (↓70%) | +0.06x |
| B02_Fibonacci | 1.7 | 0.5 | 3.72x | +0.1 (↑6%) | -1.20x ✅ |
| B03_NestedLoop | 360.5 | 59.0 | 6.11x | -154.3 (↓30%) | -11.11x ✅ |
| B04_Branching | 609.0 | 20.6 | 29.60x | -738.3 (↓55%) | +23.96x ⚠️ |
| B05_Accumulator | 1056.7 | 47.0 | 22.50x | -1060.0 (↓50%) | -12.78x ✅ |
| B06_FuncCall | 198.9 | 15.1 | 13.16x | -355.7 (↓64%) | -7.32x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 08:13 UTC — `794a68ce59243de5a820323e5d3f0e27c0c7012a`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 395.2 | 72.6 | 5.44x | -1077.4 (↓73%) | -0.56x ✅ |
| B02_Fibonacci | 2.1 | 0.5 | 4.75x | +0.5 (↑31%) | -0.17x ✅ |
| B03_NestedLoop | 353.0 | 59.0 | 5.98x | -161.8 (↓31%) | -11.24x ✅ |
| B04_Branching | 630.7 | 20.4 | 30.96x | -716.6 (↓53%) | +25.32x ⚠️ |
| B05_Accumulator | 1054.7 | 46.9 | 22.48x | -1062.0 (↓50%) | -12.80x ✅ |
| B06_FuncCall | 129.0 | 14.9 | 8.68x | -425.6 (↓77%) | -11.80x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 07:42 UTC — `f5b2bec6b3da3053e32adde43e3fb8fc8d9f052c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 395.8 | 72.2 | 5.48x | -1076.8 (↓73%) | -0.52x ✅ |
| B02_Fibonacci | 2.2 | 0.5 | 4.98x | +0.6 (↑38%) | +0.06x |
| B03_NestedLoop | 353.5 | 58.7 | 6.03x | -161.3 (↓31%) | -11.19x ✅ |
| B04_Branching | 642.0 | 20.3 | 31.57x | -705.3 (↓52%) | +25.93x ⚠️ |
| B05_Accumulator | 1044.8 | 47.2 | 22.11x | -1071.9 (↓51%) | -13.17x ✅ |
| B06_FuncCall | 127.7 | 14.9 | 8.57x | -426.9 (↓77%) | -11.91x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 07:37 UTC — `db5c4ce`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 401.7 | 74.4 | 5.40x | -1070.9 (↓73%) | -0.60x ✅ |
| B02_Fibonacci | 1.5 | 0.6 | 2.67x | -0.1 (↓6%) | -2.25x ✅ |
| B03_NestedLoop | 352.9 | 58.8 | 6.00x | -161.9 (↓31%) | -11.22x ✅ |
| B04_Branching | 612.2 | 20.4 | 30.05x | -735.1 (↓55%) | +24.41x ⚠️ |
| B05_Accumulator | 1023.3 | 47.2 | 21.67x | -1093.4 (↓52%) | -13.61x ✅ |
| B06_FuncCall | 128.5 | 14.9 | 8.63x | -426.1 (↓77%) | -11.85x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 05:06 UTC — `ccf21d3a01297c97053f54544c8ab94eeee16d4d`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 411.4 | 76.2 | 5.40x | -1061.2 (↓72%) | -0.60x ✅ |
| B02_Fibonacci | 1.7 | 0.4 | 4.01x | +0.1 (↑6%) | -0.91x ✅ |
| B03_NestedLoop | 372.9 | 53.0 | 7.04x | -141.9 (↓28%) | -10.18x ✅ |
| B04_Branching | 611.2 | 15.4 | 39.56x | -736.1 (↓55%) | +33.92x ⚠️ |
| B05_Accumulator | 1006.6 | 52.8 | 19.06x | -1110.1 (↓52%) | -16.22x ✅ |
| B06_FuncCall | 117.9 | 17.7 | 6.66x | -436.7 (↓79%) | -13.82x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-12 04:27 UTC — `a84dc328c4bcdc152dee13188c023a573d23283f`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 396.4 | 72.6 | 5.46x | -1076.2 (↓73%) | -0.54x ✅ |
| B02_Fibonacci | 1.5 | 0.5 | 3.27x | -0.1 (↓6%) | -1.65x ✅ |
| B03_NestedLoop | 351.6 | 58.8 | 5.98x | -163.2 (↓32%) | -11.24x ✅ |
| B04_Branching | 628.3 | 20.5 | 30.65x | -719.0 (↓53%) | +25.01x ⚠️ |
| B05_Accumulator | 1053.3 | 47.4 | 22.21x | -1063.4 (↓50%) | -13.07x ✅ |
| B06_FuncCall | 119.2 | 15.1 | 7.92x | -435.4 (↓79%) | -12.56x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 22:07 UTC — `f163035bbec3d6e0b569549ac62b0518f40f01f8`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 402.7 | 73.3 | 5.50x | -1069.9 (↓73%) | -0.50x ✅ |
| B02_Fibonacci | 1.7 | 0.5 | 3.68x | +0.1 (↑6%) | -1.24x ✅ |
| B03_NestedLoop | 383.3 | 59.0 | 6.49x | -131.5 (↓26%) | -10.73x ✅ |
| B04_Branching | 589.5 | 20.5 | 28.76x | -757.8 (↓56%) | +23.12x ⚠️ |
| B05_Accumulator | 1036.7 | 46.8 | 22.14x | -1080.0 (↓51%) | -13.14x ✅ |
| B06_FuncCall | 128.5 | 15.1 | 8.50x | -426.1 (↓77%) | -11.98x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 21:34 UTC — `e7458a4f8bcb16784de16f3ee65e14305616f42a`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 482.4 | 72.1 | 6.69x | -990.2 (↓67%) | +0.69x ⚠️ |
| B02_Fibonacci | 1.7 | 0.4 | 3.80x | +0.1 (↑6%) | -1.12x ✅ |
| B03_NestedLoop | 391.1 | 59.0 | 6.63x | -123.7 (↓24%) | -10.59x ✅ |
| B04_Branching | 637.3 | 13.2 | 48.26x | -710.0 (↓53%) | +42.62x ⚠️ |
| B05_Accumulator | 1111.7 | 47.0 | 23.68x | -1005.0 (↓47%) | -11.60x ✅ |
| B06_FuncCall | 144.3 | 15.0 | 9.64x | -410.3 (↓74%) | -10.84x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 20:18 UTC — `751f483ee61056a1e35d7eb681ba411c74f7e21b`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 470.9 | 76.6 | 6.15x | -1001.7 (↓68%) | +0.15x ⚠️ |
| B02_Fibonacci | 1.7 | 0.5 | 3.84x | +0.1 (↑6%) | -1.08x ✅ |
| B03_NestedLoop | 392.5 | 53.3 | 7.37x | -122.3 (↓24%) | -9.85x ✅ |
| B04_Branching | 709.8 | 13.6 | 52.07x | -637.5 (↓47%) | +46.43x ⚠️ |
| B05_Accumulator | 1007.3 | 52.8 | 19.07x | -1109.4 (↓52%) | -16.21x ✅ |
| B06_FuncCall | 141.4 | 19.6 | 7.21x | -413.2 (↓75%) | -13.27x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 19:53 UTC — `37b17b84b2d375253243df6b2c6e4d42474efa5f`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 440.6 | 72.8 | 6.06x | -1032.0 (↓70%) | +0.06x |
| B02_Fibonacci | 3.2 | 0.6 | 5.38x | +1.6 (↑100%) | +0.46x ⚠️ |
| B03_NestedLoop | 428.7 | 10.1 | 42.63x | -86.1 (↓17%) | +25.41x ⚠️ |
| B04_Branching | 607.8 | 51.0 | 11.92x | -739.5 (↓55%) | +6.28x ⚠️ |
| B05_Accumulator | 1156.9 | 47.0 | 24.63x | -959.8 (↓45%) | -10.65x ✅ |
| B06_FuncCall | 133.7 | 15.0 | 8.90x | -420.9 (↓76%) | -11.58x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 19:02 UTC — `95c1be858c8dd542737d665d88ec5977669489b6`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 447.8 | 74.3 | 6.03x | -1024.8 (↓70%) | +0.03x |
| B02_Fibonacci | 2.0 | 0.4 | 4.44x | +0.4 (↑25%) | -0.48x ✅ |
| B03_NestedLoop | 397.3 | 59.0 | 6.74x | -117.5 (↓23%) | -10.48x ✅ |
| B04_Branching | 654.0 | 12.9 | 50.73x | -693.3 (↓51%) | +45.09x ⚠️ |
| B05_Accumulator | 1066.1 | 47.7 | 22.37x | -1050.6 (↓50%) | -12.91x ✅ |
| B06_FuncCall | 147.2 | 15.3 | 9.61x | -407.4 (↓73%) | -10.87x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 16:10 UTC — `2e8a1f9e13c3b52b5a7b18b2100526b212e03fbf`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 344.9 | 47.5 | 7.26x | -1127.7 (↓77%) | +1.26x ⚠️ |
| B02_Fibonacci | 1.2 | 0.6 | 2.04x | -0.4 (↓25%) | -2.88x ✅ |
| B03_NestedLoop | 280.6 | 36.2 | 7.75x | -234.2 (↓45%) | -9.47x ✅ |
| B04_Branching | 491.4 | 19.9 | 24.67x | -855.9 (↓64%) | +19.03x ⚠️ |
| B05_Accumulator | 805.0 | 57.8 | 13.93x | -1311.7 (↓62%) | -21.35x ✅ |
| B06_FuncCall | 100.8 | 12.7 | 7.95x | -453.8 (↓82%) | -12.53x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 14:58 UTC — `65c960dd9c901dc1f2e9e2785ab29974f4883461`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 456.1 | 76.7 | 5.95x | -1016.5 (↓69%) | -0.05x |
| B02_Fibonacci | 1.7 | 0.4 | 4.16x | +0.1 (↑6%) | -0.76x ✅ |
| B03_NestedLoop | 398.7 | 54.3 | 7.35x | -116.1 (↓23%) | -9.87x ✅ |
| B04_Branching | 732.9 | 14.4 | 51.02x | -614.4 (↓46%) | +45.38x ⚠️ |
| B05_Accumulator | 1011.4 | 53.0 | 19.09x | -1105.3 (↓52%) | -16.19x ✅ |
| B06_FuncCall | 121.9 | 14.7 | 8.31x | -432.7 (↓78%) | -12.17x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 09:44 UTC — `0c9c3e5d63544447ba351f3117a3059a671c64b7`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 448.4 | 62.2 | 7.21x | -1024.2 (↓70%) | +1.21x ⚠️ |
| B02_Fibonacci | 1.5 | 0.4 | 3.38x | -0.1 (↓6%) | -1.54x ✅ |
| B03_NestedLoop | 389.9 | 58.6 | 6.65x | -124.9 (↓24%) | -10.57x ✅ |
| B04_Branching | 624.2 | 20.5 | 30.48x | -723.1 (↓54%) | +24.84x ⚠️ |
| B05_Accumulator | 1112.4 | 47.2 | 23.57x | -1004.3 (↓47%) | -11.71x ✅ |
| B06_FuncCall | 136.9 | 14.9 | 9.16x | -417.7 (↓75%) | -11.32x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 08:47 UTC — `7fcd8aeb8ff5f08ea695c960452b7c3e3a350f15`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 321.2 | 48.6 | 6.60x | -1151.4 (↓78%) | +0.60x ⚠️ |
| B02_Fibonacci | 1.3 | 0.6 | 2.02x | -0.3 (↓19%) | -2.90x ✅ |
| B03_NestedLoop | 318.3 | 41.2 | 7.73x | -196.5 (↓38%) | -9.49x ✅ |
| B04_Branching | 498.5 | 19.6 | 25.44x | -848.8 (↓63%) | +19.80x ⚠️ |
| B05_Accumulator | 834.6 | 57.6 | 14.48x | -1282.1 (↓61%) | -20.80x ✅ |
| B06_FuncCall | 106.9 | 11.9 | 8.97x | -447.7 (↓81%) | -11.51x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 08:11 UTC — `14bab7042856f4fed62845226b71705be9a0637f`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 445.3 | 61.7 | 7.21x | -1027.3 (↓70%) | +1.21x ⚠️ |
| B02_Fibonacci | 2.3 | 0.5 | 4.98x | +0.7 (↑44%) | +0.06x |
| B03_NestedLoop | 393.1 | 52.2 | 7.53x | -121.7 (↓24%) | -9.69x ✅ |
| B04_Branching | 701.4 | 20.4 | 34.35x | -645.9 (↓48%) | +28.71x ⚠️ |
| B05_Accumulator | 1098.4 | 47.5 | 23.10x | -1018.3 (↓48%) | -12.18x ✅ |
| B06_FuncCall | 135.9 | 15.0 | 9.09x | -418.7 (↓75%) | -11.39x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 08:08 UTC — `7227b04f6d1115d403b0bb2285927eeab1937e1e`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 459.9 | 61.5 | 7.48x | -1012.7 (↓69%) | +1.48x ⚠️ |
| B02_Fibonacci | 2.4 | 0.5 | 5.22x | +0.8 (↑50%) | +0.30x ⚠️ |
| B03_NestedLoop | 387.5 | 9.5 | 40.68x | -127.3 (↓25%) | +23.46x ⚠️ |
| B04_Branching | 574.0 | 40.9 | 14.02x | -773.3 (↓57%) | +8.38x ⚠️ |
| B05_Accumulator | 1049.3 | 47.4 | 22.13x | -1067.4 (↓50%) | -13.15x ✅ |
| B06_FuncCall | 128.3 | 15.0 | 8.55x | -426.3 (↓77%) | -11.93x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 07:10 UTC — `c5ac2b1f56c9762d7950dd05ffd360116ff60c5e`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 265.6 | 76.7 | 3.46x | -1207.0 (↓82%) | -2.54x ✅ |
| B02_Fibonacci | 0.9 | 0.6 | 1.60x | -0.7 (↓44%) | -3.32x ✅ |
| B03_NestedLoop | 222.8 | 72.0 | 3.09x | -292.0 (↓57%) | -14.13x ✅ |
| B04_Branching | 509.1 | 12.7 | 39.95x | -838.2 (↓62%) | +34.31x ⚠️ |
| B05_Accumulator | 689.6 | 52.7 | 13.07x | -1427.1 (↓67%) | -22.21x ✅ |
| B06_FuncCall | 150.7 | 14.7 | 10.24x | -403.9 (↓73%) | -10.24x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 06:39 UTC — `a8ac5970fdf8f13aaec4243c31f163fa029678fa`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 356.4 | 70.6 | 5.05x | -1116.2 (↓76%) | -0.95x ✅ |
| B02_Fibonacci | 1.3 | 0.4 | 2.99x | -0.3 (↓19%) | -1.93x ✅ |
| B03_NestedLoop | 264.4 | 59.0 | 4.48x | -250.4 (↓49%) | -12.74x ✅ |
| B04_Branching | 545.9 | 45.4 | 12.01x | -801.4 (↓59%) | +6.37x ⚠️ |
| B05_Accumulator | 898.0 | 47.8 | 18.77x | -1218.7 (↓58%) | -16.51x ✅ |
| B06_FuncCall | 101.0 | 14.9 | 6.76x | -453.6 (↓82%) | -13.72x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 05:36 UTC — `1240c0aa7aeb5593bc7281c2c22cbd05e1c0a114`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 356.7 | 72.2 | 4.94x | -1115.9 (↓76%) | -1.06x ✅ |
| B02_Fibonacci | 1.6 | 0.6 | 2.94x | 0 (=) | -1.98x ✅ |
| B03_NestedLoop | 246.4 | 58.9 | 4.18x | -268.4 (↓52%) | -13.04x ✅ |
| B04_Branching | 491.0 | 22.9 | 21.47x | -856.3 (↓64%) | +15.83x ⚠️ |
| B05_Accumulator | 720.2 | 47.4 | 15.19x | -1396.5 (↓66%) | -20.09x ✅ |
| B06_FuncCall | 94.2 | 14.8 | 6.36x | -460.4 (↓83%) | -14.12x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-11 05:08 UTC — `5ba7f53b029ffba182eeb74a507411454724293d`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 287.6 | 69.9 | 4.12x | -1185.0 (↓80%) | -1.88x ✅ |
| B02_Fibonacci | 1.0 | 0.5 | 2.12x | -0.6 (↓38%) | -2.80x ✅ |
| B03_NestedLoop | 235.5 | 59.0 | 3.99x | -279.3 (↓54%) | -13.23x ✅ |
| B04_Branching | 534.8 | 13.2 | 40.42x | -812.5 (↓60%) | +34.78x ⚠️ |
| B05_Accumulator | 737.3 | 47.2 | 15.62x | -1379.4 (↓65%) | -19.66x ✅ |
| B06_FuncCall | 89.6 | 14.8 | 6.06x | -465.0 (↓84%) | -14.42x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 19:57 UTC — `15b2ddccfa57dc42afbf1fb42bbd28bef33e120c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 453.8 | 71.5 | 6.35x | -1018.8 (↓69%) | +0.35x ⚠️ |
| B02_Fibonacci | 1.1 | 0.5 | 2.49x | -0.5 (↓31%) | -2.43x ✅ |
| B03_NestedLoop | 264.8 | 59.9 | 4.42x | -250.0 (↓49%) | -12.80x ✅ |
| B04_Branching | 532.0 | 13.2 | 40.17x | -815.3 (↓61%) | +34.53x ⚠️ |
| B05_Accumulator | 808.9 | 47.6 | 17.00x | -1307.8 (↓62%) | -18.28x ✅ |
| B06_FuncCall | 108.8 | 14.9 | 7.30x | -445.8 (↓80%) | -13.18x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 19:03 UTC — `b41d5309413501784e7a427d36a22712e7507a08`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 405.6 | 77.8 | 5.22x | -1067.0 (↓72%) | -0.78x ✅ |
| B02_Fibonacci | 1.9 | 0.4 | 4.30x | +0.3 (↑19%) | -0.62x ✅ |
| B03_NestedLoop | 219.8 | 53.3 | 4.12x | -295.0 (↓57%) | -13.10x ✅ |
| B04_Branching | 659.6 | 14.8 | 44.61x | -687.7 (↓51%) | +38.97x ⚠️ |
| B05_Accumulator | 770.2 | 53.2 | 14.49x | -1346.5 (↓64%) | -20.79x ✅ |
| B06_FuncCall | 81.3 | 15.7 | 5.17x | -473.3 (↓85%) | -15.31x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 18:20 UTC — `875925131ab2f10f798dc33dd07bb177dc8b37f7`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 255.0 | 76.7 | 3.33x | -1217.6 (↓83%) | -2.67x ✅ |
| B02_Fibonacci | 1.7 | 0.6 | 2.84x | +0.1 (↑6%) | -2.08x ✅ |
| B03_NestedLoop | 220.6 | 54.1 | 4.07x | -294.2 (↓57%) | -13.15x ✅ |
| B04_Branching | 541.4 | 15.2 | 35.55x | -805.9 (↓60%) | +29.91x ⚠️ |
| B05_Accumulator | 702.6 | 52.8 | 13.30x | -1414.1 (↓67%) | -21.98x ✅ |
| B06_FuncCall | 81.3 | 14.6 | 5.55x | -473.3 (↓85%) | -14.93x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 17:32 UTC — `5f0912a47a79ee5d5dc1cf261559c92b174224a2`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 398.1 | 71.4 | 5.58x | -1074.5 (↓73%) | -0.42x ✅ |
| B02_Fibonacci | 1.1 | 0.4 | 2.46x | -0.5 (↓31%) | -2.46x ✅ |
| B03_NestedLoop | 261.0 | 58.8 | 4.44x | -253.8 (↓49%) | -12.78x ✅ |
| B04_Branching | 555.2 | 12.6 | 44.08x | -792.1 (↓59%) | +38.44x ⚠️ |
| B05_Accumulator | 806.9 | 47.2 | 17.10x | -1309.8 (↓62%) | -18.18x ✅ |
| B06_FuncCall | 108.1 | 16.3 | 6.65x | -446.5 (↓81%) | -13.83x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 16:37 UTC — `b812baf59ddc6070561c0b8bcde2ab97312689ed`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 310.3 | 70.0 | 4.43x | -1162.3 (↓79%) | -1.57x ✅ |
| B02_Fibonacci | 1.1 | 0.6 | 1.79x | -0.5 (↓31%) | -3.13x ✅ |
| B03_NestedLoop | 266.1 | 58.8 | 4.53x | -248.7 (↓48%) | -12.69x ✅ |
| B04_Branching | 592.9 | 13.7 | 43.18x | -754.4 (↓56%) | +37.54x ⚠️ |
| B05_Accumulator | 884.4 | 47.5 | 18.62x | -1232.3 (↓58%) | -16.66x ✅ |
| B06_FuncCall | 106.5 | 15.1 | 7.07x | -448.1 (↓81%) | -13.41x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 14:39 UTC — `08e07b8ab1f2363ef42a93c47300b9d0da487a37`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 287.3 | 69.3 | 4.15x | -1185.3 (↓80%) | -1.85x ✅ |
| B02_Fibonacci | 1.3 | 0.5 | 2.77x | -0.3 (↓19%) | -2.15x ✅ |
| B03_NestedLoop | 242.4 | 59.2 | 4.09x | -272.4 (↓53%) | -13.13x ✅ |
| B04_Branching | 532.6 | 21.8 | 24.40x | -814.7 (↓60%) | +18.76x ⚠️ |
| B05_Accumulator | 956.9 | 50.1 | 19.11x | -1159.8 (↓55%) | -16.17x ✅ |
| B06_FuncCall | 248.2 | 16.5 | 15.00x | -306.4 (↓55%) | -5.48x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 13:54 UTC — `05727f9b08807f363dff62a09dee2d93ff6790d6`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 285.4 | 69.5 | 4.11x | -1187.2 (↓81%) | -1.89x ✅ |
| B02_Fibonacci | 1.2 | 0.5 | 2.66x | -0.4 (↓25%) | -2.26x ✅ |
| B03_NestedLoop | 240.9 | 58.9 | 4.09x | -273.9 (↓53%) | -13.13x ✅ |
| B04_Branching | 502.8 | 20.5 | 24.47x | -844.5 (↓63%) | +18.83x ⚠️ |
| B05_Accumulator | 736.1 | 47.2 | 15.58x | -1380.6 (↓65%) | -19.70x ✅ |
| B06_FuncCall | 241.3 | 14.8 | 16.25x | -313.3 (↓56%) | -4.23x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 13:10 UTC — `b5d4bc341ea42ed72e8308a77ff5bef24e570c22`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 322.1 | 75.8 | 4.25x | -1150.5 (↓78%) | -1.75x ✅ |
| B02_Fibonacci | 0.9 | 0.4 | 2.08x | -0.7 (↓44%) | -2.84x ✅ |
| B03_NestedLoop | 219.5 | 53.1 | 4.13x | -295.3 (↓57%) | -13.09x ✅ |
| B04_Branching | 534.1 | 14.3 | 37.30x | -813.2 (↓60%) | +31.66x ⚠️ |
| B05_Accumulator | 657.1 | 52.9 | 12.43x | -1459.6 (↓69%) | -22.85x ✅ |
| B06_FuncCall | 205.8 | 16.3 | 12.62x | -348.8 (↓63%) | -7.86x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 12:39 UTC — `9f6d8cb513d296573cd9a0f5ab490faa146f1c64`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 246.3 | 47.6 | 5.18x | -1226.3 (↓83%) | -0.82x ✅ |
| B02_Fibonacci | 0.9 | 0.7 | 1.28x | -0.7 (↓44%) | -3.64x ✅ |
| B03_NestedLoop | 211.2 | 37.0 | 5.71x | -303.6 (↓59%) | -11.51x ✅ |
| B04_Branching | 451.9 | 38.0 | 11.89x | -895.4 (↓66%) | +6.25x ⚠️ |
| B05_Accumulator | 639.2 | 57.6 | 11.09x | -1477.5 (↓70%) | -24.19x ✅ |
| B06_FuncCall | 204.4 | 11.9 | 17.24x | -350.2 (↓63%) | -3.24x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 10:57 UTC — `7f0fef9b6063c8aab90022d2f56f002c3a15025e`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 403.3 | 76.6 | 5.26x | -1069.3 (↓73%) | -0.74x ✅ |
| B02_Fibonacci | 1.1 | 0.4 | 2.60x | -0.5 (↓31%) | -2.32x ✅ |
| B03_NestedLoop | 227.9 | 52.9 | 4.31x | -286.9 (↓56%) | -12.91x ✅ |
| B04_Branching | 513.4 | 14.8 | 34.72x | -833.9 (↓62%) | +29.08x ⚠️ |
| B05_Accumulator | 1203.0 | 52.9 | 22.75x | -913.7 (↓43%) | -12.53x ✅ |
| B06_FuncCall | 205.8 | 16.3 | 12.65x | -348.8 (↓63%) | -7.83x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 09:49 UTC — `d558e60d8cb6dd279d9cf026397d4176e3c625c4`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 255.3 | 76.5 | 3.34x | -1217.3 (↓83%) | -2.66x ✅ |
| B02_Fibonacci | 1.9 | 0.4 | 4.56x | +0.3 (↑19%) | -0.36x ✅ |
| B03_NestedLoop | 221.7 | 52.9 | 4.19x | -293.1 (↓57%) | -13.03x ✅ |
| B04_Branching | 612.4 | 15.0 | 40.91x | -734.9 (↓55%) | +35.27x ⚠️ |
| B05_Accumulator | 655.1 | 52.9 | 12.39x | -1461.6 (↓69%) | -22.89x ✅ |
| B06_FuncCall | 209.7 | 14.6 | 14.34x | -344.9 (↓62%) | -6.14x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 09:22 UTC — `778734f9b72fba4b1a03d8509565638850327bd7`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 268.3 | 76.7 | 3.50x | -1204.3 (↓82%) | -2.50x ✅ |
| B02_Fibonacci | 0.9 | 0.4 | 2.12x | -0.7 (↓44%) | -2.80x ✅ |
| B03_NestedLoop | 219.7 | 53.0 | 4.14x | -295.1 (↓57%) | -13.08x ✅ |
| B04_Branching | 523.8 | 14.5 | 36.08x | -823.5 (↓61%) | +30.44x ⚠️ |
| B05_Accumulator | 688.0 | 52.9 | 13.00x | -1428.7 (↓67%) | -22.28x ✅ |
| B06_FuncCall | 205.8 | 14.6 | 14.08x | -348.8 (↓63%) | -6.40x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 09:19 UTC — `230ffff9f67d36c18fa037d13565d3b22dd9f456`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 286.5 | 71.6 | 4.00x | -1186.1 (↓81%) | -2.00x ✅ |
| B02_Fibonacci | 1.0 | 0.5 | 2.12x | -0.6 (↓38%) | -2.80x ✅ |
| B03_NestedLoop | 242.4 | 58.9 | 4.11x | -272.4 (↓53%) | -13.11x ✅ |
| B04_Branching | 555.1 | 44.1 | 12.58x | -792.2 (↓59%) | +6.94x ⚠️ |
| B05_Accumulator | 760.7 | 47.5 | 16.01x | -1356.0 (↓64%) | -19.27x ✅ |
| B06_FuncCall | 240.0 | 15.0 | 16.01x | -314.6 (↓57%) | -4.47x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 09:14 UTC — `807080b0605f64672134bdab3ec134a06896732b`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 424.5 | 92.5 | 4.59x | -1048.1 (↓71%) | -1.41x ✅ |
| B02_Fibonacci | 1.6 | 0.5 | 3.50x | 0 (=) | -1.42x ✅ |
| B03_NestedLoop | 245.0 | 61.2 | 4.01x | -269.8 (↓52%) | -13.21x ✅ |
| B04_Branching | 548.6 | 20.6 | 26.64x | -798.7 (↓59%) | +21.00x ⚠️ |
| B05_Accumulator | 759.3 | 47.4 | 16.01x | -1357.4 (↓64%) | -19.27x ✅ |
| B06_FuncCall | 242.1 | 15.2 | 15.91x | -312.5 (↓56%) | -4.57x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 09:02 UTC — `eedccfc4011fe0480949f903f98e1d393a861cf9`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 283.8 | 70.8 | 4.01x | -1188.8 (↓81%) | -1.99x ✅ |
| B02_Fibonacci | 1.0 | 0.6 | 1.62x | -0.6 (↓38%) | -3.30x ✅ |
| B03_NestedLoop | 240.8 | 59.1 | 4.07x | -274.0 (↓53%) | -13.15x ✅ |
| B04_Branching | 512.0 | 20.9 | 24.56x | -835.3 (↓62%) | +18.92x ⚠️ |
| B05_Accumulator | 707.5 | 47.4 | 14.91x | -1409.2 (↓67%) | -20.37x ✅ |
| B06_FuncCall | 236.7 | 14.8 | 16.01x | -317.9 (↓57%) | -4.47x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 08:57 UTC — `3ec931244e3fe562d0fedfe3b537a70bf38b1fb1`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 287.9 | 70.8 | 4.07x | -1184.7 (↓80%) | -1.93x ✅ |
| B02_Fibonacci | 1.0 | 0.7 | 1.51x | -0.6 (↓38%) | -3.41x ✅ |
| B03_NestedLoop | 242.4 | 59.0 | 4.11x | -272.4 (↓53%) | -13.11x ✅ |
| B04_Branching | 560.7 | 20.6 | 27.23x | -786.6 (↓58%) | +21.59x ⚠️ |
| B05_Accumulator | 787.4 | 47.5 | 16.58x | -1329.3 (↓63%) | -18.70x ✅ |
| B06_FuncCall | 243.7 | 14.9 | 16.33x | -310.9 (↓56%) | -4.15x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 06:28 UTC — `936d842170e690cf3d54b8134ce8de95138cdffd`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 288.8 | 84.7 | 3.41x | -1183.8 (↓80%) | -2.59x ✅ |
| B02_Fibonacci | 1.0 | 0.5 | 2.16x | -0.6 (↓38%) | -2.76x ✅ |
| B03_NestedLoop | 242.0 | 61.0 | 3.97x | -272.8 (↓53%) | -13.25x ✅ |
| B04_Branching | 525.5 | 20.4 | 25.71x | -821.8 (↓61%) | +20.07x ⚠️ |
| B05_Accumulator | 756.4 | 47.5 | 15.93x | -1360.3 (↓64%) | -19.35x ✅ |
| B06_FuncCall | 246.4 | 16.0 | 15.42x | -308.2 (↓56%) | -5.06x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 06:00 UTC — `779fa9aba8db80e0365171d2713ba590ae1ad6b8`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 287.9 | 70.8 | 4.06x | -1184.7 (↓80%) | -1.94x ✅ |
| B02_Fibonacci | 1.6 | 0.5 | 3.43x | 0 (=) | -1.49x ✅ |
| B03_NestedLoop | 242.2 | 59.1 | 4.10x | -272.6 (↓53%) | -13.12x ✅ |
| B04_Branching | 514.4 | 20.5 | 25.08x | -832.9 (↓62%) | +19.44x ⚠️ |
| B05_Accumulator | 831.9 | 47.3 | 17.59x | -1284.8 (↓61%) | -17.69x ✅ |
| B06_FuncCall | 238.4 | 15.1 | 15.81x | -316.2 (↓57%) | -4.67x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 05:41 UTC — `e36a3ac2090e622d59a9264e5e2af4a39104c6ec`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 308.5 | 87.8 | 3.51x | -1164.1 (↓79%) | -2.49x ✅ |
| B02_Fibonacci | 1.2 | 0.5 | 2.14x | -0.4 (↓25%) | -2.78x ✅ |
| B03_NestedLoop | 240.7 | 60.9 | 3.95x | -274.1 (↓53%) | -13.27x ✅ |
| B04_Branching | 584.7 | 20.5 | 28.59x | -762.6 (↓57%) | +22.95x ⚠️ |
| B05_Accumulator | 721.5 | 47.3 | 15.24x | -1395.2 (↓66%) | -20.04x ✅ |
| B06_FuncCall | 248.8 | 14.9 | 16.67x | -305.8 (↓55%) | -3.81x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 05:05 UTC — `101cfd9e31d6fa1216cd6223868ca223fbac01e4`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 274.7 | 71.2 | 3.86x | -1197.9 (↓81%) | -2.14x ✅ |
| B02_Fibonacci | 1.1 | 0.5 | 2.39x | -0.5 (↓31%) | -2.53x ✅ |
| B03_NestedLoop | 241.5 | 58.9 | 4.10x | -273.3 (↓53%) | -13.12x ✅ |
| B04_Branching | 553.5 | 20.4 | 27.07x | -793.8 (↓59%) | +21.43x ⚠️ |
| B05_Accumulator | 863.3 | 46.9 | 18.40x | -1253.4 (↓59%) | -16.88x ✅ |
| B06_FuncCall | 242.7 | 14.8 | 16.44x | -311.9 (↓56%) | -4.04x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 04:39 UTC — `298413cb339128c94c97205016b488911a897e6f`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 420.9 | 70.6 | 5.96x | -1051.7 (↓71%) | -0.04x |
| B02_Fibonacci | 1.1 | 0.4 | 2.39x | -0.5 (↓31%) | -2.53x ✅ |
| B03_NestedLoop | 260.9 | 58.9 | 4.43x | -253.9 (↓49%) | -12.79x ✅ |
| B04_Branching | 560.2 | 12.8 | 43.86x | -787.1 (↓58%) | +38.22x ⚠️ |
| B05_Accumulator | 1094.5 | 47.1 | 23.25x | -1022.2 (↓48%) | -12.03x ✅ |
| B06_FuncCall | 268.7 | 15.0 | 17.88x | -285.9 (↓52%) | -2.60x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 03:07 UTC — `671475de8960dd1abc379a7d216e8f57adff30d7`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 309.6 | 69.6 | 4.45x | -1163.0 (↓79%) | -1.55x ✅ |
| B02_Fibonacci | 1.6 | 0.4 | 3.73x | 0 (=) | -1.19x ✅ |
| B03_NestedLoop | 259.7 | 58.5 | 4.44x | -255.1 (↓50%) | -12.78x ✅ |
| B04_Branching | 550.3 | 12.9 | 42.61x | -797.0 (↓59%) | +36.97x ⚠️ |
| B05_Accumulator | 801.7 | 46.9 | 17.08x | -1315.0 (↓62%) | -18.20x ✅ |
| B06_FuncCall | 266.9 | 14.9 | 17.87x | -287.7 (↓52%) | -2.61x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-10 02:11 UTC — `e18e6cf27e18633b231c8dc3b6c4f1f0327c76d4`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 310.9 | 70.8 | 4.39x | -1161.7 (↓79%) | -1.61x ✅ |
| B02_Fibonacci | 1.1 | 0.5 | 2.41x | -0.5 (↓31%) | -2.51x ✅ |
| B03_NestedLoop | 265.3 | 59.0 | 4.50x | -249.5 (↓48%) | -12.72x ✅ |
| B04_Branching | 554.6 | 60.0 | 9.24x | -792.7 (↓59%) | +3.60x ⚠️ |
| B05_Accumulator | 812.8 | 47.1 | 17.27x | -1303.9 (↓62%) | -18.01x ✅ |
| B06_FuncCall | 267.9 | 15.4 | 17.45x | -286.7 (↓52%) | -3.03x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 17:29 UTC — `b194ba7854f78a673a39922168552f12c8bed30c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 251.8 | 47.5 | 5.31x | -1220.8 (↓83%) | -0.69x ✅ |
| B02_Fibonacci | 1.1 | 0.6 | 1.73x | -0.5 (↓31%) | -3.19x ✅ |
| B03_NestedLoop | 214.3 | 36.4 | 5.89x | -300.5 (↓58%) | -11.33x ✅ |
| B04_Branching | 446.5 | 33.0 | 13.55x | -900.8 (↓67%) | +7.91x ⚠️ |
| B05_Accumulator | 637.6 | 57.9 | 11.01x | -1479.1 (↓70%) | -24.27x ✅ |
| B06_FuncCall | 219.1 | 12.0 | 18.31x | -335.5 (↓60%) | -2.17x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 16:02 UTC — `b3712500ce24d7cc94754c8362733d03a9b1a7ab`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 313.8 | 70.5 | 4.45x | -1158.8 (↓79%) | -1.55x ✅ |
| B02_Fibonacci | 1.6 | 0.5 | 3.61x | 0 (=) | -1.31x ✅ |
| B03_NestedLoop | 261.2 | 59.0 | 4.43x | -253.6 (↓49%) | -12.79x ✅ |
| B04_Branching | 567.9 | 47.5 | 11.94x | -779.4 (↓58%) | +6.30x ⚠️ |
| B05_Accumulator | 1005.3 | 47.4 | 21.20x | -1111.4 (↓53%) | -14.08x ✅ |
| B06_FuncCall | 272.1 | 15.1 | 18.06x | -282.5 (↓51%) | -2.42x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 15:56 UTC — `1b6f7d1afa05660a63d194e991080af5beb8db4e`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 453.5 | 92.2 | 4.92x | -1019.1 (↓69%) | -1.08x ✅ |
| B02_Fibonacci | 1.6 | 0.4 | 3.73x | 0 (=) | -1.19x ✅ |
| B03_NestedLoop | 260.6 | 60.8 | 4.29x | -254.2 (↓49%) | -12.93x ✅ |
| B04_Branching | 620.1 | 13.4 | 46.27x | -727.2 (↓54%) | +40.63x ⚠️ |
| B05_Accumulator | 805.3 | 47.4 | 16.98x | -1311.4 (↓62%) | -18.30x ✅ |
| B06_FuncCall | 269.7 | 15.8 | 17.12x | -284.9 (↓51%) | -3.36x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 14:05 UTC — `712e0d91de94a0337791f1a78e57e40fd96a754d`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 247.0 | 48.0 | 5.15x | -1225.6 (↓83%) | -0.85x ✅ |
| B02_Fibonacci | 1.1 | 0.6 | 1.74x | -0.5 (↓31%) | -3.18x ✅ |
| B03_NestedLoop | 211.1 | 36.6 | 5.76x | -303.7 (↓59%) | -11.46x ✅ |
| B04_Branching | 446.1 | 33.6 | 13.27x | -901.2 (↓67%) | +7.63x ⚠️ |
| B05_Accumulator | 631.1 | 57.6 | 10.96x | -1485.6 (↓70%) | -24.32x ✅ |
| B06_FuncCall | 220.2 | 11.9 | 18.51x | -334.4 (↓60%) | -1.97x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 10:54 UTC — `ebea29d9c8c27878d08c5797a10079fcfd1eb89c`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 602.8 | 68.5 | 8.80x | -869.8 (↓59%) | +2.80x ⚠️ |
| B02_Fibonacci | 1.1 | 0.4 | 2.45x | -0.5 (↓31%) | -2.47x ✅ |
| B03_NestedLoop | 261.6 | 59.0 | 4.44x | -253.2 (↓49%) | -12.78x ✅ |
| B04_Branching | 539.9 | 13.2 | 40.75x | -807.4 (↓60%) | +35.11x ⚠️ |
| B05_Accumulator | 995.1 | 47.0 | 21.16x | -1121.6 (↓53%) | -14.12x ✅ |
| B06_FuncCall | 285.6 | 14.8 | 19.31x | -269.0 (↓49%) | -1.17x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 08:42 UTC — `8d628097f7f0768fc16cde793819921e1d192f98`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 308.9 | 71.0 | 4.35x | -1163.7 (↓79%) | -1.65x ✅ |
| B02_Fibonacci | 2.6 | 0.5 | 4.92x | +1.0 (↑62%) | =  |
| B03_NestedLoop | 260.7 | 59.0 | 4.42x | -254.1 (↓49%) | -12.80x ✅ |
| B04_Branching | 536.3 | 12.8 | 41.76x | -811.0 (↓60%) | +36.12x ⚠️ |
| B05_Accumulator | 782.8 | 47.4 | 16.51x | -1333.9 (↓63%) | -18.77x ✅ |
| B06_FuncCall | 262.8 | 15.0 | 17.51x | -291.8 (↓53%) | -2.97x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 08:00 UTC — `fd79e25ab3d6af9bdf4f55d953c2a946b4dc77cb`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 249.5 | 47.1 | 5.30x | -1223.1 (↓83%) | -0.70x ✅ |
| B02_Fibonacci | 2.1 | 0.7 | 2.80x | +0.5 (↑31%) | -2.12x ✅ |
| B03_NestedLoop | 211.5 | 36.0 | 5.87x | -303.3 (↓59%) | -11.35x ✅ |
| B04_Branching | 459.0 | 37.3 | 12.30x | -888.3 (↓66%) | +6.66x ⚠️ |
| B05_Accumulator | 637.1 | 57.9 | 11.01x | -1479.6 (↓70%) | -24.27x ✅ |
| B06_FuncCall | 225.2 | 12.1 | 18.63x | -329.4 (↓59%) | -1.85x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-09 07:12 UTC — `8b4127a88749bf51b8dae827fd582e3d046ab2a9`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 462.1 | 76.4 | 6.05x | -1010.5 (↓69%) | +0.05x |
| B02_Fibonacci | 0.9 | 0.4 | 2.30x | -0.7 (↓44%) | -2.62x ✅ |
| B03_NestedLoop | 222.0 | 53.1 | 4.18x | -292.8 (↓57%) | -13.04x ✅ |
| B04_Branching | 594.5 | 14.2 | 41.76x | -752.8 (↓56%) | +36.12x ⚠️ |
| B05_Accumulator | 749.7 | 52.8 | 14.20x | -1367.0 (↓65%) | -21.08x ✅ |
| B06_FuncCall | 227.3 | 16.6 | 13.68x | -327.3 (↓59%) | -6.80x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


### 2026-04-08 16:55 UTC — `3f29f810b6806adfe6d0f6c01113ddba444e6b3b`

> .NET 8.0.25 | Unix | 4 cores

| Benchmark | VM (μs) | C# (μs) | Ratio | Δ VM | Δ Ratio |
|-----------|---------|---------|-------|------|---------|
| B01_ArithLoop | 310.0 | 74.9 | 4.14x | -1162.6 (↓79%) | -1.86x ✅ |
| B02_Fibonacci | 2.1 | 0.6 | 3.43x | +0.5 (↑31%) | -1.49x ✅ |
| B03_NestedLoop | 261.5 | 59.0 | 4.43x | -253.3 (↓49%) | -12.79x ✅ |
| B04_Branching | 583.1 | 56.4 | 10.34x | -764.2 (↓57%) | +4.70x ⚠️ |
| B05_Accumulator | 792.9 | 46.9 | 16.92x | -1323.8 (↓63%) | -18.36x ✅ |
| B06_FuncCall | 259.3 | 15.4 | 16.81x | -295.3 (↓53%) | -3.67x ✅ |

⚠️ **Regression detected**: VM/C# ratio increased >10% on one or more benchmarks.


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
