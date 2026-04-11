# Lang-14: 位运算支持（Bitwise Operations）

> **来源**：技能 tag/flag 位操作需求（`1 << N`、`tags | flag`、`tags & mask`）。传统位运算方案，跨语言通用。
>
> **前置**：Lang-13 ✅ 枚举完成。1627 测试总计。
>
> **性能分析**：
> - **内联自动支持**：BinaryExpr/UnaryExpr 已在 `IsInlineSafeExpr` 中无条件返回 true，新的位运算 NodeKind 无需额外处理。`EstimateBodySize` 基于 AST 节点类型计数（BinaryExpr=1, UnaryExpr=1），新 NodeKind 自动适用。
> - **常量折叠自动支持**：`TryFoldConstant` 仅需为新 NodeKind 添加 `case` 分支（`1 << 11` → 编译期直接折叠为 `2048`）。
> - **VM 热循环影响**：新增 6 个 `case` 分支在 `ExecuteInstance` switch 中。OpCode 连续编号保持 JIT jump table 优化（O2）。C# switch on byte 编译为 jump table，新增 case 不影响已有 case 的分支代价。
> - **Reg() 无改动**：位运算 OpCode 与 ADD/SUB 同构（A=dest, B=lhs, C=rhs / A=dest, B=src），无需修改 Reg() 热路径。

---

## 运算符设计

| 运算符 | FFScript 语法 | Token | NodeKind | OpCode | 语义 |
|--------|--------------|-------|----------|--------|------|
| 按位与 | `a & b` | `Amp` | `BitAnd` | `BIT_AND` | `A = B & C`（整数位与） |
| 按位或 | `a \| b` | `Pipe` | `BitOr` | `BIT_OR` | `A = B \| C`（整数位或） |
| 按位异或 | `a ^ b` | `Caret` | `BitXor` | `BIT_XOR` | `A = B ^ C`（整数位异或） |
| 按位取反 | `~a` | `Tilde` | `BitNot` | `BIT_NOT` | `A = ~B`（整数位取反） |
| 左移 | `a << b` | `LtLt` | `Shl` | `SHL` | `A = B << C`（左移） |
| 右移 | `a >> b` | `GtGt` | `Shr` | `SHR` | `A = B >> C`（算术右移） |

## 优先级（C 语言传统顺序）

| 优先级 | 运算符 | 层级 | Parser 方法 |
|--------|--------|------|-------------|
| 1 | `=` | 赋值 | ParseAssignment |
| 2 | `\|\|` | 逻辑或 | ParseLogicalOr |
| 3 | `&&` | 逻辑与 | ParseLogicalAnd |
| **4** | **`\|`** | **按位或** | **ParseBitOr** |
| **5** | **`^`** | **按位异或** | **ParseBitXor** |
| **6** | **`&`** | **按位与** | **ParseBitAnd** |
| 7 | `==` `!=` | 等于 | ParseEquality |
| 8 | `<` `>` `<=` `>=` | 比较 | ParseComparison |
| **9** | **`<<` `>>`** | **移位** | **ParseShift** |
| 10 | `+` `-` | 加减 | ParseAddition |
| 11 | `*` `/` `%` | 乘除 | ParseMultiplication |
| 12 | `-` `!` **`~`** | 一元 | ParseUnary |
| 13 | `.` | 字段访问 | ParsePostfix |

---

## Checklist

### 1. Lexer（~20 行）
- [ ] 新增 6 个 TokenType：`Amp`, `Pipe`, `Caret`, `Tilde`, `LtLt`, `GtGt`
- [ ] `&` 分支：`Peek()=='&'` → `AmpAmp`，否则 → `Amp`（替换 Error）
- [ ] `|` 分支：`Peek()=='|'` → `PipePipe`，否则 → `Pipe`（替换 Error）
- [ ] `<` 分支：`Peek()=='='` → `Lte`，`Peek()=='<'` → `LtLt`，否则 → `Lt`
- [ ] `>` 分支：`Peek()=='='` → `Gte`，`Peek()=='>'` → `GtGt`，否则 → `Gt`
- [ ] `^` 新分支 → `Caret`
- [ ] `~` 新分支 → `Tilde`

