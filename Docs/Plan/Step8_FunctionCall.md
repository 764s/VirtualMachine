# 步骤 8：函数调用 + 固定深度调用栈

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序的步骤 8。
> **状态**：✅ 全部完成（279 项 Assert 通过）。
> **来源**：VM_Tracer_Bullet.md §十二 第 3 项；CallFrame 基础设施已在步骤 1 中就位。
>
> 本文由原 `Step8_FunctionCall_Checklist.md`（设计规格）和 `Step8_Implementation_SubPlan.md`（实施记录）合并而成。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| F4（寄存器生命周期分析 + 跨 await 变量提升）不在本步骤实现 | VM_Summary 明确标注"低优先级，最晚步骤 10 前" | 步骤 10 前 |
| 不支持递归调用 | 固定深度调用栈（MaxCallDepth=16）天然限制递归；本步骤先验证直接调用正确性 | 无需消除，递归受深度硬限保护 |
| 不支持闭包 / 高阶函数 | Architecture Rule 6 禁止寄存器持有托管引用；不符合 VM 物理约束 | 无需消除（设计决策） |
| 不支持跨模块函数调用 | 本步骤聚焦同模块内函数调用，跨模块依赖 ModuleTable 扩展 | 后续步骤按需扩展 |
| 函数参数上限 = 16（r0-r15 Scratch Zone） | 覆盖绝大多数场景；与 Syscall 参数传递一致 | 如需更多参数，后续扩展 |

---

## 二、基础设施盘点

以下组件在步骤 1-7 中已就位，步骤 8 直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `CallFrame` struct | ✅ 已定义 | `ReturnIP` + `ReturnModuleSlot` + `RegisterBase` + `CleanupBase` |
| `CallStackFrames` | ✅ 已分配 | 16 槽预分配内联数组（`VMInstanceState` 内） |
| `CallStackDepth` 字段 | ✅ 已有 | `VMInstanceState` 内，初始 0 |
| `RegisterBase` 字段 | ✅ 已有 | `VMInstanceState` 内，支持寄存器窗口偏移 |
| `MaxCallDepth = 16` | ✅ 已定义 | `VMConstants.cs` |
| `FuncDecl` / `CallExpr` AST | ✅ 已有 | Parser 完整支持 `func` 声明和 `name(args)` 调用解析 |
| Snapshot memcpy | ✅ 自动覆盖 | CallStack 内联于 VMInstanceState，无额外逻辑 |
| `CALL = 60` OpCode | ✅ 步骤 7 期间已预定义 | OpCode.cs Phase 3 区段 |
| `FunctionEntry` struct | ✅ 已定义 | VMProgram.cs — Name, EntryIP, ParamCount, LocalRegCount |
| `FunctionEntry[] Functions` | ✅ 已就位 | VMProgram 构造函数支持可选传入 |
| `TryGetFunction()` API | ✅ 已实现 | 按名称查找函数入口 |
| `Reg()` 偏移 | ✅ 已就位 | VMWorld 中所有 `Reg(ref inst, index)` 已加上 `RegisterBase` 偏移 |

---

## 三、设计决策

### 决策 1：寄存器窗口策略 → 方案 A（共享 scratch + local 区偏移）

- **Scratch zone r0-r15**：全局共享，不随 CALL 偏移，用于参数传递和返回值
- **Local zone r16+**：按 `callerWindowSize` 偏移。CALL 时 `new_base = old_base + callerWindowSize`
- **`CALL.B` 操作数**：携带 `callerWindowSize`（= caller 的 LocalRegCount + 16，即 scratch + local 区总宽度）
- **寄存器溢出保护**：`new_base + callee所需寄存器 > MaxRegisters` → `VMError.StackOverflow`

**容量估算**：MaxRegisters=64，若平均每层窗口偏移 16 个寄存器，可支持 ~3 层嵌套。实际业务场景中技能脚本调用深度通常 ≤ 3，满足需求。更深嵌套待 FO6（自适应窗口）优化。

### 决策 2：RETURN vs RET_FUNC 语义分离

- **`RET_FUNC`**（OpCode 61）：从用户函数返回。弹出 CallFrame → 恢复 IP、RegisterBase → 回到 caller
- **`RETURN`**（OpCode 6）：终止实例执行 / 进入 Cleanup 链。仅用于 entry 函数末尾
- **编译器保证**：非 entry 函数 → 末尾 emit `RET_FUNC`；entry 函数 → 末尾 emit `RETURN`

