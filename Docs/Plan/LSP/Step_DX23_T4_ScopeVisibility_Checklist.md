# DX23: T4 作用域与可见性 — private / 同名冲突 / 块级作用域 / override 扩展 Checklist

> 前置：DX22 已完成（T3 语法结构覆盖矩阵全绿，95/95）。T4 目标：在 T3 已建立的 fact 发射 + 作用域链基础上，补齐可见性过滤（private）、同名符号仲裁、块级作用域隔离、以及 override 在非函数声明类型上的覆盖。
> 用户意图：LSP 查询结果遵守语义可见性规则 — private 不越文件、同名不串符号、嵌套遮蔽绑定最近声明、override 仅替换合法目标。

## 零、规划原则

- P-1：以 SC-01~SC-04 四个子目标为验收单元，每个 SC 对应一个 Phase。
- P-2：CFR-09 / CFR-15 已被 T2/T3 覆盖，T4 仅补残余场景（override 非函数类型 / 块级作用域）。
- P-3：不改变 `SymbolIdentity` 数据结构（避免大范围重构），优先在 `BuildImported*` 方法中加过滤。
- P-4：块级作用域（SC-03）是最复杂项，若 AST 无 scope ID 支持，退化为"行号范围"近似方案。

## 一、完成定义（Definition of Done）

- [x] DOD-1：private 函数 / 变量 / 结构体 / 枚举的 definition 仅在本文件可见，跨文件 references 不越权命中（CFR-08）。✅ T4-SC01-01/02
- [x] DOD-2：同名函数跨文件冲突时，按可见性 + 作用域稳定仲裁，不串符号（CFR-07）。✅ T4-SC02-01/02/03
- [x] DOD-3：局部变量在块级作用域（if/while/for）中重声明时，references 绑定最近合法声明（SC-03）。✅ T4-SC03-01/02
- [x] DOD-4：override 对 var / struct / enum 声明类型的替换行为正确（SC-04 扩展）。✅ T4-SC04-01/02/03
- [x] DOD-5：所有 SC-01~SC-04 场景均有 LSPNEW 测试覆盖。✅ 121/121 全绿

## 二、当前基线

### 已实现（T2/T3 落地）

- [x] B-1：override 函数 definition/references 正确（T2-OV-01/02/03 全绿）— SC-04 函数部分 ✅。
- [x] B-2：同名字段按 parent 区分（T3-FLD-01 全绿）— CFR-15 ✅。
- [x] B-3：参数遮蔽模块变量（T3-PRM-02 全绿）— SC-03 单层 ✅。
- [x] B-4：局部变量遮蔽模块变量（T3-LOC-02 全绿）— SC-03 单层 ✅。
- [x] B-5：作用域链解析顺序 `local > param > module > imported`（T3 实现）。

### 缺口（本轮需补）

- [x] G-1：`TryReadImportedFunctionSymbols` / `TryReadImportedNameSymbols` 无 `IsPrivate` 过滤 → ✅ 已修复，6 处 IsPrivate 过滤。
- [x] G-2：同名函数跨文件时无可见性仲裁 → ✅ 已验证，local-first + first-writer-wins 已实现。
- [x] G-3：`CollectVarDeclStmtsFromBody` 扁平收集所有 `VarDeclStmt` → ✅ 已修复，新增 `CollectScopedVarDecls` + `ResolveScopedLocalVar` 行号范围方案。
- [x] G-4：override 对 var / struct / enum 声明仅有 `EmitOverrideDefinitions` 发射 → ✅ 已验证 + 补测 T4-SC04-01/02/03。

## 三、目标覆盖映射

