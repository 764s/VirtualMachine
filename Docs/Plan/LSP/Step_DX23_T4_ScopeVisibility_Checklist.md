# DX23: T4 作用域与可见性 — private / 同名冲突 / 块级作用域 / override 扩展 Checklist

> 前置：DX22 已完成（T3 语法结构覆盖矩阵全绿，95/95）。T4 目标：在 T3 已建立的 fact 发射 + 作用域链基础上，补齐可见性过滤（private）、同名符号仲裁、块级作用域隔离、以及 override 在非函数声明类型上的覆盖。
> 用户意图：LSP 查询结果遵守语义可见性规则 — private 不越文件、同名不串符号、嵌套遮蔽绑定最近声明、override 仅替换合法目标。

## 零、规划原则

- P-1：以 SC-01~SC-04 四个子目标为验收单元，每个 SC 对应一个 Phase。
- P-2：CFR-09 / CFR-15 已被 T2/T3 覆盖，T4 仅补残余场景（override 非函数类型 / 块级作用域）。
- P-3：不改变 `SymbolIdentity` 数据结构（避免大范围重构），优先在 `BuildImported*` 方法中加过滤。
- P-4：块级作用域（SC-03）是最复杂项，若 AST 无 scope ID 支持，退化为"行号范围"近似方案。

## 一、完成定义（Definition of Done）

- [ ] DOD-1：private 函数 / 变量 / 结构体 / 枚举的 definition 仅在本文件可见，跨文件 references 不越权命中（CFR-08）。
- [ ] DOD-2：同名函数跨文件冲突时，按可见性 + 作用域稳定仲裁，不串符号（CFR-07）。
- [ ] DOD-3：局部变量在块级作用域（if/while/for）中重声明时，references 绑定最近合法声明（SC-03）。
- [ ] DOD-4：override 对 var / struct / enum 声明类型的替换行为正确（SC-04 扩展）。
- [ ] DOD-5：所有 SC-01~SC-04 场景均有 LSPNEW 测试覆盖。

## 二、当前基线

### 已实现（T2/T3 落地）

- [x] B-1：override 函数 definition/references 正确（T2-OV-01/02/03 全绿）— SC-04 函数部分 ✅。
- [x] B-2：同名字段按 parent 区分（T3-FLD-01 全绿）— CFR-15 ✅。
- [x] B-3：参数遮蔽模块变量（T3-PRM-02 全绿）— SC-03 单层 ✅。
- [x] B-4：局部变量遮蔽模块变量（T3-LOC-02 全绿）— SC-03 单层 ✅。
- [x] B-5：作用域链解析顺序 `local > param > module > imported`（T3 实现）。

### 缺口（本轮需补）

- [ ] G-1：`TryReadImportedFunctionSymbols` / `TryReadImportedNameSymbols` 无 `IsPrivate` 过滤 → private 符号越文件泄漏（SC-01 / CFR-08）。
- [ ] G-2：同名函数跨文件时无可见性仲裁 — 后导入覆盖先导入（SC-02 / CFR-07）。
- [ ] G-3：`CollectVarDeclStmtsFromBody` 扁平收集所有 `VarDeclStmt`，无块级隔离（SC-03 块级）。
- [ ] G-4：override 对 var / struct / enum 声明仅有 `EmitOverrideDefinitions` 发射，无专项测试（SC-04 扩展）。

## 三、目标覆盖映射

| CFR / SC 场景 | 可搜索单元 | 当前状态 | Phase |
| --- | --- | --- | --- |
| CFR-08 / SC-01 | private func / var / struct / enum | ❌ 未过滤 | 0 |
| CFR-07 / SC-02 | 同名函数跨文件 | ❌ 无仲裁 | 1 |
| SC-03 | 块级局部变量遮蔽 | ❌ 扁平字典 | 2 |
| SC-04 扩展 | override var / struct / enum | ⚠️ 仅函数 | 3 |
| CFR-09 | override 跨文件替换（函数） | ✅ T2 已覆盖 | — |
| CFR-15 | 同名字段不同结构体 | ✅ T3 已覆盖 | — |

## 四、推进清单

### Phase 0：SC-01 Private 可见性过滤（CFR-08）

> 核心改动：在 `BuildImported*` 系列方法中增加 `IsPrivate` 过滤，阻止 private 声明跨文件泄漏。

