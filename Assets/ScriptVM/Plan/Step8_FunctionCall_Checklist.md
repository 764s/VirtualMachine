# 步骤 8：函数调用 + 固定深度调用栈验证

> **在整体计划中的位置**：本计划对应 VM_Summary.md §七 推进顺序的步骤 8。
> 步骤 7（using 语法 + Paired Syscall）已全部通过，237 项 Assert 通过。
> 本步骤实现用户函数调用（`CALL` / `RET_FUNC`），激活已预留的 CallFrame 基础设施。
> 来源：VM_Tracer_Bullet.md §十二 第 3 项；CallFrame 基础设施已在步骤 1 中就位。

---

## 整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| F4（寄存器生命周期分析 + 跨 await 变量提升）不在本步骤实现 | VM_Summary 明确标注"低优先级，最晚步骤 10 前" | 步骤 10 前 |
| 不支持递归调用 | 固定深度调用栈（MaxCallDepth=16）天然限制递归；本步骤先验证直接调用正确性 | 无需消除，递归受深度硬限保护 |
| 不支持闭包 / 高阶函数 | Architecture Rule 6 禁止寄存器持有托管引用；不符合 VM 物理约束 | 无需消除（设计决策） |
| 不支持跨模块函数调用 | 本步骤聚焦同模块内函数调用，跨模块依赖 ModuleTable 扩展 | 后续步骤按需扩展 |
| 函数参数上限 = 16（r0-r15 Scratch Zone） | 覆盖绝大多数场景；与 Syscall 参数传递一致 | 如需更多参数，后续扩展 |

---

## 现有基础设施盘点

以下组件在步骤 1-7 中已就位，步骤 8 直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `CallFrame` struct | ✅ 已定义 | `ReturnIP` + `ReturnModuleSlot` + `RegisterBase`（3×int，12 字节） |
| `CallStackFrames` | ✅ 已分配 | 16 槽预分配内联数组（`VMInstanceState` 内） |
| `CallStackDepth` 字段 | ✅ 已有 | `VMInstanceState` 内，初始 0 |
| `RegisterBase` 字段 | ✅ 已有 | `VMInstanceState` 内，支持寄存器窗口偏移 |
| `MaxCallDepth = 16` | ✅ 已定义 | `VMConstants.cs` |
| `FuncDecl` AST 节点 | ✅ 已有 | 含 Name、Parameters、ReturnType、Body |
| `CallExpr` AST 节点 | ✅ 已有 | 含 FunctionName、Arguments |
| Parser `FuncDecl` 解析 | ✅ 已有 | `func name(params) : returnType { body }` |
| Parser `CallExpr` 解析 | ✅ 已有 | `name(args)` → `CallExpr` 节点 |
| Snapshot memcpy | ✅ 自动覆盖 | CallStack 内联于 VMInstanceState，无额外逻辑 |

**Step 8 需要新增的部分**：OpCode 定义（F1）、VMWorld.Tick 分发（F1）、编译器函数表 + CALL emit（F2）、验证测试（F3）。

---

## 子任务总览

```
Sub-task A: CALL / RET_FUNC OpCode 定义 + VMWorld.Tick 分发（F1）
Sub-task B: VMProgram 函数表扩展
Sub-task C: 编译器函数调用 emit（F2）
Sub-task D: 端到端测试 + 回归验证（F3）
Sub-task E: 文档更新
```

依赖关系：`A` → `C`（需要新 OpCode）；`B` → `C`（需要函数表）；`A` 与 `B` 可并行；`D` 依赖全部完成。

---

## Sub-task A: CALL / RET_FUNC OpCode + VMWorld.Tick 分发（F1）

### 意图

新增两个 OpCode 使 VM 具备函数调用能力。`CALL` 将当前执行上下文压栈并跳转到目标函数；`RET_FUNC` 弹出上下文恢复调用方执行。区别于 `RETURN`（终止实例 / 进入 Cleanup 链）。

### 具体变更

