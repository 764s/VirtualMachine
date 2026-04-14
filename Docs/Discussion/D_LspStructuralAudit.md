# LSP 结构性审查：概念源唯一性与执行路径收敛

> **状态**：💬 讨论中
> **日期**：2026-04-14
> **来源**：D17（D_LspUsabilityAudit）KL-01~05 修复过程中暴露的系统性模式

---

## 一、问题陈述

D17 易用性审查发现 5 个已知限制（KL-01~05），DX13~DX16 逐一修复。但修复过程本身暴露了更深层的结构性问题：

**如果概念源和执行路径都唯一，功能一旦通过就不应频繁出错。** 但 KL-01~05 **全部** 是同一类遗漏——SymbolKindTag × 容器类型 × LSP 功能的笛卡尔积中的某个格子未覆盖。这不是个案，是架构使然。

---

## 二、核心问题：同一分派逻辑的 N 路复制

### 2.1 三大核心函数各自维护独立的 SymbolKindTag 分支

| 函数 | 职责 | 分支数 |
|------|------|--------|
| `FindDefinitionLocation` | 查找符号定义位置 | 8 |
| `CollectReferences` | 同文件引用收集 | 4 |
| `CollectReferencesWithOrigin` | 跨文件引用收集 | 10+ |

三者对 SymbolKindTag 做**相同的分派**（Function → Struct → Enum → StructField → EnumMember → Parameter → Variable），但各自独立实现。

**后果**：每次新增符号类型或容器类型，必须在 3 个函数中分别添加对应分支。遗漏任何一处 → 某个 LSP 功能对该符号类型失效。

### 2.2 CollectReferencesWithOrigin 内部的三段式复制

每个 SymbolKindTag 分支都独立实现同一个模式：

```
1. foreach (declaration in ast.TopLevelList)     // 声明位置
2. foreach (func in ast.Functions)                // 函数体中的引用
3. foreach (mv in ast.ModuleVariables)            // 模块变量中的引用
```

此三段式在 Function、Struct、Enum、EnumMember、StructField（×2）、Parameter、Variable 分支中各出现一次，共 **8 份副本**。

**后果**：如果新增一个顶层容器（如 global initializer block），需要在 8 个分支中各加一轮遍历。遗漏任何一处 = 某个符号类型的引用不完整。

### 2.3 实证：B001 / DX13~DX16 修复轨迹

| 修复 | 本质 | 修改点数 |
|------|------|---------|
| B001 | 为 ModuleVariable 容器补齐 8 个分支 | FindSymbolAtPosition + FindDefinitionLocation + CollectReferencesWithOrigin 各 1 处 |
| DX13 | 为 Parameter 符号类型补齐分支 | FindSymbolAtPosition + FindDefinitionLocation + CollectReferencesWithOrigin + HandleRename 各 1 处 |
| DX14 | 为 StructLiteral 表达式类型补齐引用收集 | CollectReferencesWithOrigin Struct 分支内加循环 |
| DX15 | 为 private 可见性补齐 completion 过滤 | HandleCompletion 中 4 处 guard |
| DX16 | 为作用域隔离补齐 Variable 引用 | FindSymbolWalker + ScopedIdentRefsWalker + CollectReferencesWithOrigin Variable 分支 |

**每次修复都是在笛卡尔积中补上遗漏的格子。** 测试覆盖率从 540 增长到 652（+21%），但新增的 112 个测试中大部分是在验证"交叉点是否被覆盖"——而非测试新功能。

---

## 三、R1 重构的评估

R1 引入 `AstWalker` 基类 + 17 个子类，**正确解决了底层遍历统一问题**：

✅ 消除了 Block/Stmt/Expr 手写遍历三件套（之前每个收集函数都有自己的遍历逻辑）  
✅ 新增 AST 节点类型只需在 AstWalker 中添加一处分派  
✅ `_abort` 机制统一了 early-out 语义

❌ **未解决上层分派问题**——17 个 Walker 子类中有 7 个做的是同一件事（收集特定类型的引用）的变体，被拆分仅因为 `CollectReferencesWithOrigin` 按 SymbolKindTag 分支各自调用不同 Walker 组合。

---

## 四、目标架构：概念源唯一、执行路径收敛

### 4.1 原则

- **符号解析一条路径**：`ResolveSymbol(ast, position) → ResolvedSymbol`，所有 LSP 功能共享
- **引用收集一条路径**：`CollectAllRefs(ast, symbol) → List<Location>`，一次遍历收集所有引用形式
- **新增符号类型**：只改 `ResolveSymbol` 和 `CollectAllRefs` 各一处
- **新增容器类型**：只改遍历入口一处