| CFR / SC 场景 | 可搜索单元 | 当前状态 | Phase |
| --- | --- | --- | --- |
| CFR-08 / SC-01 | private func / var / struct / enum | ✅ 已过滤 | 0 |
| CFR-07 / SC-02 | 同名函数跨文件 | ✅ 已仲裁 | 1 |
| SC-03 | 块级局部变量遮蔽 | ✅ 行号范围方案 | 2 |
| SC-04 扩展 | override var / struct / enum | ✅ 已补测 | 3 |
| CFR-09 | override 跨文件替换（函数） | ✅ T2 已覆盖 | — |
| CFR-15 | 同名字段不同结构体 | ✅ T3 已覆盖 | — |

## 四、推进清单

### Phase 0：SC-01 Private 可见性过滤（CFR-08）

> 核心改动：在 `BuildImported*` 系列方法中增加 `IsPrivate` 过滤，阻止 private 声明跨文件泄漏。

- [x] P0-1：`TryReadImportedFunctionSymbols` — 增加 `if (function.IsPrivate) continue;` 过滤。
- [x] P0-2：`TryReadImportedNameSymbols` — 对 `ModuleVariables`、`Structs`、`Enums` 增加 `IsPrivate` 过滤。
- [x] P0-3：`BuildImportedStructFieldSymbols` — private struct 的字段不导出。
- [x] P0-4：`BuildImportedEnumMemberSymbols` — private enum 的成员不导出。
- [x] P0-5：LSPNEW 测试 T4-SC01-01A/B — private 函数跨文件不可见。
- [x] P0-6：LSPNEW 测试 T4-SC01-02A~F — private 变量 / 结构体 / 枚举跨文件不可见。

验收标准：
- [x] A0-1：T4-SC01-01、T4-SC01-02 全绿（8 assertions）。
- [x] A0-2：T2/T3 全量 LSPNEW 全绿 — 无退化（103/103）。

### Phase 1：SC-02 同名函数跨文件仲裁（CFR-07）

> 核心问题：两个文件各定义 `func draw()`，include 后 references 应按可见性 + 文件来源仲裁。

- [x] P1-1：分析当前同名冲突行为 — first-writer-wins，local-first 已实现，无需代码改动。
- [x] P1-2：已验证仲裁策略 — 本地定义优先于导入；多个导入同名时 first-writer-wins。
- [x] P1-3：LSPNEW 测试 T4-SC02-01A/B/C — 同名函数本地定义优先。
- [x] P1-4：LSPNEW 测试 T4-SC02-02A/B — 同名函数跨两个导入文件 first-import-wins。
- [x] P1-5：LSPNEW 测试 T4-SC02-03A/B — 同名变量本地定义优先。

验收标准：
- [x] A1-1：T4-SC02-01/02/03 全绿（7 assertions）。
- [x] A1-2：Phase 0 测试仍绿 + 全量 LSPNEW 全绿（110/110）。

### Phase 2：SC-03 局部变量遮蔽模块变量（回溯补录）

> 核心问题：局部 `var` 声明与模块级变量同名时，references 必须绑定最近合法声明。

- [x] P2-1：引入 `CollectScopedVarDecls`（InMemoryIndexMaintainer）— 在函数体遍历阶段收集每个 `var` 声明的作用域行号区间。
- [x] P2-2：引入 `ResolveScopedLocalVar`（InMemoryLspQueryFacade）— 查询时按光标所在行号匹配最近的局部声明。
- [x] P2-3：LSPNEW 测试 T4-SC03-01/02/03 — 局部遮蔽、嵌套块、参数遮蔽。

**实现策略：行号范围近似方案**

由于当前 AST 未携带显式 scope ID，SC-03 采用 "声明起始行号 + 末尾行号" 的近似作用域表达。`CollectScopedVarDecls` 在每次 `var` 声明处记录 `[declLine, funcEndLine]`（函数级兜底），遇到后续同名声明时，前者的 `endLine` 被收窄为后者的 `declLine - 1`。`ResolveScopedLocalVar` 按光标行号在区间列表中查找最近的匹配声明。

