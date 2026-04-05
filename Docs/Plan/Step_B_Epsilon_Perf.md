# B-ε: 性能优化串行计划（追平 Lua 目标）

> 来源：VM_Summary.md §七 串行计划 B-ε1~ε4
> 状态：✅ 全部完成（4/4）

## 目标

跨语言 benchmark 显示 FFVM 比 Lua 5.4 慢 2-3x。
根因：指令密度差距 2.0x（循环控制 4 条 vs Lua FORLOOP 1 条）× 逐指令开销差距 1.5x ≈ 3.0x。
四步串行优化依次消除各层开销。

## 完成步骤

### B-ε1: fixed pin 消除边界检查
- `fixed (Instruction* codeBase = code)` pin 指令/常量数组
- 实测 .NET 10 + 现代 CPU 分支预测下差异在噪声内（±2%），但消除了分支预测器饱和时的性能悬崖

### B-ε2: Compare&Branch fusion
- Peephole P5 规则：`CMP_* tmp,B,C` + `JUMP_IF_ZERO tgt,tmp` → `JUMP_IF_* tgt,B,C`
- +6 OpCode（JUMP_IF_EQ/NEQ/LT/LTE/GT/GTE）+ 6 VMWorld case
- Liveness-based dead-register 检查（FO6 remap 后 temp 不在 TempRegBase 以上）
- 实测 B01-B06 全面提升 12-21%

### B-ε3: const + 常量传播 + 条件 DCE
- `const` 关键字 + Lexer/Parser/AST 支持
- `TryFoldConstant` 识别 const 标识符 → 编译期内联
- `CompileIf`/`CompileWhile` 常量条件消除死分支
- 赋值 const 报错；LSP keyword count 16

### B-ε4: FORLOOP 超级指令
- `FORLOOP A=loopTopIP, B=counterReg, C=limitReg`
- VM dispatch: `Reg[B] += 1; if Reg[B] < Reg[C] → IP = A`
- 编译器 `TryCompileForLoop` pattern-match `for(var i=init; i<limit; i=i+1)`
- 匹配时：JUMP_IF_GTE 初始检查 + body + FORLOOP（4→1 循环控制指令）
- 不匹配时：fallback 到标准 CompileFor
- Benchmark 全部改用 `for` 循环

## Benchmark 结果

环境：Windows / .NET 10.0.5 / 20 cores / WarmupRuns=100 / MeasureRuns=200

### 累计收益（B-ε1 起始基线 → B-ε4 完成后）

起始基线（B-ε 优化前，while 循环）：

| Benchmark | Before (μs) | Instr |
|-----------|-------------|-------|
| B01_ArithLoop | 205.8 | 19 |
| B02_Fibonacci | 0.7 | 15 |
| B03_NestedLoop | 190.5 | 24 |
| B04_Branching | 378.1 | 37 |
| B05_Accumulator | 711.3 | 14 |
| B06_FuncCall | 130.6 | 21 |

B-ε4 完成后（for 循环 + FORLOOP）：

| Benchmark | After (μs) | Instr | Δ时间 | Δ指令 |
|-----------|------------|-------|-------|-------|
| B01_ArithLoop | ~141 | 16 | -31% | -3 |
| B02_Fibonacci | 0.3 | 12 | -57% | -3 |
| B03_NestedLoop | ~107 | 18 | -44% | -6 |
| B04_Branching | ~261 | 31 | -31% | -6 |
| B05_Accumulator | ~305 | 11 | -57% | -3 |
| B06_FuncCall | ~80 | 18 | -39% | -3 |

**平均改善：~43% 时间，~4 条指令。**

## 变更文件

| 文件 | 变更 |
|------|------|
| `Assets/Scripts/VM/Core/OpCode.cs` | +JUMP_IF_EQ(32)~JUMP_IF_GTE(37), +FORLOOP(38), SENTINEL→39 |
| `Assets/Scripts/VM/Core/VMWorld.cs` | fixed pin arrays, +6 fused jump cases, +FORLOOP dispatch |
| `Assets/Scripts/VM/Compiler/BytecodeCompiler.cs` | P5 peephole, `_constValues`, const handling, DCE, `TryCompileForLoop`, `_forLoopId` |
| `Assets/Scripts/VM/Compiler/Lexer.cs` | +Const token |
| `Assets/Scripts/VM/AST/ASTNode.cs` | +VarDeclStmt.IsConst |
| `Assets/Scripts/VM/Compiler/Parser.cs` | const var-decl support |
| `Assets/Scripts/VM/Tests/BenchmarkRunner.cs` | All B01-B06 scripts converted to `for` loops |
| `Assets/Scripts/VM/Tests/LspTests.cs` | Keyword count 15→16 |

## 功能展望

| ID | 内容 | 触发时机 | 复杂度 |
|----|------|----------|--------|
| **Pε-F1** | while 循环 FORLOOP 识别 | while 循环是主要循环形式时（当前 for 已覆盖） | 中（需 AST 分析 body 尾部 increment 模式） |
| **Pε-F2** | FORLOOP 变步长支持（step ≠ 1） | 非单位步长循环出现时 | 低（扩展 FORLOOP 语义或新增 FORLOOP_STEP opcode） |
| **Pε-F3** | `<=` 条件的 FORLOOP | `for(i=0; i<=N; ...)` 模式出现时 | 低（扩展 TryCompileForLoop 匹配 Lte + 对应 FORLOOP_LE 或运行时判断） |

## 优化展望

| ID | 内容 | 预期收益 | 复杂度 |
|----|------|---------|--------|
| **O8** | 指令压缩 16B→4B | L1 缓存 10-20% 加速 | 高 |
| **FO3** | 小函数内联 | 小函数 -80% 指令 | 高 |
| **O16** | 全局寄存器分配 | 消除跨语句 temp 重复加载 | 高 |

## 风险点

| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| **Rε1** | FORLOOP pattern 仅匹配精确的 `i < limit; i = i + 1` | 非标准 for 循环不受益 | Fallback 到标准编译保证正确性 |
| **Rε2** | 隐藏限变量 `$fl{N}` 占用变量寄存器 | 变量寄存器空间稍减 | TempRegBase=48 下最多 32 个 var slot，for 循环通常 ≤3 层 |
| **Rε3** | Benchmark 改用 for 循环后与旧基线不直接可比 | 历史对比需注明循环类型 | 同时保留 B-ε2 基线数据（while）和 B-ε4 数据（for） |