### 4.2 ResolvedSymbol 统一结构

```
struct ResolvedSymbol {
    SymbolKindTag Kind;
    string Name;
    string ParentName;       // struct/enum name for fields/members
    string ScopeFunc;        // containing function (null for top-level)
    int DeclLine, DeclCol;   // governing declaration position
    string OriginFile;       // cross-file origin
}
```

当前 `FindSymbolAtPosition` 返回 `SymbolAtPosition`（缺少 OriginFile），`FindDefinitionLocation` 返回 `(line, col, nameLen, originFile)` 元组。两者应合并为 `ResolvedSymbol`。

### 4.3 统一引用收集器

当前 7 个引用 Walker（`CallRefsWalker`, `IdentRefsWalker`, `TypeRefsWalker`, `StructLiteralTypeRefsWalker`, `EnumIdentRefsWalker`, `FieldAccessRefsWalker`, `EnumMemberAccessRefsWalker`）合并为一个：

```
class UnifiedRefsWalker : AstWalker {
    ResolvedSymbol _target;
    
    VisitExpr(expr):
        if expr is IdentifierExpr && matches → add
        if expr is CallExpr && matches → add
        if expr is FieldAccessExpr && matches → add
        if expr is StructLiteralExpr && matches → add
    
    VisitVarDecl(vd):
        if vd.TypeName matches → add
        if vd.Name matches → add
}
```

`CollectReferencesWithOrigin` 的 8 个 SymbolKindTag 分支简化为：

```
// 声明位置
AddDeclarationLocation(ast, symbol);

// 所有容器中的引用（统一遍历入口）
foreach (var func in ast.Functions)
    new UnifiedRefsWalker(symbol, resolveUri(func)).WalkBlock(func.Body);
foreach (var mv in ast.ModuleVariables)
    new UnifiedRefsWalker(symbol, resolveUri(mv)).WalkExpr(mv.Initializer);
```

新增容器 → 加一行。新增引用形式 → 在 UnifiedRefsWalker 加一个 if。无笛卡尔积。

### 4.4 HandleDefinition / HandleReferences / HandleRename 统一入口

```
var symbol = ResolveSymbol(mergedAst, line, col);  // 唯一符号解析
if (symbol == null) return null;

// Definition: 直接从 symbol.DeclLine/DeclCol 构建
// References: UnifiedRefsWalker 一次遍历
// Rename: References 结果 + 名称替换
```

---

## 五、实施路径

### P1：统一符号解析（低风险，高收益）

- 合并 `SymbolAtPosition` 和 `FindDefinitionLocation` 返回值为 `ResolvedSymbol`
- `FindSymbolAtPosition` 直接返回包含 OriginFile 的完整符号
- `HandleDefinition`、`HandleReferences`、`HandleRename`、`HandleHover` 共享 `ResolveSymbol` 调用
- 消除 `ResolveSymbolDualAst` 中的二次查找

### P2：统一引用收集（中风险，高收益）

- 7 个引用 Walker → 1 个 `UnifiedRefsWalker`
- `CollectReferencesWithOrigin` 的 8 个 SymbolKindTag 分支 → 声明位置 + 统一遍历
- 现有 652 个 LSP 测试提供回归保障

### P3：消除 CollectReferences / CollectReferencesWithOrigin 二重性

- `CollectReferences`（无 Origin）仅用于极少数场景
- 统一到 `CollectReferencesWithOrigin`，无 resolver 时 fallback 到 identity

---

## 六、笛卡尔积消除验证

重构后的覆盖矩阵变化：

| 维度 | 重构前修改点数 | 重构后修改点数 |
|------|-------------|-------------|
| 新增符号类型 | FindSymbol + FindDef + CollectRefs + CollectRefsOrigin + HandleCompletion = **5+** | ResolveSymbol + UnifiedRefsWalker = **2** |
| 新增容器类型 | CollectRefsOrigin 内 8 个分支各 1 处 = **8** | 遍历入口 1 处 = **1** |
| 新增引用形式 | 对应 Walker 1 个 + CollectRefsOrigin 调用处 1 个 = **2** | UnifiedRefsWalker 内 1 个 if = **1** |

---

## 七、与现有架构文档的关系

| 文档 | 关系 |
|------|------|
| D16 (D_LspArchitecture) | DX10/DX11 已实施。本文补充 LSP 内部符号引擎的结构性改进 |
| D17 (D_LspUsabilityAudit) | KL-01~05 的**根因分析**。审查发现的 GAP 是笛卡尔积遗漏的表现 |
| R1 (AstWalker) | R1 是底层遍历统一。本文是上层分派统一，两者互补 |
