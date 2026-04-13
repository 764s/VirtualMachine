# DX11: VFS + Rename 状态更新

## 目标

修复**连续文件重命名失败**问题：第一次 rename 后 include 引用自动更新成功，第二次 rename 无法更新。

根因：`workspace/willRenameFiles` 返回 WorkspaceEdit 后，LSP Server 内部状态（`_documents` URI key、AST 缓存、依赖图）未同步更新，导致第二次 rename 时 `ScanWorkspaceForRenames` 读取到过时的文件内容。

## 前置条件

| 前置 | 状态 |
|------|------|
| DX10 依赖图 + 全项目诊断 | ✅ 已完成 |
| E003 didClose 处理 | ✅ 已完成 |
| DX6 willRenameFiles 基础 | ✅ 已完成 |

## 完成条件

| # | 条件 | 状态 |
|---|------|------|
| ① | HandleWillRenameFiles 返回 WorkspaceEdit 后内部状态同步更新 | ⏳ |
| ② | 连续重命名测试通过（DX11-01~02） | ⏳ |
| ③ | ScanWorkspaceForRenames 优先使用 DocumentStore 内容 | ⏳ |
| ④ | 依赖图在 rename 后正确更新 | ⏳ |
| ⑤ | 全部测试通过（包含新增 DX11 测试）无回归 | ⏳ |

## 根因分析

### 连续重命名场景

```
1. 用户打开 VSCode，project 包含 base.ffs 和 main.ffs（include "base"）
2. 用户重命名 base.ffs → renamed1.ffs
   → willRenameFiles 返回 WorkspaceEdit: main.ffs 中 include "base" → include "renamed1"
   → VSCode 应用 WorkspaceEdit 修改 main.ffs 编辑器缓冲区
   → VSCode 发送 didChange 更新 main.ffs 内容（include "renamed1"）
   → ✓ 第一次成功
3. 用户继续重命名 renamed1.ffs → renamed2.ffs
   → willRenameFiles 需要扫描 main.ffs 内容找到 include "renamed1"
   → 问题：ScanWorkspaceForRenames 可能从磁盘读取（仍是 include "base"）
   → ✗ 找不到 include "renamed1"，无法更新
```

### 解决方案

**方案 A（本期采用）：willRenameFiles 完成后 apply pending edits**

HandleWillRenameFiles 返回 WorkspaceEdit 后，服务器**预应用**（pre-apply）编辑结果到 DocumentStore：
1. 对于 WorkspaceEdit 中每个被修改的文件 URI，将编辑结果应用到 `_docStore` 内容
2. 将被重命名的文件的 URI key 从旧 URI 迁移到新 URI（content + AST + mergedAst + 依赖图）
3. 这样下次 willRenameFiles 时 ScanWorkspaceForRenames 从 `_docStore` 读取到最新内容

**注意**：这不需要实际的 VFS 抽象层——现有 `OverlayFileResolver` + `DocumentStore.TryGetContent` 已经提供了 overlay 语义。关键缺失是 **rename 后未更新 DocumentStore 状态**。

## 子任务清单

### Phase 1: DocumentStore rename 基础设施

- [ ] 1.1 DocumentStore.RenameUri(oldUri, newUri) — 迁移 content/ast/mergedAst + 更新依赖图 forward/dependent edges
- [ ] 1.2 DocumentStore.ApplyTextEdits(uri, edits) — 在内存中应用文本编辑（line/char range replace）

### Phase 2: HandleWillRenameFiles 状态同步

- [ ] 2.1 HandleWillRenameFiles 返回 WorkspaceEdit 后，调用 ApplyRenameState
- [ ] 2.2 ApplyRenameState: 遍历 WorkspaceEdit.changes → 对每个 URI 应用 text edits 到 DocumentStore
- [ ] 2.3 ApplyRenameState: 对被重命名的文件执行 RenameUri(oldUri, newUri)
- [ ] 2.4 ApplyRenameState: 触发受影响文件的重编译（CompileAndPublishDiagnostics）更新 AST + 依赖图

### Phase 3: ScanWorkspaceForRenames VFS 增强

- [ ] 3.1 ScanWorkspaceForRenames 对已打开文件使用 _docStore.TryGetContent（已有，验证）
- [ ] 3.2 确认 ScanWorkspaceForRenames 现有逻辑与状态同步后的一致性

### Phase 4: 测试覆盖

- [ ] 4.1 DX11-01: 连续重命名 — 第二次 rename 后 include 引用仍正确更新
- [ ] 4.2 DX11-02: Rename 后依赖图更新 — 旧路径不再有依赖者，新路径有
- [ ] 4.3 DX11-03: Rename 后 hover/definition 等符号查询仍工作
- [ ] 4.4 DX11-04: include-as 场景连续重命名
- [ ] 4.5 DX11-05: rename 后 didChange 仍正确处理

### Phase 5: 文档更新

- [ ] 5.1 VM_Summary.md 更新 DX11 状态 ✅ + 测试总计
- [ ] 5.2 Outlook_And_Risks.md 更新 DX11 状态
- [ ] 5.3 D_LspArchitecture.md 更新阶段 2 状态 ✅

## 实现细节

### DocumentStore.RenameUri

```
RenameUri(oldUri, newUri):
  1. content[newUri] = content[oldUri]; content.Remove(oldUri)
  2. asts[newUri] = asts[oldUri]; asts.Remove(oldUri)  
  3. mergedAsts[newUri] = mergedAsts[oldUri]; mergedAsts.Remove(oldUri)
  4. includeForward[newUri] = includeForward[oldUri]; includeForward.Remove(oldUri)
  5. 遍历 includeForward[newUri] 中的每个 resolvedPath:
     - includeDependents[resolvedPath].Remove(oldUri)
     - includeDependents[resolvedPath].Add(newUri)
```

### ApplyTextEdits

对单个 URI 的文本内容应用 LSP TextEdit 列表：
1. 将文本按行分割
2. 对每个 TextEdit（按 range 逆序排列防止偏移）：
   - 替换 [startLine:startChar, endLine:endChar] 范围为 newText
3. 合并行数组为新文本
4. 更新 `_docStore.SetContent(uri, newText)`

### ScanWorkspaceForRenames 已有 VFS 语义

现有代码已检查 `_docStore.TryGetContent(fileUri, out cached)` 优先使用已打开文件内容。
DX11 的关键改进是确保 **rename 后内容已更新到 DocumentStore**，使下一次 scan 能读到最新内容。
