# P001: 性能基线重建实践

> **状态**：✅ 已处理（2026-04-04）
> **日期**：2026-04-03
> **触发**：CI 性能历史 `performance_history.md` 出现大幅退化报警
> **结论**：本实践提出的 3 组建议已全部排入串行计划。性能基线重建（4.1）与基准设计改进（4.3）合并为串行步骤 B-γ3（BM1）；
> 执行循环优化（4.2）纳入串行步骤 B-γ4（O15）。两者均在当前位置（B-γ2 完成后）立即执行，确保后续功能开发中性能持续可观测。

---

## 一、背景

在 B-β3（O9 活跃实例链表）完成后，CI benchmark 历史记录显示多项基准大幅退化：

| Benchmark | 初始基线 Ratio | CI 最近 Ratio | 表观变化 |
|-----------|---------------|--------------|---------|
| B03_NestedLoop | 5.86x | 17.22x | ↑ 194% |
| B05_Accumulator | 5.00x | 35.28x | ↑ 606% |
| B04_Branching | 5.69x | 5.64x | ≈ 持平 |
| B01_ArithLoop | 6.87x | 6.00x | ↓ 13% |

看起来存在严重性能退化，但实际分析后发现情况更复杂。

## 二、发现的问题

### 2.1 跨环境对比失效

初始基线采集环境为 **Windows / .NET 6.0 / 20 核**，而 CI 运行环境为 **Unix / .NET 8.0 / 2 核**。
两个环境的硬件性能、JIT 策略、OS 调度完全不同，绝对时间和 VM/C# ratio 不具备可比性。

`performance_history.md` 的 Δ 列直接拿 CI 新值与初始基线做差，产生了误导性极强的退化报警。
实际上绝大部分"退化"是环境差异，而非代码退化。

### 2.2 基准参数不适配 CI 环境

- **Warmup 不足**：WarmupRuns = 20，在 2 核 CI 环境中不足以触发 .NET Dynamic PGO。
- **B02 精度问题**：fib(25) 在 CI 环境中总计 ~1-7μs，已低于计时器精度，ratio 波动极大。
- **B05 ratio 异常**：Accumulator 循环体仅 ~5 条指令，VM 每次循环多出的 dispatch overhead 被 C# 的
  `Number` 结构体 operator 优化反衬到 50x。这不是退化，是该基准的 overhead 放大效应。

### 2.3 热循环可优化点

在分析过程中识别到 `ExecuteInstance` 热循环中的真实优化机会：

1. **逐指令边界检查**：`if (inst.IP < 0 || inst.IP >= code.Length)` 每条指令执行前都做，
   但编译器保证所有代码路径以 RETURN/RET_FUNC/RET_LEAF 终止，大部分情况下不会越界。
2. **`MaxStepsPerTick` 字段读取**：每次循环读取字段而非局部变量缓存。
3. **JIT 提示缺失**：`ExecuteInstance` 未标记 `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`。

这些优化在本地测试中确认有效（VM 执行时间下降 31-65%），但需要经过串行计划的正式评审。

## 三、尝试的修改（已退回）

本次实践尝试了以下修改，均已退回至 `origin/main` 状态：

| 文件 | 修改内容 | 状态 |
|------|---------|------|
| `OpCode.cs` | 新增 `SENTINEL = 31` 内部哨兵操作码 | ❌ 已退回 |
| `VMProgram.cs` | 构造函数追加哨兵指令 + `InstructionCount` 属性 + SourceMap 同步扩展 | ❌ 已退回 |
| `VMWorld.cs` | 移除逐指令边界检查 + SENTINEL case + `AggressiveOptimization` + 局部缓存 MaxSteps | ❌ 已退回 |
| `BenchmarkRunner.cs` | WarmupRuns 20→100 + B02 scale 25→250 + 使用 `InstructionCount` | ❌ 已退回 |
| `update-history.sh` | 环境指纹对比 + `(env changed)` 标记 | ❌ 已退回 |
| `performance_history.md` | 基线说明增加环境不匹配警告 | ❌ 已退回 |

## 四、建议的改进方向

以下建议经讨论后应纳入串行计划（新步骤）或展望条目或风险点。

### 4.1 性能基线重建（建议新增串行步骤）

