# 步骤 7：using 语法 + Paired Syscall → 理想 Cleanup 模式

> **在整体计划中的位置**：本计划对应 VM_Summary.md §七 推进顺序的步骤 7。
> 步骤 6（Lexer + Parser + BytecodeCompiler）已全部通过，164 项 Assert 通过。
> 本步骤实现 `using` 语法 + 配对 Syscall 协议，使 Cleanup 从 `defer` 逃生舱升级为理想主要模式。
> 同时修复步骤 6 自审发现的 G1（wait_for 编译器缺口）和 G2（POP_CLEANUP 未被编译器生成）。

---

## 整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| C4（requires_cleanup 强制检查）不在本步骤实现 | VM_Summary 明确标注"低优先级，最晚步骤 10 前" | 步骤 10 前 |
| C5（Cleanup 超时保护）/ C6（嵌套 using 优化）暂不实现 | 展望项，暂无排期 | 待定 |
| `using` 块内不支持嵌套 `using`（本步骤） | 先验证单层 using 正确性 | C6 展望项 |
| Paired Syscall 仅支持"无参反向调用"模式 | 覆盖 80%+ 场景（SetBB/ResetBB, PlayEffect/StopEffect） | 如需带参反向调用，后续扩展 |

---

## 子任务总览

```
Sub-task A: wait_for 编译器缺口修复（G1）            ← 前置，独立于 using
Sub-task B: UsingStmt AST 节点定义
Sub-task C: Paired Syscall 注册协议（C2）
Sub-task D: Parser 解析 using 语法（C1）
Sub-task E: 编译器 emit using → PUSH_CLEANUP/POP_CLEANUP（C3 + G2 修复）
Sub-task F: 端到端测试 + 回归验证
Sub-task G: 文档更新
```

依赖关系：`A` 独立可先行；`B → D → E`；`C` 独立可与 B 并行；`F` 依赖全部完成。

---

## Sub-task A: wait_for 编译器缺口修复（G1）

### 意图

`wait_for` 在 Lexer（`TokenType.WaitFor`）和 AST（`WaitForStmt`）已就绪，VMWorld runtime 也已实现（检查目标实例完成），但 Parser 和 BytecodeCompiler 缺少接入。当前脚本无法使用 `wait_for(expr)` 语法。

### 具体变更

- [ ] A.1 **Parser**：在 `ParseStatement()` 的 switch 中添加 `case TokenType.WaitFor` → 调用 `ParseWaitFor()`
- [ ] A.2 **Parser**：实现 `ParseWaitFor()`：消费 `wait_for` → `(` → `ParseExpression()` → `)` → 返回 `WaitForStmt`
- [ ] A.3 **BytecodeCompiler**：在 `CompileStmt()` 的 switch 中添加 `case NodeKind.WaitFor` → 调用 `CompileWaitFor()`
- [ ] A.4 **BytecodeCompiler**：实现 `CompileWaitFor(WaitForStmt)`：编译目标表达式到寄存器 → emit `WAIT_FOR` 指令（需确认 OpCode 设计，或复用 WAIT + 扩展语义）
- [ ] A.5 **确认 OpCode 方案**：VMWorld 中 `wait_for` 使用 `WaitTargetInstanceId` 字段，需要新 OpCode `WAIT_FOR`（A=srcReg）设置此字段，或复用现有 WAIT 的扩展编码
- [ ] A.6 **测试**：编写 CompilerTest — `wait_for(instanceId)` 端到端编译 + 执行验证

### 验收标准

- `wait_for(expr)` 语法可被 Parser 解析为 `WaitForStmt` AST 节点
- BytecodeCompiler 可将 `WaitForStmt` 编译为正确的字节码序列
- 端到端测试通过：脚本中使用 `wait_for(id)` 可正确挂起并在目标完成后恢复

---

## Sub-task B: UsingStmt AST 节点定义

### 意图

`using` 语法需要对应的 AST 节点。设计为 `using SyscallName(args) { body }` 形式，编译器看到 `UsingStmt` 时自动生成 acquire SYSCALL + PUSH_CLEANUP（反向 SYSCALL）+ body + POP_CLEANUP。

