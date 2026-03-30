# 曳光弹执行计划 (Tracer Bullet Checklist)

> **在整体计划中的位置**：本计划对应 VM_Summary.md §七 推进顺序的步骤 1-6。
> 曳光弹是整个 VM 工程的**物理验收门槛**：只有本计划全部通过，才允许进入后续的 OpCode 扩展（MOVE/JUMP）、语法设计（Lexer/Parser）、UI 编辑器等工作。

---

## 整体阶段中的临时妥协

以下妥协在本计划范围内生效，不影响曳光弹验证目标：

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| 手写 AST，无 Lexer/Parser | 先验证 VM 物理闭环；Parser 依赖语法设计稳定 | 步骤 8（VM_Summary §七） |
| TreeWalker 用异常做控制流信号 | Phase 2 原型，字节码阶段用 IP 跳转替代 | 步骤 3-5 的字节码路径 |
| 开发期 Number 用 float | Fix64 调试痛苦；float 仅用于迭代 | 正式测试/上线构建启用 `USE_FIXPOINT` |
| 无 `using` 语法（用 `defer` 等价） | `using` 需 Paired Syscall 协议 + 编译器支持 | 步骤 9（VM_Summary §七） |
| AST 中 `defer` 用最简实现 | 只需验证 Cleanup 栈物理正确性 | 编译器阶段实现完整作用域配对 |

---

## 步骤总览

```
Step 1: 扩展 VMInstanceState — 加入 StateFlags / Cleanup 栈
Step 2: 扩展 AST — 加入 DeferStmt 节点
Step 3: 扩展 TreeWalker — 支持 defer 和 Kill Cleanup 语义
Step 4: 编写 Phase A 验证测试（正常路径 + Kill 路径）
Step 5: 编写 Phase B 验证测试（Save/Load 一致性 + 0 GC 检查）
Step 6: 字节码层（OpCode 定义 + 编译器 + 解释循环）重新通过全部验证
```

---

## Step 1: 扩展 VMInstanceState

### 意图

VMInstanceState 当前缺少 `StateFlags`（区分 Active/Killed/InCleanup/Completed）和 `CleanupFrames[]`（固定深度 Cleanup 栈）。没有这些字段，强制 Kill 路径和 Cleanup 机制无法落地。

### 具体变更

- [x] 1.1 在 `VMConstants.cs` 中新增 `MaxCleanupDepth = 8`
- [x] 1.2 新增 `VMStateFlags` 枚举（`[Flags] byte`）：`None = 0`, `Active = 1`, `Killed = 2`, `InCleanup = 4`, `Completed = 8`
- [x] 1.3 新增 `CleanupFrame` 值类型结构体：`int CleanupEntryIP`（最小字段）
- [x] 1.4 新增 `CleanupFrames` 内联固定大小结构体（8 × `CleanupFrame`，含 `Get/Set` unsafe 访问器），与 `CallStackFrames` 同样模式
- [x] 1.5 在 `VMInstanceState` 中新增字段：`VMStateFlags StateFlags`、`byte CleanupDepth`、`CleanupFrames CleanupStack`
- [x] 1.6 编译通过，运行现有 14 项测试全部通过（新字段默认值不影响已有逻辑）

### 曳光弹范围内的妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| `CleanupFrame` 只含 `CleanupEntryIP` 一个字段 | 曳光弹不需要作用域基寄存器 | 字节码阶段按需扩展为 `{ CleanupEntryIP, BaseRegister }` |
| `StateFlags` 未被 `VMWorld.Tick()` 消费 | 本步只扩展数据结构，行为在 Step 3 补齐 | Step 3 |

### 验收标准

- `VMInstanceState` 仍为 `[StructLayout(LayoutKind.Sequential)]` 纯值类型
- 无托管字段
- 编译 0 error 0 warning
- 全部 14 项已有测试通过

---

## Step 2: 扩展 AST — DeferStmt

### 意图

AST 当前没有 `defer` 节点。TreeWalker 需要一个 `DeferStmt` 来注册 Cleanup 块。

### 具体变更

- [x] 2.1 在 `NodeKind` 枚举中新增 `Defer`
- [x] 2.2 新增 `DeferStmt : Stmt` 类：持有一个 `BlockStmt Body`（Cleanup 块内容）
- [x] 2.3 编译通过，运行现有 14 项测试全部通过

### 曳光弹范围内的妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| `DeferStmt` 只支持 `BlockStmt`，不支持单表达式 defer | 曳光弹只需块级 defer | Parser 阶段可扩展 |
| 不校验 defer 块内是否含 `wait`（语义上应禁止） | 曳光弹手写 AST 自行保证 | 编译器语义检查阶段 |

### 验收标准

- 编译 0 error
- 全部 14 项已有测试通过

