# E003 LSP 理想化结构修复蓝图（基础设施 + 文件执行清单）

状态: 🔧 进行中（架构整改任务）
日期: 2026-04-15
定位: 为 B003 行为偏差清单提供“非最小修复”的统一能力层，降低复发率

---

## 0. 核心目标与边界

### 0.1 核心目标

- 用统一语义内核替代 LspServer 单体内重复逻辑。
- 用统一位置系统替代各 handler 手算 line/column。
- 用统一路径/URI 规范化替代散落字符串比较。
- 用工作区索引支持 include/include-as/memberCall 跨文件语义。
- 用场景矩阵测试和 CI 门禁保证“修一次，长期不回退”。

### 0.2 非目标（本任务不做）

- 不重写编译器主流程（BytecodeCompiler 主功能不改）。
- 不一次性替换所有旧 API（允许过渡期 facade）。
- 不在缺乏回归测试前进行行为性大重构。

### 0.3 实施原则

- 先建基础设施，再迁移功能点。
- 所有 LSP 功能共享 SymbolIdentity。
- 新代码禁止直接比较路径字符串。
- 每一项行为修复必须绑定测试编号（B003-xx）。

---

## 1. 目标目录/文件框架（建议最终态）

> 位置基线: `Assets/Scripts/VM/Debug`

```text
Assets/Scripts/VM/Debug/
	LspServer.cs                           # 组合根（薄层）
	Lsp/
		Contracts/
			SymbolIdentity.cs
			SymbolKindTag.cs
			TextSpan.cs
			TextPosition.cs
			SymbolQueryRequest.cs
			SymbolQueryResult.cs

		Infrastructure/
			Paths/
				PathKey.cs
				UriKey.cs
				PathCanonicalizer.cs
			Text/
				LineMap.cs
				SpanConverter.cs
			Workspace/
				WorkspaceDocumentStore.cs
				WorkspaceSnapshot.cs

		Index/
			WorkspaceSymbolIndex.cs
			IncludeGraph.cs
			AliasGraph.cs

		Query/
			SymbolQueryCore.cs
			SymbolResolver.cs
			DefinitionService.cs
			ReferencesService.cs
			RenameService.cs
			HoverService.cs
			CompletionService.cs
			SignatureHelpService.cs

		Diagnostics/
			DiagnosticOwnershipPolicy.cs
			DiagnosticRouter.cs

		Protocol/
			LspRequestDispatcher.cs
			LspNotificationDispatcher.cs
			LspResponseWriter.cs

		Handlers/
			DefinitionHandler.cs
			ReferencesHandler.cs
			HoverHandler.cs
			CompletionHandler.cs
			SignatureHelpHandler.cs
			RenameHandler.cs
			PrepareRenameHandler.cs
			SemanticTokensHandler.cs
			WorkspaceRenameFilesHandler.cs

		Legacy/
			LspServerFacade.cs                 # 过渡桥接层
```

---

## 2. 文件职责边界（必须写在文件顶部）

### 2.1 统一顶部模板

```csharp
// Responsibility:
//   <该文件唯一职责>
// Owns:
//   <该文件维护的数据/决策>
// Out of Scope:
//   <明确不负责的内容>
// Inputs/Outputs:
//   In:  <输入对象>
//   Out: <输出对象>
// Invariants:
//   - <必须长期成立的不变量>
// Error Policy:
//   <错误传播/降级策略>
```

### 2.2 关键文件边界定义

| 文件 | 责任边界（必须） | 禁止事项 |
|---|---|---|
| `Assets/Scripts/VM/Debug/LspServer.cs` | 仅做生命周期与依赖组装，协议入口 | 禁止写符号匹配逻辑 |
| `Assets/Scripts/VM/Debug/Lsp/Query/SymbolQueryCore.cs` | 统一 SymbolIdentity 解析入口 | 禁止直接写 LSP JSON 协议序列化 |
| `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Text/SpanConverter.cs` | 唯一 line/column <-> span 转换点 | 禁止任何语义判断 |
| `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Paths/PathCanonicalizer.cs` | 唯一路径规范化入口 | 禁止依赖业务语义（symbol、diag） |
| `Assets/Scripts/VM/Debug/Lsp/Index/WorkspaceSymbolIndex.cs` | 工作区符号/引用索引与增量更新 | 禁止协议发送 |
| `Assets/Scripts/VM/Debug/Lsp/Diagnostics/DiagnosticRouter.cs` | 统一 error/warning 归属与输出目标 | 禁止做编译/语义解析 |
| `Assets/Scripts/VM/Debug/Lsp/Handlers/*.cs` | 协议参数解包 + 调用 Query/Index/Diagnostics | 禁止重复实现语义规则 |