### 具体变更

- [ ] B.1 **ASTNode.cs**：在 `NodeKind` 枚举中添加 `Using`
- [ ] B.2 **ASTNode.cs**：定义 `UsingStmt` 类：
  ```
  SyscallName: string       // acquire 调用名
  Arguments: List<Expr>     // 参数列表
  Body: BlockStmt           // using 块体
  ```
  继承 `Stmt`，`NodeKind.Using`

### 验收标准

- `UsingStmt` 为纯数据 AST 节点，无逻辑
- 编译通过，不破坏现有测试

---

## Sub-task C: Paired Syscall 注册协议（C2）

### 意图

SyscallTable 需要扩展，使 Syscall 可绑定反向操作。例如 `PlayEffect` 的反向操作是 `StopEffect`，`SetBB` 的反向操作是 `ResetBB`。编译器在处理 `using` 时查询配对表，自动 emit 反向 SYSCALL 到 Cleanup 块。

### 具体变更

- [ ] C.1 **SyscallTable.cs**：新增 `_pairedSlots` 数组（`int[]`，大小 = `MaxSyscalls`），每个 Syscall 可绑定其反向 Syscall 的 slot（-1 表示无配对）
- [ ] C.2 **SyscallTable.cs**：新增 `RegisterPaired(int acquireSlot, string acquireName, SyscallHandler acquireHandler, int releaseSlot, string releaseName, SyscallHandler releaseHandler)` API
- [ ] C.3 **SyscallTable.cs**：新增 `GetPairedSlot(int slot)` 查询 API，返回配对的 release slot（-1 表示无配对）
- [ ] C.4 **SyscallTable.cs**：新增 `HasPair(int slot)` 便捷查询
- [ ] C.5 **测试**：验证配对注册和查询的基本正确性

### 验收标准

- Paired Syscall 注册不影响现有的 `Register()` / `Invoke()` 流程
- 可通过 slot 查询配对的反向 slot
- 0 GC（`_pairedSlots` 预分配）

---

## Sub-task D: Parser 解析 using 语法（C1）

### 意图

Parser 支持 `using SyscallName(args) { body }` 语法。Lexer 已识别 `using` 关键字（`TokenType.Using`）。

### 设计

```
using_stmt = "using" IDENT "(" expr_list? ")" block ;
```

示例：
```
using PlayEffect(effectId, pos) {
    // ... 业务逻辑 ...
    wait 10
}
// 离开 using 块或被 Kill 时，自动调用 StopEffect
```

### 具体变更

- [ ] D.1 **Parser**：在 `ParseStatement()` 的 switch 中添加 `case TokenType.Using` → 调用 `ParseUsing()`
- [ ] D.2 **Parser**：实现 `ParseUsing()`：
  - 消费 `using`
  - 解析 Syscall 名称（`IDENT`）
  - 解析参数列表 `(` expr_list? `)`
  - 解析块 `{ ... }`
  - 返回 `UsingStmt`
- [ ] D.3 **测试**：Parser 单元测试 — 验证 `using PlayEffect(1) { wait 5 }` 解析为正确的 `UsingStmt` AST

### 验收标准

- `using SyscallName(args) { body }` 可被正确解析
- 错误恢复：缺少 `{` / `}` / `(` / `)` 时报有意义的错误
- 不影响 `defer` 和其他已有语法的解析

---

## Sub-task E: 编译器 emit using → PUSH_CLEANUP / POP_CLEANUP（C3 + G2）

### 意图

BytecodeCompiler 将 `UsingStmt` 编译为：
1. 编译参数 → 寄存器
2. emit `SYSCALL`（acquire，即 PlayEffect）
3. emit `PUSH_CLEANUP`（占位 → 指向 cleanup 块）
4. 编译 body
5. emit `POP_CLEANUP`（正常退出时弹出 cleanup frame）**← 修复 G2：POP_CLEANUP 首次被编译器生成**
6. 在函数末尾 emit cleanup 块：`SYSCALL`（release，即 StopEffect）→ `RETURN`

### 具体变更

