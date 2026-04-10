# A5 深度内联展开可行性分析

> **状态**：💬 讨论中
> **来源**：Lang-9 — A5 深度内联（函数体展开优化）
> **日期**：2026-04-10

---

## 一、背景

Lang-8 引入了 `@inline` hint 注解，目前仅作为元数据存储于 `ExportFuncEntry.IsInlineHint`，不执行实际函数体展开。编译器在 `@inline` 函数无法退化为 A1/A2（getter→XLOAD_MVAR / setter→XSTORE_MVAR）时发出警告，但仍生成 XCALL。

A5 深度内联的目标是：**编译器对所有满足内联条件的函数主动执行函数体展开**，将 CALL/XCALL 替换为等价的内联指令序列，消除调用开销。

> **核心结论：`@inline` 不改变内联行为。** 编译器总是对所有可内联函数主动内联（无论是否标注 `@inline`）。`@inline` 的唯一作用是：当标注了 `@inline` 的函数无法内联时，根据编译器配置（`InlineFailurePolicy`）打印警告或报编译错误。未标注 `@inline` 的函数若无法内联，则静默退化到 CALL/XCALL，无任何诊断输出。

**已有基础设施**：

| 组件 | 状态 | 位置 |
|------|------|------|
| `@inline` 解析 (Lexer+Parser+AST) | ✅ | `FuncDecl.IsInline`, Parser `@inline` 关键字 |
| `IsInlineHint` 存储到 ExportTable | ✅ | `ExportFuncEntry.IsInlineHint` |
| A1/A2 退化分析 (`DetectFuncDegradation`) | ✅ | `BytecodeCompiler.DetectFuncDegradation()` |
| XCALL + XLOAD/XSTORE_MVAR 基线 | ✅ | VMWorld OpCode 执行, Lang-6 |
| `_warnings` 诊断通道 | ✅ | `CompileResult.Warnings`, LSP severity=2 |
| `ServiceBinding` 跨模块编译 | ✅ | `BytecodeCompiler._serviceBindings` |
| VMConfig 配置模式 | ✅ | `VMConfig` (XCallDepthPolicy) |

---

## 二、两种内联范围

### 2.1 跨模块内联（svc.func → inline）

调用方脚本中 `svc.get_combo_modifier()` 展开为服务脚本的函数体。

**示例**（D_SkillScripting.md A5 设计）：

```
// 内联前
XCALL r0, r_svc, FN_GET_COMBO_MODIFIER       // ~15 ns

// 内联后
XLOAD_MVAR r_tmp1, r_svc, IDX_base_modifier  // 读 base_modifier
XLOAD_MVAR r_tmp2, r_svc, IDX_combo_count    // 读 combo_count
MUL r_tmp3, r_tmp2, r_const_0_1              // combo_count * 0.1
ADD r0, r_tmp1, r_tmp3                       // base_modifier + ...
// ~8 ns，约 2× 提升
```

**核心挑战**：
1. 被调用方的模块变量在服务实例寄存器中 → 需生成 XLOAD_MVAR/XSTORE_MVAR 替代直接寄存器访问
2. ServiceBinding 当前只传 `ExportTable`，需扩展传递目标模块的 `FuncDecl[]` AST
3. 被调用函数若调用自身模块其他函数 → 需递归处理或放弃内联

### 2.2 模块内内联（本模块 func → inline）

同模块 helper 函数（如 `applyHitbox()`, `pushHitPhase()`）在调用点展开。

**优势**（比跨模块显著简单）：
1. 无跨实例问题 — 模块变量同一寄存器空间，直接可达
2. AST 已可用 — `_funcDecls` 已存储所有函数 AST
3. 无 XLOAD_MVAR 需求 — 模块变量直接用原始寄存器编号
4. 退化安全 — 不满足条件 → 正常 CALL/CALL_LEAF

