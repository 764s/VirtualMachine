# Step VOM4: YieldCall + YieldHandle

> **位置**: VOM 系列第 4 步（VOM3 后并行可推）。
> **状态**: 🟢 已完成。
> **前置**: [VOM3](Step_VOM3_CPUDataPool.md) 完成。
> **来源**: [IdealAndGap §四 S6](../Discussion/D_VM_ObjectModel_IdealAndGap.md)。
> **核心原则**: 持久 yield 状态由主 InstancePool 槽位承载；同实例宿主重入被 `HostExecuting` 阻断；对外通过 `YieldHandle` 访问。

---

## 一、本步骤的临时妥协（最终）

| 妥协 | 决策 | 状态 |
|------|------|------|
| 持久 CPUData 由独立 handle 化池管理 | **永久放弃**：复用主 `InstancePool`，槽位即 yield 状态承载体；`Generation` 字段提供 ABA 防御 | 🟢 已结案 |
| 同实例 yield 重入策略 = Reject（不 Queue） | 通过 `HostExecuting` flag 在 `ExecuteInstance` 入口/出口 set/clear，`YieldHandle` 三个方法（`Release` / `TickOnce` / `ReadReturn`）在 syscall/debugger 回调内重入时抛 `YieldReentrancyException` | 🟢 |
| 单活跃 continuation：同实例同时仅一个 yield 链 | 由槽位 1:1 + `Generation` 唯一性自然保证 | 🟢 |

---

## 二、最终架构

| 组件 | 实现 |
|------|------|
| Yield 状态承载 | 主 `InstancePool` 槽位（无独立池） |
| Stale handle 防御 | `VMInstanceState.Generation` + `YieldHandle.Generation` 比对 |
| 宿主重入防御 | `VMStateFlags.HostExecuting` (=32)，`ExecuteInstance` try/finally 守护 |
| 入口 API | `VMEngine.YieldCall(world, moduleSlot, MethodHandle, Arguments) → YieldHandle` |
| 驱动 API | `world.Tick()` 推进所有活动实例；`YieldHandle.TickOnce(world)` 单实例驱动 |
| 读取 / 释放 | `YieldHandle.IsValid` / `IsCompleted` / `HasError` / `GetError` / `ReadReturn` / `Release` |

### Sentinel CallFrame + LeafReturnIP 双闸门

- `YieldCall` 在槽位 0 写入 `CallFrame { ReturnIP = -1 }` 并设 `CallStackDepth = 1`；
- 同时强制 `inst.LeafReturnIP = -1`（**关键**：编译器对纯叶子函数 emit `RET_LEAF`，会从 `LeafReturnIP` 而非帧栈恢复 IP；若不写 -1，叶子函数返回会落到 `IP=0`，进入 `main` 体并破坏 r0 返回值）；
- `RET_FUNC` / `RET_LEAF` / `RETURN`-cleanup-pop 三处统一在恢复 IP 后检测 `IP < 0` → 设 `Completed`，停机。

---

## 三、子任务总览（最终）

```
A. 持久 CPUData handle 化           ❌ 永久放弃（架构已等价）
B. VMEngine.YieldCall              🟢 实现
C. 重入 reject                     🟢 通过 HostExecuting 实现
D. yield 挂起 / 恢复状态保留        🟢 现有 Wait 流程天然支持
E. 测试 + 基准                     🟢
F. 文档同步                        🟢
```

### Sub-task A — 已结案（不再进行）
理由：VOM3 验证 sentinel 模式 + Generation 防御组合已经在不引入 handle 化独立池的情况下满足全部安全语义；新增独立 `CPUData[]` 反而增加路径长度与 GC 复杂度，无收益。

### Sub-task B — VMEngine.YieldCall
- [x] B.1 入口签名 `YieldCall(VMWorld, moduleSlot, MethodHandle, Arguments) → YieldHandle`
- [x] B.2 校验：world / handle.IsResolved / module 已加载 / handle.IsValid(program) / args.Count == ParamCount
- [x] B.3 槽位分配 + 写 sentinel + `LeafReturnIP=-1` + 拷贝 args 到 r0..rN-1
- [x] B.4 不立即执行，返回 `YieldHandle(InstanceId, Generation, ModuleSlot, ReturnCount)`