---

## 3. 基础设施能力清单（先建后迁移）

### 3.1 Symbol Query Core

- [ ] 定义统一 SymbolIdentity: `kind + name + scope + parent + origin + declSpan`
- [ ] 定义统一 Query API: `ResolveAtPosition / FindDefinition / FindReferences / CanRename / GetRenameRanges`
- [ ] Definition/References/Rename/PrepareRename 全部走 Query Core
- [ ] Hover/Completion/SignatureHelp 改走同一符号解析入口

### 3.2 Span & Position

- [ ] 引入 span 主模型（offset-first）
- [ ] 所有 range 输出统一由 SpanConverter 生成
- [ ] AST 层补充 struct literal 字段名位置（必要时）
- [ ] 禁止以 `VarDeclStmt.Column` 代表变量名列

### 3.3 Path & URI Canonicalization

- [ ] `PathKey` / `UriKey` 引入并全链路接入
- [ ] `PathToFileUri` 与 `FilePathToUri` 规则统一
- [ ] include graph、rename map、origin compare 全走 canonicalized key

### 3.4 Workspace Semantic Index

- [ ] 文件声明索引 + 引用索引 + include/alias 图
- [ ] 支持 MemberCall（`Alias.Func`）语义绑定
- [ ] 支持 didOpen/didChange/didClose/watchedFiles 增量更新

### 3.5 Diagnostic Routing Policy

- [ ] 统一 errors/warnings 归属策略
- [ ] 策略日志化（便于 debug）
- [ ] 跨文件归属行为可配置并测试覆盖

### 3.6 Scenario Matrix Test Harness

- [ ] 操作维度: Definition/References/Rename/Completion/Hover/SignatureHelp
- [ ] 特性维度: include/alias/memberCall/dottedType/shadowing/nestedField
- [ ] 回放链路: didOpen -> didChange -> willRenameFiles -> watchedFiles

---

## 4. 执行步骤（按文件落地）

> 目标: 每一步都明确“新增哪些文件、修改哪些文件、验证哪些测试”。

### Step 0: 建基线与防回退门槛

执行目标:
- 锁定 B003 P0 场景为回归基线。

新增/修改文件:
- 修改 `Assets/Scripts/VM/Tests/LspTests.cs`（补 B003-01~B003-08）
- 可新增 `Assets/Scripts/VM/Tests/LspScenarioTests.cs`（场景回放专用）

验收:
- [ ] 新增测试可稳定复现当前偏差（先允许失败）

---

### Step 1: 建立契约层（Contracts）

执行目标:
- 定义统一符号、位置、查询输入输出契约。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Contracts/SymbolIdentity.cs`
- `Assets/Scripts/VM/Debug/Lsp/Contracts/SymbolKindTag.cs`
- `Assets/Scripts/VM/Debug/Lsp/Contracts/TextSpan.cs`
- `Assets/Scripts/VM/Debug/Lsp/Contracts/TextPosition.cs`
- `Assets/Scripts/VM/Debug/Lsp/Contracts/SymbolQueryRequest.cs`
- `Assets/Scripts/VM/Debug/Lsp/Contracts/SymbolQueryResult.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（仅引入契约，不迁移逻辑）

验收:
- [ ] 编译通过
- [ ] 旧行为不变

---

### Step 2: 建立位置与路径基础设施

执行目标:
- 统一位置转换和路径规范化。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Text/LineMap.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Text/SpanConverter.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Paths/PathKey.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Paths/UriKey.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Paths/PathCanonicalizer.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（路径相关 helper 改调新服务）
- `Assets/Scripts/VM/Debug/JsonHelper.cs`（如需扩展 id 类型兼容）

验收:
- [ ] B003-13/B003-14/B003-15 通过

---

### Step 3: 建立 Query Core（先接 Definition/References/Rename）

执行目标:
- 抽离核心语义查询，消除重复规则。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Query/SymbolQueryCore.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/SymbolResolver.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/DefinitionService.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/ReferencesService.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/RenameService.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（Definition/References/Rename/PrepareRename 改调 Core）
- `Assets/Scripts/VM/AST/ASTNode.cs`（必要时补字段位置信息）
- `Assets/Scripts/VM/Compiler/Parser.cs`（输出新增位置信息）

