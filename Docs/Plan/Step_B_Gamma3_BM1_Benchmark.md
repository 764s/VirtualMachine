# B-γ3: BM1 Benchmark 基础设施改进

> 来源：P001_Performance_Baseline_Rebuild.md §4.1 + §4.3
> 状态：⏳ 执行中

## 目标

CI benchmark 历史的 Δ 列直接使用跨环境数值，产生误导性退化警告。
B02 时间低于精度门限，B05 受 dispatch 开销放大影响。
本步骤改善基准设施可靠性，确保后续优化步骤（B-γ4 O15）有可信度量基础。

## 子计划

- [x] T1: `update-history.sh` 环境指纹对比 — 从环境行提取 `runtime|os|cores` 指纹，与上一条记录对比；环境变化时 Δ 列追加 `(env changed)` 标记
- [x] T2: `performance_history.md` 基线说明 — 在 Baseline 节添加说明：初始基线来自 Windows/.NET 6/20 核环境，CI 为 Unix/.NET 8/2 核，跨环境 Δ 不可直接比较
- [x] T3: `BenchmarkRunner.cs` WarmupRuns 20 → 100 — 确保 Dynamic PGO 完全生效
- [x] T4: B02 scale 25 → 250 — 避免亚微秒精度噪声；O(N) 线性时间增长，Number 溢出在 VM/C# 双边一致
- [x] T5: B05 说明 — 添加复杂度注释解释 dispatch 开销放大效应
- [x] T6: B06 新增函数调用密集基准 — CALL/RET + CALL_LEAF/RET_LEAF 开销测量
- [x] T7: CI 自我基线 — update-history.sh 检测环境变化时跳过 delta 计算，标记为新环境基线
- [x] T8: `generate-report.sh` + `run-benchmarks.cmd` 同步更新 — B02 描述、WarmupRuns、B06 描述
- [x] T9: 验证 — 795 项 Assert × 2 模式全通过 + benchmark 运行正确

## 完成条件

| # | 条件 | 验证方式 |
|---|------|----------|
| ① | update-history.sh 环境指纹 | 脚本能检测 runtime/os/cores 变化并标记 |
| ② | performance_history.md 基线说明 | 文件头部包含跨环境差异说明 |
| ③ | WarmupRuns ≥ 100 | BenchmarkRunner.cs 常量检查 |
| ④ | B02 scale 调整 | 从 fib(25) 改为 fib(250) |
| ⑤ | B05 说明 | 有 dispatch 开销放大解释 |
| ⑥ | B06 新增基准 | 函数调用密集测试存在并通过 |
| ⑦ | CI 自我基线 | 环境变化时记录为新基线而非对比 |
| ⑧ | benchmark 历史对比正确 | 同环境内对比有效，跨环境标记清晰 |

## 妥协点

无永久妥协。所有条件均可在本步骤内满足。

## 功能展望

无新增。

## 优化展望

- B-γ4 (O15) 紧随本步骤执行，将利用改善后的基准设施验证热循环优化效果。

## 风险点

- **R-BM1**: 2 核 CI 环境 benchmark 方差天然较大；WarmupRuns=100 缓解但无法消除。同环境内趋势比较仍然有效。已确认安全。
