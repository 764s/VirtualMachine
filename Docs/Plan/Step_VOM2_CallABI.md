# Step VOM2: Arguments / ReturnSlot ABI + StaticReadOnlyCall

> **位置**: VOM 系列第 2 步；ABI 与最简调用打通。
> **状态**: � Phase1 ✅ + Phase2 ✅（编译期 `@readonly` / `@static_readonly` 注解 + 写指令拒绝 + `VMEngine.StaticReadOnlyCall` 强制 IsReadOnly；37 项测试全过 — 26 Phase1 + 11 Phase2）。**性能门禁 ≤ 31 ns 推迟到 VOM3** 完成临时 CPUData 池后再做。
> **前置**: [VOM1](Step_VOM1_StateSplit.md) 完成。
> **来源**: [IdealAndGap §四 S3+S4](../Discussion/D_VM_ObjectModel_IdealAndGap.md)。
> **核心原则**: 引入 `Span<Number>` 中介零分配 ABI；以 StaticReadOnlyCall 为最小验证档（不需 CPUData 池，不需 VMData 写入）。

---

## 一、本步骤的临时妥协

**调整范围 (Phase 拆分)**：VOM2 拆为两相 — Phase1 只交付 ABI + 运行路径（本文档当前状态），Phase2 交付编译期限制与性能门禁。理由：Lexer/Parser/Compiler 三处改动 + 31ns 门禁与 ABI 落地同相会遵主要验证。

| 妄协 | 理由 | 消除时间点 |
|------|------|-----------|
| StaticReadOnlyCall 仅允许访问 VMDef 的 constants 段 | 限定最小入口便于验证 | VOM3 引入 ReadOnlyCall 时扩 |
| 编译器 `@readonly` / 纯静态校验仅做白名单（无写指令） | 与 inline 优化交互简化（风险 4） | VOM2 Phase2 |
| `Arguments` / `ReturnSlot` 仅支持 Number 类型，不含 struct 视图 | 与现有寄存器 ABI 一致 | 嵌套结构体扩展时 |
| **Phase1 跳过运行期 write-opcode 白名单** | 需要 `ExecuteInstance` 增加 `isReadOnly` flag；与 Phase2 同步 | VOM2 Phase2 |
| ~~Phase1 StaticReadOnlyCall 要求被调函数为 entry 函数~~ | 避免 RET_FUNC 在空 CallStack 上溢出 | ✅ **VOM3 Phase1 已消除**（sentinel CallFrame.ReturnIP=-1 + RET_FUNC/RET_LEAF/RETURN-cleanup-pop 哨兵停机） |
| ~~Phase1 用 Spawn/Destroy 临时实例，每次调用有分配~~ | 最小实现优先 | ✅ **VOM3 Phase1 已消除**（`TransientInstancePool` 复用 slot；perf gate ≤31ns 留给 VOM3 Phase2） |

---

## 二、基础设施盘点

| 组件 | 现状 | 来源 |
|------|------|------|
| 寄存器直接传参 | 调用者 r0..r(n-1) | `VMWorld.cs:970-991` |
| Number 类型 | 已有 | — |
| MethodHandle | VOM1 ✅ | VOM1 |
| VMDef 概念 | VOM1 内嵌 partial | VOM1 |

### 需要新增

- `ref struct Arguments { Span<Number> _data; int Count; Number this[int i]; }`
- `ref struct ReturnSlot { Span<Number> _data; int Capacity; void Set(int i, Number v); }`
- `static class VMEngine`
- `VMEngine.StaticReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMDef)`
- 编译期 `@readonly` 标注 + 写指令拒绝路径
- VMDef 的 constants 段访问视图（已有数据，包装入口）

---

## 三、子任务总览

```
A. Arguments / ReturnSlot ref struct 定义
B. VMEngine.StaticReadOnlyCall 实现
C. 编译期 @readonly 标注 + 验证
D. Span 长度 / 类型校验入口
E. 测试 + 基准
F. 文档同步
```

---

## Sub-task A: Arguments / ReturnSlot

- [x] A.1 新建 `Assets/Scripts/VM/Core/Arguments.cs`（**位置变更**：Core 而非 Runtime，与 MethodHandle 同目录）：`readonly ref struct Arguments`，带 `Span<Number>` 字段
- [x] A.2 同文件 `readonly ref struct ReturnSlot` + `VMABIException`
- [x] A.3 stackalloc + Span ctor + Number[] ctor + `Empty` 静态属性
- [~] A.4 越界检查：依靠 Span 本身边界检查（未额外加 DEBUG_VM 编译开关；如需可于 Phase2 补）
- [x] A.5 ABI01-10 测试覆盖读写 / 越界拒绝 / 0 长度 / null 数组 / Span 写穿透

---

## Sub-task B: VMEngine.StaticReadOnlyCall

