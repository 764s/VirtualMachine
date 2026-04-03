# 调试 Phase 2：运行时调试基础能力（DBG3 + DBG5 + DBG6）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 F4 之后的**脚本调试 · Phase 2**。
> **状态**：✅ 已完成。412 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 FFScript + 51 Debug）。
> **前置**：F4 ✅ 已完成（361 项 Assert 通过）。DBG1（SourceMap）✅、DBG2 Phase 1（SymbolTable）✅ 已就位。
> **来源**：
> - [Outlook_And_Risks.md §二.5](Outlook_And_Risks.md#25-脚本调试dbg-系列) — DBG3/DBG5/DBG6 定义
> - [Step_Debug_Decisions.md](Step_Debug_Decisions.md) — 决策 D-01（真实宿主断点）、D-02（DAP）、D-04（Release 隔离）
> - [VM_Summary.md §七](../VM_Summary.md#七推进顺序串行计划) — 串行计划中 Phase 2 位置
>
> **核心原则**：Phase 2 的目标是在**零外部依赖**下建立命令行调试能力（Gate 0）。
> 所有调试代码通过 `#if FFVM_SCRIPT_DEBUG` 条件编译隔离，Release 构建零残留。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| DBG4（单步映射）不在本步骤实现 | 属于 Phase 3B，依赖 DAP 接入后才有价值 | Phase 3B |
| DBG7（DAP 适配器）不在本步骤实现 | 属于 Phase 3A，独立排期 | Phase 3A |
| 仅支持行号级断点（非字节码级） | Source Map 粒度为行号，满足 Gate 0 需求 | 未来如需可扩展 |
| 断点回调替代 `Debugger.Break()` | StandaloneRunner 自动化测试无法使用真实断点（会阻塞进程） | 正式使用时切换为 `Debugger.Break()` |

---

## 二、基础设施盘点

以下组件在之前步骤中已就位，本步骤直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `VMProgram.SourceMap` | ✅ 已有 | DBG1：`int[]` 平行数组，IP → 源码行号 |
| `VMProgram.SymbolTable` | ✅ 已有 | DBG2：`SymbolEntry[]`，变量名 → 寄存器 + struct 字段信息 + 作用域 |
| `VMProgram.Functions` | ✅ 已有 | `FunctionEntry[]`，函数名 + 入口 IP + 参数数 + LocalRegCount |
| `VMInstanceState.CallStackDepth` | ✅ 已有 | 当前调用栈深度 |
| `VMInstanceState.CallStack` | ✅ 已有 | `CallStackFrames`，16 层 CallFrame（ReturnIP, RegisterBase, CleanupBase） |
| `VMInstanceState.IP` | ✅ 已有 | 当前指令指针 |
| `VMInstanceState.RegisterBase` | ✅ 已有 | 当前寄存器窗口基址 |
| `VMInstanceState.Registers` | ✅ 已有 | `NumberRegisters`，64 个 Number 槽位 |

### 需要新增

| 组件 | 说明 | 子任务 |
|------|------|--------|
| `ScriptDebugger` 类 | 调试桥接器：管理断点集合 + 提供查询 API | A（DBG3 核心） |
| `ScriptDebugger.BreakpointLines` | `HashSet<int>` 行号断点集合 | A |
| `ScriptDebugger.OnBreakpointHit` | 回调委托，断点命中时调用（测试用回调，生产用 `Debugger.Break()`） | A |
| `ScriptDebugger.GetVariables()` | 根据 SymbolTable + 当前实例状态返回变量名 → 值映射 | B（DBG5） |
| `ScriptDebugger.GetCallStack()` | 根据 CallStack + Functions + SourceMap 返回调用栈帧列表 | C（DBG6） |
| `VMWorld` 集成 | `ExecuteInstance` 中每条指令前查询断点（`#if` 隔离） | A |

---

## 三、子任务清单

### A. DBG3 — 宿主断点桥接（Host Breakpoint Bridge）

- [x] **A1**. 创建 `Assets/Scripts/VM/Core/ScriptDebugger.cs`
  - `HashSet<int> BreakpointLines` — 行号断点集合
  - `Action<int, int, int> OnBreakpointHit` — 回调 `(instanceId, ip, line)`
  - `void AddBreakpoint(int line)` / `void RemoveBreakpoint(int line)` / `void ClearBreakpoints()`
  - `bool CheckBreakpoint(int instanceId, int ip, int[] sourceMap)` — 查询+触发
- [x] **A2**. `VMWorld` 增加 `public ScriptDebugger Debugger` 字段（可选，null = 不调试）
- [x] **A3**. `VMWorld.ExecuteInstance` 中指令执行前插入断点检查
  - 仅在 `Debugger != null` 时检查（null-check + srcMap cache 优化）
  - 通过 `SourceMap[inst.IP]` 获取当前行号
  - 行号命中 → 调用 `Debugger.OnBreakpointHit(inst.InstanceId, inst.IP, line)`
  - 同一行去重（`_lastHitLine`），每 Tick 重置
- [x] **A4**. 测试：编译脚本 → 设断点行 → 执行 → 验证回调触发（行号、IP 正确）— DBG3-01
- [x] **A5**. 测试：多断点 → 验证每个断点均触发 — DBG3-02
- [x] **A6**. 测试：无断点 → 验证执行不受影响，正常完成 — DBG3-03
- [x] **A7**. 测试：Debugger == null → 验证零开销，行为与未修改前一致 — DBG3-04
- [x] **A8**. 测试：循环中断点 → 每次迭代（不同 Tick）均触发 — DBG3-05

### B. DBG5 — 变量查看适配器（Variable Display Adapter）

- [x] **B1**. `ScriptDebugger.GetVariables(VMProgram, ref VMInstanceState)` 方法
  - 返回 `List<VariableInfo>`
  - 结构：`{ Name, Value (Number), IsStruct, FieldNames[], FieldValues[] }`
  - 根据当前 `RegisterBase` 正确读取寄存器值
  - 通过 `ScopeFunctionName` 过滤当前函数作用域内的变量
- [x] **B2**. `FindFunctionByIP(VMProgram, int ip)` — 确定当前函数名
  - 遍历 FunctionEntry[] 找到 `EntryIP ≤ IP` 的最后一个
- [x] **B3**. 测试：断点命中时获取标量变量值 → 正确 — DBG5-01
- [x] **B4**. 测试：断点命中时获取 struct 变量 → 字段名 + 字段值正确 — DBG5-02
- [x] **B5**. 测试：函数调用内部断点 → 仅显示当前函数作用域变量 — DBG5-03

### C. DBG6 — 调用栈查看（Call Stack Inspection）

- [x] **C1**. `ScriptDebugger.GetCallStack(VMProgram, ref VMInstanceState)` 方法
  - 返回 `List<CallStackEntry>` 
  - 结构：`{ FunctionName, SourceLine, IP }`
  - 从 CallStack 各帧读取 ReturnIP-1 → SourceMap 映射 → 行号
  - 当前帧 = 当前 IP → SourceMap 映射
  - 顺序：栈顶（当前函数）在前，caller 在后
- [x] **C2**. 函数名解析：复用 `FindFunctionByIP()`
- [x] **C3**. 测试：单函数 → 调用栈只有 1 帧（main），行号正确 — DBG6-01
- [x] **C4**. 测试：func a() 调用 func b()，断点在 b 内 → 调用栈 2 帧，函数名和行号均正确 — DBG6-02
- [x] **C5**. 测试：3 层调用 a→b→c → 调用栈 3 帧 — DBG6-03

### D. 集成 + 回归

- [x] **D1**. 创建 `Assets/Scripts/VM/Tests/DebugTests.cs`，51 项 Assert
- [x] **D2**. `StandaloneRunner/Program.cs` 注册 `DebugTests.RunAll()`
- [x] **D3**. 运行全量测试 — 361 项原有测试零回归 + 51 项调试测试 = 412 项全部通过
- [x] **D4**. 更新 VM_Summary.md §七 — 标记 Phase 2 ✅
- [x] **D5**. 更新 Outlook_And_Risks.md §二.5 — 标记 DBG3/DBG5/DBG6 ✅

---

## 四、Gate 0 验收标准

Gate 0 代表**命令行调试能力**——零外部依赖，仅通过 StandaloneRunner 即可验证。

| Gate 0 验收项 | 对应测试 | 结果 |
|--------------|---------|------|
| 断点命中行号正确 | DBG3-01 | ✅ 通过 |
| 多断点均触发 | DBG3-02 | ✅ 通过 |
| 无断点不回调 | DBG3-03 | ✅ 通过 |
| 循环中多次触发 | DBG3-05 | ✅ 通过 |
| 断点命中时变量值正确 | DBG5-01 | ✅ 通过 |
| 断点命中时 struct 字段展开 | DBG5-02 | ✅ 通过 |
| 变量仅当前作用域 | DBG5-03 | ✅ 通过 |
| 断点命中时调用栈正确 | DBG6-01/02/03 | ✅ 通过 |
| Debugger == null 零开销 | DBG3-04 | ✅ 通过 |
| 原有 361 项测试零回归 | D3 | ✅ 通过 |

---

## 五、设计要点

### 5.1 Release 隔离策略

```
#if FFVM_SCRIPT_DEBUG
    // 所有调试代码仅在此条件编译符号下存在
    // StandaloneRunner 默认定义此符号（便于测试）
    // Unity Release 构建不定义此符号 → 调试代码完全消失
#endif
```

由于 StandaloneRunner 用于测试，调试代码在 StandaloneRunner 中**始终可用**。
条件编译隔离的目标是 Unity 的 Release 构建。

**本步骤的务实决策**：ScriptDebugger 类**不使用条件编译**包裹整体类定义，
而是让 `VMWorld.Debugger` 字段始终存在（nullable），检查逻辑通过 `if (Debugger != null)` 防御。
这样做的理由：
1. 避免 `#if` 跨多文件传染，保持代码简洁
2. `Debugger == null` 时 JIT 可优化掉整个分支（null check 近零开销）
3. 真正需要条件编译隔离的是 `Debugger.Break()` 调用（Phase 3A 的 DBG7）

### 5.2 断点触发策略

- **行级去重**：同一行的多条指令只触发一次断点。通过在 ScriptDebugger 中记录 `_lastHitLine` 实现。
  每次 Tick 开始时重置，允许下次经过同一行时再次触发。
- **回调模式**：Phase 2 使用 `Action<int, int, int>` 回调，测试中用 lambda 收集断点事件。
  Phase 3A（DAP）时替换为 DAP 协议消息发送。

### 5.3 函数查找策略

根据 IP 查找当前所在函数：遍历 `VMProgram.Functions[]`，找到 `EntryIP ≤ ip` 的最后一个函数。
Functions 数组按编译顺序排列（即 EntryIP 递增），因此可用线性扫描。