### 2. AST NodeKind（~2 行）
- [ ] 新增：`BitAnd`, `BitOr`, `BitXor`, `BitNot`, `Shl`, `Shr`

### 3. Parser（~50 行）
- [ ] 新增 `ParseBitOr()`：`ParseBitXor()` + while `Pipe` → `BinaryExpr(BitOr, ...)`
- [ ] 新增 `ParseBitXor()`：`ParseBitAnd()` + while `Caret` → `BinaryExpr(BitXor, ...)`
- [ ] 新增 `ParseBitAnd()`：`ParseEquality()` + while `Amp` → `BinaryExpr(BitAnd, ...)`
- [ ] 新增 `ParseShift()`：`ParseAddition()` + while `LtLt`/`GtGt` → `BinaryExpr(Shl/Shr, ...)`
- [ ] 重新接线：`ParseLogicalAnd` → 调用 `ParseBitOr`（原调用 `ParseEquality`）
- [ ] 重新接线：`ParseComparison` → 调用 `ParseShift`（原调用 `ParseAddition`）
- [ ] `ParseUnary` 新增 `~` 处理 → `UnaryExpr(BitNot, operand)`

### 4. Number 位运算操作符（~20 行）
- [ ] Fix64 模式：`BitAnd(a,b)` = `new Number(a.Raw & b.Raw)`（直接 long 位运算）
- [ ] Fix64 模式：`BitOr(a,b)` = `new Number(a.Raw | b.Raw)`
- [ ] Fix64 模式：`BitXor(a,b)` = `new Number(a.Raw ^ b.Raw)`
- [ ] Fix64 模式：`BitNot(a)` = `new Number(~a.Raw)`
- [ ] Fix64 模式：`Shl(a,b)` = `new Number(a.Raw << b.ToInt())`（位移量取整数部分）
- [ ] Fix64 模式：`Shr(a,b)` = `new Number(a.Raw >> b.ToInt())`
- [ ] Dev 模式（double）：先 ToInt → 位运算 → FromInt（位运算仅对整数有意义）
- [ ] **注意**：Fix64 中 `Number.FromInt(1)` 的 Raw = `1L << 32`，位运算直接操作 Raw 对整数语义正确（bit N 在 Raw 中为 bit N+32）。左移 `(1 << N)` 需在整数域执行：`FromInt(a.ToInt() << b.ToInt())`。

### 5. OpCode（~6 行）
- [ ] 新增 6 个连续编号 OpCode：`BIT_AND`, `BIT_OR`, `BIT_XOR`, `BIT_NOT`, `SHL`, `SHR`
- [ ] 编号接续当前最大值（LOAD_CONST_W=56 之后，跳过 SENTINEL=55），使用 57-62

### 6. Compiler BytecodeCompiler（~15 行）
- [ ] `BinOpCode()` 添加 5 个映射：`BitAnd→BIT_AND`, `BitOr→BIT_OR`, `BitXor→BIT_XOR`, `Shl→SHL`, `Shr→SHR`
- [ ] `UnOpCode()` 添加 1 个映射：`BitNot→BIT_NOT`
- [ ] `TryFoldConstant()` BinaryExpr switch 添加 5 个 case：`BitAnd`/`BitOr`/`BitXor`/`Shl`/`Shr`
- [ ] `TryFoldConstant()` UnaryExpr switch 添加 1 个 case：`BitNot`
- [ ] `GetRegisterMask()` 添加：BIT_AND/BIT_OR/BIT_XOR/SHL/SHR → 7（A,B,C=regs）；BIT_NOT → 3（A,B=regs）

### 7. VM 解释循环 VMWorld（~30 行）
- [ ] `case BIT_AND:` → `regs[Reg(A,rb)] = Number.BitAnd(regs[Reg(B,rb)], regs[Reg(C,rb)]); IP++;`
- [ ] `case BIT_OR:` → `regs[Reg(A,rb)] = Number.BitOr(regs[Reg(B,rb)], regs[Reg(C,rb)]); IP++;`
- [ ] `case BIT_XOR:` → `regs[Reg(A,rb)] = Number.BitXor(regs[Reg(B,rb)], regs[Reg(C,rb)]); IP++;`
- [ ] `case BIT_NOT:` → `regs[Reg(A,rb)] = Number.BitNot(regs[Reg(B,rb)]); IP++;`
- [ ] `case SHL:` → `regs[Reg(A,rb)] = Number.Shl(regs[Reg(B,rb)], regs[Reg(C,rb)]); IP++;`
- [ ] `case SHR:` → `regs[Reg(A,rb)] = Number.Shr(regs[Reg(B,rb)], regs[Reg(C,rb)]); IP++;`

