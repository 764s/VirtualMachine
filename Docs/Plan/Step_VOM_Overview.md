# Step VOM Overview: VM 对象模型转向落地总入口

> **位置**: VM_Summary 串行需求列表 → 特性区 → 转向落地（S1-S9）。
> **状态**: ✅ VOM1-VOM11 已完成（VOM8 含 VOM8a 内部 ref 局部化；VOM9 Phase 1+2+4-minimal ✅，Phase 3 取消，Phase 4-full 推迟至 VOM-Tail；VOM10 Phase A 无代码改动 + Phase B.1-B.3 ModuleVar 旧轨迁移完成，B.4 Debug 断言推迟见 §八 D5，C.2 微基准复测已由 VOM11 A.6 完成并在 §八 D6 结案；**VOM11** A.1 lazy reset + MVars 安全带 + A.4 simplified poison（fill-only）+ A.5 测试 T1-T5 12/12 PASS + A.6 重测全部落地，B08-equivalent (T4 Rent+Return) **1.29 ns/op**（−85% vs 8.8 baseline）、F1=80.3/F2=78.4 ns、P03 alloc=0）。**2026-04-26 二次修订**：VOM9 perf 假说证伪（见 VOM9 §零），契约重写，新增 §八 推迟项登记。
> **来源**: [D_VM_ObjectModel_IdealAndGap §四 调整点](../Discussion/D_VM_ObjectModel_IdealAndGap.md)。
> **契约源**: [D_VM_ObjectModel_Transition.md](../Discussion/D_VM_ObjectModel_Transition.md)（五对象 / 四调用契约 / 性能天花板）。

---

## 一、目标

将当前单结构体 `VMInstanceState` + 单调用路径 `XCALL` 的实现，逐步演进为：

- **五对象**：`VMDef` / `CPUData`（含临时池）/ `VMData` / `HostBindings` / `VMInstance` (handle façade) + `InstancePool` (SoA)
- **四调用契约**（`VMEngine` 静态）：`YieldCall` / `Call` / `ReadOnlyCall` / `StaticReadOnlyCall`
- **Span<Number> ABI**：`Arguments` / `ReturnSlot` ref struct，零分配

执行完所有 VOM1-VOM11 后，妥协 A/B 全部消除，IdealAndGap 收敛条件可转 ✅，VM_Summary 转向 banner 可摘除。

---

## 二、阶段图

```
VOM1 (StateSplit + MethodHandle)
      │
      ▼
VOM2 (Arguments/ReturnSlot ABI + StaticReadOnlyCall)
      │
      ▼
VOM3 (CPUData 池 + Call/ReadOnlyCall)  ── 关键里程碑：三档调用打通
      │
      ├──────► VOM4 (YieldCall + 持久 CPUData)
      ├──────► VOM5 (HostBindings 实例化 + VMInstance façade)
      └──────► VOM6 (Batch + 摊销基准)
```

VOM4/5/6 仅依赖 VOM3，三者可在 VOM3 完成后并行/任意顺序推进。

VOM7-11 系消除 VOM6 实施期遗留的两项妥协（详见各 Step 文件 §一）：

```
VOM7 (CPUData/VMData 类型加法)                                          —— ✅ 已完成
      │
      ▼
VOM8 (VMInstanceState 兼容包装 + VOM8a 内部 ref 局部化)                  —— ✅ 已完成
      │
      ▼
VOM9 (VMInstanceView API + ExecuteInstance dual-ref + 旧 partial view 清理)  —— Phase 1+2+4-minimal ✅；Phase 3 取消；Phase 4-full 推迟 VOM-Tail
      │
      ▼
VOM10 (VMInstance façade 精简 + ModuleVar 旧轨清理)                      —— 与 SYSCALL ABI 解耦的纯清理
      │
      ▼
VOM11 (Lazy Rent Reset)                                                 —— 妥协 B 消除
```

VOM7→8→9→10→11 严格串行；每步仅在前置完成后启动。

**VOMX 重规划说明（2026-04-26）**：原 VOM9 单步包含 SoA + SYSCALL break + façade + ModuleVar + snapshot 五个修改面，回滚成本与验收可证伪性问题严重。VOM8a 实测证明 P01/P02 +11 ns 是布局成本（非 ref-property 间接），SoA + sig break 在物理上不可拆，必须独立成"原子提交 + 单一性能验收"的新 VOM9；façade 精简 + ModuleVar 清理与之解耦，拆为新 VOM10；原 VOM10 lazy reset 顺延为 VOM11。

