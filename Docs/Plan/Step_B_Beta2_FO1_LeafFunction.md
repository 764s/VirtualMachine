# B-β2: FO1 叶函数优化（Leaf Function Optimization）

## 目标

编译器静态标记"叶函数"（函数体内无 CallExpr / wait / wait_for），为叶函数调用跳过 CallFrame push/pop，减少调用开销 40~60%。

## 完成条件

1. 叶函数静态检测 + CALL_LEAF / RET_LEAF 指令对
2. 叶函数跳过 CallFrame push/pop（无调试器时）
3. 调试器透明降级：debugger != null 时 CALL_LEAF → 完整 CallFrame（调试可见性）
4. benchmark 验证开销减少 ≥40%
5. 676+ 项 Assert 全通过（回归无破坏）

## 设计

### 叶函数定义

一个用户函数为"叶函数"当且仅当其 AST Body 中**不包含**：
- `CallExpr`（不调用其他用户函数）
- `WaitStmt`（无 wait 挂起）
- `WaitForExpr`（无 wait_for 挂起）

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

- [ ] T1: OpCode 新增 CALL_LEAF=29, RET_LEAF=30
- [ ] T2: VMInstanceState 新增 LeafReturnIP, LeafRegisterBase 字段
- [ ] T3: FunctionEntry 新增 IsLeaf 字段
- [ ] T4: BytecodeCompiler 实现叶函数检测（AST walk）
- [ ] T5: 编译器 emit CALL_LEAF / RET_LEAF
- [ ] T6: VMWorld.ExecuteInstance 实现 CALL_LEAF / RET_LEAF handler
- [ ] T7: 更新 peephole 优化器（HasJumpTargetInA + CALL_LEAF）
- [ ] T8: 更新 backpatch 逻辑处理 CALL_LEAF
- [ ] T9: 添加编译器测试
- [ ] T10: 添加执行测试
- [ ] T11: benchmark 验证 ≥40% 开销减少
- [ ] T12: 运行全部 Assert 验证无回归
- [ ] T13: 更新 VM_Summary.md + Outlook_And_Risks.md

## 妥协点

- **调试器降级**：当 debugger 附加时，CALL_LEAF/RET_LEAF 降级为完整 CallFrame push/pop，不享受优化。这是永久妥协，因为调试器需要完整调用栈信息。
- **仅限无挂起叶函数**：含 wait/wait_for 的函数不标记为叶函数，即使不含 CallExpr。永久妥协，因为挂起恢复需要 CallFrame。
