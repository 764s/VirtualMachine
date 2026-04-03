# B-γ1: FO6 自适应寄存器窗口（Adaptive Register Window）

> **目标**：编译器为每个函数精确计算窗口大小（locals + temps），
> 消除 temp 区固定偏移导致的嵌套溢出，将嵌套层数从 ~3 扩展到 ~6+，
> 根本解决 R1/SR1 风险。

## 一、背景

**现状**（FO6 之前）：
- 寄存器布局：r0-r15 scratch、r16-r47 locals、r48-r63 temps
- CALL 的窗口偏移 = 调用者 locals 数（`_nextVarReg - VarRegBase`）
- 问题：temps 始终从 r48 开始，但被 RegisterBase 偏移后绝对地址 = r48 + RegisterBase
- 3 层嵌套后 RegisterBase ≥ 16 → temp r48 映射到绝对 r64 → 溢出

**方案**：编译后重映射每个函数的 temp 寄存器到紧接 locals 之后（r[maxLocal+1]+），
CALL 窗口大小 = locals + temps 的总和。

## 二、子任务清单

| # | 子任务 | 说明 | 状态 |
|---|--------|------|------|
| 1 | `_maxTempUsed` 跟踪 | `AllocTemp()` 记录每个函数的峰值 temp 寄存器号 | ✅ |
| 2 | `ComputeAndRemapFunctionWindow` | 编译后遍历指令，重映射 temp 操作数 + 修补 CALL.B 窗口大小 | ✅ |
| 3 | `GetRegisterMask` | 按 OpCode 返回 A/B/C 哪些是寄存器操作数的位掩码 | ✅ |
| 4 | `CompileModule` 集成 | 每个函数编译后调用重映射，FunctionEntry.LocalRegCount 改为总窗口 | ✅ |
| 5 | FO6 累计窗口溢出检测 | `AnalyzeCallDepth` 扩展：DFS 累加窗口大小，超过 48 slot 报编译错误 | ✅ |
| 6 | FO6-01~04 测试 | 4 层/6 层嵌套正确性 + 窗口包含 temps + 溢出编译错误 | ✅ |
| 7 | 全量回归 | 721 项 Assert × 2 模式全通过 | ✅ |

## 三、设计细节

### 3.1 重映射算法

```
输入: 函数指令范围 [startIP, endIP), _maxVarRegUsed, _maxTempUsed
    localCount = _maxVarRegUsed - VarRegBase + 1    (精确 local 窗口)
    numTemps   = _maxTempUsed - TempRegBase + 1     (峰值 temp 数)
    totalWindow = localCount + numTemps
    tempRemapBase = _maxVarRegUsed + 1               (temps 紧接 locals)
    shift = tempRemapBase - TempRegBase               (通常为负数)

对范围内每条指令:
    - CALL/CALL_LEAF → 修补 B = totalWindow
    - 其他指令 → 按 GetRegisterMask 位掩码重映射 ≥ TempRegBase 的操作数
```

### 3.2 溢出检测

在 `AnalyzeCallDepth` 中新增 DFS：
- 每个函数节点贡献 = `FunctionEntry.LocalRegCount`（含 temps）
- 递归累加调用链 max cost
- 若 `maxCost > MaxRegisters - VarRegBase (48)` → 编译错误

### 3.3 效果

| 场景 | FO6 前 | FO6 后 |
|------|--------|--------|
| 平均每函数 5 regs (locals) + 2 temps | 嵌套 ≤3 层 | 嵌套 ≤6 层 |
| 平均每函数 8 regs + 2 temps | 嵌套 ≤2 层 | 嵌套 ≤4 层 |
| 溢出检测 | 运行时 OOB | 编译时报错 |

## 四、测试

| ID | 描述 | 预期 |
|----|------|------|
| FO6-01 | 4 层嵌套 + 表达式 | result = 20 |
| FO6-02 | 6 层嵌套 + 多 locals | result = 37 |
| FO6-03 | LocalRegCount 包含 temps | caller.LocalRegCount > 1 |
| FO6-04 | 10 层 × 6 locals → 溢出 | 编译错误含 "register window" |

## 五、完成条件核验

- [x] 嵌套层数 ~3 → ~6（FO6-02: 6 层嵌套正确执行）
- [x] R1/SR1 根本解决（编译时溢出检测 + temps 紧凑排列）
- [x] 测试通过（721 项 Assert，含 10 项 FO6 新增）