**VOMX 二次修订（2026-04-26）**：VOM9 实施期发现 SoA "回收 +11 ns" 假说证伪 —— P01/P02 是单 transient 实例工作负载，AOS 与 SoA 触及相同 cache line，物理拆分对该指标无差别。VOM9 范围收缩为 Phase 1+2+4-minimal（VMInstanceView pass-through API + ExecuteInstance dual-ref + 旧 partial view 清理），SYSCALL ABI 硬切因 SyscallArgs legacy unsafe 路径阻断推迟至 VOM-Tail，+11 ns 真因调研剥离为 VOM9-perf。详见 §八。

---

## 三、文件索引

| # | 文件 | 涵盖 IdealAndGap §四 / 妥协 | 体量 |
|---|------|---------------------|------|
| 1 | [Step_VOM1_StateSplit.md](Step_VOM1_StateSplit.md) | S1 + S2 | 大 |
| 2 | [Step_VOM2_CallABI.md](Step_VOM2_CallABI.md) | S3 + S4 | 中 |
| 3 | [Step_VOM3_CPUDataPool.md](Step_VOM3_CPUDataPool.md) | S5 | 大（关键） |
| 4 | [Step_VOM4_YieldCall.md](Step_VOM4_YieldCall.md) | S6 | 中 |
| 5 | [Step_VOM5_HostBindings_Facade.md](Step_VOM5_HostBindings_Facade.md) | S7 + S8 | 中 |
| 6 | [Step_VOM6_Batch.md](Step_VOM6_Batch.md) | S9 | 小 |
| 7 | [Step_VOM7_CPUData_VMData_Types.md](Step_VOM7_CPUData_VMData_Types.md) | 妥协 A.1（类型加法，已完成；A.5 converter helper **OBSOLETE** — VOM8 走 ref-property 包装路线） | 小 |
| 8 | [Step_VOM8_FieldMigration_Engine.md](Step_VOM8_FieldMigration_Engine.md) | 妥协 A.2（VMInstanceState 包装 + VOM8a 内部 ref 局部化，已完成；B/C/D 整体并入新 VOM9） | 小 |
| 9 | [Step_VOM9_SoA_SyscallBreak.md](Step_VOM9_SoA_SyscallBreak.md) | 妥协 A.3（VMInstanceView pass-through API + ExecuteInstance dual-ref + 旧 partial view 清理；Phase 3 取消，Phase 4-full 推迟 VOM-Tail） | 中 |
| 10 | [Step_VOM10_Facade_ModuleVar_Cleanup.md](Step_VOM10_Facade_ModuleVar_Cleanup.md) | façade 精简 + ModuleVar 清理（与 SoA 解耦的纯清理） | 中 |
| 11 | [Step_VOM11_LazyRentReset.md](Step_VOM11_LazyRentReset.md) | 妥协 B（lazy reset） | 小 |

---

## 四、聚合验收门禁

每步通过的最低条件汇总；详细位于各文件 §四。

| 步 | Assert / 行为 | 性能门禁（[Transition §性能天花板](../Discussion/D_VM_ObjectModel_Transition.md)）|
|----|--------------|------|
| VOM1 | 现有 ~2140 Assert 全通过；MethodHandle hot-reload 失效语义验证 | MethodHandle 解析 ≤ 1 ns/次 |
| VOM2 | StaticReadOnlyCall 端到端串通；写指令在 ReadOnly 上下文被拒 | StaticReadOnlyCall 单次 ≤ 31 ns |
| VOM3 | 同 world 多实例 Call/ReadOnlyCall 互不污染；CPUData 池 0 分配 | Call ≤ 51 ns / ReadOnlyCall ≤ 41 ns / Reset ≤ 15 ns |
| VOM4 | 一次 YieldCall 挂起+恢复正确；同实例重入 reject | YieldCall ≤ 230 ns |
| VOM5 | 多 binding 切换无泄漏；façade 调用 0 bytes alloc / 100 Tick | — |
| VOM6 | batch=64 摊销 Call ≤ 45 ns / ReadOnlyCall ≤ 35 ns | 见左 |
| VOM7 | 2152 现有 Assert 零修改 PASS；新增 layout test | B07/B08/B09/B10/VOM6.P01-P03 ±5 ns |
| VOM8 | 全 PASS 零回归；VMInstanceState 改为 `{Cpu:CPUData, Data:VMData}` + ref-property 兼容门面 | 功能 0 回归达成；P01/P02 +11 ns 真因调研移交 **VOM9-perf**（见 §八 D3） |
| VOM9 | VMInstanceView 18 ref-prop pass-through API + ExecuteInstance(ref CPUData, ref VMData) + 删 VMInstanceStateViews/VW01-VW14 | **Phase 2 实测 P01=55.3 ns / P02=57.2 ns 入档为新基线**；不 gate +11 ns 回收（移交 VOM9-perf）；SYSCALL ABI 硬切移交 **VOM-Tail**（见 §八 D1） |
| VOM10 | façade 公开成员仅 `ref VMData Data` + handle 标识；ModuleVar 写入唯一面 = `vmd.MVars` | **已完成**：façade 自 VOM5/VOM7 起即最简，本步无代码改动；4 处 MVar 热路径站点（LOAD/STORE_MVAR + XLOAD/XSTORE_MVAR）已迁移至 `vmd.MVars` / `Data.MVars.Raw`，14 套件全 PASS / EXIT=0；B.4 Debug 断言推迟（§八 D5）；C.2 P01/P02 微基准复测已由 VOM11 A.6 统一落库（§八 D6 已结案） |
| VOM11 | **已完成**：A.1 Rent lazy reset（移除 ~1 KB memzero）+ `inst.Data.MVars = default;` 安全带（InvokeOnTransient + Batch）+ A.4 simplified poison（fill-only，仅 DEBUG_VM_POISON）+ A.5 VOM11Tests T1-T5（12/12 PASS） | **B08-equivalent (T4 Rent+Return) = 1.29 ns/op**（−85% vs 8.8 baseline，远超 ≥44% 目标）；F1=80.3 ns（−12.1 vs 92.4）/ F2=78.4 ns（−9.3 vs 87.7）；P01=56.1 / P02=56.0（±2 ns 零回归）；P03 alloc=0 维持 |

