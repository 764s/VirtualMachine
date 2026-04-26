# Step VOM7: CPUData / VMData 类型引入（纯加法）

> **位置**: 妥协 A 消除第 1 步（共 3 步：VOM7→VOM8→VOM9）。
> **状态**: 🟢 已完成。
> **前置**: VOM6 完成。
> **核心原则**: **纯加法**，零调用现场修改、零 SYSCALL 签名变更，2152 现有测试零修改 PASS。
> **来源**: [D_VM_ObjectModel_Transition.md](../Discussion/D_VM_ObjectModel_Transition.md) 结论 1+2；[/memories/repo/vom_compromise_a_corrected.md](../../.. /memories/repo/vom_compromise_a_corrected.md)。

---

## 0. 实施结果

- 新增 `Assets/Scripts/VM/Core/VMObjectModel.cs`，包含：
  - `MVarRegisters` fixed buffer（`unsafe fixed long Raw[ModuleVarSlots]`）
  - `CPUData` struct（IP / RegisterBase / Registers / CallStack / CleanupStack / CallStackDepth / CleanupDepth / LeafReturn* / StateFlags / ErrorFlag）
  - `VMData` struct（InstanceId / ModuleSlot / Generation / Wait* / ActiveListIndex / IsAlive / MVars）
  - `VMInstanceView` ref struct（`MemoryMarshal.CreateSpan` 聚合双 ref，零拷贝）
- 新增 `Assets/Scripts/VM/Tests/VOM7Tests.cs`：6 个用例，**11 条断言全部 PASS**
  - `MVarRegisters.Size = 64 B`（8×ModuleVarSlots）
  - `VMData` / `CPUData` blittable + pinnable
  - `VMInstanceState` 字节布局未变化（size ≥ documented min）
  - **反射字段覆盖**：所有 `VMInstanceState` 公共字段必须出现在 `CPUData ∪ VMData` —— 防止 VOM8/9 添字段时漏迁移
  - `VMInstanceView` 双向 round-trip
- 接入 `StandaloneRunner/Program.cs` 末尾 `VOM7Tests.RunAll();`
- A.5 converter helper 推迟到 VOM8（VOM7 范围内无消费场景）

### 关键决策

1. **范围收紧**：原计划"`VMInstanceState` 改写为兼容包装"会改变 snapshot 字节布局，违反"零回归"目标。改为**仅引入新类型**，`VMInstanceState` 字节级别零修改。
2. `CPUData` / `VMData` 字段顺序参考 `VMInstanceState` 但**不**强求字节匹配 —— 它们是 VOM8 的新住所，VOM9 删除旧 struct 时 snapshot 双 BufferCopy 同步重写。
3. `VMInstanceView` 用 `MemoryMarshal.CreateSpan(ref T, 1)` 实现 ref 聚合，避免 `ref` 字段（C# 11 特性 + 跨结构借用复杂度），保持代码兼容性更好。

### 实测数据（StandaloneRunner Release / net10.0）

| 套件 | 结果 |
|------|------|
| TreeWalker | 114 PASS |
| Compiler | 1302 PASS |
| Performance | 44 PASS |
| FFScript | 18 PASS |
| Debug | 51 PASS |
| Dap | 97 PASS |
| Lsp* | 全 PASS（含 LspCoverageMatrix 3） |
| VOM1-6 | 36+37+25+36+49+27 = 210 PASS |
| **VOM7 (新增)** | **11 PASS** |
| **EXIT** | **0** |

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| `VMInstanceState` 不改字节布局，暂保留原始独立 struct | snapshot 字节布局不变，零回归 | VOM8 接管调用现场后在 VOM9 删除 |
| `CPUData/VMData/VMInstanceView` 仅作为**未使用**的新类型存在 | VOM7 不动调用现场，减低验证面 | VOM8 引擎接入后取代 |
| `CPUData` 与 `VMInstanceState` 字段重复定义 | 并存期不可避免 | VOM9 删除 VMInstanceState |

---

## 二、子任务

