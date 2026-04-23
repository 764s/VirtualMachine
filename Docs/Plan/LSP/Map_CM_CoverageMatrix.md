# Map_CM: LSP 覆盖矩阵首版（人工基线）

> 讨论来源：[D_CoverageMatrix.md](../../Discussion/D_CoverageMatrix.md) §三、§四、§五
> 执行计划：[Step_CM_CoverageMatrix.md](Step_CM_CoverageMatrix.md)
> 定位：**Map_ 辅助地图** — 步骤实施的交叉验证与完整性视角（见 [LSP/README.md](README.md)）
> 基准日期：2026-04-23

本文件是"语法结构层 × 语义覆盖层"首版基线。所有 AST 节点族、子位置、语义投影以当前 `Assets/Scripts/VM/AST/ASTNode.cs` 为准。

---

## 一、轴定义

### 轴 1：AST 节点族（主轴）

| 族 | 成员 |
|----|------|
| TOP-LEVEL | `ModuleNode.Imports[]`、`ModuleNode.Structs[]`、`ModuleNode.Enums[]`、`ModuleNode.Functions[]`、`ModuleNode.ModuleVariables[]`、`ModuleNode.AliasedModules` |
| DECL | `ImportDecl`、`StructDecl`、`StructField`、`EnumDecl`、`EnumMember`、`FuncDecl`、`ParamDecl`、`VarDeclStmt` |
| STMT | `BlockStmt`、`IfStmt`、`WhileStmt`、`ForStmt`、`ReturnStmt`、`WaitStmt`、`WaitForStmt`、`YieldStmt`、`DeferStmt`、`UsingStmt`、`ExprStmt`、`VarDeclStmt`（作为语句） |
| EXPR | `NumberLiteralExpr`、`IntLiteralExpr`、`BoolLiteralExpr`、`StringLiteralExpr`、`StringIdLiteralExpr`、`IdentifierExpr`、`FieldAccessExpr`、`BinaryExpr`（Add..Shr 全部 NodeKind）、`UnaryExpr`（Negate/Not/BitNot）、`AssignExpr`、`CallExpr`、`SyscallExpr`、`MemberCallExpr`、`StructLiteralExpr` |

### 轴 2：符号角色 A（语义）

- `DEF` 定义点（声明位置）
- `NAME_REF` 名称引用（读 / 写）
- `TYPE_REF` 类型引用（`TypeName` 字符串）
- `CALL_REF` 调用引用（`CallExpr.FunctionName` / `MemberCallExpr.MemberName` / `SyscallExpr.SyscallName`）
- `MEMBER_REF` 成员访问（`FieldAccessExpr.FieldName` / `StructLiteralExpr.Fields[].FieldName`）
- `ENUM_VALUE_REF` 枚举值访问（`FieldAccessExpr` 形式的 `Enum.MEMBER`）
- `ALIAS_USE` 模块别名使用（`ImportDecl.Alias` + `FieldAccessExpr.Target` 前缀）

### 轴 3：采集通道 B

- `DECL_REGION` 声明区（顶层列表本身）
- `FUNC_BODY` 函数体（`FuncDecl.Body`）
- `MVAR_INIT` 模块变量初始化器（`ModuleVariables[].Initializer`）
- `PARAM_DEFAULT` 参数默认值（`ParamDecl.DefaultValue`）
- `STMT_SUBEXPR` 语句内子表达式（IfStmt.Condition / Return.Value / Wait*.*/Using.Arguments / …）
- `EXPR_SUBEXPR` 表达式子树（Binary/Unary/Call.Arguments/StructLiteral.Fields[].Value 等）

### 轴 4：LSP 功能 C

- `documentSymbol` / `hover` / `definition` / `references` / `completion` / `signatureHelp` / `rename` / `prepareRename` / `semanticTokens/full` / `workspace/willRenameFiles`

对照 `LspServer` 支持集合。新增请求方法 → 此轴增列 → 所有行重评。

---

## 二、语法结构层：节点族 × 子位置

所有非 N/A 子位置以 *字段* 形式列出。`*NameLine/Column`、`OriginFile`、`DocComment` 等元数据字段不列入（它们不承载结构形态）。

### 2.1 DECL 族

