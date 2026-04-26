# D_VM_ObjectModel_IdealAndGap

状态: ✅ 已完成（转向计划闭环完成）
关联: [D_VM_ObjectModel_Transition.md](D_VM_ObjectModel_Transition.md)

## 目的

转向定稿前，盘点 “理想形态” 与 “当前实现” 的差距，整理推进过程中需要调整的点。  
不重复转向讨论中已定的语义/契约，仅做现状对照与改造路径。

---

## 一、理想形态（摘录）

详见 [D_VM_ObjectModel_Transition.md](D_VM_ObjectModel_Transition.md)。本节仅列与差距分析直接相关的点：

1. **五对象**：`VMDef` / `CPUData`（含临时池）/ `VMData` / `HostBindings` / `VMInstance`(handle façade) + `InstancePool`(SoA)
2. **四调用契约**（`VMEngine` 静态方法）：
   - `YieldCall(MethodHandle, Arguments, ReturnSlot, CPUData, VMData, VMDef)`
   - `Call(MethodHandle, Arguments, ReturnSlot, VMData, VMDef)`（隐式 CPUData 池）
   - `ReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMData, VMDef)`（隐式 CPUData 池 + 写禁止）
   - `StaticReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMDef)`（仅 constants）
3. **Span<Number> ABI**：`Arguments` / `ReturnSlot` 为 ref struct，零分配
4. **生命周期约束**：
   - CPUData 外部不可见；持久 CPUData 仅 yield 期间存在
   - VMData 任何调用类型下宿主可读写
   - Yield 阻断同实例任何重入
5. **HostBindings 暂定不可切换**，每实例一份
6. **MethodHandle 持久缓存**（VMDef 级，避免字符串查找）

---

## 二、当前实现速览

| 维度 | 当前状态 | 文件:行 |
|------|---------|---------|
| 实例生命周期 | `SpawnInstance(moduleSlot, entryIP)` + `Tick()` 推进 ActiveList + 私有 `ExecuteInstance(ref VMInstanceState)` | VMWorld.cs:52 / 149 / 225 |
| 实例状态结构 | `VMInstanceState` 单结构体混合执行态（IP/CallStack/Registers/Flags/Cleanup/LeafReturnIP）与 wait 业务态（WaitCounter/WaitTargetInstanceId） | VMInstanceState.cs:94-174 |
| 跨实例调用 | `XCALL` 直接递归 `ExecuteInstance(ref targetInst)`，无临时 CPUData | VMWorld.cs:895 |
| 调用入口 | 单一路径：`XCALL` opcode；无 ReadOnly/Static 区分 | — |
| 参数/返回值 ABI | 寄存器直接传递，参数在调用者 r0..r(n-1)，返回值 r0；XCALL 参数 r0→r0 复制 | VMWorld.cs:970-991 / SyscallTable.cs:17-18 |
| HostBindings | 全局：`VMWorld.Syscalls`，每 world 一份，所有实例共享 | VMWorld.cs:12 / 48 |
| 方法解析 | `TryGetFunction(name)` 每次线性遍历 Functions[]，无缓存句柄 | VMProgram.cs:114-123 |
| 实例 façade | 无。调用者使用裸 `int instanceId` 或直接 `ref VMInstanceState` | InstancePool.cs:11 |

---

## 三、差距清单

| # | 维度 | 差距描述 | 影响 | 复杂度 |
|---|------|---------|------|--------|
| G1 | CPUData / VMData 字段切分 | ❌ 不再进行（VOM3+VOM4 验证：sentinel + Generation + HostExecuting 已具备同等安全语义，外置反而增成本无收益） | 永久结案 | ⭐⭐⭐ |
| G2 | 临时 CPUData 池 | ✅ VOM3 Phase1+2：`TransientInstancePool` 复用 `VMInstanceState` slot + 运行期 `ReadOnlyMode` flag + 7 opcode 守卫 + SyscallTable isReadOnly 升级；CPUData 类外迁合并至 VOM4 | Call/ReadOnlyCall 安全 + 防护完备；性能 warn-only 通过（Reset ≈15.8ns） | ⭐⭐⭐ |
| G3 | 四调用契约入口 | ✅ 全通：`VMEngine.Call` / `ReadOnlyCall` / `StaticReadOnlyCall` (VOM3) + `YieldCall` (VOM4，返回 `YieldHandle`) | 宿主可走对象式调用心智 | ⭐⭐⭐ |
| G4 | Arguments / ReturnSlot ref struct ABI | ✅ VOM2 Phase1+Phase2：ref struct ABI + `VMEngine.StaticReadOnlyCall` + 编译期 `@readonly` / `@static_readonly` 注解 + 写指令拒绝；缺微基准门禁（待 VOM3 临时 CPUData 池） | 零分配宿主 ABI 可用；读-写不变式在编译期被强制 | ⭐⭐ |
| G5 | HostBindings 实例绑定 | ✅ VOM5：`HostBindings` 类 + `InstancePool.Bindings[]` 平铺数组；SYSCALL 分发读取每实例绑定；Snapshot 浅拷贝；`world.Syscalls` 透传 zero-churn | 同 world 多技能 / 多角色场景可启用 | ⭐⭐ |
| G6 | MethodHandle 缓存 | ✅ 完成（VOM1：`VMProgram.ResolveMethod` + `Version` + `Invalidate`） | 取消热路径字符串查找预算 | ⭐ |
| G7 | InstancePool SoA + VMInstance façade | ✅ VOM5：`readonly struct VMInstance(World, Id, Generation)` + `IsValid/IsAlive/IsCompleted/Tick/Kill/Bindings`；`SpawnInstance` 新重载返回 façade；靴带 ABA 防御 | 调用面心智一致；旧 `int instanceId` API 保留兼容 | ⭐⭐ |
| G8 | ReadOnly/Static 编译期校验 | 编译器无 `@readonly` 标记，无写指令拒绝路径 | 只读语义只能靠运行期断言 | ⭐⭐ |

