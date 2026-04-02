# 调试 Phase 3B：DAP 单步 + 完整调试体验（DBG4 + DBG7-B）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 Phase 3A ✅ 之后的**调试 Phase 3B**。
> **状态**：✅ 完成。505 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 SkillScript + 51 Debug + 93 DAP），float + Fix64 双模式通过。
> **前置**：
> - Debug Phase 3A（Gate 1）✅ 已完成 — DAP Server 核心（12 消息 handler + HaltOnBreakpoint + SkipNextCheck）
> - ScriptDebugger ✅ 已有 — 断点桥接 + 变量查看 + 调用栈
> - VMProgram.SourceMap ✅ — IP → 行号映射
> - VMProgram.Functions ✅ — 函数名 + 入口 IP
> - OpCode.CALL / RET_FUNC ✅ — 函数调用/返回指令
> **来源**：
> - [Step_Debug_Decisions.md D-08](Step_Debug_Decisions.md#决策-d-08dbg4-单步映射--归约为临时断点) — 单步归约为临时断点
> - [Outlook_And_Risks.md §八](Outlook_And_Risks.md#81-总览时间线) — 串行计划中 Phase 3B 位置
>
> **核心目标**：实现 DAP 三种单步行为（next/stepIn/stepOut），使 VS Code 中可进行完整的行级调试体验（Gate 2 验收）。

---

## 〇、设计决策回顾

### D-08 核心决策：单步 = 临时断点

三种单步行为全部归约为"设临时断点 + 继续执行"，复用 DBG3 断点检查路径：

| 操作 | 临时断点位置 | 说明 |
|------|-------------|------|
| **Step Over (next)** | Source Map 中 `IP+1` 起首个 `line != currentLine` 的 IP | 跳到下一行，不进入函数 |
| **Step Into (stepIn)** | 当前 IP 为 CALL → CALL 目标 IP；否则等同 Step Over | 如果当前行有函数调用则进入 |
| **Step Out (stepOut)** | `CallStack` 顶层帧的 `ReturnIP` | 执行到当前函数返回处 |

### 关键机制

- **临时断点**：ScriptDebugger 新增 `_tempBreakpointIP` 字段（int，-1 = 无）。CheckBreakpoint 中额外检查 IP == `_tempBreakpointIP`，命中后自动清除。
- **复用 continue 逻辑**：设好临时断点后，走与 `continue` 完全相同的 Tick 循环。
- **SkipNextCheck**：resume 时跳过第一次检查，防止在当前行重触发。

---

## 一、临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| Step Into 仅检测 CALL OpCode，不检测 SYSCALL | SYSCALL 是宿主调用无法"进入"脚本层 | 设计决策，非妥协 |
| Step Over 对跨越 WAIT 的行为不特殊处理 | WAIT 导致 yield，下次 continue 再命中临时断点 | 行为正确无需特殊处理 |
| 无条件断点 / 日志点 | Phase 3B 聚焦三种单步 | 按需扩展 |

---

## 二、基础设施盘点

以下组件已就位，本步骤直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `DapServer` + 10 个 handler | ✅ Phase 3A | continue / stackTrace / scopes / variables 等已实现 |
| `ScriptDebugger.CheckBreakpoint` | ✅ | 行号断点集合 + SkipNextCheck + HaltOnBreakpoint |
| `ScriptDebugger.GetCallStack` | ✅ | CallStack 遍历 + SourceMap 映射 |
| `VMProgram.SourceMap` | ✅ | IP → 行号平行数组 |
| `VMProgram.Functions` | ✅ | 函数名 + EntryIP 数组 |
| `OpCode.CALL` / `RET_FUNC` | ✅ | CALL: A=targetIP, B=windowSize; RET_FUNC: 弹出 CallFrame |
| `ContentLengthStream` + `JsonHelper` | ✅ | DAP 消息 I/O |
| `DapTests` 测试框架 | ✅ | 58 项现有测试 |

### 需要新增 / 修改

| 组件 | 变更 | 子任务 |
|------|------|--------|
| `ScriptDebugger` | 新增 `_tempBreakpointIP` 临时断点机制 | A |
| `ScriptDebugger` | 新增 `FindNextLineIP()` / `FindStepIntoIP()` / `FindStepOutIP()` | A |
| `DapServer` | 新增 `next` / `stepIn` / `stepOut` handler | B |
| `DapServer` | `HandleContinue` 重构为可复用的 `RunUntilBreakpoint()` | B |
| `DapTests` | 新增单步测试 DAP-S01 ~ DAP-S08 | C |

---

## 三、子任务清单

### A. ScriptDebugger 单步映射（DBG4）

- [x] **A1**. `ScriptDebugger` 新增临时断点字段
  - `private int _tempBreakpointIP = -1;`
  - `public void SetTempBreakpoint(int ip)` → 设置单次临时断点
  - `public void ClearTempBreakpoint()` → 清除
- [x] **A2**. `CheckBreakpoint` 扩展：检查 `_tempBreakpointIP`
  - 在现有行号断点检查之前，增加 IP 精确匹配检查
  - 命中后自动 `_tempBreakpointIP = -1`（单次触发）
  - 命中临时断点时也触发 `OnBreakpointHit` 回调
- [x] **A3**. `FindNextLineIP(VMProgram, int currentIP)` — Step Over
  - 从 `currentIP + 1` 开始扫描 SourceMap
  - 找到首个 `line != currentLine && line > 0` 的 IP
  - 如果找不到（函数末尾），返回 -1
- [x] **A4**. `FindStepIntoIP(VMProgram, int currentIP)` — Step Into
  - 检查 `program.Instructions[currentIP].Code == OpCode.CALL`
  - 如果是 CALL：返回 `Instructions[currentIP].A`（目标函数入口 IP）
  - 如果不是 CALL：退化为 Step Over（返回 `FindNextLineIP` 结果）
- [x] **A5**. `FindStepOutIP(VMProgram, ref VMInstanceState inst)` — Step Out
  - 如果 `inst.CallStackDepth > 0`：返回 `CallStack.Get(depth-1).ReturnIP`
  - 如果 `depth == 0`（已在顶层函数）：返回 -1（无法 Step Out）
- [x] **A6**. 单元测试 — DBG4 核心逻辑
  - DAP-S01: Step Over 找到下一行 IP
  - DAP-S02: Step Into 进入 CALL 目标
  - DAP-S03: Step Into 无 CALL 退化为 Step Over
  - DAP-S04: Step Out 返回 caller
  - DAP-S05: 临时断点单次触发后自动清除

### B. DapServer 单步 handler（DBG7-B）

- [x] **B1**. 重构 `HandleContinue` → 提取 `RunUntilBreakpoint()` 共用方法
  - `RunUntilBreakpoint()` 包含 Tick 循环 + 断点检查 + terminated 逻辑
  - `HandleContinue` 调用 `RunUntilBreakpoint()`
  - 单步 handler 先设临时断点，再调用 `RunUntilBreakpoint()`
- [x] **B2**. `next` handler
  - 获取当前 IP → `FindNextLineIP` → `SetTempBreakpoint` → `RunUntilBreakpoint()`
  - stopped event 的 reason = "step"
- [x] **B3**. `stepIn` handler
  - 获取当前 IP → `FindStepIntoIP` → `SetTempBreakpoint` → `RunUntilBreakpoint()`
  - stopped event 的 reason = "step"
- [x] **B4**. `stepOut` handler
  - 获取当前 inst → `FindStepOutIP` → `SetTempBreakpoint` → `RunUntilBreakpoint()`
  - stopped event 的 reason = "step"
- [x] **B5**. DapServer switch 中注册 `"next"` / `"stepIn"` / `"stepOut"` case

### C. 自动化测试

- [x] **C1**. DAP-S06: 完整 DAP 会话 — next 单步（多行脚本，验证行号递进）
- [x] **C2**. DAP-S07: 完整 DAP 会话 — stepIn 进入函数（验证函数名切换）
- [x] **C3**. DAP-S08: 完整 DAP 会话 — stepOut 返回 caller（验证行号回到调用点）

### D. 回归验证 + 文档更新

- [x] **D1**. 运行全部测试 — 零回归（470 现有 + 新增单步测试）
- [x] **D2**. 更新 `VM_Summary.md §五` — 标记 Phase 3B 状态
- [x] **D3**. 更新 `VM_Summary.md §七` — 串行计划中 Phase 3B 完成

---

## 四、Gate 2 验收标准

| 验收项 | 通过标准 |
|--------|---------|
| next (Step Over) | 断点命中后 next → 跳到下一行（不进入函数），stackTrace 行号正确 |
| stepIn (Step Into) | CALL 行 stepIn → 进入目标函数首行，stackTrace 函数名切换 |
| stepOut (Step Out) | 函数内 stepOut → 返回到 caller 的 CALL 下一行 |
| 回退行为 | stepIn 在非 CALL 行 → 等同 next；stepOut 在顶层 → terminated |
| 自动化测试 | DAP-S01~S08 全部通过 |
| 零回归 | 现有 470 项 Assert 不受影响 |

---

## 五、风险评估

| 风险 | 影响 | 缓解 |
|------|------|------|
| Step Over 跨越 WAIT 指令时临时断点命中时机 | 用户下次 continue 才命中 | 行为正确：WAIT 导致 yield，临时断点在后续 Tick 中正常触发 |
| Step Into 当前 IP 不在 CALL 指令（优化后指令顺序变化） | stepIn 退化为 next | 正确行为：D-08 决策明确"非 CALL 退化为 Step Over" |
| 临时断点与用户断点同时存在 | 可能命中用户断点而非临时断点 | 正确行为：先命中谁都可以，用户断点更高优先级 |

---

## 六、预估工作量

| 子任务 | 预估代码量 | 复杂度 |
|--------|----------|--------|
| A. ScriptDebugger 单步映射 | ~60 行 | 低 |
| B. DapServer handler | ~80 行 | 低 |
| C. 自动化测试 | ~200 行 | 低 |
| D. 回归验证 + 文档 | — | 低 |
| **合计** | **~340 行** | — |
