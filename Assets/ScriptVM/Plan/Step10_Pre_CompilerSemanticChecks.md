# 步骤 10 前置：编译器语义安全检查（C4 + G6）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中"步骤 10 前必须就位"的 C4、G5、G6 三项。
> **状态**：✅ 全部完成（315 项 Assert 通过，其中 12 项为新增 C4/G6 语义检查测试）。
> **前置**：步骤 9 已全部完成（303 项 Assert 通过）。
> **来源**：
> - C4 / G5：VM_Summary §3.3、§11.1 — 编译器 "requires cleanup" 强制检查
> - G6：VM_Summary §11.1 — `defer`/`using` Cleanup 块内禁止 `wait`/`wait_for`
>
> **核心原则**：这些是**编译期**安全屏障，确保脚本作者（人类或 AI）不会写出语义上非法的代码。
> 运行时行为不变；所有改动都在编译器的语义检查层。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| F4（寄存器生命周期分析）不在本步骤实现 | 复杂度为"中"，独立于语义安全检查 | 本步骤后单独评估 |
| V5（帧内 Profiler 验证）不在本步骤实现 | 依赖真实 Syscall 接入 ECS（前置条件未满足） | 真实 Syscall 接入后 |
| S4（结构体函数参数传递）不在本步骤实现 | 仅在编辑器需要展示结构体节点时才需要 | 步骤 10 中按需 |
| C5（Cleanup 块执行超时保护）是展望项 | 非步骤 10 前必须 | 展望 |
| R8（Cleanup 块内函数调用语义）仅做编译器禁止检查的基础设施 | 完整的语义规则在后续步骤精化 | 后续步骤 |

---

## 二、基础设施盘点

以下组件在步骤 1-9 中已就位，本步骤直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `SyscallTable` | ✅ 已有 | 支持 `RegisterPaired()`、`GetPairedSlot()`、`HasPair()` |
| `SyscallTable._pairedSlots` | ✅ 已有 | 配对 Syscall 关系映射 |
| `BytecodeCompiler._errors` | ✅ 已有 | `List<string>` 错误收集器 |
| `BytecodeCompiler.CompileUsing()` | ✅ 已有 | 编译 `using` 语句（SYSCALL + PUSH_CLEANUP + body + POP_CLEANUP） |
| `BytecodeCompiler.CompileDefer()` | ✅ 已有 | 编译 `defer` 语句（PUSH_CLEANUP） |
| `BytecodeCompiler._deferredCleanups` | ✅ 已有 | `List<DeferredCleanup>` 跟踪当前函数的 cleanup 信息 |
| `WaitStmt` / `WaitForStmt` AST | ✅ 已有 | Parser 已支持 `wait` / `wait_for` 语句解析 |
| `CompileWait()` / `CompileWaitFor()` | ✅ 已有 | 编译 `wait(N)` 和 `wait_for(id)` |

### 需要新增

| 组件 | 说明 |
|------|------|
| `SyscallTable._requiresCleanup[slot]` | 布尔标记：此 Syscall 是否强制要求 `using`/`defer` 包裹 |
| `SyscallTable.RequiresCleanup(slot)` | 查询方法 |
| `SyscallTable` 注册接口扩展 | `Register()` / `RegisterPaired()` 增加 `requiresCleanup` 参数（或独立的标记方法） |
| `BytecodeCompiler._inCleanupBlock` | 布尔标志：当前是否正在编译 cleanup 块内部（defer body / using release） |
| `BytecodeCompiler` 语义检查 — Syscall cleanup 强制 | 编译 `SYSCALL` 时检查是否需要 cleanup 包裹 |
| `BytecodeCompiler` 语义检查 — cleanup 块内 wait 禁止 | 编译 `wait`/`wait_for` 时检查 `_inCleanupBlock` |

---

## 三、子任务总览

```
Sub-task A: SyscallTable 扩展 requires_cleanup 标记
Sub-task B: 编译器 C4 — Syscall requires_cleanup 强制检查
Sub-task C: 编译器 G6 — Cleanup 块内禁止 wait/wait_for
Sub-task D: 端到端测试 + 回归验证
Sub-task E: 文档更新
```

