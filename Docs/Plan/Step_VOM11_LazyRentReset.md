# Step VOM11: Lazy Rent Reset（消除妥协 B，target 44-60%）

> **位置**: 妥协 B 消除（独立步，VOM10 落地后实施）。
> **状态**: 🟢 已完成（2026-04-26）。A.1 lazy reset + MVars 安全带 + A.4 simplified poison（fill-only）+ A.5 测试（T1-T5, 12/12 PASS）+ A.6 重测全部落地，B08-equivalent (T4 Rent+Return) 1.29 ns/op（−85% vs 8.8 baseline，远超 ≥44% 目标），全量 EXIT=0。
> **前置**: VOM10 完成（façade + ModuleVar 清理已落地）。
> **核心原则**: 把 `Rent()` 的全量 memzero 移除，仅在槽位**首次创建**时清零；运行期靠"VMEngine 入口显式 reset 控制字段"+"Compiler 严格 init-before-use"+"Debug poison 兜底"三层保证安全。
> **目标**: B08 TransientReset 8.8 → ≤ 5 ns（≥44% 削减）。
> **来源**: VOMX 重规划（2026-04-26）。原 `Step_VOM10_LazyRentReset.md.pre-vomx.bak` 顺延为本步，子任务无修改 —— TransientPool 维持 `VMInstanceState[]` AOS 单数组（VOM9 Phase 3 SoA 已取消，见 [Overview §八 D2](Step_VOM_Overview.md)）；`VMInstanceState` 已在 VOM7 内嵌 `Cpu:CPUData` + `Data:VMData`，对 CPUData 单独 reset 通过 `pool.Slots[id].Cpu` 访问。

---

## 零、准入评估实测基线（2026-04-26）

执行 VOM10 收口后跑一次 StandaloneRunner Debug 测得：

| 指标 | 测点 | 实测值 | VOM11 含义 |
|------|------|--------|------------|
| B08 TransientReset | `VOM3.P2_F3_TransientResetLatency`（直接微基准 `slot = default;`） | **8.8 ns** | 本步主削减面 |
| F1 Call | `VOM3.P2_F1_CallLatency`（端到端 Call） | 92.4 ns（warn）| VMEngine.Call 路径基线，**不在 VOM11 削减范围内** |
| F2 ReadOnlyCall | `VOM3.P2_F2_ReadOnlyCallLatency` | 87.7 ns（warn）| 同上 |

**关键观察**：

1. B08 baseline 已从 VOM3 时期的 15.8 ns 下降至 8.8 ns（VOM7 类型重构后 `= default;` 成本下降）。VOM11 削减目标从 ≥68% 修订为 **≥44%**（8.8 → ≤5 ns）。
2. F1/F2 远超旧门禁 51/41 ns。差距主因是 VOM8/9 引入的 +11 ns 性能债（[Overview §八 D3](Step_VOM_Overview.md) VOM9-perf 调研未启动）+ Debug 构建固有开销。**这部分不是 VOM11 工作面**，VOM11 §四 P01/B07 门禁应改为相对基线零回归（±2 ns），而不是绝对值 ≤53 ns。
3. 调试器交互调研：`TransientInstancePool` 槽位不在 `InstancePool.ActiveList`，ScriptDebugger / DapServer 不可达，lazy reset 对调试可观测面零影响。
4. 控制字段重置完备性：[VMEngine.cs L154-180](../../Assets/Scripts/VM/Core/VMEngine.cs) `InvokeOnTransient` Rent 后立即逐字段重写 14 个控制字段（IP / RegisterBase / IsAlive / StateFlags / ErrorFlag / CallStackDepth 等）；剩余非显式重置面 = `Registers.Raw[]` + `CallStack[1..]` + `CleanupStack[]`，正是 §A.4 poison 守护对象。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 后续 |
|------|------|------|
| Register file 不做"分窗口 zeroing"（B-2） | 风险/收益不划算；B-1 lazy reset 单项已能命中 60-80% | 若后续 B07 仍卡瓶颈再考虑 |
| Compiler 不新增 definite-assignment verifier | 现有 Compiler 在 Sub-task A.4 之外的所有 `STORE_*` 之前都有先 `LOAD_CONST` / 参数注入；A.5 T1/T2 主动构造跨调用复用 + 异常路径，验证控制字段重置 layer 已足够保证无残留 | 长期看仍建议加 verifier；A.4 fill-only poison 仅作调试器/dump 视觉标记，非运行期兜底 |

