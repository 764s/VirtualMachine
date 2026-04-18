# DX14: Rename 完整性补全

## 目标

修复 LSP 审查（D17 D_LspUsabilityAudit）发现的已知限制 KL-03：
- **KL-03**：`textDocument/rename` 对 struct 执行重命名时，struct 字面量表达式中的类型名（如 `Vec2 { x: 1 }` 中的 `Vec2`）未被编辑覆盖

## 前置条件

| 前置 | 状态 |
|------|------|
| DX13 参数 LSP 完整支持 | ✅ 已完成 |
| DX12 LSP 可用性审查（KL-03 发现） | ✅ 已完成 |

## 变更摘要

### LspServer.cs

`CollectReferencesWithOrigin` 的 `Struct` 分支原本仅通过 `TypeRefsWalker` 收集函数体内的 `VarDeclStmt.TypeName` 引用，而 `StructLiteralTypeRefsWalker` 仅用于模块级变量初始化器。

修复：在遍历函数体时追加 `StructLiteralTypeRefsWalker` 走查，使 struct 字面量中的类型名（如 `Vec2 { ... }` 中的 `Vec2`）也被收集到引用列表中。

### 测试

| 测试 ID | 场景 | 断言数 |
|---------|------|--------|
| DX14-01 | 单文件 struct 重命名含字面量名 | 3 |
| DX14-02 | 从字面量位置发起重命名 | 3 |
| DX14-03 | 同函数多个 struct 字面量引用 | 1 |
| DX14-04 | 跨文件 struct 重命名含字面量名 | 4 |
| DX14-05 | 多字面量重命名编辑数验证 | 3 |

DX12-12 测试升级：`≥2` → `≥3` 编辑断言（struct 字面量名现已计入）。

## 完成条件

| # | 条件 | 状态 |
|---|------|------|
| ① | struct 字面量类型名计入 rename 编辑（KL-03 修复） | ✅ |
| ② | DX12-12 测试升级为 ≥3 编辑断言 | ✅ |
| ③ | DX14-01~05 测试全部通过（14 asserts） | ✅ |
| ④ | 全部测试通过无回归 | ✅ |

## 测试统计

- 2248 测试总计（622 LSP）
- DX14 新增 14 asserts（5 测试用例）
