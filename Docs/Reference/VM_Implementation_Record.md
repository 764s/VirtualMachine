# 实现状态、性能优化与已知缺口

> 本文从 VM_Summary.md 迁出，记录当前实现状态、性能优化记录、已知缺口、持续集成与跨语言性能对比的详细内容。

---

## 一、当前实现状态

### 已完成

| 层级 | 内容 | 状态 |
|------|------|------|
| AST | 44 种节点 + `DeferStmt` + `UsingStmt` + `StructDecl` + `FieldAccessExpr` | ✅ 完成 |
| 数值 | `Number`（float/Fix64 双模式，8B） | ✅ 完成 |
| 状态 | `VMInstanceState`（blittable，含 StateFlags + CleanupStack） | ✅ 完成 |
| 实例池 | `InstancePool`（确定性 free stack） | ✅ 完成 |
| 快照 | `SnapshotRingBuffer`（8 帧环，零分配） | ✅ 完成 |
| Syscall | `SyscallTable`（256 slot，热替换，配对注册协议） | ✅ 完成 |
| 字节码 | `OpCode`（27 条指令，含 WAIT_FOR）+ `Instruction` + `VMProgram` + `VMModuleTable` | ✅ 完成 |
| 调度 | `VMWorld`（Tick 字节码解释循环 + Spawn/Destroy/Save/Load） | ✅ 完成 |
| 解释器 | `TreeWalker`（Phase 2 原型，含 defer + Kill） | ✅ 完成 |
| 词法分析 | `Lexer`（手写，16 关键字 + 运算符 + 字面量 + 注释，含 `struct` 关键字 + `.` 分隔符） | ✅ 完成 |
| 语法分析 | `Parser`（手写递归下降，source → `ModuleNode` AST，含 using/wait_for/struct 声明/字段访问/错误恢复） | ✅ 完成 |
| 编译器 | `BytecodeCompiler`（AST → `VMProgram`，完整寄存器分配、优化、多函数编译、struct 编译期拍平、内联、模块变量、常量配置化） | ✅ 完成 |
| 调试信息 | `VMProgram.SourceMap`（DBG1：IP→行号平行数组）+ `VMProgram.SymbolTable`（DBG2：变量名→寄存器+struct字段信息） | ✅ 完成 |
| 运行时调试 | `ScriptDebugger`（DBG3 断点桥接 + DBG5 变量查看 + DBG6 调用栈 + DBG4 单步映射） | ✅ 完成 |
| DAP 适配器 | `DapServer`（12 消息 DAP 最小协议 + next/stepIn/stepOut），Gate 1 + Gate 2 自动化验证通过 | ✅ Phase 3B 完成 |
| VS Code 扩展 | `vscode-ffvm-debug/`（package.json + TextMate grammar + language-configuration.json） | ✅ 完成 |
| 测试 | 2140 项 Assert 全部通过（114 TW + 1302+ Compiler + 44 Perf + 18 FFS + 51 Debug + 97 DAP + 514 LSP） | ✅ 通过 |
| 性能基准 | `BenchmarkRunner`（6 组 VM vs C# 对比基准）+ `run-benchmarks.cmd` 自动化管线 | ✅ 完成 |
| 语言服务 | `LspServer`（LSP1-LSP7 全部完成：诊断 + 核心 + 实时诊断 + 符号分析 + 代码补全 + Syscall 声明 + 参数提示） | ✅ 完成 |

### 未完成（按优先级排列）

| 优先级 | 内容 | 阻塞关系 |
|--------|------|----------|
| P3 | V5 帧内 Profiler 验证（真实 Syscall 接入后） | 阻塞编辑器 UI |
| P3 | 编辑器流程图投影 | 不阻塞 VM 核心 |
| — | Handle64 批处理协议 | 展望项，最晚于真实多目标业务接入前 |

---

## 二、性能优化