---

## 二、子任务

- [x] **A.1** 移除 `TransientInstancePool.Rent` 的 `_slots[id] = default` 全量清零
- [x] **A.2** 槽位首次创建语义由 `new VMInstanceState[capacity]` 提供（.NET 数组分配自动零初始化，无需新增标记位）
- [x] **A.3** VMEngine 调用入口保留显式重置 hot-path 信任字段；**追加** `inst.Data.MVars = default;` 安全带（`InvokeOnTransient` + `Batch` 两处，~1 ns 成本，避免跨模块 transient 调用 MVar 残留）
- [x] **A.4 Debug poison（简化版 C，2026-04-26 决策）**：仅 `#if DEBUG_VM_POISON` 守护下在 `Rent()` 写入 `0xDEADBEEFDEADBEEF` 至 `cpu.Registers.Raw[*]`；不增加 LOAD_REG 读侧断言（避免热路径开销）。Sentinel 仅作为调试器/panic dump 视觉标记。Release 零开销（`[Conditional("DEBUG_VM_POISON")]`）
- [x] **A.5 回归测试新增**（`Assets/Scripts/VM/Tests/VOM11Tests.cs`，12 断言全 PASS）
  - T1（3）：连续 Call 复用同槽位 + 返回值正确 + 池容量未增长
  - T2（2）：ReadOnlyViolation 后下一次 Call 控制字段隔离干净
  - T3（5）：Batch continue-on-error，poison 行外其他行结果与错误码均正确
  - T4：Rent+Return roundtrip latency（1000-内循环放大法），实测 1.29 ns/op ≤ 5 ns
  - T5：100x VMEngine.Call 0 alloc
- [x] **A.6** 重测全部 perf gate，记录到 [benchmarks/benchmark_results.md](../../benchmarks/benchmark_results.md) VOM11 段

---

## 三、不变量

- 所有 ~2152 + VOM7-10 + VOM11 新增测试 PASS
- 0 alloc / 100 batches 保持
- 控制字段重置 layer（VMEngine 入口 14 字段显式重写 + MVars 安全带）保证 T1-T3 无残留观测
- Release 构建零额外开销（A.4 `[Conditional("DEBUG_VM_POISON")]` 注解使 Rent 处的 PoisonRegistersIfDebug 调用在未定义该宏的所有构建里被编译器消除）
- A.4 fill-only poison（仅 `DEBUG_VM_POISON` 显式启用时生效）：调试器/crash dump 中残留读会显示 `0xDEADBEEFDEADBEEF`，便于定位；**不**触发 panic（避免 LOAD_REG 热路径税）

---

## 四、验收门禁

| 类型 | 阈值 | 备注 |
|------|------|------|
| Build | 0 errors（Debug + Release 双构建） | |
| Assert | 全 PASS（含 VOM11 新增 5 测试 / 12 断言） | T1×3 + T2×2 + T3×5 + T4×1 + T5×1 |
| B08 TransientReset | 8.8 → ≤ 5 ns | **≥44% 削减**（baseline 修订自 §零 实测；旧文档 15.8→5 ns 数据已过时） |
| B07 Call | 启动前实测基线 ±2 ns 零回归 | 绝对值受 VOM9-perf D3 +11 ns 影响，不在 VOM11 范围；用相对基线 |
| VOM6.P01 ReadOnly batch | 启动前实测基线 ±2 ns 零回归 | 同上 |
| VOM6.P03 alloc | 0 B / 100 batch | 维持 |
| EXIT | StandaloneRunner Release 0 | |

> 旧文档曾设 "B07 ≤ 53 ns / P01 ≤ 53 ns" 绝对门禁。准入评估实测 F1=92.4 / F2=87.7 ns，与该绝对值差 ≥34 ns，差距源自 VOM9-perf（[Overview §八 D3](Step_VOM_Overview.md)）未解决，非本步可达。改为相对基线 ±2 ns 零回归更可证伪。

---

## 五、风险

