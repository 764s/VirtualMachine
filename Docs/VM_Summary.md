# FFEditor 胶水执行器与技能虚拟机：阶段性总结

> 本文是项目的单一入口文档。
> 详细背景与理想目标见 [Reference/VM_Background.md](Reference/VM_Background.md)。
> 核心架构决策详见 [Reference/VM_Core_Decisions.md](Reference/VM_Core_Decisions.md)。
> 实现状态、性能记录与缺口见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)。

### 实现流程

严格按照如下顺序执行:
1. (临时讨论)
2. 固化讨论文件
   - 讨论完成前, 文件末尾有专用讨论区用于交流
   - 之后的讨论都通过修改讨论文件达成
3. 向串行需求列表提交需求
4. 为需求规划 checklist 子计划文件
5. 推进子计划至完成

> **推进指令**：使用 `.github/prompts/` 提示模板引导串行推进：
> `#check-and-next`（检查+推进）、`#check`（仅检查）、`#requirement`（评估新需求）。
> AI 通过 `当前位置 →` 标记定位下一步。

---

### 文档目录结构

> 文档区已从 `Assets/ScriptVM/` 迁移至仓库根目录 `Docs/`，以避免 Unity 为 `.md` 文件自动生成 `.meta` 文件。

```
Docs/
  VM_Summary.md              ← 本文（唯一入口文档）
  Discussion/                ← 讨论区：设计探讨 / 方案对比 / 决策记录
  Plan/                      ← 计划区：确定步骤 / 无脑执行
  Emergency/                 ← 紧急区：恶性 / 影响深远缺陷的修复通道
  BugFix/                    ← Bug 修复区：已知 Bug 的修复跟踪
  Practice/                  ← 实践区：探索性实践记录
  Reference/                 ← 参考区：技术规格 / 架构约束 / 示例脚本
```