> 通用 VM 优化详见 [VM_Optimization_Outlook.md](VM_Optimization_Outlook.md)
> 函数调用路径专项优化详见 [Step8_FunctionCall.md §七](../Plan/Step8_FunctionCall.md#七性能优化展望)
> 结构体路径潜在优化详见 [Step9_StructFlatten.md §七](../Plan/Step9_StructFlatten.md#七性能优化展望)
> 全部展望与风险的统一索引详见 [Plan/Outlook_And_Risks.md](../Plan/Outlook_And_Risks.md)

### 已执行优化记录

当前编译脚本性能基准为 5-7x（vs 等价 C# Number），跨语言对比约 2-3x Lua（见下方 §四.3）。
以下优化均已实施并通过测试，后续新增优化前应先查阅此表避免重复。

| ID | 名称 | 实施步骤 | 类别 | 核心改动 | 效果 |
|----|------|---------|------|---------|------|
| O1 | 消除逐次 fixed pin | B3 Tier 1 | 解释器 | 单次 `fixed(Number* regs)` 覆盖整个 burst | dispatch 开销降低 |
| O2 | 连续 OpCode（0-32） | B3 Tier 1 | 解释器 | enum 连续编号，JIT 生成跳转表 | switch dispatch 优化 |
| O3 | 去冗余边界检查 | F4 阶段 | 解释器 | 编译器保证寄存器索引合法 | 移除 per-instruction 检查 |
| O4 | dest-reg 传递 | F4 阶段 | 编译器 | 表达式直接写入目标寄存器 | 减少 MOVE 指令 |
| O5 | 常量折叠 | F4 阶段 | 编译器 | 编译期计算常量表达式 | 减少 LOAD_CONST + 运算指令 |
| O6 | Peephole 优化 pass | B-β1 | 编译器 | 自赋值消除 + dest-redirect + jump-to-next + NOP 压缩 | 指令数减少 ≥5% |
| O7 | Syscall 结果直达 | F4 阶段 | 编译器 | Syscall 返回值直写目标寄存器 | 减少 MOVE |
| O9 | 活跃实例链表 | B-β3 | 调度层 | ActiveList 替代全量遍历 + swap-remove O(1) | 稀疏场景 Tick 开销大幅降低 |
| O10 | 快照只拷贝活跃实例 | B-δ1 | 调度层 | SaveState/LoadState 仅遍历 ActiveList | 快照数据量减少 80-90% |
| O15 | 热循环优化 | B-γ4 | 解释器 | SENTINEL 哨兵 + AggressiveOptimization + MaxStepsPerTick 局部缓存 | VM 时间 -32%~-80% |
| FO1 | 叶函数优化 | B-β2 | 函数调用 | CALL_LEAF/RET_LEAF 跳过 CallFrame push/pop | 叶函数开销 -40~60% |
| FO5 | 返回值直达 | F4 阶段 | 函数调用 | 返回值直写调用方目标寄存器 | 减少 MOVE |
| FO6 | 自适应寄存器窗口 | B-γ1 | 函数调用 | temp 重映射紧接 locals + 窗口 = locals+temps | 嵌套层数 ~3→~6 |
| FO7 | 调用栈深度静态分析 | F4 阶段 | 函数调用 | 编译期计算最大调用深度 | 运行时无栈溢出检查 |
| SO1 | COPY_BLOCK OpCode | B-δ2 | 结构体 | COPY_BLOCK(dst,src,count) 替代 N×MOVE | 大 struct 赋值 N→1 指令 |
| C6 | 相邻 cleanup 合并 | B-γ6 | 编译器 | 连续 defer compound merge | 减少 PUSH_CLEANUP/POP_CLEANUP 对 |
| O17 | LICM 循环不变量提升 | B-ζ1 | 编译器 | 循环体内常量提升到循环前 | B04↓33%，普遍↓17-39% |
| O18 | CMP-immediate 指令 | B-ζ2 | 编译器+解释器 | JUMP_IF_*_K 直接比较常量池 | 分支含常量比较加速 |
| O19 | SWITCH 跳转表 | B-ζ3 | 编译器+解释器 | 连续整数 if-else → O(1) 分派 | B04↓40-47% |

### 优化展望（未实施）

#### 指令压缩（O8，已完成 B-η）

Instruction 16B → 4B（1B opcode + 3×1B operand），全场景 10-20% L1 缓存系统性加速。
详见 [Step_O8_InstructionCompression.md](../Plan/Step_O8_InstructionCompression.md)。

#### 其他展望

| Tier | 核心优化 | 预期收益 | 复杂度 | 状态 |
|------|---------|---------|--------|------|
| **5. 长期** | 函数指针 Syscall（O11）、SIMD Fix64（O14）等 | 特定路径加速 | 中-高 | ⏳ |
| 函数调用 | FO2 尾调用消除、FO3 小函数内联 | 尾调用不增长调用深度；小函数 -80% 指令 | 中-高 | ⏳ |

---

## 三、已知缺口与改进指引

> 本节由步骤 6 完成后的自审生成，记录当前代码和文档中已确认的缺口，作为后续步骤的输入。

### 代码缺口

| # | 位置 | 问题 | 状态 |
|---|------|------|------|
| G1 | Parser / BytecodeCompiler | `wait_for` Parser + Compiler 接入 | ✅ 已修复 |
| G2 | BytecodeCompiler | `POP_CLEANUP` 编译器生成 | ✅ 已修复 |
| G3 | BytecodeCompiler | 未知 `NodeKind` 静默返回 NOP | ✅ 已修复 |
| G4 | VMWorld.ExecuteInstance() | 步数上限耗尽错误码 | ✅ 已修复 |
| G5 | BytecodeCompiler | "requires cleanup" 强制检查 | ✅ 已修复 |
| G6 | BytecodeCompiler | Cleanup 块内禁止 wait | ✅ 已修复 |

### 测试缺口

| # | 场景 | 当前状态 |
|---|------|---------|
| T1 | `wait_for` 运行时路径 | ✅ 已覆盖 |
| T2 | 除以零 | ✅ 已覆盖 |
| T3 | 步数上限触发 | ✅ 已覆盖 |
| T4 | 实例池满溢 | ✅ 已覆盖 |
| T5 | Fix64 模式 | 需要 `USE_FIXPOINT` 编译标志独立构建 |
| T6-T16 | 各编译器路径 | ✅ 已覆盖 |

### 文档缺口（来自档案交叉审查）

以下内容存在于 Discussion 早期讨论稿中，已全部合并入 VM_Summary.md（B-γ8 完成）：

| # | 来源 | 内容 | 合并位置 |
|---|------|------|---------|
| D1 | VMScript.md | 技能流水线模式 | ✅ §1.2（现 → VM_Background.md） |
| D2 | VMScript2.md | 历史失败教训表 | ✅ §9.2（现 → VM_Background.md） |
| D3 | VMScript4.md | 成功标准 | ✅ §7.0（现 → 本文 §5.1） |
| D4 | VMScript4.md | 递进轴线 | ✅ §7.0b（现 → 本文 §5.2） |

---

## 四、持续集成与跨语言性能对比

### 4.1 GitHub Actions CI 工作流

`.github/workflows/ci.yml` 包含三个自动化 Job：

| Job | 触发 | 内容 |
|-----|------|------|
| **test** | push / PR | 构建 StandaloneRunner → 运行全部测试断言 |
| **benchmark** | test 通过后 | 运行 B01-B05 VM vs C# 基准，生成 `benchmark_ci.md` artifact；push 到 main/master 时自动追加历史记录 |
| **cross-lang** | test 通过后 | 运行 Lua / Python / Node.js 同源基准，生成 `cross_lang_results.md` artifact |

由于 `StandaloneRunner.csproj` 被 `.gitignore` 排除（Unity 约定），CI 中通过 inline `cat >` 自动生成。

### 4.2 性能历史追踪

每次 push 到 main/master 后，benchmark Job 自动执行以下流程：

1. 运行 B01-B05 基准测试
2. 调用 `benchmarks/update-history.sh` 解析结果并追加到 [`benchmarks/performance_history.md`](../../benchmarks/performance_history.md)
3. 与上一次记录对比，计算 VM 时间和 Ratio 的变化量（Δ）
4. 若任一 Benchmark 的 Ratio 退化超过 10%，标注 ⚠️ 回归警告
5. 自动 commit 并 push 更新后的历史文件（`[skip ci]` 避免循环触发）

手动生成历史记录：

```bash
dotnet run --project StandaloneRunner/StandaloneRunner.csproj -c Release -- --bench 2>&1 | tee bench-raw.txt
bash benchmarks/update-history.sh bench-raw.txt
```

### 4.3 跨语言性能基准

`benchmarks/` 目录包含与 FFVM BenchmarkRunner (B01-B05) **逻辑完全一致**的实现：

| 语言 | 文件 | 运行时 |
|------|------|--------|
| Lua 5.4 | `benchmarks/lua/bench.lua` | 标准解释器（无 JIT） |
| Python 3.12 | `benchmarks/python/bench.py` | CPython（无 JIT） |
| Node.js 20 | `benchmarks/js/bench.js` | V8（多级 JIT） |

所有脚本均使用整数算术，输出统一的 `[XLANG]` 格式供 `run-cross-lang.sh` 汇总。

**定位说明**：跨语言对比不是为了证明 FFVM "比 X 快"，而是确定 FFVM 在解释器性能谱中的位置：
- 预期 **快于 CPython**（FFVM 是固定类型寄存器 VM，无装拆箱）
- 预期 **与 Lua 5.4 同量级**（均为字节码解释器）
- 预期 **慢于 V8 JIT**（V8 有多级编译优化）
- 预期 **5-10x 于原生 C#**（使用相同 Number 数据类型）

---

## 五、串行计划辅助信息

### 5.1 成功标准与验收维度

> 来源：Discussion/VMScript4.md §1.4

新系统如果要被认为是成功的，至少应满足以下五个维度：

1. **业务覆盖**：能同时覆盖技能主流程、子弹持续行为、Buff 事件反应三类核心业务；能表达生命周期入口、等待、阶段切换、嵌套效果调用与局部逻辑计算。
2. **执行模型**：`wait`/`await` 成为一等语义；挂起被压缩为显式状态，而不是宿主栈残留或行为树 `Running`。
3. **性能模型**：战斗中零运行时 GC；快照/回滚接近纯内存拷贝；宿主 + VM 综合效率不低于纯 Lua 封闭逻辑方案。
4. **工具链**：编辑器能稳定显示流程、阶段、当前执行位置；支持断点、单步、变量查看、源码映射。
5. **工程落地**：可以渐进替换旧系统，而不是必须一次性重写；前端语法可演进，但核心 AST/VM 模型尽量稳定。

### 5.2 设计验证递进轴线

> 来源：Discussion/VMScript4.md §六

项目按 **曳光弹 → 编辑器/工具链 → 实战接入** 三阶段递进验证：

1. **曳光弹阶段**（Steps 1–4）：VMInstanceState → TreeWalker defer/Kill → 7 指令字节码解释循环 → Phase A+B 全验证。目的：用最小实现证明执行模型可行。
2. **编译器 + 工具链阶段**（Steps 5–9 + Debug + LSP）：完整编译器 → using/defer → 函数调用 → struct → DAP 调试器 → LSP 语言服务。目的：验证从源码到调试的全链路工具链。
3. **优化 + 实战接入阶段**（B 区间 → C 区间）：性能优化 → 功能完整性 → 真实 Syscall 接入 ECS → 帧同步集成。目的：在真实业务中验证性能与工程可行性。

---

## 六、实践专区

> **职责说明**：实践专区记录串行计划之外的探索性实践。详见 [Practice/](../Practice/) 目录。
>
> **文件命名**：`Docs/Practice/P{NNN}_{简短英文标题}.md`，编号递增。

### 实践处理标准流程

当实践文档中提出的问题或优化建议被正式处理后，按以下流程标记：

1. **逐条标注处理结果**：✅ 完全处理 / 🟡 部分处理 / 🔵 回收至次优先级 / ⏭️ 已跳过
2. **更新实践文档开头状态**
3. **更新总结表**
4. **更新索引表**

### 索引

| # | 实践文档 | 主题 | 日期 | 产出建议去向 | 状态 |
|---|---------|------|------|------------|------|
| P001 | [P001_Performance_Baseline_Rebuild.md](../Practice/P001_Performance_Baseline_Rebuild.md) | 性能基线重建 + 执行循环优化 | 2026-04-03 | → 串行计划 B-γ3（BM1）、B-γ4（O15） | ✅ 已处理 |
| P002 | [P002_Sandbox_Build.md](../Practice/P002_Sandbox_Build.md) | Sandbox 构建实践 | 2026-04-03 | → 紧急独立任务区 E001, E002；次优先级 T001-T003 | ✅ 已处理 |

---

## 七、紧急独立任务区

> **职责说明**：独立于串行计划的紧急修复通道。详见 [Emergency/README.md](../Emergency/README.md)。
>
> **文件命名**：`Docs/Emergency/E{NNN}_{简短英文标题}.md`，编号递增。

### 当前状态：✅ 已清空（串行计划已恢复）

| 等级 | ID | 缺陷 | 状态 |
|------|-----|------|------|
| 🔴 恶性 | E001 | 编译器寄存器生命周期 Bug | ✅ 已修复 |
| 🟠 深远 | E002 | Syscall 寄存器约定隐患 | ✅ 已修复 |
| 🟠 深远 | E003 | LSP 引用查找不完整 + didClose 缺失 | ✅ 已修复 |
| 🟠 深远 | E004 | 模块级符号导航缺失 | ✅ 已修复 |

### 次优先级（缺陷修复后处理）

| ID | 内容 | 来源 | 预期去向 |
|----|------|------|---------|
| T001 | Number 智能格式化 | P002-P5 | 展望计划 |
| T002 | 深度递归验证 | P002-P6 | 依赖 E001 修复 |
| T003 | Sandbox 回归测试 | P002-P7 | 工作流优化项 |

### 新增风险点（修复后纳入 Outlook_And_Risks.md）

| ID | 所属 | 风险 |
|----|------|------|
| ER1-ER3 | E001 | 分配策略变更影响 / 根因层级不确定 / 被掩盖问题暴露 |
| ER4-ER7 | E002 | 抽象层开销 / 兼容性 / no-op 掩盖错误 / API 变更影响 |
