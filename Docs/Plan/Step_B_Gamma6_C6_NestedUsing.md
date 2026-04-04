# B-γ6: C6 嵌套 using 作用域优化（合并相邻 PUSH_CLEANUP）

**状态**: ⏳ → 执行中  
**依赖**: 无  
**完成条件**: 合并相邻 PUSH_CLEANUP 指令 + 性能验证 + 测试通过

---

## 设计概要

当多个 `defer` 或 `using` 在同一作用域连续声明时，编译器生成的 PUSH_CLEANUP 指令相邻。
优化：将相邻的 PUSH_CLEANUP 合并为一个，减少 cleanup stack 占用和指令数。

### 合并策略

**检测**：在 cleanup block 发射阶段，识别 PushCleanupIP 连续的 DeferredCleanup 组。

**合并规则**：
- 组内保留最后一个 PUSH_CLEANUP（LIFO 顺序中最先执行）
- 其余 PUSH_CLEANUP 改写为 MOVE r0,r0（peephole P1 消除）
- cleanup blocks 按 LIFO 顺序发射（组内倒序），中间不插 RETURN
- 仅最后一个 cleanup block 尾部加 RETURN

**安全限制**：
- 含 ReturnStmt 的 defer body 不参与合并（会跳过后续 cleanup）

### 收益

- 减少 cleanup stack 深度（MaxCleanupDepth=8 更不易溢出）
- 减少指令数（少 N-1 个 PUSH_CLEANUP + N-1 个 RETURN）
- 运行时少 N-1 次 push/pop 操作

---

## 子任务 Checklist

- [ ] P1: CompileFunction cleanup emission — 识别相邻 PUSH_CLEANUP 组并合并
- [ ] P2: AST 检查 — ReturnStmt 存在性检测（安全限制）
- [ ] P3: 测试 — 嵌套 using、连续 defer、混合场景
- [ ] P4: 性能验证 — benchmark 无回退
- [ ] P5: 运行全部测试
- [ ] P6: 更新文档
