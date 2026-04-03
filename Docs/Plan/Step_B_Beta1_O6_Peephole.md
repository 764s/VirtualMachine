# B-β1: O6 Peephole 优化 Pass

## 目标

在 BytecodeCompiler 的 emit 完成后、VMProgram 构造前，插入 post-emit 扫描，消除冗余指令模式，实现 ≥5% 指令数减少。

## 完成条件

1. Peephole pass 实现，覆盖 4 种模式
2. 指令数减少 ≥5%（benchmark 验证）
3. 676 项 Assert 全通过（回归无破坏）

## 实现模式

| ID | 模式 | 优化为 | 场景 |
|----|------|--------|------|
| P1 | `MOVE rA, rA` | 删除 (NOP) | 自赋值 |
| P2 | `OP rT, B, C` → `MOVE rV, rT` | `OP rV, B, C` | dest-redirect: 表达式写入 temp 后拷贝到变量，消除 MOVE |
| P3 | `MOVE rA, rB` → `MOVE rB, rA` | 删除第二条 | 冗余回拷 |
| P4 | `JUMP target` 且 target == IP+1 | 删除 (NOP) | 跳转到下一条指令 |

## 算法

### Phase 1: 构建跳转目标集合

扫描所有指令，收集 JUMP / JUMP_IF_ZERO / JUMP_IF_NOT_ZERO / CALL / PUSH_CLEANUP 的目标 IP 到 `HashSet<int>`。被跳转目标指向的指令不可安全消除。

### Phase 2: 模式匹配与标记

逐条扫描 `_instructions`，匹配模式：
- P1: `MOVE rA, rA` → 标记为 NOP
- P2: 如果 `_instructions[i]` 是结果生成指令 (LOAD_CONST, ADD, SUB, ... NOT, NEG)，且 `_instructions[i+1]` 是 `MOVE rV, rT`（T == 指令 i 的目标寄存器），且 i+1 不是跳转目标 → 重定向目标寄存器，标记 MOVE 为 NOP
- P3: 连续 `MOVE rA, rB ; MOVE rB, rA` 且第二条不是跳转目标 → 标记第二条为 NOP
- P4: `JUMP target` 且 target == i+1 → 标记为 NOP（仅无条件跳转）

### Phase 3: NOP 压缩 + 跳转目标重建

1. 构建 remap[oldIP] → newIP 映射表
2. 遍历非 NOP 指令，重建 `_instructions` 和 `_sourceLines`
3. 更新所有跳转指令的目标 IP
4. 更新 FunctionEntry 的 EntryIP

## 子任务清单

- [x] T1: 实现 `PeepholeOptimize()` 方法
- [x] T2: 在 Compile() 中调用（backpatch 之后、VMProgram 构造之前）
- [x] T3: 添加编译器测试验证优化效果
- [x] T4: 运行全部 676 项 Assert 验证无回归
- [x] T5: Benchmark 验证指令数减少 ≥5%
- [x] T6: 更新 VM_Summary.md 和 Outlook_And_Risks.md

## 妥协点

- **P2 dest-redirect 保守策略**：仅优化 `_instructions[i+1]` 紧跟在操作指令后的 MOVE，不做跨指令活跃性分析。这是永久妥协，因为更激进的优化需要完整的数据流分析（接近 SSA），成本/收益不匹配。
- **无条件跳转 P4 限定**：JUMP_IF_ZERO/JUMP_IF_NOT_ZERO 目标为 IP+1 时不删除，避免改变条件求值副作用语义。这是永久妥协。
