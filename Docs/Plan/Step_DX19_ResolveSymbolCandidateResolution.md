# DX19: ResolveSymbol 候选仲裁修复（同位置信息冲突）

> 前置：DX18 ✅ 统一引用收集  
> 讨论来源：[D_ResolveSymbolCollisionTopDown.md](../Discussion/D_ResolveSymbolCollisionTopDown.md)

## 一、目标

在保留长期测试 `VERIFY-01` 的前提下，修复 `ResolveSymbol` 在 include/main 同 line+col 碰撞场景中的误解析问题。  
采用“自上而下”的统一仲裁策略，确保 Definition/References/Rename/Hover 全链路语义一致。

## 二、推进清单（Checklist）

### Phase 0：流程固化（本轮已完成）

- [x] P0-1：固化讨论文件（D20）
- [x] P0-2：向串行需求列表提交 DX19
- [x] P0-3：创建 DX19 子计划 checklist 文件

### Phase 1：长期测试护栏

- [ ] P1-1：确认 `VERIFY-01` 作为长期回归测试保留
- [ ] P1-2：补齐 collision 变体矩阵（同名同位、局部遮蔽、仅跨文件定义）
- [ ] P1-3：验证 Definition/References/Rename/Hover 在同一光标点语义一致

### Phase 2：ResolveSymbol 顶层仲裁改造

- [ ] P2-1：定义候选仲裁规则（文件一致性、作用域一致性、符号完整性、位置置信度）
- [ ] P2-2：重构 ResolveSymbol：per-file/merged 候选统一收集与排序
- [ ] P2-3：移除“变量符号强制 merged fallback 覆盖”路径
- [ ] P2-4：为跨文件 fallback 增加显式触发条件（仅必要时接管）

### Phase 3：接入与回归

- [ ] P3-1：确保 HandleDefinition/HandleReferences/HandleRename/HandleHover 无分叉绕行
- [ ] P3-2：运行 LSP 测试并通过
- [ ] P3-3：运行全量测试并通过

## 三、完成标准

- `VERIFY-01` 及新增 collision 用例全部通过。
- 同一光标点在 Definition/References/Rename/Hover 的解析目标一致。
- 不退化现有跨文件导航能力。

