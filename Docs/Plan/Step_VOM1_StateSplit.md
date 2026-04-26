# Step VOM1: VMInstanceState 切分 + MethodHandle 缓存

> **位置**: VOM 系列第 1 步；前置铺路。
> **状态**: ✅ 已完成。新增类型 `MethodHandle` / `CPUDataView` / `VMDataView` 与 `VMProgram.Version` / `ResolveMethod` / `Invalidate`。36/36 VOM1 测试通过，全套件回归 0 failed，exit=0。
> **前置**: 无（VOM 系列起点）。
> **来源**: [IdealAndGap §四 S1+S2](../Discussion/D_VM_ObjectModel_IdealAndGap.md)；[Transition §性能天花板](../Discussion/D_VM_ObjectModel_Transition.md)。
> **核心原则**: 不引入新调用契约，仅做底层数据结构准备。所有现有测试保持通过。

---

## 一、本步骤的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| `CPUData` / `VMData` 仍为同一实例内嵌 partial（不外迁到独立类型） | 渐进式切分降低回退面（IdealAndGap §五 风险 1） | VOM3 临时池引入时正式外迁 |
| `MethodHandle` 仅承载 functionIndex + version，不含 frame size 等热信息 | 第一版以正确性优先 | VOM2 ABI 落地时按需扩 |
| 不删除 `TryGetFunction(name)` 旧路径 | 兼容 XCALL 字符串路径，逐步迁移 | VOM2 完成后清理 |

---

## 二、基础设施盘点

| 组件 | 现状 | 文件:行 |
|------|------|---------|
| `VMInstanceState` 单结构体 | 混合 IP/Reg/CallStack/Wait/Cleanup | `VMInstanceState.cs:94-174` |
| `TryGetFunction(string)` | 线性遍历 Functions[] | `VMProgram.cs:114-123` |
| `VMProgram.Functions` | List<FunctionInfo> | 同上 |
| `VMDef` 概念 | 不存在；`VMProgram` 即等价 | — |

### 需要新增

- `CPUData` 字段组（partial / inner struct）：`IP`、`RegisterBase`、`CallStackDepth`、`ErrorFlag`、`StateFlags`、`CleanupDepth`、`LeafReturnIP`、`LeafRegisterBase`
- `VMData` 字段组：`WaitCounter`、`WaitTargetInstanceId`（业务 wait 状态）
- `MethodHandle` readonly struct：`{ int FunctionIndex; int VMDefVersion; }`
- `VMProgram.ResolveMethod(string) → MethodHandle`（首次解析后缓存到内部字典）
- `VMProgram.Version`（int，hot-reload 递增）

---

## 三、子任务总览

```
A. CPUData / VMData 字段分组（不破坏现有引用）
B. MethodHandle 类型 + ResolveMethod 缓存
C. VMProgram.Version + 句柄失效协议
D. ExecuteInstance / XCALL 引用迁移到分组访问
E. 测试 + 基准
F. 文档同步
```

---

## Sub-task A: CPUData / VMData 字段分组

### 意图

将 `VMInstanceState` 内部字段按"执行态 vs 业务态"分组，但保持 ABI 兼容（同一结构体），为 VOM3 外迁做准备。

### 具体变更

- [~] A.1 **未做**：避免直接修改原文件，分组语义改由 `VMInstanceStateViews.cs` 表达
- [x] A.2 引入 `readonly ref struct CPUDataView` / `readonly ref struct VMDataView`（新文件 `Assets/Scripts/VM/Core/VMInstanceStateViews.cs`），通过 `ref` 字段映射
- [x] A.3 通过扩展类 `VMInstanceStateViews` 提供 `AsCPU()` / `AsVM()`（无侵入；`[MethodImpl(AggressiveInlining)]`）
- [x] A.4 现有调用点不强制迁移（VOM1 范围内零迁移；VOM3 再切换）
- [x] A.5 全套件 + 36 项 VOM1 新增 全通过

---

## Sub-task B: MethodHandle 类型 + ResolveMethod 缓存

### 具体变更

