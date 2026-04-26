# Step VOM6: Batch 调用入口 + 摊销基准

> **位置**: VOM 系列第 6 步（VOM3 后并行可推；与 VOM4/5 不互依）。
> **状态**: 🟢 已完成。
> **前置**: [VOM3](Step_VOM3_CPUDataPool.md) 完成。
> **来源**: [IdealAndGap §四 S9](../Discussion/D_VM_ObjectModel_IdealAndGap.md)。
> **核心原则**: Batch 内 transient slot 借用、stackalloc、handle 解引用复用，方案成本被摊薄；YieldCall 不参与 batch（continuation 不能共享借用）。

---

## 0. 实施结果（落地小节）

- 新增 `Assets/Scripts/VM/Core/BatchPlan.cs`：`public enum BatchKind { Call, ReadOnlyCall }` + `public readonly ref struct BatchPlan { MethodHandle Handle; int Count; ReadOnlySpan<Number> Args; Span<Number> Returns; Span<VMError> Errors; }`，构造函数在边界处一次性校验 `Args.Length == Count*ParamCount`、`Returns.Length == Count*ReturnCount`、`Errors.Length ∈ {0, Count}`，提供 `ArgsAt(row)/ReturnsAt(row)/HasErrorSink`。
- 新增 `VMEngine.Batch(VMWorld, int moduleSlot, BatchPlan, BatchKind)`：在 transient pool 上 Rent **一次**、外层一次性写入哨兵 `CallFrame[0]`、循环内仅重置 IP/StateFlags/LeafReturnIP/CallStackDepth/CleanupDepth/ErrorFlag/IsAlive 等行级字段并 ExecuteInstance。`HasErrorSink` 时单行失败写入 `Errors[row]` 并继续；缺省（无 sink）首失败抛 `ReadOnlyViolationException`（违反场景）或 `VMABIException`（其它）。返回失败行计数。
- 新增测试拆分：`VOM6Tests.cs / .Basic.cs / .Validation.cs / .Perf.cs`，27 个断言全通过。
- 接入 `StandaloneRunner/Program.cs` 末尾 `VOM6Tests.RunAll();`，整体回归 0 失败、`EXIT=0`。

### 关键决策

1. **API = 行扁平化矩阵**（`Count + Args(N×P) + Returns(N×R)`），不引入 instanceId 列表 —— transient pool 没有真实 ID。
2. **BatchKind = Call / ReadOnlyCall**（YieldCall 不入列）。
3. **continue-on-error**：调用方提供 `Span<VMError> Errors` 即收集；缺省（空）等价 fail-fast，首失败抛异常。
4. **失败语义**：`PanicReadOnlyViolation` 抛 `ReadOnlyViolationException`（与 VOM3 单次调用一致）；其它 panic 抛 `VMABIException`；行内未 Completed 视为 yield/wait 违规并以 `VMError.PanicStepLimitExceeded` 编码（无 sink 时抛 VMABIException 文本化）。
5. **性能门 = warn-only**（沿用 VOM3 P2_F2 先例）：≤35 ns ReadOnly / ≤45 ns Call。
6. **0 alloc 验证**通过 `GC.GetAllocatedBytesForCurrentThread` 跨 100 个 batch 调用，delta=0。

### 实测数据（StandaloneRunner Release / net10.0）

| 项 | 实测 | 门 | 结论 |
|----|------|----|------|
| P01 ReadOnly batch=64 per-row | 43.7 ns | ≤35 ns warn-only | WARN（与 VOM3 P2_F2 先例一致——记录差距，不阻塞） |
| P02 Call batch=64 per-row | 44.6 ns | ≤45 ns warn-only | PASS |
| P03 100 batch alloc delta | 0 B | 0 B | PASS |
| 整体 | 27/27 PASS | — | PASS |

P01 略超门 ~25%；与单次 ReadOnlyCall（VOM3 P2_F2 实测 ~67.6 ns）相比仍摊薄 ~35%，方向正确。后续若需达 35 ns 门，可考虑：

- ExecuteInstance 内 ReadOnly 守卫的分支预测优化。
- 行内 IP/Flags 重置的 SIMD 化（目前是 7 个标量字段写）。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| BatchPlan 仅支持同 MethodHandle | 简化第一版；最常见场景 = 一万实例同步逻辑 | 多句柄混合 batch 后续按需 |
| YieldCall 不在 batch | continuation 不能共享借用 | — |
| API 行扁平矩阵，未实例化为 instanceId 列表 | transient pool 无真实 ID；模型与上层 ECS-row 一致 | 若引入 persistent batch 再扩 |
| P01 warn-only 不达 ≤35 ns | 与 VOM3 P2_F2 先例一致；摊销方向已验证 | 后续单独优化迭代 |

---

## 二、基础设施盘点

| 组件 | 现状 |
|------|------|
| Call / ReadOnlyCall | VOM3 ✅ |
| CPUDataPool | VOM3 ✅ |
| InstancePool | VOM5 平铺 ✅ |
| BatchPlan / VMEngine.Batch | VOM6 🟢 |

---

## 三、子任务（落地状态）

- [x] A.1 新建 `BatchPlan.cs`
- [x] A.2 字段：`MethodHandle Handle`、`int Count`、`ReadOnlySpan<Number> Args`、`Span<Number> Returns`、`Span<VMError> Errors`
- [x] A.3 索引器辅助：`ArgsAt(row)/ReturnsAt(row)/HasErrorSink`
- [x] B.1 入口 `Batch(VMWorld, int moduleSlot, BatchPlan, BatchKind)`
- [x] B.2 单次 `pool.Rent()`
- [x] B.3 循环：行级 reset + ExecuteInstance + 写回
- [x] B.4 `finally pool.Return(id)`
- [x] B.5 单实例失败：`HasErrorSink` 收集 / 缺省抛
- [x] C.1/C.2 P01/P02 摊销基准（warn-only 实测记录）
- [x] C.4 P03 0 分配验证（实测 0 B）
- [x] D.1 VM_Summary VOM6 🟢
- [x] D.2 IdealAndGap §六 收敛条件 3 验证记录
- [x] D.4 本文件 🟢

---

## 四、验收门禁（实测）

| 类型 | 阈值 | 实测 |
|------|------|------|
| Assert | 现有 + ~10 新增全通过 | 27/27 PASS（既有 ~2152 仍 0 失败） |
| 性能 | batch=64 ReadOnly ≤35 / Call ≤45 ns | 43.7 / 44.6 ns（P01 warn-only 超门 25%；P02 PASS） |
| 一致性 | 单次实测与 Transition 天花板表无矛盾 | 摊销 vs 单次 ~35% 优化，方向一致 |
| 0 alloc | 100 batch 调用 delta=0 | 0 B |
| EXIT | 0 | 0 |

---

## 五、回退策略

- ~~C 性能不达标：先记录差距~~ — P01 已用 warn-only 记录，遵循 VOM3 P2_F2 先例。

---

## 六、关联文档更新

- VM_Summary VOM6 行 🟢；
- IdealAndGap §六 收敛条件 3 标注 VOM6 实测验证；
- 本文件 🟢。