**已知边界**（DX25 P4.4b 文档化）：
- 行号范围为线性（无分支感知）。`if/else` 的两个分支使用同名 `var` 时，两分支共用一个线性区间链，精度不足以区分"哪个分支的声明被引用"。Q 查询返回的是声明列表中最近的一条（通常为后出现分支的 `var`），这是已知偏差。
- `while`/`for` 循环体内同名 `var` 与外层同名 `var`：进入循环体的局部在其 `var` 声明行起生效，退出循环后外层 `var` 重新生效。当前实现对循环结束行的识别依赖函数体遍历器的返回深度，对嵌套深层循环已做栈式处理，对 `defer`/闭包捕获场景**未覆盖**。
- 上述边界场景用测试 T6-R02-01/02 做了基线 smoke 断言（LSPNEW 136/136 通过），不保证行为语义正确性，仅保证不崩溃、返回合理数量的绑定。

验收标准：
- [x] A2-1：T4-SC03-01/02/03 全绿。
- [x] A2-2：Phase 0-1 回归全绿 + 全量 LSPNEW 全绿（115/115）。

### Phase 3：SC-04 override 合法性与非函数类型（回溯补录）

> 核心问题：override 只应替换合法 alias 绑定；非法 override（如 override 到结构体、枚举）不应污染引用结果。

- [x] P3-1：`ResolveAliasOverrideTarget` — 仅对 `FunctionSymbol`/`ModuleVariableSymbol` 等合法目标生效。
- [x] P3-2：非法 override 静默忽略，不发射 fact，不影响 references 仲裁。
- [x] P3-3：LSPNEW 测试 T4-SC04-01/02/03 — 合法 override / 非法 override 静默 / 多层 override 链。

验收标准：
- [x] A3-1：T4-SC04-01/02/03 全绿。
- [x] A3-2：Phase 0-2 回归全绿 + 全量 LSPNEW 全绿（121/121）。

### Phase 4：收敛与回归（回溯补录）

- [x] P4-1：全量 LSPNEW 121/121 全绿。
- [x] P4-2：FFVM.Cli + StandaloneRunner 编译全绿。
- [x] P4-3：更新 Overview_LSP.md 标记 DX23 T4 完成。

## 五、已知边界与后续工作

- 行号范围近似方案的精度边界（见 Phase 2 已知边界章节）— 由 DX25 P4.2/P4.4b 复核并文档化，不再向后转嫁。
- T2 R1（alias × 局部变量重名）闭环复核 — 由 DX25 P4.1 的 T6-R01-01 smoke 测试覆盖。

## 六、回归命令

```
dotnet run --project StandaloneRunner -- --lsp-new-tests
dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug
```

### Phase 2：SC-03 块级作用域隔离

> 核心改动：`CollectVarDeclStmtsFromBody` 需追踪块深度或行号范围，使同名局部变量在不同块中独立解析。
> T3 R3 延迟项：块级作用域（if/while/for 内 var）、同名局部多次声明精度。

- [x] P2-1：设计方案 — 行号范围近似方案。新增 `ComputeMaxLine` / `CollectScopedVarDecls` / `ResolveScopedLocalVar`。
- [x] P2-2：扩展 `EmitLocalVarDefinitionFacts` — 返回 `List<(name, symbol, scopeStart, scopeEnd)>` 替代扁平字典。
- [x] P2-3：扩展 `EmitIdentifierReferenceFacts` — 使用 `ResolveScopedLocalVar` 按引用行号选择最窄包围作用域内的同名变量。
- [x] P2-4：LSPNEW 测试 T4-SC03-01A~D — if 块内 var 遮蔽外层 var。
- [x] P2-5：LSPNEW 测试 T4-SC03-02A~D — while 循环变量作用域隔离。

验收标准：
- [x] A2-1：T4-SC03-01、T4-SC03-02 全绿（8 assertions）。
- [x] A2-2：Phase 0-1 测试仍绿 + 全量 LSPNEW 全绿（118/118）。

### Phase 3：SC-04 Override 扩展（var / struct / enum）