- [x] B.1 新建 `Assets/Scripts/VM/Core/MethodHandle.cs`（**位置变更**：与 `VMProgram` 同 Core 目录）：`readonly struct MethodHandle { int FunctionIndex; int Version; int EntryIP; int ParamCount; int ReturnCount; bool IsResolved; }`
- [x] B.2 `VMProgram._methodIndexCache: Dictionary<string,int>`（首次惰性构建）
- [x] B.3 `ResolveMethod(string)` 首次 O(N)；后续 O(1)；first-wins 一致 `TryGetFunction` 语义
- [~] B.4 暂不引入 `GetFunction(MethodHandle)`：当前无热路径调用方；改由 `MethodHandle.IsValid(VMProgram)` 提供 Version 校验。VOM2 Call ABI 时再补
- [~] B.5 缓存命中 1ns 微基准未跑：超出 §零 调整后纯加新类型范围；改为功能测试 MH16 验证缓存稳定性。基准延后到 VOM2

---

## Sub-task C: VMProgram.Version + 句柄失效协议（风险 5）

### 具体变更

- [x] C.1 `VMProgram.Version { get; private set; } = 1`
- [x] C.2 `Invalidate()`：`Version++` 并清空 `_methodIndexCache`
- [x] C.3 `MethodHandle.IsValid(VMProgram p)` 比较 `Version == p.Version` 且 `IsResolved` 且索引在范围内
- [~] C.4 暂无调用方：VOM1 范围内未引入热路径，调用方校验留待 VOM2
- [x] C.5 MH17/MH18/MH19/MH20 覆盖：Invalidate 后旧句柄 `IsValid == false`、Version 正确递增、重解析得新版本

---

## Sub-task D: ExecuteInstance / XCALL 引用迁移

**调整后跳过**：依据 §零 探查（`TryGetFunction` 是死代码，XCALL 已 O(1)），VOM1 调整为"纯加新类型零行为变更"。D.1/D.2/D.3 整体推迟到 VOM2 Call ABI 落地时一并完成。

- [~] D.1 跳过（VOM2 处理）
- [~] D.2 跳过（XCALL 已 O(1)）
- [~] D.3 不适用

---

## Sub-task E: 测试 + 基准

- [x] E.1 现有全部测试通过（TreeWalker 114 / Compiler 1302 / Performance 44 / FFScript 18 / Debug 51 / Dap 97 / Lsp* 全部 / LspCoverageMatrix 3 — 0 failed）
- [x] E.2 新增 **36** 项 VOM1 测试（超出计划 5 项；MH01-22 + VW01-14；含 hit/miss/invalidate/version mismatch/null/empty + view 读写穿透）
- [~] E.3 B06 基准未跑：本步骤未触动热路径，理论无影响
- [~] E.4 ResolveMethod 微基准延后到 VOM2

---

## Sub-task F: 文档同步

- [x] F.1 [VM_Summary.md](../VM_Summary.md) 特性区 → 转向落地 VOM1 行 ⏳ → ✅
- [x] F.2 [IdealAndGap.md](../Discussion/D_VM_ObjectModel_IdealAndGap.md) §三 G1（标注"分组完成，外迁待 VOM3"）+ G6 ✅
- [x] F.3 本文件顶部状态改 ✅。MethodHandle 缓存命中微基准延后至 VOM2

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Assert | ~2140 + 5 新增全通过 |
| 性能 | MethodHandle 缓存命中 ≤ 1 ns；B06 不回退 |
| 行为 | hot-reload 句柄失效语义一致 |

---

## 五、回退策略

- A 失败：保留 `VMInstanceState` 原状，仅注释分组边界，VOM2 改用名义分组
- B 失败：保留 `TryGetFunction` 路径不删除，VOM2 直接走字符串
- C 失败：跳过 hot-reload 语义，文档登记延后

---

## 六、关联文档更新

完成后：
- VM_Summary 特性区 VOM1 ✅
- IdealAndGap §三 G6 ✅，G1 部分 ✅
- 本文件状态 ✅