- **Discussion/**：讨论区。设计探讨、方案对比、决策记录。状态：💬 讨论中 / ✅ 已完成讨论（可再次激活）。详见 [Discussion/README.md](Discussion/README.md)。
- **Plan/**：计划区。确定步骤与检查清单，无脑执行。状态：⏳ 等待中 / 🔄 进行中 / ✅ 已完成。
- **Emergency/**：紧急区。恶性/影响深远缺陷的独立修复通道。详见 [Emergency/README.md](Emergency/README.md)。
- **BugFix/**：Bug 修复区。已知 Bug 的修复跟踪。与讨论区平行。详见 [BugFix/README.md](BugFix/README.md)。
- **Practice/**：实践区。串行计划之外的探索性实践。
- **Reference/**：参考区。技术规格、架构约束、示例脚本、背景文档、实现记录等长期引用文档。

---

## 设计原则与哲学

### 一句话目标

> 把技能、子弹、状态效果三套旧系统中已经客观存在但分散隐藏的流程语义，统一抬升到脚本层，并用"脚本文本为真理源 + 结构化流程图为主 UI + 寄存器式 VM 为底层 + `wait` 为一等语义 + 零分配快照回滚"为核心约束，构建一套统一执行架构。

### 核心决策（速览）

| 决策 | 要点 |
|------|------|
| ROM/RAM 分离 | 字节码只读共享，实例状态纯值类型 ECS 组件，快照退化为 memcpy |
| `wait` 一等语义 | 非语法糖，VM 写 WaitFrames + 更新 IP + 交出执行权，值类型天然可快照 |
| `using` + `defer` Cleanup | 配对 Syscall 自动 cleanup（理想）+ defer 逃生舱 + 编译器强制检查 |
| 全程 Fix64 | 确定性数值，表现走 Syscall。开发期 float 快速迭代 |
| 寄存器定长 + 句柄化 | 64 slot 固定，复杂数据 Handle64，Syscall 边界，零 GC |
| 手写递归下降 Parser | DSL 规模小（~21 关键字），零外部依赖，精确控制 |
| 黑板 Key 编译期 ID | 零碰撞，零运行时开销 |
| 脚本为真理源 | 结构化流程图为主 UI，AST 受控投影 |

> 详细理由与实现见 [Reference/VM_Core_Decisions.md](Reference/VM_Core_Decisions.md)

### 架构硬约束速查

> 完整 20 条硬纪律见 [Reference/VM_Architecture_Rules.md](Reference/VM_Architecture_Rules.md)

1. 脚本文本 = 唯一真理源
2. UI = await 驱动的视图投影
3. VM = 偏瞬态寄存器式执行模型
4. RAM = ECS 纯值类型组件
5. ROM / RAM 物理分离
6. 寄存器禁持动态/托管/不定长结构
6b. 全程 Fix64，表现走 Syscall
7. 脚本 = 单体逻辑（AoS）
8. 批处理 = 宿主 Syscall
9. 复杂临时数据 = 句柄化
10. 宿主交互 = 统一 Syscall Table
11. `wait` = 一等语义
12. 快照/回滚 > 表达便利性
13. Cleanup = `using` + `defer` + 编译器强制检查
14. 语法服从 VM 物理法则
15. AST 禁悬空高级结构
16. 新提案必须过物理约束校验
17. 每次协作重新注入本文件
18. 文档尽快落为可运行锚点
19. 以可执行验证为准
20. 黑板 Key = 编译期静态 ID

**最终裁决**：任何新提案先问——能否放进定长寄存器和 ECS 纯值 RAM？是否支持零 GC / 快照 / 回滚 / 强制 Cleanup？是否保持 Syscall 边界与句柄化纪律？否定即拒绝。

---

## 语言特性

### 当前语言能力

| 分类 | 能力 |
|------|------|
| 流程控制 | `if`/`else`、`while`、`for`、`wait N`、`wait_for(id)` |
| 函数 | `func`、`entry`、多参数、可选参数默认值、返回值、递归、CALL/RET + CALL_LEAF/RET_LEAF |
| 结构体 | `struct` 编译期拍平、嵌套 struct、字面量构造 `TypeName { field: expr }` |
| 枚举 | `enum Name { A, B = expr, C }` 语法糖 → 编译期命名整数常量 |
| 变量 | `var`/`const`、模块级变量、扩展寄存器溢出 |
| 模块 | `include "path"`、`include "path" as Alias`、`private`/`public`/`override` 可见性 |
| 跨实例 | `@export` 导出、XCALL/XLOAD_MVAR/XSTORE_MVAR、`svc.member` 统一语法 |
| Cleanup | `using SomeCall(args) { body }`、`defer { body }`、超时保护 |
| 运算 | 算术、比较、逻辑、位运算（`& \| ^ ~ << >>`） |
| 字符串 | 常量字符串字面量（ROM，不支持拼接） |
| 优化 | 常量折叠、Peephole、LICM、CMP-immediate、SWITCH 跳转表、内联（模块内+跨模块+深度链式）、FORLOOP 超级指令、指令压缩 4B |
| 调试 | 源码映射、符号表、断点/单步/变量查看、DAP 协议、VS Code 扩展 |
| 语言服务 | LSP 诊断、补全、hover、definition、references、rename、signatureHelp、语义染色、依赖图、全项目诊断 |
| 分发 | 独立 .NET 类库（netstandard2.1 + net8.0）、CLI 工具、单文件发布 |

### 实现状态概要

> 详细实现状态表见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)

- **全链路完成**：源码 → Lexer → Parser → AST → BytecodeCompiler → VMProgram → VMWorld.Tick → 执行
- **测试**：2140 项 Assert 全部通过（114 TW + 1302+ Compiler + 44 Perf + 18 FFS + 51 Debug + 97 DAP + 514 LSP）
- **性能**：编译脚本 5-7x vs C#，与 Lua 5.4 同量级，已实施 19 项优化

---

## 串行需求列表

> **阅读指南**：
> - 本节为项目唯一的串行执行计划，所有任务严格串行推进。
> - ✅ = 已完成，⏳ = 待执行，⚪ = 被外部前置条件阻塞。
> - 展望项（暂无排期）的完整索引见 [Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)。

---

### A. 已完成阶段（Steps 1–9 + 调试 + CI）

下表按实际执行顺序列出所有已完成步骤。

| # | 步骤 | 关键产出 | 测试数 | 详情 |
|---|------|---------|--------|------|
| 1 | VMInstanceState Cleanup 字段 | blittable struct + CleanupStack | — | — |
| 2 | TreeWalker defer + Kill 验证 | Phase A 通过 | — | — |
| 3 | 最小 7 指令字节码解释循环 | VMWorld.Tick | — | — |
| 4 | 字节码 Phase A+B 全部验证 | **曳光弹成立** | — | [TracerBullet_Checklist](Plan/TracerBullet_Checklist.md) |
| — | V1 GC 精确验证 | 100 Tick 0 bytes alloc | — | [D_TracerBullet §6](Discussion/D_TracerBullet.md#6-验证门禁与通过条件) |
| — | V2 回滚正确性验证 | Syscall 序列 bit-exact | — | [D_TracerBullet §6](Discussion/D_TracerBullet.md#6-验证门禁与通过条件) |
| 5 | MOVE/JUMP/比较/布尔 | 19 条 Phase 2 指令 | — | — |
| — | V3 单实例性能基准 | 3.8x vs C# | — | [D_TracerBullet §6](Discussion/D_TracerBullet.md#6-验证门禁与通过条件) |
| — | V4 N 实例吞吐上限 | 128 实例 = 0.391ms | — | [D_TracerBullet §6](Discussion/D_TracerBullet.md#6-验证门禁与通过条件) |
| 6 | Lexer + Parser + BytecodeCompiler | 端到端文本→字节码→执行 | C01-C22 | — |
| — | 自动化性能基准 B01-B05 | 编译脚本 5-7x ratio | — | [D_TracerBullet §6](Discussion/D_TracerBullet.md#6-验证门禁与通过条件) |
| 7 | using + Paired Syscall (C1-C3, G1-G2) | 理想 Cleanup 模式 | — | [Step7](Plan/Step7_Using_PairedSyscall_Checklist.md) |
| 8 | 函数调用 + 调用栈 (F1-F3) | CALL/RET_FUNC + GC/回滚验证 | 279 | [Step8](Plan/Step8_FunctionCall.md) |
| 9 | 结构体编译期拍平 (S1-S3) | struct → 连续寄存器 | 303 | [Step9](Plan/Step9_StructFlatten.md) |
| — | Step 10 前置 (C4+G6) | requires_cleanup + Cleanup 块禁 wait | 315 | [Step10_Pre](Plan/Step10_Pre_CompilerSemanticChecks.md) |
| — | F4 + 自然优化 + 调试 Phase 1 | 寄存器生命周期 + O4/O5/O7/O3 + DBG1/DBG2 | 361 | [Step_F4](Plan/Step_F4_RegisterLifecycle.md) |
| — | 调试 Phase 2 (Gate 0) | ScriptDebugger 命令行调试 | 412 | [Phase2](Plan/Step_Debug_Phase2.md) |
| — | GR1 CI 构建矩阵 | float + Fix64 双模式自动验证 | 412×2 | [GR1](Plan/Step_GR1_CI_BuildMatrix.md) |
| — | 调试 Phase 3A (Gate 1) | DAP Server 核心 + VS Code 扩展 | 470 | [Phase3A](Plan/Step_Debug_Phase3A_DAP.md) |
| — | 调试 Phase 3B (Gate 2) | DAP 单步 next/stepIn/stepOut | 505 | [Phase3B](Plan/Step_Debug_Phase3B_DAP_SingleStep.md) |
| — | LSP Phase 4 | LSP Server 核心 + 实时诊断 | 546 | [Phase4](Plan/Step_LSP_Phase4.md) |
| — | LSP4 符号分析 | documentSymbol + hover + definition + references | 586 | [LSP4](Plan/Step_LSP4_Symbols.md) |
| — | LSP5 代码补全 | textDocument/completion | 624 | [LSP5](Plan/Step_LSP5_Completion.md) |
| — | B3 Tier 1 (O1+O2) | OpCode 连续编号 + unsafe fixed | 624 | [B3-T1](Plan/Step_B3_Optimization_Tier1.md) |
| — | B-R1 FFScript 命名 | `.vm` → `.ffs` 统一 | 624 | [B-R1](Plan/Step_R1_FFScript_Rename.md) |
| — | B-α1 LSP6 Syscall 声明 | .ffvm.d.json + 补全增强 | 644 | [B-α1](Plan/Step_B_Alpha1_LSP6_SyscallDecl.md) |
| — | B-α2 LSP7 参数提示 | signatureHelp | 676 | [B-α2](Plan/Step_B_Alpha2_LSP7_SignatureHelp.md) |
| — | B-β1 O6 Peephole | 自赋值消除 + NOP 压缩 | 676 | [B-β1](Plan/Step_B_Beta1_O6_Peephole.md) |
| — | B-β2 FO1 叶函数 | CALL_LEAF/RET_LEAF | 700 | [B-β2](Plan/Step_B_Beta2_FO1_LeafFunction.md) |
| — | B-β3 O9 活跃链表 | ActiveList + swap-remove O(1) | 711 | [B-β3](Plan/Step_B_Beta3_O9_ActiveList.md) |
| — | B-γ1 FO6 自适应窗口 | 嵌套 ~3→~6 | 721 | [B-γ1](Plan/Step_B_Gamma1_FO6_AdaptiveWindow.md) |
| — | B-γ2 FF5 非 entry defer | Kill 逐层展开 | 763 | [B-γ2](Plan/Step_B_Gamma2_FF5_NonEntryDefer.md) |
| — | B-γ3 BM1 Benchmark | B06 FuncCall + CI 基线 | 795 | [B-γ3](Plan/Step_B_Gamma3_BM1_Benchmark.md) |
| — | B-γ4 O15 热循环 | SENTINEL，VM 时间 -32%~-80% | 795 | [B-γ4](Plan/Step_B_Gamma4_O15_HotLoop.md) |
| — | B-γ7 SN1 嵌套结构体 | 递归拍平 + 循环引用检测 | 884 | [B-γ7](Plan/Step_B_Gamma7_SN1_NestedStruct.md) |
| — | B-γ9 STR1 常量字符串 | StringConstants ROM | 913 | — |
| — | B-δ1~δ5 | O10+SO1+FF3+SN2+C5 | 1007 | 各步骤子文档 |

**B 阶段全部完成。1007 项 Assert × 2 模式全通过。**

---

### B-ε/ζ/η 优化串行计划 ✅（全部完成）

- **B-ε**（追平 Lua，4/4 ✅）：fixed pin + Compare&Branch fusion + const/DCE + FORLOOP 超级指令
- **B-ζ**（分支优化，3/3 ✅）：LICM + CMP-immediate + SWITCH 跳转表
- **B-η**（指令压缩 ✅）：16B → 4B（O8-1~O8-3/O8-5 ✅，O8-4 临时妥协 ⏸）

---

### Lang. 语言需求实现（SK14 + Q4）

> **来源**：SK14 — FFS 语言需求整合 + Q4 服务脚本设计
> **原则**：优先理想方案。任何语言层改动必须运行 B01-B06 benchmark 确认无回归。

| 序号 | 步骤 | 状态 | 内容 | 复杂度 |
|------|------|------|------|--------|
| Lang-1 | 模块变量 (L1) | ✅ | Parser 顶层 `var`/`const` + 保留寄存器段 | ⭐⭐ |
| Lang-1.1a | MaxRegisters 配置化 | ✅ | VMConstants 派生常量 | ⭐ |
| Lang-1.1b | 扩展寄存器 | ✅ | LOAD_XREG/STORE_XREG 零开销溢出 | ⭐⭐ |
| Lang-2 | include (L2) | ✅ | 预处理器递归展开 + 重定义规则 | ⭐⭐ |
| Lang-3 | 黑板 Syscall | ✅ | Get/SetBlackboard 标准 Syscall | ⭐ |
| *Lang-4* | *跨模块共享变量* | *⏳* | *按需触发（黑板瓶颈时）* | *⭐⭐⭐* |
| Lang-6 | XCALL 基线 | ✅ | XCALL + XLOAD_MVAR + XSTORE_MVAR + @export | ⭐⭐⭐ |
| Lang-7 | 自动退化 + VMConfig | ✅ | getter/setter 退化 + XCallDepthPolicy | ⭐⭐ |
| Lang-8 | 统一语法 + @inline | ✅ | `svc.member` 点号语法 | ⭐⭐⭐ |
| Lang-9 | 深度内联 P1-P4 | ✅ | 模块内/跨模块/深度链式 | ⭐⭐⭐ |
| Lang-10 | 导出变量默认值 | ✅ | ExportVarEntry.DefaultValue | ⭐⭐ |
| Lang-11 | 模块级 struct 初始化 | ✅ | 模块级 struct var/const 直接初始化 | ⭐⭐ |
| Lang-12 | @export const | ✅ | 基础类型导出常量 | ⭐ |
| Lang-13 | 枚举 (enum) | ✅ | 语法糖 → 编译期命名整数常量 | ⭐⭐ |
| Lang-14 | 位运算 | ✅ | `& \| ^ ~ << >>` 全链路 | ⭐⭐ |
| Lang-15 | Include 可见性 | ✅ | public/private + origin-aware 编译 | ⭐⭐⭐ |
| Lang-16 | Override 关键字 | ✅ | 显式跨文件替换 | ⭐⭐ |
| Lang-17 | Include As 别名 | ✅ | `include "path" as Alias` | ⭐⭐⭐ |
| Lang-18 | Override Alias | ✅ | `override func Alias.Name()` | ⭐⭐ |

---

### DX. 开发体验改进

| 序号 | 步骤 | 状态 | 内容 | 复杂度 |
|------|------|------|------|--------|
| DX4-P0 | LSP workspace | ✅ | rootUri + .ffvm.d.json 自动发现 | ⭐⭐ |
| DX4-P1 | .ffproj 项目文件 | ✅ | ProjectFile + CompositeFileResolver | ⭐⭐⭐ |
| DX4-P2 | CLI 项目编译 | ✅ | `ffvm-cli init/compile/run --project` | ⭐⭐ |
| DX4-P3 | 跨文件符号 | ✅ | 合并 AST + 跨文件 definition/references | ⭐⭐⭐ |
| DX4-P4 | LSP 辅助创建 .ffproj | ✅ | workspace/applyEdit 自动创建 | ⭐⭐ |
| DX5 | 重命名 + 语义染色 | ✅ | rename + semanticTokens/full + Include 导航 | ⭐⭐⭐ |
| DX6 | Include 重命名 | ✅ | willRenameFiles 自动更新引用 | ⭐⭐ |
| DX7 | AST 精确位置 | ✅ | 字段/类型注解精确位置追踪 | ⭐⭐ |
| DX8 | external func | ✅ | 无体声明 + 跨文件错误 + 表达式染色 | ⭐⭐⭐ |
| DX9 | 语义染色改进 | ✅ | 10 类 token 染色 | ⭐⭐ |
| R1 | LSP 架构重构 | ✅ | AstWalker + DocumentStore，4100→3780 行 | ⭐⭐ |
| E003 | 紧急修复 | ✅ | 枚举引用 + didClose | ⭐⭐ |
| E004 | 模块级符号导航 | ✅ | 模块变量完整 LSP 支持 | ⭐⭐ |
| DX10 | 依赖图 + 全项目诊断 | ✅ | Include 依赖图 + RecompileDependents | ⭐⭐⭐ |
| DX11 | VFS + Rename 状态 | ✅ | DocumentStore.RenameUri + 连续重命名修复 | ⭐⭐ |
| DX12 | 后台编译调度 | ⚪ | debounce + 取消 + 缓存（远期待激活） | ⭐⭐ |
| DX13 | 参数 LSP 完整支持 | ✅ | KL-01 参数引用含声明位置 + KL-02 参数重命名 + 声明位置定义精确化 + 签名悬停。DX13-01~09（16 asserts）。计划 → [Step_DX13_ParameterLsp](Plan/Step_DX13_ParameterLsp.md)　讨论 → [D_LspUsabilityAudit](Discussion/D_LspUsabilityAudit.md) | ⭐⭐ |
| DX14 | Rename 完整性补全 | ✅ | KL-03 struct 字面量名计入 struct 重命名编辑（CollectReferencesWithOrigin Struct 分支追加 StructLiteralTypeRefsWalker 函数体走查）。DX14-01~05（14 asserts）。计划 → [Step_DX14_RenameCompleteness](Plan/Step_DX14_RenameCompleteness.md)　讨论 → [D_LspUsabilityAudit](Discussion/D_LspUsabilityAudit.md) | ⭐ |
| DX15 | Private 跨文件补全过滤 | ✅ | KL-04 private func/struct/enum/var 不出现在 include 文件的 completion 中（HandleCompletion IsPrivate + IsFromOtherFile 守卫）。DX15-01~07（16 asserts）。计划 → [Step_DX15_PrivateCompletionFilter](Plan/Step_DX15_PrivateCompletionFilter.md)　讨论 → [D_LspUsabilityAudit](Discussion/D_LspUsabilityAudit.md) | ⭐⭐ |
| DX16 | 变量引用作用域隔离 | ✅ | KL-05 同名变量引用按作用域精确匹配（FindSymbolWalker 块级作用域追踪 + ScopedIdentRefsWalker 精确声明匹配）。DX16-01~08（16 asserts），DX12-22 升级严格断言。计划 → [Step_DX16_ScopeIsolatedRefs](Plan/Step_DX16_ScopeIsolatedRefs.md)　讨论 → [D_LspUsabilityAudit](Discussion/D_LspUsabilityAudit.md) | ⭐⭐⭐ |
| DX17 | 统一符号解析 | ✅ | 合并 SymbolAtPosition + FindDefinitionLocation → ResolvedSymbol。HandleDefinition/HandleReferences/HandleRename/HandleHover 共享 ResolveSymbol。消除 ResolveSymbolDualAst 二次查找。修复 WaitForStmt 子表达式遗漏。DX17-01~02（4 asserts）。计划 → [Step_DX17_UnifiedSymbolResolution](Plan/Step_DX17_UnifiedSymbolResolution.md)　讨论 → [D_LspStructuralAudit](Discussion/D_LspStructuralAudit.md) | ⭐⭐ |
| DX18 | 统一引用收集 | ✅ | 8 个引用 Walker → 1 个 UnifiedRefsWalker。CollectReferencesWithOrigin 8 分支 → CollectDeclarationLocations + 统一遍历 4 分支。死代码 CollectReferences 删除。LspServer.cs −242 行（4799→4557）。656 LSP 测试全通过。计划 → [Step_DX18_UnifiedRefCollection](Plan/Step_DX18_UnifiedRefCollection.md)　讨论 → [D_LspStructuralAudit](Discussion/D_LspStructuralAudit.md) | ⭐⭐⭐ |

**Lang 系列全部完成。DX13 ✅ 完成。DX14 ✅ 完成。DX15 ✅ 完成。DX16 ✅ 完成。DX17 ✅ 完成。DX18 ✅ 完成。**
**DX12（后台编译调度）⚪ 远期待激活。C 区间阻塞于宿主 ECS 就绪。**
**语言易用性审查（D18）→ 7 项改进建议（UC-1~UC-7）已纳入 [Outlook §2.10](Plan/Outlook_And_Risks.md)。**

---

### C. 待执行阶段（宿主集成侧 — 生产必经路径）

以下步骤依赖真实游戏宿主环境。部署架构决策详见 [Step_C0_DeploymentArchitecture.md](Discussion/Step_C0_DeploymentArchitecture.md)。

| 序号 | 步骤 | 状态 | 内容 | 前置条件 |
|------|------|------|------|----------|
| C0 | 部署架构决策 | ✅ | VM 分配策略、多实例交互、数据读取 | — |
| C1 | 真实 Syscall 接入 ECS | ⚪ | stub → 真实宿主实现 | 宿主 ECS 就绪 |
| C2 | V5 帧内 Profiler 验证 | ⚪ | 含真实 ECS 交互开销的 Tick 耗时测量 | C1 完成 |
| C3 | 技能资源管线 | ⚪ | .ffs 加载/编译/缓存/热更新 | C1 完成 |
| C4 | Handle64 批处理 | ⏳ | 句柄化多目标数据流转 | C1 完成 |
| C5 | 帧同步集成验证 | ⚪ | 真实网络环境快照/回滚 | C1+C2 完成 |
| C6 | 编辑器流程图投影 | ⏳ | AST → 结构化流程图主视图 | C2 通过 |

---

### D. 分发基础设施 ✅（全部完成）

| 序号 | 步骤 | 状态 | 内容 |
|------|------|------|------|
| DIST-1 | 独立 FFVM 类库 | ✅ | `src/FFVM/FFVM.csproj` 双目标 |
| DIST-2 | 统一 CLI | ✅ | `ffvm-cli run/compile/lsp/dap/version` |
| DIST-3 | 单文件发布 | ✅ | PublishSingleFile 跨平台 |
| DIST-8 | EmbeddableDapServer | ✅ | DapServerBase + attach 模式 |
| DIST-9 | Sandbox 改造 | ✅ | 消费分发库 API |
| DIST-10 | .NET 多版本兼容 | ✅ | 双目标 TFM + RollForward |

---

## 临时妥协区

| 当前妥协 | 理由 | 未来补全路径 |
|----------|------|-------------|
| 开发期 float（`Number`） | 快速迭代，Fix64 调试较痛苦 | 正式测试和上线构建必须 `USE_FIXPOINT` |
| 无编辑器 UI | 编辑器依赖稳定 AST + 真实 Syscall | C6 流程图投影（C2 通过后） |
| 无 Handle64 批处理 | 曳光弹不涉及多目标 | C4（真实多目标业务接入前） |
| Paired Syscall 仅无参反向调用 | 覆盖 80%+ 场景 | 如需带参反向调用扩展配对协议 |
| 函数参数上限 = 16 | 与 Syscall 参数传递一致 | 如需更多参数扩展寄存器布局 |
| O8-4 Peephole 适配 ⏸ | EXTEND_AX 在 Peephole 后运行 | 未来按需消除冗余 |

---

## 展望区

> 详见 [Plan/Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)

### 功能展望

| 分组 | 未完成 | 已完成 |
|------|--------|--------|
| 函数调用 | FF2 回调、FF4 多返回值 | FF3 可选参数 ✅、FF5 非 entry defer ✅ |
| 全局 / 跨步骤 | H1 Handle64、PR1 带参配对、DM1 双轨、B1 Editor DAP | BB1 黑板 Key ✅ |
| 部署架构 (MI) | MI-1~MI-5 | — |
| 结构体 | MSV-F1 const 折叠、MSV-F2 @export struct（暂缓） | S4 ✅、SN1 ✅、SN2 ✅ |
| Include | IA-F1 LSP、VIS-1 默认 private（暂缓） | Lang-15~18 ✅ |
| LSP 架构 | DX12 后台编译（远期）、DX13~DX16 审查改进 ✅、DX17 统一符号解析 ✅、DX18 统一引用收集 ✅ | DX10 ✅、DX11 ✅ |

### 优化展望

已执行 19 项优化（详见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)）。

未实施：O11-O14 运行时优化、FO2 尾调用、FO3 小函数内联。

### 已识别风险（20 项，全部降至低/极低）

| 分组 | 条目 | 数量 | 降级后等级 |
|------|------|------|-----------|
| 步骤 8 — 已缓解 | R1-R4 | 4 | 极低 |
| 步骤 8 — 前瞻 | R5-R7 | 3 | 低 |
| 步骤 9 | SR1-SR4 | 4 | 低~极低 |
| 全局 | GR1-GR3 | 3 | 低~极低 |
| 外部工具对接 | DR1-DR5 | 5 | 低~极低 |

> 风险降级详细措施见 [Outlook_And_Risks.md §六](Plan/Outlook_And_Risks.md#六风险降级计划目标全部--低--极低)。
