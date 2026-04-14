# B001: 模块级符号导航缺陷

> **状态**：✅ 已修复
> **来源**：skill_walk_forward.ffs / skill_light_punch.ffs 实际使用场景
> **影响**：定义提供器（Go to Definition）、引用提供器（Find References）在模块级声明上下文中功能异常

---

## 缺陷清单

### skill_walk_forward.ffs

| # | 行 | 符号 | 症状 | 根因 |
|---|-----|------|------|------|
| 1 | `override @export const tags: int = TagBit.WALK` | `TagBit`、`WALK` | 定义/引用提供器无响应 | FindSymbolAtPosition 不扫描 ModuleVariables 初始化器 |
| 2 | `@export const requireInputHeld: int = InputBit.ANY_DIR_H` | `ANY_DIR_H` | 符号不可导航，无报错 | 同上；若成员不存在，TryFoldConstant 仅给出笼统错误 |
| 3 | `var leftHeld: int = IsInputHeld(InputButton.LEFT)` | `LEFT` | 引用提供器遗漏模块级用法 | CollectReferencesWithOrigin 不扫描 ModuleVariables 初始化器 |

### skill_light_punch.ffs

| # | 行 | 符号 | 症状 | 根因 |
|---|-----|------|------|------|
| 4 | `override @export const tags: int = TagBit.ATTACK` | `TagBit`、`ATTACK` | 同 #1 | FindSymbolAtPosition 不扫描 ModuleVariables 初始化器 |
| 5 | `const hitPhase: HitPhaseDef = HitPhaseDef {` | `HitPhaseDef` | 定义/引用提供器无响应 | FindSymbolAtPosition 不扫描 ModuleVariables 类型注解和初始化器 |
| 6 | `box: Box4 { ox: 0.3, ... }` | `Box4`、`ox` 系列 | 定义/引用提供器无响应 | FindSymbolAtPosition 不扫描嵌套 StructLiteral 内的符号 |
| 7 | `if f >= hitPhase.startFrame ...` | `hitPhase` | 定义提供器无响应 | FindDefinitionLocation 不回退到 ModuleVariables |
| 8 | `ApplyDamage(target, 1.0, DamageType.NORMAL_UPPER)` | `ApplyDamage` | 引用提供器遗漏模块级用法 | CollectReferencesWithOrigin 不扫描 ModuleVariables 初始化器 |

---

## 根因分析

### RC1: FindSymbolAtPosition 缺少 ModuleVariables 扫描

`FindSymbolAtPosition()` (LspServer.cs:2530) 扫描范围：
- ✅ Imports
- ✅ Functions（名称）
- ✅ Structs（名称 + 字段）
- ✅ Enums（名称 + 成员）
- ✅ Function bodies（通过 FindSymbolWalker）
- ❌ **ModuleVariables**（名称、类型注解、初始化器）

**后果**：所有出现在模块级声明中的符号（枚举成员、结构体类型、结构体字面量字段等）对 LSP 不可见。

### RC2: FindDefinitionLocation 不处理模块级变量

`FindDefinitionLocation()` (LspServer.cs:2715) 对 `Variable` 类型：
- ✅ 搜索 function body 内的 VarDeclStmt
- ❌ **不搜索 ast.ModuleVariables**

**后果**：在函数体内引用模块级变量（如 `hitPhase.startFrame`），`hitPhase` 被识别为 Variable（scopeFunc="main"），但 FindDefinitionLocation 在 main() 内找不到声明。

### RC3: CollectReferencesWithOrigin 不扫描模块级初始化器

`CollectReferencesWithOrigin()` (LspServer.cs:2919) 对每种符号类型：
- ✅ 声明位置
- ✅ Function body 内的使用
- ❌ **ModuleVariables 初始化器内的使用**
- ❌ **ModuleVariables 类型注解中的使用**

**后果**：Find References 遗漏模块级声明中的所有引用。

### RC4: 模块级 const 枚举成员不存在时错误信息不精确

`ProcessModuleVariables()` (BytecodeCompiler.cs:1119) 使用 `TryFoldConstant()` 处理初始化器。当枚举成员不存在时，`TryFoldConstant` 返回 false，给出笼统错误："Module 'const' initializer must be a compile-time constant"，而非具体的 "Enum 'X' has no member 'Y'"。

---

## 修复方案

### Fix 1: FindSymbolAtPosition — 添加 ModuleVariables 扫描

在 `FindSymbolAtPosition` 的 enum 扫描之后、function body 扫描之前，添加 ModuleVariables 扫描：
- 遍历 `ast.ModuleVariables`
- 检查变量名（NameLine/NameColumn）
- 检查类型注解（TypeNameLine/TypeNameColumn）
- 遍历初始化器表达式（使用 FindSymbolWalker，func 传 null）

### Fix 2: FindDefinitionLocation — 模块级变量回退

在 Variable 类型处理中：
- 搜索 function body 失败后，搜索 `ast.ModuleVariables`

### Fix 3: CollectReferencesWithOrigin — 扫描模块级上下文

对每种符号类型：
- Function: 增加模块级初始化器中的 CallExpr 扫描
- Struct/Enum: 增加模块级类型注解扫描
- EnumMember: 增加模块级初始化器中的 FieldAccessExpr 扫描
- StructField: 增加模块级初始化器中的 FieldAccessExpr/StructLiteral 扫描
- Variable: 增加模块级变量声明和初始化器中的 IdentifierExpr 扫描

### Fix 4: 精确枚举成员错误信息

在 `ProcessModuleVariables` 中，`TryFoldConstant` 失败后，额外检查初始化器是否为 `FieldAccessExpr` 且目标为已知枚举，给出精确错误信息。

---

## 测试覆盖

| 测试 ID | 场景 | 验证内容 |
|---------|------|---------|
| B001-01 | 模块级 const 初始化器 — EnumName.MEMBER | 定义/引用提供器 |
| B001-02 | 模块级 const 类型注解 — struct type | 定义/引用提供器 |
| B001-03 | 模块级 struct const 初始化器 — 嵌套 StructLiteral | 定义/引用提供器 |
| B001-04 | 函数体内引用模块级变量 | 定义提供器 |
| B001-05 | 模块级变量声明名称 | 定义/引用提供器 |
| B001-06 | 跨文件模块级引用 | 引用提供器包含模块级用法 |
| B001-07 | 不存在的枚举成员 — 精确错误 | 诊断信息含 "has no member" |