依赖关系：`A → B`；`C` 独立于 A/B；`D` 依赖 B + C 完成；`E` 最后。

---

## Sub-task A: SyscallTable 扩展 requires_cleanup 标记

### 意图

当前 `SyscallTable` 只记录 Syscall 的 handler、name 和 paired 关系，但无法表达"此 Syscall 必须被 `using`/`defer` 包裹"的语义。
需要新增 `requires_cleanup` 布尔标记，允许注册时声明此约束。

### 具体变更

- [x] A.1 **SyscallTable.cs — 新增字段**：`bool[] _requiresCleanup`（与 `_handlers` 同长度），初始全 `false`
- [x] A.2 **SyscallTable.cs — 注册扩展**：`RegisterPaired()` 中，自动将 acquire 槽标记为 `requiresCleanup = true`（配对 Syscall 的 acquire 端天然需要 cleanup）
- [x] A.3 **SyscallTable.cs — 独立标记方法**：`void MarkRequiresCleanup(int slot)` — 允许非配对 Syscall 也可被标记为需要 cleanup（未来扩展点）
- [x] A.4 **SyscallTable.cs — 查询方法**：`bool RequiresCleanup(int slot)` — 编译器在 emit SYSCALL 前调用此方法
- [x] A.5 **测试**：验证 `RegisterPaired()` 后 `RequiresCleanup(acquireSlot)` 返回 `true`，`RequiresCleanup(releaseSlot)` 返回 `false`

### 验收标准

- `RegisterPaired()` 自动标记 acquire 端为 `requiresCleanup`
- `RequiresCleanup()` 正确查询
- 非配对 Syscall 默认 `requiresCleanup = false`
- 不破坏现有 SyscallTable 用法（向后兼容）

---

## Sub-task B: 编译器 C4 — Syscall requires_cleanup 强制检查

### 意图

当脚本中直接调用（非 `using` 包裹）一个标记为 `requires_cleanup` 的 Syscall 时，编译器应生成编译错误。
唯一合法的用法是在 `using SyscallName(args) { ... }` 或在 `defer { ... }` 之后调用。

### 设计考量

**检查时机**：在 `CompileSyscall()` / `CompileExprSyscall()` 中，emit `SYSCALL` 指令前。

**判断逻辑**：
- 如果当前 Syscall 的 slot 满足 `_syscallTable.RequiresCleanup(slot) == true`
- 并且当前调用**不是**来自 `CompileUsing()` 内部（using 包裹的 acquire 调用自带 cleanup）
- 则报错：`"Syscall '{name}' requires cleanup. Use 'using {name}(args) {{ ... }}' or wrap with 'defer'."`

**实现方式**：增加编译器内部标志 `_inUsingSyscall`（bool），在 `CompileUsing()` emit acquire SYSCALL 前设为 `true`，emit 后恢复 `false`。或者更简洁地：在 `CompileUsing()` 中不走通用的 `CompileSyscall()` 路径（当前实现已是如此——`CompileUsing()` 直接 emit SYSCALL），所以只需在通用 `CompileSyscall()` 中加检查。

**defer 情况**：`defer { SomeSyscall(args) }` — defer 块内的 Syscall 调用走通用 `CompileSyscall()` 路径。考虑到 defer 块本身就是 cleanup 代码，在 defer 块内调用 `requires_cleanup` 的 Syscall 是否合法？
- 分析：`requires_cleanup` 的语义是"此 Syscall 获取了需要释放的资源"。在 defer 块内获取资源是不合理的（defer 本身是释放路径）。
- 结论：**即使在 defer 块内，直接调用 requires_cleanup 的 Syscall 也应报错**。合法模式只有 `using`。
- 但如果 defer 块内调用的是 release 端（非 acquire），则不受限制。
- 简化规则：**只有 `using` 包裹的 acquire 调用免除 requires_cleanup 检查**。