### Sub-task C — 重入 reject
- [x] C.1 `VMStateFlags.HostExecuting` 新位 (=32)
- [x] C.2 `ExecuteInstance` 入口 `inst.StateFlags |= HostExecuting; try { … } finally { inst.StateFlags &= ~HostExecuting; }`
- [x] C.3 `YieldHandle.CheckUsable` 检查 `world == null` / `IsValid` / `HostExecuting`，按情况抛 `VMABIException` 或 `YieldReentrancyException`
- [x] C.4 `Release` 对 stale handle 静默幂等；对 HostExecuting 则抛

### Sub-task D — yield 挂起 / 恢复
- [x] D.1 完全复用现有 `WAIT 1` / `WaitCounter` 机制；`Tick()` 自动跳过 `WaitCounter > 0` 的实例并递减
- [x] D.2 IP / RegisterBase / CallStack / CleanupDepth / Registers 均位于 `VMInstanceState`，跨 Tick 自然保留

### Sub-task E — 测试 + 基准
- [x] E.1 4 个端到端 + 5 个 handle 语义 + 3 个重入 = 12 用例（拆分到 `VOM4Tests.Basic.cs` / `Handle.cs` / `Reentrancy.cs`，均 ≤120 行）
- [x] E.2 Generation ABA 防御（H02）
- [x] E.3 性能门禁 B09 / B10（warn-only）
- [x] E.4 全部 36 项 PASS，StandaloneRunner exit=0

#### 实测性能（warn-only，gate 仅作回归告警）

| 基准 | 路径 | 实测 | gate |
|------|------|------|------|
| B09 | YieldCall + Tick + ReadReturn + Release（无 yield） | **85.0 ns/op** | ≤ 100 ns |
| B10 | YieldCall + 3× TickOnce + ReadReturn + Release（单 yield） | **96.6 ns/op** | ≤ 230 ns |

### Sub-task F — 文档同步
- [x] F.1 [VM_Summary](../VM_Summary.md) VOM4 ⏳ → 🟢
- [x] F.2 [IdealAndGap](../Discussion/D_VM_ObjectModel_IdealAndGap.md) G1 ❌ / G3 ✅
- [x] F.3 本文件 🟢
- [x] F.4 [benchmarks/benchmark_results.md](../../benchmarks/benchmark_results.md) B09/B10

---

## 四、验收门禁

| 类型 | 阈值 | 实际 |
|------|------|------|
| Assert | 现有 + ~12 新增全通过 | **36 passed, 0 failed** |
| 性能 | YieldCall ≤ 230 ns（一次挂起+恢复周期）| **96.6 ns** ✅ |
| 行为 | 同实例宿主重入精确 reject | ✅ R01-R03 全过 |

---

## 五、关键决策记录

1. **不外置 CPUData**：VOM3 + sentinel + Generation 已具备 yield 所需全部能力，外置仅增成本无新能力。
2. **复用 WAIT 机制**：Tick 已天然支持挂起/恢复；YieldCall 不引入新的调度路径，仅是「不立即执行的 spawn」。
3. **`LeafReturnIP = -1` 是必须的**：纯叶子函数 (`return a+b` 类) 编译为 `RET_LEAF`，绕开帧栈使用 `LeafReturnIP`。该字段不属于 sentinel CallFrame；必须显式设置才能命中 host-call 哨兵。

---

## 六、关联文档更新

- [VM_Summary](../VM_Summary.md) — VOM4 状态行更新；
- [IdealAndGap](../Discussion/D_VM_ObjectModel_IdealAndGap.md) — G1 标记不再进行 / G3 ✅；
- [benchmarks/benchmark_results.md](../../benchmarks/benchmark_results.md) — VOM4 段添加 B09/B10。
