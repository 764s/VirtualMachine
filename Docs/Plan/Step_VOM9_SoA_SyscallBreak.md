# Step VOM9: SYSCALL ABI Cleanup（VOM8 wrapper 退役）

> **位置**: 妥协 A 消除 — ABI 基础设施完成部分。
> **状态**: ✅ 已完成（Phase 1 + Phase 2 + Phase 4-minimal）。Phase 4-full SYSCALL ABI 硬切已**接受为长期债务**（VOM-Tail D1=C，2026-04-26 决策），不再立项；详见 [Step_VOM_Overview §八 D1](Step_VOM_Overview.md)。VMInstanceView pass-through API 保留为未来重启资产。
> **前置**: VOM8 完成（VMInstanceState 包装 + VOM8a 内部 ref 局部化）。
> **来源**: VOMX 重规划（2026-04-26）。VOM9 perf 假说证伪后二次修订（见 §零）。

---

## 零、关于 +11ns 长期债务的修订（2026-04-26 二次修订）

**原假说（已弃）**：plan §一 曾断言 "InstancePool SoA split (Cpus[]/Datas[] 物理分开) 可在 P01/P02 回收 +11ns"。

**证伪依据**：
1. P01 ReadOnlyBatch / P02 CallBatch 是**单 transient instance** 工作负载（Batch 仅 Rent 1 个 slot，64 次 invocation 复用同一槽位）。
2. 在单实例情况下，AOS（`VMInstanceState[]`）与 SoA（`Cpus[]`+`Datas[]`）触及完全相同数量的 cache line — 内存布局对单实例 hot loop 无差别。
3. SoA 的真正受益场景是**多活实例 Tick** 中 `Datas[]` 紧密打包，但这不是被 gated 的指标。
4. +11ns 真因更可能是 (a) JIT 对 nested CPUData/VMData 结构的 codegen / inlining 启发与 VOM6 flat 布局不同；或 (b) CPUData 内部具体字段顺序与 VOM6 原 flat 布局某个 hot 字段的 cache 偏移差异。**这两个根因都与 SoA 拆分**无关**。**

**重定向**：
- VOM9 不再 gate P01/P02 ≤ baseline+2ns。+11ns 作为 VOM8 wrapper 引入的长期债务暂留，归入独立调研项 **VOM9-perf**（待立条目，见 [Step_VOM_Overview.md](Step_VOM_Overview.md)）。
- VOM9 后续目标缩减为：完成 SYSCALL ABI 切换、退役 `VMInstanceState` wrapper、清理 VMInstanceStateViews 旧 partial view。
- 物理 SoA split 是否值得做：留待 VOM9-perf 调研得出 +11ns 真因后再决定（若真因与 SoA 无关则不做；若多实例 Tick 有独立动机则单独立项）。

---

## 一、交付物（已完成）

| Phase | 内容 | 验收 |
|---|---|---|
| **Phase 1 ✅** | `VMInstanceView readonly ref struct` 加入 18 个 pass-through ref-property（VMData 侧 7 + CPUData 侧 11，每个 `[MethodImpl(AggressiveInlining)]`）。意义：SYSCALL ABI 切换时 lambda body 内 `s.Registers / s.IP / s.WaitCounter` 等无需修改（为 VOM-Tail D1 保留） | build 0 / 14 套件 PASS / EXIT=0 |
| **Phase 2 ✅** | `ExecuteInstance(ref VMInstanceState inst)` → `ExecuteInstance(ref CPUData cpu, ref VMData vmd)`；5 callsite 迁移（VMWorld TickInstance/Tick/XCALL recursion + VMEngine InvokeOnTransient/Batch）；SYSCALL 派发处通过 `fixed (CPUData* p = &cpu) { (VMInstanceState*)p }` 指针重解释恢复 wrapper ref 传旧 ABI（依赖 `[StructLayout(Sequential)]` 首字段不变量；VOM-Tail D7 已替换原 `Unsafe.As` shim 以兼容 Unity netstandard2.1，D1=C 后 shim 永久保留） | build 0 / 14 套件 PASS / EXIT=0 / P01=55.3 ns / P02=57.2 ns |
| **Phase 4-minimal ✅** | 删除 `VMInstanceStateViews.cs`（VOM1 时期 `CPUDataView`/`VMDataView`/`AsCPU`/`AsVM` 死代码）；删除 `VOM1Tests.cs` VW01-VW14 共 14 条对应 assertion | build 0 / 全 PASS / EXIT=0 |

- **Phase 3**（InstancePool SoA split）：整体取消。P01/P02 是单实例工作负载，AOS vs SoA 触及相同 cache line，假说证伪（见 §零）。如 VOM9-perf 调研得出多实例 Tick 强动机可独立立项 VOM12-SoA。
- **Phase 4-full**（SYSCALL ABI 硬切）：已接受为长期债务（VOM-Tail D1=C，2026-04-26 决策）。任务列表见 [Step_VOM_Overview §八 D1](Step_VOM_Overview.md)（已废弃，仅作历史保留）。

---

## 二、实测基线

| 指标 | 数值 | 说明 |
|---|---|---|
| Build | 0 errors | Debug + Release |
| Assert | 全 PASS（14 套件）/ EXIT=0 | |
| P01 ReadOnly batch | 55.3 ns | VOM8a 持平；+11 ns 真因移交 VOM9-perf（[Overview §八 D3](Step_VOM_Overview.md)） |
| P02 Call batch | 57.2 ns | 同上 |
| P03 alloc | 0 B / 100 batch | 维持 |

---

## 三、关联文档更新（待同步）

- VM_Summary：VOM9 行 ⏳→🟢；妥协 A.3 更新为"Phase 1+2+4-minimal ✅；SYSCALL ABI 硬切移交 VOM-Tail"
- benchmark_results.md / performance_history.md：追加 VOM9 列（P01=55.3 / P02=57.2）
- 历史备份 `Step_VOM9_Snapshot_SYSCALL_Cleanup.md.pre-vomx.bak`：已删除
