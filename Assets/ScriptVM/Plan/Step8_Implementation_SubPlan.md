# 步骤 8 实施子计划：函数调用 + 固定深度调用栈

> **前置文档**：[Step8_FunctionCall_Checklist.md](Step8_FunctionCall_Checklist.md)（设计级规格，含 Sub-task A-E 详细说明）
> **本文定位**：实施层面的状态确认 + 设计决策锁定 + 分阶段执行 checklist。

---

## 一、当前状态（起步前盘点）

### 已完成（步骤 1-7 期间）

- [x] `CALL = 60` OpCode 已定义（OpCode.cs Phase 3 区段）
- [x] `FunctionEntry` struct 已定义（VMProgram.cs — Name, EntryIP, ParamCount, LocalRegCount）
- [x] `FunctionEntry[] Functions` 字段已在 VMProgram 中就位，构造函数已支持可选传入
- [x] `TryGetFunction(string, out FunctionEntry)` 查询 API 已实现
- [x] `CallFrame` struct 已定义（ReturnIP, ReturnModuleSlot, RegisterBase, CleanupBase）
- [x] `CallStackFrames`（16 槽内联数组）已分配于 `VMInstanceState`
- [x] `CallStackDepth` 字段已存在于 `VMInstanceState`
- [x] `RegisterBase` 字段已存在于 `VMInstanceState`
- [x] VMWorld 所有 OpCode 的 `Reg()` 访问已加上 `RegisterBase` 偏移
- [x] Parser 已支持 `FuncDecl` 和 `CallExpr` 解析
- [x] Snapshot memcpy 自动覆盖 CallStack（内联于 VMInstanceState）

### 未完成（本次实施目标） → ✅ 全部完成

- [x] `RET_FUNC = 61` OpCode 已定义
- [x] VMWorld `case OpCode.CALL` 分发逻辑已实现
- [x] VMWorld `case OpCode.RET_FUNC` 分发逻辑已实现
- [x] `RETURN` 路径：编译器保证 entry 函数用 RETURN，非 entry 函数用 RET_FUNC（设计决策 2）
- [x] BytecodeCompiler 支持多函数两遍编译 + 前向引用回填
- [x] BytecodeCompiler `CallExpr` 区分用户函数（→ CALL）和 Syscall（→ SYSCALL）
- [x] 42 项新增断言（14 TreeWalker + 28 Compiler），总计 279 项全部通过
- [x] 文档已更新

---

## 二、设计决策锁定

### 决策 1：寄存器窗口策略 → 方案 A（共享 scratch + local 区偏移）

> 来源：Step8_FunctionCall_Checklist.md §寄存器窗口策略 设计备忘

- **Scratch zone r0-r15**：全局共享，不随 CALL 偏移，用于参数传递和返回值
- **Local zone r16+**：按 `callerWindowSize` 偏移。CALL 时 `new_base = old_base + callerWindowSize`
- **`CALL.B` 操作数**：携带 `callerWindowSize`（= caller 的 LocalRegCount + 16，即 scratch + local 区总宽度）
- **寄存器溢出保护**：`new_base + callee所需寄存器 > MaxRegisters` → `VMError.StackOverflow`

**容量估算**：MaxRegisters=64，若平均每层窗口偏移 16 个寄存器，可支持 ~3 层嵌套。实际业务场景中技能脚本调用深度通常 ≤ 3，满足需求。更深嵌套待 FO6（自适应窗口）优化。

### 决策 2：RETURN vs RET_FUNC 语义分离

- **`RET_FUNC`**（新增 OpCode 61）：从用户函数返回。弹出 CallFrame → 恢复 IP、RegisterBase → 回到 caller
- **`RETURN`**（现有 OpCode 6）：终止实例执行 / 进入 Cleanup 链。仅用于 entry 函数末尾
- **编译器保证**：非 entry 函数 → 末尾 emit `RET_FUNC`；entry 函数 → 末尾 emit `RETURN`
- **RETURN 路径不检查 CallStackDepth**：编译器层面保证只在 entry 函数使用 RETURN，运行时无需兼容

### 决策 3：Reg() 偏移已就位

- VMWorld 中所有 `Reg(ref inst, index)` 已加上 `RegisterBase` 偏移
- CALL/RET_FUNC 只需修改 `RegisterBase` 字段，所有寄存器访问自动生效
- 无需逐 OpCode 修改

---

## 三、分阶段执行 Checklist

### Phase 1：运行时基础（对应 Sub-task A 剩余项）

> 目标：VM 层面具备 CALL / RET_FUNC 能力，可用手写字节码验证

