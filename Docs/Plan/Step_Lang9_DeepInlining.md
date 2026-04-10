# Plan: Lang-9 A5 深度内联

> **状态**：✅ P1 ✅ P2 ✅ P3 ✅ P4 ✅ 全部完成
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
| **P2** | 模块内一般内联（多语句、分支、循环、多 return、用户函数调用、struct 参数、void） | ⭐⭐⭐ | ✅ |
| **P3** | 跨模块内联（XLOAD_MVAR 替换 + ServiceBinding AST 传递） | ⭐⭐⭐ | ✅ |
| P4 | 深度内联（A→B→C 链式展开） | ⭐⭐ | ✅ |

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
| IN05 | P2: 含用户函数调用的函数也内联 | 调用其他 user func → 嵌套内联 |
| IN06 | 超 InlineThreshold 不内联 | 大函数退化到 CALL |
| IN07 | @inline 标注 + 无法内联 → 警告 | IsInline + yield → warning 输出 |
| IN08 | 多处调用同一函数都内联 | 函数被调用 3 次，3 次都内联展开 |

---

## 三-B、P2 子步骤

### P2-1: 解除 P1 限制

- CanInline：允许 if/else/while/for/block（仍拒绝 yield/wait/defer/using）
- CanInline：允许用户函数调用（递归通过 _inlineStack 守护）
- CanInline：允许 struct 参数
- IsInlineSafeStmt：新增 IfStmt/WhileStmt/ForStmt/BlockStmt 递归检查
- IsInlineSafeExpr：CallExpr 不再被排除（MemberCallExpr 仍然排除）
- EstimateStmtSize：IfStmt/WhileStmt/ForStmt 正确估算（不再返回大数）

### P2-2: 多 return 支持（exit label 模式）

- 新增 `_inlineExitJumps: List<int>` 和 `_inlineDestReg: int` 编译器字段
- CompileReturn 检测内联上下文（`_inlineExitJumps != null`）：
  - 将返回值编译到 `_inlineDestReg`
  - 发射 JUMP 到退出点（前向跳转，待回填）
  - 不发射 RET/RETURN 指令
- TryInlineCall 在内联体编译完成后回填所有退出跳转到 CurrentIP

### P2-3: Void 内联

- P2 不再要求最后一条语句为 ReturnStmt
- 所有语句通过 CompileStmt 编译，包括最后一条
- 无返回值的函数体自然结束（退出跳转由 return 语句触发，若无 return 则顺序执行到结尾）

### P2-4: Struct 参数支持

- TryInlineCall 参数绑定阶段：检测 struct 类型参数
- 为 struct 参数调用 DeclareStructVar 分配连续寄存器
- 从 arg 寄存器逐字段复制到本地 struct 变量

### P2-5: 临时寄存器安全（inlineTempBaseline）

- TryInlineCall 在内联体 for 循环前保存 `_tempTop` 为 `inlineTempBaseline`
- 每条语句编译后重置 `_tempTop = inlineTempBaseline`（而非全局 `ResetTemps()`）
- 确保外层临时寄存器（actualDest、arg 寄存器）在内联体内不被覆盖

### P2-6: R8 清理块守护

- TryInlineCall 开头检查 `_inCleanupBlock`，若在清理块内则不内联
- 让 EmitUserCall 的 R8 错误检查正常触发

### P2-7: FO6 窗口分析修正

- 新增 `_inlinedCalleesPerFunc` 字典，在 TryInlineCall 成功时记录被内联的 callee
- AnalyzeCallDepth 的 ComputeMaxWindow 跳过已内联的 callee
- 避免寄存器窗口双重计算（内联 callee 的寄存器已包含在调用方窗口中）

### P2-8: 测试 IN09-IN23

