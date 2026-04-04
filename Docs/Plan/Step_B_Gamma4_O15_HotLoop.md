# B-γ4: O15 ExecuteInstance 热循环优化

> 来源：P001_Performance_Baseline_Rebuild.md §4.2 + Outlook_And_Risks.md O15
> 状态：✅ 完成

## 目标

ExecuteInstance 是 VM 执行时间占比最高的方法。
逐指令边界检查、JIT Tier-0 编译、字段重复读取三项开销叠加，
在 dispatch-heavy 负载（B05）下尤为显著。
本步骤通过哨兵指令 + JIT 提示 + 局部缓存三项改动，
在不影响正确性的前提下显著降低热循环开销。

## 子计划

- [x] T1: SENTINEL 操作码 — OpCode.cs 新增 `SENTINEL = 31`（永不由编译器生成）
- [x] T2: VMProgram 构造函数追加 SENTINEL — Instructions 数组末尾追加哨兵，SourceMap 同步扩展（哨兵映射 line = -1）
- [x] T3: InstructionCount 属性 — `Instructions.Length - 1`，返回逻辑指令数（不含哨兵）
- [x] T4: ExecuteInstance 移除逐指令边界检查 — `if (inst.IP < 0 || inst.IP >= code.Length)` 移除，SENTINEL switch-case 触发 PanicOutOfBounds
- [x] T5: AggressiveOptimization — `[MethodImpl(MethodImplOptions.AggressiveOptimization)]` 跳过 Tier-0 直接 Tier-1
- [x] T6: MaxStepsPerTick 局部缓存 — 循环前 `int maxSteps = MaxStepsPerTick;`，while 条件改用 maxSteps
- [x] T7: BenchmarkRunner / SandboxRunner — `Instructions.Length` → `InstructionCount`
- [x] T8: 验证 — 795 项 Assert × 2 模式全通过 + benchmark ≥30% 下降

## 完成条件

| # | 条件 | 验证方式 |
|---|------|----------|
| ① | SENTINEL 操作码 | OpCode.cs 包含 `SENTINEL = 31` |
| ② | VMProgram 追加 SENTINEL | 构造函数末尾追加 + SourceMap 同步 |
| ③ | InstructionCount 属性 | `Instructions.Length - 1` |
| ④ | 移除逐指令边界检查 | ExecuteInstance 无 `inst.IP >= code.Length` |
| ⑤ | AggressiveOptimization | 方法特性已添加 |
| ⑥ | MaxStepsPerTick 局部缓存 | while 条件使用局部变量 |
| ⑦ | benchmark ≥30% 下降 | 全部 6 项基准均超 30% |
| ⑧ | 795 项 Assert 全通过 | CI 双模式验证 |

## Benchmark 结果

环境：Unix / .NET 8.0.25 / 2 cores / WarmupRuns=100 / MeasureRuns=200

| Benchmark | Before (μs) | After (μs) | Δ | 改善 |
|-----------|-------------|------------|---|------|
| B01_ArithLoop | 1191.1 | 809.5 | -381.6 | -32.0% |
| B02_Fibonacci | 37.3 | 7.5 | -29.8 | -79.9% |
| B03_NestedLoop | 680.4 | 260.8 | -419.6 | -61.7% |
| B04_Branching | 1420.0 | 697.8 | -722.2 | -50.9% |
| B05_Accumulator | 2065.3 | 1096.8 | -968.5 | -46.9% |
| B06_FuncCall | 454.9 | 243.8 | -211.1 | -46.4% |

**平均改善：~53%**。全部基准超过 ≥30% 目标。

## 变更文件

| 文件 | 变更 |
|------|------|
| `Assets/Scripts/VM/Core/OpCode.cs` | +SENTINEL = 31 |
| `Assets/Scripts/VM/Core/VMProgram.cs` | 构造函数追加 SENTINEL + SourceMap 同步 + InstructionCount 属性 |
| `Assets/Scripts/VM/Core/VMWorld.cs` | AggressiveOptimization + 移除边界检查 + SENTINEL case + maxSteps 缓存 |
| `Assets/Scripts/VM/Tests/BenchmarkRunner.cs` | Instructions.Length → InstructionCount |
| `Sandbox/SandboxRunner.cs` | Instructions.Length → InstructionCount |
