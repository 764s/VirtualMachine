# 脚本调试决策文档

> **定位**：本文件是脚本调试体系的专用决策记录，完整说明每项调试相关决策的**选型理由**、
> **被否决方案**、以及**最终 / 理想方案**的技术细节。
>
> **引用关系**：
> - [Outlook_And_Risks.md §二.5](Outlook_And_Risks.md#25-脚本调试dbg-系列) — DBG1-DBG7 子项定义
> - [Outlook_And_Risks.md §六](Outlook_And_Risks.md#六风险降级计划目标全部--低--极低) — 风险降级计划
> - [VM_Summary.md §七](../VM_Summary.md#七推进顺序严格串行) — 串行计划中调试阶段的位置
>
> **维护原则**：调试相关的设计决策变更时同步更新本文件。

---

## 一、总体方向决策

### 决策 D-01：真实宿主断点 vs VM 级断点检测

| 维度 | VM 级断点检测（否决） | 真实宿主断点（✅ 采纳） |
|------|---------------------|----------------------|
| 断点机制 | VM Tick 循环中每条指令前检测断点命中 | `System.Diagnostics.Debugger.Break()` 触发宿主真实断点 |
| 多实例冻结 | 需自行设计冻结策略（哪些实例暂停、哪些继续） | 真实断点冻结整个进程，**天然解决多实例调度问题** |
| 性能开销 | 每条指令多一次条件判断（Release 也存在） | `#if FFVM_SCRIPT_DEBUG` 隔离，Release **零开销** |
| 调试体验 | 自研 UI，体验受限于投入 | 与 C# 原生调试一致（IDE 级体验） |
| 实现复杂度 | 中（需处理冻结/恢复/并发） | **极低**（DBG3 仅 ~5 行代码） |

**决策理由**：
1. **零复杂度冻结**：真实断点冻结整个 CLR 进程，无需处理"其他实例在断点期间继续执行"的问题。
2. **Release 零残留**：通过 `#if FFVM_SCRIPT_DEBUG` 条件编译，Release 构建中断点检查代码**完全不存在**。
3. **DBG3 极简性**：宿主断点桥接仅需 5 行代码（Source Map 查表 → `Debugger.Break()`），
   风险极低，可立即实现。

**被否决的替代方案**：
- **VM 级暂停标志位**：在 `VMInstanceState` 中增加 `_isPaused` 标志，Tick 循环检测后跳过。
  缺点：需要处理多实例冻结策略；Release 构建无法完全消除开销。
- **轮询式断点**：在 Tick 结束后检查断点而非每条指令前。
  缺点：断点命中精度不足（只能在 Tick 边界暂停，无法行级断点）。

---

### 决策 D-02：外部 IDE（DAP）vs 自研调试 UI

| 维度 | 自研调试 UI（否决） | 外部 IDE + DAP（✅ 采纳） |
|------|-------------------|-------------------------|
| UI 开发成本 | 高（编辑器面板 + 变量树 + 调用栈视图 + 断点列表） | **零**（VS Code 等已有完善 UI） |
| 调试协议 | 自研（需定义通信格式） | DAP 标准协议（1000+ IDE 支持） |
| 维护成本 | 持续维护 UI 组件 | 仅维护 ~600-800 行 DAP 适配器 |
| 编辑器外调试 | 不支持 | **支持**（任何 DAP 客户端均可） |
| 调试时修改防护 | 需自行检测 | 编辑器标志禁止即可 |

**决策理由**：
1. **投入产出比极高**：DAP 适配器 ~600-800 行 C# 即可获得完整的 IDE 调试体验，
   而自研 UI 预估需要 3000-5000 行代码（含 Editor 面板、自定义 TreeView、事件绑定等）。
2. **生态复用**：VS Code 的调试 UI 已经过数百万用户验证，质量远超可能自研的水平。
3. **无锁定风险**：DAP 是开放标准，未来可切换到任何支持 DAP 的 IDE。

**被否决的替代方案**：
- **Unity Editor 自研面板**：在 EditorWindow 中实现断点管理、变量查看、调用栈显示。
  缺点：开发成本高；Unity Editor 脚本的 UI 能力有限；不支持编辑器外使用。
- **自研 WebSocket 调试协议**：自定义轻量协议通过 WebSocket 通信。
  缺点：需自建调试 UI 前端；无 IDE 生态支持。

---

### 决策 D-03：禁止调试时修改源码

**最终方案**：调试暂停期间编辑器设置只读标志，禁止修改脚本源码。

**决策理由**：
1. 修改源码会导致 Source Map（IP → 行号映射）立即失效。
2. 真实宿主断点方案下，进程冻结期间无法重新编译。
3. 编辑器标志控制成本极低（一行代码）。

**被否决的替代方案**：
- **Edit-and-Continue**：允许修改后增量重编译 + 热替换字节码。
  缺点：实现复杂度极高（需 IP 重映射 + 寄存器状态迁移）；投入不成比例。

---

## 二、Release 模式隔离决策

### 决策 D-04：`FFVM_SCRIPT_DEBUG` 条件编译隔离

**最终方案**：引入编译符号 `FFVM_SCRIPT_DEBUG`，与已验证的 `USE_FIXPOINT` 模式一致。

```
编译配置矩阵：
┌──────────────┬──────────────────┬─────────────────┐
│ 构建模式     │ FFVM_SCRIPT_DEBUG │ USE_FIXPOINT    │
├──────────────┼──────────────────┼─────────────────┤
│ Dev/Editor   │ ✅ 定义          │ ❌ 不定义       │
│ Release      │ ❌ 不定义        │ ✅ 定义         │
│ QA-Debug     │ ✅ 定义          │ ✅ 定义         │ ← 仅内部 QA，不部署
└──────────────┴──────────────────┴─────────────────┘
```

**隔离边界**：

| 层级 | 被隔离的内容 | 隔离方式 |
|------|-------------|---------|
| `VMProgram` | `SourceMap` / `SymbolTable` 字段 | `#if FFVM_SCRIPT_DEBUG` 包裹字段 + 构造函数重载 |
| `BytecodeCompiler` | Source Map emit 逻辑、符号表收集逻辑 | `#if` 包裹对应代码块 |
| `VMWorld.ExecuteInstance` | 断点检查热路径注入点 | `#if` 包裹整个断点检查分支 |
| DAP 适配器 | 整个 `FFVM.Debug` namespace | 独立程序集（asmdef），`#if` + Assembly Definition Reference 双重隔离 |
| LSP Server | 整个 `FFVM.LanguageService` namespace | 独立程序集，不被 Release 构建引用 |

**决策理由**：
1. 模式与已验证的 `USE_FIXPOINT` 完全一致，**无新模式引入**。
2. 双重隔离（`#if` + 独立 asmdef）确保 Release 构建中调试代码**零残留、零开销**。
3. 验证门控明确：`grep -r "FFVM_SCRIPT_DEBUG"` + 体积 diff + 反编译确认。

**被否决的替代方案**：
- **运行时 bool 开关**：`if (debugMode) { ... }`。
  缺点：Release 构建中仍存在分支判断代码；热路径有 branch prediction 开销。
- **仅 asmdef 隔离**：不用 `#if`，仅通过 Assembly Definition 排除。
  缺点：VMProgram/BytecodeCompiler 等核心类中的调试字段无法排除。

---

## 三、各子项方案选择

### 决策 D-05：DBG1 Source Map — 仅记录行号，省略列号

**最终方案**：Source Map 为 `int[]` 平行数组，索引 = IP，值 = lineNumber。省略 column 信息。

**决策理由**：
1. **脚本行通常只有一条语句**：FFVM 脚本风格为每行一条语句，列号信息价值低。
2. **实现极简**：`int[]` 比 `(int line, int col)[]` 节省 50% 内存，查表 O(1)。
3. **可扩展**：如未来确实需要列号，扩展为 `(int, int)[]` 仅改数据结构，不影响上层接口。

**约束**：Source Map 在**所有优化 pass 之后、`_instructions` 冻结时**最终生成。
这确保优化 pass 不会破坏 IP → 行号映射。

**门控测试**：`DBG1_T01` — 编译已知脚本 → 断言每个 LOAD_CONST/SYSCALL IP 的 lineNumber 正确。

---

### 决策 D-06：DBG2 Symbol Table — 与 F4 解耦，分两阶段

**最终方案**：
- Phase 1（随 F4）：只记录 `varName → register`（不含生命周期范围）。
- Phase 2（F4 完成后）：补充寄存器生命周期信息（活跃范围）。

**决策理由**：
1. **解耦依赖**：Phase 1 不需要 F4 的活跃范围信息，可以与 F4 同步启动。
2. **struct 字段映射零额外成本**：编译器 `_structVarTypes` + `_structTypes` 已有数据，只需序列化。
3. **渐进式能力**：Phase 1 完成后 DBG5（变量查看）即可工作（只是不知道变量何时已失效）。

**门控测试**：`DBG2_T01` — 编译含 var + struct 的脚本 → 断言符号表各项正确。

---

### 决策 D-07：DBG3 宿主断点桥接 — HashSet + Debugger.Break()

**最终方案**（伪代码）：

```csharp
#if FFVM_SCRIPT_DEBUG
if (_breakpointLines != null && program.SourceMap != null)
{
    int line = program.SourceMap[inst.IP];
    if (line > 0 && _breakpointLines.Contains(line))
        System.Diagnostics.Debugger.Break();
}
#endif
```

**决策理由**：
1. **5 行代码**，无新依赖，Release 零残留。
2. `HashSet<int>` 行号命中检查 O(1)，Dev 构建中性能可接受。
3. `Debugger.Break()` 冻结整个进程，**天然解决多实例/多线程问题**。

**被否决的替代方案**：
- **断点 IP 集合（而非行号集合）**：用 `HashSet<int>` 存 IP 而非行号。
  缺点：同一行可能编译出多条指令，用 IP 则同一行会命中多次。
- **`_isPaused` 标志位轮询**：设标志后在 Tick 结束时检查。
  缺点：无法在指令级暂停，只能在 Tick 级暂停。

---

### 决策 D-08：DBG4 单步映射 — 归约为临时断点

**最终方案**：三种单步行为全部归约为"设临时断点 + 继续执行"，复用 DBG3 断点检查路径。

| 操作 | 临时断点位置 |
|------|-------------|
| **Step Over** | Source Map 中 `IP+1` 起首个 `line != currentLine` 的 IP |
| **Step Into** | 当前 IP 为 CALL → CALL 目标 IP |
| **Step Out** | `CallStack.ReturnIP` |

**决策理由**：
1. **零新基础设施**：完全复用 DBG3 的断点检查路径，不引入新的执行模式。
2. **实现简单**：每种单步 ≤10 行代码。
3. **行为一致**：单步本质上就是"在特定位置设断点然后继续"，语义清晰。

**被否决的替代方案**：
- **单指令执行模式**：VM 增加 `_singleStep` 标志，每执行一条指令后暂停。
  缺点：粒度过细（用户期望行级单步，不是指令级）；需额外模式管理。

---

### 决策 D-09：DBG7 DAP 适配器 — 窄化 MVP（12 消息）+ 4-Gate 验证

**最终方案**：

1. **协议窄化**：DAP 全协议约 50+ 消息，MVP 仅实现 12 个必需消息：
   `initialize`, `launch`, `setBreakpoints`, `configurationDone`, `threads`,
   `continue`, `next`, `stepIn`, `stepOut`, `stackTrace`, `scopes`/`variables`, `disconnect`。

2. **分阶段验证门控**：

```
Gate 0: 纯命令行调试（零外部依赖）
  → StandaloneRunner 中手动设断点行号 → 命中时 Console 输出调用栈 + 变量值
  → 3 个门控测试通过即可
  → 风险：极低

Gate 1: DAP 最小协议（stdin/stdout JSON-RPC）
  → StandaloneRunner 作为 DAP server → VS Code launch.json 连接
  → 断点命中 + 变量显示
  → 风险：低

Gate 2: DAP 单步 + 完整 VS Code 体验
  → next / stepIn / stepOut → 源码行正确跳转
  → 风险：低

Gate 3: Unity Editor 内嵌 DAP（可选）
  → Unity Editor Play Mode 中 VS Code 附加调试
  → 风险：低（EditorApplication.update 轮询模式）
```

3. **代码量预估**：~600-800 行 C#
   - ~150 行：Content-Length 分帧 + JSON-RPC 解析（与 LSP1 共享）
   - ~200 行：12 个 request handler
   - ~150 行：DAP 消息类型定义
   - ~100-300 行：JSON 序列化辅助 + 错误处理 + 生命周期管理

**决策理由**：
1. **Gate 0 是保底能力**：即使 DAP 对接遇到问题，命令行调试已经可用。
2. **协议窄化大幅降低复杂度**：50+ → 12 消息，实现量减少 75%。
3. **VS Code 扩展极简**：纯 JSON 配置（`package.json` + `launch.json`，~60 行），无 TypeScript 逻辑。

**被否决的替代方案**：
- **使用第三方 DAP 库**：如 `Microsoft.VisualStudio.Shared.VSCodeDebugProtocol`。
  缺点：引入 NuGet 依赖；Unity 兼容性不确定；违反零外部依赖原则。
- **一次性实现全部 DAP**：实现全部 50+ 消息。
  缺点：工作量过大；大部分消息无需使用；无法渐进验证。

---

### 决策 D-10：Unity Editor 线程模型 — 轮询而非多线程

**最终方案**：Unity Editor 模式下用 `EditorApplication.update` 轮询 DAP 消息，
而非在独立线程中处理 I/O。

```csharp
// Unity Editor 模式
private void OnEditorUpdate()
{
    if (_dapServer != null && _dapServer.HasPendingMessage())
    {
        var msg = _dapServer.ReadMessage(); // 非阻塞
        HandleDapMessage(msg);
    }
}
```

**决策理由**：
1. **规避线程安全问题**：Unity API 不是线程安全的，从后台线程调用 Unity API 会导致异常。
2. **轮询开销可忽略**：`EditorApplication.update` 每帧一次，检查是否有 DAP 消息的成本极低。
3. **StandaloneRunner 仍可用同步 I/O**：stdin/stdout 同步读写，无线程问题。

**被否决的替代方案**：
- **后台线程 + 主线程 Invoke**：DAP I/O 在独立线程，通过 Invoke 回主线程。
  缺点：正确但复杂度高；需 ConcurrentQueue + 同步原语。
- **async/await + SynchronizationContext**：使用 Unity 2023+ 的 Awaitable。
  缺点：对 Unity 版本有依赖；不兼容旧版本。

---

## 四、语言服务决策

### 决策 D-11：LSP 而非自研语言智能

**最终方案**：通过 LSP（Language Server Protocol）实现语言智能，与 DAP 共享通信层。

**决策理由**：
1. **与 DAP 策略一致**：DAP 用于调试，LSP 用于编辑，两者共同构成完整的外部 IDE 支持。
2. **通信层复用**：`ContentLengthStream` + JSON-RPC 解析与 DAP 完全共享。
3. **IDE 生态覆盖**：VS Code、Sublime Text、Vim/Neovim 等均支持 LSP。

### 决策 D-12：LSP 窄化 — 初版仅 4 个核心消息

**最终方案**：初版 LSP Server 仅实现：
- `initialize`
- `textDocument/didOpen`
- `textDocument/didChange`
- `textDocument/publishDiagnostics`

**决策理由**：
1. 最快实现"实时报错"核心能力（用户输入 → 即时红线反馈）。
2. 复用 `BytecodeCompiler._errors` 错误列表（已有行号信息）。
3. LSP4/LSP5（符号分析、补全）可在后续逐步叠加。

### 决策 D-13：初版不做增量编译

**最终方案**：LSP3（实时诊断）使用全量重编译，不做增量编译。

**决策理由**：
1. 当前编译器 ~800 行，编译速度对小脚本文件足够（<10ms）。
2. 增量编译复杂度高（需追踪依赖图、缓存 AST），投入不成比例。
3. 可在后续性能瓶颈出现时再引入。

### 决策 D-14：LSP2（语法高亮）独立于 LSP Server

**最终方案**：LSP2 用纯 TextMate Grammar（.tmLanguage.json）实现，
约 80-100 行 JSON，定义 16 个关键字 + 运算符 + 字面量 + 注释的着色规则。

**决策理由**：
1. TextMate Grammar 不需要 LSP Server 运行，**可立即实施**。
2. VS Code 原生支持 .tmLanguage.json，零运行时依赖。
3. Semantic Tokens（上下文感知着色）可在 LSP Server 就绪后叠加。

---

## 五、风险应对策略决策

### 决策 D-15：所有风险选择理想方案（不保守）

以下是每个风险点的**最终 / 理想**应对方案（非保守的妥协方案）：

#### R1（寄存器窗口嵌套层数不足）
- **理想方案**：FO6 自适应寄存器窗口。编译器分析每个函数实际使用的寄存器数量，
  窗口偏移 = max(实际使用数) 而非固定 64。嵌套从 ~3 层扩展到 ~6 层。
- **否决保守方案**："3 层够用"的消极等待。

#### R5（结构体作为函数参数窗口空间不足）
- **理想方案**：S4 实施时 struct 参数仅允许 ≤4 字段的结构体直接传递，>4 时编译报错。
  与 FO6 联合评估后解除限制。
- **否决保守方案**：不支持结构体参数。

#### R7（前向引用回填性能）
- **理想方案**：当函数数量 >50 时自动将 `_pendingCalls` 从 `List` 切换为 `Dictionary`。
  编译器内部阈值控制，对用户透明。
- **否决保守方案**：保持线性扫描（"百函数级模块下 <1ms"）。

#### R8（Cleanup 块内函数调用语义）
- **理想方案**：编译器增加一行检查，禁止 Cleanup 块内函数调用（与 G6 禁止 wait 一致）。
- **否决保守方案**：仅在文档中标注限制。

#### SR1（struct 耗尽 local 区 32 槽）
- **理想方案**：编译器超限报错 + 明确的错误信息提示用户减少 struct 变量数量。
  后续 FO6 扩大 local 区后自动缓解。
- **否决保守方案**：仅文档警告。

#### SR2（大 struct 赋值 N×MOVE 性能退化）
- **理想方案**：SO1 COPY_BLOCK OpCode。新增一条指令替代 N 条 MOVE 的结构体赋值，
  内部使用 `Buffer.MemoryCopy` 批量拷贝。
- **否决保守方案**：等待业务需要再评估。

#### GR1（Fix64 未独立验证）
- **理想方案**：CI 构建矩阵中增加 `USE_FIXPOINT` 构建配置，每次提交自动验证。
  构建配置矩阵确保 Release 强制 USE_FIXPOINT。
- **否决保守方案**：步骤 10 前手动验证一次。

#### DR5（Unity Editor 主线程阻塞）
- **理想方案**：轮询模式（`_isPaused` 标志位 + `EditorApplication.update` 检查）。
  主线程永不阻塞，DAP 消息在 update 回调中处理。
- **否决保守方案**：仅在 StandaloneRunner 中支持调试。

---

## 六、扩展串行计划（调试专项）

> 以下计划是 [VM_Summary.md §七](../VM_Summary.md#七推进顺序严格串行) 和
> [Outlook_And_Risks.md §八](Outlook_And_Risks.md#八扩展串行计划理想方案) 中调试部分的详细展开。

### Phase 1：编译器侧（与 F4 合并，边际排期极低）

```
F4. 编译器寄存器生命周期分析 + 跨 await 变量提升
  │
  ├─ DBG1. 源码映射表（Source Map）
  │    实现：编译器每次 emit 指令时记录当前行号到 int[] 平行数组
  │    约束：在所有优化 pass 之后、_instructions 冻结时最终生成
  │    门控：DBG1_T01 通过
  │
  └─ DBG2. 符号表（Symbol Table）— Phase 1
       实现：varName → register 映射（不含生命周期范围）
       复用：_structVarTypes + _structTypes 序列化
       门控：DBG2_T01 通过
```

**验收标准**：DBG1_T01 + DBG2_T01 通过。

### Phase 2：运行时侧（Gate 0 命令行调试）

```
DBG3. 宿主断点桥接
  │    实现：5 行代码（见 D-07）
  │    #if FFVM_SCRIPT_DEBUG 包裹
  │    门控：DBG3_T01（断点命中验证）
  │
  ├─ DBG5. 变量查看适配器
  │    实现：Symbol Table + 寄存器值 → 变量名 + 可读值（含 struct 字段展开）
  │    门控：DBG5_T01（变量值显示正确）
  │
  └─ DBG6. 调用栈查看
       实现：CallStack → Source Map 映射 → 函数名 + 源码位置
       门控：DBG6_T01（调用栈显示正确）
```

**Gate 0 验收**：StandaloneRunner 中设断点行号 → 命中时 Console 输出调用栈 + 变量值。
3 个门控测试通过。**此时已具备命令行调试能力**。

### Phase 3A：DAP 最小协议

```
DBG7-A. DAP Server 核心（stdin/stdout JSON-RPC）
  │    实现：Content-Length 分帧 + 12 个必需消息 handler
  │    独立程序集 FFVM.Debug，#if FFVM_SCRIPT_DEBUG
  │    门控：Gate 1（VS Code 断点命中 + 变量显示）
  │
  └─ VS Code 扩展
       实现：package.json + launch.json（~60 行 JSON，无 TypeScript）
```

**Gate 1 验收**：VS Code launch.json 连接 → setBreakpoints → continue → 命中 →
stackTrace + variables 正确。

### Phase 3B：DAP 单步 + 完整体验

```
DBG4. 单步映射（Step Over / Into / Out）
  │    实现：归约为临时断点（见 D-08），每种 ≤10 行
  │    门控：DBG4_T01/T02/T03（三种单步验证）
  │
  └─ DBG7-B. DAP next/stepIn/stepOut handler
       门控：Gate 2（VS Code 中三种单步行为正确）
```

**Gate 2 验收**：VS Code 中 next / stepIn / stepOut → 源码行正确跳转。

### Phase 3C：Unity Editor 内嵌 DAP（可选）

```
DBG7-C. Unity Editor DAP 集成
  │    实现：EditorApplication.update 轮询（见 D-10）
  │    门控：Gate 3（Editor 模式下断点命中 + 变量查看）
```

### Phase 4：语言服务

```
LSP2. 语法高亮（独立，可最早实施）
  │    实现：TextMate grammar .tmLanguage.json（~80-100 行）
  │
  ├─ LSP1. LSP Server 核心框架
  │    实现：JSON-RPC 通信（复用 DAP 通信层） + initialize/shutdown + 文档同步
  │
  ├─ LSP3. 实时诊断
  │    实现：全量重编译（不做增量）→ _errors → publishDiagnostics
  │
  ├─ LSP4. 符号分析（Go-to-Definition / References / Hover）
  │    实现：基于 DBG2 符号表
  │
  └─ LSP5. 代码补全
       实现：关键字 + 作用域变量 + 函数名 + Syscall 名 + struct 字段
```

---

## 七、决策追溯索引

| ID | 决策 | 选择 | 否决 |
|----|------|------|------|
| D-01 | 断点机制 | 真实宿主断点 (`Debugger.Break()`) | VM 级检测 |
| D-02 | 调试 UI | 外部 IDE + DAP | 自研 Editor 面板 |
| D-03 | 调试时修改 | 禁止（编辑器只读标志） | Edit-and-Continue |
| D-04 | Release 隔离 | `FFVM_SCRIPT_DEBUG` + 独立 asmdef | 运行时 bool / 仅 asmdef |
| D-05 | Source Map 格式 | `int[]`（仅行号） | `(int, int)[]`（行+列） |
| D-06 | 符号表时序 | 与 F4 解耦，分两阶段 | 等 F4 完成后再开始 |
| D-07 | 断点桥接实现 | HashSet 行号 + Debugger.Break() | IP 集合 / 轮询标志 |
| D-08 | 单步实现 | 归约为临时断点 | 单指令执行模式 |
| D-09 | DAP 范围 | 窄化 12 消息 + 4-Gate 验证 | 第三方库 / 全协议 |
| D-10 | Editor 线程模型 | EditorApplication.update 轮询 | 后台线程 / async-await |
| D-11 | 语言智能方案 | LSP 标准协议 | 自研语言服务 |
| D-12 | LSP 初版范围 | 4 个核心消息 | 全协议 |
| D-13 | LSP 编译策略 | 全量重编译 | 增量编译 |
| D-14 | 语法高亮方案 | TextMate Grammar（独立于 LSP） | 仅 Semantic Tokens |
| D-15 | 风险应对策略 | 全部选择理想方案 | 保守妥协 |