- [ ] A.1 **OpCode.cs**：新增 `CALL`（建议编号 60）和 `RET_FUNC`（建议编号 61），归入新的 Phase 3 区段
  - `CALL`：A = 目标函数入口 IP，B = 参数数量
  - `RET_FUNC`：A = 返回值所在寄存器（0 表示无返回值或已在 r0）

- [ ] A.2 **VMWorld.cs — ExecuteInstance()**：在 switch 中添加 `case OpCode.CALL`：
  - 检查 `CallStackDepth < MaxCallDepth`，否则 → `VMError.StackOverflow`
  - 保存当前 CallFrame：`ReturnIP = IP + 1`，`ReturnModuleSlot = 当前 ModuleSlot`，`RegisterBase = 当前 RegisterBase`
  - Push CallFrame → `CallStackFrames.Set(CallStackDepth, frame)`
  - `CallStackDepth++`
  - 设置新 `RegisterBase`（偏移 = 旧 base + 该函数所需本地寄存器数，或固定窗口大小）
  - `IP = inst.A`（跳转到目标函数入口）
  - 不 `IP++`

- [ ] A.3 **VMWorld.cs — ExecuteInstance()**：在 switch 中添加 `case OpCode.RET_FUNC`：
  - `CallStackDepth--`
  - 弹出 CallFrame：`CallStackFrames.Get(CallStackDepth)`
  - 恢复 `IP = frame.ReturnIP`
  - 恢复 `RegisterBase = frame.RegisterBase`
  - 如有返回值：将 callee r0 的值写入 caller 需要的寄存器（通过约定或 CALL 指令的 C 操作数）
  - 不 `IP++`（ReturnIP 已是正确的下一条指令）

- [ ] A.4 **VMWorld.cs — RETURN 处理**：确认 `RETURN` 路径在 `CallStackDepth > 0` 时应退化为 `RET_FUNC` 语义（或要求编译器在函数末尾生成 `RET_FUNC` 而非 `RETURN`）

- [ ] A.5 **StackOverflow 保护测试**：编写测试验证 `CallStackDepth >= MaxCallDepth` 时触发 `VMError.StackOverflow`

### 验收标准

- `CALL` 正确压栈 + 跳转，`RET_FUNC` 正确弹栈 + 恢复
- 调用深度超限触发 `VMError.StackOverflow`
- 寄存器窗口在 CALL/RET_FUNC 后正确偏移/恢复
- 0 GC（全部操作在预分配结构上执行）

---

## Sub-task B: VMProgram 函数表扩展

### 意图

当前 `VMProgram` 只存储单一函数的字节码。支持多函数后，需要函数表将函数名映射到字节码入口 IP，使编译器和运行时能解析函数调用目标。

### 设计方案

函数表作为 `VMProgram` 的一部分，在编译期静态构建：

```
FunctionEntry:
    Name: string           // 函数名
    EntryIP: int           // 函数第一条指令在 Instructions[] 中的索引
    ParamCount: int        // 参数数量
    LocalRegCount: int     // 本地寄存器需求（用于计算 RegisterBase 偏移）
```

### 具体变更

- [ ] B.1 **VMProgram.cs**：新增 `FunctionEntry` struct（Name, EntryIP, ParamCount, LocalRegCount）
- [ ] B.2 **VMProgram.cs**：新增 `FunctionEntry[] Functions` 字段（或 `Dictionary<string, FunctionEntry>`），在构造时传入
- [ ] B.3 **VMProgram.cs**：新增 `TryGetFunction(string name, out FunctionEntry entry)` 查询 API
- [ ] B.4 **确认 entry 函数处理**：main / entry 函数也在函数表中，EntryIP = 0（或由编译器指定）

### 验收标准

- 函数表可存储多个函数的入口信息
- 可通过名称查找函数入口
- 不破坏现有单函数编译路径（向后兼容）

---

## Sub-task C: 编译器函数调用 emit（F2）

### 意图