| AST 类 | 子位置 | 备注 |
|--------|--------|------|
| `ImportDecl` | `ModulePath`、`Alias` | Alias 决定 namespace vs mixin |
| `StructDecl` | `Name`、`Fields[]`、`IsPrivate`、`IsOverride`、`AliasTarget` | |
| `StructField` | `Name`、`TypeName` | 字段声明 |
| `EnumDecl` | `Name`、`Members[]`、`IsPrivate`、`IsOverride`、`AliasTarget` | |
| `EnumMember` | `Name`、`ValueExpr` | ValueExpr 可为 null（自增） |
| `FuncDecl` | `Name`、`Parameters[]`、`ReturnType`、`Body`、`IsExternal`、`IsInline`、`IsOverride`、`IsPrivate`、`IsExported`、`AliasTarget` | `Body==null` 当 `IsExternal` |
| `ParamDecl` | `Name`、`TypeName`、`DefaultValue` | `DefaultValue` 为 Expr（FF3） |
| `VarDeclStmt` | `Name`、`TypeName`、`Initializer`、`IsConst`、`IsExported`、`IsPrivate`、`IsOverride`、`AliasTarget` | 同时出现在模块级与函数内 |

### 2.2 STMT 族

| AST 类 | 子位置 |
|--------|--------|
| `BlockStmt` | `Statements[]` |
| `IfStmt` | `Condition`、`ThenBranch`、`ElseBranch` |
| `WhileStmt` | `Condition`、`Body` |
| `ForStmt` | `Initializer`、`Condition`、`Increment`、`Body` |
| `ReturnStmt` | `Value`（可 null） |
| `WaitStmt` | `FrameCount` |
| `WaitForStmt` | `TargetInstanceId` |
| `YieldStmt` | — |
| `DeferStmt` | `Body` |
| `UsingStmt` | `SyscallName`、`Arguments[]`、`Body` |
| `ExprStmt` | `Expression` |

### 2.3 EXPR 族

| AST 类 | 子位置 |
|--------|--------|
| `NumberLiteralExpr` / `IntLiteralExpr` / `BoolLiteralExpr` / `StringLiteralExpr` / `StringIdLiteralExpr` | `Value`（纯字面量，无符号角色） |
| `IdentifierExpr` | `Name` |
| `FieldAccessExpr` | `Target`、`FieldName` |
| `BinaryExpr` | `Left`、`Right`（op 由 `NodeKind` 决定） |
| `UnaryExpr` | `Operand` |
| `AssignExpr` | `Target`、`Value` |
| `CallExpr` | `FunctionName`、`Arguments[]` |
| `MemberCallExpr` | `TargetName`、`MemberName`、`Arguments[]` |
| `SyscallExpr` | `SyscallName`、`Arguments[]`、`SyscallSlot` |
| `StructLiteralExpr` | `TypeName`、`Fields[].FieldName`、`Fields[].Value` |

---

## 三、语义覆盖层：节点 × 角色（A）× 功能（C）

仅列出承担符号角色的节点。纯字面量、纯结构语句（Block/If/While/For/Return/Yield/Defer）不承担角色。

图例：✅ = 有测试覆盖（以当前 675 LSP 测试为准，粗粒度）　⚠️ = 部分覆盖　❌ = **缺口**　— = N/A（不承担该角色）