验收:
- [ ] B003-04/B003-05/B003-06/B003-07/B003-08 通过
- [ ] Definition 与 Rename 同位置 SymbolIdentity 一致

---

### Step 4: 建立工作区索引与 alias/memberCall 语义

执行目标:
- 完成跨文件 include/include-as/memberCall 语义闭环。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Index/WorkspaceSymbolIndex.cs`
- `Assets/Scripts/VM/Debug/Lsp/Index/IncludeGraph.cs`
- `Assets/Scripts/VM/Debug/Lsp/Index/AliasGraph.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Workspace/WorkspaceDocumentStore.cs`
- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Workspace/WorkspaceSnapshot.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（didOpen/didChange/didClose/watchedFiles 接入 index）
- `Assets/Scripts/VM/Compiler/Preprocessor.cs`（必要的 include/alias 元数据暴露）

验收:
- [ ] B003-01/B003-02/B003-03 通过
- [ ] include references 支持跨文件返回

---

### Step 5: 迁移 Hover/Completion/SignatureHelp 到统一服务

执行目标:
- 三项能力共享 Query Core 与 Workspace Index。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Query/HoverService.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/CompletionService.cs`
- `Assets/Scripts/VM/Debug/Lsp/Query/SignatureHelpService.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（三项 handler 改调 service）

验收:
- [ ] B003-09/B003-10/B003-11 通过

---

### Step 6: 建立诊断归属策略层

执行目标:
- 统一 errors/warnings 归属。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Diagnostics/DiagnosticOwnershipPolicy.cs`
- `Assets/Scripts/VM/Debug/Lsp/Diagnostics/DiagnosticRouter.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（CompileAndPublishDiagnostics 改走 router）

验收:
- [ ] B003-12 通过

---

### Step 7: 协议层薄化与 handler 分拆

执行目标:
- LspServer 成为组合根，handler 独立。

新增文件:
- `Assets/Scripts/VM/Debug/Lsp/Protocol/LspRequestDispatcher.cs`
- `Assets/Scripts/VM/Debug/Lsp/Protocol/LspNotificationDispatcher.cs`
- `Assets/Scripts/VM/Debug/Lsp/Protocol/LspResponseWriter.cs`
- `Assets/Scripts/VM/Debug/Lsp/Handlers/*.cs`（按功能拆分）
- `Assets/Scripts/VM/Debug/Lsp/Legacy/LspServerFacade.cs`

修改文件:
- `Assets/Scripts/VM/Debug/LspServer.cs`（保留入口 + 依赖组装）

验收:
- [ ] `LspServer.cs` 不再包含大段语义匹配逻辑
- [ ] 所有 handler 仅做协议适配

---

## 5. 与 B003 偏差点映射

- G01/G02/G03 -> Step 4 + Step 5
- G04/G05/G06/G07 -> Step 2 + Step 3
- G08/G09 -> Step 5
- G10 -> Step 6
- G11/G12/G13 -> Step 2 + Step 4
- G14 -> Step 2（协议兼容增强）
- G15 -> Step 4（索引生命周期）

---

## 6. 合并门禁（DoD）

- [ ] 无新增点状语义分支进入 handler
- [ ] 所有 range 由 SpanConverter 生成
- [ ] 所有路径比较使用 PathKey/UriKey
- [ ] B003 P0 全部通过
- [ ] 至少 2 个 Windows 路径风格回归测试通过
- [ ] 设计文档与实现一致（职责边界无漂移）

---

## 7. 本周执行包（可直接开工）

Week-1 建议执行顺序:

1. Step 0（测试基线）
2. Step 1（Contracts）
3. Step 2（Span + Path）
4. Step 3（Definition/References/Rename）

本周文件清单:

- 新增:
	- `Assets/Scripts/VM/Debug/Lsp/Contracts/*.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Text/*.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Infrastructure/Paths/*.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Query/SymbolQueryCore.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Query/DefinitionService.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Query/ReferencesService.cs`
	- `Assets/Scripts/VM/Debug/Lsp/Query/RenameService.cs`
- 修改:
	- `Assets/Scripts/VM/Debug/LspServer.cs`
	- `Assets/Scripts/VM/AST/ASTNode.cs`
	- `Assets/Scripts/VM/Compiler/Parser.cs`
	- `Assets/Scripts/VM/Tests/LspTests.cs`

---

## 8. 备注

该文档是“执行导向”的架构蓝图，不追求一次性重构完毕，而追求每一步可落地、可回归、可验收。
只要严格遵守职责边界 + 测试门禁，结构化修复将显著降低同类 bug 的再发生概率。