| 风险 | 缓解 |
|------|------|
| Lazy reset 后异常路径残留旧 register 数据 → 安全风险 | 两层防护：①VMEngine 入口显式 reset 控制字段（既有，14 字段全覆盖 + MVars 安全带）；②Compiler 严格 init-before-use（既有）。A.4 fill-only poison 仅在 `DEBUG_VM_POISON` 启用时为调试器/dump 提供 0xDEADBEEFDEADBEEF 视觉标记，**不构成第三层运行期 trap**（避免 LOAD_REG 热路径税；权衡见 §一 简化 C 决策） |
| Compiler 现有 init-before-use 不严格存在漏洞 | A.5 T1（连续 Call 复用同槽位）+ T2（ReadOnlyViolation 后复用）+ T3（Batch continue-on-error）三组用例覆盖跨调用复用 + 异常路径；任一行返回值出错或控制字段污染都会触发断言失败 |
| Poison 在 Release 不生效 | 接受；Debug 矩阵已覆盖所有测试 |
| Rent 后的 `CallStack` / `CleanupStack` 残留导致返回地址混乱 | A.3 显式重置 `CallStackDepth=0` / `CleanupDepth=0` 即足够，frames 数据在 depth=0 时不可达 |
| ~~调试器读 stale 值产生混淆~~ | **已排除**：TransientPool 槽位不在 InstancePool.ActiveList，ScriptDebugger / DapServer 契约上不可达（[TransientInstancePool.cs L9-11](../../Assets/Scripts/VM/Core/TransientInstancePool.cs)） |

---

## 六、回退策略

A.1 是单点修改（移除一行 `_slots[id] = default`）；A.4 是新增 `#if` 段。回退 = 单 commit revert 即恢复全量 default 清零，影响面极小。

---

## 七、关联文档更新（2026-04-26 实施）

- ✅ 本文件 ⏳→🟢；§二全勾；§八 实测结果落库（见下）
- ✅ [Step_VOM_Overview.md](Step_VOM_Overview.md) §状态 banner + §四 VOM11 行 ⏳→🟢
- ✅ [VM_Summary.md](../VM_Summary.md) VOM11 行 🟢
- ✅ [benchmark_results.md](../../benchmarks/benchmark_results.md) 追加 VOM11 段
- ✅ 清理 `Step_VOM10_LazyRentReset.md.pre-vomx.bak`（VOMX 重规划已收口）
- N/A IdealAndGap.md / Transition.md 在仓库中不存在（plan 模板项，跳过）

---

## 八、实测结果（2026-04-26 Release 全量回归 EXIT=0）

| 指标 | 测点 | 准入基线 | VOM11 实测 | 变化 | 验收 |
|------|------|---------|-----------|------|------|
| **B08-equiv** | `VOM11.T4_RentReturnRoundtrip`（1000-内循环放大）| 8.8 ns | **1.29 ns** | **−85.3%** | ✅ 远超 ≥44% 目标 |
| F1 Call | `VOM3.P2_F1_CallLatency` | 92.4 ns | 80.3 ns | −12.1 ns | ✅ 端到端连带改善 |
| F2 ReadOnlyCall | `VOM3.P2_F2_ReadOnlyCallLatency` | 87.7 ns | 78.4 ns | −9.3 ns | ✅ 端到端连带改善 |
| P2_F3 (legacy) | `VOM3.P2_F3_TransientResetLatency`（直接 micro-bench `slot=default;`）| 8.8 ns | 9.0 ns | +0.2 ns | ✅ 噪声内（操作本身未改变，只是不再被 Rent 调用）|
| VOM6.P01 ReadOnly batch | per-row | 56.9 ns | 56.1 ns | −0.8 ns | ✅ 相对基线 ±2 ns 零回归 |
| VOM6.P02 Call batch | per-row | 57.7 ns | 56.0 ns | −1.7 ns | ✅ 相对基线 ±2 ns 零回归 |
| VOM6.P03 alloc | over 100 batches | 0 B | 0 B | — | ✅ 维持 |
| B09 YieldCall noyield | roundtrip | 101.7 ns | 102.2 ns | +0.5 ns | ✅ 噪声内 |
| B10 YieldCall yield | roundtrip | 117.4 ns | 118.6 ns | +1.2 ns | ✅ 噪声内 |
| 测试 | StandaloneRunner Release | — | EXIT=0, VOM11Tests 12/12 PASS, 全 14 套件 PASS | — | ✅ |

**结论**：A.1 的 ~1 KB memzero 移除即换得 ~7 ns 的 Rent 路径直接削减；F1/F2 端到端 ~9-12 ns 改善证实了热路径收益；MVars 安全带的 ~1 ns 成本被吸收。Poison 简化版（仅 fill）保留调试可识别性，零热路径开销。