| # | 测试 | 验证 |
|---|------|------|
| IN09 | if-else 分支 + 多 return | `abs(-5)+abs(3)=8` |
| IN10 | 三路 if 多 return | `sign()` 正负零 → 90 |
| IN11 | 嵌套内联 A→B | `sumOfSquares(3,4)=25` |
| IN12 | void 函数内联 | `greet(5)→105, greet(10)→110` |
| IN13 | clamp(x,lo,hi) 多 return | 3 种情况 → 510 |
| IN14 | max(a,b) 变量 + 分支 | `max(3,7)=7, max(10,2)=10` |
| IN15 | 3 层嵌套内联 | `add4(10)=14` |
| IN16 | struct 参数内联 | `area(Box2{3,5})=15` |
| IN17 | void 内联 + syscall 副作用 | `emitTwo(10,20)` |
| IN18 | 深度限制 (InlineDepthMax=3) | 4 层链 → 至少 1 个 CALL |
| IN19 | yield 不内联 | CALL 发射 |
| IN20 | 多 if 变量变异 | `classify(200)=6` |
| IN21 | @inline 诊断 (yield) | warning 输出 |
| IN22 | void + early return | `maybeReport(5,-3,10)→5,10` |
| IN23 | struct 参数 + 分支 | `bigger({3,7})+bigger({10,2})=17` |

---

## 三-C、P3 子步骤（跨模块内联）

### P3-1: 数据结构

- 新增 `ModuleInlineInfo` 类（ExportTable.cs）：包含 FuncDecls, VarExportIndices, ConstValues, AllFuncNames
- 扩展 `CompileResult` 添加 `InlineInfo` 字段
- 扩展 `ServiceBinding` 添加 `InlineInfo` 字段和新构造器

### P3-2: 编译产物填充

- `BuildModuleInlineInfo`：在 CompileModule 末尾构建 ModuleInlineInfo
- 导出函数 AST → FuncDecls
- 导出变量 name → export var index → VarExportIndices
- 模块常量 → ConstValues
- 全部函数名 → AllFuncNames（用于区分 callee 用户函数和 syscall）

### P3-3: 编译器字段

- `_xInlineSvcReg: int`（跨模块内联时 service instance 寄存器，-1=非活跃）
- `_xInlineVars: Dictionary<string, int>`（callee 导出变量 name → export var index）

### P3-4: CanInlineCrossModule

- 深度限制 + 递归守护 + R8 清理块守护
- IsCrossModuleInlineSafeStmt/Expr：与 P2 相同安全检查，额外拒绝 callee 用户函数调用
- AllVarRefsResolvable：预检查所有变量引用可解析（参数/导出变量/常量/局部声明）
- EstimateBodySize ≤ InlineThreshold

### P3-5: TryInlineMemberCall

- 集成到 CompileMemberCallExpr，在 DegradationType.None 分支、XCALL 发射前尝试
- 参数编译 + 内联作用域设置（保存/恢复 _variables, _constValues, _structVarTypes, _liveRanges 等）
- 填充 callee 常量到 _constValues
- 设置 _xInlineSvcReg 和 _xInlineVars 上下文
- 参数绑定（同 P2 TryInlineCall）
- 编译内联体（支持多 return exit label 模式）
- 恢复所有编译器状态

### P3-6: 变量读写重定向

- CompileIdentifierExpr 中：_xInlineVars 检查 → XLOAD_MVAR 发射
- CompileAssignExpr 标量赋值中：_xInlineVars 检查 → XSTORE_MVAR 发射

### P3-7: 测试 XIN01-XIN10

| # | 测试 | 验证 |
|---|------|------|
| XIN01 | 基础跨模块 getter 内联 | `svc.get_hp()` 内联为 XLOAD_MVAR，无 XCALL |
| XIN02 | 导出变量算术 | `base_mod + combo * 2` → XLOAD_MVAR + 算术 |
| XIN03 | 模块常量传播 | `base_val * MULTIPLIER` → 常量折叠 |
| XIN04 | 参数 + 导出变量 | `x + bonus` → 参数 + XLOAD_MVAR |
| XIN05 | if-else 分支 | `classify(x)` → 多 return exit label |
| XIN06 | 跨模块 + 模块内组合内联 | `double_it(svc.get_val())` 两层内联 |
| XIN07 | 超大函数 → XCALL 退化 | body 超 InlineThreshold |
| XIN08 | callee 调用自身函数 → XCALL 退化 | `helper()` 在 callee 内 |
| XIN09 | @inline + 非导出变量 → 警告 | `internal_state` 不可解析 |
| XIN10 | 导出变量写入 | `counter = counter + 1` → XSTORE_MVAR |

### P3 约束/妥协

