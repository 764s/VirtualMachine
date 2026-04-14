# DX18: 统一引用收集 (Unified Reference Collection)

> 前置：DX17 ✅ 统一符号解析  
> 讨论来源：[D_LspStructuralAudit.md](../Discussion/D_LspStructuralAudit.md) §五 P2+P3

## 一、目标

消除 LSP 引用收集的笛卡尔积复制问题。将 7 个引用 Walker 合并为 1 个 `UnifiedRefsWalker`，将 `CollectReferencesWithOrigin` 从 8 个 SymbolKindTag 分支简化为「声明位置 + 统一遍历」两阶段。

## 二、变更

### 2.1 新增 UnifiedRefsWalker

- 接收 `SymbolKindTag kind, string name, string parentName, string uri, List<object> locations`
- `VisitExpr` 统一处理 8 种 Kind 的所有表达式级匹配：
  - **Function**: `CallExpr.FunctionName`
  - **Variable/Parameter**: `IdentifierExpr.Name`（含 scope-isolated 模式）
  - **Struct**: `StructLiteralExpr.TypeName`
  - **Enum**: `FieldAccessExpr.Target(Ident)` — EnumName.MEMBER 中的 EnumName 部分
  - **StructField**: `FieldAccessExpr.FieldName` + `StructLiteralExpr.Fields[].FieldName`
  - **EnumMember**: `FieldAccessExpr.FieldName` + 验证 Target 为 parentName
- `VisitVarDecl` 处理：
  - 类型注解（Struct/Enum: `VarDeclStmt.TypeName` via `GetBaseTypeName`）
  - 变量名声明（Variable/Parameter: `VarDeclStmt.Name`，仅非 scope-isolated）
- DX16 scope-isolation 逻辑通过 `VisitStmt` 集成（仅 `_scopeIsolated == true` 时激活）

### 2.2 重写 CollectReferencesWithOrigin

分为两阶段：

**Phase 1**: `CollectDeclarationLocations` — 收集声明位置（函数名、结构体名、枚举名、字段名、枚举成员名、参数名）

**Phase 2**: 遍历容器收集使用引用：
- Parameter → 仅遍历声明函数 body
- Variable (scope-isolated) → 仅遍历声明函数 body（带 scope 跟踪）
- StructField (parentName == null) → 遍历所有函数 body + 模块变量 initializer
- 通用情况 → 遍历所有函数 body + 模块变量（含 type annotation + name decl + initializer）

### 2.3 删除代码

- 7 个旧 Walker 类：`CallRefsWalker`、`IdentRefsWalker`、`ScopedIdentRefsWalker`、`TypeRefsWalker`、`StructLiteralTypeRefsWalker`、`EnumIdentRefsWalker`、`FieldAccessRefsWalker`、`EnumMemberAccessRefsWalker`
- 6 个旧 helper 方法：`CollectCallRefsInBlock`、`CollectIdentRefsInBlock`、`CollectScopedIdentRefsInBlock`、`CollectTypeRefsInBlock`、`CollectFieldAccessRefsInBlock`、`CollectEnumMemberAccessRefsInBlock`
- 死代码 `CollectReferences`（无 Origin 版本，已无调用者）

## 三、数据

| 指标 | 变更前 | 变更后 |
|------|--------|--------|
| LspServer.cs 行数 | 4799 | 4557 (−242) |
| 引用 Walker 类数量 | 8 | 1 |
| CollectReferencesWithOrigin 分支数 | 8 | 4（Parameter/ScopeVar/StructFieldNull/General） |
| 新增符号类型修改点 | 5+ | 2（CollectDeclarationLocations + UnifiedRefsWalker.VisitExpr） |
| 新增引用形式修改点 | 2 | 1（UnifiedRefsWalker 内加 if） |
| 新增容器类型修改点 | 8 | 1（CollectReferencesWithOrigin 加一行） |

## 四、测试

656 个现有 LSP 测试提供完整回归保障。2282 测试全部通过。无需新增测试（纯重构，行为不变）。

## 五、风险

- ~~中风险：大范围重构~~ → ✅ 656 LSP 测试全部通过，风险已消除
- 性能：单次遍历中多几个 `is` 分支判断（纳秒级），但消除了 Struct/Enum 分支的重复遍历（旧代码 TypeRefsWalker + StructLiteralTypeRefsWalker / EnumIdentRefsWalker 各走一遍），净效果为正或中性
