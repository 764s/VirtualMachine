# Lang-13: 语法糖枚举 (Enum)

> **状态**：⏳ 进行中
> **前置条件**：CFG1 ✅ 完成
> **定位**：纯编译期语法糖特性，零运行时改动，零新 OpCode

## 一、需求背景

当前 FFScript 中表示一组命名整数常量的方式是使用多个独立的 `const` 声明：

```ffs
const DamageType_NONE: int = 0
const DamageType_PHYSICAL: int = 1
const DamageType_MAGICAL: int = 2
const DamageType_TRUE: int = 3
```

**痛点**：
- 没有逻辑分组，所有常量散落在模块顶层
- 命名冗余（每个成员需手动加前缀）
- 值需手动递增，容易出错
- 无法通过 `EnumName.` 点号语法获得编辑器自动补全

**目标**：提供 `enum` 语法糖，让以上场景变为：

```ffs
enum DamageType {
    NONE,
    PHYSICAL,
    MAGICAL,
    TRUE
}
```

编译器自动展开为 `DamageType.NONE = 0, DamageType.PHYSICAL = 1, ...`，全部为编译期整数常量。

## 二、设计方案

### 2.1 语法

```
enum <Name> {
    <Member1>,
    <Member2> = <constExpr>,
    <Member3>,
    ...
}
```

- 成员从 0 开始自动递增
- 显式赋值后，后续成员从该值 +1 继续递增
- 值必须是编译期可折叠的整数常量表达式
- 尾逗号可选
- 空枚举 `enum E {}` 合法（零成员）

### 2.2 编译模型（语法糖）

`enum DamageType { NONE, PHYSICAL = 5, MAGICAL }` 等价展开为：

```
const DamageType.NONE: int = 0
const DamageType.PHYSICAL: int = 5
const DamageType.MAGICAL: int = 6
```

- 所有枚举成员注入 `_moduleConstValues["EnumName.Member"]`
- 枚举名本身注册到 `_enumNames` 集合（用于 FieldAccessExpr 拦截）
- 运行时使用时，`DamageType.PHYSICAL` 编译为 `LOAD_CONST 5`（直接常量折叠）
- 零新 OpCode，零运行时改动

### 2.3 作用域与约束

- 枚举仅支持模块级声明（与 struct/func/var/const 同级）
- 枚举名不可与 struct/func/var/const 同名（编译器报重复定义）
- 枚举成员名以 `EnumName.Member` 形式存储，不污染模块顶层命名空间
- 枚举成员值为 int 类型（Number 存储，整数语义）
- `@export` 目前不支持枚举（后续按需扩展）

## 三、实施步骤 checklist

### Phase A: 语言基础设施

- [ ] **A1. Lexer: `enum` 关键字**
  - `TokenType` 新增 `Enum`
  - `Lexer.Keywords` 表新增 `{ "enum", TokenType.Enum }`

- [ ] **A2. AST: `EnumDecl` 节点**
  - 新增 `EnumMember` 类：`Name: string, ValueExpr: Expr (nullable), Line: int, Column: int`
  - 新增 `EnumDecl` 类：`Name: string, Members: List<EnumMember>, Line: int, Column: int, DocComment: string`
  - `ModuleNode` 新增 `Enums: List<EnumDecl>` 字段（构造函数初始化空列表）
  - `NodeKind` 新增 `EnumDecl`

- [ ] **A3. Parser: 解析 enum 声明**
  - 顶层循环新增 `else if (Check(TokenType.Enum))` 分支 → `ParseEnumDecl()`
  - `ParseEnumDecl()`: consume `enum` → expect Identifier → expect `{` → 循环解析成员 → expect `}`
  - 成员解析：Identifier [= constExpr], 逗号分隔，尾逗号可选
  - 错误报告：缺少名字、缺少 `{`、缺少 `}`

### Phase B: 编译器集成

- [ ] **B1. 编译器: `ProcessEnums` 方法**
  - 新增 `_enumNames: HashSet<string>` — 已注册枚举名集合
  - 新增 `_enumMemberMap: Dictionary<string, Dictionary<string, Number>>` — EnumName → { MemberName → Value }
  - 在 `Compile()` 方法的 `ProcessModuleVariables(module)` 调用前，插入 `ProcessEnums(module)`
  - `ProcessEnums` 遍历 `module.Enums`，对每个枚举：
    - 检查枚举名不与已有 struct/func/var/const/enum 重复
    - 遍历成员，维护 `nextValue` 计数器（初始 0）
    - 有显式值 → `TryFoldConstant(valueExpr)` 得到 int → 设为当前值
    - 无显式值 → 使用 `nextValue`
    - 注入 `_moduleConstValues["EnumName.Member"] = value`
    - `nextValue = value + 1`
    - 注册到 `_enumNames` 和 `_enumMemberMap`