**挑战（仍存在）**：
- 寄存器重映射（被内联函数局部变量 → 调用方空闲寄存器）
- return 路径处理（多 return → 跳转到统一出口）
- 递归检测（直接/间接递归不可内联）
- 指令膨胀控制

---

## 三、内联决策逻辑

> **注意**：CanInline 判定**不检查 `@inline` 标记** — 编译器对所有函数一视同仁地尝试内联。`@inline` 仅影响内联失败时的诊断行为（见§六）。

```
CanInline(func, depth):
  if depth > InlineDepthMax → false
  if func.body contains YieldStmt/WaitStmt → false
  if func.body contains DeferStmt → false
  if func contains recursive call → false
  if EstimateInstructionCount(func) > InlineThreshold → false
  if func accesses module variables AND is cross-module → false (P1/P2 限制)
  → true

// 内联失败后的诊断（与 CanInline 独立）:
OnInlineFailure(func):
  if func.IsInlineHint:
    if InlineFailurePolicy == Warn → emit warning
    if InlineFailurePolicy == Error → emit compile error
  else:
    // 静默退化，无诊断输出
```

**退化条件**（D_SkillScripting.md 已定义）：
函数包含分支/循环/递归/yield → 不内联，退化到 XCALL/CALL。退化完全安全。

---

## 四、关键技术点

### 4.1 参数绑定

被内联函数参数在 AST 中为 `IdentifierExpr`。内联时建立 `paramName → callerArgReg` 映射，在编译内联体时替换标识符解析。

方案：临时推入新 scope，将 `paramName` 映射到调用方已编译的 argReg → 编译内联体 → 弹出 scope。

**优势**：参数零拷贝传递（无 MOVE 到 scratch zone）。

### 4.2 寄存器管理

- 内联体局部变量：分配调用方的空闲 temp 寄存器（`AllocTemp()`）
- 内联体参数：直接指向调用方传入的 argReg
- 内联体临时表达式结果：使用调用方 temp 池

**风险**：内联体 + 调用方的寄存器总需求超过 LocalVarSlots → 拒绝内联，退化到 CALL/XCALL。

### 4.3 return 路径处理

**Trivial case**（单 return）：直接编译 `return.Value` 到 destReg，无需跳转。

**General case**（多 return）：
```
exitLabel = NewLabel()
// 内联体中每个 return expr:
//   编译 expr → destReg
//   JUMP exitLabel
PatchLabel(exitLabel, CurrentIP())
```

### 4.4 模块变量替换（跨模块限定）

跨模块内联时，被调用函数的模块变量访问 → 生成 XLOAD_MVAR/XSTORE_MVAR 指向服务实例。已有 OpCode 基础设施（Lang-6）。

### 4.5 递归检测

需构建调用图或保守策略（函数体含任意用户函数调用 → P1 不内联）。P2 可引入调用图分析。

---

## 五、分阶段路径

| 阶段 | 内容 | 复杂度 | 收益 |
|------|------|--------|------|
| **P1** | 模块内 trivial 内联（单 return，无分支/循环，无模块变量依赖的纯表达式函数） | ⭐⭐ | 消除 CALL 开销 |
| **P2** | 模块内一般内联（多语句、分支、模块变量、多 return） | ⭐⭐⭐ | 通用模块内优化 |
| **P3** | 跨模块内联（A5 原设计，XLOAD_MVAR 替换）— 需 ServiceBinding 扩展传递 AST | ⭐⭐⭐ | 消除 XCALL 开销 |
| **P4** | 深度内联（A→B→C 链式展开）— P2/P3 基础上增量 | ⭐⭐ | 链式调用全展开 |

**建议切入**：P1（模块内 trivial inline）— 复杂度低，收益明确，可独立验证。

---

## 六、配置

