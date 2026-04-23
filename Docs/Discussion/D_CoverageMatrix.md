# LSP 覆盖矩阵：从语法文法生成的两层结构化穷举

> **状态**：✅ 已完成讨论
> **日期**：2026-04-23
> **来源**：用户关于"覆盖矩阵"整理过程中的注意力耗尽问题 → 换视角从语法文法生成
> **关联**：D17 [D_LspUsabilityAudit](D_LspUsabilityAudit.md)（三维矩阵审查）、D19 [D_LspStructuralAudit](D_LspStructuralAudit.md)（笛卡尔积遗漏根因）、D20 [D_ResolveSymbolCollisionTopDown](D_ResolveSymbolCollisionTopDown.md)

---

## 一、问题陈述

整理覆盖矩阵的原意是：**以机械的矩阵形式让 AI 在实现 LSP 功能时获得完整参考，确保语言服务器不会遗漏任何可能，新增语法时也能完美覆盖。**

实践中发现：

- 仅"简单声明和初始化"两个入口就让人工注意力耗尽；
- 展开成"声明结构 → 标识符名 / 类型结构 → 模块名结构 → 直接/别名 …"这样的嵌套树后，分类继续无限细化；
- 这个树结构本质上和语法文法几乎同构，只是另一个视角。

**核心疑问**：是否存在从语法文法生成覆盖矩阵、从结构上确保不遗漏的可能？

**结论**：存在。而且本仓库的 AST 已具备足够的结构来作为矩阵生成源。

---

## 二、为什么手工穷举会爆炸

手工整理时选择的主维度是**"可能出现的位置"**，但位置本身是由文法递归定义的——同一个 `Expr` 可以出现在 `VarDeclStmt.Initializer`、`ParamDecl.DefaultValue`、`CallExpr.Arguments[i]`、`StructLiteralExpr.Fields[k].Value`、`ReturnStmt.Value`、`IfStmt.Condition`、`UsingStmt.Arguments[i]`、`WaitForStmt.TargetInstanceId` …

以位置为主轴 → 维度数 ≈ 文法产生式的所有位置组合 → 指数爆炸。

正确做法：**把位置降为派生维度，把 AST 节点族升为主维度。**

---

## 三、两层矩阵

### 3.1 第一层：语法结构层（Grammar / AST）

**源**：`Assets/Scripts/VM/AST/ASTNode.cs` 的 `NodeKind` 枚举 + AST 类定义。

**作用**：枚举所有可能出现的结构形态，解决"漏语法形态"。

**主轴（AST 节点族）**：

| 族 | 成员 | 当前来源 |
|----|------|---------|
| **顶层容器** | `ModuleNode.Imports / Structs / Enums / Functions / ModuleVariables` | ASTNode.cs:544-564 |
| **声明** | `ImportDecl`、`StructDecl`、`EnumDecl`、`EnumMember`、`StructField`、`FuncDecl`、`ParamDecl`、`VarDeclStmt` | ASTNode.cs:385-540 |
| **语句** | `BlockStmt`、`IfStmt`、`WhileStmt`、`ForStmt`、`ReturnStmt`、`WaitStmt`、`WaitForStmt`、`YieldStmt`、`DeferStmt`、`UsingStmt`、`ExprStmt`、`VarDeclStmt`（作为语句） | ASTNode.cs:238-383 |
| **表达式** | `NumberLiteralExpr`、`IntLiteralExpr`、`BoolLiteralExpr`、`StringLiteralExpr`、`StringIdLiteralExpr`、`IdentifierExpr`、`FieldAccessExpr`、`BinaryExpr`、`UnaryExpr`、`AssignExpr`、`CallExpr`、`SyscallExpr`、`MemberCallExpr`、`StructLiteralExpr` | ASTNode.cs:80-229 |

**子轴（节点内的子位置）**：每个节点的非平凡字段。例如：

