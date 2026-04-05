# B-δ5 C5 Cleanup 超时保护

> 状态：✅ 完成
> 依赖：无
> 来源：[VM_Summary.md §七](../VM_Summary.md) B-δ5 + [Outlook_And_Risks.md §C5](Outlook_And_Risks.md)
> 完成测试数：1007 Assert × 2 模式全通过

---

## 一、目标

防止 Cleanup 块内死循环阻塞实例回收。当某个 cleanup 块执行步数超限时：
- **跳过当前 cleanup 块**，继续执行剩余 cleanup 块（LIFO 顺序不变）
- **保证实例最终进入 Completed 状态**，不会永久卡住
- **记录超时信息**（不 panic，因为其他 cleanup 块可能正常）

## 二、妥协点

| 妥协 | 原因 | 消除时间点 |
|------|------|-----------|
| 超时粒度为步数而非时间 | 与 MaxStepsPerTick 一致，确定性可回放 | 永久妥协（确定性是硬约束） |
| 超时的 cleanup 块内 Syscall 可能未执行 release | 超时意味着 cleanup 块本身有 bug（死循环），跳过是最安全选择 | 永久妥协 |

## 三、子任务 Checklist

### 3.1 核心实现

- [x] **C5-1**: VMWorld 新增 `MaxCleanupSteps` 字段（默认 256）
- [x] **C5-2**: ExecuteInstance 中，当进入 cleanup 执行时，追踪 cleanup 专用步数计数器
- [x] **C5-3**: 当 cleanup 步数超限时，跳过当前 cleanup 块，进入下一个 cleanup 块
- [x] **C5-4**: 如果所有 cleanup 块都处理完（含超时跳过），实例进入 Completed 状态
- [x] **C5-5**: 新增 `VMError.WarnCleanupTimeout`（软警告，不阻止 Completed）

### 3.2 测试

- [x] **C5-T1**: cleanup 块内死循环 → 超时跳过 → 实例 Completed
- [x] **C5-T2**: 多个 cleanup 块，第一个超时，第二个正常执行
- [x] **C5-T3**: 正常 cleanup 块不受影响（步数在限制内）
- [x] **C5-T4**: cleanup 超时 + wait_for 依赖方正确恢复

---

## 四、完成条件

1. ✅ MaxCleanupSteps 可配置
2. ✅ cleanup 超时跳过 → 继续剩余 cleanup → Completed
3. ✅ C5-T1~T4 测试通过
4. ✅ 全部 1007 Assert × 2 模式全通过（无回退）

---

## 五、功能展望

| ID | 说明 | 优先级 |
|----|------|--------|
| C5-F1 | 超时时记录具体 cleanup 块来源（defer/using 行号） | 低 |
| C5-F2 | 允许运行时动态调整 MaxCleanupSteps（如 debug 模式放大） | 低 |

## 六、风险点

| 风险 | 缓解 |
|------|------|
| C6 合并相邻 defer 可能导致超时跳过整个合并块 | 已在 C5-02 测试中验证；文档明确"超时粒度是 cleanup 块" |