### 决策 3：两遍编译 + 前向引用回填

- **Pass 1**：扫描所有 `FuncDecl`，注册函数名到 `_functionTable`（占位 IP=-1）
- **Pass 2**：先编译 entry 函数（emit `RETURN`），再编译其余函数（emit `RET_FUNC`）
- **前向引用**：遇到尚未编译的目标函数时，emit `CALL` with placeholder IP → 记录到 `_pendingCalls` → 所有函数编译完成后统一回填

---

## 四、实施记录

### Sub-task A: CALL / RET_FUNC OpCode + VMWorld 分发 ✅

| 项目 | 状态 |
|------|------|
| `RET_FUNC = 61` OpCode 定义 | ✅ |
| `case OpCode.CALL`：检查深度 → 保存 CallFrame → push → 偏移 RegisterBase → 跳转 | ✅ |
| `case OpCode.RET_FUNC`：pop CallFrame → 恢复 IP + RegisterBase | ✅ |
| StackOverflow 保护测试 | ✅ |

### Sub-task B: VMProgram 函数表 ✅

函数表在步骤 7 期间已提前完成（FunctionEntry struct + Functions[] + TryGetFunction）。

### Sub-task C: 编译器多函数支持 ✅

| 项目 | 状态 |
|------|------|
| `_functionTable` + `_funcDecls` + `_pendingCalls`（前向引用回填列表） | ✅ |
| `CompileModule()` 两遍编译 + 编译后回填 | ✅ |
| `CompileFunction(FuncDecl, bool isEntry)` | ✅ |
| `CompileUserCallExpr` / `CompileUserCallVoid` / `EmitUserCall` | ✅ |
| 参数传递：scratch zone r0..rN | ✅ |
| 返回值：r0 约定（与 Syscall 一致） | ✅ |
| `FunctionEntry[]` 传入 `VMProgram` 构造 | ✅ |

### Sub-task D: 测试验证 ✅

42 项新增断言（14 TreeWalker + 28 Compiler），总计 279 项全部通过。

| 测试 | 内容 | 状态 |
|------|------|------|
| F01 | 基本函数调用 add(3,4)=7 | ✅ |
| F02 | 多函数调用链 a()→b(), 结果 43 | ✅ |
| F03 | 寄存器窗口隔离 — caller/callee 局部变量互不干扰 | ✅ |
| F03 TW | 调用深度保护 — 递归超 MaxCallDepth → StackOverflow | ✅ |
| F04 | 函数 + Syscall 混合 — Ping() + double(21)=42 | ✅ |
| F05 | void 函数调用（副作用 sideEffect） | ✅ |
| F06 | 嵌套调用链 square(7)→mul(7,7)=49 | ✅ |
| F05 TW | GC 零分配验证 — 100 次 CALL/RET_FUNC = 0 GC | ✅ |
| CF07 | defer cleanup 正确执行（单函数回归） | ✅ |
| CF08 | 参数数量不匹配 → 编译错误 | ✅ |
| CF09 | 单函数回归验证 — 10+20=30 | ✅ |

---

## 五、风险分析

### 5.1 已识别风险（步骤 8 实施期间）

| # | 风险 | 影响 | 缓解措施 | 当前状态 |
|---|------|------|---------|---------|
| R1 | 寄存器窗口偏移导致 MaxRegisters=64 下嵌套层数不足 | 深度调用寄存器溢出 | 方案 A 下平均 3 层够用；FO6 自适应窗口可扩展 | ⚠️ 已知限制，业务可接受 |
| R2 | 编译器两遍编译引入 entry IP 错误 | 函数跳转目标错误 → 运行时 panic | 占位 + 回填保证正确性；F-02 多函数链测试覆盖 | ✅ 已验证 |
| R3 | RETURN 路径意外在非 entry 函数触发 | 实例提前终止 | 编译器保证非 entry 函数只 emit RET_FUNC | ✅ 已保证 |
| R4 | CleanupBase 在函数边界的交互 | using/defer 跨函数调用时 cleanup 范围错误 | F-07 测试覆盖；CallFrame.CleanupBase 已预留 | ✅ 已验证 |

### 5.2 后续风险（面向步骤 9+ 的前瞻）

