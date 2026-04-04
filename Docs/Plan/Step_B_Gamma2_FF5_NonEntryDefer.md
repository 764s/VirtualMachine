# B-γ2: FF5 非 entry 函数 defer（Non-Entry Function Defer）

> **目标**：RET_FUNC 执行前检查当前函数的 Cleanup 链深度，
> 在返回调用者之前执行所有函数作用域内的 defer/using cleanup 块。
> 同时保护返回值不被 cleanup 块覆盖，Kill 路径正确逐层清理。

## 一、背景

**现状**（FF5 之前）：
- defer/using 的 cleanup 由 RETURN 指令触发，仅在 entry 函数中正确执行
- RET_FUNC 直接弹出 CallFrame 返回调用者，跳过所有 pending cleanup
- CALL_LEAF/RET_LEAF 不保存 CleanupBase，无法感知函数级 cleanup 边界
- CallFrame.CleanupBase 字段已预留但 RET_FUNC 未使用

**方案**：
1. 编译器：含 defer/using 的函数不使用 leaf 优化（确保 CALL/RET_FUNC 路径）
2. RET_FUNC：检测 CleanupDepth > frame.CleanupBase → 进入 InCleanup 模式
3. RETURN（InCleanup）：按 CleanupBase 确定函数作用域边界，完成后弹出 CallFrame 返回调用者
4. r0 保护：RET_FUNC 进入 cleanup 前保存 r0，cleanup 完成后恢复（防止 syscall 参数覆盖返回值）
5. Kill 路径：cleanup 完成返回调用者时，若实例已 Killed 则停止执行，下一 Tick 处理父作用域 cleanup

## 二、子任务清单

| # | 子任务 | 说明 | 状态 |
|---|--------|------|------|
| 1 | `ContainsDeferOrUsing` | 编译器 AST 遍历，含 DeferStmt/UsingStmt 的函数标记为 non-leaf | ✅ |
| 2 | `AnalyzeLeafFunctions` 集成 | 在 leaf 分析中调用 ContainsDeferOrUsing 排除含 cleanup 的函数 | ✅ |
| 3 | RET_FUNC 修改 | 检测 CleanupDepth > CleanupBase → InCleanup + 保存 r0 | ✅ |
| 4 | RETURN InCleanup 修改 | 用 CleanupBase 划分函数作用域 cleanup 边界，完成后 pop 调用帧 | ✅ |
| 5 | r0 保护 | savedR0 局部变量在 RET_FUNC 保存、RETURN 恢复 | ✅ |
| 6 | Kill 路径 | 函数级 cleanup 完成返回时，若 Killed 则 return（停止执行） | ✅ |
| 7 | FF5-01~07 测试 | 基本 / 混合 / 嵌套 / using / kill / LIFO / 返回值 共 42 项 Assert | ✅ |
| 8 | 全量回归 | 763 项 Assert × 2 模式全通过 | ✅ |

## 三、设计细节

### 3.1 编译器变更

**ContainsDeferOrUsing(Stmt)**：递归 AST 遍历，检测 DeferStmt 或 UsingStmt。
若函数体包含 defer/using，`AnalyzeLeafFunctions` 将其标记为 `false`（non-leaf），
确保调用方使用 CALL（保存 CleanupBase）而非 CALL_LEAF。

### 3.2 RET_FUNC 变更

```
RET_FUNC:
    frame = CallStack[CallStackDepth - 1]
    if CleanupDepth > frame.CleanupBase:
        savedR0 = regs[0]           // 保护返回值
        InCleanup = true
        CleanupDepth--
        IP = CleanupStack[CleanupDepth].CleanupEntryIP
    else:
        CallStackDepth--
        IP = frame.ReturnIP
        RegisterBase = frame.RegisterBase
```

### 3.3 RETURN InCleanup 变更

```
RETURN (InCleanup):
    cleanupBase = (CallStackDepth > 0) ? CallStack[CallStackDepth-1].CleanupBase : 0

    if CleanupDepth > cleanupBase:
        // 更多函数作用域 cleanup（LIFO）
        CleanupDepth--
        IP = CleanupStack[CleanupDepth].CleanupEntryIP
    elif CallStackDepth > 0:
        // 函数级 cleanup 完成 → 返回调用者
        CallStackDepth--
        InCleanup = false
        IP = frame.ReturnIP
        RegisterBase = frame.RegisterBase
        regs[0] = savedR0            // 恢复返回值
        if Killed: return            // Kill 路径：停止执行
    else:
        // entry 函数 cleanup 完成 → Completed
        InCleanup = false
        Completed = true
```

### 3.4 Kill 路径

Kill → Tick 进入 InCleanup → 执行最内层函数 cleanup → RETURN 弹出 CallFrame 返回调用者 →
检测 Killed 停止执行 → 下一 Tick 重新检测 Killed + !InCleanup → 处理父函数 cleanup → 逐层展开直到 entry。

## 四、测试覆盖

| 测试 | 场景 | Assert 数 |
|------|------|-----------|
| FF5-01 | 非 entry 函数基本 defer | 5 |
| FF5-02 | entry + 非 entry 都有 defer | 6 |
| FF5-03 | 三层嵌套函数各自 defer | 8 |
| FF5-04 | 非 entry 函数中 using（配对 syscall） | 7 |
| FF5-05 | Kill 期间非 entry defer + 跨函数清理 | 5 |
| FF5-06 | 非 entry 函数中多个 defer（LIFO 顺序） | 6 |
| FF5-07 | 含 defer 的非 entry 函数返回值保护 | 4 |
| **合计** | | **42** (新增) |

## 五、风险消除

| 风险 ID | 内容 | 结果 |
|---------|------|------|
| R4 | CleanupBase 在函数边界的交互 | ✅ 已通过 FF5-02/03/05 验证 |
| FF5 | defer 在非 entry 函数中的正确执行 | ✅ 已实现并测试 |