- [ ] **B2. 编译器: FieldAccessExpr 枚举拦截**
  - 在 `CompileFieldAccess` 或 `CompileExpr(FieldAccessExpr)` 中：
    - 当 `target` 为 `IdentifierExpr` 且 `target.Name ∈ _enumNames` 时
    - 查 `_moduleConstValues["EnumName.FieldName"]`
    - 找到 → 编译为 `LOAD_CONST` (dest, constIndex)
    - 未找到 → 错误 `"Enum 'X' has no member 'Y'"`
  - 确保优先级：枚举查找在 struct 字段查找之前（或并行无冲突，因为枚举名不会是变量名）

### Phase C: LSP 编辑器支持

- [ ] **C1. TextMate 染色**
  - `vscode-ffvm-debug/syntaxes/ffvm.tmLanguage.json` 的 `keyword.declaration` 正则中加入 `enum`

- [ ] **C2. LSP documentSymbol**
  - `HandleDocumentSymbol` 中遍历 `ast.Enums`，生成 SymbolKind=10 (Enum) 条目

- [ ] **C3. LSP 补全 — EnumName. 点号补全**
  - `HandleCompletion` 的 `isDotContext` 分支中：
    - 在 struct 字段补全之前，检查 `dotPrefix` 是否为已知枚举名
    - 匹配 → 列出该枚举全部成员（CompletionItemKind = 20 EnumMember）

- [ ] **C4. LSP 补全 — 通用补全列出枚举名**
  - `HandleCompletion` 的通用补全路径中：
    - 遍历 `ast.Enums` → 添加枚举名补全项（CompletionItemKind = 13 Enum）

- [ ] **C5. LSP hover**
  - `FindHoverText` 中新增枚举名 hover → 显示完整枚举定义
  - `FindHoverInExpr` 中对 `FieldAccessExpr` 的 `target ∈ enumNames` → 显示 `EnumName.Member = value`

- [ ] **C6. LSP definition + references**
  - `FindSymbolAtPosition` 新增 SymbolKindTag.Enum
  - `FindDefinitionLocation` 枚举名 → 返回 enum 声明位置
  - `CollectReferences` 枚举成员引用收集

### Phase D: 测试

- [ ] **D1. 编译器测试 EN01-EN12**

| ID | 描述 | 关键断言 |
|----|------|---------|
| EN01 | 基础枚举声明 + 使用 | `enum Color { R, G, B }` → `Color.R=0, Color.G=1, Color.B=2`，函数中使用返回正确值 |
| EN02 | 显式赋值 | `enum E { A=10, B, C=20, D }` → `A=10, B=11, C=20, D=21` |
| EN03 | 常量表达式赋值 | `enum E { A=1+2, B }` → `A=3, B=4` |
| EN04 | 空枚举 | `enum Empty {}` 合法，编译成功 |
| EN05 | 重复成员名检测 | `enum E { A, A }` → 编译错误 |
| EN06 | 枚举名与 struct 重名检测 | `struct S {} enum S {}` → 编译错误 |
| EN07 | 在 if 分支中使用枚举 | `if (x == Color.R) { ... }` 正确比较 |
| EN08 | 跨函数使用枚举 | 多个函数引用同一枚举成员 |
| EN09 | include 跨文件枚举 | include 文件声明枚举，主文件使用 |
| EN10 | 未知枚举成员检测 | `Color.UNKNOWN` → 编译错误 |
| EN11 | 枚举值参与常量折叠 | `const x: int = Color.R + 1` → 折叠为 1 |
| EN12 | 尾逗号 | `enum E { A, B, }` 合法 |

- [ ] **D2. LSP 测试**

| ID | 描述 |
|----|------|
| LSP-EN01 | documentSymbol 包含枚举 |
| LSP-EN02 | `EnumName.` 点号补全列出成员 |
| LSP-EN03 | 枚举名 hover 显示定义 |
| LSP-EN04 | 枚举成员 hover 显示值 |
| LSP-EN05 | 通用补全列出枚举名 |

- [ ] **D3. 回归验证**
  - 全部 1574+ 现有测试无回归
  - 纯编译期特性，无需 benchmark（零运行时改动）

## 四、影响范围

| 文件 | 改动 |
|------|------|
| `Compiler/Lexer.cs` | +1 TokenType, +1 Keywords entry |
| `AST/ASTNode.cs` | +EnumMember class, +EnumDecl class, +ModuleNode.Enums, +NodeKind.EnumDecl |
| `Compiler/Parser.cs` | +ParseEnumDecl(), +顶层 enum 分支 |
| `Compiler/BytecodeCompiler.cs` | +ProcessEnums(), +FieldAccessExpr 枚举拦截, +_enumNames/_enumMemberMap |
| `Debug/LspServer.cs` | +documentSymbol/completion/hover/definition/references enum 支持 |
| `vscode-ffvm-debug/syntaxes/ffvm.tmLanguage.json` | +`enum` 关键字 |
| `Tests/CompilerTests.cs` | +EN01-EN12 测试 |
| `Tests/LspTests.cs` | +LSP-EN01~LSP-EN05 测试 |

**零改动文件**：`VMWorld.cs`, `OpCode.cs`, `VMInstanceState.cs`, `Instruction.cs` — 运行时完全不受影响。