- [ ] P0-1：`TryReadImportedFunctionSymbols` — 增加 `if (function.IsPrivate) continue;` 过滤。
- [ ] P0-2：`TryReadImportedNameSymbols` — 对 `ModuleVariables`、`Structs`、`Enums` 增加 `IsPrivate` 过滤。
- [ ] P0-3：`BuildImportedStructFieldSymbols` — private struct 的字段不导出。
- [ ] P0-4：`BuildImportedEnumMemberSymbols` — private enum 的成员不导出。
- [ ] P0-5：LSPNEW 测试 T4-SC01-01 — private 函数跨文件不可见。
  - 场景：lib.ffs 声明 `private func helper()` + `func api() { helper() }`，main.ffs include lib 并尝试引用 `helper`。
  - 断言：main 中 `helper` 的 references 不包含 lib 的 private 定义。
- [ ] P0-6：LSPNEW 测试 T4-SC01-02 — private 变量 / 结构体 / 枚举跨文件不可见。

验收标准：
- [ ] A0-1：T4-SC01-01、T4-SC01-02 全绿。
- [ ] A0-2：T2/T3 全量 LSPNEW 全绿 — 无退化。

### Phase 1：SC-02 同名函数跨文件仲裁（CFR-07）

> 核心问题：两个文件各定义 `func draw()`，include 后 references 应按可见性 + 文件来源仲裁。

- [ ] P1-1：分析当前同名冲突行为 — 确认 `importedFunctionSymbols` 字典 last-writer-wins 还是 first-writer-wins。
- [ ] P1-2：实现仲裁策略 — 本地定义优先于导入；多个导入同名时保留第一个（或产出诊断）。
- [ ] P1-3：LSPNEW 测试 T4-SC02-01 — 同名函数本地定义优先。
  - 场景：lib.ffs 声明 `func draw()`，main.ffs include lib 并自行声明 `func draw()`。
  - 断言：main 中调用 `draw()` 的 references 绑定到 main 本地定义，不含 lib 定义。
- [ ] P1-4：LSPNEW 测试 T4-SC02-02 — 同名函数跨两个导入文件。

验收标准：
- [ ] A1-1：T4-SC02-01、T4-SC02-02 全绿。
- [ ] A1-2：Phase 0 测试仍绿 + 全量 LSPNEW 全绿。

### Phase 2：SC-03 块级作用域隔离

> 核心改动：`CollectVarDeclStmtsFromBody` 需追踪块深度或行号范围，使同名局部变量在不同块中独立解析。
> T3 R3 延迟项：块级作用域（if/while/for 内 var）、同名局部多次声明精度。

- [ ] P2-1：设计方案 — 行号范围近似 vs. 块 ID 标注（AST 无 scope ID，优先行号方案）。
- [ ] P2-2：扩展 `CollectVarDeclStmtsFromBody` — 为每个 `VarDeclStmt` 附加所属块的起止行号。
- [ ] P2-3：扩展 `EmitIdentifierReferenceFacts` 局部变量解析 — 按引用位置行号选择最近包围块内的同名变量。
- [ ] P2-4：LSPNEW 测试 T4-SC03-01 — if 块内 var 遮蔽外层 var。
  - 场景：`func f() { var x = 1; if true { var x = 2; return x } return x }`
  - 断言：内层 `x` references 不含外层，外层 `x` references 不含内层。
- [ ] P2-5：LSPNEW 测试 T4-SC03-02 — for 循环变量作用域隔离。

验收标准：
- [ ] A2-1：T4-SC03-01、T4-SC03-02 全绿。
- [ ] A2-2：Phase 0-1 测试仍绿 + 全量 LSPNEW 全绿。

### Phase 3：SC-04 Override 扩展（var / struct / enum）

> T2-OV-01/02/03 仅覆盖函数 override。本 Phase 补测 var / struct / enum override 行为。

- [ ] P3-1：验证 `EmitOverrideDefinitions` 已处理 var / struct / enum override 发射。
- [ ] P3-2：LSPNEW 测试 T4-SC04-01 — var override definition + references。
- [ ] P3-3：LSPNEW 测试 T4-SC04-02 — struct override definition + references。
- [ ] P3-4：LSPNEW 测试 T4-SC04-03 — enum override definition + references。

验收标准：
- [ ] A3-1：T4-SC04-01/02/03 全绿。
- [ ] A3-2：Phase 0-2 测试仍绿 + 全量 LSPNEW 全绿。

### Phase 4：收敛 + 回归

- [ ] P4-1：全量 LSPNEW 回归通过。
- [ ] P4-2：FFVM.Cli 与 StandaloneRunner 编译全绿。
- [ ] P4-3：更新 Overview_LSP.md 仪表板，标记 DX23 完成。

验收标准：
- [ ] A4-1：全量 LSPNEW 全绿。
- [ ] A4-2：FFVM.Cli + StandaloneRunner 编译全绿。

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