`@force_inline` 关键字已取消（D_SkillScripting.md 决策）。`@inline` 不改变内联行为（编译器总是主动内联所有可内联函数）。`@inline` 的唯一作用是：当标注函数无法内联时，触发诊断。诊断严格程度由编译器配置控制：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `InlineThreshold` | 16 | 最大可内联指令数（适用于所有函数） |
| `InlineDepthMax` | 3 | 最大链式展开深度（适用于所有函数） |
| `InlineFailurePolicy` | Warn | **仅作用于 `@inline` 标记的函数**。Warn: 不可内联→警告+退化 / Error: 不可内联→编译错误。未标记 `@inline` 的函数不受此配置影响（静默退化） |

遵循 VMConfig 模式（同 `MaxXCallDepth` / `XCallDepthPolicy`）。

---

## 七、与现有优化的关系

| 优化 | 关系 |
|------|------|
| A1 getter→XLOAD_MVAR | A5 的特例（纯 getter = 单语句 `return moduleVar`） |
| A2 setter→XSTORE_MVAR | A5 的特例（纯 setter = 单语句 `moduleVar = param`） |
| CALL_LEAF | 模块内内联与 CALL_LEAF 互补 — 内联消除调用开销，CALL_LEAF 减少帧开销 |
| Peephole | 内联后的代码可进一步被 peephole 优化（常量折叠、死代码消除） |

---

## 八、风险与退化策略

| 风险 | 影响 | 缓解 |
|------|------|------|
| 寄存器溢出 | 内联体+调用方寄存器总需求超限 | 检测阶段拒绝内联，退化到 CALL/XCALL |
| 指令膨胀 | 多次内联同一函数导致字节码膨胀 | InlineThreshold + 单函数最多内联 N 次 |
| 调试困难 | 内联后源码行号映射混乱 | DAP SourceMap 扩展（标记内联区域） |
| 递归未检测 | 间接递归导致无限展开 | 调用图分析 或 保守策略（P1 跨函数调用即放弃） |
| 语义差异 | 内联后行为与 CALL 不一致 | 严格等价性测试套件 |

**退化路径**：所有情况下，内联失败 → 原样发射 CALL/XCALL。零风险退化，功能完全正确。

---

## 九、工作量估算

| 阶段 | 新增代码 | 测试用例 | 依赖 |
|------|---------|---------|------|
| P1 (trivial inline) | ~200 行编译器 | ~8 个 | 无 |
| P2 (general inline) | ~400 行编译器 | ~15 个 | P1 |
| P3 (cross-module) | ~300 行编译器 | ~10 个 | P2 + ServiceBinding 扩展 |
| P4 (deep chain) | ~100 行增量 | ~5 个 | P2 或 P3 |
| 配置 + LSP | ~100 行 | ~5 个 | P1 |
| **总计** | **~1100 行** | **~43 个** | — |

---

## 十、结论

**可行性：✅ 可行**

**核心设计决策：编译器总是主动内联，`@inline` 仅控制诊断。**
- 编译器对所有满足 CanInline 条件的函数执行内联展开，无论是否标注 `@inline`
- `@inline` 不改变内联行为 — 有无 `@inline`，内联决策完全相同
- `@inline` 的唯一作用：当标注函数无法内联时，根据 `InlineFailurePolicy` 打印警告或报错
- 未标注 `@inline` 的函数若无法内联 → 静默退化到 CALL/XCALL，无诊断输出

1. 基础设施就绪度高 — `@inline` 解析、ExportTable 传播、A1/A2 退化框架、VMConfig 配置模式均已就位
2. 退化路径完全安全 — 不可内联时退化到 CALL/XCALL，零风险
3. 模块内内联（P1/P2）是最佳切入点 — 复杂度显著低于跨模块，AST 已可用，KOF collision helper 直接受益
4. 跨模块内联（P3）需 ServiceBinding 扩展传递 FuncDecl AST — 最大架构变更点
5. 深度内联（P4）是 P2/P3 的增量 — 递归识别内联候选 + 深度控制
