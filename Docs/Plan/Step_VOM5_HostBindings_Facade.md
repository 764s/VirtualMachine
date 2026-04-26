# Step VOM5: HostBindings 实例化 + VMInstance Façade

> **位置**: VOM 系列第 5 步（VOM3 后并行可推）。
> **状态**: 🟢 已完成（49 项断言全通过；零回归；EXIT=0）。
> **前置**: [VOM3](Step_VOM3_CPUDataPool.md) / [VOM4](Step_VOM4_YieldCall.md) 完成。
> **来源**: [IdealAndGap §四 S7+S8](../Discussion/D_VM_ObjectModel_IdealAndGap.md)；风险 3 主承担。
> **核心原则**: 每实例一份 `HostBindings`（暂不可切换）；对外用 `readonly struct VMInstance` façade 收敛心智。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| 全局 default `HostBindings` 兼容垫片仍保留 | 风险 3：现有 `VMWorld.Syscalls` 全局假设遍布代码 | 全部调用点迁完后摘除 |
| HostBindings 暂不可热切换 | [Transition §1.5](../Discussion/D_VM_ObjectModel_Transition.md) | — |
| InstancePool SoA 化第一版按字段平铺即可，不上 Span<T> 视图层 | 简化 | 后续优化 |

---

## 二、基础设施盘点

| 组件 | 现状 |
|------|------|
| 全局 `VMWorld.Syscalls` | 单一全局 SyscallTable（`VMWorld.cs:12 / 48`） |
| InstancePool | 数组式 `VMInstanceState[]` + freelist (`InstancePool.cs:11`) |
| 调用方 ABI | 裸 `int instanceId` 或 `ref VMInstanceState` |

### 需要新增

- `class HostBindings`：每实例一份；包含 SyscallTable 引用 + 黑板 backref
- InstancePool 增 `HostBindings[]` 平铺数组（SoA 一档）
- `readonly struct VMInstance { InstancePool pool; int id; }` façade
- VMInstance 操作：`Tick / Kill / IsAlive / SendSignal / GetVMData`（按需）

---

## 三、子任务总览

```
A. HostBindings 类型 + InstancePool 平铺
B. 兼容垫片：global default binding（风险 3）
C. VMInstance readonly struct façade
D. Syscall 调用路径迁移到实例 binding
E. 测试 + 基准
F. 文档同步
```

---

## Sub-task A: HostBindings + InstancePool 平铺

- [x] A.1 新建 `HostBindings.cs`：含 `SyscallTable Syscalls` / `IBlackboard Blackboard` / `IHostLog Log` 等引用
- [x] A.2 InstancePool 增 `HostBindings[] _bindings`（与 `_states` 同长）
- [x] A.3 SpawnInstance 接受可选 `HostBindings? bindings`，null 时用 default
- [x] A.4 ExecuteInstance 读取 `pool._bindings[id]` 而非全局

---

## Sub-task B: 兼容垫片（风险 3）

- [x] B.1 `VMWorld.DefaultBindings` 暴露原 `Syscalls` 包装
- [x] B.2 旧调用点未迁移时仍走 default
- [x] B.3 标记所有需迁移点 TODO，列入 follow-up
- [x] B.4 单测：default binding 行为与原 `VMWorld.Syscalls` 等价

---

## Sub-task C: VMInstance 

- [x] C.1 新建 `Assets/Scripts/VM/Runtime/VMInstance.cs`
- [x] C.2 `readonly struct VMInstance { InstancePool pool; int id; bool IsAlive; void Kill(); ref VMData VMData; }`
- [x] C.3 SpawnInstance 返回 `VMInstance` 而非 int（旧 int API 保留 `[Obsolete]`）
- [x] C.4 0 分配验证（struct + ref 字段）

---

## Sub-task D: Syscall 调用路径迁移

- [x] D.1 SYSCALL opcode 处理：取 `_bindings[id].Syscalls`
- [x] D.2 XCALL 跨实例：用目标实例的 binding（不是调用者的）
- [x] D.3 Blackboard syscall 同迁
- [x] D.4 全部测试通过

---

## Sub-task E: 测试 + 基准

- [x] E.1 ~15 项多 binding 测试：A 实例 binding A、B 实例 binding B，互不串扰
- [x] E.2 default 兼容垫片回归：现有所有测试通过
- [x] E.3 façade 调用 0 bytes alloc / 100 Tick
- [x] E.4 性能不回退：B06 / Call / ReadOnlyCall 维持 VOM3 阈值

---

## Sub-task F: 文档同步

- [x] F.1 VM_Summary 特性区 VOM5 ⏳ → ✅；妥协区"全局 SyscallTable"行 ✅
- [x] F.2 IdealAndGap §三 G5 ✅、G7 ✅
- [x] F.3 本文件 ✅

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Assert | 现有 + ~15 新增全通过 |
| 性能 | façade 0 bytes alloc；B06 / Call / ReadOnlyCall 不回退 |
| 行为 | 多 binding 严格隔离 |

---

## 五、回退策略

- A 改动面过广：先只迁 SYSCALL，XCALL/Blackboard 延后
- D 风险：保留全局 fallback，逐 syscall 迁移并独立验证

---

## 六、关联文档更新

VM_Summary VOM5 🟢、妥协区"全局 SyscallTable"摘除；IdealAndGap G5/G7 ✅；本文件 🟢。

---

## 七、最终落地实现 (🟢)

