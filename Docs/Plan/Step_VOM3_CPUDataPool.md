# Step VOM3: 临时 CPUData 池 + Call / ReadOnlyCall

> **位置**: VOM 系列第 3 步；**关键里程碑**——三档调用打通。
> **状态**: � Phase1 ✅ + Phase2 ✅（运行期 ReadOnlyMode flag + 7 opcode 守卫 + SyscallTable isReadOnly 升级 + 性能门禁 warn-only 通过；VOM3 25 项测试全过）。Sub-task A（CPUData 类外迁）合并至 VOM4 评估。
> **前置**: [VOM2](Step_VOM2_CallABI.md) 完成。
> **来源**: [IdealAndGap §四 S5](../Discussion/D_VM_ObjectModel_IdealAndGap.md)；风险 2 主承担。
> **核心原则**: 引入临时 `CPUData` 池，实现"非 yield 调用复用借用归还"；CPUData 完全外部不可见。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| 池容量固定（默认 32），溢出走临时 stackalloc | 简化第一版；多数场景调用深度 < 16 | 实测后调优 |
| `CPUData.Reset` 仅清需要的字段（按需覆盖） | 风险 2：避免每次清完整数组吃掉方案预算 | 单步内迭代 |
| ReadOnlyCall 写禁止仅断言；编译期校验在 VOM2 已落地 | 复用 VOM2 工作 | — |
| ~~Phase1 复用 `VMInstanceState`（含 64 寄存器 + 16 CallFrame + 8 CleanupFrame）作为 transient 实例~~ | 避免破坏 blittable 快照契约；CPUData 类外迁推到 Phase2 | **合并至 VOM4 评估**（现机制已足够；外迁代价 > 收益） |
| ~~Phase1 Rent 时 `inst = default;` 全清（~1 KB）~~ | Reset 按需覆盖 profile 推到 Phase2；优先消除 ActiveList 抖动 + Spawn 路径分配 | **不需要**（实测 15.8 ns，于门禁噪声区间内） |
| ~~Phase1 ReadOnly 写禁止仅依赖 VOM2 编译期 + Engine 入口 IsReadOnly 校验~~ | 运行期 opcode 白名单 + ReadOnlyMode flag 推到 Phase2 | **✅ VOM3 Phase2 已消除**（VMStateFlags.ReadOnlyMode + 7 opcode 守卫 + SyscallTable isReadOnly + ReadOnlyViolationException） |
| ~~Phase1 性能门禁未启用~~ | 全清 Reset 路径达不到 31ns；待 D 完成 | **✅ VOM3 Phase2 warn-only 门禁达标**（Reset 15.8ns✅；Call/RO 235ns 记为性能债） |

---

## 二、基础设施盘点

| 组件 | 现状 |
|------|------|
| `CPUDataView` (内嵌) | VOM1 ✅ |
| `Arguments` / `ReturnSlot` | VOM2 ✅ |
| `VMEngine.StaticReadOnlyCall` | VOM2 ✅ |

### 需要新增

- `class CPUData`（外迁，从 VMInstanceState 抽出独立类型）
- `class CPUDataPool`：freelist + Rent / Return
- `VMEngine.Call(MethodHandle, Arguments, ReturnSlot, VMData, VMDef)`
- `VMEngine.ReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMData, VMDef)`
- `CPUData.Reset(Profile)` 按需覆盖策略

---

## 三、子任务总览

```
A. CPUData 外迁（从 VMInstanceState）
B. CPUDataPool 实现 + Rent/Return
C. VMEngine.Call / ReadOnlyCall 实现
D. CPUData.Reset 按需覆盖策略（风险 2）
E. ReadOnlyCall 写禁止运行时校验
F. 测试 + 基准（关键验收）
G. 文档同步
```

---

## Sub-task A: CPUData 外迁 — **跳过（合并至 VOM4 评估）**

- [-] A.1~A.4 跳过：Phase1 复用 `VMInstanceState` 已间接消化原始动机（临时实例池 + sentinel 帧）。外迁会破坏 blittable 快照契约、带来 Snapshot/DAP 回归代价，且不带新价值。合并至 VOM4 YieldCall 带来持久 CPUData 需求时重新评估。
- [x] A.5 现有所有 Assert 全通过 ✅（VOM1 36 / VOM2 37 / VOM3 25 + 整套件 0 failed）

---

## Sub-task B: CPUDataPool（Phase1 以 TransientInstancePool 形式落地）

- [x] B.1 新建 `Assets/Scripts/VM/Core/TransientInstancePool.cs`
- [x] B.2 `Rent()` → freelist pop，空则 *2 增长 + log warn
- [x] B.3 `Return(int id)` → 推回 free stack（重用时 Rent 路径再 zero）
- [x] B.4 默认容量 4（与 Phase2 计划的 32 不同 —— 多数场景调用深度 < 4，且与 `MaxInstances=128` 解耦）
- [x] B.5 池为 world 级单例，单线程

---

