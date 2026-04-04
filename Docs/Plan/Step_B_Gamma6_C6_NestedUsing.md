# B-γ6: C6 嵌套 using 作用域优化（合并相邻 PUSH_CLEANUP）

**状态**: ✅ 完成  
**依赖**: 无  
**完成条件**: 合并相邻 PUSH_CLEANUP 指令 + 性能验证 + 测试通过  
**结果**: 850 项 Assert × 2 模式全通过（+35 新 Assert C6-01~C6-05）

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

- [x] P1: CompileFunction cleanup emission — 识别相邻 PUSH_CLEANUP 组并合并
- [x] P2: AST 检查 — ReturnStmt 存在性检测（安全限制）
- [x] P3: 测试 — C6-01~C6-05（嵌套 using、连续 defer、混合场景、kill path）
- [x] P4: 性能验证 — benchmark 无回退
- [x] P5: 运行全部测试（850 × 2 模式）
- [x] P6: 更新文档

---

## 实现详情

### 编译器改动 (BytecodeCompiler.cs)

1. **EmitDeferredCleanups()**: 新方法，替代原有的 inline cleanup emission
2. **相邻组检测**: 按 PushCleanupIP 连续性分组
3. **合并策略**: 
   - 组内保留最后一个 PUSH_CLEANUP
   - 其余改写为 MOVE r0,r0（peephole P1 消除）
   - cleanup blocks 按 LIFO 顺序发射（组内倒序），无中间 RETURN
4. **安全限制**: ContainsReturn() 递归检查 defer body，含 ReturnStmt 则不合并

### 测试覆盖

| ID | 场景 | 验证 |
|----|------|------|
| C6-01 | 2 个连续 defer | 合并为 1 PUSH_CLEANUP + LIFO 顺序正确 |
| C6-02 | 3 个连续 defer | 合并为 1 PUSH_CLEANUP |
| C6-03 | 嵌套 using（非相邻） | 不合并 + 正常 POP_CLEANUP |
| C6-04 | defer + using 混合 | defer 合并 + using 独立 = 2 PUSH_CLEANUP |
| C6-05 | kill path + merged defers | compound cleanup 在 kill 时完整执行 |

---

## 功能展望

| ID | 描述 | 触发时机 |
|----|------|----------|
| C6-F1 | 嵌套 using 的 PUSH_CLEANUP 合并（当前 SYSCALL 阻隔导致非相邻） | 如引入"批量 acquire"语法 |

## 优化展望

| ID | 描述 | 触发时机 |
|----|------|----------|
| C6-O1 | 编译器层面标记 compound cleanup，运行时直接跳转而非 RETURN chain | 性能分析发现 cleanup chain 是瓶颈时 |

## 风险点

无新增风险。

## 妥协记录

| 项目 | 妥协内容 | 原因 | 消除时间点 |
|------|----------|------|------------|
| using 不合并 | 嵌套 using 的 PUSH_CLEANUP 因 SYSCALL 间隔不相邻，不触发合并 | 合并 using 需改变 acquire 时序，语义复杂 | C6-F1 如有需求 |
| 含 return 的 defer | defer body 含 ReturnStmt 时不参与合并 | RETURN 会中断 compound block 执行 | 如禁止 defer 内 return 则自动消除 |
