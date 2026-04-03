# 语言服务 LSP4：符号分析（Document Symbols / Hover / Definition / References）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 B2 中 LSP4 子项。
> **状态**：✅ 完成。586 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 SkillScript + 51 Debug + 93 DAP + 81 LSP），float + Fix64 双模式通过。
> **前置**：
> - LSP Phase 4 (LSP2+LSP1+LSP3) ✅ — LSP Server 核心 + TextMate Grammar + 实时诊断，546 项 Assert
> - VMProgram.SymbolTable ✅ — 变量符号表（DBG2）
> - VMProgram.SourceMap ✅ — IP → 行号映射
> - VMProgram.Functions ✅ — 函数表（名称 + 入口 IP + 参数数）
> - Parser.Parse() ✅ — 返回 ModuleNode（AST），所有节点含 Line/Column
> **来源**：
> - [Outlook_And_Risks.md §2.6](Outlook_And_Risks.md#26-语言服务lsp-系列--外部-ide-智能支持) — LSP4 定义
> - [Step_LSP_Phase4.md](Step_LSP_Phase4.md) — LSP Server 核心实现
>
> **核心目标**：在 VS Code 中实现文档大纲（Outline）、悬停类型提示（Hover）、跳转定义（Go-to-Definition）、查找引用（Find References）四项符号分析功能。

---

## 〇、设计决策

### D-LSP4-01：AST 缓存策略

LspServer 在每次 didOpen/didChange 时已调用 BytecodeCompiler 进行全量编译。LSP4 需要 AST 来回答位置查询（hover/definition/references），因此：
- 在 didOpen/didChange 中额外调用 `Parser.Parse()` 保留 `ModuleNode`
- 将 `ModuleNode` + `CompileResult` 一起缓存在 `_documents` 旁（每个 URI 一份）
- 解析失败时保留上一次成功的 AST（确保部分编辑不破坏符号查询）

### D-LSP4-02：位置查找策略

实现一个轻量 AST 遍历器 `FindNodeAtPosition(ModuleNode, line, column)` 来定位光标所在节点：
- 遍历 Functions → Body 中所有语句/表达式
- 匹配 `node.Line == targetLine` 且列范围包含目标列
- 返回最深层匹配节点（优先具体节点）

### D-LSP4-03：符号解析范围

LSP4 仅处理单文件作用域（当前无跨模块引用机制）：
- 函数定义 = FuncDecl（Name + Line + Column）
- 结构体定义 = StructDecl（Name + Line + Column）
- 变量定义 = VarDeclStmt（Name + Line + Column）
- 参数定义 = FuncDecl.Parameters（Name + 位置从 FuncDecl 推断）

---

## 一、LSP4-01 — AST 缓存 + documentSymbol

> 复杂度：低 | 基础设施

### 实现清单

- [x] **LSP4-01a** 新增文档缓存结构：`_documentData[uri]` 存储 `(string content, ModuleNode ast, CompileResult result)`
- [x] **LSP4-01b** didOpen/didChange 中调用 `Parser.Parse()` 缓存 AST；解析失败时保留旧 AST
- [x] **LSP4-01c** initialize 响应中添加 `documentSymbolProvider: true`
- [x] **LSP4-01d** HandleRequest 分发 `textDocument/documentSymbol`
- [x] **LSP4-01e** 实现 documentSymbol handler：遍历 AST 返回函数（Function）和结构体（Struct）符号列表
  - SymbolKind: Function = 12, Struct = 23, Variable = 13
  - 每个符号包含 name, kind, range, selectionRange

### 验收标准

- VS Code Outline 面板显示所有函数和结构体
- 测试覆盖：含函数+结构体的脚本返回正确符号列表

---

## 二、LSP4-02 — Hover（悬停类型提示）

> 复杂度：中 | 依赖 AST 缓存

### 实现清单

- [x] **LSP4-02a** initialize 响应中添加 `hoverProvider: true`
- [x] **LSP4-02b** HandleRequest 分发 `textDocument/hover`
- [x] **LSP4-02c** 实现 hover handler：
  - 在缓存的 AST 中查找光标位置对应的标识符
  - 函数名 → 显示 `func name(params): returnType`
  - 变量名 → 显示 `var name: type`（从 VarDeclStmt.TypeName 或推断）
  - 结构体名 → 显示 `struct name { fields... }`
  - Syscall 名 → 显示 `syscall name(argCount)`
- [x] **LSP4-02d** 未找到符号时返回 null（LSP 规范）

### 验收标准

- 悬停在函数名上显示函数签名
- 悬停在变量名上显示变量类型
- 测试覆盖：hover 函数名、变量名、无符号位置

---

## 三、LSP4-03 — Definition（跳转定义）

> 复杂度：中 | 依赖 AST 缓存

### 实现清单

- [x] **LSP4-03a** initialize 响应中添加 `definitionProvider: true`
- [x] **LSP4-03b** HandleRequest 分发 `textDocument/definition`
- [x] **LSP4-03c** 实现 definition handler：
  - 在 AST 中查找光标位置的标识符
  - 如果是函数调用（CallExpr.FunctionName）→ 找到对应 FuncDecl 的位置
  - 如果是变量引用（IdentifierExpr）→ 找到对应 VarDeclStmt 或参数声明的位置
  - 如果是字段访问（FieldAccessExpr）→ 找到对应 StructDecl 中的字段位置
  - 返回 Location { uri, range }
- [x] **LSP4-03d** 符号未找到时返回 null

### 验收标准

- 在函数调用上 Ctrl+Click 跳转到函数定义
- 在变量使用处 Ctrl+Click 跳转到变量声明
- 测试覆盖：definition 函数调用、变量引用、无定义位置

---

## 四、LSP4-04 — References（查找引用）

> 复杂度：中 | 依赖 AST 遍历

### 实现清单

- [x] **LSP4-04a** initialize 响应中添加 `referencesProvider: true`
- [x] **LSP4-04b** HandleRequest 分发 `textDocument/references`
- [x] **LSP4-04c** 实现 references handler：
  - 确定光标位置的符号名称和类型（函数/变量/结构体）
  - 遍历 AST 收集所有引用位置：
    - 函数引用：所有 CallExpr.FunctionName == name 的位置 + FuncDecl 声明位置
    - 变量引用：所有 IdentifierExpr.Name == name（同作用域）+ VarDeclStmt 声明位置
    - 结构体引用：所有 VarDeclStmt.TypeName == name 的位置 + StructDecl 声明位置
  - 返回 Location[] 数组
- [x] **LSP4-04d** 未找到引用时返回空数组

### 验收标准

- 右键→Find All References 列出所有使用位置
- 测试覆盖：references 函数、变量、结构体

---

## 五、测试计划

| ID | 测试 | 覆盖 |
|----|------|------|
| LSP4-T01 | initialize 响应包含 documentSymbolProvider + hoverProvider + definitionProvider + referencesProvider | LSP4-01c, 02a, 03a, 04a |
| LSP4-T02 | documentSymbol 返回函数和结构体列表 | LSP4-01e |
| LSP4-T03 | documentSymbol 空文件返回空列表 | LSP4-01e |
| LSP4-T04 | hover 函数名返回签名 | LSP4-02c |
| LSP4-T05 | hover 变量名返回类型 | LSP4-02c |
| LSP4-T06 | hover 无符号位置返回 null | LSP4-02d |
| LSP4-T07 | definition 函数调用跳转到 FuncDecl | LSP4-03c |
| LSP4-T08 | definition 变量引用跳转到 VarDeclStmt | LSP4-03c |
| LSP4-T09 | definition 无定义返回 null | LSP4-03d |
| LSP4-T10 | references 函数名返回声明+所有调用位置 | LSP4-04c |
| LSP4-T11 | references 变量名返回声明+所有使用位置 | LSP4-04c |

---

## 六、执行顺序

```
LSP4-01（AST 缓存 + documentSymbol）    ← 基础设施 + 最简功能
  ↓
LSP4-02（Hover）                         ← 依赖 AST 位置查找
  ↓
LSP4-03（Definition）                    ← 依赖 AST 位置查找 + 声明定位
  ↓
LSP4-04（References）                    ← 依赖 AST 全遍历
  ↓
测试（LSP4-T01~T11）                     ← 验证
```

---

## 七、与串行计划的关系

完成本步骤后：
- B2 的 LSP4 完成（符号分析：documentSymbol + hover + definition + references）
- LSP5（代码补全）作为下一个增量功能
- 不阻塞其他 B 区间步骤（B3 优化、B4 功能补全）