| # | 风险 | 影响 | 建议应对 |
|---|------|------|---------|
| R5 | 结构体作为函数参数时寄存器窗口空间不足 | struct 字段拍平后占多个连续寄存器，加剧窗口压力 | 步骤 9 S4 实施时需与 FO6 联合评估；或限制 struct 参数大小 |
| R6 | 跨模块函数调用引入 ModuleSlot 切换 | 当前 CALL 只支持同模块内跳转，跨模块需扩展 | 后续步骤按需扩展 CALL 指令操作数或新增 CALL_EXT |
| R7 | 前向引用回填在大模块中的性能 | `_pendingCalls` 列表线性扫描 | 当前模块规模小无压力；百函数级模块时考虑改用 Dictionary |
| R8 | Cleanup 块内调用函数时 CleanupBase 语义 | 函数返回后 CleanupDepth 可能与 CleanupBase 不一致 | 当前 Cleanup 块内禁止函数调用（编译器可增加检查） |

---

## 六、功能展望

> 以下功能方向基于步骤 8 实现现状，面向后续步骤。暂无排期，按需启动。

### FF1. 跨模块函数调用

**现状**：CALL 仅支持同模块内函数跳转（目标 IP 是当前 VMProgram 内的偏移）。

**方向**：扩展 CALL 指令支持跨模块跳转（目标 = ModuleSlot + EntryIP），或新增 `CALL_EXT` OpCode。CallFrame 已预留 `ReturnModuleSlot` 字段。

**前提**：VMModuleTable 跨模块解析机制就位。

**触发时机**：真实业务需要模块间函数共享时（如公共工具函数库）。

### FF2. 函数作为 Syscall 参数（回调模式）

**现状**：函数只能被直接调用（`name(args)`），不能作为值传递。

**方向**：支持将函数入口 IP 作为 Number 值传递给 Syscall，宿主侧通过 IP 回调脚本函数。需要约定回调协议（参数传递、返回值、调用栈状态）。

**约束**：Architecture Rule 6 禁止寄存器持有托管引用 — 函数"引用"只能是编译期确定的整数 IP，不是闭包。

**触发时机**：需要宿主驱动的回调场景（如 ForEach 目标、自定义排序比较器）。

### FF3. 可选参数与默认值

**现状**：函数参数数量必须精确匹配，不支持默认值。

**方向**：编译器在 CALL 前为缺省参数 emit `LOAD_CONST` 到 scratch zone。运行时无需变更。

**复杂度**：低。仅需编译器支持 `func f(a: int, b: int = 10)` 语法 + 缺省值 emit。

### FF4. 多返回值

**现状**：函数返回值约定写入 r0 单个寄存器。

**方向**：约定返回值写入 r0..rN（scratch zone 多个寄存器），caller 从多个寄存器读取。编译器需支持 `var a, b = f()` 解构语法。

**约束**：返回值数量 ≤ 16（scratch zone 上限），编译器静态检查。

**触发时机**：业务需要函数返回多个值时（如 GetPosition 返回 x, y）。

### FF5. defer 在非 entry 函数中的正确执行

> ✅ **已在 B-γ2 实现** — 详见 [Step_B_Gamma2_FF5_NonEntryDefer.md](Step_B_Gamma2_FF5_NonEntryDefer.md)

**方案**：RET_FUNC 检测 CleanupDepth > CleanupBase → InCleanup + savedR0 → RETURN 按 CleanupBase 边界弹出 CallFrame。含 defer/using 的函数排除 leaf 优化。763 项 Assert 通过。

---

## 七、性能优化展望

> 以下优化方向聚焦**函数调用路径**（CALL/RET_FUNC 引入的新开销）。
> 与 [VM_Optimization_Outlook.md](../Reference/VM_Optimization_Outlook.md) 中的通用 VM 优化（O1-O14）互补但不重叠。
> 暂无排期，待 benchmark 数据后按收益排序实施。

### FO1. 叶函数优化（Leaf Function Optimization）

**现状**：所有函数调用均执行完整 CallFrame push/pop + RegisterBase 偏移，即使 callee 不再调用其他函数。

**方案**：编译器静态标记"叶函数"（函数体内无 `CallExpr`）。对叶函数的 CALL，跳过 CallFrame 压栈，直接跳转 + 返回。

**前提**：叶函数内不能有 `wait` / `wait_for`（否则挂起后 CallFrame 缺失导致恢复失败）。

**预估收益**：叶函数调用开销减少 **~40-60%**。**复杂度**：低。

### FO2. 尾调用优化（Tail Call Elimination）

**现状**：`func a() { return b() }` 生成 `CALL b` → `RET_FUNC`，占用两层调用栈。