---

## 五、风险登记（IdealAndGap §五 → 文件主承担）

| 风险 | 主承担 | 缓解锚点 |
|------|--------|----------|
| 1 S1 切分回退面 | VOM1 | 渐进切分：partial → 外迁 |
| 2 CPUData Reset 成本 | VOM3 | 按需覆盖 + 基准守门 |
| 3 HostBindings 迁移 | VOM5 | 兼容垫片：global default binding |
| 4 只读校验 ↔ inline | VOM2 | inline 后再校验 / inline 前 readonly 提升 |
| 5 MethodHandle hot-reload | VOM1 | VMDef 版本号 + 句柄失效 |

---

## 六、关联文档同步

每个 VOMx 完成后必须同步：

- [VM_Summary.md](../VM_Summary.md) 串行需求列表 → 特性区 → 对应行 ⏳ → ✅
- [D_VM_ObjectModel_IdealAndGap.md](../Discussion/D_VM_ObjectModel_IdealAndGap.md) §三 对应 G 行标注 ✅
- 全部完成后：IdealAndGap §六 收敛条件 3 验证一致 → 转 ✅；VM_Summary 转向 banner 摘除
- **推迟项 / 未立项调研项的状态变化必须反向同步至本文件 §八**（避免散落在各 Step 文件中失去全局视图）

---

## 八、推迟项与未立项调研登记

> 本节是 VOM 套件中所有 deferred / 未立项条目的唯一权威登记。任何 Step 文件提及"推迟"或"移交"必须对应到本表的 D-ID。新增条目以 D{n+1} 递增。

| ID | 名称 | 阻断原因 / 根本原因 | 移交目标 | 状态 |
|----|------|---------------------|----------|------|
| **D1** | SYSCALL ABI 硬切：`SyscallHandler(ref VMInstanceState)` → `(ref VMInstanceView)` + VMInstanceState wrapper 退役 SYSCALL 表面 | `SyscallArgs` legacy 路径用 `unsafe VMInstanceState* _state`（Unity C# 9 / netstandard2.1），而 `VMInstanceView` 是含 `Span` 的 ref struct 不可取地址。需先决策方案 A（升级 Unity / 弃 legacy）/ B（改 SyscallHandler 用 `(ref CPUData, ref VMData)`，VOM9 Phase 1 投入失效）/ C（接受为长期债务）。推荐 C。 | **VOM-Tail**（未立项，待 Unity 升级窗口或方案决策） | 推迟 |
| **D2** | InstancePool SoA split（`Cpus[]` + `Datas[]` 物理分开） | 假说"P01/P02 通过 SoA 回收 +11 ns"已证伪。P01/P02 是单 transient 实例工作负载，AOS 与 SoA 触及相同 cache line。多实例 Tick 受益场景未被 gated。 | **取消**（如 VOM9-perf 调研得出多实例 Tick 强动机可独立立项 VOM12-SoA） | 取消 |
| **D3** | +11 ns 性能债真因调研（VOM8 引入，VOM8a 证实为布局成本而非 ref-property 间接） | 真因疑似 (a) JIT 对 nested CPUData/VMData 结构 codegen / inlining 启发；(b) CPUData 内部字段顺序与 VOM6 flat 布局某 hot 字段 cache 偏移差异。需独立调研。 | **VOM9-perf**（未立项调研，无固定 deliverable） | 调研待启动 |
| **D4** | VMInstanceState 类型本身退役 | 仍是 InstancePool 存储 / ScriptDebugger / DapServer / Tests 的工作类型；其 SYSCALL ABI 表面身份与 D1 绑定。D1 解锁前不退役。 | 绑定 D1 | 推迟 |
| **D5** | VOM10 B.4：Debug 断言 `Registers.Raw[ModuleVarRegBase..]` 不再被任何热路径写入 | VOM10 已通过全工程 grep 验证 4 处迁移点之外无写入残留；新增 `[Conditional("DEBUG_VM")]` 断言收益边际，主要价值是回归保护。Compiler 中 `ModuleVarRegBase` 仍作虚拟寄存器空间分隔常量使用，断言需精确区分"指令操作数计算" vs "实际 register file 写入"。 | **VOM-Tail** 任务列表（与 D1 选 A/B 任一执行时合并实施一次性扫描） | 推迟 |
| **D6** | VOM10 C.2：P01-P03 / B07 / B09 / B10 微基准复测，记录到 benchmark_results.md（VOM10 列） | 原计划推迟至 VOM-Tail 统一复测；后续已在 VOM11 A.6 一并完成并落库（含 P01/P02/P03 与 B09/B10、F1/F2/B08-equivalent）。 | **VOM11 A.6**（已完成） | 已完成 |

