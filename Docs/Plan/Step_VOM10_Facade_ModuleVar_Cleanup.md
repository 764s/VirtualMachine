# Step VOM10: VMInstance Façade 精简 + ModuleVar 旧轨清理

> **位置**: VOM9 落地后的纯清理步（与 SYSCALL ABI 解耦）。
> **状态**: ✅ 已完成（2026-04-26）。Phase A 经全工程 grep 验证为已满足（VMInstance.cs façade 自 VOM5/VOM7 收敛起即最简）；Phase B.1-B.3 已落地 —— VMWorld.cs 4 个热路径站点（LOAD_MVAR / STORE_MVAR / XLOAD_MVAR / XSTORE_MVAR）从 `regs[ModuleVarRegBase+x]` 迁移至 `vmd.MVars` / `targetInst.Data.MVars.Raw`，ExecuteInstance 顶部一次性 fixed-pin `Number* mvars`。14 套件全 PASS / EXIT=0。B.4 Debug 断言定为可选项，未实施。
> **前置**: VOM9 Phase 1+2+4-minimal 完成（VMInstanceView pass-through API + ExecuteInstance dual-ref + VMInstanceStateViews 清理）。注：VOM9 Phase 4-full SYSCALL ABI 硬切已移交 VOM-Tail（见 [Step_VOM_Overview.md](Step_VOM_Overview.md) §八 D1），本步与之解耦，可独立启动。
> **核心原则**: 与 ABI 解耦的纯清理 —— 任何子任务可独立 revert。
> **来源**: VOMX 重规划（2026-04-26）。从原 `Step_VOM9_Snapshot_SYSCALL_Cleanup.md.pre-vomx.bak` §A.5 + A.6 + A.7 拆出。

---

## 一、为什么独立成步

原 VOM9 设计为"原子提交 + 单一性能验收"（P01/P02 ≤ baseline + 2 ns）。façade 精简和 ModuleVar 清理与热路径性能**完全解耦**：

- façade API 缩减仅影响宿主可见性，不改变热路径
- ModuleVar 旧轨清理是写路径去重，不动读路径
- 这两项可独立 revert，不污染 VOM9 的验收面

把它们塞进 VOM9 会让验收表无法证伪：如果性能指标未达但 fixtures 全 pass，是 PASS 还是 FAIL？

> 注：2026-04-26 二次修订后，VOM9 perf 假说已证伪、+11 ns 调研已剥离为 VOM9-perf（[Overview §八 D3](Step_VOM_Overview.md)）。本步的"与热路径性能解耦"论据仍成立——façade / ModuleVar 清理与 VOM9-perf 调研同样互不干扰。

---

## 二、子任务

### A. VMInstance Façade 精简 — ✅ N/A（已满足）

**结论**：经全工程 grep 验证（`\.Cpu\.IP` / `\.Cpu\.Registers` / `\.Cpu\.CallStack`），KOF98 / Sandbox / StandaloneRunner 中无任何宿主侧 CPU 状态读取；`VMInstance.cs` 当前公开成员仅有 `World / InstanceId / Generation / IsValid / IsAlive / IsCompleted / Bindings / Tick() / Kill()`，无 IP / Registers / CallStack 透出。façade 在 VOM5/VOM7 收敛阶段已抵达最简形态。

- [x] **A.1** ~~仅暴露 `ref VMData Data`~~ → 现状未暴露任何 CPU/Data 字段（handle 设计），无需进一步收敛。
- [x] **A.2** ~~隐藏 CPUData~~ → 已隐藏；唯一访问点是测试代码中的 `VMInstanceView`（VOM7 引入的诊断结构），不属宿主 API。
- [x] **A.3** ~~编译时验证~~ → Build 通过即完成。

### B. ModuleVar 旧轨清理 — ✅ 已完成

**前提澄清**：VOM7 仅添加了 `MVarRegisters` / `VMData.MVars` 类型定义，原计划由 VOM8 完成的 `regs[ModuleVarRegBase+B]` → `vmd.MVars.Raw[B]` 迁移当时未执行（见 [VMObjectModel.cs](../../Assets/Scripts/VM/Core/VMObjectModel.cs) L8-13 注释）。VOM10 接手该迁移。

实施摘要（[VMWorld.cs](../../Assets/Scripts/VM/Core/VMWorld.cs)）：

