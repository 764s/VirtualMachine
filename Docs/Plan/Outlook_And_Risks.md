# 功能展望、优化展望与风险点汇总

> **定位**：本文件汇总散布于各步骤子计划中的全部**功能展望**、**优化展望**和**已识别风险**，
> 按类别整理并统一编号，便于在进入步骤 10（编辑器流程图投影）前进行全局评估与排序。
>
> **引用关系**：各条目附有来源链接，可回溯到原始设计详情。
>
> **维护原则**：
> - 新增展望 / 风险时，在此文件追加并分配唯一编号。
> - 条目状态变化时同步更新。
> - VM_Summary.md §十三 提供指向本文件的入口索引。

---

## 一、确定执行（步骤 10 前必须就位）

这些不是展望——它们是**确定要做**但被延期到步骤 10 前的必须项。

| ID | 来源 | 内容 | 说明 | 状态 |
|----|------|------|------|------|
| **C4** | [§3.3 C4](../VM_Summary.md#33-cleanup-机制using理想defer逃生舱) | 编译器 "requires cleanup" 强制检查 | 标记了 `requires_cleanup` 的 Syscall 若既未配 `using` 也未配 `defer`，编译报错 | ✅ 已完成 |
| **F4** | [Step8 §八](Step8_FunctionCall.md#八依赖关系总览) | 编译器寄存器生命周期分析 + 跨 await 变量提升 | 来源：VM_Tracer_Bullet.md §十二 第 2 项"寄存器复用" | ✅ 已完成 |
| **G5** | [§11.1 G5](../VM_Summary.md#111-代码缺口) | C4 对应的代码缺口（同上） | — | ✅ 已完成（同 C4） |
| **G6** | [§11.1 G6](../VM_Summary.md#111-代码缺口) | `defer`/`using` Cleanup 块内禁止 `wait`/`wait_for` 编译检查 | 语义上 Cleanup 块不应挂起 | ✅ 已完成 |
| **V5** | [§4.6 V5](../VM_Summary.md#v5-帧内-profiler-验证真实-syscall-接入后--待前置) | 帧内 Profiler 验证（真实 Syscall 接入后） | 必须在步骤 10 前通过 | ⚪ 待前置条件 |

---

## 二、功能展望

按来源分组，每组内按优先级排列。

### 2.1 Cleanup / using 相关（来源：步骤 7）

| ID | 内容 | 触发时机 | 详情 |
|----|------|----------|------|
| **C5** | Cleanup 块执行超时保护 | 待定 | 防止 Cleanup 块内死循环阻塞实例回收。[§3.3 C5](../VM_Summary.md#33-cleanup-机制using理想defer逃生舱) |
| **C6** | 嵌套 `using` 作用域优化（合并相邻 PUSH_CLEANUP） | 待定 | 性能优化，非功能阻塞。[§3.3 C6](../VM_Summary.md#33-cleanup-机制using理想defer逃生舱) |

### 2.2 函数调用相关（来源：步骤 8）

> 详细设计见 [Step8_FunctionCall.md §六 功能展望](Step8_FunctionCall.md#六功能展望)

| ID | 内容 | 触发时机 | 复杂度 |
|----|------|----------|--------|
| **FF1** | 跨模块函数调用 | 真实业务需要模块间函数共享时 | 中（需 VMModuleTable 扩展 + CALL_EXT OpCode） |
| **FF2** | 函数作为 Syscall 参数（回调模式） | 需要宿主驱动的回调场景时 | 中（约定回调协议；受 Rule 6 约束只能传 IP 整数） |
| **FF3** | 可选参数与默认值 | 提升开发体验时 | 低（仅编译器变更） |
| **FF4** | 多返回值 | 业务需要函数返回多值时（如 GetPosition → x,y） | 中（scratch zone 多寄存器 + 解构语法） |
| **FF5** | defer 在非 entry 函数中的正确执行 | 非 entry 函数使用 defer/using 时 | 中（RET_FUNC 需与 Cleanup 链对齐） |

### 2.3 结构体相关（来源：步骤 9）

> 详细设计见 [Step9_StructFlatten.md §六 功能展望](Step9_StructFlatten.md#六功能展望)

| ID | 内容 | 触发时机 | 复杂度 |
|----|------|----------|--------|
| **S4** | 结构体作为函数参数 / 返回值的寄存器传递 | 最晚步骤 10 前如需编辑器展示结构体节点 | 中（与 FO6 自适应窗口联合评估） |
| **SN1** | 嵌套结构体（struct 字段为另一个 struct） | 后续步骤按需 | 中（递归拍平为连续寄存器） |
| **SN2** | 结构体字面量构造语法 | 后续步骤按需 | 低（编译器 sugar） |

### 2.4 全局 / 跨步骤展望

| ID | 内容 | 触发时机 | 来源 |
|----|------|----------|------|
| **H1** | Handle64 批处理协议 | 最晚于真实多目标业务接入前 | [§七 推进顺序](../VM_Summary.md#七推进顺序串行计划)（VM_Tracer_Bullet §十二 第 4 项） |
| **BB1** | 黑板 Key 编译期自动分配 ID + 静态映射表 | 编译器成熟后 | [§六 决策妥协表](../VM_Summary.md#六决策妥协表为什么当前这样将来如何补全) |
| **PR1** | Paired Syscall 支持带参反向调用 | 需要带参释放场景时 | [§六 决策妥协表](../VM_Summary.md#六决策妥协表为什么当前这样将来如何补全) |
| **FIX1** | Fix64 模式 (`USE_FIXPOINT`) 独立构建验证 | 正式测试前 | [§11.2 T5](../VM_Summary.md#112-测试缺口) |
| **DM1** | VM 编排表现脚本（双轨模式：部分实例不参与快照） | 需要复杂镜头/特效序列时 | [§3.4](../VM_Summary.md#34-全程-fix64表现走-syscall) |

### 2.5 脚本调试（DBG 系列）

> 来源：[§六 决策妥协表](../VM_Summary.md#六决策妥协表为什么当前这样将来如何补全) "无调试符号 / 源码映射"
>
> 脚本调试是编辑器阶段的核心开发体验能力，涉及编译器、运行时、外部工具三层改动。

#### 设计方向确认

经讨论确定以下方向：

1. **真实宿主断点**：脚本断点触发宿主（C#）的真实断点（`System.Diagnostics.Debugger.Break()` 或条件断点），
   调试体验与 C# 原生调试一致，只是通过 Source Map / Symbol Table 将视角包装为脚本级别。
   由于是真实断点，整个进程冻结，**不存在多实例调度问题**。
2. **禁止调试时修改**：调试暂停期间禁止修改脚本源码（防止 Source Map 失效），可通过编辑器标志控制。
3. **外部工具支持（DAP + LSP）**：
   - 调试通过 **DAP（Debug Adapter Protocol）** 接入外部 IDE（VS Code 等），无需自研调试 UI。
   - 语言智能通过 **LSP（Language Server Protocol）** 实现（语法高亮、补全、实时报错、符号分析），见 §2.6。

#### 子项

| ID | 内容 | 层级 | 复杂度 | 说明 |
|----|------|------|--------|------|
| **DBG1** | 源码映射表（Source Map） | 编译器 | 中 | 编译器追踪每条 emit 指令对应的源码行号，生成 `IP → line` 映射表，存入 `VMProgram.SourceMap` | ✅ 已完成 |
| **DBG2** | 符号表（Symbol Table） | 编译器 | 中 | 记录每个变量名 → 寄存器槽位 + struct 字段信息，存入 `VMProgram.SymbolTable` | ✅ Phase 1 完成 |
| **DBG3** | 宿主断点桥接（Host Breakpoint Bridge） | 运行时 | 低 | ScriptDebugger：行号断点集合 + 回调触发，VMWorld.ExecuteInstance 中 null-check 防御式集成 | ✅ 已完成 |
| **DBG4** | 单步映射（Step Mapping） | 运行时 | 中 | 基于 Source Map 的行级单步标记：Step Over = 下一行 IP 设临时断点；Step Into/Out 感知 CALL / RET_FUNC |
| **DBG5** | 变量查看适配器（Variable Display Adapter） | 运行时 | 低 | ScriptDebugger.GetVariables()：根据 SymbolTable + RegisterBase + ScopeFunctionName 返回当前作用域变量 | ✅ 已完成 |
| **DBG6** | 调用栈查看（Call Stack Inspection） | 运行时 | 低 | ScriptDebugger.GetCallStack()：遍历 CallStack 帧 + SourceMap 映射 → 函数名+行号列表 | ✅ 已完成 |
| **DBG7** | DAP 适配器（Debug Adapter Protocol） | 接口 | 中 | 实现 DAP 协议，将 DBG3-DBG6 能力暴露给外部 IDE（VS Code 等），无需自研调试 UI |

> **注**：原 DBG7（编辑器调试 UI）和 DBG8（自研调试协议）合并为新 DBG7（DAP 适配器），
> 调试 UI 由外部 IDE 提供，不再自研。编号从 8 项缩减为 7 项。

#### 前置任务与依赖关系

```
F4（寄存器生命周期分析）          ← 已确认，步骤 10 前必须
  │  DBG2 符号表需要 F4 的活跃范围信息
  ↓
DBG1（源码映射表）                ← 基础设施，所有调试功能的前提
  │
  ├→ DBG2（符号表）               ← DBG5 变量查看的前提
  │
  ├→ DBG3（宿主断点桥接）         ← 核心调试能力（实现极简：IP 命中 → Debugger.Break()）
  │    │
  │    └→ DBG4（单步映射）        ← 依赖断点桥接 + Source Map
  │
  ├→ DBG5（变量查看适配器）       ← 依赖 DBG2
  │
  └→ DBG6（调用栈查看）           ← 依赖函数表 + Source Map
        │
        ↓
DBG7（DAP 适配器）                ← DBG3-DBG6 的 DAP 协议封装 → 接入外部 IDE
```

#### 建议实施策略

1. **第一阶段（编译器侧，已与 F4 合并完成）**：DBG1 + DBG2。✅ 编译器 emit 源码映射表和符号表已实现。
2. **第二阶段（运行时侧）**：DBG3 + DBG5 + DBG6。宿主断点桥接 + 变量查看 + 调用栈查看。由于采用真实断点方案，DBG3 实现极简（几行代码），可在 Step 10 前先以测试方式验证。
3. **第三阶段（DAP 接入）**：DBG4 + DBG7。单步映射 + DAP 适配器。DAP 适配器将 DBG3-DBG6 封装为标准协议，外部 IDE（VS Code 等）直接使用。

#### 补充说明：与自研调试的对比

| 维度 | 自研调试（原方案） | 真实宿主断点 + DAP（新方案） |
|------|-------------------|---------------------------|
| 断点机制 | VM Tick 循环检测命中 | `Debugger.Break()` 真实断点 |
| 单步执行 | VM 层行级模拟 | 宿主断点 + Source Map 行映射 |
| 调试 UI | 自研编辑器面板（DBG7, 高复杂度） | 外部 IDE 提供（VS Code 等） |
| 多实例冻结 | 需自行设计冻结策略 | 真实断点冻结整个进程，天然解决 |
| 调试时修改 | 需自行检测 | 编辑器标志禁止即可 |
| 协议标准 | 自研（DBG8） | DAP 标准协议 |
| 编辑器外调试 | 不支持 | 支持（任何 DAP 客户端均可） |

### 2.6 语言服务（LSP 系列 — 外部 IDE 智能支持）

> 来源：脚本调试讨论确认需要外部工具支持，包括语法高亮、补全、实时报错、符号分析等。
>
> 语言服务通过 **LSP（Language Server Protocol）** 实现，与 DAP 调试互补，
> 共同构成外部 IDE 对脚本语言的完整支持。

#### 子项

| ID | 内容 | 层级 | 复杂度 | 说明 |
|----|------|------|--------|------|
| **LSP1** | LSP Server 核心框架 | 基础设施 | 中 | 实现 LSP 协议的 JSON-RPC 通信、生命周期管理（initialize/shutdown）、文档同步（didOpen/didChange） |
| **LSP2** | 语法高亮（Semantic Tokens / TextMate Grammar） | 编辑器 | 低 | 为 16 个关键字 + 运算符 + 字面量 + 注释定义 token 类型；TextMate grammar（.tmLanguage）提供基础着色，Semantic Tokens 提供上下文感知着色 |
| **LSP3** | 实时诊断（Diagnostics） | 编译器 | 中 | 增量编译 → 错误/警告实时推送（`textDocument/publishDiagnostics`）；复用 `BytecodeCompiler._errors` 列表 + Source Map 定位 |
| **LSP4** | 符号分析（Go-to-Definition / References / Hover / Document Symbols） | 编译器 | 中 | 基于 AST + Symbol Table 实现 `textDocument/definition`、`textDocument/references`、`textDocument/hover`、`textDocument/documentSymbol` | ✅ 已完成 |
| **LSP5** | 代码补全（Completion） | 编译器 | 中 | 关键字 + 作用域内变量 + 函数名 + Syscall 名 + struct 字段补全（`textDocument/completion`）；当 LSP6 声明可用时，补全项包含 Syscall 参数签名 | ✅ 已完成 |
| **LSP6** | Syscall 声明协议（Declaration Protocol） | 基础设施 | 中 | 允许宿主通过声明文件（`.ffvm.d.json`）或注册 API 声明 Syscall 签名（参数名、参数类型、返回类型、说明文本），为 LSP5/LSP7 提供宿主方法元数据 | ✅ 已完成 → [B-α1](Step_B_Alpha1_LSP6_SyscallDecl.md) |
| **LSP7** | 参数提示（Signature Help） | 编译器 | 中 | 输入 `funcName(` 或 `,` 时显示参数列表与当前参数高亮（`textDocument/signatureHelp`）；覆盖用户函数 + Syscall（需 LSP6 声明） |

#### 前置任务与依赖关系

```
DBG1（源码映射表） + DBG2（符号表）  ← LSP3/LSP4/LSP5 的数据基础
  │
  ↓
LSP1（LSP Server 核心框架）           ← 所有 LSP 功能的通信基础
  │
  ├→ LSP2（语法高亮）                ← 独立，仅需 token 定义
  │
  ├→ LSP3（实时诊断）                ← 依赖编译器增量化
  │
  ├→ LSP4（符号分析）                ← 依赖 DBG2 符号表
  │
  ├→ LSP5（代码补全）                ← 依赖符号表 + SyscallTable；可选依赖 LSP6（增强 Syscall 补全）
  │
  ├→ LSP6（Syscall 声明协议）        ← 独立基础设施；SyscallTable 扩展签名元数据
  │
  └→ LSP7（参数提示）                ← 依赖 LSP6（Syscall 签名）+ AST（用户函数签名）
```

#### 建议实施策略

1. **LSP2（语法高亮）** 可独立最早实施：仅需编写 TextMate grammar 文件，无需 LSP Server。
2. **LSP1 + LSP3（核心 + 诊断）** 在 DBG1/DBG2 完成后实施：复用编译器错误列表，最快实现"实时报错"。
3. **LSP4 + LSP5（符号 + 补全）** 随编译器数据丰富后逐步完善。
4. **LSP6（声明协议）** 可与 LSP5 并行或在 LSP5 基础补全完成后叠加；核心工作量在 `SyscallTable` 扩展签名元数据 + 声明文件加载。
5. **LSP7（参数提示）** 在 LSP6 完成后实施：用户函数签名从 AST 获取，Syscall 签名从 LSP6 声明获取。

---

## 三、优化展望

### 3.0 分类说明

优化分为两类，**优先安排自然优化**：

| 类别 | 定义 | 驱动方式 |
|------|------|----------|
| **自然优化** | 在实现计划内功能（F4 寄存器生命周期分析、编译器成熟化）过程中自然产生的改进，边际成本极低 | 随功能实现顺带完成 |
| **调整型优化** | 以性能提升为首要目标，需专门投入时间回头调整已有代码或架构 | Benchmark 数据驱动，确认瓶颈后再投入 |

**原则**：自然优化在实现对应功能时一并完成（零额外排期）；调整型优化按 benchmark 结果排序，逐项验证收益后再推进。

### 3.1 自然优化（7 项，随功能实现顺带完成）

这些优化是计划内工作（尤其 F4 寄存器生命周期分析 + 编译器成熟化）的自然副产品，边际成本极低，应在实现对应功能时一并完成。

> 详细方案：通用优化见 [VM_Optimization_Outlook.md](../Refs/VM_Optimization_Outlook.md)；函数调用优化见 [Step8_FunctionCall.md §七](Step8_FunctionCall.md#七性能优化展望)

| ID | 内容 | 预期收益 | 复杂度 | 自然来源 | 状态 |
|----|------|---------|--------|----------|------|
| **O4** | 目标寄存器传递（dest-reg hint） | 指令数 ~15-20% 减少 | 中 | F4 寄存器分析的直接产出 | ✅ |
| **O5** | 常量折叠 | 常量表达式 3→1 条 | 低 | 编译器 `CompileExpr` 入口一行检测 | ✅ |
| **O7** | Syscall 结果直达 | 每次 Syscall -1~2 条 MOVE | 低 | O4 dest-reg hint 的延伸 | ✅ |
| **O3** | 消除冗余 IP 边界检查 | 热路径 ~5-10% | 极低 | 审查后确认仅 1 处必要检查 | ✅ |
| **FO4** | 参数就位检测（skip MOVE if arg already in place） | 每已就位参数 -1 MOVE | 低 | 调用约定优化，编译器自然判断 | ✅ |
| **FO5** | 返回值直达 | 每次带返回值调用 -1~2 MOVE | 低 | 同 FO4，调用约定自然改进 | ✅ |
| **FO7** | 调用栈深度静态分析 | 编译期捕获溢出 + 浅调用省分支 | 中 | 编译器已有函数表，自然扩展分析 | ✅ |

**时机**：已随 F4 全部完成。

### 3.2 调整型优化 — 解释器热路径（Benchmark 驱动）

> 详细方案与代码示例见 [VM_Optimization_Outlook.md](../Refs/VM_Optimization_Outlook.md)

| Tier | ID | 内容 | 预期收益 | 复杂度 | 状态 |
|------|----|------|---------|--------|------|
| **1** | **O1** | 消除逐次 `fixed` 钉住 | dispatch ~9% (.NET JIT)；IL2CPP 预期 30-50% | 低 | ✅ |
| **1** | **O2** | OpCode 连续编号 → 强制跳转表 | dispatch ~20% 加速 | 低 | ✅ |
| **2** | **O6** | Peephole 优化 pass | ~5-10% 指令数减少 | 中 | ⏳ |
| **3** | **O8** | 指令压缩 16B → 4B | L1 缓存 10-20% 加速 | 高 | ⏳ |

**推荐顺序**：O1 ✅ → O2 ✅ → O6 → 视需要 O8。B3 Tier 1 详情见 [Step_B3_Optimization_Tier1.md](Step_B3_Optimization_Tier1.md)。

### 3.3 调整型优化 — 调度 / 快照 / 运行时

| Tier | ID | 内容 | 预期收益 | 复杂度 |
|------|----|------|---------|--------|
| **4** | **O9** | 活跃实例链表 | 稀疏场景 ~85% 无效遍历减少 | 低 |
| **4** | **O10** | 快照只拷贝活跃实例 | 快照 80-90% 数据量减少 | 中 |
| **5** | **O11** | Syscall 函数指针（`delegate*`） | Syscall 调用 ~30% 加速 | 中 |
| **5** | **O12** | Number 原始字段比较优化 | 比较指令 ~10% | 低 |
| **5** | **O13** | 热/冷字段分离 | 缓存行利用率提升 | 高 |
| **5** | **O14** | Fix64 SIMD 加速 | Fix64 乘法 ~2x | 中 |

### 3.4 调整型优化 — 函数调用路径专项

> 详细方案见 [Step8_FunctionCall.md §七 性能优化展望](Step8_FunctionCall.md#七性能优化展望)

| 优先级 | ID | 内容 | 预期收益 | 复杂度 |
|--------|----|------|---------|--------|
| 🟡 中 | **FO1** | 叶函数优化（跳过 CallFrame push/pop） | 叶函数开销 -40~60% | 低 |
| 🟡 中 | **FO6** | 自适应寄存器窗口 | 嵌套从 ~3 扩展到 ~6 层 | 中 |
| 🔵 低 | **FO2** | 尾调用消除 | 尾调用不增长深度 | 中 |
| 🔵 低 | **FO3** | 小函数内联 | 小函数 -80% 指令 | 高 |

### 3.5 调整型优化 — 结构体路径

> 详细方案见 [Step9_StructFlatten.md §七 性能优化展望](Step9_StructFlatten.md#七性能优化展望)

| ID | 内容 | 预期收益 | 复杂度 |
|----|------|---------|--------|
| **SO1** | COPY_BLOCK OpCode（替代 N 条 MOVE 的结构体赋值） | 大 struct 赋值性能提升 | 中（需新 OpCode + VMWorld 实现） |

### 3.6 综合预估

**实施进度**（自然优化 + 调整型合并）：

```
[自然优化：随 F4 实施] ✅
  O4 → O5 → O7 → O3, FO4/FO5/FO7
        ↓ benchmark 验证 ✅
[调整型：Tier 1] ✅
  O1 → O2
        ↓ benchmark 验证（.NET JIT ~9%；IL2CPP 预期 30-50%）
[调整型：Tier 2] ⏳
  O6
        ↓ benchmark 验证（期望 2-3x）
[调整型：按需] ⏳
  O9 → O10 → 视需要 O8/O13
```

**预估目标**：自然优化（O4 减少 15-20% 指令数 ✅）+ 调整型 Tier 1（O1/O2 .NET JIT ~9% ✅）+ 调整型 Tier 2（O6 再减 5-10% 指令 ⏳）完成后，编译脚本预期降至 **2-3x**（vs C#）。

---

## 四、已识别风险

### 4.1 步骤 8 风险（函数调用）

> 详细分析见 [Step8_FunctionCall.md §五 风险分析](Step8_FunctionCall.md#五风险分析)

#### 已缓解 / 已验证

| ID | 风险 | 影响 | 缓解 | 状态 |
|----|------|------|------|------|
| **R1** | 寄存器窗口偏移导致 MaxRegisters=64 下嵌套层数不足 | 深度调用溢出 | 方案 A 下平均 3 层够用；FO6 可扩展 | ⚠️ 已知限制 |
| **R2** | 两遍编译引入 entry IP 错误 | 函数跳转目标错误 | 占位 + 回填保证正确；F-02 覆盖 | ✅ 已验证 |
| **R3** | RETURN 路径在非 entry 函数误触发 | 实例提前终止 | 编译器保证非 entry 只 emit RET_FUNC | ✅ 已保证 |
| **R4** | CleanupBase 在函数边界的交互 | Cleanup 范围错误 | F-07 测试覆盖 | ✅ 已验证 |

#### 前瞻风险

| ID | 风险 | 影响 | 建议应对 |
|----|------|------|---------|
| **R5** | 结构体作为函数参数时寄存器窗口空间不足 | struct 字段拍平加剧窗口压力 | S4 实施时需与 FO6 联合评估 |
| **R6** | 跨模块函数调用引入 ModuleSlot 切换 | CALL 只支持同模块跳转 | 后续扩展 CALL 操作数或新增 CALL_EXT |
| **R7** | 前向引用回填在大模块中的性能 | _pendingCalls 线性扫描 | ✅ 理想方案已实施：>50 自动切换 Dictionary |
| **R8** | Cleanup 块内调用函数时 CleanupBase 语义 | 返回后 CleanupDepth 不一致 | ✅ 理想方案已实施：编译器禁止 Cleanup 块内函数调用 |

### 4.2 步骤 9 风险（结构体拍平）

> 详细分析见 [Step9_StructFlatten.md §四 风险分析](Step9_StructFlatten.md#四风险分析)

| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| **SR1** | struct 变量加速耗尽 local 区（r16-r47 = 32 槽） | 寄存器不足 | 编译器超限报错 |
| **SR2** | struct 赋值生成 N 条 MOVE，大 struct 性能退化 | 大结构体慢 | 业务通常 2-5 字段；后续 SO1（COPY_BLOCK） |
| **SR3** | struct 变量与函数调用寄存器窗口交互 | 局部 struct 窗口偏移 | 不涉及 struct 参数（S4 展望）；局部变量自然正确 |
| **SR4** | 字段访问与方法调用语法歧义（`a.b(c)`） | 解析歧义 | 不支持方法调用；`a.b` 只能是字段访问 |

### 4.3 全局风险

| ID | 风险 | 影响 | 来源 |
|----|------|------|------|
| **GR1** | ~~Fix64 模式 (`USE_FIXPOINT`) 未经独立构建验证~~ → 已修复：CI 矩阵双模式自动验证 + Fix64 除法溢出修复 | ~~上线前必须通过~~ | ✅ 已完成 |
| **GR2** | ~~Cleanup 块内 `wait`/`wait_for` 未被编译器禁止~~ → 已修复：G6 编译器检查 | ~~阻塞实例回收~~ | ✅ 已修复 |
| **GR3** | 文档缺口 D1-D4 未合并入总结文档 | 设计上下文缺失 | [§11.3](../VM_Summary.md#113-文档缺口来自档案交叉审查) |

### 4.4 B-α1 风险（LSP6 Syscall 声明协议）

| ID | 风险 | 影响 | 来源 |
|----|------|------|------|
| **R-LSP6-1** | `.ffvm.d.json` 仅通过 API 加载，无文件系统自动发现 | 宿主需显式调用 `LoadDeclarationJson`；编辑器扩展需配置路径 | [B-α1](Step_B_Alpha1_LSP6_SyscallDecl.md) |
| **R-LSP6-2** | 声明文件 slot 与运行时 SyscallTable slot 的一致性由宿主保证 | slot 不匹配时补全签名与实际行为不符 | [B-α1](Step_B_Alpha1_LSP6_SyscallDecl.md) |

---

## 五、索引速查

### 按紧急程度

| 类别 | 条目 |
|------|------|
| **步骤 10 前必须** | C4, F4, G5, G6, V5 |
| **步骤 10 前如需** | S4 |
| **业务驱动** | FF1-FF5, H1, BB1, PR1, FIX1, DM1 |
| **自然优化（随功能实现）** | O3, O4, O5, O7, FO4, FO5, FO7 |
| **调整型优化（Benchmark 驱动）** | O1, O2, O6, O8, O9, O10, O11, O12, O13, O14, FO1, FO2, FO3, FO6, SO1 |
| **脚本调试** | DBG1-DBG7（真实宿主断点 + DAP） |
| **语言服务** | LSP1-LSP7（语法高亮、诊断、符号、补全、Syscall 声明、参数提示） |
| **无需消除（设计决策）** | 不支持闭包/高阶函数, 不支持结构体方法 |

### 按实施复杂度

| 复杂度 | 条目 |
|--------|------|
| 低 | C4, G6, O1, O2, O3, O5, O7, O9, FO4, FO5, FF3, SN2, DBG3, DBG5, DBG6, LSP2 |
| 中 | F4, S4, O4, O6, O10, O11, O14, FO1, FO6, FO7, FO2, FF1, FF2, FF4, FF5, SO1, SN1, H1, DBG1, DBG2, DBG4, DBG7, LSP1, LSP3, LSP4, LSP5, LSP6, LSP7 |
| 高 | O8, O13, FO3 |

### 按风险等级（§六 降级后）

| 等级 | 条目 | 数量 |
|------|------|------|
| **极低** | R2, R3, R4, R7, SR4, GR1, GR2, DBG3, DBG5, DBG6, LSP2, DR1, DR3, DR4 | 14 |
| **低** | R1, R5, R6, R8, SR1, SR2, SR3, GR3, DBG1, DBG2, DBG4, DBG7, LSP1, LSP3, LSP4, LSP5, LSP6, LSP7, DR2, DR5 | 20 |
| **中 / 高** | — | 0 |

### 按优化类别

| 类别 | 条目 | 数量 | 说明 |
|------|------|------|------|
| 自然优化 | O3, O4, O5, O7, FO4, FO5, FO7 | 7 | 随 F4 / 编译器成熟化顺带完成 |
| 调整型优化 | O1, O2, O6, O8, O9-O14, FO1-FO3, FO6, SO1 | 15 | Benchmark 驱动，专项投入 |

---

## 六、风险降级计划（目标：全部 → 低 / 极低）

> **迭代 1**：2026-04-02。针对所有中/高风险项，制定具体降级措施。
> 特别聚焦：外部工具对接（DAP/LSP）、宿主断点包装为脚本调试、Release 模式隔离。

### 6.1 Release 模式隔离策略（`FFVM_SCRIPT_DEBUG` 条件编译）

**原则**：所有调试 / 外部工具对接代码在 Release 构建中**完全不存在**（零运行时开销、零代码残留）。

**实现**：引入编译符号 `FFVM_SCRIPT_DEBUG`，与已有 `USE_FIXPOINT` 模式一致。

```
编译配置矩阵：
┌──────────────┬──────────────────┬─────────────────┐
│ 构建模式     │ FFVM_SCRIPT_DEBUG │ USE_FIXPOINT    │
├──────────────┼──────────────────┼─────────────────┤
│ Dev/Editor   │ ✅ 定义          │ ❌ 不定义       │
│ Release      │ ❌ 不定义        │ ✅ 定义         │
│ QA-Debug     │ ✅ 定义          │ ✅ 定义         │ ← 仅内部 QA，不部署
└──────────────┴──────────────────┴─────────────────┘
```

**隔离边界**：

| 层级 | 被隔离的内容 | 隔离方式 |
|------|-------------|---------|
| `VMProgram` | `SourceMap` / `SymbolTable` 字段 | `#if FFVM_SCRIPT_DEBUG` 包裹字段 + 构造函数重载 |
| `BytecodeCompiler` | Source Map emit 逻辑、符号表收集逻辑 | `#if` 包裹对应代码块 |
| `VMWorld.ExecuteInstance` | 断点检查热路径注入点 | `#if` 包裹整个断点检查分支 |
| DAP 适配器 | 整个 `FFVM.Debug` namespace | 独立程序集（asmdef），`#if` + Assembly Definition Reference 双重隔离 |
| LSP Server | 整个 `FFVM.LanguageService` namespace | 独立程序集，不被 Release 构建引用 |

**验证门控**：
- ✅ Release 构建中 `grep -r "FFVM_SCRIPT_DEBUG"` 确认无符号泄漏
- ✅ Release 构建体积 diff < 1KB（与隔离前对比）
- ✅ Release `ExecuteInstance` 反编译确认无断点检查分支

> **风险等级**：**极低**。模式与已验证的 `USE_FIXPOINT` 完全一致，无新模式引入。

### 6.2 DBG 系列风险逐项降级

#### DBG1（Source Map） — 中 → **低**

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| 实现复杂度 | 中 | **窄化**：仅记录 `IP → lineNumber`（省略 column），用 `int[]` 平行数组，索引 = IP |
| 数据正确性 | 编译器优化 pass 可能破坏映射 | **约束**：Source Map 在所有优化 pass 之后、`_instructions` 冻结时最终生成 |
| 验证 | 无 | **门控测试**：`DBG1_T01` 编译已知脚本 → 断言每个 LOAD_CONST/SYSCALL IP 的 lineNumber 正确 |

#### DBG2（Symbol Table） — 中 → **低**

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| 与 F4 耦合 | 符号表需要寄存器生命周期 | **解耦**：Phase 1 只记录 `varName → register`（不含生命周期范围）；生命周期信息在 F4 完成后补充 |
| struct 字段映射 | 需追踪拍平后的寄存器布局 | **复用**：编译器 `_structVarTypes` + `_structTypes` 已有数据，只需序列化 |
| 验证 | 无 | **门控测试**：`DBG2_T01` 编译含 var + struct 的脚本 → 断言符号表各项正确 |

#### DBG3（宿主断点桥接） — 低 → **极低**

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| 实现 | ~5 行代码 | 不变，极简 |
| 热路径性能 | ExecuteInstance 每条指令多一次检查 | **#if 隔离**：Release 构建零开销；Dev 构建中用 `HashSet<int>` 行号命中检查，O(1) |
| 验证 | 无 | **门控测试**：`DBG3_T01` 设断点行 → 验证 `Debugger.Break()` 被调用（mock 方式） |

**DBG3 伪代码（确认极简性）**：

```csharp
// VMWorld.ExecuteInstance 热路径中，#if FFVM_SCRIPT_DEBUG 包裹
#if FFVM_SCRIPT_DEBUG
if (_breakpointLines != null && program.SourceMap != null)
{
    int line = program.SourceMap[inst.IP];
    if (line > 0 && _breakpointLines.Contains(line))
        System.Diagnostics.Debugger.Break();
}
#endif
```

> 完全确认：5 行代码，无新依赖，Release 零残留。

#### DBG4（单步映射） — 中 → **低**

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| Step Over 复杂度 | 需找"下一行"的首 IP | **简化**：Source Map 为 `int[]`，线性扫描 `IP+1` 起找到 `line != currentLine` 的首个 IP，设为临时断点 |
| Step Into | 需感知 CALL 指令 | **简化**：检查当前 IP 是否为 CALL OpCode → 是则在 CALL 目标 IP 设临时断点 |
| Step Out | 需感知 RET_FUNC | **简化**：在 `CallStack.ReturnIP` 设临时断点 |
| 验证 | 无 | **门控测试**：`DBG4_T01` Step Over 验证、`DBG4_T02` Step Into 验证、`DBG4_T03` Step Out 验证 |

> 三种单步都归约为"设临时断点 + 继续执行"，实现复用 DBG3 的断点检查路径。

#### DBG5（变量查看适配器） — 低 → **极低**

不变，已经是低复杂度。纯读取 Symbol Table + 寄存器值。

#### DBG6（调用栈查看） — 低 → **极低**

不变，已经是低复杂度。`CallStack` 已有完整帧信息。

#### DBG7（DAP 适配器） — 中 → **低**（关键降级项）

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| 协议复杂度 | DAP 全协议约 50+ 消息 | **窄化 MVP**：仅实现 12 个必需消息（见下表） |
| JSON-RPC 通信层 | 需实现 Content-Length 分帧 | **复用**：C# 标准 StreamReader + 手写 header 解析（~50 行），或直接使用 `Newtonsoft.Json`（Unity 已内置） |
| 线程模型 | DAP I/O 线程 vs Unity 主线程 | **简化方案**：Unity Editor 模式下用 `EditorApplication.update` 轮询而非多线程；StandaloneRunner 用 stdin/stdout 同步 I/O |
| VS Code 扩展 | 需要编写扩展 | **极简**：`package.json` + `launch.json` 配置（~60 行 JSON），无 TypeScript 逻辑 |
| 验证 | 无 | **分阶段验证**：见 §6.3 |

**DAP MVP 消息集（12 个）**：

| 消息 | 方向 | 复杂度 | 说明 |
|------|------|--------|------|
| `initialize` | Request | 极低 | 返回 capabilities 静态对象 |
| `launch` | Request | 低 | 加载脚本模块 + spawn 实例 |
| `setBreakpoints` | Request | 低 | Source Map 查表 → `_breakpointLines` |
| `configurationDone` | Request | 极低 | 空响应 |
| `threads` | Request | 极低 | 返回单线程（VM 主线程） |
| `continue` | Request | 极低 | 清除单步标记 + 恢复 Tick |
| `next` | Request | 低 | Step Over → 设临时断点 + continue |
| `stepIn` | Request | 低 | Step Into → 设临时断点 + continue |
| `stepOut` | Request | 低 | Step Out → 设临时断点 + continue |
| `stackTrace` | Request | 低 | DBG6 调用栈查看 |
| `scopes` / `variables` | Request | 低 | DBG5 变量查看 |
| `disconnect` | Request | 极低 | 清理 |

> **总代码量预估**：~600-800 行 C#，分解如下：
> - ~150 行：Content-Length 分帧 + JSON-RPC 解析（与 LSP1 共享）
> - ~200 行：12 个 request handler（每个 ~15 行平均）
> - ~150 行：DAP 消息类型定义（Request/Response/Event 结构体）
> - ~100-300 行：JSON 序列化辅助 + 错误处理 + 生命周期管理
>
> 独立程序集，`#if FFVM_SCRIPT_DEBUG`。

### 6.3 DAP 对接分阶段验证门控（降级关键）

**核心策略**：不一次性实现全部 DAP，而是逐层叠加，每层有独立验证门控。

```
Gate 0: 纯命令行调试（零外部依赖）              ← DBG3+DBG5+DBG6
  验证：StandaloneRunner 中手动设断点行号 →
        命中时 Console 输出调用栈 + 变量值
  通过标准：3 个门控测试通过
  风险等级：极低（零外部依赖）
      │
      ↓
Gate 1: DAP 最小协议（stdin/stdout JSON-RPC）    ← DBG7 Phase A
  验证：StandaloneRunner 作为 DAP server →
        VS Code launch.json 连接 →
        setBreakpoints → continue → 命中 → stackTrace + variables 正确
  通过标准：手动验证 VS Code 断点命中 + 变量显示
  风险等级：低（标准协议，有参考实现可对照）
      │
      ↓
Gate 2: DAP 单步 + 完整 VS Code 体验             ← DBG7 Phase B + DBG4
  验证：next / stepIn / stepOut → 源码行正确跳转
  通过标准：手动验证三种单步行为
  风险等级：低（复用断点机制）
      │
      ↓
Gate 3: Unity Editor 内嵌 DAP（可选）             ← DBG7 Phase C
  验证：Unity Editor Play Mode 中 VS Code 附加调试
  通过标准：Editor 模式下断点命中 + 变量查看
  风险等级：中 → 低（EditorApplication.update 轮询模式规避线程问题）
```

> **关键洞察**：Gate 0 完全不依赖外部工具，用纯测试验证 Source Map + 断点 + 变量查看的正确性。
> 即使 DAP 对接遇到问题，Gate 0 的能力已经可用（命令行调试）。

### 6.4 LSP 系列风险逐项降级

#### LSP1（LSP Server 核心框架） — 中 → **低**

| 维度 | 原始风险 | 降级措施 |
|------|---------|---------|
| 协议复杂度 | LSP 全协议 100+ 消息 | **窄化**：仅实现 `initialize` + `textDocument/didOpen` + `textDocument/didChange` + `textDocument/publishDiagnostics` |
| 通信层 | 与 DAP 相同的 JSON-RPC | **复用 DAP 通信层**：共享 `ContentLengthStream` + JSON 序列化 |
| 验证 | 无 | **门控**：VS Code 扩展安装后，脚本文件打开 → 实时报错红线出现 |

#### LSP2（语法高亮） — 低 → **极低**

零风险。纯 JSON 文件（TextMate grammar），约 80-100 行，16 个关键字 + 运算符。可立即实施。

#### LSP3-LSP7 — 中 → **低**

| 措施 | 说明 |
|------|------|
| **增量编译不做** | 初版直接全量重编译（当前编译器 ~800 行，编译速度对小脚本文件足够） |
| **复用编译器错误列表** | `BytecodeCompiler._errors` 已有行号信息（待 DBG1 Source Map 增强） |
| **延迟实施** | LSP4/LSP5 排在 LSP3 之后，逐步叠加 |
| **LSP6 声明协议复用 SyscallTable** | 仅扩展现有 `SyscallTable` 加签名元数据 + JSON 声明文件加载，无新通信层 |
| **LSP7 复用 AST + LSP6** | 用户函数签名从已缓存的 AST 获取，Syscall 签名从 LSP6 声明获取 |

### 6.5 已有风险项降级汇总

#### 步骤 8 风险

| ID | 原等级 | 降级后 | 措施 |
|----|--------|--------|------|
| R1 | ⚠️ 已知限制 | **低** | FO6 自适应窗口已在调整型优化中，且 3 层嵌套覆盖绝大多数业务场景 |
| R2 | ✅ 已验证 | **极低** | 不变 |
| R3 | ✅ 已保证 | **极低** | 不变 |
| R4 | ✅ 已验证 | **极低** | 不变 |
| R5 | 前瞻 | **低** | S4 实施时 struct 参数仅允许 ≤4 字段的结构体直接传递，>4 时编译报错 |
| R6 | 前瞻 | **低** | 跨模块调用推迟至业务需要时；当前单模块完全满足 |
| R7 | 前瞻 | **极低** | 百函数级模块下 `_pendingCalls` 线性扫描 < 1ms，无实际影响 |
| R8 | 前瞻 | **低** | G6 已禁止 Cleanup 块内 wait；进一步扩展为禁止 Cleanup 块内函数调用（增加一行编译器检查） |

#### 步骤 9 风险

| ID | 原等级 | 降级后 | 措施 |
|----|--------|--------|------|
| SR1 | ⚠️ | **低** | 编译器已有超限报错；实际业务 struct ≤5 字段为主 |
| SR2 | ⚠️ | **低** | 业务 struct 通常 2-5 字段；SO1 COPY_BLOCK 作为后备 |
| SR3 | ⚠️ | **低** | S4 实施时联合验证；当前局部 struct 不涉及窗口偏移 |
| SR4 | ⚠️ | **极低** | 语法层面已禁止方法调用；Parser 不支持 `a.b(c)` 形式 |

#### 全局风险

| ID | 原等级 | 降级后 | 措施 |
|----|--------|--------|------|
| GR1 | ⚠️ | **极低** | ✅ CI 矩阵双模式自动验证 + Fix64 除法溢出修复；412 项 Assert 双模式通过 |
| GR2 | ✅ 已修复 | **极低** | 不变 |
| GR3 | ⚠️ | **低** | D1-D4 文档缺口列入步骤 10 前批量补全，不阻塞功能 |

### 6.6 新增风险识别（外部工具对接）

| ID | 风险 | 原等级 | 降级措施 | 降级后 |
|----|------|--------|---------|--------|
| **DR1** | DAP JSON 序列化/反序列化引入 GC 压力 | 中 | Editor 模式专用，不影响 Release；`#if` 隔离 | **极低** |
| **DR2** | VS Code 扩展维护成本 | 中 | 极简扩展（纯 JSON 配置，无 TypeScript 逻辑）；无运行时代码 | **低** |
| **DR3** | DAP 协议版本演进导致不兼容 | 低 | MVP 仅用 DAP 1.0 核心消息，极稳定子集 | **极低** |
| **DR4** | `Debugger.Break()` 在非 IDE 环境中无效 | 低 | Gate 0 提供纯 Console 回退路径 | **极低** |
| **DR5** | Unity Editor 主线程阻塞导致 UI 冻结 | 中 | 改用轮询模式：`_isPaused` 标志位 + `EditorApplication.update` 检查，主线程不阻塞 | **低** |

### 6.7 全风险矩阵（降级后）

| 等级 | 条目 | 数量 |
|------|------|------|
| **极低** | R2, R3, R4, R7, SR4, GR1, GR2, DBG3, DBG5, DBG6, LSP2, DR1, DR3, DR4 | 14 |
| **低** | R1, R5, R6, R8, SR1, SR2, SR3, GR3, DBG1, DBG2, DBG4, DBG7, LSP1, LSP3, LSP4, LSP5, LSP6, LSP7, DR2, DR5 | 20 |
| **中** | — | 0 |
| **高** | — | 0 |

> ✅ **目标达成**：全部 34 项风险降至低或极低。

---

## 七、文档缺口（来自档案交叉审查）

> 详见 [VM_Summary.md §11.3](../VM_Summary.md#113-文档缺口来自档案交叉审查)

| # | 来源 | 内容 | 建议动作 |
|---|------|------|---------|
| D1 | VMScript.md | "条件→目标→数据效果→视觉效果"技能流水线模式 | 补入 §1.2 或 §2 |
| D2 | VMScript2.md | 历史失败教训表 → 设计约束推导 | 补入 §9 选型理由 |
| D3 | VMScript4.md | 项目级成功标准（5 类验收维度） | 补入 §7 或新增验收节 |
| D4 | VMScript4.md | 设计验证递进轴线 | 已隐含于 §7，可显式化 |

---

## 八、扩展串行计划（理想方案）

> **原则**：每个风险点选择**最终 / 理想方案**而非保守妥协。
> 扩展后的串行计划在 [VM_Summary.md §七](../VM_Summary.md#七推进顺序串行计划) 的基础上，
> 将所有风险应对措施、调试子项、语言服务子项展开为具体的执行步骤。
>
> **调试决策详情**：见专用文档 → [Step_Debug_Decisions.md](Step_Debug_Decisions.md)
>
> **推进指令**：项目使用 `.github/prompts/` 下的提示模板引导串行推进：
> - `#check-and-next` — 检查当前步骤完成条件，满足则推进到下一步并继续执行
> - `#check` — 仅检查当前步骤状态，不执行推进
> - `#requirement` — 评估新需求并安排执行时机

### 8.1 总览时间线

> 已完成步骤的详细时间线见 VM_Summary.md §七-A。以下仅展开待执行部分。
> **当前位置以 `当前位置 →` 标记**，见 VM_Summary.md §七-B。

#### 脚本引擎侧（B 区间 — 4 个 Phase，17 个步骤）

| Phase | 定位 | 步骤 | 内容摘要 |
|-------|------|------|----------|
| **α 语言服务收尾** | 开发体验质变 | B-α1, B-α2 | LSP6 Syscall 声明协议 → LSP7 参数提示 |
| **β 优化 Tier 2** | 性能逼近 2x | B-β1, B-β2, B-β3 | O6 peephole → FO1 叶函数 → O9 活跃链表 |
| **γ 功能完整性** | 语言能力补全 | B-γ1 ~ B-γ6 | FO6 自适应窗口 → FF5 非 entry defer → S4 struct 参数 → C6 嵌套 using → SN1 嵌套 struct → GR3 文档 |
| **δ 按需补全** | 业务驱动激活 | B-δ1 ~ B-δ6 | O10 活跃快照 → SO1 COPY_BLOCK → FF3 可选参数 → SN2 struct 字面量 → C5 Cleanup 超时 → B1 Editor DAP |

> 完整的步骤序号、完成条件、依赖关系见 [VM_Summary.md §七-B](../VM_Summary.md#b-待执行阶段脚本引擎侧--细化推进序列)。

#### 宿主集成侧（C 区间 — 生产必经路径）

| 序号 | 步骤 | 状态 | 子项 | 前置 |
|------|------|------|------|------|
| C1 | 真实 Syscall 接入 ECS | ⚪ | stub → 真实宿主实现（碰撞/伤害/击退/特效/黑板等） | 宿主 ECS 就绪 |
| C2 | V5 帧内 Profiler | ⚪ | 含真实 ECS 交互的 Tick 耗时 + GC.Alloc = 0 | C1 |
| C3 | 技能资源管线 🆕 | ⚪ | .ffs 加载/编译/缓存/热更新 | C1 |
| C4 | Handle64 批处理 | ⏳ | H1 句柄化多目标流转 | C1 |
| C5 | 帧同步集成验证 🆕 | ⚪ | 真实网络快照/回滚正确性 | C1+C2 |
| C6 | 编辑器流程图投影 | ⏳ | 步骤 10 AST→流程图 | C2 |

#### 剩余展望项（暂无排期，业务驱动激活）

| ID | 内容 | 触发条件 |
|----|------|----------|
| FF1 | 跨模块函数调用 | 业务需要模块间函数共享 |
| FF2 | 函数作为 Syscall 参数（回调） | 需要宿主驱动的回调场景 |
| FF4 | 多返回值 | 业务需要函数返回多值（如 GetPosition→x,y） |
| O8 | 指令压缩 16B→4B | L1 缓存瓶颈出现 |
| O11-O14 | 运行时高级优化 | Benchmark 驱动 |
| FO2 | 尾调用消除 | 递归深度成为瓶颈 |
| FO3 | 小函数内联 | Benchmark 驱动 |
| BB1 | 黑板 Key 编译期自动分配 ID | 编译器成熟后 |
| PR1 | Paired Syscall 支持带参反向调用 | 需要带参释放场景 |
| DM1 | VM 编排表现脚本（双轨模式） | 需要复杂镜头/特效序列 |

### 8.2 风险理想方案速查

| 风险 ID | 理想方案 | 插入位置 | 决策依据 |
|---------|---------|---------|---------|
| **R1** | FO6 自适应寄存器窗口（嵌套 ~3→~6 层） | B-γ1 | 编译器已有函数表，分析实际使用寄存器数自然扩展 |
| **R5** | ≤4 字段直传 + 编译报错 → FO6 后解除 | B-γ3 (S4) | 与 FO6 联合评估，先设安全限制 |
| **R7** | _pendingCalls >50 自动切 Dictionary | F4 阶段 | ✅ 已完成 |
| **R8** | 编译器禁止 Cleanup 块内函数调用 | F4 阶段 | ✅ 已完成 |
| **SR1** | 编译器超限报错 + FO6 扩大 local 区 | B-γ1 (FO6) | 明确错误信息 + 根本解决方案 |
| **SR2** | SO1 COPY_BLOCK OpCode | B-δ2 | `Buffer.MemoryCopy` 批量拷贝替代 N×MOVE |
| **GR1** | ✅ CI 构建矩阵 USE_FIXPOINT 自动验证 + Fix64 除法溢出修复 | ✅ 已完成 | CI 矩阵双模式，624 项 Assert 均通过 |
| **GR3** | D1-D4 文档缺口批量补全 | B-γ6 | 不阻塞功能，但必须补全 |
| **DR5** | EditorApplication.update 轮询模式 | B-δ6 | 主线程永不阻塞 |

### 8.3 门控测试清单

| Gate | 测试 ID | 内容 | 前置 |
|------|---------|------|------|
| — | DBG1_T01 | 编译已知脚本 → 断言 IP→行号映射正确 | F4 |
| — | DBG2_T01 | 编译含 var+struct → 断言符号表正确 | F4 |
| **0** | DBG3_T01 | 设断点行 → Debugger.Break() 被调用 | DBG1 |
| **0** | DBG5_T01 | 断点命中时变量值显示正确 | DBG2+DBG3 |
| **0** | DBG6_T01 | 断点命中时调用栈显示正确 | DBG3 |
| **1** | Gate1_Manual | VS Code 连接 → 断点命中 → stackTrace + variables | DBG7-A |
| **2** | DBG4_T01 | Step Over → 下一行 IP 正确 | DBG4 |
| **2** | DBG4_T02 | Step Into → CALL 目标 IP 正确 | DBG4 |
| **2** | DBG4_T03 | Step Out → ReturnIP 正确 | DBG4 |
| **2** | Gate2_Manual | VS Code 三种单步行为正确 | DBG7-B |
| **3** | Gate3_Manual | Editor 模式下断点 + 变量查看 | DBG7-C |
