# LSP 架构演进：全项目诊断、状态管理与增量更新

> **状态**：🔨 部分实施（DX10 ✅ 完成，DX11 🟡 可执行，DX12 ⚪ 待 DX11 完成后开始）
> **来源**：E003 — 枚举引用修复与 didClose 缺失暴露的架构性问题
> **日期**：2026-04-13

---

## 一、背景与问题来源

E003 紧急修复发现了以下直接缺陷：
1. **枚举类型引用不完整** — `findReferences` 对 enum 类型不收集类型注解使用处（已修复）
2. **枚举成员引用不完整** — `findReferences` 对 enum member 不收集 `EnumName.MEMBER` 使用处（已修复）
3. **didClose 未实现** — 服务器声明 `openClose: true` 但不处理 `textDocument/didClose`，导致文档缓存泄漏（已修复）

在修复过程中暴露了更深层的架构问题：
- **连续文件重命名失败**：第一次 rename 后 include 引用自动更新，第二次 rename 无法自动更新
- **仅诊断打开文件**：用户无法看到未打开文件的错误
- **无依赖图**：修改被 include 的文件后，依赖方不会自动重新诊断
- **无后台编译调度**：所有编译同步在请求路径中执行

---

## 二、连续重命名失败的根因分析

### 2.1 问题场景

```
1. 用户打开 VSCode，进入 ffs 工程（项目初始正确）
2. 用户重命名 xxbase.ffs → renamed1.ffs（include 引用自动更新 ✓）
3. 用户继续重命名 renamed1.ffs → renamed2.ffs（include 引用不再自动更新 ✗）
```

### 2.2 直接原因

`workspace/willRenameFiles` 的处理流程是：
1. 接收 `oldUri` + `newUri`
2. `ResolveFileToIncludePaths(oldAbsPath)` 计算旧文件的 include 路径
3. `ScanWorkspaceForRenames` 扫描所有 `.ffs` 文件找到匹配的 include 指令

第二次重命名失败的原因是 **ScanWorkspaceForRenames 读取的文件内容可能是过时的**：

- **对未打开的文件**：从磁盘读取。如果 VSCode 第一次 WorkspaceEdit 的结果尚未保存到磁盘（仅修改了编辑器缓冲区），磁盘上仍是 `include "xxbase"` 而非 `include "renamed1"`
- **对已打开的文件**：从 `_documents` 缓存读取。但 VSCode 应用 WorkspaceEdit 后发送的 `didChange` 和下一次 `willRenameFiles` 之间可能存在时序竞争

### 2.3 根因

缺少统一的文件内容视图（VFS）和事件驱动的状态更新机制。服务器不知道第一次 WorkspaceEdit 的结果是什么，也无法保证 `_documents` 缓存反映了最新的编辑器状态。

---

## 三、当前诊断模型的局限

### 3.1 现状

诊断仅在 `didOpen`/`didChange` 时对当前文件触发 `CompileAndPublishDiagnostics`。

### 3.2 问题

| 场景 | 期望 | 现状 |
|------|------|------|
| 修改被多文件 include 的基础文件 | 所有依赖方诊断更新 | 仅当前文件更新 |
| 未打开文件存在错误 | 全项目错误可见 | 不可见 |
| 文件重命名后 include 路径变更 | 受影响文件诊断更新 | 不更新 |

### 3.3 错误隔离

DX8 已实现 `IsCrossFileError` 过滤跨文件错误，这为错误隔离提供了基础。但结合全项目诊断后，需要确保每个文件独立编译+发布自己的错误。

---

## 四、跨语言对比

| 特性 | **C# (Roslyn/OmniSharp)** | **TypeScript (tsserver)** | **Rust (rust-analyzer)** | **FFS (当前)** |
|------|--------------------------|--------------------------|------------------------|---------------|
| 诊断范围 | 全项目 | 全项目 | 全项目 | ❌ 仅打开文件 |
| 增量更新 | 增量语法树 diff | 增量类型检查 | salsa 增量计算引擎 | ❌ 全量 Parse+Compile |
| 后台编译 | 独立后台线程 | 主线程调度 | 线程池 | ❌ 同步请求路径 |
| 依赖图 | 项目引用图 | import 依赖图 | crate + module 图 | ❌ 无 |
| 文件 rename | Roslyn 重构 | rename symbol | 支持 | ✅ willRenameFiles（无状态更新） |
| 脏文件追踪 | overlay | overlay FS | VFS | ❌ `_documents` 仅 open files |
| 错误隔离 | 每文件独立编译 | 每文件独立检查 | 每 crate 类型检查 | ⚠️ IsCrossFileError 基础 |

