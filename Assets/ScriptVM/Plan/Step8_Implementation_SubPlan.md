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

### 未完成（本次实施目标）

- [ ] `RET_FUNC = 61` OpCode 未定义
- [ ] VMWorld `case OpCode.CALL` 分发逻辑未实现
- [ ] VMWorld `case OpCode.RET_FUNC` 分发逻辑未实现
- [ ] `RETURN` 路径未适配 `CallStackDepth > 0` 场景
- [ ] BytecodeCompiler 仍为单函数编译（仅编译 entry function）
- [ ] BytecodeCompiler `CallExpr` 全部走 Syscall 路径，无用户函数识别
- [ ] 无函数调用端到端测试
- [ ] 文档未更新

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

- [ ] **P1.1** 在 OpCode.cs Phase 3 区段新增 `RET_FUNC = 61`
  - 注释：`A=unused → pop CallFrame, restore IP + RegisterBase`
- [ ] **P1.2** VMWorld.cs `ExecuteInstance()` 新增 `case OpCode.CALL`：
  - 检查 `inst.CallStackDepth < MaxCallDepth`，否则 → `VMError.PanicStackOverflow`
  - 构造 CallFrame：`ReturnIP = inst.IP + 1`，`RegisterBase = inst.RegisterBase`，`CleanupBase = inst.CleanupDepth`
  - `inst.CallStack.Set(inst.CallStackDepth, frame)` → `inst.CallStackDepth++`
  - `inst.RegisterBase += inst.RegisterBase + callerWindowSize`（`callerWindowSize = 当前指令.B`）
  - `inst.IP = 当前指令.A`（目标函数入口 IP）
  - **不** `IP++`（已直接跳转）
- [ ] **P1.3** VMWorld.cs `ExecuteInstance()` 新增 `case OpCode.RET_FUNC`：
  - `inst.CallStackDepth--`
  - `var frame = inst.CallStack.Get(inst.CallStackDepth)`
  - `inst.IP = frame.ReturnIP`
  - `inst.RegisterBase = frame.RegisterBase`
  - **不** `IP++`（ReturnIP 已是 CALL 的下一条指令）
- [ ] **P1.4** 手写字节码单元测试：CALL + RET_FUNC 基本压栈/弹栈
- [ ] **P1.5** 手写字节码单元测试：StackOverflow 保护（递归超 MaxCallDepth=16）

**Phase 1 验收**：手写字节码函数调用 + 返回正确，StackOverflow 正确触发，0 GC。

---

### Phase 2：编译器多函数支持（对应 Sub-task C）

> 目标：BytecodeCompiler 能编译含多个函数的模块，正确区分用户函数 vs Syscall

- [ ] **P2.1** BytecodeCompiler 新增 `_functionTable: Dictionary<string, int>`（函数名 → 入口 IP）
- [ ] **P2.2** 改造 `CompileModule()` 为两遍编译：
  - 第一遍：扫描所有 `module.Functions`，注册函数名到 `_functionTable`（占位 IP=-1）
  - 第二遍：先编译 entry 函数（末尾 emit `RETURN`），再逐一编译其余函数（末尾 emit `RET_FUNC`）
  - 编译每个函数前回填 `_functionTable[name] = 当前 IP`
- [ ] **P2.3** 新增 `CompileFunction(FuncDecl, bool isEntry)` 方法：
  - 重置/隔离寄存器分配状态（每个函数独立分配 r16+）
  - 为参数绑定寄存器：参数从 scratch zone r0..rN 拷贝到本地寄存器 r16+
  - 编译函数体 body
  - 非 entry 函数末尾 emit `RET_FUNC`；entry 函数末尾 emit `RETURN`
  - 记录函数的 `LocalRegCount`
- [ ] **P2.4** 改造 `CompileCallExpr()`（原 `CompileSyscallExpr`）：
  - 先查 `_functionTable` → 命中则 emit 参数到 scratch zone → emit `CALL(entryIP, callerWindowSize)`
  - 未命中则查 Syscall 表 → emit `SYSCALL`（现有路径）
  - 两者都未命中 → 编译错误 `Unknown function '{name}'`