---

## Step 3: 扩展 TreeWalker — defer 执行 + Kill Cleanup

### 意图

让 TreeWalker 支持：(a) 遇到 `DeferStmt` 时注册 Cleanup 块；(b) 函数正常结束时按 LIFO 执行所有已注册的 Cleanup 块；(c) 外力 Kill 时也执行 Cleanup 块。TreeWalker 是 Phase 2 原型，用异常/信号做控制流；字节码阶段会用 IP 跳转替代，但语义必须一致。

### 具体变更

- [x] 3.1 在 TreeWalker 中新增 `_deferStack`（`List<BlockStmt>`），在 `CallFunction` 入口清空
- [x] 3.2 `ExecuteStmt` 中新增 `DeferStmt` 分支：将 `Body` 压入 `_deferStack`，不立即执行
- [x] 3.3 在 TreeWalker 中新增 `ExecuteCleanups()` 方法：按 LIFO 顺序执行 `_deferStack` 中所有块，执行后清空栈
- [x] 3.4 `CallFunction` 正常结束路径（return 或函数体结束）增加 `ExecuteCleanups()` 调用
- [x] 3.5 新增 `Kill()` 公共方法：设置 killed 标志 → 调用 `ExecuteCleanups()` → 不再执行后续主流程
- [x] 3.6 编译通过，运行现有 14 项测试全部通过

### 曳光弹范围内的妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| `_deferStack` 使用 `List<BlockStmt>`（托管对象） | TreeWalker 本身是 Phase 2 原型，不要求零 GC；字节码阶段用定长 `CleanupFrames[]` | Step 6 字节码路径 |
| Kill 是同步方法调用，非 Tick 入口优先级检查 | TreeWalker 无 Tick 循环；字节码阶段 Kill 在 Tick 入口以最高优先级处理 | Step 6 |
| 不处理 Cleanup 执行期间再 throw 的情况 | 曳光弹 Cleanup 块只含 Syscall，不会抛异常 | 编译器语义检查 + VM 强制限制 Cleanup 块能力 |

### 验收标准

- 编译 0 error
- 全部 14 项已有测试通过
- 可手动构造含 `DeferStmt` 的 AST 并验证 `ExecuteCleanups` 被调用（在 Step 4 正式测试）

---

## Step 4: Phase A 验证测试

### 意图

这是曳光弹成立的硬门槛。必须验证：正常结束走 Cleanup、强制 Kill 走 Cleanup、Kill 优先于 Wait。

### 具体变更

编写以下测试，全部加入 `TreeWalkerTests.RunAll()`：

- [x] 4.1 **测试：defer 正常路径**
  - 构造 AST：`defer { syscall SetBlackboard(0) }; syscall SetBlackboard(1); return`
  - 注册 Syscall 记录调用历史
  - 断言：SetBlackboard 调用顺序为 `[1, 0]`（主流程先，Cleanup 后）
  - 断言：Cleanup 只执行一次

- [x] 4.2 **测试：defer + wait 正常路径**
  - 构造 AST：`defer { syscall SetBB(0) }; syscall SetBB(1); wait 10; syscall PlayEffect()`
  - 第一次 CallFunction 应抛 `WaitSignal(10)`
  - 断言：此时 SetBB(1) 已执行，SetBB(0) 和 PlayEffect 未执行
  - 模拟等待结束后继续执行（调用 resume/continue）
  - 断言：PlayEffect 执行
  - 断言：最终 Cleanup 执行（SetBB(0)）

- [x] 4.3 **测试：defer + wait + Kill 路径**
  - 构造同 4.2 的 AST
  - 第一次 CallFunction 抛 `WaitSignal(10)`
  - 不 resume，而是调用 `Kill()`
  - 断言：PlayEffect **不**执行
  - 断言：Cleanup 执行（SetBB(0)）
  - 断言：Cleanup 只执行一次

- [x] 4.4 **测试：多层 defer LIFO 顺序**
  - 构造 AST：`defer { A() }; defer { B() }; return`
  - 断言：执行顺序为 `[B, A]`（后注册先执行）

- [x] 4.5 全部 Phase A 测试通过，加上原有测试仍通过（共 31 个 Assert 全部通过）

### 曳光弹范围内的妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| 测试 4.2 的"resume"机制可能需要 TreeWalker 增加恢复支持 | TreeWalker 当前 wait 后无法原地恢复；可简化为两阶段调用 | Step 6 字节码路径天然支持 IP 恢复 |
| 如果 TreeWalker 无法简洁支持 wait 后恢复，测试 4.2 可拆为"wait 前"和"wait 后"两段独立验证 | 不影响 Phase A 核心验证目标（Cleanup 正确性） | 字节码阶段完整验证连续执行 |