---

## 四、调整点（按依赖排序）

依赖原则：底层数据结构 → ABI → 调用契约 → 优化层 → façade。

| 序 | 调整点 | 依赖 | 输出 |
|----|--------|------|------|
| S1 | `VMInstanceState` 字段级切分为 `CPUData`(执行) + `VMData`(业务) | — | 两个独立结构 + 迁移现有 ExecuteInstance 引用 |
| S2 | `MethodHandle` 缓存层（VMDef 级，惰性或预解析） | — | `VMProgram.ResolveMethod(name) → MethodHandle` |
| S3 | `Arguments` / `ReturnSlot` ref struct + `Span<Number>` 中介 | S1 | 类型 + 校验入口 |
| S4 | `VMEngine.StaticReadOnlyCall` 入口（最简）+ 编译期 `@readonly`/纯静态校验 | S2, S3, G8 | 单档调用打通 |
| S5 | 临时 CPUData 池 + `VMEngine.ReadOnlyCall` / `Call` | S1, S4 | 三档调用打通 |
| S6 | `VMEngine.YieldCall` + 持久 CPUData handle 化（仅 yield 期间存在） | S1, S5 | 四档调用全打通 |
| S7 | `HostBindings` 抽出 + 实例引用化 | S5 | 多技能多 binding 支持 |
| S8 | `InstancePool` SoA 化 + `VMInstance` readonly struct façade | S1, S7 | 对外心智收敛 |
| S9 | Batch 调用入口（专用 BatchPlan） | S5 | 摊销新增成本，回到原始天花板 |

---

## 五、风险与不确定项

1. **S1 切分的回退面**：`VMInstanceState` 当前被 ExecuteInstance / XCALL / Cleanup / Wait / Defer 等多处直接引用，字段重排会触发广泛重构；建议先以 “内部分组（partial 或 inner struct）” 兼容，再逐步外迁
2. **临时 CPUData 池的清理成本**：CPUData `Reset()` 若每次清理完整调用栈/寄存器数组，可能吃掉 5-15 ns，必须按需覆盖策略
3. **HostBindings 实例化的迁移路径**：现有 Syscalls 全局假设遍布代码，转实例引用需评估 XCALL / 黑板 Syscall 等是否都按实例语义重写
4. **编译期只读校验** 与现有内联/peephole 优化的交互（写指令可能在内联后才出现）
5. **MethodHandle 与 hot-reload**：脚本热更新后旧句柄需要失效策略（版本号或全失效）

---

## 六、收敛条件

差距清单（G1-G8）在转向落地计划中均有对应步骤；S1-S9 形成最小可执行序列。  
本讨论在以下条件后转 ✅：

1. S1-S9 序列被采纳并进入串行需求列表 — ✅
2. 风险 1-5 各自有缓解策略 — ✅
3. 与转向讨论文件的契约/性能天花板保持一致（无矛盾）— ✅ **VOM11 收口验证**：B08-equivalent (T4 Rent+Return) = 1.29 ns/op（−85% vs 8.8 baseline）；F1=80.3 ns / F2=78.4 ns；VOM6.P01=56.1 ns、P02=56.0 ns（相对基线 ±2 ns 零回归）；P03 alloc=0。详见 [Step_VOM11_LazyRentReset.md](../Plan/Step_VOM11_LazyRentReset.md) 与 [Step_VOM_Overview.md](../Plan/Step_VOM_Overview.md)。

