# 调试 Phase 3A：DAP 最小协议（DBG7-A）

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 GR1 ✅ 之后的**调试 Phase 3A**。
> **状态**：✅ 已完成。470 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 SkillScript + 51 Debug + 58 DAP），float + Fix64 双模式通过。
> **前置**：
> - Debug Phase 2（Gate 0）✅ 已完成 — ScriptDebugger 核心（断点桥接 + 变量查看 + 调用栈）
> - GR1 CI 构建矩阵 ✅ 已完成 — 412 项 Assert 双模式通过
> **来源**：
> - [Step_Debug_Decisions.md D-09](Step_Debug_Decisions.md#决策-d-09dbg7-dap-适配器--窄化-mvp12-消息-4-gate-验证) — DAP 窄化 12 消息 + 4-Gate 验证
> - [Step_Debug_Decisions.md D-02](Step_Debug_Decisions.md#决策-d-02外部-idedap-vs-自研调试-ui) — 外部 IDE + DAP 决策
> - [Step_Debug_Decisions.md D-04](Step_Debug_Decisions.md#决策-d-04release-模式隔离) — Release 隔离策略
> - [Outlook_And_Risks.md §八.1](Outlook_And_Risks.md#81-总览时间线) — 串行计划中 Phase 3A 位置
> - [VM_Summary.md §七](../VM_Summary.md#七推进顺序串行计划) — 串行计划
>
> **核心目标**：实现 DAP 最小协议 Server，使 VS Code 可通过 launch.json 连接 StandaloneRunner，
> 在命中断点时显示调用栈与变量值（Gate 1 验收）。

---

## 〇、串行计划执行状态分析

### 已完成（按串行顺序）

| # | 步骤 | 状态 | Assert 数 |
|---|------|------|-----------|
| 1-4 | 曳光弹（VMInstanceState + TreeWalker + 字节码 + V1/V2） | ✅ | — |
| 5 | MOVE/COPY + JUMP + 比较布尔 + V3/V4 | ✅ | — |
| 6 | Lexer + Parser + BytecodeCompiler + C01-C22 + B01-B05 | ✅ | 70 |
| 7 | using + Paired Syscall + wait_for（C1-C3, G1-G2） | ✅ | — |
| 8 | 函数调用 F1-F3 | ✅ | 279 |
| 9 | 结构体编译期拍平 S1-S3 | ✅ | 303 |
| 10 Pre | C4 + G6 编译器语义检查 | ✅ | 315 |
| F4 | 寄存器生命周期 + 自然优化 7 项 + DBG1/DBG2 + R7/R8 | ✅ | 361 |
| Phase 2 | DBG3 + DBG5 + DBG6（Gate 0 命令行调试） | ✅ | 412 |
| GR1 | CI 构建矩阵 float + Fix64 双模式 | ✅ | 412×2 |

### 串行计划中 GR1 之后的待执行项

| # | 步骤 | 状态 | 阻塞情况 |
|---|------|------|----------|
| V5 | 帧内 Profiler 验证 | ⏳ | **被阻塞**：依赖真实 Syscall 接入 ECS（外部依赖） |
| S4/FF5 | 功能补全（结构体参数 / defer） | ⏳ | 条件执行："步骤 10 前如需" |
| Handle64 | 批处理协议 | ⏳ | 展望项，"不阻塞编辑器" |
| Step 10 | 编辑器流程图投影 | ⏳ | **被 V5 阻塞**："必须在进入步骤 10 前通过" |
| 调整型优化 | O1/O2/O6 等（Benchmark 驱动） | ⏳ | 无硬阻塞 |
| **Phase 3A** | **DAP 最小协议（DBG7-A）** | **⏳** | **无阻塞，前置全部就位** |
| Phase 3B | DAP 单步 + DBG4 | ⏳ | 依赖 Phase 3A |
| Phase 3C | Unity Editor DAP | ⏳ | 依赖 Phase 3B |
| Phase 4 | 语言服务 LSP1-LSP5 | ⏳ | 部分依赖 Phase 3A 通信层 |

### 下一步执行判定

**选择 Phase 3A（DAP 最小协议）作为下一步执行内容**，理由：

1. **前置条件全部满足**：DBG3（断点桥接）✅、DBG5（变量查看）✅、DBG6（调用栈）✅、Gate 0 ✅
2. **无外部阻塞**：不依赖 V5（真实 Syscall/ECS）、不依赖 Step 10
3. **高价值产出**：从命令行调试升级到 VS Code IDE 调试，开发者体验质变
4. **代码量可控**：~600-800 行 C#，风险低（D-09 决策已详细论证）
5. **解锁后续路径**：Phase 3A → Phase 3B（单步）→ Phase 3C（Unity Editor）→ Phase 4（LSP 复用通信层）
6. **串行计划允许**：V5/Step 10 被外部依赖阻塞时，前推独立的调试 Phase 是合理选择

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| 仅实现 12 个 DAP 消息（非全部 50+） | D-09 决策：窄化 MVP，覆盖断点 + 变量 + 调用栈 | 按需扩展 |
| 不实现 DAP 单步（next/stepIn/stepOut） | 属于 Phase 3B（DBG4 + DBG7-B） | Phase 3B |
| VS Code 扩展为纯 JSON 配置（无 TypeScript 逻辑） | 极简化：~60 行 JSON 的 package.json 即可 | 如需可扩展 |
| StandaloneRunner 作为 DAP server（非 Unity 进程） | Gate 1 目标是 CLI 环境验证 | Phase 3C（Unity Editor） |
| 不引入 `FFVM_SCRIPT_DEBUG` 条件编译 | 当前调试代码通过 null-check 隔离已足够 | Phase 3C 引入 |
| JSON 手写序列化（不引入第三方库） | 零外部依赖原则；DAP 消息结构简单 | — |

---

## 二、基础设施盘点

以下组件在之前步骤中已就位，本步骤直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `ScriptDebugger` | ✅ 已有 | 断点集合 + CheckBreakpoint + OnBreakpointHit 回调 |
| `ScriptDebugger.GetVariables()` | ✅ 已有 | SymbolTable → 变量名/值列表（含 struct 展开） |
| `ScriptDebugger.GetCallStack()` | ✅ 已有 | CallStack → SourceMap → 函数名/行号帧列表 |
| `VMProgram.SourceMap` | ✅ 已有 | IP → 行号映射 |
| `VMProgram.SymbolTable` | ✅ 已有 | 变量名 → 寄存器 + struct 字段信息 |
| `VMProgram.Functions` | ✅ 已有 | 函数名 + 入口 IP + 参数数 |
| `VMWorld.Debugger` | ✅ 已有 | 可选挂载，null = 不调试 |
| StandaloneRunner | ✅ 已有 | 独立 .NET 运行环境 |
| CI 工作流 | ✅ 已有 | float + Fix64 双模式矩阵 |

### 需要新增

| 组件 | 说明 | 子任务 |
|------|------|--------|
| `ContentLengthStream` | DAP/LSP 共用的 Content-Length 分帧 I/O 层 | A |
| `DapMessageTypes` | DAP 消息类型定义（Request/Response/Event 基类 + 12 个具体消息） | B |
| `DapServer` | DAP Server 主循环：读消息 → 分发 → 响应 | C |
| 12 个 Request Handler | initialize/launch/setBreakpoints/configurationDone/threads/continue/stackTrace/scopes/variables/disconnect + 2 个 Event（stopped/terminated） | D |
| StandaloneRunner DAP 模式 | 命令行参数 `--dap` 启动 DAP server 模式 | E |
| VS Code 扩展 | `package.json`（~60 行 JSON） | F |
| 自动化测试 | DAP 消息序列化 + 协议交互测试 | G |

---

## 三、子任务清单

### A. Content-Length 分帧 I/O（与 LSP 共享）

- [x] **A1**. 创建 `Assets/Scripts/VM/Debug/ContentLengthStream.cs`
  - `ReadMessage(Stream input)` → 读取 `Content-Length: N\r\n\r\n` + N 字节 body
  - `WriteMessage(Stream output, string body)` → 写出 Content-Length 头 + body
  - 纯 byte 操作，不依赖 Newtonsoft.Json 等外部库
- [x] **A2**. 测试：构造 Content-Length 消息 → ReadMessage 正确解析 — DAP-A01
- [x] **A3**. 测试：WriteMessage → ReadMessage 往返正确 — DAP-A02
  - 额外：DAP-A03 (UTF-8), DAP-A04 (空流), DAP-A05 (多消息序列)

### B. DAP 消息类型定义

- [x] **B1**. 创建 `Assets/Scripts/VM/Debug/JsonHelper.cs`（替代 DapMessages.cs）
  - 轻量级 `JsonObject` 类：手写 JSON 序列化 + 解析，零外部依赖
  - 支持 nested object, array, string, number, boolean, null
  - DAP 消息通过 JsonObject 动态构建（无需静态类型定义）
- [x] **B2**. JSON 序列化/反序列化辅助方法
  - 手写 JSON 解析器（完整 JSON 子集）
  - StringBuilder 序列化 + 字符串转义
- [x] **B3**. 测试：各消息类型 → JSON 序列化 → 反序列化往返正确 — DAP-B01
  - 额外：DAP-B02 (嵌套对象), DAP-B03 (数组), DAP-B04 (转义), DAP-B05 (null)

### C. DAP Server 主循环

- [x] **C1**. 创建 `Assets/Scripts/VM/Debug/DapServer.cs`
  - 构造：`DapServer(Stream input, Stream output)`
  - 主循环：`Run()` — 循环读消息 → 按 command 分发 → 调用 handler → 写响应
  - switch-based 分发（非 Dictionary，更简洁）
  - 生命周期：initialize → launch → (loop: continue/setBreakpoints/stackTrace/...) → disconnect
- [x] **C2**. 错误处理：未知 command → 返回 `success=false` + 错误消息
- [x] **C3**. 测试：发送 initialize → 收到 capabilities 响应 — DAP-C01, DAP-C02

### D. 12 个 Request Handler 实现

- [x] **D1**. `initialize` handler
  - 返回 capabilities：`supportsConfigurationDoneRequest=true`
  - 发送 `initialized` event
- [x] **D2**. `launch` handler
  - 读取 `program` 参数 → 编译脚本 → 创建 VMWorld + Spawn 实例
  - VMProgram / VMWorld 作为 DapServer 实例字段存储（单会话生命周期）
  - disconnect 时置 null
- [x] **D3**. `setBreakpoints` handler
  - 读取 source + breakpoints[] → 更新 ScriptDebugger.BreakpointLines
  - 返回验证后的断点列表（verified=true，行号映射到 SourceMap 中实际有效行）
- [x] **D4**. `configurationDone` handler
- [x] **D5**. `threads` handler — 单线程（threadId=1）
- [x] **D6**. `continue` handler
  - **单线程状态机模型**：循环 Tick → 回调设标志 → 检查标志退出 → 回到消息读取
  - `HaltOnBreakpoint` 模式：VM 在断点处暂停（yield before instruction），保留完整状态
  - `SkipNextCheck` 机制：resume 时跳过当前断点防止重触发
  - 安全保护：最大 100000 Tick 超时
- [x] **D7**. `stackTrace` handler — GetCallStack() → DAP StackFrame[]
- [x] **D8**. `scopes` handler — 单一 Locals scope
- [x] **D9**. `variables` handler — GetVariables() → DAP Variable[]，含 struct 展开
- [x] **D10**. `disconnect` handler
- [x] **D11**. 测试：完整 DAP 会话模拟 — DAP-D01 (9 步完整流程)
- [x] **D12**. 测试：断点命中时 stackTrace 返回正确函数名 + 行号 — DAP-D02
- [x] **D13**. 测试：断点命中时 variables 返回正确变量值 — DAP-D03
  - 额外：DAP-D04 (terminated), DAP-D05 (threads), DAP-D06 (continue after bp), DAP-D07 (unknown cmd)

### E. StandaloneRunner DAP 模式

- [x] **E1**. StandaloneRunner 增加 `--dap` 命令行参数
- [x] **E2**. DAP 模式入口：DapServer(Console.OpenStandardInput(), Console.OpenStandardOutput()) → Run()
- [x] **E3**. 测试：DAP-E01（全管线 ContentLengthStream → DapServer → ContentLengthStream 验证）

### F. VS Code 扩展配置

- [x] **F1**. 创建 `vscode-ffvm-debug/package.json`
  - 扩展 ID: `ffvm-debug`，debuggers + breakpoints + languages + grammars 配置
- [x] **F2**. 创建 `.vscode/launch.json` 模板
- [x] **F3**. 创建 TextMate grammar（`ffvm.tmLanguage.json`）+ language-configuration.json
  - 关键字高亮：control (if/else/while/for/return/yield/wait/wait_for/defer/using)、declaration (func/var/struct)、types (int/bool/void)、numbers、strings、comments

### G. 回归验证 + 文档更新

- [x] **G1**. 运行全部 470 项测试 — 零回归（412 现有 + 58 新 DAP 测试）
- [x] **G2**. float + Fix64 双模式通过（470 × 2）
- [ ] **G3**. 更新 `VM_Summary.md §五` — 标记 Phase 3A 状态
- [ ] **G4**. 更新 `VM_Summary.md §七` — 串行计划中 Phase 3A 完成

---

## 四、Gate 1 验收标准

| 验收项 | 通过标准 |
|--------|---------|
| DAP 连接 | VS Code launch.json 配置后可成功连接 StandaloneRunner DAP server |
| 断点命中 | setBreakpoints → continue → stopped event 正确触发 |
| 调用栈 | stackTrace 返回正确的函数名 + 源码行号 |
| 变量显示 | variables 返回正确的变量名 + 值（含 struct 展开） |
| 自动化测试 | DAP-A01~A02, B01, C01, D01~D03, E01 全部通过 |
| 零回归 | 现有 412 项 Assert 不受影响 |

---

## 五、风险评估

| 风险 | 影响 | 缓解 |
|------|------|------|
| JSON 手写序列化可能有边界 case | DAP 消息解析错误 | 使用 System.Text.Json（.NET 内置）或充分测试手写实现 |
| stdin/stdout 在 VS Code 启动进程时可能有缓冲问题 | 连接失败 | Console.OpenStandardInput/Output + Flush 每条响应 |
| FFVM 单线程模型与 DAP 多线程假设不匹配 | VS Code 显示异常 | threads 返回单一线程，所有操作绑定 threadId=1 |
| ScriptDebugger.OnBreakpointHit 当前为回调模式，DAP 需要阻塞等待 | 断点后无法查询状态 | **单线程状态机**：continue handler 内循环 Tick → 回调设标志 → 检查标志退出循环 → 回到消息读取。无线程阻塞，无死锁风险 |
| VS Code 扩展需要最低 package.json 结构 | 扩展加载失败 | 参考 mock-debug 示例，保持最小配置 |

---

## 六、架构约束检查

| 约束 | 合规说明 |
|------|---------|
| 零外部依赖 | 不引入 NuGet 包，JSON 使用 System.Text.Json（.NET 内置）或手写 |
| Release 零残留 | 调试代码通过 null-check 隔离（当前模式），Phase 3C 时升级为 `#if FFVM_SCRIPT_DEBUG` |
| 零 GC 运行时 | DAP I/O 不在 VMWorld.Tick 热路径中，不影响运行时零分配保证 |
| 脚本文本 = 唯一真理源 | DAP 读取脚本文件路径，编译后调试，不修改脚本文件 |

---

## 七、预估工作量

| 子任务 | 预估代码量 | 复杂度 |
|--------|----------|--------|
| A. Content-Length I/O | ~50 行 | 低 |
| B. DAP 消息类型 | ~150 行 | 低 |
| C. DAP Server 主循环 | ~100 行 | 中 |
| D. 12 个 Handler | ~200 行 | 中 |
| E. StandaloneRunner 集成 | ~30 行 | 低 |
| F. VS Code 扩展 | ~60 行 JSON | 低 |
| G. 测试 + 文档 | ~200 行测试 | 低 |
| **合计** | **~600-800 行** | — |