- `VarDeclStmt`：`Name` / `TypeName` / `Initializer`
- `FuncDecl`：`Name` / `Parameters[]` / `ReturnType` / `Body`
- `ParamDecl`：`Name` / `TypeName` / `DefaultValue`
- `StructLiteralExpr`：`TypeName` / `Fields[].FieldName` / `Fields[].Value`
- `MemberCallExpr`：`TargetName` / `MemberName` / `Arguments[]`

**关键性质**：这一层是**机械可生成**的——反射/代码生成器扫一遍 AST 类即可得出。新增 `NodeKind` 或字段 → 自动出现在矩阵中。

### 3.2 第二层：语义覆盖层（LSP 关注点）

**源**：`Assets/Scripts/VM/Debug/Lsp/Database/Contracts/SymbolKindTag.cs` + LspServer 处理器。

**作用**：把语法结构投影到 LSP 语义维度，解决"语法有但功能漏采集"。

固定三条语义轴，**不从位置推导**：

| 轴 | 取值 |
|----|------|
| **A. 符号角色** | 定义点（definition）、名称引用、类型引用、调用引用、成员访问、枚举值访问、模块别名使用 |
| **B. 采集通道** | 声明区（top-level 列表本身）、函数体（`FuncDecl.Body`）、表达式子树（初始化器 / 参数默认值 / 调用实参 / 条件表达式 / 等）|
| **C. LSP 功能面** | definition、references（含 includeDeclaration）、rename、prepareRename、hover、completion、signatureHelp、documentSymbol、semanticTokens |

矩阵单元 = **AST 节点族 × 子位置 × (A, B, C)**。

**关键性质**：C 轴取值稳定（LSP 协议决定），A/B 轴取值稳定（符号语义决定），只有 AST 主轴变化——因此新增语法只增加行，不改变列。

---

## 四、机械闭环

### 4.1 生成

1. **枚举器**：遍历 AST 类型，按族/子位置输出行。
2. **投影器**：对每行尝试与 (A, B, C) 笛卡尔交集——不是盲目笛卡尔，而是按"节点语义是否承担该角色"过滤。例如 `NumberLiteralExpr` 不承担任何符号角色，整行标注 **N/A**。
3. **输出**：一张表 + 一份"未投影"节点清单（用于人工确认是否真的 N/A 还是遗漏语义）。

### 4.2 绑定

每个非 N/A 单元对应至少一个测试点——以 `SymbolKindTag × 容器 × 功能` 的三元标签命名，使矩阵单元与测试有**双向可追溯**关系：

- 正向：矩阵 → 测试是否存在？不存在 → **未覆盖**。
- 反向：测试 → 标签是否落在矩阵中？不在 → 说明矩阵漏了一行。

### 4.3 约束

- **新增 `NodeKind` 或 AST 字段** → 生成器输出新行 → 若无对应测试 → CI 失败。
- **新增 `SymbolKindTag`** → 投影器多出一列 → 旧行出现空单元 → CI 失败。
- **新增 LSP 功能面**（新请求方法） → C 轴多取值 → 所有行重新评估 → CI 失败。

这把"靠注意力穷举"变成"结构化穷举 + 自动缺口报警"。

---

## 五、当前仓库的对齐情况

本讨论**不立即实施**代码层矩阵生成器，仅做对齐记录，供后续计划引用。

已有的"隐式矩阵"位点：

| 位点 | 性质 | 文件 |
|------|------|------|
| `SymbolKindTag` 枚举 | A 轴的离散化 | `Assets/Scripts/VM/Debug/Lsp/Database/Contracts/SymbolKindTag.cs` |
| `UnifiedRefsWalker` 的 7 类匹配规则 | A×AST 的一维化（DX18 成果） | `Assets/Scripts/VM/Debug/LspServer.cs:3157-3525` |
| `CollectDeclarationLocations` | B 轴"声明区"通道 | 同上 |
| `CollectReferencesWithOrigin` 的两条遍历入口 | B 轴"函数体 / 模块变量初始化器"通道 | 同上 |
| D17 §三维矩阵 | 10 符号类型 × 8 功能 × 6 文件范围的人工投影 | `Docs/Discussion/D_LspUsabilityAudit.md` |

