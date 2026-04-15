# DX19: ResolveSymbol 候选仲裁修复（同位置信息冲突）

> 前置：DX18 ✅ 统一引用收集
> 讨论来源：[D_ResolveSymbolCollisionTopDown.md](../Discussion/D_ResolveSymbolCollisionTopDown.md)

## 一、目标

在保留长期测试 `VERIFY-01` 的前提下，修复 `ResolveSymbol` 在 include/main 同 line+col 碰撞场景中的误解析问题。
采用"自上而下"的统一仲裁策略，确保 Definition/References/Rename/Hover 全链路语义一致。

## 二、推进清单（Checklist）

### Phase 0：流程固化

- [x] P0-1：固化讨论文件（D20）
- [x] P0-2：向串行需求列表提交 DX19
- [x] P0-3：创建 DX19 子计划 checklist 文件

### Phase 1：长期测试护栏

- [x] P1-1：确认 `VERIFY-01` 作为长期回归测试保留
- [x] P1-2：补齐 collision 变体矩阵（DX19-01~05：跨文件定义、局部遮蔽、4 操作一致性、跨文件结构体字段、跨文件模块变量）
- [x] P1-3：验证 Definition/References/Rename/Hover 在同一光标点语义一致（DX19-03）

### Phase 2：ResolveSymbol 顶层仲裁改造

- [x] P2-1：定义候选仲裁规则（文件一致性、作用域一致性、符号完整性、位置置信度）
- [x] P2-2：重构 ResolveSymbol：per-file 有完整作用域身份（scopeFunc+declLine>0）时保留，否则合并 AST 候选接管
- [x] P2-3：移除"变量符号强制 merged fallback 覆盖"路径（改为条件化触发）
- [x] P2-4：为跨文件 fallback 增加显式触发条件（仅 per-file 未命中、模块变量无作用域、StructField 无 parentName 时）

### Phase 3：接入与回归

- [x] P3-1：确保 HandleDefinition/HandleReferences/HandleRename/HandleHover 无分叉绕行（共享 ResolveSymbol）
- [x] P3-2：运行 LSP 测试并通过（675 通过，0 失败）
- [x] P3-3：运行全量测试并通过（2301 总计：114 TW + 1302 Compiler + 44 Perf + 18 FFS + 51 Debug + 97 DAP + 675 LSP）

## 三、完成标准

- ✅ `VERIFY-01` 及新增 collision 用例全部通过。
- ✅ 同一光标点在 Definition/References/Rename/Hover 的解析目标一致。
- ✅ 不退化现有跨文件导航能力。

## 四、实现摘要

### 核心变更（LspServer.cs `ResolveSymbol`）

将原来的"变量符号强制 merged AST 覆盖"改为"条件化候选仲裁"：

- **保留 per-file**：当 per-file AST 找到 Variable 且具有完整作用域身份（`scopeFunc != null && declLine > 0`），即局部变量有声明位置追踪，直接保留 per-file 结果。
- **merged 接管**：仅在以下条件触发 merged fallback：
  1. per-file 未命中（`resolvedTarget == null`）
  2. Variable 无作用域身份（模块变量或跨文件类型被错误解析为 Variable）
  3. StructField 无 parentName（需要跨文件结构体查找）

### 新增测试（LspTests.cs）

| 测试 | 内容 | 断言数 |
|------|------|--------|
| DX19-01 | 跨文件函数定义（仅 included 文件有定义） | 3 |
| DX19-02 | 局部变量遮蔽模块变量 | 2 |
| DX19-03 | 碰撞场景 Definition/References/Rename/Hover 语义一致性 | 6 |
| DX19-04 | 跨文件结构体字段访问（StructField parentName==null fallback） | 3 |
| DX19-05 | 跨文件模块变量定义 | 2 |