- [x] **A.1 `MVarRegisters` fixed buffer 类型**（`unsafe fixed long Raw[VMConstants.ModuleVarSlots]`），与 `NumberRegisters` 同结构
- [x] **A.2 `CPUData` struct**（独立新类型，**不**修改 `VMInstanceState`）
  - 字段：`IP` `RegisterBase` `StateFlags` `ErrorFlag` `Registers` `CallStack` `CleanupStack` `CallStackDepth` `CleanupDepth` `LeafReturnIP` `LeafRegisterBase`
  - `[StructLayout(LayoutKind.Sequential)]` 保留 blittable
  - **本步骤不会被使用**，VOM8 才接管
- [x] **A.3 `VMData` struct**（独立新类型）
  - 字段：`InstanceId` `ModuleSlot` `Generation` `IsAlive` `ActiveListIndex` `WaitCounter` `WaitTargetInstanceId` `MVars`（MVarRegisters）
  - blittable
- [x] **A.4 `VMInstanceView` ref struct**：`ref CPUData Cpu` + `ref VMData Data`，零字段拷贝
- [~] **A.5 包装与会话双向转换 helper** —— **OBSOLETE（VOMX 重规划后废弃）**：原计划在 VOM8 引入 `VMInstanceStateConverter.ToCpuData / ToVMData / WriteBack`。VOM8 实际走的是"VMInstanceState 内嵌 `Cpu:CPUData + Data:VMData` + ref-property 透传"路线，调用点零修改即可访问 cpu/vmd 字段，**不再需要任何转换器**。VOM9 SoA split 后 wrapper 整体删除，更不需要 helper。本子项不会实施。
- [x] **A.6 类型身份测试**（`VOM7Tests.cs`）：`Marshal.SizeOf<CPUData>()` / `Marshal.SizeOf<VMData>()` 与计算期望对齐；view 迭代所有字段名与原始 VMInstanceState 集合等价（以防遗漏字段）。

---

## 三、不变量

- `VMInstanceState` **零修改**（字节布局不变）
- SYSCALL callback `(ref VMInstanceState s)` 签名 **零修改**
- `VMWorld.ExecuteInstance(ref VMInstanceState)` 签名 **零修改**
- `InstancePool.Instances[]` 类型 **不变**
- 所有 ~2152 现有测试 **零修改** PASS
- snapshot 字节布局 **零变化**

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Build | 0 errors（存在 3 条既有无关 warning） |
| Assert | 全量回归 PASS + 新增 `VOM7Tests` 11/11 PASS |
| Perf | B07 / B08 / B09 / B10 / VOM6.P01-P03 vs VOM6 基线 ±5 ns 噪声内 |
| 内存 | `sizeof(VMInstanceState)` 与 VOM6 末态一致（允许末尾 padding ±sizeof(byte)） |
| EXIT | StandaloneRunner Release 0 |

---

## 五、风险

| 风险 | 缓解 |
|------|------|
| `ref` property 透传引入额外 indirection | JIT 应内联；B07 实测验证；超 +3 ns 触发回退评估 |
| 字段顺序变化破坏 snapshot memcpy | layout test + 既有 snapshot 测试双重兜底 |
| MVar 双轨并存出现写入语义双写 | A 阶段 `vmd.MVars` 仅声明、不接管访问路径，写入仍走旧 `regs[]` |
| blittable 不再成立（`MVarRegisters` 非托管） | unsafe fixed long Raw 与 NumberRegisters 同结构，确认 blittable |

---

## 六、回退策略

VOM7 是纯加法，回退 = `git revert <commit>` 即可，无副作用。建议在 feature branch 上完成、回归通过后再合主线。

---

## 七、关联文档更新

- VM_Summary 特性区新增 VOM7 行 ⏳→🟢
- IdealAndGap §六 收敛条件 1（"S1 拆 CPUData/VMData"）状态推进至"实施中"
- Step_VOM_Overview.md 索引表追加 VOM7 行
- 本文件 ⏳→🟢