- [x] **B.1** 在 ExecuteInstance 顶部 pin block 增加 `fixed (long* rawMvars = vmd.MVars.Raw)` 与 `Number* mvars = (Number*)rawMvars;`，一次性钉住整个 burst（与 `regs` 同 lifetime，零额外 fixed 开销）
- [x] **B.2** `LOAD_MVAR` 改为 `regs[Reg(op.A,rb)] = mvars[op.B]`；`STORE_MVAR` 改为 `mvars[op.A] = regs[Reg(op.B,rb)]`
- [x] **B.3** `XLOAD_MVAR` / `XSTORE_MVAR` 的 fixed-register-path 分支改为 `fixed (long* targetMvarRaw = targetInst.Data.MVars.Raw)` + `targetMvars[mvarSlot]`（删除 `+ ModuleVarRegBase` 偏移）；extended-register-path 分支保持不变
- [ ] **B.4** ~~Debug 断言（Rent/Reset 路径校验 `Registers.Raw[ModuleVarRegBase..]` 不再被写入）~~ → 推迟，登记为 [Overview §八 D5](Step_VOM_Overview.md)。理由：所有写入点已收敛至 4 个迁移完成的 opcode 处理器，且 grep 全工程无残留 `Registers.Raw[ModuleVarRegBase` 写入；新增 `[Conditional("DEBUG_VM")]` 断言收益边际。移交 VOM-Tail 任务列表（与 D1 选 A/B 时合并扫描）。

**默认值兼容性**：模块变量默认值由 module init 函数中编译器发射的 `STORE_MVAR slot, constReg` 指令应用，迁移后 STORE_MVAR 直接写入 `mvars[slot]`，默认值正确流入。`Pool.Allocate` 的 `inst = default` 同时清零 `Cpu.Registers` 和 `Data.MVars`，新实例两侧均从 0 起步。

### C. 基准复测 — 部分（推迟到 VOM-Tail）

- [x] **C.1** 14 套件全 PASS / EXIT=0 验证零功能回归（VOM3 ModuleVar 25/0、VOM2 XCALL 37/0、VOM4 YieldCall 36/0）
- [ ] **C.2** P01-P03 / B07 / B09 / B10 微基准重测推迟，登记为 [Overview §八 D6](Step_VOM_Overview.md)。理由：本步只迁移读写目标缓冲区，热路径指令计数与寻址模式（指针 + 立即偏移）形态不变；仅 cache topology 由"register file 高端"切到"独立 MVars 缓冲"。在无 MVar 重负载的 P01/P02 上几乎不可观测，VOM-Tail 阶段统一复测更经济。

---

## 三、不变量

- 全部 Assert PASS（含 façade 改造后宿主端到端测试）
- 0 alloc / 100 batches 维持
- P01/P02 ± 2 ns vs VOM9 Phase 2 实测基线（55.3 / 57.2 ns）零回归
- ModuleVar 读写双轨退化为单轨（`VMData.MVars` 唯一）

---

## 四、验收门禁

| 类型 | 阈值 | 实测 |
|------|------|------|
| Build | 0 errors（Debug + Release） | ✅ Release 0 errors / 3 warnings (pre-existing) |
| Assert | 全 PASS | ✅ 14 套件全 PASS |
| Perf | P01/P02 ±2 ns vs VOM9 Phase 2 实测基线（55.3 / 57.2 ns）零回归 | 🟡 推迟至 VOM-Tail 统一复测，登记为 [Overview §八 D6](Step_VOM_Overview.md) |
| API surface | `VMInstance` 公开成员仅 handle 标识 / 生命周期方法 | ✅ 已满足（A.1-A.3） |
| ModuleVar 写入点 | grep 全代码无 `Registers.Raw[ModuleVarRegBase + ?] = ` 写入（LOAD/STORE_MVAR 实现内除外） | ✅ 4 个站点已迁移至 `mvars[]` / `Data.MVars.Raw` |
| EXIT | 0 | ✅ EXIT=0 |

---

## 五、风险

| 风险 | 缓解 |
|------|------|
| façade API 缩减破坏宿主代码（KOF98 / Sandbox） | API surface 缩减是 Transition 既定契约；分批改造，每子项独立 commit |
| ModuleVar 兜底删除后某条编译路径失活 | B.4 Debug 断言 + 全量 fixture 跑一遍 |
| 调试 API（A.2）增加新 surface | 命名为 `Debug*` 前缀；编译条件 `[Conditional("DEBUG_VM")]` 限定 |

---

## 六、回退策略

每个子项（A / B / C）独立 commit，可单点 revert。

---

## 七、关联文档更新

- VM_Summary VOM10 ⏳→✅
- IdealAndGap §六 façade 收敛条件 ✅
- Transition.md §结论：CPUData/VMData 分离透出宿主面契约定稿
- benchmark_results.md VOM10 列：推迟至 VOM-Tail 统一复测
- 本文件 ⏳→✅
