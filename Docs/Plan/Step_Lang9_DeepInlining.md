# Plan: Lang-9 A5 深度内联

> **状态**：⏳ P1 ✅ 完成，P2 待开始
> **来源**：[D_DeepInlining.md](../Discussion/D_DeepInlining.md) 可行性分析结论
> **日期**：2026-04-10

---

## 一、核心设计决策

> 编译器总是对所有满足 CanInline 条件的函数主动执行内联展开，无论是否标注 `@inline`。
> `@inline` 不改变内联行为，仅在标注函数无法内联时触发诊断（`InlineFailurePolicy`: Warn/Error）。
> 未标注 `@inline` 的函数若无法内联 → 静默退化到 CALL/XCALL。

---

## 二、分阶段路径

| 阶段 | 内容 | 复杂度 | 状态 |
|------|------|--------|------|
| **P1** | 模块内 trivial 内联（单 return，纯表达式，无分支/循环/yield/defer，无递归） | ⭐⭐ | ✅ |
| P2 | 模块内一般内联（多语句、分支、循环、多 return） | ⭐⭐⭐ | ⬚ |
| P3 | 跨模块内联（XLOAD_MVAR 替换 + ServiceBinding AST 传递） | ⭐⭐⭐ | ⬚ |
| P4 | 深度内联（A→B→C 链式展开） | ⭐⭐ | ⬚ |

---

## 三、P1 子步骤

### P1-1: 内联配置

- 在 `BytecodeCompiler` 中新增编译器选项（不在 VMConfig，因为这是编译期行为）：
  - `InlineThreshold = 16`（最大可内联指令估算数）
  - `InlineDepthMax = 3`（最大嵌套内联深度）
  - `InlineFailurePolicy = Warn`（Warn/Error/Silent）
- 涉及文件：`BytecodeCompiler.cs`

### P1-2: CanInline 判定

```
CanInline(funcName, depth):
  func = _funcDecls[funcName]
  if depth > InlineDepthMax → false
  if func.Body contains YieldStmt / WaitStmt / WaitForStmt → false
  if func.Body contains DeferStmt / UsingStmt → false
  if func.Body contains CallExpr (用户函数调用) → false  // P1 保守策略
  if func.Body contains MemberCallExpr (XCALL) → false
  if EstimateSize(func) > InlineThreshold → false
  → true
```

### P1-3: EstimateInstructionCount

AST 层面简单估算（无需实际编译）：
- ReturnStmt: 1 + estimate(value)
- VarDeclStmt: 1 + estimate(initializer)
- ExprStmt: estimate(expr)
- BinaryExpr: 1 + estimate(left) + estimate(right)
- UnaryExpr: 1 + estimate(operand)
- CallExpr(syscall): 1 + sum(estimate(args))
- IdentifierExpr / NumberLiteral: 0（寄存器引用 or 常量，不产生额外指令）
- IfStmt/WhileStmt/ForStmt: 对 P1 直接返回大数（不内联）
- 其它: 1（保守）

### P1-4: 内联展开

在 `EmitUserCall` 之前插入内联尝试逻辑：

1. **参数绑定**：建立 `paramName → argReg` 映射表
2. **创建内联作用域**：临时 scope，参数名映射到调用方已计算的 arg 寄存器
3. **编译内联体**：对 FuncDecl.Body 中的语句逐一编译
4. **Return 处理**：
   - Trivial（P1 限制为单 return）：将 return value 编译到 destReg
5. **跳过 CALL 发射**

### P1-5: @inline 诊断

- 内联失败时检查 `funcDecl.IsInline`
- 若 `IsInline` 且 `InlineFailurePolicy == Warn` → `_warnings.Add(...)`
- 若 `IsInline` 且 `InlineFailurePolicy == Error` → `_errors.Add(...)`
- 若非 `IsInline` → 静默退化到 CALL

### P1-6: 测试 IN01-IN08

| # | 测试 | 验证 |
|---|------|------|
| IN01 | 单 return 纯表达式函数内联 | `func add(a, b) { return a + b }` — 调用方无 CALL 指令 |
| IN02 | 内联函数结果正确性 | 编译+运行，验证值等于 CALL 路径 |
| IN03 | yield 函数不内联 | 含 yield 的函数退化到 CALL |
| IN04 | defer 函数不内联 | 含 defer 的函数退化到 CALL |
| IN05 | 含用户函数调用的函数不内联 (P1) | 调用其他 user func → CALL |
| IN06 | 超 InlineThreshold 不内联 | 大函数退化到 CALL |
| IN07 | @inline 标注 + 无法内联 → 警告 | IsInline + yield → warning 输出 |
| IN08 | 多处调用同一函数都内联 | 函数被调用 3 次，3 次都内联展开 |

---

## 四、与现有优化的交互

- **FO1 Leaf 分析**：内联分析在 leaf 分析之后。被内联的函数仍参与 leaf 分析（其他调用者可能不内联）
- **FO6 窗口重映射**：内联展开的代码使用调用方寄存器空间，FO6 自然适用
- **B-ε3 常量传播**：内联后常量折叠可进一步优化（内联参数为常量时）
- **A1/A2 退化**：A5 内联是 A1/A2 的泛化。A1/A2 仍独立存在（跨模块场景，P3 之前无法 A5 内联）

---

## 五、依赖

- 无外部依赖。P1 纯编译器内部改动
- P3 依赖 ServiceBinding 扩展（传递 FuncDecl AST）

---

## 六、验收标准

- [x] IN01-IN08 全通过
- [x] 现有 1388 → 1411 测试无回归
- [ ] B01-B06 benchmark 无回归