| AST 节点 / 子位置 | DEF | NAME_REF | TYPE_REF | CALL_REF | MEMBER_REF | ENUM_VALUE_REF | ALIAS_USE |
|-------------------|-----|---------|---------|---------|-----------|---------------|-----------|
| `FuncDecl.Name` | ✅ definition/hover/rename | ✅ references | — | — | — | — | — |
| `FuncDecl.Parameters[]` → `ParamDecl.Name` | ✅ (DX13) | ✅ references (DX13) | — | — | — | — | — |
| `ParamDecl.TypeName` | — | — | ✅ (DX7/DX9) | — | — | — | ⚠️ alias prefix |
| `ParamDecl.DefaultValue` | — | ⚠️ as container | — | **❌ call-ref 不采集 (DB 后端)** | ⚠️ | ⚠️ | ⚠️ |
| `StructDecl.Name` | ✅ | ✅ | ✅ type-ref | — | — | — | — |
| `StructField.Name` | ✅ | ✅ references | — | — | ✅ via `FieldAccessExpr` / `StructLiteral` | — | — |
| `StructField.TypeName` | — | — | ✅ (DX7) | — | — | — | ⚠️ |
| `EnumDecl.Name` | ✅ | ✅ | ✅ | — | — | — | — |
| `EnumMember.Name` | ✅ | — | — | — | — | ✅ references (E003) | — |
| `EnumMember.ValueExpr` | — | ⚠️ | — | ⚠️ | — | ⚠️ | — |
| `VarDeclStmt.Name` (block) | ✅ scope-isolated (DX16) | ✅ scoped (DX16) | — | — | — | — | — |
| `VarDeclStmt.Name` (module) | ✅ (E004) | ✅ (E004) | — | — | — | — | — |
| `VarDeclStmt.TypeName` | — | — | ✅ (DX7) | — | — | — | ⚠️ |
| `VarDeclStmt.Initializer` (module) | — | ✅ (E004 `EmitIdentifierReferenceFacts`) | ✅ | **❌ call-ref 不采集 (DB 后端)** | ⚠️ | ⚠️ | ⚠️ |
| `VarDeclStmt.Initializer` (block) | — | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| `ImportDecl.ModulePath` | ✅ include definition (DX5) | ✅ willRename (DX6) | — | — | — | — | — |
| `ImportDecl.Alias` | ✅ | — | — | — | — | — | ✅ as prefix |
| `IdentifierExpr.Name` | — | ✅ | ⚠️ (parser disambiguation) | — | — | — | — |
| `FieldAccessExpr.FieldName` | — | — | — | — | ✅ (DX7) | ✅ (E003) | — |
| `FieldAccessExpr.Target` + MemberName | — | — | — | — | ✅ | ✅ | ✅ (Lang-17) |
| `CallExpr.FunctionName` | — | — | — | ✅ (FUNC_BODY) / ❌ (MVAR_INIT/PARAM_DEFAULT DB 后端) | — | — | — |
| `MemberCallExpr.TargetName` + `MemberName` | — | — | — | ✅ (Lang-8 XCALL) | ✅ (as member) | — | ✅ |
| `SyscallExpr.SyscallName` | ✅ (DX8 external func) | — | — | ✅ | — | — | — |
| `SyscallExpr.Arguments[].CallExpr` | — | — | — | **❌ DB 后端递归器缺分支** | — | — | — |
| `StructLiteralExpr.TypeName` | — | — | ✅ (DX14) | — | — | — | — |
| `StructLiteralExpr.Fields[].FieldName` | — | — | — | — | ✅ | — | — |
| `UsingStmt.SyscallName` | — | — | — | ✅ | — | — | — |
| `WaitForStmt.TargetInstanceId` 内 `IdentifierExpr` | — | ✅ (DX17 AstWalker 修复) | — | ✅ if call | — | — | — |

---

## 四、采集通道（B）与 LSP 功能（C）的覆盖表

针对最易遗漏的 CALL_REF / NAME_REF / TYPE_REF 三种角色，对 **LspServer** 路径与 **Database 后端** 路径分别表态。

### 4.1 LspServer（`UnifiedRefsWalker`，DX18 后）

| 通道 B | CALL_REF | NAME_REF | TYPE_REF |
|--------|----------|----------|----------|
| `DECL_REGION`（Struct/Enum/Func/Var 顶层列表） | ✅ | ✅ | ✅ |
| `FUNC_BODY` | ✅ | ✅ | ✅ |
| `MVAR_INIT` | ✅ | ✅ | ✅ |
| `PARAM_DEFAULT` | ✅ (via UnifiedRefsWalker `WalkExpr`) | ✅ | ✅ |
| `STMT_SUBEXPR` (`IfStmt.Condition` / `WaitForStmt.TargetInstanceId` / `UsingStmt.Arguments` / …) | ✅ | ✅ | ✅ |
| `EXPR_SUBEXPR` (Call args / StructLiteral.Value / Binary/Unary 操作数) | ✅ | ✅ | ✅ |

**LspServer 路径目前无结构性 B 轴遗漏。**（`UnifiedRefsWalker` 于 DX18 统一到单一遍历器。）

### 4.2 Database 后端（`InMemoryDatabaseExecutionOrchestrator`）

| 通道 B | CALL_REF | NAME_REF | TYPE_REF |
|--------|----------|----------|----------|
| `DECL_REGION` | ✅ | ✅ | ✅ |
| `FUNC_BODY` | ⚠️ 缺 `SyscallExpr` 分支 | ✅ | ✅ |
| `MVAR_INIT` | ❌ 不遍历 | ✅ (非对称，仅 NAME_REF 扫了) | ⚠️ |
| `PARAM_DEFAULT` | ❌ 不遍历 | ❌ | ⚠️ |
| `STMT_SUBEXPR` | ⚠️ (缺 SyscallExpr 分支) | ✅ | ✅ |
| `EXPR_SUBEXPR` | ⚠️ | ✅ | ✅ |

