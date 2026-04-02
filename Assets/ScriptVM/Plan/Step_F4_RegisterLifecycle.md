# F4：编译器寄存器生命周期分析 + 自然优化 + 调试 Phase 1 + 风险理想方案

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 Step 10 Pre 之后、Step 10（编辑器流程图投影）之前的**核心编译器升级阶段**。
> **状态**：✅ 已完成。361 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 SkillScript）。
> **前置**：Step 10 Pre（C4 + G6）✅ 已完成（315 项 Assert 通过）。
> **来源**：
> - F4：VM_Tracer_Bullet.md §十二 第 2 项"寄存器复用"
> - O3/O4/O5/O7/FO4/FO5/FO7：[Outlook_And_Risks.md §三.1 自然优化](Outlook_And_Risks.md#31-自然优化7-项随功能实现顺带完成)
> - DBG1/DBG2：[Outlook_And_Risks.md §二.5 脚本调试](Outlook_And_Risks.md#25-脚本调试dbg-系列)
> - R7/R8：[Outlook_And_Risks.md §八.2 风险理想方案](Outlook_And_Risks.md#82-风险理想方案速查)
> - 调试决策详情：[Step_Debug_Decisions.md](Step_Debug_Decisions.md)
>
> **核心原则**：F4 是多条线的汇聚点——自然优化、调试基础设施、风险应对都自然附着其上，边际成本极低。
> 本步骤完成后，编译器从"功能正确"升级到"生产级质量"（寄存器复用 + 调试信息 + 优化指令生成）。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| DBG2 仅实现 Phase 1（varName → register，不含生命周期范围） | Phase 2 需要 F4 活跃范围的完整数据结构稳定后再补充 | LSP Phase 4（语言服务阶段） |
| FO6（自适应寄存器窗口）不在本步骤实现 | 属于调整型优化，需 benchmark 数据驱动 | 调整型优化阶段 |
| FO1（叶函数优化）不在本步骤实现 | 同上，benchmark 驱动 | 调整型优化阶段 |
| O1/O2（消除 fixed pin / 跳转表）不在本步骤实现 | 属于解释器热路径优化，与编译器无关 | 调整型优化 Tier 1 |
| V5（帧内 Profiler）不在本步骤实现 | 依赖真实 Syscall 接入 ECS | 真实 Syscall 接入后 |
| S4（结构体函数参数传递）不在本步骤实现 | 仅在编辑器需要展示时才需要 | 步骤 10 前（如需） |

---

## 二、基础设施盘点

以下组件在步骤 1-10 Pre 中已就位，本步骤直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `BytecodeCompiler` | ✅ 908 行 | 两遍编译（函数表 → 编译 → 回填），寄存器布局 r0-15 scratch / r16-47 locals / r48-63 temps |
| `_nextVarReg` / `_tempTop` | ✅ 已有 | 线性递增分配，_nextVarReg 从 16 起，_tempTop 从 48 起 |
| `_variables` | ✅ 已有 | `Dictionary<string, int>` 变量名 → 寄存器 |
| `_structVarTypes` / `_structTypes` | ✅ 已有 | struct 类型表 + 变量类型映射，用于 DBG2 符号表 |
| `_functionTable` / `_funcDecls` | ✅ 已有 | 函数名 → 入口 IP / 函数声明信息 |
| `_pendingCalls` | ✅ 已有 | `List<PendingCall>` 前向引用待回填列表 |
| `_inCleanupBlock` | ✅ 已有 | G6：Cleanup 块内禁止 wait（R8 扩展检查的基础） |
| `_deferredCleanups` | ✅ 已有 | Cleanup 信息列表 |
| `VMProgram` | ✅ 已有 | Instructions + Constants + RequiredRegisters + Functions |
| `FunctionEntry` | ✅ 已有 | Name + EntryIP + ParamCount + LocalRegCount |
| `AllocTemp()` / `ResetTemps()` | ✅ 已有 | 表达式临时寄存器管理 |
| `CompileExpr()` | ✅ 已有 | 返回结果所在寄存器（当前无 dest-reg hint） |
| 性能基准 B01-B05 | ✅ 已有 | 编译脚本 5-7x ratio，F4 后预期改善 |

### 需要新增 / 修改

| 组件 | 说明 | 子任务 |
|------|------|--------|
| `CompileExpr(expr, destReg?)` | 增加可选 dest-reg hint 参数 | A（F4 核心） |
| 寄存器活跃范围追踪 | 编译期记录每个变量的 [startIP, endIP]，用于复用和 DBG2 Phase 2 | A |
| 死代码后寄存器回收 | 变量最后一次使用后释放寄存器，后续变量可复用 | A |
| 常量折叠 | `CompileExpr` 入口检测纯常量二元表达式 → 直接 emit 单条 LOAD_CONST | B（O5） |
| `VMProgram.SourceMap` | `int[]` 平行数组（IP → 行号） | D（DBG1） |
| `VMProgram.SymbolTable` | `SymbolEntry[]`（varName → register + structInfo） | E（DBG2） |
| `_pendingCalls` 自动切换 Dictionary | 函数数 >50 时切换查找策略 | F（R7） |
| Cleanup 块内禁止函数调用检查 | `_inCleanupBlock` 时报错 | G（R8） |

---

## 三、子任务总览

```
Sub-task A: F4 核心 — 寄存器生命周期分析 + 寄存器复用
Sub-task B: O5 常量折叠
Sub-task C: O4 目标寄存器传递（dest-reg hint）+ O7 Syscall 结果直达
Sub-task D: DBG1 源码映射表（Source Map）
Sub-task E: DBG2 符号表 Phase 1（Symbol Table）
Sub-task F: R7 理想方案 — _pendingCalls 自动切换 Dictionary
Sub-task G: R8 理想方案 — 编译器禁止 Cleanup 块内函数调用
Sub-task H: FO4 参数就位检测 + FO5 返回值直达
Sub-task I: FO7 调用栈深度静态分析
Sub-task J: O3 消除冗余 IP 边界检查
Sub-task K: Benchmark 验证 + 回归测试
Sub-task L: 文档更新
```

依赖关系：

```
A（F4 核心）──→ C（dest-reg hint，依赖生命周期分析产出）
     │                │
     │                └→ H（FO4/FO5，依赖 dest-reg hint）
     │
     ├──→ I（FO7 静态深度分析，依赖函数表已稳定）
     │
     └──→ K（benchmark，依赖所有优化完成）

B（O5 常量折叠）── 独立（可与 A 并行）
D（DBG1 Source Map）── 独立（可与 A 并行，但建议在 A 之后以利用稳定的 emit 路径）
E（DBG2 符号表）── 依赖 A 中变量寄存器映射数据结构
F（R7）── 独立
G（R8）── 独立（极简，一行检查）
J（O3）── 独立（代码清理）
L（文档）── 最后
```

建议执行顺序：

```
F → G → B → A → C → H → J → I → D → E → K → L
  ↑ 极简   ↑ 独立  ↑ 核心  ↑ 依赖A  ↑ 清理 ↑ 依赖A  ↑ 验证  ↑ 最后
```

---

## Sub-task A: F4 核心 — 寄存器生命周期分析 + 寄存器复用

### 意图

当前编译器使用线性递增寄存器分配：每个 `var` 声明递增 `_nextVarReg`，每个表达式中间值递增 `_tempTop`。
变量一旦分配寄存器，永不释放。这导致：
- 寄存器浪费：短生命周期变量（如循环计数器）占用的寄存器永不释放
- 指令膨胀：无法将结果直接写入目标寄存器（为 Sub-task C dest-reg hint 做基础）
- struct 寄存器压力加剧（SR1 风险根源）

F4 的目标是引入**变量活跃范围追踪**和**寄存器复用**，使编译器能在变量不再使用后回收其寄存器。

### 设计方向

**方法选择**：简单的线性扫描活跃范围分析（非完整的图着色），复杂度可控。

**关键概念**：
- **活跃范围（live range）**：变量从声明到最后一次使用的 IP 区间 `[defIP, lastUseIP]`
- **寄存器回收**：当变量的 `lastUseIP < currentIP` 时，其寄存器可被后续变量复用
- **跨 await 变量提升**：如果变量在 `wait` / `wait_for` 前定义、后使用，必须使用持久寄存器（local 区 r16-47），不能使用 temp 区

**两阶段策略**（与当前两遍编译兼容）：

1. **Pass 1（已有）**：构建函数表（不变）
2. **Pass 2 — 第一轮：分析**：遍历 AST 收集每个变量的引用点 → 计算活跃范围 → 标记跨 await 变量
3. **Pass 2 — 第二轮：emit**：使用活跃范围信息进行寄存器分配 + 指令生成

**简化约束**（降低实现复杂度）：
- 不做跨函数分析（每个函数独立分析）
- 不做 SSA / phi 节点（脚本语言简单，线性扫描即可）
- struct 变量暂不拆分（整块分配整块回收）
- 活跃范围以 AST 节点的**声明顺序**为基础，非 IP 精确

### 具体变更

- [x] A.1 **新增 `LiveRange` 结构**：`struct LiveRange { string Name; int Register; int DefOrder; int LastUseOrder; bool CrossesAwait; int FieldCount; }` — 记录每个变量的活跃范围
- [x] A.2 **新增 `AnalyzeVariableLifetimes(FuncDecl)` 方法**：遍历函数 AST 的所有语句和表达式，为每个变量记录定义点和所有使用点，标记是否跨越 `wait` / `wait_for`
- [x] A.3 **新增 `RegisterAllocator` 辅助类**（或内嵌逻辑）：基于活跃范围的线性扫描分配器。释放的寄存器进入 free list，后续 DeclareVar 优先从 free list 分配
- [x] A.4 **修改 `DeclareVar()` 方法**：从 free list 中查找合适的空闲寄存器（struct 需连续槽位），如无空闲则线性递增（保持向后兼容行为）
- [x] A.5 **修改 `CompileFunction()` 入口**：在编译函数体前先调用 `AnalyzeVariableLifetimes()`，生成活跃范围信息
- [x] A.6 **记录 `_callerWindowSize` 精确值**：追踪函数实际使用的最大寄存器编号（用于 FO6 未来使用和 FunctionEntry.LocalRegCount 精确化）
- [x] A.7 **测试 F4-01**：两个非重叠生命周期的变量 → 复用同一个寄存器
- [x] A.8 **测试 F4-02**：跨 await 变量 → 使用 local 区寄存器（不被回收）
- [x] A.9 **测试 F4-03**：struct 变量回收 → 连续寄存器整块释放
- [x] A.10 **测试 F4-04**：寄存器复用不改变执行结果（端到端正确性）

### 验收标准

- 非重叠生命周期变量可复用寄存器
- 跨 await 变量正确保持在持久寄存器
- struct 变量连续寄存器整块管理
- 所有现有 315 项测试无回归
- `FunctionEntry.LocalRegCount` 精确反映实际使用寄存器数

---

## Sub-task B: O5 常量折叠

### 意图

当前 `var x = 2 + 3` 生成 3 条指令（LOAD_CONST 2 → LOAD_CONST 3 → ADD）。常量折叠在编译期计算结果，只 emit 1 条 LOAD_CONST 5。

### 具体变更

- [x] B.1 **新增 `TryFoldConstant(ASTNode expr, out Number value)` 方法**：递归检测纯常量表达式（整数/浮点字面量 + 四则运算 + 比较 + 布尔运算），返回折叠后的值
- [x] B.2 **修改 `CompileExpr()` 入口**：在处理二元/一元表达式前，先调用 `TryFoldConstant()`。如成功折叠，直接 `LOAD_CONST` 到 temp 寄存器（或 dest-reg）
- [x] B.3 **测试 O5-01**：`var x = 2 + 3` → 只生成 1 条 LOAD_CONST（值 = 5），无 ADD 指令
- [x] B.4 **测试 O5-02**：`var x = 10 > 5` → 只生成 1 条 LOAD_CONST（值 = 1）
- [x] B.5 **测试 O5-03**：`var x = a + 3`（含变量引用）→ 不折叠，正常 emit

### 验收标准

- 纯常量表达式折叠为单条 LOAD_CONST
- 含变量引用的表达式不受影响
- 不改变执行结果

---

## Sub-task C: O4 目标寄存器传递（dest-reg hint）+ O7 Syscall 结果直达

### 意图

当前 `var x = a + b` 生成：`ADD temp, regA, regB` → `MOVE varX, temp`。
如果编译器知道结果要存入 varX，可以直接生成 `ADD varX, regA, regB`，省去 MOVE。

O7 是 O4 的延伸：`var x = SomeSyscall(args)` 当前生成 `SYSCALL` → `MOVE r0, varX`。
如果 Syscall 结果约定直接写入目标寄存器（或编译器知道目标），可省去 MOVE。

### 设计方向

**核心改动**：`CompileExpr(ASTNode expr)` → `CompileExpr(ASTNode expr, int destReg = -1)`

- `destReg = -1`：行为与当前一致（自行分配 temp）
- `destReg >= 0`：将结果直接写入 destReg，省去 MOVE

**影响范围**：
- `CompileVarDecl()`：传入 `destReg = varReg`
- `CompileAssign()`：传入 `destReg = targetReg`
- `BinaryExpr` 分支：如有 destReg，用 destReg 替代 AllocTemp()
- `CallExpr`（函数调用返回值）分支：CALL 后 r0 → MOVE destReg（或利用 FO5 优化）
- `SyscallExpr`（Syscall 返回值）分支：SYSCALL 后 r0 → MOVE destReg（O7）

### 具体变更

- [x] C.1 **修改 `CompileExpr()` 签名**：增加 `int destReg = -1` 参数
- [x] C.2 **修改 `BinaryExpr` 分支**：如 `destReg >= 0`，用 destReg 替代 `AllocTemp()` 作为 emit 目标
- [x] C.3 **修改 `UnaryExpr` 分支**：同上
- [x] C.4 **修改 `CompileVarDecl()`**：`CompileExpr(init, destReg: varReg)`，省去后续 MOVE
- [x] C.5 **修改 `CompileAssign()`**：传入 `destReg = targetReg`
- [x] C.6 **O7 — 修改 Syscall 返回值处理**：SYSCALL 后直接 `MOVE r0 → destReg`（如 destReg 有效），而非 `MOVE r0 → temp` → `MOVE temp → target`
- [x] C.7 **测试 O4-01**：`var x = a + b` → 生成 `ADD varX, regA, regB`（无额外 MOVE）
- [x] C.8 **测试 O4-02**：`x = a * b` → 结果直接写入 x 的寄存器
- [x] C.9 **测试 O7-01**：`var x = SomeSyscall(args)` → SYSCALL 后只有 1 条 MOVE（r0 → varX）
- [x] C.10 **正确性回归**：所有 315 项现有测试无回归

### 验收标准

- `var x = expr` 类赋值减少 1-2 条 MOVE
- Syscall 返回值直接到目标寄存器
- 不影响非赋值场景的表达式编译
- 所有现有测试无回归

---

## Sub-task D: DBG1 源码映射表（Source Map）

### 意图

编译器生成 IP → 行号的映射表，作为断点、单步、调用栈显示的基础数据。

### 设计方向（来自决策 D-05）

- **格式**：`int[]` 平行数组，索引 = IP，值 = lineNumber（省略列号）
- **时机**：编译器每次 `Emit()` 时记录当前行号
- **存储**：`VMProgram.SourceMap`（`#if FFVM_SCRIPT_DEBUG` 包裹）
- **约束**：Source Map 在所有优化 pass 之后、`_instructions` 冻结时最终生成

### 具体变更

- [x] D.1 **BytecodeCompiler — 新增 `_sourceLines` 列表**：`List<int>`，与 `_instructions` 平行，每次 `Emit()` 时追加当前行号
- [x] D.2 **BytecodeCompiler — 追踪当前行号**：新增 `_currentLine` 字段，在编译每条语句时从 AST 节点的 `Line` 属性更新
- [x] D.3 **VMProgram — 新增 `SourceMap` 字段**：`int[] SourceMap`（可选，调试构建时填充）
- [x] D.4 **VMProgram — 构造函数扩展**：增加可选 `int[] sourceMap` 参数
- [x] D.5 **BytecodeCompiler.Compile() — 输出 Source Map**：在构造 VMProgram 时传入 `_sourceLines.ToArray()`
- [x] D.6 **测试 DBG1_T01**：编译已知脚本（多行 var + syscall + wait + if）→ 断言每个关键 IP（LOAD_CONST / SYSCALL / WAIT）的 lineNumber 与源码一致
- [x] D.7 **测试 DBG1_T02**：Source Map 长度 == Instructions 长度

### 验收标准

- Source Map 长度与指令数一致
- 每条指令的行号映射正确
- 不影响非调试构建（VMProgram 构造函数向后兼容）

---

## Sub-task E: DBG2 符号表 Phase 1（Symbol Table）

### 意图

记录每个变量的名称 → 寄存器槽位映射，使调试器能在断点时按变量名查看值。
Phase 1 不含生命周期范围（D-06 决策：与 F4 解耦，分两阶段）。

### 设计方向

- **数据结构**：`SymbolEntry { string Name; int Register; int FieldCount; string[] FieldNames; string ScopeFunctionName; }`
- **来源**：编译器 `_variables` + `_structVarTypes` + `_structTypes`
- **存储**：`VMProgram.SymbolTable`（`#if FFVM_SCRIPT_DEBUG` 包裹）

### 具体变更

- [x] E.1 **新增 `SymbolEntry` 结构**：在 VMProgram.cs 或独立文件中定义
- [x] E.2 **BytecodeCompiler — 收集符号信息**：每次 `DeclareVar()` 时记录 `SymbolEntry`（名称、寄存器、struct 字段信息）
- [x] E.3 **VMProgram — 新增 `SymbolTable` 字段**：`SymbolEntry[] SymbolTable`（可选）
- [x] E.4 **VMProgram — 构造函数扩展**：增加可选 `SymbolEntry[] symbolTable` 参数
- [x] E.5 **BytecodeCompiler.Compile() — 输出符号表**：在构造 VMProgram 时传入收集的符号列表
- [x] E.6 **测试 DBG2_T01**：编译含 `var a = 1; var b = 2;` 的脚本 → 符号表有 2 项，名称和寄存器正确
- [x] E.7 **测试 DBG2_T02**：编译含 struct 变量的脚本 → 符号表记录 struct 字段名和起始寄存器

### 验收标准

- 符号表正确记录每个变量的名称和寄存器
- struct 变量正确记录字段名和连续寄存器布局
- 不影响非调试构建

---

## Sub-task F: R7 理想方案 — _pendingCalls 自动切换 Dictionary

### 意图

当前 `_pendingCalls` 是 `List<PendingCall>`，回填时线性扫描。百函数级模块下 <1ms，无实际问题。
但理想方案是在函数数量 >50 时自动切换为按名称索引的 Dictionary，消除理论瓶颈。

### 具体变更

- [x] F.1 **修改回填逻辑**：当 `_pendingCalls.Count > 50` 时，先构建 `Dictionary<string, List<int>>` 索引（functionName → instructionIPs），然后按 `_functionTable` 逐函数回填，避免线性扫描
- [x] F.2 **测试 R7-01**：100 个函数（含大量前向引用）→ 编译成功，回填正确
- [x] F.3 **测试 R7-02**：<50 个函数 → 走原有 List 路径，行为不变

### 验收标准

- >50 函数自动切换策略，对用户透明
- ≤50 函数行为不变
- 回填结果正确

---

## Sub-task G: R8 理想方案 — 编译器禁止 Cleanup 块内函数调用

### 意图

G6 已禁止 Cleanup 块内 `wait`/`wait_for`。R8 进一步禁止 Cleanup 块内函数调用，
因为函数调用可能修改 CallStack/CleanupDepth 状态，在 Cleanup 路径中可能导致语义不一致。

### 具体变更

- [x] G.1 **修改 `CompileCallExpr()` / 函数调用编译路径**：在 emit `CALL` 前检查 `_inCleanupBlock`，若 `true` 则 `_errors.Add("Cannot call functions inside a cleanup block (defer/using)")` 并跳过 emit
- [x] G.2 **测试 R8-01**：`defer { someFunc() }` → 编译报错
- [x] G.3 **测试 R8-02**：正常函数体内的 `someFunc()` → 编译成功
- [x] G.4 **测试 R8-03**：`using SomeSyscall(args) { someFunc() }` → 编译成功（using body 不是 cleanup 块）

### 验收标准

- Cleanup 块内函数调用 → 编译报错
- 正常代码中的函数调用 → 不受影响
- using body 内的函数调用 → 仍然合法
- 错误信息清晰

---

## Sub-task H: FO4 参数就位检测 + FO5 返回值直达

### 意图

**FO4**：当函数参数已在正确的 scratch zone 寄存器 r[i] 中时，跳过 MOVE。
**FO5**：函数调用返回值（r0）结合 dest-reg hint 直接写入目标寄存器。

### 具体变更

- [x] H.1 **FO4 — 修改函数调用参数准备逻辑**：emit CALL 前，检查第 i 个参数值是否已在 `r[i]`（考虑 RegisterBase 偏移）。如已就位，跳过该 MOVE
- [x] H.2 **FO5 — 修改函数调用返回值处理**：如 `destReg >= 0` 且 destReg != r0，emit 单条 `MOVE destReg, r0`。如 destReg == r0，零指令
- [x] H.3 **测试 FO4-01**：`func f(a) { ... } f(x)` 当 x 已在 r0 → 无参数 MOVE
- [x] H.4 **测试 FO5-01**：`var result = f(x)` → 返回值从 r0 直接 MOVE 到 result 寄存器（1 条 MOVE，非 2 条）

### 验收标准

- 已就位参数跳过 MOVE
- 返回值直达目标寄存器
- 所有测试无回归

---

## Sub-task I: FO7 调用栈深度静态分析

### 意图

编译器构建函数调用图，计算最大调用深度。若超过 `MaxCallDepth = 16` 则编译期报错（而非运行时 panic）。
浅调用可标记用于运行时跳过深度检查（优化）。

### 具体变更

- [x] I.1 **新增 `AnalyzeCallDepth()` 方法**：遍历 `_functionTable` 和 `_funcDecls`，构建调用图，计算从 entry 函数出发的最大深度
- [x] I.2 **在 `Compile()` 末尾调用 `AnalyzeCallDepth()`**：若 max_depth > MaxCallDepth → `_errors.Add(...)`
- [x] I.3 **在 `FunctionEntry` 中记录最大深度**：或在 VMProgram 中记录全局 `MaxObservedCallDepth`
- [x] I.4 **测试 FO7-01**：调用深度 = 3（a→b→c）→ 编译成功，记录 depth = 3
- [x] I.5 **测试 FO7-02**：调用深度 > MaxCallDepth（构造深链）→ 编译报错
- [x] I.6 **测试 FO7-03**：递归调用 → 编译器检测环路，报告潜在无限递归（或标记为动态检测）

### 验收标准

- 深度超限在编译期捕获
- 正常深度的调用编译成功
- 递归场景有合理处理（至少不崩溃）

---

## Sub-task J: O3 消除冗余 IP 边界检查

### 意图

当前 `VMWorld.ExecuteInstance()` 中可能存在每条指令执行前的冗余 IP 范围检查。
如果解释循环的结构保证 IP 在合法范围内（由 RETURN/JUMP 等指令控制），部分冗余检查可消除。

### 具体变更

- [x] J.1 **审查 `VMWorld.ExecuteInstance()` 热路径**：识别可安全移除的 IP 边界检查
- [x] J.2 **移除或合并冗余检查**：保留必要的安全检查（如非法跳转保护），消除每条指令执行前的冗余检查
- [x] J.3 **测试**：性能基准 B01-B05 仍然通过，所有行为正确

### 验收标准

- 热路径减少不必要的分支
- 安全检查保留（非法跳转仍被捕获）
- 所有测试无回归

---

## Sub-task K: Benchmark 验证 + 回归测试

### 意图

所有优化完成后，运行完整测试套件和性能基准，验证正确性和优化效果。

### 具体变更

- [x] K.1 **运行全部测试**：确认所有现有 315+ 项 Assert 无回归
- [x] K.2 **运行 B01-B05 性能基准**：记录优化前后的 ratio 变化
- [x] K.3 **新增测试统计**：记录本步骤新增的测试数量（F4 / O5 / O4 / DBG1 / DBG2 / R7 / R8 / FO4 / FO5 / FO7 / O3 各自的测试）
- [x] K.4 **生成 benchmark diff**：与 Step 10 Pre 的基准数据对比

### 验收标准

- 所有测试通过（现有 + 新增）
- 编译脚本性能基准改善（预期从 5-7x 降至 4-5x，主要由 O4 贡献 15-20% 指令数减少）
- 新增 Assert 数 ≥ 20

---

## Sub-task L: 文档更新

### 具体变更

- [x] L.1 **VM_Summary.md §七 推进顺序**：标记 F4 + 自然优化 + 调试 Phase 1 ✅，更新 Assert 总数
- [x] L.2 **VM_Summary.md §5.1 已完成**：更新编译器描述（寄存器生命周期分析 + dest-reg hint + 常量折叠 + Source Map + 符号表）
- [x] L.3 **VM_Summary.md §5.2 未完成**：标记 F4 ✅
- [x] L.4 **Outlook_And_Risks.md §一 确定执行**：更新 F4 状态为 ✅
- [x] L.5 **Outlook_And_Risks.md §三.1 自然优化**：标记 O3/O4/O5/O7/FO4/FO5/FO7 ✅
- [x] L.6 **Outlook_And_Risks.md §四 风险**：标记 R7 ✅ 理想方案已实施、R8 ✅ 理想方案已实施
- [x] L.7 **本文件**：更新各子任务状态为 ✅，记录最终测试数量和性能基准结果

---

## 四、风险分析

| # | 风险 | 影响 | 缓解措施 |
|---|------|------|---------|
| R-A1 | F4 活跃范围分析改变寄存器分配 → 大面积测试回归 | 中 | 回退策略：如回归严重，保留旧分配路径作为 fallback（`useLifetimeAnalysis = true` 开关） |
| R-A2 | dest-reg hint 在复杂嵌套表达式中引入寄存器冲突 | 中 | 仅在赋值语句（VarDecl/Assign）顶层使用 hint，嵌套子表达式内仍使用 AllocTemp |
| R-A3 | Source Map 在优化 pass 后失效 | 低 | 约束：Source Map 记录的行号在 Emit 时即确定，优化 pass 不重排指令顺序（当前无优化 pass 重排） |
| R-A4 | 常量折叠在 Fix64 模式下精度不同 | 低 | 折叠逻辑使用 Number 结构（自动走 Fix64 或 float），与运行时一致 |
| R-A5 | FO7 调用图分析遇到递归 → 无限循环 | 低 | 用 visited 集合检测环路，标记为"动态检测"而非编译期深度确定 |
| R-A6 | _pendingCalls Dictionary 切换引入边界 bug | 极低 | 阈值 >50 很保守，且新旧路径结果可交叉验证 |

---

## 五、验收总览

| 条目 | 来源 | 描述 |
|------|------|------|
| F4 | VM_Summary §七、Outlook §一 | 寄存器生命周期分析 + 寄存器复用 |
| O3 | Outlook §三.1 | 消除冗余 IP 边界检查 |
| O4 | Outlook §三.1 | 目标寄存器传递（dest-reg hint） |
| O5 | Outlook §三.1 | 常量折叠 |
| O7 | Outlook §三.1 | Syscall 结果直达 |
| FO4 | Step8 §七 | 参数就位检测 |
| FO5 | Step8 §七 | 返回值直达 |
| FO7 | Step8 §七 | 调用栈深度静态分析 |
| DBG1 | Outlook §二.5 | 源码映射表 |
| DBG2 | Outlook §二.5 | 符号表 Phase 1 |
| R7 | Outlook §四.1 | _pendingCalls 自动切换 Dictionary |
| R8 | Outlook §四.1 | 编译器禁止 Cleanup 块内函数调用 |
| 回归 | — | 现有 315 项 Assert 无回归 |
| Benchmark | — | 编译脚本性能基准改善 |

全部通过后，F4 阶段闭环。下一步进入 V5（帧内 Profiler，待真实 Syscall 接入）或功能补全（S4/FF5，如需），然后 Step 10（编辑器流程图投影）。