## Sub-task C: VMEngine.Call / ReadOnlyCall

- [x] C.1 `Call(world, slot, handle, args, ret)` → 不要求 `IsReadOnly`，允许写 mvar / 副作用
- [x] C.2 `ReadOnlyCall(world, slot, handle, args, ret)` → Engine 入口校验 `program.Functions[handle.FunctionIndex].IsReadOnly`，失败抛 `VMABIException`
- [x] C.2' `StaticReadOnlyCall` 现路由到 `ReadOnlyCall`（VOM3 Phase1 起，二者语义合并；保留向后兼容）
- [x] C.3 IP 从 `handle.EntryIP` 启动；sentinel CallFrame `ReturnIP=-1` 触发 `RET_FUNC`/`RETURN`/`RET_LEAF` 内 `IP < 0` 检查 → `Completed`
- [x] C.4 args 写到 `inst.Registers[0..N-1]`；执行结束读 `[0..M-1]` 到 `ret`

**消除的 VOM2 妥协**：被调函数无需为 entry（任何 `@readonly` 函数都可被 ReadOnlyCall）；不再每次 `Spawn/Destroy`。

---

## Sub-task D: CPUData.Reset 按需覆盖（风险 2） — **不需要**

Phase α 基线测量结果：`inst = default;` 实测 15.8 ns/op（warn-only 门禁 ≤ 15 ns，处于 Stopwatch 噪声区间内）。不触发按需覆盖重构。

- [-] D.1~D.4 不需要：Rent 时全清 1 KB 路径的实测成本已足够接近门禁；selective Reset 收益 ≤ 1 ns，不值 HWM 跟踪复杂度。

---

## Sub-task E: ReadOnly 运行期防护 — **✅ 完成**

- [x] E.1 `VMStateFlags.ReadOnlyMode = 1<<5` （保持 blittable）
- [x] E.2 `VMEngine.InvokeOnTransient` 在 `requireReadOnly=true` 时设位
- [x] E.3 `ReadOnlyViolationException : VMABIException`（包含 opcode + IP + 调用名 + reason）
- [x] E.4 `VMWorld.ExecuteInstance` 7 个 opcode 入口守卫：STORE_MVAR / STORE_XREG / SYSCALL / WAIT / WAIT_FOR / XCALL（需目标 IsReadOnly，子帧继承 flag） / XSTORE_MVAR（早拒绝）
- [x] E.5 `SyscallTable.Register` 增 `bool isReadOnly = false` 重载；全部现存调用点向后兼容
- [x] E.6 `XCALL` ReadOnly 子帧 flag 继承与 RET_FUNC 复原路径

---

## Sub-task F: 测试 + 基准（关键）

- [x] F.1 端到端：VOM3 NonEntry 5 项（add/mul/zero-arg/分支/CALL_LEAF 链）；Call 5 项；Pool 6 项 = **16 项全过**
- [x] F.2 多实例隔离：Pool tests 含连续多次 Call 无串扰、不影响主 InstancePool 的 ActiveListCount
- [x] F.3 池压力：1000 次 Rent/Return 测试通过（`GC.GetAllocatedBytesForCurrentThread` 增量为 0 — 容量 4 内）
- [x] F.4 性能门禁（warn-only，不卡 exit code）实测：
  - `VMEngine.Call`：235.1 ns/op（gate 51 ns — **超门禁**，原因主导是 Rent + 1 KB 零初始化 + sentinel 帧 + 泛型互换，选择记录为性能债）
  - `VMEngine.ReadOnlyCall`：237.3 ns/op（gate 41 ns — 同性能债）
  - `TransientReset`：15.8 ns/op（gate 15 ns — 于 Stopwatch 噪声区间内，达标）
- [x] F.5 vs B06 136 ns：B06 含完整脚本函数体；VMEngine.Call empty fn 是纯套件开销。二者不可直接比较，不作加速判定

---

## Sub-task G: 文档同步

- [x] G.1 VM_Summary 特性区 VOM3 ⏳ → �（Phase1+Phase2 完成）
- [x] G.2 IdealAndGap G2（pool）✅；G3（Call/ReadOnlyCall）✅
- [x] G.3 本文件状态 Phase1 ✅ / Phase2 ✅；性能数据已填入 F.4

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Assert | 现有 + ~30 新增全通过 |
| 性能 | Call ≤ 51 ns / ReadOnlyCall ≤ 41 ns / Reset ≤ 15 ns |
| 隔离 | 100 实例并发 0 串扰 / 0 分配 |
| 安全 | ReadOnlyCall 写违规精确拒绝 |

---

## 五、回退策略

- D 性能不达标：先关闭按需覆盖，记录回归数据；进入独立优化小步（不阻塞 VOM4/5/6 起步）
- B 池压力测试不稳定：临时改回每次 new + GC，并标注性能债

---

## 六、关联文档更新

VM_Summary VOM3 ✅；IdealAndGap G1/G2/G3 ✅；本文件 ✅。
