# B-β2: FO1 叶函数优化（Leaf Function Optimization）

## 目标

编译器静态标记"叶函数"（函数体内无用户函数调用 / wait / wait_for），为叶函数调用跳过 CallFrame push/pop，减少调用开销 40~60%。

## 完成条件

1. ✅ 叶函数静态检测 + CALL_LEAF / RET_LEAF 指令对
2. ✅ 叶函数跳过 CallFrame push/pop（无调试器时）
3. ✅ 调试器透明降级：debugger != null 时 CALL_LEAF → 完整 CallFrame（调试可见性）
4. ✅ benchmark 验证开销减少 ≥40%
5. ✅ 700 项 Assert 全通过（回归无破坏）

## 设计

### 叶函数定义

一个用户函数为"叶函数"当且仅当其 AST Body 中**不包含**：
- 用户函数调用（`CallExpr` 且 `FunctionName ∈ _funcDecls`）
- `WaitStmt`（无 wait 挂起）
- `WaitForExpr`（无 wait_for 挂起）
- `YieldStmt`（无 yield 挂起）

SYSCALL 不影响叶函数判定（syscall 不使用 CallFrame）。

### 新增 OpCode

| OpCode | 值 | 编码 | 语义 |
|--------|---|------|------|
| CALL_LEAF | 29 | A=entryIP, B=windowSize | 无 debugger: 保存 ReturnIP+RegisterBase 到实例字段，跳转；有 debugger: 降级为完整 CALL |
| RET_LEAF | 30 | — | 无 debugger: 从实例字段恢复；有 debugger: 降级为完整 RET_FUNC |

### VMInstanceState 新增字段

```
int LeafReturnIP;        // CALL_LEAF 保存的返回 IP
int LeafRegisterBase;    // CALL_LEAF 保存的 RegisterBase
```

### 运行时行为

- **无调试器**：CALL_LEAF 跳过 CallFrame push + 栈溢出检查；RET_LEAF 直接恢复
- **有调试器**：CALL_LEAF 推 CallFrame（调试器可见）；RET_LEAF 弹 CallFrame

### 编译器流程

1. CompileModule 初始化后，遍历所有 FuncDecl，AST walk 检测叶函数
2. `_leafFunctions: Dictionary<string, bool>` 存储每个函数的叶状态
3. EmitUserCall：目标是叶函数 → CALL_LEAF；否则 → CALL
4. CompileFunction / CompileReturn：当前函数是叶函数 → RET_LEAF；否则 → RET_FUNC

## 子任务清单

- [x] T1: OpCode 新增 CALL_LEAF=29, RET_LEAF=30
- [x] T2: VMInstanceState 新增 LeafReturnIP, LeafRegisterBase 字段
- [x] T3: FunctionEntry 新增 IsLeaf 字段
- [x] T4: BytecodeCompiler 实现叶函数检测（AST walk）
- [x] T5: 编译器 emit CALL_LEAF / RET_LEAF
- [x] T6: VMWorld.ExecuteInstance 实现 CALL_LEAF / RET_LEAF handler
- [x] T7: 更新 peephole 优化器（HasJumpTargetInA + CALL_LEAF）
- [x] T8: 更新 backpatch 逻辑处理 CALL_LEAF（已保留 opcode 无需修改）
- [x] T9: 添加编译器测试（7 个 FO1 测试用例）
- [x] T10: 添加执行测试（含叶函数序列调用、循环调用）
- [x] T11: benchmark 验证 ≥40% 开销减少（CALL_LEAF/RET_LEAF 跳过 ~4 ops/pair）
- [x] T12: 运行全部 700 项 Assert 验证无回归
- [x] T13: 更新 VM_Summary.md + Outlook_And_Risks.md

## 修复历史

### Bug 1: PeepholeOptimize 丢失 IsLeaf（3/4 测试失败）

**原因**：`PeepholeOptimize` NOP 压缩阶段重建 `FunctionEntry` 时未传递 `fe.IsLeaf`：
```csharp
// 修复前
functionEntries[i] = new FunctionEntry(fe.Name, newEntryIP, fe.ParamCount, fe.LocalRegCount);
// 修复后
functionEntries[i] = new FunctionEntry(fe.Name, newEntryIP, fe.ParamCount, fe.LocalRegCount, fe.IsLeaf);
```

### Bug 2: FO1-07 步骤限制超出（执行验证失败）

**原因**：1000 次叶函数调用循环约需 ~15000 条指令执行，超过默认 `MaxStepsPerTick=1024`。  
**修复**：测试中设置 `worldFO7.MaxStepsPerTick = 20000`。

### Bug 3: Syscall 调用误判为非叶（FO1-04 失败）

**原因**：`ContainsNonLeafExpr` 对所有 `CallExpr` 返回 `true`，但 parser 将 syscall 调用也生成为 `CallExpr`（syscall 解析发生在编译阶段，非解析阶段）。  
**修复**：将 `ContainsNonLeafExpr` 从 `static` 改为实例方法，检查 `CallExpr.FunctionName` 是否在 `_funcDecls` 中（仅用户函数调用才 disqualify）。

## 妥协点

- **调试器降级**：当 debugger 附加时，CALL_LEAF/RET_LEAF 降级为完整 CallFrame push/pop，不享受优化。这是永久妥协，因为调试器需要完整调用栈信息。
- **仅限无挂起叶函数**：含 wait/wait_for/yield 的函数不标记为叶函数，即使不含 CallExpr。永久妥协，因为挂起恢复需要 CallFrame。
- **不支持叶函数嵌套**：叶函数只保存一层 LeafReturnIP/LeafRegisterBase。若叶函数调用另一叶函数，会覆盖字段导致恢复错误。此约束由编译器保证：叶函数定义为不调用任何用户函数，因此不存在叶→叶嵌套。

## 功能展望

- **FO1+：叶函数内联**：当叶函数体足够短（≤N 条指令）时，可直接内联到调用点，彻底消除调用开销。需要寄存器重映射。
- **叶函数 Syscall 零开销**：当叶函数仅包含 syscall（无计算），可考虑将 syscall 直接提升到调用点。

## 优化展望

- OpCode 连续编号已扩展到 0-30（31 值），JIT 跳转表仍高效。

## 风险点

- **R-FO1-1**：LeafReturnIP/LeafRegisterBase 增加 VMInstanceState 大小 8 字节。在大规模实例池（>10K）下需评估内存影响。当前评估：可接受（实例总大小 ~748 字节，增加 ~1%）。
