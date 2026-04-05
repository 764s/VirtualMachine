# B-ζ2 CMP-immediate 指令 ✅

## 目标

+6 OpCode `JUMP_IF_EQ_K` ~ `JUMP_IF_GTE_K`：一条指令完成"与常量比较并跳转"，
替代 `CMP_*` + `JUMP_IF_ZERO` → P5 fusion 路径。

## 完成条件

- [x] +6 新 OpCode（JUMP_IF_EQ_K 到 JUMP_IF_GTE_K）
- [x] VMWorld 执行：`if (Reg[B] op Constants[C]) then IP = A`
- [x] 编译器 `TryEmitKJump`：CompileIf/CompileWhile/CompileFor 条件为常量比较时直接 emit
- [x] 处理常量在左侧的情况（swap comparison + flip）
- [x] Peephole HasJumpTargetInA 包含新 opcodes
- [x] GetRegisterMask 正确标注新 opcodes 的寄存器使用
- [x] 1007 assert × 2 模式全通过

## 实现

### 新增 OpCode

| OpCode | 值 | 语义 |
|--------|---|------|
| JUMP_IF_EQ_K | 39 | if Reg[B] == Constants[C] → IP = A |
| JUMP_IF_NEQ_K | 40 | if Reg[B] != Constants[C] → IP = A |
| JUMP_IF_LT_K | 41 | if Reg[B] < Constants[C] → IP = A |
| JUMP_IF_LTE_K | 42 | if Reg[B] <= Constants[C] → IP = A |
| JUMP_IF_GT_K | 43 | if Reg[B] > Constants[C] → IP = A |
| JUMP_IF_GTE_K | 44 | if Reg[B] >= Constants[C] → IP = A |
| SENTINEL | 45 | (updated) |

### 编译器路径

`CompileIf` / `CompileWhile` / `CompileFor` 条件编译时：
1. `TryEmitKJump(condition)` 检查条件是否为 BinaryExpr 比较 + 一侧常量
2. 若匹配：`CompileExpr(variableOperand)` + 直接 emit `JUMP_IF_*_K`（inverted semantics）
3. 常量在左侧：swap comparison（Lt↔Gt, Lte↔Gte）再 emit
4. 不匹配：回退到原路径（CompileExpr + JUMP_IF_ZERO → P5 fusion）

### 与 B-ζ1 LICM 的交互

- LICM 仍会提升所有循环体常量（包括仅用于比较的常量）
- CMP-immediate 直接从常量池读取，不需要 hoisted register
- 仅用于比较的常量的 hoisted register 变为 dead code（微小浪费，可接受）
- 两个优化独立正确，组合后无冲突

## 性能

CMP-immediate 在 LICM 已激活时提供:
- 减少寄存器压力（比较常量不需要 hoisted register read）
- 直接常量池访问替代寄存器间接访问
- 非循环代码中节省 1 条指令（CMP_* 消除）

基准测试结果有较大方差（±20%），无法精确衡量增量收益。
B04 指令数 28（不变），B01 指令数 16（不变）。

## 风险点

- 无新增风险。新 opcodes 仅为已有 JUMP_IF_* 的常量变体，语义等价。