**已知 B 轴遗漏**（来自串行记忆）：

- 数据库后端 call-reference 递归器缺 `SyscallExpr` 分支，`syscall(...)` 实参中的 `CallExpr` 不生成 `SymbolReference`。
- `CollectCallReferencesFromStatement` 入口仅覆盖 `function.Body`，未覆盖 `ModuleVariables.Initializer` 与 `ParamDecl.DefaultValue`。
- 来源：`Assets/Scripts/VM/Debug/Lsp/Database/Operations/InMemoryDatabaseExecutionOrchestrator.cs:4405-4554 / 1502-1532 / 3450`

这两处正是"矩阵单元存在但 B 通道未接入"的典型，属于**即使当前静态矩阵补齐、只要不机械强制，也会再次遗漏**的品类——恰好验证了本讨论的必要性。

---

## 六、对用户提纲的回译

用户原提纲是手工展开的一棵树，将其按本框架回译：

| 用户提纲节点 | 本框架位置 |
|-------------|-----------|
| 声明结构 / 标识符名 + 类型结构 | **主轴：声明族**（ParamDecl、VarDeclStmt、StructField、EnumMember 等）。`Name` / `TypeName` 为子位置 |
| 初始化结构 | 子位置：`VarDeclStmt.Initializer` / `ParamDecl.DefaultValue`（B 轴"表达式子树"通道） |
| 类型结构 / 模块名 / 类型名 | **A 轴"类型引用"**在 `TypeName` 字符串上的解析（当前通过 `TypeRefsWalker` 的规则覆盖） |
| 模块名 / 直接 / 别名 | 别名由 `ImportDecl.Alias + ModuleNode.AliasedModules` 驱动；A 轴新增"模块别名使用"取值 |
| 值结构 / 枚举值 / 结构体值 / 函数调用 | **主轴：表达式族**（IdentifierExpr / FieldAccessExpr / StructLiteralExpr / CallExpr / MemberCallExpr / SyscallExpr） |
| 单函数调用 / 嵌套 / 链式 | 嵌套通过 `CallExpr.Arguments[i]` 递归，链式目前仅 `MemberCallExpr`（一层） |
| 普通调用 / 扩展调用 | `CallExpr` vs `MemberCallExpr` / `SyscallExpr`——都在表达式族内 |
| 简单调用 / 函数名 + 实参列表 | `CallExpr.FunctionName`（A 轴"调用引用"）+ `Arguments[]`（B 轴"表达式子树"） |

**全部嵌套结构都降为"节点族 → 子位置 → (A,B,C) 投影"的三级，不再需要无限细化。**

---

## 七、行动项

> 本讨论只固化框架，不引入代码变更。生成器与 CI 守卫作为后续计划提交，不列入本次 DX 串行计划。

- **CM1**（可选）生成器骨架：扫描 `AST/ASTNode.cs` 输出 AST 族/子位置清单（代码生成或运行期反射）。
- **CM2**（可选）投影器骨架：对每行×(A,B,C) 打出"覆盖 / N/A / 未覆盖"三态。
- **CM3**（可选）测试标签化：LSP 测试以 `[SymbolKindTag]_[Container]_[Feature]` 命名，使矩阵 ↔ 测试可双向查。
- **CM4**（可选）CI 守卫：新增 `NodeKind` / `SymbolKindTag` / LSP 功能 → 矩阵增量未覆盖 → 失败。
- **CM 立即可用**：已知 B 轴遗漏（§五）可作为第一批用例，验证矩阵建立后能否机械暴露问题——无需完整生成器即可手工交叉核对。

**这些条目暂不进入主串行计划**；若后续决定激活，通过 `#requirement` 模板提交入 Outlook 串行。