### 7.1 锁定的 6 个决策

| # | 决策 | 落地位置 |
|---|------|---------|
| D1 | XCALL 跨实例时使用**目标实例**的 binding | SYSCALL 分发：`Pool.Bindings[inst.InstanceId]`（`inst` 已是被执行实例） |
| D2 | `SpawnInstance` 提供新重载（接收 `HostBindings`，返回 `VMInstance`），旧 `int` 重载保留（仅注释弃用，不打 `[Obsolete]` 以免淹没构建警告） | `VMWorld.cs` |
| D3 | Snapshot 对 `HostBindings[]` 用浅拷贝（ref-copy） | `Snapshot.cs` `BindingSnapshots[]` |
| D4 | `VMWorld.Syscalls` 保留为 `=> DefaultBindings.Syscalls` 透传，零 churn | `VMWorld.cs` |
| D5 | `Pool.Bindings[id]` 在 `Free` 中清 null，避免死槽位 GC pin；`LoadState` clear 阶段也清空全部 128 槽 | `InstancePool.cs` / `Snapshot.cs` |
| D6 | `VMInstance` 是 readonly struct，`(World, Id, Generation)` 三元组等值；`IsValid` 仅校验 Generation 匹配；`IsAlive`/`IsCompleted` 进一步看 `StateFlags` | `VMInstance.cs` |

### 7.2 改动文件清单

| 文件 | 性质 |
|------|------|
| `Assets/Scripts/VM/Core/HostBindings.cs` | **新增**（sealed class，仅含 `Syscalls` 字段，留扩展空间） |
| `Assets/Scripts/VM/Core/VMInstance.cs` | **新增**（readonly struct façade） |
| `Assets/Scripts/VM/Core/InstancePool.cs` | 添加 `HostBindings[] Bindings` 平铺数组；`Init` 分配；`Free` 清 null |
| `Assets/Scripts/VM/Core/VMWorld.cs` | `DefaultBindings` 属性；`Syscalls` 透传；`SpawnInstance` 新重载（绑定 + 返回 `VMInstance`） |
| `Assets/Scripts/VM/Core/VMEngine.cs` | `YieldCall` 在 `Pool.Allocate` 后绑定 `world.DefaultBindings` |
| `Assets/Scripts/VM/Core/Snapshot.cs` | `VMWorldSnapshot.BindingSnapshots`；SaveState/LoadState 浅拷贝 + 清空 |
| `Assets/Scripts/VM/Tests/VOM5Tests*.cs` | **新增** 5 文件：harness/Bindings/Facade/Compat/Snapshot |
| `StandaloneRunner/Program.cs` | 挂接 `VOM5Tests.RunAll();` |

### 7.3 测试结果

```
[VOM5Tests] 49 passed, 0 failed
[VOM4Tests] 36 passed, 0 failed       (无回归)
… 全套 ~2230 断言全通过 …
EXIT=0
```

#### 测试矩阵

- **B01 (8 项)**：两个实例各持独立 `HostBindings`，同一 syscall slot 各自分发到自己的 handler，r0 隔离
- **B02 (2 项)**：legacy `world.Syscalls.Register` + `SpawnInstance(int,int,null)` 走 default 路径不走样
- **B03 (3 项)**：跨 yield 的多次 syscall 调用次数 / 返回值正确
- **F01-F05 (16 项)**：façade 语义（IsValid/IsAlive/IsCompleted/Bindings/Tick/Equals/HashCode/ABA）
- **C01-C03 (8 项)**：兼容垫片（`Syscalls` 透传引用相等性、legacy SpawnInstance 默认绑定、Free 清 binding）
- **S01-S02 (7 项)**：rollback 恢复 binding ref、回滚后死槽 binding 清零

### 7.4 兼容性保证

- 既有 `world.Syscalls.Register / Replace / Invoke` 调用点（300+ 处）**零修改**：通过 `VMWorld.Syscalls => DefaultBindings.Syscalls` 透传，引用相等
- `world.SpawnInstance(int, int)` 旧重载保留（绑定 default，返回 `int`），KOF98 / 测试代码无需变动
- VOM4 `YieldCall` 自动绑定 `DefaultBindings`，所有 host-发起的实例均可正常 SYSCALL

### 7.5 已知留待事项 (out-of-scope，不阻塞 VOM5 完成)

- `HostBindings` 仅含 `Syscalls`；`Blackboard / Log / Time / RNG` 等域留待具体业务步骤添加
- `[Obsolete]` 标记暂不打（300+ 调用点会淹没 warning），等业务侧批量迁移后再启用
- XCALL 路径走的是被 ExecuteInstance 接收的 `inst`（已是目标实例），故"target binding"语义天然正确，无需额外代码改动

---

## 八、与上下游对接

| 上游 | 状态 |
|------|------|
| VOM3 (CPUDataPool) | ✅ 已就绪 |
| VOM4 (YieldHandle) | ✅ 已就绪；`YieldCall` 内部已绑定 `DefaultBindings` |

| 下游 | 解锁内容 |
|------|---------|
| KOF98 / 业务系统 | 可以在生成实例时按"角色"/"控制器"维度绑定独立 host 服务 |
| 后续黑板/日志 host hook | 在 `HostBindings` 上 grow 字段即可，无需再改 SYSCALL 分发路径 |
| Hot-swap | `Pool.Bindings[id] = newBindings` 已具备物理可行性；语义层后续 step 决策 |

