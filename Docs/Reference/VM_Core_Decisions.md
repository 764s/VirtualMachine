# 核心架构决策与理由

> 本文从 VM_Summary.md 迁出，记录每项核心架构决策的完整理由、当前实现与实现路线图。
> 速查版见 [VM_Architecture_Rules.md](VM_Architecture_Rules.md)（20 条硬纪律）。

---

## 3.1 ROM/RAM 分离 + ECS 组件化 RAM

**决策**：静态脚本资产（字节码、常量表、调试信息）为只读 ROM，同一脚本所有实例共享；实例运行状态（IP、WaitFrames、Registers[]）为纯值类型 RAM，直接挂载为 ECS 组件。

**理由**：
- 快照/回滚退化为 `Array.Copy`（memcpy 级），无需深拷贝对象树；
- 与宿主 ECS 帧同步框架物理咬合；
- 战斗中零 GC。

**当前实现**：`VMInstanceState`（~740B blittable struct）、`InstancePool`（确定性 free stack）、`SnapshotRingBuffer`（8 帧环，预分配）。已在 TreeWalker 测试中通过 Save/Load 一致性验证。

## 3.2 `wait` 作为一等语义

**决策**：`wait` 不是语法糖，不依赖宿主协程或行为树 `Running`。执行到 `wait` 时，VM 设置 `WaitFrames`，更新 `IP` 指向下一条指令，立即交出执行权。恢复由宿主调度器驱动。

**理由**：
- 彻底消灭行为树 `Running` 枚举和宿主栈残留；
- 挂起状态完全落在值类型字段中，天然可快照；
- UI 编辑器可以用 `wait` 点自动切分阶段。

**当前实现**：TreeWalker 中通过 `WaitSignal` 异常实现；字节码层设计为 `WAIT` OpCode 直接写 `WaitFrames` 并退出解释循环。

## 3.3 Cleanup 机制：`using`（理想）+ `defer`（逃生舱）

**决策**：
- **理想主要模式**：`using` + 配对 Syscall。在 SyscallTable 注册时为 Syscall 绑定反向操作。脚本用 `using SomeCall(args)` 时，编译器自动生成 `PUSH_CLEANUP`。开发者无需手写清理逻辑。编译器对标记 "requires cleanup" 的 Syscall 强制检查，未配 `using` 或 `defer` 则编译报错。
- **逃生舱**：`defer` 块，用于不可自动配对的复杂场景。
- 底层统一展开为 `PUSH_CLEANUP` + `SYSCALL`，不引入新 OpCode。

**理由**：
- 覆盖战斗中 80%+ 的 Cleanup 场景（黑板状态重置、循环特效停止、句柄释放、状态效果移除），零心智负担；
- 保留 `defer` 处理剩余复杂情况；
- 编译器强制检查从源头消灭"忘写清理"。

**曳光弹范围**：用 `defer` 等价验证 Cleanup 栈的物理正确性。`using` 语法在 Parser 阶段实现。

**实现路线图**（按执行时机分类）：

| # | 实现项 | 分类 | 执行时机 | 说明 |
|---|--------|------|----------|------|
| C1 | `using` 语法 Parser 解析 | 确定执行 | ✅ 步骤 7 | Parser 支持 `using Syscall(args) { body }` 语句 |
| C2 | Paired Syscall 注册协议 | 确定执行 | ✅ 步骤 7 | SyscallTable.RegisterPaired / GetPairedSlot / HasPair |
| C3 | 编译器 emit `PUSH_CLEANUP`/`POP_CLEANUP` for `using` | 确定执行 | ✅ 步骤 7 | CompileUsing：acquire SYSCALL + PUSH_CLEANUP + body + POP_CLEANUP |
| C4 | 编译器 "requires cleanup" 强制检查 | 确定执行（低优先级） | ✅ 步骤 10 前 | 标记了 requires_cleanup 的 Syscall 若既未配 `using` 也未配 `defer`，编译报错 |
| C5 | Cleanup 块执行超时保护 | ✅ | B-δ5 | 防止 Cleanup 块内死循环阻塞实例回收 |
| C6 | 嵌套 `using` 作用域优化（合并相邻 PUSH_CLEANUP） | ✅ | B-γ6 | 性能优化 |