- [ ] E.1 **BytecodeCompiler**：在 `CompileStmt()` 的 switch 中添加 `case NodeKind.Using` → 调用 `CompileUsing()`
- [ ] E.2 **BytecodeCompiler**：实现 `CompileUsing(UsingStmt)`：
  - 通过 Syscall 名称查找 acquire slot
  - 通过 `SyscallTable.GetPairedSlot()` 查找 release slot
  - 若无配对 → 编译错误（`using` 必须用于有配对的 Syscall）
  - 编译参数 → emit `SYSCALL`（acquire）
  - emit `PUSH_CLEANUP`（占位）→ 记录到 `_deferredCleanups`
  - 编译 body
  - emit `POP_CLEANUP`
  - cleanup 块（emit 在函数末尾）：emit `SYSCALL`（release）→ `RETURN`
- [ ] E.3 **编译器需要访问 SyscallTable 的名称→slot 映射**：确认 BytecodeCompiler 构造时可接收 SyscallTable 引用（或名称→slot 字典）
- [ ] E.4 **测试 C-U01**：基本 using — `using SetBB(key, 1) { wait 5 }` → 验证 acquire 执行 + 正常退出触发 POP_CLEANUP + cleanup 不执行
- [ ] E.5 **测试 C-U02**：Kill 路径 — `using SetBB(key, 1) { wait 100 }` → Kill → 验证 cleanup（ResetBB）执行
- [ ] E.6 **测试 C-U03**：using + defer 混合 — 验证 LIFO 顺序正确
- [ ] E.7 **测试 C-U04**：using 内嵌 wait — 验证 wait 恢复后 using 块正常继续

### 验收标准

- `using SyscallName(args) { body }` 端到端：source → AST → bytecode → 执行
- Kill 路径自动执行反向 Syscall
- 正常退出路径 POP_CLEANUP 正确弹出（G2 修复）
- using + defer 混合时 LIFO 顺序正确
- 0 GC（编译产物不引入托管分配）

---

## Sub-task F: 端到端测试 + 回归验证

### 意图

确保所有新功能通过端到端验证，且不破坏现有 185 项测试断言。

### 具体变更

- [ ] F.1 运行全部现有测试（185 项 Assert），确认无回归
- [ ] F.2 运行 B01-B05 性能基准，确认无性能退化
- [ ] F.3 汇总新增测试项数量，更新 VM_Summary.md §十二 测试数据

### 验收标准

- 现有 185 项 Assert 全部通过
- 新增测试全部通过
- B01-B05 性能基准无显著退化（±10% 以内）

---

## Sub-task G: 文档更新

### 意图

更新 VM_Summary.md 反映步骤 7 完成状态。

### 具体变更

- [ ] G.1 **VM_Summary.md §七**：步骤 7 标记 ✅，补充通过信息
- [ ] G.2 **VM_Summary.md §五**：更新已完成/未完成表格（C1/C2/C3 → ✅，G1/G2 → ✅ 已修复）
- [ ] G.3 **VM_Summary.md §十一**：G1/G2 标记为已修复
- [ ] G.4 **VM_Summary.md §十二**：更新测试断言总数

### 验收标准

- VM_Summary.md 准确反映步骤 7 完成后的项目状态

---

## 工作量预估

| Sub-task | 预估工作量 | 说明 |
|----------|-----------|------|
| A: wait_for 缺口修复 | 小 | Parser + Compiler 各加 1 个函数 + 可能需要新 OpCode + 1 个测试 |
| B: UsingStmt AST 节点 | 极小 | 1 个枚举值 + 1 个类定义 |
| C: Paired Syscall 协议 | 小 | SyscallTable 加 1 个数组 + 2-3 个 API |
| D: Parser using 语法 | 小-中 | 1 个解析函数 + 错误处理 |
| E: 编译器 using emit | 中 | 核心逻辑，需要 acquire/release 双路径 + POP_CLEANUP 首次使用 + 4 个测试 |
| F: 端到端回归 | 小 | 运行测试 + 基准 |
| G: 文档更新 | 极小 | 更新 VM_Summary.md |

**总体评估**：任务量适中，可在单次协作中完成。建议按 A → B+C（并行）→ D → E → F → G 的顺序推进。