- [ ] **P2.5** 参数传递：caller 将参数编译到 r0..rN（scratch zone），CALL 后 callee 通过参数绑定读取
- [ ] **P2.6** 返回值：callee 将返回表达式编译到 r0，caller 在 CALL 后从 r0 读取（与 Syscall 一致）
- [ ] **P2.7** 更新 `Compile()` / `CompileModule()` 输出：将 `FunctionEntry[]` 传入 `VMProgram` 构造函数

**Phase 2 验收**：多函数模块编译成功，CallExpr 正确区分用户函数/Syscall，现有单函数 + Syscall 路径无回归。

---

### Phase 3：端到端测试 + 回归验证（对应 Sub-task D）

> 目标：全面验证函数调用正确性，包括 GC 和快照

- [ ] **P3.1** 测试 F-01：基本函数调用 — `func add(a, b) { return a + b }` → `add(3, 4)` → 结果 7
- [ ] **P3.2** 测试 F-02：多函数调用链 — `func a() { return b() + 1 }` `func b() { return 42 }` → 结果 43
- [ ] **P3.3** 测试 F-03：寄存器窗口隔离 — caller/callee 局部变量互不干扰
- [ ] **P3.4** 测试 F-04：调用深度保护 — 递归超过 MaxCallDepth → `VMError.StackOverflow`
- [ ] **P3.5** 测试 F-05：函数 + Syscall 混合 — 同一模块中两者共存，互不干扰
- [ ] **P3.6** 测试 F-06：函数 + wait — 函数内 `wait` 后恢复，调用栈状态正确
- [ ] **P3.7** 测试 F-07：函数 + defer/using — cleanup 在函数返回时正确执行
- [ ] **P3.8** 测试 F-08：GC 零分配验证 — 函数调用路径 0 GC
- [ ] **P3.9** 测试 F-09：快照回滚验证 — SaveState → 修改 → LoadState → CallStack 正确恢复
- [ ] **P3.10** 回归验证：运行全部现有 237 项 Assert，确认无回归
- [ ] **P3.11** 性能基准：运行 B01-B05，确认 ±10% 以内

**Phase 3 验收**：全部新增测试通过，无回归，0 GC，快照回滚正确，性能无退化。

---

### Phase 4：文档更新（对应 Sub-task E）

- [ ] **P4.1** VM_Summary.md §七：步骤 8 标记 ✅，补充 F1-F3 通过信息
- [ ] **P4.2** VM_Summary.md：更新 OpCode 表（新增 RET_FUNC）
- [ ] **P4.3** VM_Summary.md：更新测试断言总数
- [ ] **P4.4** VM_Summary.md：更新"已完成/未完成"表格
- [ ] **P4.5** 更新本文件 checklist 状态

**Phase 4 验收**：VM_Summary.md 准确反映步骤 8 完成后的项目状态。

---

## 四、依赖关系与推进顺序

```
Sub-task B (已完成) ─────┐
                         ├──→ Phase 2 (编译器) ──→ Phase 3 (测试) ──→ Phase 4 (文档)
Phase 1 (运行时 A 剩余) ─┘
```

- Phase 1 无外部依赖，可立即开始
- Phase 2 依赖 Phase 1（需要 CALL/RET_FUNC OpCode + VM 分发）
- Phase 3 依赖 Phase 1 + Phase 2
- Phase 4 依赖 Phase 3 全部通过

---

## 五、风险与关注点

| 风险 | 影响 | 缓解 |
|------|------|------|
| 寄存器窗口偏移导致 MaxRegisters=64 下嵌套层数不足 | 深度调用编译后寄存器溢出 | 方案 A 下平均 3 层够用；后续 FO6 自适应窗口优化可扩展 |
| 编译器两遍编译引入 entry IP 错误 | 函数跳转目标错误 → 运行时 panic | Phase 2 中用占位 + 回填保证正确性；F-02 多函数链测试覆盖 |
| RETURN 路径意外在非 entry 函数触发 | 实例提前终止 | 编译器保证非 entry 函数只 emit RET_FUNC，不 emit RETURN |
| CleanupBase 在函数边界的交互 | using/defer 跨函数调用时 cleanup 范围错误 | F-07 测试覆盖；CallFrame.CleanupBase 已预留 |
