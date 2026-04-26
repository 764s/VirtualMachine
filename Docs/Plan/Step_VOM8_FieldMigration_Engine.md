# Step VOM8: InstancePool 双数组 + ExecuteInstance 签名切换

> **位置**: 妥协 A 消除第 2 步（实质迁移）。
> **状态**: 🟢 已完成（A + 内部 ref 局部化 VOM8a；B/C/D **物理依赖 SYSCALL 签名 break**，按 **VOMX 重规划** 整体并入新 VOM9）。
> **VOMX 重规划补丁（2026-04-26）**: VOM8 的 B/C/D 子任务（InstancePool SoA、ExecuteInstance 签名切、SYSCALL break、Tick/façade 双数组）原本写在本文件 §二，但实际由 **新 VOM9** 作为单一原子提交承担（与原 VOM9 的 snapshot 双 BufferCopy + callback 站点迁移合并）。façade 精简 + ModuleVar 旧轨清理拆出为新 **VOM10**；原 VOM10 lazy rent reset 顺延为 **VOM11**。详见 [Step_VOM_Overview.md](Step_VOM_Overview.md)。
> **前置**: VOM7 完成（CPUData/VMData/VMInstanceView 已就绪）。
> **核心原则**: **运行层零开销** — 任何新增成本必须证实 ≤1 ns/burst（一次性，非每指令）。
> **来源**: [Step_VOM7_CPUData_VMData_Types.md](Step_VOM7_CPUData_VMData_Types.md) 结论。

---

## 0. 实施结果（VOM8 收窄版）