- **仅标量导出变量**：struct 类型的导出变量跨模块读写暂不支持内联（需逐字段 XLOAD/XSTORE_MVAR）
- **不支持 callee 调用自身模块函数**：~~P3 保守策略~~ → P4 已解除此限制
- **仅导出变量可内联访问**：callee 函数引用非导出模块变量时拒绝内联（XLOAD_MVAR/XSTORE_MVAR 需要 export var index）

---

## 三-D、P4 子步骤（深度链式内联）

### P4-1: BuildModuleInlineInfo 包含全部函数 AST

- 修改 `BuildModuleInlineInfo`：`FuncDecls` 包含所有函数 AST（不限于 @export），使 callee 非导出 helper 函数 AST 可用于递归内联检查和展开
- AllFuncNames 无变化（本已包含全部函数名）

### P4-2: CanInlineCrossModule 递归安全检查

- 新增 `CanInlineCrossModule(func, inlineInfo, visited)` 重载，`visited: HashSet<string>` 防止互递归无限展开
- 深度检查变为 `_inlineDepth + visited.Count >= InlineDepthMax`
- `IsCrossModuleInlineSafeExpr` 对 callee 用户函数调用不再直接拒绝，改为递归调用 `CanInlineCrossModule` 验证

### P4-3: _xInlineInfo 上下文

- 新增编译器字段 `_xInlineInfo: ModuleInlineInfo`，在 `TryInlineMemberCall` 进入时设置、退出时恢复
- 在 `CompileExprStmt` 和 `CompileExpr` 的 CallExpr 路径中，检测 `_xInlineInfo != null && _xInlineInfo.FuncDecls.ContainsKey(call.FunctionName)` 拦截 callee 函数调用

### P4-4: TryInlineCalleeFunc

- 新增方法 `TryInlineCalleeFunc(call, destReg, out resultReg)`：在跨模块内联体内递归展开 callee 自身函数
- 复用已有 `_xInlineSvcReg` / `_xInlineVars` / `_xInlineInfo` 上下文（同一 callee 模块）
- 参数绑定 + scope 保存/恢复 + multi-return exit label（与 TryInlineMemberCall 一致）

### P4-5: 测试 DIN01-DIN05 + XIN08 升级

| # | 测试 | 验证 |
|---|------|------|
| XIN08 | P4 升级：callee 调用 helper → 不再 XCALL，全链内联 | 无 XCALL + XLOAD_MVAR + runtime(counter+1=1) |
| DIN01 | 2 级 callee 链：compute→add_bonus→exported var | 无 XCALL + XLOAD_MVAR + runtime((5+10)*2=30) |
| DIN02 | callee helper 含 if-else 分支 | 无 XCALL + runtime(classify(60)=1, classify(30)=0) |
| DIN03 | 非导出变量 → 不可内联 → XCALL 退化 | XCALL 存在 |
| DIN04 | callee helper 写导出变量 | 无 XCALL + XSTORE_MVAR + runtime(double_bump()=3) |
| DIN05 | 大 callee helper 超 InlineThreshold → XCALL 退化 | XCALL 存在 |

---

## 四、与现有优化的交互

- **FO1 Leaf 分析**：内联分析在 leaf 分析之后。被内联的函数仍参与 leaf 分析（其他调用者可能不内联）
- **FO6 窗口重映射**：内联展开的代码使用调用方寄存器空间，FO6 通过排除内联 callee 正确计算
- **B-ε3 常量传播**：内联后常量折叠可进一步优化（内联参数为常量时）
- **A1/A2 退化**：A5 内联是 A1/A2 的泛化。A1/A2 仍独立存在（纯 getter/setter 场景使用 DegradationType 退化，不经过 P3 内联路径）
- **R8 清理块**：内联在清理块内被禁止，保持 R8 安全性

---

## 五、依赖

- 无外部依赖。P1/P2 纯编译器内部改动
- P3 ✅ ServiceBinding 扩展完成（ModuleInlineInfo 传递 FuncDecl AST + VarExportIndices + ConstValues）

---

## 六、验收标准

- [x] IN01-IN08 全通过（P1）
- [x] IN09-IN23 全通过（P2）
- [x] XIN01-XIN10 全通过（P3）
- [x] XIN08 升级 + DIN01-DIN05 全通过（P4）
- [x] 现有 1492 → 1517 测试无回归
- [x] B01-B06 benchmark 无回归