## 3.4 全程 Fix64，表现走 Syscall

**决策**：VM 全程使用确定性数值类型（Fix64）。不区分 float/Fix64 双模式，不引入"非回滚实例"。不需要确定性的表现逻辑通过 Syscall 将值传给宿主 C# 侧，宿主自行使用 float。

**理由**：
- VM 架构最简——所有实例均参与快照回滚，零歧义；
- Fix64 在战斗脚本中的性能差异可忽略（大部分执行时间在 Syscall 和 Wait，而非数学运算）；
- 表现层（插值、动画、UI）本就是"可重建的"，回滚后由确定性状态重新驱动即可，无需在 VM 中运行表现逻辑；
- 兼容路径 A（VM 全确定性）方案。

**开发期妥协**：`Number` 结构保留 `USE_FIXPOINT` 编译符号，开发期用 float 快速迭代。但 float 模式的执行结果不得作为确定性正确性依据——在正式测试和上线构建中必须启用 Fix64。

**未来补全**：如需 VM 编排表现脚本（复杂镜头、特效序列等），可通过标志位将部分实例标记为"不参与快照"，升级为双轨模式。但当前阶段不引入此复杂度。

## 3.5 寄存器定长 + 句柄化 + Syscall 边界

**决策**：
- 寄存器区固定 64 个 VMSlot（64 位），编译期确定，运行期不扩容；
- 所有复杂临时数据（目标列表、批量结果等）只在宿主侧存在，VM 只流转整数句柄（Handle64）；
- 宿主能力只通过 SyscallTable 暴露（256 槽位，支持热替换）；
- 调用栈物理上限固定（`MaxCallDepth = 16`），保证 `VMInstanceState` 定长可 memcpy；编译器静态分析每个脚本的调用图，算出实际最大深度，超出物理上限则编译报错——即「物理上限是固定的，逻辑上限由编译器确定」。

**理由**：
- 寄存器定长 → 状态可 memcpy；
- 句柄化 → 零 GC（VM 不分配堆内存）；
- Syscall 边界 → VM 不隐式依赖宿主调用栈。
- 三者构成"零分配快速快照"的物理前提。

**当前实现**：`NumberRegisters`（64 × 8B = 512B inline struct）、`SyscallTable`（256 slot）。

## 3.6 手写递归下降 Parser

**决策**：脚本前端采用手写递归下降 Parser，不使用 Roslyn 或其他现有解析器。

**理由**：
- DSL 规模小（<15 种语句类型），手写 Parser 约 500-800 行 C#；
- 零外部依赖；
- 完全自主的错误信息（"VM 脚本不支持 X"而非"C# 不允许 X"）；
- 避免 Roslyn 带来的认知干扰——用户看到 C# 语法会不自觉期望 class、LINQ、泛型等能力；
- 精确控制"只允许什么"而非"禁止什么"。

**曳光弹范围**：不需要 Parser。手写 AST 直接验证 VM。

**未来补全**：在字节码 VM 曳光弹通过后，实现 Lexer → Parser → AST。后续可通过 VS Code LSP 补充 IDE 支持。

## 3.7 黑板 Key 编译期静态 ID

**决策**：黑板 Key 使用编译期分配的唯一整数 ID，禁止运行时 Hash。

**理由**：
- 零碰撞风险——Hash 碰撞导致的跨实例状态污染在战斗系统中是致命错误；
- 零运行时开销；
- 编译器生成静态映射表，运行时按 ID 索引。

**曳光弹范围**：常量表中手工映射 Key ID。

## 3.8 结构化流程图为主 UI（脚本为真理源）

**决策**：
- 脚本文本是唯一真理源；
- 编辑器主视图为结构化流程图（非行为树、非纯蓝图）；
- 编译器自动分析 AST，含 `await` 的函数在主视图展开，纯逻辑默认折叠为黑盒节点；
- `[Flow]` / `[Logic]` 仅作覆盖默认行为的兜底标记。

**理由**：
- 行为树太依赖 `Running`，不适合快照/回滚；
- 纯蓝图会炸线，反向绑架语法设计；
- 结构化流程图最适合展示顺序/分支/循环/wait 点/生命周期入口/阶段切换。

**曳光弹范围**：不涉及 UI。
