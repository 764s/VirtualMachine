# B-ζ1 LICM（循环不变量常量提升）✅

## 目标

编译器识别循环体内 LOAD_CONST 引用的常量为循环不变量，提升到循环前一次加载到寄存器，循环体内复用。B04 每迭代约省 ~6 条 LOAD_CONST。

## 完成条件

- [x] 循环体内 LOAD_CONST 常量在循环前预加载到持久寄存器
- [x] 循环体内直接使用预加载的寄存器（不再重复 LOAD_CONST）
- [x] 支持 while / for / FORLOOP 三种循环结构
- [x] 嵌套循环正确处理（内层继承外层已提升常量）
- [x] 所有现有测试通过（1007 assert × 2 模式全通过）
- [x] B04_Branching 指令数显著减少，性能提升

## 实现方案

### 编译期 LICM（非 bytecode post-pass）

在循环编译时：
1. **AST 预扫描**：`CollectLoopLiterals()` 遍历循环体 AST 收集所有字面常量
2. **循环前预加载**：`BeginLoopHoist()` 为每个唯一常量分配隐藏变量寄存器（`$lc{id}`），emit 一次 LOAD_CONST
3. **体内复用**：`EmitLoadConst()` 检查 `_hoistedConstants` map，若常量已提升，直接返回预加载寄存器（0 条指令）

### 关键设计决策

- `Dictionary<int, int> _hoistedConstants`：constIndex → hoisted register
- 嵌套循环 save/restore：内层继承外层 map，结束后恢复
- 每个循环最多提升 8 个常量（`MaxHoistedPerLoop`）
- hoisted 寄存器属于 variable 区域（r16-r47），不受 temp reset 或 P2 dest-redirect 影响
- destReg < 0 时零指令返回 hoisted register；destReg >= 0 时 emit MOVE（比 LOAD_CONST 便宜）

## 性能结果

.NET 8.0.25 | Windows | 8 cores

| Benchmark | Before VM(μs) | After VM(μs) | Δ VM | Before Instrs | After Instrs |
|-----------|----------|---------|------|------|------|
| B01_ArithLoop | 1837.8 | 1525.3 | ↓17% | 16 | 16 |
| B03_NestedLoop | 1198.5 | 946.7 | ↓21% | 18 | 20 |
| B04_Branching | 2590.4 | 1742.4 | **↓33%** | 31 | 28 |
| B05_Accumulator | 4444.1 | 2713.9 | ↓39% | 11 | 11 |

B04 目标（↓30-40%）达成。B05 额外受益（循环体内 `0.5` 常量提升）。

## 功能展望

- LICM 当前仅提升 LOAD_CONST（常量字面量）。未来可扩展至循环不变量表达式提升（如 `a + 1` 中 a 不在循环内修改时整个表达式可提升）。

## 优化展望

- 当前 MaxHoistedPerLoop=8，对绝大多数循环足够。若出现寄存器压力问题可降低阈值或改为动态评估。

## 风险点

- ⚠️ 寄存器压力：每个嵌套循环层级消耗额外 variable 寄存器用于 hoisted 常量。深度嵌套+大量常量可能耗尽 r16-r47（32 槽位）。
  - 缓解：MaxHoistedPerLoop=8 限制，实际场景中极少触及。
  - 消除时间点：永久妥协，32 槽位足够覆盖所有合理场景。