> T2-OV-01/02/03 仅覆盖函数 override。本 Phase 补测 var / struct / enum override 行为。

- [x] P3-1：验证 `EmitOverrideDefinitions` 已处理 var / struct / enum override 发射 — 4 种类型全部实现。
- [x] P3-2：LSPNEW 测试 T4-SC04-01 — override const B.X go-to-definition。
- [x] P3-3：LSPNEW 测试 T4-SC04-02 — override struct B.Config go-to-definition。
- [x] P3-4：LSPNEW 测试 T4-SC04-03 — override enum Lib.Mode go-to-definition。

验收标准：
- [x] A3-1：T4-SC04-01/02/03 全绿（3 assertions）。
- [x] A3-2：Phase 0-2 测试仍绿 + 全量 LSPNEW 全绿（121/121）。

### Phase 4：收敛 + 回归

- [x] P4-1：全量 LSPNEW 回归通过 — 121/121 全绿。
- [x] P4-2：FFVM.Cli 与 StandaloneRunner 编译全绿 — 0 errors。
- [x] P4-3：更新 Overview_LSP.md 仪表板，标记 DX23 完成。

验收标准：
- [x] A4-1：全量 LSPNEW 全绿 — 121/121。
- [x] A4-2：FFVM.Cli + StandaloneRunner 编译全绿。

## 五、依赖关系

- Phase 0（private 过滤）是独立改动，可立即开始。
- Phase 1（同名仲裁）依赖 Phase 0（private 过滤后同名冲突场景更清晰）。
- Phase 2（块级作用域）独立于 Phase 0/1，可并行；但建议串行以控制风险。
- Phase 3（override 扩展）独立于 Phase 0-2，可任意时机执行。
- Phase 4 只在前面全部完成后执行。

建议顺序：Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4。

## 六、关键改动文件清单（预估）

| 文件 | 改动 |
| --- | --- |
| `InMemoryDatabaseExecutionOrchestrator.cs` | `TryReadImportedFunctionSymbols` / `TryReadImportedNameSymbols` 增 `IsPrivate` 过滤；`CollectVarDeclStmtsFromBody` 增块级行号追踪；`EmitIdentifierReferenceFacts` 按行号范围解析最近声明 |
| `LspServerNewTests.cs` | T4-SC01-01/02, T4-SC02-01/02, T4-SC03-01/02, T4-SC04-01/02/03（共 9 条新测试） |

> 只读参考：`ASTNode.cs`（`IsPrivate` 属性）、`SymbolIdentity.cs`（Scope 字段）。

## 七、回归命令清单

- `dotnet run --project StandaloneRunner -- --lsp-new-tests`
- `dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug`
- `dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Debug`

## 八、风险与缓解

| 风险 | 描述 | 缓解措施 |
| --- | --- | --- |
| R1 | `IsPrivate` 过滤可能破坏同文件内 private 函数的引用解析 | Private 过滤仅在 `BuildImported*`（跨文件导入）中生效，本地定义不受影响 |
| R2 | 块级作用域方案复杂度高（AST 无 scope ID） | 采用行号范围近似方案，不改 AST 结构；退化为 T3 扁平行为时仍正确（只是不精确） |
| R3 | 同名仲裁策略可能破坏 override 语义 | Override 使用 `AliasTarget` 独立路径解析，不经过同名仲裁 |
| R4 | Phase 2 块级作用域与未来 T5 嵌套函数 / 闭包有交叠 | T4 仅处理 if/while/for 块级，不处理嵌套函数；T5 再扩展 |

## 九、参考资料

- [Overview_LSP.md](Overview_LSP.md) — T4 专题定义、SC-01~SC-04、CFR-07/08/09/15
- [Step_DX22_T3_SyntaxStructure_Checklist.md](Step_DX22_T3_SyntaxStructure_Checklist.md) — T3 基线、R3 延迟项
- [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) — V2 矩阵基线