- [x] B.1 新建 `Assets/Scripts/VM/Core/VMEngine.cs` 静态类
- [x] B.2 实现 `StaticReadOnlyCall(VMWorld, int moduleSlot, MethodHandle, Arguments, ReturnSlot)`（**签名变更**：必须传 VMWorld 宿主，因为 `ExecuteInstance` 是实例方法；传 moduleSlot 而非 VMDef，因为 VMDef 未独立抽象 — VOM3+ 会抽出）
- [~] B.3 **路径调整**：未使用“栈上临时 CPUData”（VOM3 项）；改为 `Spawn → 写 r0..rN-1 → TickInstance → 读 r0..rM-1 → Destroy`。该路径依赖被调函数为 entry（发 RETURN 触发 Completed）。补足于 §一 妄协表
- [x] B.4 运行期断言：`inst.ErrorFlag` 检查；Completed 检查拒绝未完成
- [x] B.5 端到端：E2E01 add(3,4)=7、E2E02 mul(6,7)=42、E2E03 meaning()=42、E2E04 重复调用、E2E05 分支控制流 absv　— 全过

---

## Sub-task C: 编译期 @readonly 标注（风险 4） — **Phase2 ✅**

Lexer / Parser / Compiler / VMProgram / VMEngine 全链落地；性能门禁仍延后。

- [x] C.1 Lexer 新 `@readonly` / `@static_readonly` 注解 token（合并为 `TokenType.ReadOnly`，二者别名）
- [x] C.2 Parser 在函数声明前缀位置吸收注解，可与 `@export [private|public]` / `@inline` 组合
- [x] C.3 BytecodeCompiler 在 `EmitStoreModuleVar` / `XSTORE_MVAR`（直接写、跨模块写、setter 退化）三个出口对 `_isReadOnlyFunction` 短路并报错
- [~] C.4 inline 优化前置 readonly 提升：现有 inline 路径自然继承调用者上下文（被 inline 函数若发生写指令仍命中 EmitStoreModuleVar 检查）；未额外做 readonly 推断
- [x] C.5 编译期错误测试 P2_02 / P2_07：违规招 `[VOM2] @readonly function ... cannot write to ...`

---

## Sub-task D: Span 长度 / 类型校验

- [x] D.1 `VMEngine.StaticReadOnlyCall` 入口校验 `args.Count == handle.ParamCount`
- [x] D.2 `ReturnSlot.Capacity >= handle.ReturnCount`
- [x] D.3 不匹配招 `VMABIException`（丝线拋出）

---

## Sub-task E: 测试 + 基准

- [x] E.1 ABI 类型测试 ABI01-10（stackalloc / array / Span / null 保护 / 写穿透）
- [x] E.1' StaticReadOnlyCall 端到端 E2E01-05（add / mul / zero-arg / 重复 / 分支控制流，全部 `@readonly`）
- [x] E.1'' 违规路径 FAIL01-07（arg too few/many、ret too small、unresolved、stale (Invalidate)、module not loaded、world null）— 全招 `VMABIException`
- [x] **Phase2 新增 P2_01..P2_07**（IsReadOnly 字段传播 / 写 mvar 拒编 / 非 readonly 被引擎拒 / `@readonly @export` 组合 / `@static_readonly` 别名 / 读 mvar 允许 / 显式写 mvar 拒编）
- [x] **合计 37 项新增，全过；现有全套件 0 failed**
- [~] E.2 微基准 ≤ 31 ns：**仍推迟至 VOM3**。Spawn/Destroy 临时实例路径不可能达该门禁；VOM3 临时 CPUData 池是必要前置
- [~] E.3 vs B06 136ns: 推迟至 VOM3
- [~] E.4 0 字节分配验证：推迟至 VOM3

---

## Sub-task F: 文档同步

- [x] F.1 VM_Summary 特性区 VOM2 🟡 → 🟢（Phase1 + Phase2）
- [x] F.2 IdealAndGap §三 G4（ABI ref struct + @readonly 编译期保护）✅；G3 / G8 仍⏳
- [x] F.3 本文件状态 Phase1 ✅ + Phase2 ✅；性能门禁延后到 VOM3 已注记

---

## 四、验收门禁

| 类型 | 阈值 |
|------|------|
| Assert | 现有全通过 + ~10 新增 |
| 性能 | StaticReadOnlyCall 单次 ≤ 31 ns；0 alloc |
| 编译期 | 写指令在 readonly 上下文被静态拒绝 |

---

## 五、回退策略

- C 失败：先实现运行时拒绝（断言），编译期校验延后到独立小步
- B 性能不达标：先以正确性为准记录实测；优化挪到 VOM6 摊销前

---

## 六、关联文档更新

VM_Summary VOM2 ✅；IdealAndGap G3 / G4 / G8 ✅；本文件 ✅。