BytecodeCompiler 当前将所有 `CallExpr` 视为 Syscall。本步骤使编译器能区分用户函数调用与 Syscall，对用户函数 emit `CALL` 指令。

### 设计

编译流程：
1. 编译器接收整个模块的 `FuncDecl` 列表
2. **第一遍**：扫描所有 FuncDecl，建立函数名 → 占位入口的映射
3. **第二遍**：逐函数编译字节码，entry 函数先编译，其余函数紧随其后
4. 在每个函数末尾 emit `RET_FUNC`（非 entry 函数）或 `RETURN`（entry 函数）
5. 遇到 `CallExpr` 时：
   - 查找函数表 → 如果是用户函数 → emit 参数到 scratch zone → emit `CALL`
   - 查找 Syscall 表 → 如果是 Syscall → emit `SYSCALL`（现有路径）
   - 两者都找不到 → 编译错误

### 具体变更

- [ ] C.1 **BytecodeCompiler.cs**：新增 `_functionTable: Dictionary<string, int>`（函数名 → 入口 IP 占位）
- [ ] C.2 **BytecodeCompiler.cs**：修改 `CompileModule()` —— 第一遍扫描 FuncDecl 注册函数名，第二遍逐函数编译
- [ ] C.3 **BytecodeCompiler.cs**：新增 `CompileFunction(FuncDecl)` —— 编译单个函数体：
  - 重置寄存器分配状态（每个函数独立分配）
  - 为参数绑定寄存器（r0..rN → 拷贝到本地寄存器 r16+，或直接使用）
  - 编译 body
  - emit `RET_FUNC`（非 entry 函数）
  - 记录函数的 LocalRegCount
- [ ] C.4 **BytecodeCompiler.cs**：修改 `CompileCallExpr()` —— 区分用户函数 vs Syscall：
  - 若 `_functionTable.ContainsKey(name)` → emit 参数 → emit `CALL(entryIP, argCount)`
  - 否则 → 走现有 Syscall 路径
- [ ] C.5 **BytecodeCompiler.cs**：参数传递 —— caller 将参数编译到 scratch zone（r0..r15），CALL 后由 callee 的参数绑定读取
- [ ] C.6 **BytecodeCompiler.cs**：返回值处理 —— callee 将返回值放入 r0（与 Syscall 一致），caller 在 CALL 后从 r0 读取
- [ ] C.7 **BytecodeCompiler.cs**：更新 `Compile()` 输出 —— 将 `FunctionEntry[]` 传入 `VMProgram` 构造

### 验收标准

- 多函数模块可被编译为单一 VMProgram（多段字节码 + 函数表）
- `CallExpr` 根据目标名称正确区分用户函数 / Syscall
- 参数传递和返回值通过 scratch zone（r0-r15）
- 现有单函数编译 + Syscall 路径无回归

---

## Sub-task D: 端到端测试 + 回归验证（F3）

### 意图

确保函数调用端到端正确，包括 GC 零分配验证和快照回滚验证。

### 具体变更

- [ ] D.1 **测试 F-01**：基本函数调用 —— `func add(a, b) { return a + b }` → main 调用 `add(3, 4)` → 验证结果 7
- [ ] D.2 **测试 F-02**：多函数调用链 —— `func a() { return b() + 1 }` `func b() { return 42 }` → 验证结果 43
- [ ] D.3 **测试 F-03**：函数内局部变量 —— 验证 caller 和 callee 的局部变量不互相干扰（寄存器窗口隔离）
- [ ] D.4 **测试 F-04**：调用深度保护 —— 递归调用超过 MaxCallDepth=16 → 触发 `VMError.StackOverflow`
- [ ] D.5 **测试 F-05**：函数 + Syscall 混合 —— 同一模块中用户函数调用和 Syscall 共存，互不干扰
- [ ] D.6 **测试 F-06**：函数 + wait —— 函数内执行 `wait` 后恢复，调用栈状态正确
- [ ] D.7 **测试 F-07**：函数 + defer/using —— 函数内使用 `defer` 或 `using`，cleanup 在函数返回时正确执行
- [ ] D.8 **测试 F-08**：GC 零分配验证 —— 函数调用路径 0 GC（`GC.GetAllocatedBytesForCurrentThread()` 前后对比）
- [ ] D.9 **测试 F-09**：快照回滚验证 —— 在函数调用中途 SaveState → 修改状态 → LoadState → 验证 CallStack 正确恢复
- [ ] D.10 运行全部现有测试（237 项 Assert），确认无回归
- [ ] D.11 运行 B01-B05 性能基准，确认无性能退化