### 验收标准

- 4.1、4.3、4.4 必须通过（不依赖 wait 恢复）
- 4.2 尽力通过；如因 TreeWalker 架构限制须简化，需记录并在 Step 6 补齐
- 全部已有测试仍通过

---

## Step 5: Phase B 验证测试

### 意图

Phase A 通过后立即验证 Save/Load 一致性和零 GC。这些不阻塞 Phase A 闭环，但必须在进入字节码阶段前确认结构层面的正确性。

### 具体变更

- [x] 5.1 **测试：Save/Load 含 Cleanup 栈**
  - 使用 `InstancePool` + `SnapshotRingBuffer`
  - 分配实例，手动设置 `StateFlags = Active`、`CleanupDepth = 1`、`CleanupStack[0].CleanupEntryIP = 42`
  - SaveState → 修改上述字段 → LoadState
  - 断言：所有字段恢复到保存时的值

- [x] 5.2 **测试：StateFlags 快照一致性**
  - 分配实例，设 `StateFlags = Killed`
  - SaveState → 改为 `Active` → LoadState
  - 断言：恢复后 `StateFlags == Killed`

- [x] 5.3 **0 GC 备注**
  - 当前 TreeWalker 本身使用托管对象（Environment、List、异常），零 GC 验证不适用于 TreeWalker 阶段
  - 在此步骤仅记录：**零 GC 验证推迟到 Step 6 字节码路径**
  - 添加注释标记 `// TODO: 0 GC regression test — bytecode phase`

### 曳光弹范围内的妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| 0 GC 不在 TreeWalker 阶段验证 | TreeWalker 是原型，允许 GC | Step 6 字节码路径 |
| Save/Load 只验证字段级正确性，不验证完整执行行为恢复 | 执行行为恢复需要字节码 VM 的 IP 驱动 | Step 6 |

### 验收标准

- 5.1 和 5.2 通过
- 全部已有测试仍通过

---

## Step 6: 字节码路径（完整曳光弹闭环）

> **注意**：Step 6 是最大的一步，内部再拆为 6a-6e 子步骤。每个子步骤独立可编译可测试。

### 意图

实现最小 7 指令字节码 VM，将曳光弹 AST 手工编译为字节码，用字节码解释循环重新通过全部 Phase A + Phase B 验证。这是曳光弹成立的**最终闭环**。

---

### Step 6a: OpCode 定义 + Instruction 结构

- [x] 6a.1 新增 `OpCode.cs`：枚举 `OpCode : byte`，含 `NOP`, `LOAD_CONST`, `SYSCALL`, `WAIT`, `PUSH_CLEANUP`, `POP_CLEANUP`, `RETURN`
- [x] 6a.2 新增 `Instruction` 值类型结构体：`OpCode Code; int A; int B; int C;`（固定宽度，值类型）
- [x] 6a.3 编译通过，已有测试通过

#### 妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| `Instruction` 使用 `int A/B/C`（16 字节/指令），不追求压缩 | 先追求可读可调试 | 优化阶段可切紧凑编码 |
| 只有 7 条指令 | 曳光弹不需要分支/循环/搬运 | 曳光弹后立即补 `MOVE` → `JUMP/JUMP_IF` |

---

### Step 6b: 最小 Program / ROM 结构

- [x] 6b.1 新增 `VMProgram` 类（ROM）：`Instruction[] Instructions`; `Number[] Constants`; `int RequiredRegisters`
- [x] 6b.2 新增 `VMModuleTable` 类：按 `moduleSlot` 存放 `VMProgram` 引用，固定大小 `MaxModules`
- [x] 6b.3 编译通过，已有测试通过

#### 妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| `VMProgram` 使用 class + 托管数组（`Instruction[]`） | ROM 是只读静态资产，不参与快照，允许托管 | 长期可考虑 NativeArray 但非必须 |
| 无调试符号 / 源码映射 | 曳光弹不需要 | Parser + 编译器阶段 |

---

### Step 6c: 字节码解释循环 (VMWorld.Tick 内核)

- [ ] 6c.1 在 `VMWorld` 中新增 `VMModuleTable ModuleTable` 字段
- [ ] 6c.2 重写 `VMWorld.Tick()` 中对每个 alive 实例的处理逻辑：

