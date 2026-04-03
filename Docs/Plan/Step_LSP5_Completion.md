# Step LSP5: Code Completion

> **前置**：LSP4 (符号分析) ✅ 586 assertions  
> **目标**：实现 `textDocument/completion`，为编辑器提供上下文感知的代码补全  
> **产出**：CompletionProvider 注册 + HandleCompletion 实现 + 测试覆盖

---

## 补全类别

| # | 类别 | 数据来源 | CompletionItemKind |
|---|------|---------|-------------------|
| 1 | 关键字 | Lexer Keywords (15 个) | 14 (Keyword) |
| 2 | 用户函数名 | AST `ModuleNode.Functions` | 3 (Function) |
| 3 | 作用域内变量 | AST 遍历 (VarDeclStmt + Parameters) | 6 (Variable) |
| 4 | 结构体类型名 | AST `ModuleNode.Structs` | 22 (Struct) |
| 5 | Syscall 名 | `_defaultSyscalls` 字典 keys | 3 (Function) |
| 6 | 结构体字段 | `StructDecl.Fields` (在 `expr.field` 上下文) | 5 (Field) |

---

## 实施清单

### A. LspServer.cs 修改

- [x] **A1** `HandleInitialize`: 添加 `completionProvider` capability
  - `triggerCharacters: ["."]` — 触发结构体字段补全
- [x] **A2** `HandleRequest`: 添加 `case "textDocument/completion"` 分支
- [x] **A3** 实现 `HandleCompletion(JsonObject parameters)`:
  - 提取 `position` (line, character)
  - 获取文档源码和缓存 AST
  - 检测上下文：是否在 `expr.` 后（字段补全）或普通标识符位置
  - 构建 CompletionItem 列表
- [x] **A4** 实现上下文检测:
  - 光标前有 `.` → 字段补全模式：解析 `.` 前变量名 → 查找 struct 类型 → 返回字段列表
  - 否则 → 通用补全模式：关键字 + 函数 + 变量 + 结构体名 + Syscall 名
- [x] **A5** 实现作用域感知:
  - FindContainingFunction 定位光标所在函数
  - 参数补全 + 局部变量补全（仅光标前声明的变量）

### B. LspTests.cs 测试

- [x] **B1** `LspBatchSession.AddCompletion()` 辅助方法
- [x] **B2** LSP5-T01: initialize 返回 completionProvider capability
- [x] **B3** LSP5-T02: 基本补全返回关键字 + 函数 + 变量
- [x] **B4** LSP5-T03: 结构体字段补全 (`v.` → x, y)
- [x] **B5** LSP5-T04: 空文件补全返回关键字
- [x] **B6** LSP5-T05: Syscall 名在补全结果中
- [x] **B7** LSP5-T06: 作用域感知 — 仅返回当前函数内变量
- [x] **B8** LSP5-T07: 补全项包含 detail 签名文本
- [x] **B9** LSP5-T08: 函数参数在补全结果中

### C. 文档更新

- [x] **C1** VM_Summary.md §七 A 表添加 LSP5 完成行
- [x] **C2** VM_Summary.md §七 B2 行 LSP5 状态 → ✅
- [x] **C3** 更新 LspServer.cs 文件头注释
- [x] **C4** Outlook_And_Risks.md LSP5 标记 ✅

---

## 通过标准

1. ✅ 全部现有 586 项 Assert 不回归
2. ✅ LSP5 新增 38 项 Assert 全部 PASS（8 个测试用例）
3. ✅ 总计 624 项 Assert 通过
4. ⏳ `dotnet run` 双模式 (float + Fix64) 通过（待 CI 验证）