**方案**：当函数末尾语句是 `return f(args)` 时，emit `TAIL_CALL`：复用当前帧跳转到目标函数，返回时直接回到 caller 的 caller。

**收益场景**：状态机模式（技能多阶段 `phase1 → phase2 → phase3` 链式调用）不再增长调用深度。

**预估收益**：尾调用场景 CallStackDepth 不增长 + 省去一次 push/pop。**复杂度**：中。

### FO3. 小函数内联（Small Function Inlining）

**现状**：`func add(a, b) { return a + b }` 调用生成完整 CALL/RET_FUNC 序列（~7-8 条指令）。

**方案**：对函数体 ≤ 3 条指令的函数，在调用点直接展开字节码。

```
// 内联前（~8 条指令）：  MOVE r0,r16 → MOVE r1,r17 → CALL → ADD → RET_FUNC → MOVE result
// 内联后（1 条指令）：   ADD r48, r16, r17
```

**预估收益**：小函数调用 **~80% 指令数减少**。**复杂度**：中-高（需寄存器重映射）。

### FO4. 参数就位检测（Argument-in-Place Detection）

**现状**：编译器为每个函数参数生成 `MOVE` 拷贝到 scratch zone，即使值已在正确位置。

**方案**：emit `CALL` 前检查：若第 i 个参数已在 `r[i]`，跳过该 MOVE。

**预估收益**：每个已就位参数减少 1 条 MOVE。**复杂度**：低。

### FO5. 返回值直达（Return Value Direct Placement）

**现状**：`var x = f()` 生成 `CALL f` → `MOVE r0 → temp` → `MOVE temp → varReg`，两条额外 MOVE。

**方案**：结合通用优化 O4（dest-reg hint），`CALL` 返回的 r0 直接 `MOVE r0 → varReg`（1 条），或在 CALL 指令中编码结果目标寄存器（0 条额外 MOVE）。

**预估收益**：每个带返回值的函数调用减少 1-2 条指令。**复杂度**：低。

### FO6. 自适应寄存器窗口（Adaptive Register Window Size）

**现状**：窗口偏移基于 `callerWindowSize`，但某些函数可能过度分配。

**方案**：编译器为每个函数精确计算 `LocalRegCount`（实际使用的 r16+ 寄存器数），CALL 时窗口偏移 = `LocalRegCount`（而非固定步长）。

**收益**：MaxRegisters=64 下，如果平均每个函数用 8 个本地寄存器，可支持 (64-16)/8 = 6 层嵌套。

**前提**：需配合 F4（寄存器生命周期分析）精确计算活跃寄存器数。**复杂度**：中。

### FO7. 调用栈深度静态分析（Static Call Depth Analysis）

**现状**：调用深度超限在运行时检测（`CallStackDepth >= MaxCallDepth`）。

**方案**：编译器构建函数调用图，计算最大调用深度。若 max_depth > MaxCallDepth → 编译期报错。若 max_depth ≤ 阈值 → 标记"浅调用"，运行时跳过深度检查。

**预估收益**：编译期捕获深度溢出 + "浅调用"每条 CALL 省 1 次分支。**复杂度**：中。

### 优先级排序

| 编号 | 方向 | 优先级 | 理由 |
|------|------|--------|------|
| **FO4** | 参数就位检测 | 🟢 高 | 复杂度极低，无风险，立即可做 |
| **FO5** | 返回值直达 | 🟢 高 | 依赖 O4，同步实施成本低 |
| **FO1** | 叶函数优化 | 🟡 中 | 收益显著但需验证 wait 约束 |
| **FO6** | 自适应窗口 | 🟡 中 | CALL.B 已预留操作数，成本可控 |
| **FO7** | 静态深度分析 | 🟡 中 | 开发体验改善 > 性能收益 |
| **FO2** | 尾调用优化 | 🔵 低 | 状态机场景有意义，但需新 OpCode |
| **FO3** | 小函数内联 | 🔵 低 | 收益最大但复杂度最高，benchmark 驱动 |

---

## 八、依赖关系总览

```
步骤 7（using / Paired Syscall）✅
    ↓
步骤 8（函数调用）✅  ←  本文档
    ├── F1-F3 核心实现  ✅
    ├── F4 寄存器生命周期分析  → 最晚步骤 10 前
    ├── FF1-FF5 功能展望  → 按需
    └── FO1-FO7 性能优化展望  → benchmark 驱动
    ↓
步骤 9（结构体编译期拍平）
    ↓
步骤 10（编辑器流程图投影）
```