```
对每个 alive 实例：
  1. 如果 StateFlags 含 Completed → 跳过
  2. 如果 StateFlags 含 Killed 且不含 InCleanup：
     → 若 CleanupDepth > 0：设 InCleanup，IP 跳转到栈顶 Cleanup 入口
     → 否则：设 Completed，跳过
  3. 如果 WaitCounter > 0 且不含 Killed：
     → WaitCounter--，跳过
  4. 进入解释循环（限最大步数防死循环）：
     fetch Instruction[IP]
     switch opcode:
       NOP          → IP++
       LOAD_CONST   → Registers[A] = Constants[B]; IP++
       SYSCALL      → Syscalls.Invoke(A, ref inst); IP++
       WAIT         → WaitCounter = A; IP++; break 循环
       PUSH_CLEANUP → CleanupStack[CleanupDepth++] = { A }; IP++
       POP_CLEANUP  → CleanupDepth--; IP++
       RETURN       →
         if InCleanup:
           CleanupDepth--
           if CleanupDepth > 0: IP = CleanupStack[CleanupDepth-1].CleanupEntryIP
           else: 清除 InCleanup, 设 Completed; break
         else:
           if CleanupDepth > 0: 设 InCleanup, IP = CleanupStack[CleanupDepth-1].CleanupEntryIP
           else: 设 Completed; break
       default → ErrorFlag = PanicIllegalInstruction; break
```

- [x] 6c.3 编译通过
- [x] 6c.4 手工构造曳光弹字节码序列（对应 VM_OpCodes_Draft §八），通过 `VMWorld` 驱动执行，验证正常路径 Syscall 调用顺序

#### 妥协

| 妥协 | 理由 | 消除位置 |
|------|------|---------|
| 解释循环有最大步数上限（防无限循环） | 安全措施，曳光弹业务不会触及 | 长期可由编译器静态分析保证终止性 |
| `RETURN` 在 Cleanup 模式下的退栈策略为最简实现 | 曳光弹只有单层 defer | 多层 defer 时在后续功能测试中验证 |

---

### Step 6d: 字节码路径 Phase A 测试

- [x] 6d.1 手工编写曳光弹字节码（对应 `VM_OpCodes_Draft.md §八` 的示意序列）
- [x] 6d.2 **测试：正常路径**
  - SpawnInstance → Tick × 1（执行到 WAIT）→ Tick × 10（等待倒计时）→ Tick × 1（恢复后 PlayEffect + Cleanup）
  - 断言：Syscall 调用顺序 = `[SetBB(1), PlayEffect, SetBB(0)]`
  - 断言：StateFlags 最终含 `Completed`

- [x] 6d.3 **测试：Kill 路径**
  - SpawnInstance → Tick × 1（执行到 WAIT）→ 设 `StateFlags |= Killed` → Tick × 1
  - 断言：Syscall 调用顺序 = `[SetBB(1), SetBB(0)]`（无 PlayEffect）
  - 断言：StateFlags 最终含 `Completed`

- [x] 6d.4 **测试：Killed 优先级高于 WaitCounter**
  - SpawnInstance → Tick × 1（执行到 WAIT，WaitCounter = 10）→ 设 Killed → Tick × 1
  - 断言：不因 WaitCounter > 0 而跳过 Cleanup，直接进入 Cleanup 模式

- [x] 6d.5 **测试：多层 defer LIFO**
  - 手工构造含两个 `PUSH_CLEANUP` 的字节码
  - 断言：Cleanup 按后进先出执行

- [x] 6d.6 全部通过

---

### Step 6e: 字节码路径 Phase B 测试

- [x] 6e.1 **测试：Save/Load 后执行行为一致**
  - SpawnInstance → Tick × 1（执行到 WAIT）→ SaveState → Tick × 5 → LoadState → 继续 Tick 到结束
  - 断言：与未中断运行的结果完全一致（Syscall 调用序列、最终 StateFlags）

- [x] 6e.2 **测试：0 GC 回归**
  - 预热后，循环驱动曳光弹若干轮
  - 使用 `GC.GetTotalMemory(false)` 前后对比（或 `Profiler.GetMonoUsedSizeLong`）
  - 断言：字节码执行路径不产生托管堆分配
  - *注：Syscall 注册和 VMProgram 构造允许在预热中分配，只要 Tick 循环内零分配*

- [x] 6e.3 全部通过

---

## 曳光弹通过后的交接

当 Step 6e 全部通过时，曳光弹成立。此时：

- ✅ ROM/RAM 分离已验证
- ✅ wait 挂起/恢复已验证（字节码 IP 驱动）
- ✅ Cleanup 正常结束路径已验证
- ✅ Cleanup Kill 路径已验证
- ✅ Killed 优先级 > WaitCounter 已验证
- ✅ Save/Load 一致性已验证
- ✅ 零 GC 已验证

接下来按 VM_Summary §七 步骤 7 起继续：

```
→ 7. 补充 MOVE/COPY → JUMP/JUMP_IF → 比较/布尔
→ 8. 设计并确定脚本语法 → 实现 Lexer + Parser
→ 9. 实现 using 语法 + Paired Syscall → 理想 Cleanup 模式
→ 10. 编辑器流程图投影
```