**目标**：在 CI 环境中建立可靠的、可跨版本追踪的性能基线。

具体建议：

1. **环境感知的 Δ 计算**：`update-history.sh` 应比较环境指纹（runtime + OS + cores），
   当环境变化时在 Δ 列标记 `(env changed)` 而非输出误导数值。
2. **基线环境说明**：`performance_history.md` 开头应注明初始基线环境与 CI 环境不同，
   历史条目之间的 Δ 仅在同环境下有意义。
3. **Warmup 参数调整**：WarmupRuns 从 20 提升到 ≥100，以确保 Dynamic PGO 充分生效。
4. **B02 scale 调整**：fib(25) → fib(250) 或更大，避免亚微秒精度噪声。
5. **考虑在 CI 中生成环境内的自我基线**：首次运行时记录为该环境的基线，后续仅与同环境基线比较。

> **处理结果**：📌 **已排入串行计划** — 新增串行步骤 B-γ3（BM1 Benchmark 基础设施改进），
> 涵盖环境感知 Δ 计算、基线环境说明、Warmup/Scale 调参、CI 自我基线等 5 项建议。理由：后续功能开发需持续观察性能变化。

### 4.2 执行循环优化（建议纳入展望或排入串行步骤）

**目标**：消除 dispatch 热循环中的冗余开销。

具体建议：

1. **哨兵指令方案**：VMProgram 构造函数在指令末尾追加 SENTINEL 操作码（永不由编译器生成），
   switch-case 中 SENTINEL 触发 `PanicOutOfBounds`。这样可安全移除逐指令边界检查。
   - 需同步扩展 SourceMap（哨兵映射到 line -1）
   - 需新增 `InstructionCount` 属性返回逻辑指令数（不含哨兵）
   - BenchmarkRunner 等使用 `Instructions.Length` 的地方需改用 `InstructionCount`
2. **`AggressiveOptimization`**：为 `ExecuteInstance` 添加 JIT 提示，跳过 Tier-0 直接 Tier-1 编译。
3. **局部变量缓存**：`MaxStepsPerTick` 缓存到循环前的局部变量。

> **处理结果**：📌 **已排入串行计划** — 新增串行步骤 B-γ4（O15 ExecuteInstance 热循环优化），
> 涵盖哨兵指令方案、AggressiveOptimization JIT 提示、MaxStepsPerTick 局部缓存。本地验证 VM 执行时间下降 31-65%。改动独立，不影响后续功能步骤。

### 4.3 基准设计改进（建议展望）

- B05 Accumulator 的 50x ratio 主要来自"极短循环体 + dispatch overhead 放大"，
  并非真实性能退化。考虑增加循环体复杂度，或增加文档说明其特殊性。
- 考虑增加"函数调用密集型"基准（B06），专门测试 CALL/RET 和 CALL_LEAF/RET_LEAF。

> **处理结果**：📌 **已排入串行计划** — 纳入串行步骤 B-γ3（BM1 Benchmark 基础设施改进），
> B05 说明与 B06 新增基准一并归入 BM1 统一跟踪。

## 五、本次实践的经验教训

1. **跨环境性能对比是陷阱**：基线和 CI 环境不同时，所有数值对比都是噪声。
2. **基准工具应内建环境感知**：性能跟踪脚本应自动检测并标记环境变化。
3. **优化修改应走串行计划**：即便本地验证有效，性能优化也应作为正式步骤推进，
   包含设计评审、测试补充、文档更新的完整流程。
4. **退回不等于浪费**：实践产出了可行方案和参数建议，纳入计划后可快速实现。

---

## 六、总结与处理状态

| ID | 建议 | 类型 | 优先级 | 处理结果 |
|----|------|------|--------|---------|
| 4.1 | 性能基线重建（环境感知 Δ + Warmup/Scale 调参 + CI 自我基线） | CI / 基准设施 | 中 | 📌 已排入串行计划 → B-γ3 (BM1) |
| 4.2 | 执行循环优化（哨兵指令 + AggressiveOptimization + 局部缓存） | 运行时优化 | 中 | 📌 已排入串行计划 → B-γ4 (O15) |
| 4.3 | 基准设计改进（B05 说明 + B06 新增） | 基准改进 | 低 | 📌 已排入串行计划 → B-γ3 (BM1) |