### 8. TreeWalker（~10 行）
- [ ] 6 个新 case 对应 BitAnd/BitOr/BitXor/BitNot/Shl/Shr

### 9. FFS_Syntax.md 更新（~10 行）
- [ ] 运算符优先级表新增位运算 4 行
- [ ] 关键字/注解表无变化（位运算使用符号，非关键字）

### 10. 测试（~300 行预估）
- [ ] BW01: 基础 `&` `|` `^` `~` `<<` `>>`
- [ ] BW02: 优先级正确性（`a | b & c` → `a | (b & c)`）
- [ ] BW03: 逻辑运算符与位运算符不混淆（`a && b` vs `a & b`）
- [ ] BW04: 常量折叠（`const x: int = 1 << 11` → 2048）
- [ ] BW05: 枚举 + 位运算（`enum Flags { A = 1, B = 2 }; const mask: int = Flags.A | Flags.B`）
- [ ] BW06: 移位组合（`(1 << 4) | (1 << 0)` → 17）
- [ ] BW07: `~0` = -1, `~1` = -2
- [ ] BW08: 左移右移互逆（`(x << 3) >> 3 == x` for small x）
- [ ] BW09: 位与掩码（`255 & 0xF0` — 需十六进制字面量或等效十进制）
- [ ] BW10: 函数参数中的位运算
- [ ] BW11: 位运算在 if 条件中使用
- [ ] BW12: 位运算内联自动支持验证
- [ ] BW13: 跨模块位运算内联
- [ ] BW14: TreeWalker 位运算
- [ ] BW15: 位运算错误检测（如需要）

### 11. Benchmark 验证
- [ ] B01-B06 无回归

### 12. VM_Summary 完成更新
- [ ] Lang-14 状态 ⏳ → ✅
- [ ] 子计划 checklist 块添加
- [ ] 测试计数更新

---

## 内联自动支持分析

**结论：位运算表达式完全自动获得内联支持，无需额外改动。**

证据链：
1. `IsInlineSafeExpr`（BytecodeCompiler.cs:4501-4527）：`BinaryExpr` → 递归检查子表达式（不区分 NodeKind）。`UnaryExpr` → 递归检查操作数。新的 `BitAnd`/`BitOr`/... NodeKind 自动安全。
2. `EstimateBodySize`（BytecodeCompiler.cs:4533+）：AST 节点计数启发式，`BinaryExpr`=1, `UnaryExpr`=1，与 NodeKind 无关。
3. `CanInline` / `CanInlineCrossModule`：调用 `IsInlineSafeStmt`/`IsInlineSafeExpr`，无 NodeKind 白名单。
4. `TryInlineCall` / `TryInlineMemberCall`：内联展开调用 `CompileExpr`，后者通过 `BinOpCode()` / `UnOpCode()` 映射到 OpCode，新 NodeKind 只需添加映射即可。
5. 常量折叠在内联前执行（`CompileExpr` 入口），`1 << 11` 等表达式在内联体中也会被折叠。

## 性能回归风险评估

**风险：极低。**

1. **Reg() 无改动**：最敏感的热路径不受影响。
2. **ExecuteInstance switch**：C# 编译器对 `byte` switch 生成 jump table（最多 256 条目），新增 6 个 case 仅填充 jump table 中的空位，不影响已有 case 的跳转代价。
3. **指令编码**：新 OpCode 与 ADD/SUB 完全同构（3-reg ABC 或 2-reg AB），无特殊编码需求。
4. **Number 操作**：位运算比乘法/除法更轻量（纯位操作，无溢出处理），不会引入新的性能瓶颈。

**必须验证**：B01-B06 benchmark 无回归。