---

## 七、Checklist 子计划路线图

> 状态：✅ 已创建。S1-S9 已映射到 `Docs/Plan/Step_VOM*.md` 系列。
> 命名前缀 `VOM` = Virtual-machine Object Model。
> 入口：[Step_VOM_Overview.md](../Plan/Step_VOM_Overview.md)。

| # | 计划文件 | 涵盖 | 依赖 | 量化门禁（vs [Transition §性能天花板](D_VM_ObjectModel_Transition.md)）|
|---|---------|------|------|------|
| 0 | [Step_VOM_Overview.md](../Plan/Step_VOM_Overview.md) | 全部 | — | 总入口 + 阶段图 + 门禁聚合 |
| 1 | [Step_VOM1_StateSplit.md](../Plan/Step_VOM1_StateSplit.md) | **S1** + S2 | — | 现有 ~2140 Assert 全通过；MethodHandle 解析 ≤ 1 ns/次 |
| 2 | [Step_VOM2_CallABI.md](../Plan/Step_VOM2_CallABI.md) | **S3** + S4 | VOM1 | StaticReadOnlyCall 单次 ≤ 31 ns |
| 3 | [Step_VOM3_CPUDataPool.md](../Plan/Step_VOM3_CPUDataPool.md) | **S5** | VOM2 | Call ≤ 51 ns / ReadOnlyCall ≤ 41 ns；CPUData Reset ≤ 15 ns |
| 4 | [Step_VOM4_YieldCall.md](../Plan/Step_VOM4_YieldCall.md) | **S6** | VOM3 | YieldCall 一次挂起+恢复 ≤ 230 ns；同实例重入 reject |
| 5 | [Step_VOM5_HostBindings_Facade.md](../Plan/Step_VOM5_HostBindings_Facade.md) | **S7** + S8 | VOM3 | 多 binding 切换无泄漏；façade 0 bytes alloc / 100 Tick |
| 6 | [Step_VOM6_Batch.md](../Plan/Step_VOM6_Batch.md) | **S9** | VOM3 | batch=64 摊销 Call ≤ 45 ns / ReadOnlyCall ≤ 35 ns |

### 文件骨架（每个 VOMx 共用，对齐 [Step9_StructFlatten.md](../Plan/Step9_StructFlatten.md) 风格）

```
# Step VOMx: <主题>
> 位置 / 状态 / 前置 / 来源(IdealAndGap §四 Sx) / 核心原则
一、本步骤的临时妥协            —— 对应 §五 风险
二、基础设施盘点                —— 已就位 vs 需新增
三、子任务总览                  —— Sub-task A..F 拓扑
Sub-task A..F                   —— [ ] 项粒度任务（每条带文件:行预期）
四、验收门禁                    —— Assert / 基准 / 兼容性 三档
五、回退策略                    —— 步骤失败时如何还原
六、关联文档更新                —— VM_Summary / IdealAndGap / Transition 同步
```

### 风险 → 主承担文件（§五 映射）

| 风险 | 主承担 | 缓解锚点 |
|------|--------|----------|
| 1 S1 切分回退面 | VOM1 | Sub-task A "渐进切分：partial → 外迁" |
| 2 CPUData Reset 成本 | VOM3 | Sub-task C "按需覆盖 + 基准守门" |
| 3 HostBindings 迁移 | VOM5 | Sub-task A "兼容垫片：全局 default binding" |
| 4 只读校验 ↔ inline | VOM2 | Sub-task D "inline 后再校验 / inline 前 readonly 提升" |
| 5 MethodHandle hot-reload | VOM1 | Sub-task B "VMDef 版本号 + 句柄失效" |

### 已决策

1. 命名前缀 `VOM` 已采纳。
2. VOM1 暂保持单文件；实施中若超 250 行再拆 `VOM1a / VOM1b`。
3. Outlook_And_Risks.md 暂不开 VOM 追踪表，统一在 [Step_VOM_Overview.md](../Plan/Step_VOM_Overview.md) 跟踪。

### 推进协议

- 按 VOM1 → VOM2 → VOM3 → (VOM4 / VOM5 / VOM6 任意顺序) 执行。
- 每个 VOMx 完成时：本文件 §三 对应 G 行标注 ✅；VM_Summary 特性区对应行 ✅；该文件状态 ✅。
- VOM11 完成后回检 §六 收敛条件 3（与 Transition 天花板无矛盾），全闭环 → §六 ✅ → VM_Summary 转向 banner 摘除（已完成）。

---

## 讨论区（进行中）

- 本文件用于转向计划推进前的差距锚点。
- 后续讨论在本节追加，达成共识后回填正文。