### 4.1 推荐模式

对 FFS 项目规模（脚本引擎，项目文件数量有限），采用 **tsserver 模式的简化版**：
- 全量编译成本可控
- 关键是建立依赖图 + 按拓扑序重编译变更影响范围
- 无需增量解析（性能收益有限，复杂度高）

---

## 五、缺失的前置功能清单

### P0：必须实现

| 功能 | 说明 | 用途 |
|------|------|------|
| **Include 依赖图** | `Dictionary<string, HashSet<string>>`：文件 → 依赖者集合。didOpen/didChange 时解析 import 列表更新图 | 修改 fileB 时知道 fileA 需要重新诊断；rename 时知道需要扫描哪些文件 |
| **`didClose` 处理** | ✅ E003 已修复 | 缓存清理 |
| **`workspace/didChangeWatchedFiles`** | 文件在磁盘上变更时（外部编辑、git checkout 等）触发重新编译 | 外部变更感知 |

### P1：应该实现

| 功能 | 说明 | 用途 |
|------|------|------|
| **VFS / 文件内容 Overlay** | 统一抽象：已打开 → `_documents`；未打开 → 磁盘。提取为 `IContentProvider` | 消除 rename 时的内容不一致 |
| **后台诊断调度器** | debounce 300ms + 后台线程 + 取消机制 | 全项目诊断不阻塞编辑 |
| **Rename 后状态更新** | 更新 `_documents` URI key、清除旧 AST 缓存、更新依赖图 | 连续重命名正确性 |

### P2：可选优化

| 功能 | 说明 |
|------|------|
| 编译结果缓存 | 未变更的 include 文件跳过重编译 |
| 取消令牌 | 用户快速连续输入时取消过时的编译 |
| 增量解析 | 仅重新解析变更区域（视项目规模决定必要性） |

---

## 六、更新过程中用户继续编辑的处理策略

### 6.1 Debounce + Cancel

1. 每次 `didChange` 重置 debounce 计时器（300ms）
2. 计时器到期后启动后台编译
3. 编译进行中又收到 `didChange` → 取消当前编译，重新 debounce
4. 编译完成后发布诊断

### 6.2 错误隔离

结合全项目诊断后，每个文件独立编译并发布自己的错误：
- 文件 A 的编辑错误不影响文件 B 的诊断显示
- DX8 `IsCrossFileError` 过滤跨文件错误的机制继续生效

---

## 七、实施路线建议

### 阶段 1 — DX10：依赖图 + 全项目诊断基础 ✅ 已完成

- ✅ Include 依赖图数据结构（`_includeDependents` + `_includeForward` 双向图）
- ✅ `Preprocessor.ResolvedFilePaths` 追踪传递性依赖（含间接 include）
- ✅ `OverlayFileResolver` 使级联重编译可见打开文档的内存内容
- ✅ `RecompileDependents()` 按依赖图传播 + visited 防环 + snapshot 安全
- ✅ didOpen/didChange 触发级联重编译受影响文件
- ✅ `workspace/didChangeWatchedFiles` 处理外部磁盘变更
- ✅ DX10-01~08 测试覆盖（14 asserts），2127 测试总计（501 LSP）

### 阶段 2 — DX11：VFS + Rename 状态更新 🟡 可执行

- 统一文件内容提供器（VFS/Overlay）
- Rename 后状态更新（URI key 更新、缓存清除、依赖图更新）
- 修复连续重命名问题

### 阶段 3 — DX12：后台编译调度

- 后台诊断调度器（debounce + 取消机制）
- 性能优化（编译结果缓存、增量处理）

---

## 八、与现有架构的关系

| 已有功能 | 可复用点 |
|---------|---------|
| `_mergedAsts` via Preprocessor（DX4-P3） | 合并 AST 已有，可扩展为依赖图数据源 |
| `IFileResolver.ResolveFilePath`（DX4-P0） | include 路径解析已完善 |
| `IsCrossFileError`（DX8） | 错误隔离基础 |
| `ScanWorkspaceForRenames`（DX6） | 磁盘扫描模式可参考，但应由依赖图替代全量扫描 |
| `willRenameFiles`（DX6） | 继续使用，但需配合状态更新 |
| `didClose`（E003） | ✅ 已实现 |