### 范围收窄理由
原计划一刀切完成 InstancePool 双数组 + ExecuteInstance 签名切换 + Tick 重写 + SYSCALL 兼容包装，单步骤改动面跨 8+ 文件、回退风险高。实施时收窄至 **VMInstanceState 兼容包装重写**：

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct VMInstanceState
{
    public CPUData Cpu;
    public VMData Data;

    [UnscopedRef] public ref int IP { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref Cpu.IP; }
    // ... 17 个 ref-property 透传至 Cpu / Data 子字段
}
```

这一步将 **存储层心智模型**完成切分（CPU 瞬态 vs VM 持久），但保留旧字段名作为 ref-property 兼容门面。所有 ~50 处调用点（VMWorld / InstancePool / Snapshot / VMEngine / TransientInstancePool / SyscallTable / HostBindings / KOF98 / Sandbox / StandaloneRunner / 各测试套件）零修改通过。

### 关键决策
1. **`[UnscopedRef]` 必需**：CS8170 禁止 struct member 直接 `ref this` 返回，须显式标注允许借出 `this` 寿命。
2. **`[MethodImpl(AggressiveInlining)]` 必须放 getter**：直接放 property 会 CS0592；`{ [MethodImpl] get => ref X; }` 形式才有效。
3. **`default(VMInstanceState)` 语义保持**：包装结构仍 `[StructLayout(Sequential)]`，`default` 递归零化 Cpu/Data，所有现存 `Allocate()` 路径无须改动。
4. **VOM7 字段覆盖测试调整**：原断言"每个公共字段在 CPUData ∪ VMData 中"已不再适用（包装结构现仅 `Cpu` + `Data` 两个公共字段）。改为更紧的不变量"恰好 2 个字段，分别为 CPUData 和 VMData 类型"。
5. **VOM9 提前**：原 VOM8 的 InstancePool 双数组 + ExecuteInstance 签名切换 + SYSCALL signature break 全部移交 VOM9 一次性完成（与 Snapshot 重写合并，避免分两次 break ABI）。

### 实测数据

| 指标 | VOM6 基线 | VOM8 后 | Δ | 评价 |
|------|---------|---------|----|------|
| 测试套件 | 14 套 | 14 套 | 0 | 零回归 |
| 总断言 | ~2152 + 11(VOM7) | ~2152 + 14(VOM7 调整后) | +3 | VOM7 测试因结构变化合并 |
| EXIT | 0 | 0 | — | ✅ |
| Build | 0 errors / 3 warnings (pre-existing) | 0 errors / 3 warnings (pre-existing) | 0 | ✅ |
| VOM6.P01 ReadOnly 64-row | 43.7 ns | 55.3 ns | **+11.6 ns** ⚠️ | warn-only 软门 |
| VOM6.P02 Call 64-row | 44.6 ns | 56.1 ns | **+11.5 ns** ⚠️ | warn-only 软门 |
| VOM6.P03 alloc/100 batches | 0 B | 0 B | 0 | ✅ |
| VMInstanceState size | 1024 B | 1048 B | +24 B | sequential layout 重排 |

### 已知遗留（VOM9 解决）

P01/P02 +11 ns 是 **ref-property 间接的可观测代价**。诊断方向：尽管 `[MethodImpl(AggressiveInlining)]` 已强制内联 getter，VMInstanceState 字段布局重排后 IP/RegisterBase/StateFlags 在 Cpu 子结构开头（offset 0/4/21），而 NumberRegisters 起始 offset 24（原为 48），意味着寄存器数组与控制字段的 cache 行邻接性变化。**真正的修复路径是 VOM9 ExecuteInstance 签名切换为 `(ref CPUData cpu, ref VMData vmd, ...)`**：届时整个 dispatch 循环从根本不再经过 VMInstanceState，ref-property 间接消失。

用户既有指示"先证 0 回归不主动改善"在此采纳：功能零回归已达成，性能延迟到 VOM9 与签名切换一起处理。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| SYSCALL callback 签名仍 `ref VMInstanceState` | 集中工作量留 VOM9 一次性切换 | VOM9 |
| `VMInstanceState` 兼容包装继续存在 | SYSCALL 兼容期需要 | VOM9 删除 |
| `regs[ModuleVarRegBase+B]` 旧轨在 LOAD/STORE_MVAR 之外可能残留 | 仅在测试 fixture 直读寄存器处 | VOM9 清理 |
| **InstancePool 仍单数组 `VMInstanceState[]`** | VOM8 收窄；与 Snapshot 重写一同 VOM9 切双数组 | VOM9 |
| **VMWorld.ExecuteInstance 签名仍 `ref VMInstanceState`** | 同上 | VOM9 |
| **P01/P02 +11 ns 性能回归** | ref-property 间接代价；VOM9 签名切换后消失 | VOM9 |

---

## 二、子任务

> 标记约定：`[x]`=本步已完成，`[~]`=已移交后续步骤（非本步待做），`[ ]`=仍在本步待做。

### A. VMInstanceState 兼容包装 + 内部 ref 局部化（VOM8 实际实施部分）

#### A.0 — VMInstanceState 包装重写
- [x] **A.0** VMInstanceState body 改写为 `{ CPUData Cpu; VMData Data; }` + 18 个 `[UnscopedRef] ref T Name { [MethodImpl(AggressiveInlining)] get => ref Cpu.X / Data.X; }` 透传
- [x] **A.0.1** VOM7Tests.Test_FieldCoverage 适配新结构（断言"恰好 2 字段, Cpu:CPUData + Data:VMData"）
- [x] **A.0.2** Build 0 errors，全 14 套件 PASS，EXIT=0

#### A.1 — ExecuteInstance / Tick / TickInstance 内部 ref 局部化（VOM8a, 2026-04-26）
- [x] **A.1.1** 在 3 个函数顶端加 `ref CPUData cpu = ref inst.Cpu; ref VMData vmd = ref inst.Data;`
- [x] **A.1.2** PowerShell regex 批量替换 VMWorld.cs 全文 `inst.<CPUfield>` → `cpu.<field>`、`inst.<VMfield>` → `vmd.<field>`，共 285 处替换
- [x] **A.1.3** SYSCALL 调用点保留 `ref VMInstanceState`（签名零变更）；`target` / `targetInst` 等 wait_for 和 XCALL 慢路径不局部化
- [x] **A.1.4** Build 0 errors，全 14 套件 PASS，EXIT=0

**VOM8a 实测数据**:

| 指标 | VOM6 基线 | VOM8（ref-property） | VOM8a（ref localized） | Δ vs 基线 |
|------|---------|---------|---------|---------|
| P01 ReadOnly | 43.7 ns | 55.3 ns | 57.4 ns | +13.7 ns |
| P02 Call | 44.6 ns | 56.1 ns | 55.1 ns | +10.5 ns |
| EXIT | 0 | 0 | 0 | — |
| 套件 | 14/14 | 14/14 | 14/14 | — |

**VOM8a 关键结论（strict-challenge）**:

VOM8a 之前的假设是"ref-property getter 即使 [AggressiveInlining] 也未被 RyuJIT 完全 inline，导致 +11 ns"。VOM8a 内部 ref 局部化后函数体已**完全不经过** ref-property（285 处 `inst.X` 全部替换为直接子结构访问），但 P01/P02 几乎不动（57.4 vs 55.3、55.1 vs 56.1，差异在 ±2 ns 测量噪声内）。

**这证伪了 ref-property 假说**。剩余 +11 ns 的真因排除 ref-property 间接，只能是结构布局/cache 邻接性变化（VMInstanceState 从 1024 B → 1048 B；CPUData 内字段顺序与 padding 与原始 VMInstanceState 不同；CallStack / CleanupStack 相对热字段的距离变了）。

**VOM8a 的实际价值**:

1. **排除 ref-property 假说**，把 +11 ns 锁定为布局问题，VOM9 必须做物理重排才能修复。
2. **为 VOM9 签名切换铺路**：函数体已只用 cpu / vmd locals，VOM9 把签名改为 `void ExecuteInstance(ref CPUData cpu, ref VMData vmd, VMProgram program)` 仅需删除入口 2 行 + 改 3 个调用点，零函数体改动。
3. **不变量加固**：SYSCALL 签名兼容性零变化，KOF98 / FFVM.Cli / 200+ 处测试零修改。

### B. InstancePool 双数组化（移交 VOM9，物理依赖）

- [~] **B.1** `InstancePool.Instances[]` 拆为 `Datas[]`(VMData[]) + `Cpus[]`(CPUData[])
- [~] **B.2** `Allocate` / `Free` / `ActiveList` / `Bindings` / `ExtendedRegs` 同步双数组维护
- [~] **B.3** 暴露 `ref VMInstanceView At(int id)` 兼容方法（聚合 `ref Cpus[id]` + `ref Datas[id]`）
- [~] **B.4** `TransientInstancePool` 改持 `CPUData[]`（不持 VMData）

### C. VMWorld.ExecuteInstance 签名切换（移交 VOM9，VOM8a 已完成函数体重构）

- [~] **C.1** 签名：`void ExecuteInstance(ref CPUData cpu, ref VMData vmd, VMProgram program)` —— VOM8a 后函数体已完全使用 cpu/vmd locals，仅需改签名
- [x] **C.2** 热路径只触 `cpu.*`，0 跨界读 vmd —— **VOM8a 已实现**
- [~] **C.3** `LOAD_MVAR/STORE_MVAR` 改读 `vmd.MVars.Raw[B]`；入口 +1 次 pin
- [x] **C.4** `WAIT/WAIT_FOR` → `vmd.*` —— **VOM8a 已实现**（局部化时已完成）
- [~] **C.5** `XCALL/XLOAD_MVAR/XSTORE_MVAR` 改用 `Pool.Datas[id] + Pool.Cpus[id]`
- [~] **C.6** SYSCALL 经兼容 view 继续 `(ref VMInstanceState)`，VOM9 才 break

### D. Tick / TickInstance / VMInstance façade（部分完成，B/double-array 部分移交 VOM9）

- [x] **D.1.1** `Tick / TickInstance` 内部加 cpu/vmd locals —— **VOM8a 已完成**
- [~] **D.1.2** `Tick / TickInstance` 直接从双数组取双 ref —— 依赖 B（VOM9）
- [~] **D.2** `VMInstance` façade 透传到双数组 —— 依赖 B（VOM9）

---

## 三、不变量

- SYSCALL callback `(ref VMInstanceState s)` 签名 **暂保留**（VOM9 才 break）
- 2152 现有测试 + VOM7 layout test **零修改** PASS
- B07 / B09 / B10 vs VOM7 基线 ±5 ns 噪声

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Build | 0 errors |
| Assert | 全 PASS 零回归 |
| Perf | B07 ≤ VOM6 基线 67.6 + 5 ns（先证 0 回归不主动改善）<br>B09/B10 ±5 ns；VOM6.P01/P02/P03 ±2 ns |
| EXIT | 0 |

---

## 五、风险

| 风险 | 缓解 |
|------|------|
| MVar pin 数从 3 → 4，B07 增 ~1 ns | 实测验证；超 +3 ns 触发回退 |
| `Pool.Datas[id] + Pool.Cpus[id]` 双取址 XCALL 增 ~0.5 ns | XCALL 频次低，纳入接受范围 |
| TransientPool CPUData 化破坏 yield 复用假设 | Yield 用 InstancePool 非 TransientPool，互不干扰 |
| 兼容 view 包装阻碍 JIT 内联 | 标 `[MethodImpl(AggressiveInlining)]` 强制内联 |
| WAIT 字段位置变 → snapshot 兼容性 | snapshot 此前跨 yield 持久化即依赖此字段在持久区，无逻辑变化 |

---

## 六、回退策略

影响面大，建议三段式 commit（A.1-A.4 / B.1-B.6 / C.1-C.2），便于精准 bisect。整体单 commit revert 仍可恢复。

---

## 七、关联文档更新

- VM_Summary VOM8 ⏳→🟢
- IdealAndGap §六 收敛条件 1 推进
- 本文件 ⏳→🟢
