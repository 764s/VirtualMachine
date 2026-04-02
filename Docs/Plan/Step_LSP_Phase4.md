# 语言服务 Phase 4：LSP Server + TextMate Grammar（B2）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 Phase 3B ✅ 之后的 **B2 语言服务 Phase 4**。
> **状态**：⏳ 进行中
> **前置**：
> - Debug Phase 3B（Gate 2）✅ — DAP Server 完整单步调试，505 项 Assert
> - ContentLengthStream.cs ✅ — Content-Length 分帧 I/O（与 LSP 共享）
> - JsonHelper.cs ✅ — 零依赖 JSON 读写（与 LSP 共享）
> - VMProgram.SourceMap ✅ — IP → 行号映射
> - VMProgram.SymbolTable ✅ — 符号表（变量名 + 类型 + 寄存器）
> - BytecodeCompiler._errors ✅ — 编译错误列表（行号 + 消息）
> **来源**：
> - [Outlook_And_Risks.md §2.6](Outlook_And_Risks.md#26-语言服务lsp-系列--外部-ide-智能支持) — LSP 子项定义与依赖关系
> - [Outlook_And_Risks.md §6.4](Outlook_And_Risks.md#64-lsp-系列风险逐项降级) — LSP 风险降级策略
> - [Outlook_And_Risks.md §八](Outlook_And_Risks.md#81-总览时间线) — 串行计划 B2 位置
> - [VM_Summary.md §七-B](../VM_Summary.md#b-待执行阶段脚本引擎侧) — B2 定义
>
> **核心目标**：实现 LSP Server 核心 + TextMate 语法高亮 + 实时诊断，使 VS Code 可在编辑 .vm/.ffvm 文件时获得语法着色和实时编译错误提示。

---

## 〇、设计决策

### D-LSP-01：复用 DAP 通信基础设施

LSP 使用与 DAP 完全相同的 Content-Length 分帧协议和 JSON-RPC 消息格式。复用现有 `ContentLengthStream` 和 `JsonHelper`，零新增依赖。

### D-LSP-02：LSP Server = 独立 StandaloneRunner 模式

与 DapServer 类似，LspServer 通过 `StandaloneRunner --lsp` 启动，使用 stdin/stdout 通信。VS Code 通过 `ServerOptions` 启动子进程。

### D-LSP-03：窄化 MVP — 仅实现 6 消息

| 消息 | 方向 | 用途 |
|------|------|------|
| `initialize` | → | 客户端发送能力协商 |
| `initialized` | ← | 服务端确认就绪 |
| `shutdown` | → | 客户端请求关闭 |
| `exit` | → | 客户端通知退出 |
| `textDocument/didOpen` | → | 文件打开通知 |
| `textDocument/didChange` | → | 文件内容变更通知 |
| `textDocument/publishDiagnostics` | ← | 服务端推送编译错误 |

### D-LSP-04：增量编译策略

每次 `didChange` 触发全量重编译（脚本 ≤ 200 行，编译 < 1ms）。无需增量化。

---

## 一、LSP2 — TextMate Grammar 修复

> 复杂度：低 | 独立，无需 LSP Server

### 修改清单

- [ ] **LSP2-01** 移除 `break`/`continue`：Lexer 中不存在这两个关键字，从 grammar 中删除
- [ ] **LSP2-02** 添加 `float` 类型：types 模式中补充 `float`（Lexer 支持 FloatLiteral，类型标注中使用 `float`）
- [ ] **LSP2-03** 支持 `.vm` 扩展名：在 package.json 的 `extensions` 中同时注册 `[".ffvm", ".vm"]`
- [ ] **LSP2-04** 移除 string 高亮规则：语言无字符串字面量类型，移除 `strings` 模式避免误导
- [ ] **LSP2-05** 优化关键字匹配顺序：将 `wait_for` 放在 `wait` 之前确保正确匹配
- [ ] **LSP2-06** 添加 `entry` 注释高亮（可选）：`entry` 不是关键字，但脚本中的 `func entry()` 是入口函数

### 验收标准

- TextMate grammar 正确匹配 Lexer.cs 的 15 个关键字
- `.vm` 和 `.ffvm` 文件均触发语法高亮
- float 类型标注获得正确着色

---

## 二、LSP1 — LSP Server 核心框架

> 复杂度：中 | 依赖 ContentLengthStream + JsonHelper

### 新增文件

| 文件 | 说明 |
|------|------|
| `Assets/Scripts/VM/Debug/LspServer.cs` | LSP Server 主逻辑 |

### 修改文件

| 文件 | 修改 |
|------|------|
| `StandaloneRunner/Program.cs` | 添加 `--lsp` 启动分支 |

### 实现清单

- [ ] **LSP1-01** LspServer 类框架：`Run()` 主循环 + 消息分发（参照 DapServer 模式）
- [ ] **LSP1-02** `initialize` handler：返回 server capabilities（textDocumentSync = Full, diagnosticProvider = true）
- [ ] **LSP1-03** `initialized` 通知处理：确认客户端就绪
- [ ] **LSP1-04** `shutdown` + `exit`：清理资源 + 退出进程
- [ ] **LSP1-05** `textDocument/didOpen`：存储文件 URI→内容映射
- [ ] **LSP1-06** `textDocument/didChange`：更新文件内容（全量同步）
- [ ] **LSP1-07** StandaloneRunner `--lsp` 入口

### 验收标准

- 通过 stdin/stdout 完成 initialize → initialized → shutdown → exit 生命周期
- didOpen/didChange 正确存储文件内容
- 测试覆盖生命周期 + 文档同步

---

## 三、LSP3 — 实时诊断

> 复杂度：中 | 依赖 LSP1 + BytecodeCompiler

### 实现清单

- [ ] **LSP3-01** 编译触发：didOpen/didChange 时调用 BytecodeCompiler 编译文件内容
- [ ] **LSP3-02** 错误映射：将 `_errors` 列表转换为 LSP Diagnostic 对象（行号→0-based，severity = Error）
- [ ] **LSP3-03** publishDiagnostics 推送：发送 `textDocument/publishDiagnostics` 通知
- [ ] **LSP3-04** 空诊断清除：编译成功时推送空 diagnostics 数组清除旧错误

### 验收标准

- 打开含语法错误的文件 → VS Code 显示红色波浪线
- 修复错误后 → 红色波浪线消失
- 测试覆盖：有错误 + 无错误 + 修复错误三种场景

---

## 四、测试计划

| ID | 测试 | 覆盖 |
|----|------|------|
| LSP-T01 | initialize → response 包含正确 capabilities | LSP1-02 |
| LSP-T02 | initialize → initialized → shutdown → exit 生命周期 | LSP1-01~04 |
| LSP-T03 | didOpen → 文件内容存储 | LSP1-05 |
| LSP-T04 | didChange → 文件内容更新 | LSP1-06 |
| LSP-T05 | didOpen 含语法错误 → publishDiagnostics 包含错误 | LSP3-01~03 |
| LSP-T06 | didChange 修复错误 → publishDiagnostics 为空 | LSP3-04 |
| LSP-T07 | didOpen 正确脚本 → publishDiagnostics 为空 | LSP3-04 |

---

## 五、VS Code 扩展集成

### 修改文件

| 文件 | 修改 |
|------|------|
| `vscode-ffvm-debug/package.json` | 添加 LSP 客户端配置 + `.vm` 扩展名 |

### 实现清单

- [ ] **EXT-01** 在 package.json 中添加 `main` 字段指向扩展入口
- [ ] **EXT-02** 添加 `activationEvents`：`onLanguage:ffvm`
- [ ] **EXT-03** 创建 `extension.js`：启动 LspServer 子进程，连接 vscode-languageclient

> **注意**：VS Code LSP 客户端需要 `vscode-languageclient` npm 包。如依赖管理复杂度过高，可延后至独立子步骤。

---

## 六、执行顺序

```
LSP2（Grammar 修复）          ← 5 分钟，独立
  ↓
LSP1（Server 核心）           ← 主要工作
  ↓
LSP3（诊断）                  ← 在 LSP1 基础上叠加
  ↓
测试（LSP-T01~T07）          ← 验证
  ↓
EXT（VS Code 集成）           ← 最后集成
```

---

## 七、与串行计划的关系

完成本步骤后：
- B2 的 LSP2 + LSP1 + LSP3 完成
- LSP4（符号分析）和 LSP5（补全）作为后续增量功能在本步骤基础上叠加
- 不阻塞其他 B 区间步骤（B3 优化、B4 功能补全）