**已确认的结构性缺口（参见 §五）**。

---

## 五、已知缺口（Missing 基线）

这些缺口通过本矩阵首次被"机械可定位"，是验证矩阵价值的基线样本。

| 编号 | 节点 / 子位置 | 通道 | 角色 | 功能影响 | 代码位置 |
|------|--------------|------|------|---------|---------|
| **GAP-01** | `SyscallExpr.Arguments[].CallExpr.FunctionName` | `EXPR_SUBEXPR` (via SyscallExpr 参数) | CALL_REF | Find References 漏报（syscall 实参中的函数调用未生成 SymbolReference） | `Assets/Scripts/VM/Debug/Lsp/Database/Operations/InMemoryDatabaseExecutionOrchestrator.cs:4405-4554`（`CollectCallReferences*` 无 `SyscallExpr` 分支） |
| **GAP-02** | `ModuleVariables[].Initializer` 内任意 `CallExpr` | `MVAR_INIT` | CALL_REF | Find References 漏报（模块级 const/var 初始化器中的函数调用） | 同上 `:1502-1532`（遍历只从 `function.Body` 出发） |
| **GAP-03** | `ParamDecl.DefaultValue` 内任意 `CallExpr` | `PARAM_DEFAULT` | CALL_REF | Find References 漏报（FF3 可选参数默认值中的函数调用） | 同上 `:1502-1532`、`:4405-4554` |
| **GAP-04** | Database 后端的 NAME_REF 与 CALL_REF 不对称 | `MVAR_INIT` | NAME_REF vs CALL_REF | `EmitIdentifierReferenceFacts` 扫了 `mv.Initializer`（NAME_REF ✅），但 `CollectCallReferences` 未扫 → 方法间不对称，功能级易错 | 同上 `:3450` (非对称入口) |

**修复不在 CM 计划范围**。CM 的职责是**暴露**缺口；修复作为独立需求提出（GAP-01~04 可合并为"Database backend call-ref 遍历对齐"）。

---

## 六、新增语法/功能时的"增量契约"

本矩阵是**增量维护**的。以下事件必须触发对应更新：

| 事件 | Map 文件动作 | 生成器/CI 动作（CM1~CM4 激活后） |
|------|------------|------------------------------|
| 新增 `NodeKind` | §二相应族表增行；§三评估承担的符号角色 | 生成器自动增行；与 Map diff 为空；否则 CI 失败 |
| 新增 AST 字段（含 `Expr` / 子语句类型） | §二子位置列增项；§三评估通道与角色 | 生成器自动增项；无对应测试 → 增量缺口报告 |
| 新增 `SymbolKindTag` | §一轴 2 增值；§三表头扩列 | 所有行重新评估；未评估行产生 Missing |
| 新增 LSP 方法（如 `codeAction`） | §一轴 4 增值；为关键节点族补行评估 | C 轴展开 → 所有 (kind, role) 行扩列 |
| 发现新缺口 | §五补行 | 新增缺口自动标红并链接到代码位置 |
| 修复已知缺口 | §五行状态改为 ✅ 并链接修复计划编号 | 缺口报告自动清零 |

---

## 七、与测试体系的绑定（预告，CM3）

当前 675 LSP 测试中，大多以功能命名（如 `TestRenameStruct`、`TestReferencesParameter`）。CM3 激活后建议的命名规则：

```
[<SymbolKindTag>_<ContainerB>_<FeatureC>_<Variant>]
```

例如：

- `[Function_MVarInit_References_CrossFile]` — 覆盖 GAP-02 的回归用例
- `[Function_ParamDefault_References_Basic]` — 覆盖 GAP-03
- `[Function_SyscallArg_References_Basic]` — 覆盖 GAP-01

矩阵工具据此反查：任一单元若无匹配标签测试 → 标 Missing。

---

## 八、引用链

- 讨论：[D_CoverageMatrix.md](../../Discussion/D_CoverageMatrix.md)（D21）
- 计划：[Step_CM_CoverageMatrix.md](Step_CM_CoverageMatrix.md)
- 根因背景：[D_LspStructuralAudit.md](../../Discussion/D_LspStructuralAudit.md)（D19）+ [D_LspUsabilityAudit.md](../../Discussion/D_LspUsabilityAudit.md)（D17）
- 相关 LSP 演进：DX17（统一符号解析）/ DX18（统一引用收集）/ DX19（候选仲裁）