### 具体变更

- [x] B.1 **BytecodeCompiler — CompileSyscall()**：在 emit `SYSCALL` 指令前，检查 `_syscallTable?.RequiresCleanup(slot) == true`，若是则 `_errors.Add(...)` 并跳过 emit
- [x] B.2 **BytecodeCompiler — CompileUsing()**：确认 `CompileUsing()` 的 acquire SYSCALL 不经过通用 `CompileSyscall()`（当前已如此，无需修改），即 `using` 包裹的调用天然免除检查
- [x] B.3 **测试 C4-01**：直接调用 `requires_cleanup` 的 Syscall → 编译报错，错误信息包含 syscall 名称和建议
- [x] B.4 **测试 C4-02**：`using` 包裹调用同一 Syscall → 编译成功，无错误
- [x] B.5 **测试 C4-03**：调用非 `requires_cleanup` 的普通 Syscall → 编译成功（不受影响）
- [x] B.6 **测试 C4-04**：`_syscallTable` 为 null 时（无 SyscallTable 传入）→ 跳过检查，编译成功（兼容旧用法）

### 验收标准

- `requires_cleanup` Syscall 在 `using` 外直接调用 → 编译报错
- `requires_cleanup` Syscall 在 `using` 内调用 → 编译成功
- 非 `requires_cleanup` Syscall 不受影响
- 无 SyscallTable 时不影响编译（向后兼容）
- 错误信息清晰、可操作

---

## Sub-task C: 编译器 G6 — Cleanup 块内禁止 wait/wait_for

### 意图

Cleanup 块（`defer` body / `using` release）在语义上是"实例退出前的清理代码"。
如果 Cleanup 块内执行 `wait(N)` 或 `wait_for(id)`，实例会挂起在清理路径中，阻塞实例回收，破坏 VM 生命周期语义。
编译器应在 Cleanup 块内遇到 `wait`/`wait_for` 时报错。

### 设计考量

**检查时机**：在 `CompileWait()` 和 `CompileWaitFor()` 中。

**实现方式**：新增编译器状态 `bool _inCleanupBlock`。
- 在 cleanup 块编译入口设为 `true`，出口恢复。
- Cleanup 块有两个入口：
  1. **defer body 编译**：在 `EmitDeferredCleanups()` 中编译 `DeferredCleanup.Body` 时
  2. **using release 编译**：在 `EmitDeferredCleanups()` 中 emit release SYSCALL 时（此处无用户代码，不需要检查）
- 实际上只有 **defer body** 可能包含用户编写的 `wait`/`wait_for`（using 的 release 是自动 emit 的 SYSCALL，不经过用户代码编译）。

**嵌套考虑**：如果 defer 块内嵌套了另一个 defer 或 using（虽然语义上不合理），`_inCleanupBlock` 应维持 `true`。这意味着不需要计数器，只需设置 `true` 即可（嵌套的 cleanup 块仍在外层 cleanup 块内）。

### 具体变更

- [x] C.1 **BytecodeCompiler — 新增字段**：`bool _inCleanupBlock = false`
- [x] C.2 **BytecodeCompiler — EmitDeferredCleanups()**：在编译 `DeferredCleanup.Body`（defer body）前设 `_inCleanupBlock = true`，编译后恢复为先前值
- [x] C.3 **BytecodeCompiler — CompileWait()**：在 emit `WAIT` 前检查 `_inCleanupBlock`，若 `true` 则 `_errors.Add("Cannot use 'wait' inside a cleanup block (defer/using)")` 并跳过 emit
- [x] C.4 **BytecodeCompiler — CompileWaitFor()**：同上，检查 `_inCleanupBlock`，报错 `"Cannot use 'wait_for' inside a cleanup block (defer/using)"`
- [x] C.5 **测试 G6-01**：`defer { wait(10) }` → 编译报错
- [x] C.6 **测试 G6-02**：`defer { wait_for(someId) }` → 编译报错
- [x] C.7 **测试 G6-03**：正常函数体内的 `wait(10)` → 编译成功（不受影响）
- [x] C.8 **测试 G6-04**：`using SomeSyscall(args) { wait(10) }` → 编译成功（using body 不是 cleanup 块，只有 release 路径是 cleanup 块）