### D1 附录：VOM-Tail 立项时的可执行细节

> 本附录从原 VOM9 Phase 4-full 任务列表迁入，作为 D1 解锁后的现成任务清单。VOM-Tail 立项时直接消费本节，无需重新设计。

**前置决策**（必须三选一）：

- **方案 A**：放弃 legacy path 支持。要求 Unity 项目升至 Unity 6 / .NET Standard 2.1+ 且开启 C# 11，或 KOF98 切换到 StandaloneRunner 模式。
- **方案 B**：新增 `SyscallArgs(ref CPUData cpu, ref VMData vmd)` 第三构造函数 + `unsafe CPUData* _cpu; VMData* _data;` 字段，避开 ref struct 限制。代价：新 SyscallHandler delegate 签名 `(ref CPUData, ref VMData)`，VOM9 Phase 1 的 VMInstanceView pass-through API 失去用武之地，lambda body 必须改 `s.Registers` → `cpu.Registers` 等（~150 站点全量）。
- **方案 C**（推荐）：保留 SyscallHandler ABI 不变，接受为长期债务。VOM-Tail 不立项，Overview §八 D1 / D4 长期保留为"已知接受项"。

**选 A 后的任务清单**（选 B 需重新设计任务列表）：

- [ ] **T1** `SyscallTable.SyscallHandler` 签名：`delegate void(ref VMInstanceState, ...)` → `delegate void(ref VMInstanceView, ...)`
- [ ] **T2** `SyscallArgs` 双路径分别适配（modern 路径直接换 `ref VMInstanceView _state`；legacy 路径删除）
- [ ] **T3** ExecuteInstance SYSCALL 派发处构造 `new VMInstanceView(ref cpu, ref vmd)` 传 handler；删除 VOM9 Phase 2 留下的 `Unsafe.As<CPUData, VMInstanceState>` shim（[VMWorld.cs](../../Assets/Scripts/VM/Core/VMWorld.cs) 派发位点）
- [ ] **T4** PowerShell 全工程批量正则替换 `\((\s*)ref VMInstanceState (\w+)\s*\)` → `($1ref VMInstanceView $2)` 在 KOF98 / Sandbox / src/FFVM.Cli / Assets/Scripts/VM/Tests / StandaloneRunner / VMInstance façade
- [ ] **T5** ScriptDebugger.FindStepOutIP / GetVariables / GetCallStack 三处 `ref VMInstanceState` 签名 → 推荐**保留**（这些不在 SYSCALL ABI 表面，无需改造）
- [ ] **T6** **保留** `VMInstanceState` 类型本身（D4 不退役类型，仅退役其作为 SYSCALL ABI 表面的身份）—— 仍是 InstancePool 存储和 ScriptDebugger / DapServer / Tests 的工作类型
- [ ] **T7** 验收：build 0 errors / 全 PASS / EXIT=0 / SYSCALL handler delegate 类型为 `(ref VMInstanceView, ...)` / 性能维持 VOM9 Phase 2 水平（不要求回收 +11 ns，该项归 VOM9-perf）

---

## 讨论区

- 命名前缀 VOM 已采纳（`Virtual-machine Object Model`）。
- 如某 VOM 文件实施中行数超 250，按 `VOMxa / VOMxb` 拆分（VOM1 优先评估）。