- [x] **P1.1** 在 OpCode.cs Phase 3 区段新增 `RET_FUNC = 61`
- [x] **P1.2** VMWorld.cs `ExecuteInstance()` 新增 `case OpCode.CALL`
- [x] **P1.3** VMWorld.cs `ExecuteInstance()` 新增 `case OpCode.RET_FUNC`
- [x] **P1.4** 手写字节码单元测试：F01 基本调用 + F02 调用链 + F04 参数传递
- [x] **P1.5** 手写字节码单元测试：F03 StackOverflow + F05 GC 零分配

**Phase 1 验收** ✅：14 项新 Assert 全部通过。

---

### Phase 2：编译器多函数支持（对应 Sub-task C）

> 目标：BytecodeCompiler 能编译含多个函数的模块，正确区分用户函数 vs Syscall

- [x] **P2.1** BytecodeCompiler 新增 `_functionTable` + `_funcDecls` + `_pendingCalls`（前向引用回填列表）
- [x] **P2.2** 改造 `CompileModule()` 为两遍编译 + 编译后回填前向引用
- [x] **P2.3** 新增 `CompileFunction(FuncDecl, bool isEntry)` 方法
- [x] **P2.4** 新增 `CompileUserCallExpr` / `CompileUserCallVoid` / `EmitUserCall`
- [x] **P2.5** 参数传递：scratch zone r0..rN
- [x] **P2.6** 返回值：r0 约定（与 Syscall 一致）
- [x] **P2.7** `FunctionEntry[]` 传入 `VMProgram` 构造函数

**Phase 2 验收** ✅：多函数编译成功，前向引用回填正确，现有路径无回归。

---

### Phase 3：端到端测试 + 回归验证（对应 Sub-task D）

> 目标：全面验证函数调用正确性

- [x] **P3.1** CF01：基本函数调用 add(3,4)=7
- [x] **P3.2** CF02：多函数调用链 a()→b(), 结果 43
- [x] **P3.3** CF03：寄存器窗口隔离 — caller x=100, callee x=999 互不干扰
- [x] **P3.4** F03（TreeWalker）：调用深度保护 — 递归超 MaxCallDepth → StackOverflow
- [x] **P3.5** CF04：函数 + Syscall 混合 — Ping() + double(21)=42
- [x] **P3.6** CF05：void 函数调用（副作用 sideEffect）
- [x] **P3.7** CF06：嵌套调用链 square(7)→mul(7,7)=49
- [x] **P3.8** F05（TreeWalker）：GC 零分配验证 — 100 次 CALL/RET_FUNC = 0 GC
- [x] **P3.9** CF07：defer cleanup 正确执行（单函数回归）
- [x] **P3.10** CF08：参数数量不匹配 → 编译错误
- [x] **P3.11** CF09：单函数回归验证 — 10+20=30

**Phase 3 验收** ✅：42 项新 Assert，总计 279 项全部通过，0 GC，无回归。

---

### Phase 4：文档更新（对应 Sub-task E）

- [x] **P4.1** VM_Summary.md §七：步骤 8 标记 ✅
- [x] **P4.2** VM_Summary.md：新增 Phase 3 OpCode 表（CALL, RET_FUNC）
- [x] **P4.3** VM_Summary.md：更新测试断言总数 → 279
- [x] **P4.4** VM_Summary.md：更新 F1-F2, F3 状态为 ✅
- [x] **P4.5** 更新本文件 checklist 状态

**Phase 4 验收** ✅：VM_Summary.md 准确反映步骤 8 完成后的项目状态。

---

## 四、依赖关系与推进顺序

```
Sub-task B (已完成) ─────┐
                         ├──→ Phase 2 (编译器) ✅ ──→ Phase 3 (测试) ✅ ──→ Phase 4 (文档) ✅
Phase 1 (运行时) ✅ ─────┘
```

全部 Phase 已完成。

---

## 五、风险与关注点

| 风险 | 影响 | 缓解 |
|------|------|------|
| 寄存器窗口偏移导致 MaxRegisters=64 下嵌套层数不足 | 深度调用编译后寄存器溢出 | 方案 A 下平均 3 层够用；后续 FO6 自适应窗口优化可扩展 |
| 编译器两遍编译引入 entry IP 错误 | 函数跳转目标错误 → 运行时 panic | Phase 2 中用占位 + 回填保证正确性；F-02 多函数链测试覆盖 |
| RETURN 路径意外在非 entry 函数触发 | 实例提前终止 | 编译器保证非 entry 函数只 emit RET_FUNC，不 emit RETURN |
| CleanupBase 在函数边界的交互 | using/defer 跨函数调用时 cleanup 范围错误 | F-07 测试覆盖；CallFrame.CleanupBase 已预留 |