### 验收标准

- `defer { wait(N) }` → 编译报错
- `defer { wait_for(id) }` → 编译报错
- 正常代码中的 `wait`/`wait_for` → 不受影响
- `using` body 内的 `wait` → 仍然合法（已有测试 T11 验证运行时正确性）
- 错误信息清晰、定位准确

---

## Sub-task D: 端到端测试 + 回归验证

### 意图

确保新增的语义检查正确工作，且不破坏现有 303 项 Assert。

### 具体变更

- [x] D.1 **CompilerTests — C4 系列**：新增 4 个测试（B.3–B.6），覆盖 requires_cleanup 的正确/错误路径
- [x] D.2 **CompilerTests — G6 系列**：新增 4 个测试（C.5–C.8），覆盖 cleanup 块内 wait 的正确/错误路径
- [x] D.3 **回归验证**：现有 303 项 Assert 全部通过无回归
- [x] D.4 **CI 验证**：确认 CI 工作流仍然通过（如果可触发）

### 验收标准

- 所有新增测试通过
- 所有现有 303 项测试无回归
- 新增 assert 数 ≥ 8（C4 × 4 + G6 × 4）

---

## Sub-task E: 文档更新

### 具体变更

- [x] E.1 **VM_Summary.md §七 推进顺序**：标记 C4 ✅、G6 ✅
- [x] E.2 **VM_Summary.md §11.1 代码缺口**：标记 G5 ✅ 已修复、G6 ✅ 已修复
- [x] E.3 **Outlook_And_Risks.md §一 确定执行**：更新 C4、G5、G6 状态为 ✅
- [x] E.4 **本文件**：更新状态为 ✅，记录最终测试数量
- [x] E.5 **VM_Summary.md**：更新总 Assert 数量

---

## 四、风险分析

| # | 风险 | 影响 | 缓解措施 |
|---|------|------|---------|
| R1 | `requires_cleanup` 标记向后兼容 — 现有测试中的 SyscallTable 使用需同步更新 | 低 | `RegisterPaired()` 签名不变，自动标记 acquire 端；现有测试无需修改 |
| R2 | `_inCleanupBlock` 状态在异常路径未恢复 | 低 | 使用 try/finally 或在 cleanup emit 循环中统一管理 |
| R3 | defer 块内嵌套 defer — `_inCleanupBlock` 语义是否正确 | 低 | 嵌套的 cleanup 块仍应标记为 `_inCleanupBlock=true`，当前设计满足 |
| R4 | using body vs using release 混淆 — using body 内的 wait 应合法 | 中 | 仅在 `EmitDeferredCleanups()` 中（编译 release 代码时）设 `_inCleanupBlock`；using body 的编译在 `CompileUsing()` 中，不受影响 |

---

## 五、验收总览

| 条目 | 来源 | 描述 |
|------|------|------|
| C4 | VM_Summary §3.3 / §11.1 G5 | requires_cleanup Syscall 强制 using 包裹 |
| G6 | VM_Summary §11.1 | Cleanup 块内禁止 wait/wait_for |
| 回归 | — | 现有 303 项 Assert 无回归 |

全部通过后，步骤 10 前必须就位项中的 C4、G5、G6 闭环。
剩余：F4（寄存器生命周期分析）待独立评估，V5（帧内 Profiler）待前置条件满足。

---

## 六、后续评估

本步骤完成后，需评估以下事项以决定是否直接进入步骤 10：

1. **F4（寄存器生命周期分析）**：是否真正阻塞编辑器流程图？如果编辑器初版不需要寄存器优化信息，F4 可延至步骤 10 后。
2. **V5（帧内 Profiler）**：前置条件（真实 Syscall 接入 ECS）何时就绪？
3. **S4（结构体函数参数）**：编辑器是否需要展示"struct 参数传递"节点？