### 验收标准

- 全部新增测试通过
- 现有 237 项 Assert 无回归
- 函数调用路径 0 GC
- 快照回滚正确恢复调用栈
- B01-B05 性能基准 ±10% 以内

---

## Sub-task E: 文档更新

### 意图

更新 VM_Summary.md 反映步骤 8 完成状态。

### 具体变更

- [ ] E.1 **VM_Summary.md §七**：步骤 8 标记 ✅，补充 F1-F3 通过信息
- [ ] E.2 **VM_Summary.md**：更新 OpCode 表（新增 CALL、RET_FUNC）
- [ ] E.3 **VM_Summary.md §十二**：更新测试断言总数
- [ ] E.4 **VM_Summary.md**：更新"已完成/未完成"表格（F1-F3 → ✅）

### 验收标准

- VM_Summary.md 准确反映步骤 8 完成后的项目状态

---

## 寄存器窗口策略（设计备忘）

函数调用的核心挑战是寄存器隔离。方案：

```
调用前（caller 视角）：
  RegisterBase = 0
  r0-r15:  scratch zone（参数/返回值）
  r16-r47: caller 本地变量
  r48-r63: caller 临时寄存器

CALL 后（callee 视角）：
  RegisterBase = caller_base + caller_local_count  (或固定步长)
  r0-r15:  scratch zone（接收参数 / 写返回值）—— 物理上是 caller 的 r0-r15
  r16-r47: callee 本地变量 —— 物理上是 caller 的 rN+16..rN+47
  r48-r63: callee 临时寄存器
```

**关键约束**：MaxRegisters = 64，MaxCallDepth = 16。如果每次调用偏移 32 个寄存器（本地区），64 / 32 = 2 层就溢出。

**可行方案**：
- **方案 A（共享 scratch + 独占 local）**：scratch zone r0-r15 不偏移（全局共享，用于参数传递），local zone 按需偏移。需要 caller-save 约定。
- **方案 B（全量偏移，扩大寄存器空间）**：增加 MaxRegisters 到 256+，每层窗口 32 寄存器，支持 8 层。需要修改 VMInstanceState 内存布局。
- **方案 C（不使用寄存器窗口，改用显式 save/restore）**：CALL 时将 caller 的活跃寄存器保存到 CallFrame 附加区域，RET_FUNC 时恢复。CallFrame 需扩展。

> **建议**：实施时优先评估方案 A（最小变更），如不满足再考虑方案 B/C。具体方案在实施时根据 Architecture Rules 裁决确定。

---

## 工作量预估

| Sub-task | 预估工作量 | 说明 |
|----------|-----------|------|
| A: CALL/RET_FUNC OpCode + Tick 分发 | 小-中 | 2 个 OpCode + 2 个 case 分支 + 寄存器窗口逻辑 |
| B: VMProgram 函数表 | 小 | 1 个 struct + 1 个数组 + 1 个查询 API |
| C: 编译器函数调用 emit | 中-大 | 多函数编译、函数解析、CALL emit、参数传递 — 核心工作量 |
| D: 端到端测试 + 回归 | 中 | 9+ 个新测试 + 回归验证 + GC/快照验证 |
| E: 文档更新 | 极小 | 更新 VM_Summary.md |

**总体评估**：任务量中等偏大。建议按 A + B（并行）→ C → D → E 的顺序推进。核心风险在寄存器窗口策略选择（Sub-task A 设计备忘），需在实施前确认方案。
