# FFEditor 胶水执行器与技能虚拟机：阶段性总结

> 本文是 VMScript.md ~ VMScript4.md、VM_Architecture_Rules.md、VM_Runtime_Layout.md、VM_OpCodes_Draft.md、VM_Tracer_Bullet.md 及后续讨论的压缩总结。目标是作为单一入口文档，完整描述理想意图、当前曳光弹范围、每项决策的理由与妥协，以及未来补全路径。

### 文档目录结构

```
Assets/ScriptVM/
  VM_Summary.md              ← 本文（唯一入口文档）
  Refs/                      ← 子文件：当前活跃的引用文档
    VM_Architecture_Rules.md         架构硬约束（20 条纪律）
    VM_Runtime_Layout.md             运行时内存布局
    VM_OpCodes_Draft.md              OpCode 设计草案
    VM_Tracer_Bullet.md              曳光弹验证方案
    VM_Optimization_Outlook.md       性能优化展望（14 项方向）
    VM_Script_Language_Decision.md   脚本语言选型决策（候选对比 + AI 友好性分析）
  Plan/                      ← 计划文件
    TracerBullet_Checklist.md    曳光弹检查清单
  Archive/                   ← 归档：早期讨论稿（已被本文压缩替代）
    VMScript.md ~ VMScript4.md   初期需求与设计讨论
```

- **Refs/**：VM_Summary.md 引用的子文档，包含各专题的详细说明。
- **Plan/**：计划与检查清单。
- **Archive/**：早期分散的讨论稿，内容已合并入本文，保留仅供追溯。

---

## 一、项目背景与核心问题

### 1.1 项目环境

- 自研 2D 横版动作游戏；
- 自研类 ECS 架构引擎编辑器（FFEditor）；
- 需要帧同步、预测回滚的网络战斗架构。

### 1.2 核心问题

现有战斗逻辑不是缺少功能，而是已经在长期演化中分裂为三套各自有效、但整体不统一的体系：

1. **技能系统**（Skill / SubSkill / EffectProcessor）：角色主动释放的主流程，已有完善的条件→目标→数据效果→视觉效果流水，但主流程被分散在宿主语言的多层对象图中。
2. **子弹系统**（BulletBehaviour / BulletTask）：脱离角色后的持续体行为，本质是"行为链 + 嵌套效果器"，但没有统一的挂起/恢复语义。
3. **状态效果系统**（BuffBehaviour）：附着式、事件驱动、跨帧响应逻辑，生命周期管理独立于前两者。

三套系统共享的瓶颈：

- 主流程散落在宿主对象图、Builder、Manager、Task、Condition、事件派发中；
- 挂起/等待/生命周期边界等控制语义没有成为一等公民；
- 快照/回滚没有被统一到同一种执行模型中；
- 难以在一个地方完整阅读一个业务；
- 难以稳定映射为编辑器 UI。

### 1.3 不做什么

- 不是做一门通用脚本语言；
- 不是行为树；
- 不是纯蓝图；
- 不是继续在旧对象系统上叠补丁。

---

## 二、理想目标形态

### 2.1 一句话目标

> 把技能、子弹、状态效果三套旧系统中已经客观存在但分散隐藏的流程语义，统一抬升到脚本层，并用"脚本文本为真理源 + 结构化流程图为主 UI + 寄存器式 VM 为底层 + `wait` 为一等语义 + 零分配快照回滚"为核心约束，构建一套统一执行架构。

### 2.2 理想形态的完整能力清单

#### 语言层

| 能力 | 理想状态 | 曳光弹范围 |
|------|---------|-----------|
| 自然过程式脚本书写 | 支持 | 手写 AST，无前端语法 |
| `wait / await` 一等语义 | 支持 | 支持（`wait N` 帧） |
| 自定义结构体（编译期拍扁为寄存器） | 支持 | 不需要 |
| 脚本间函数调用与返回值 | 支持 | 不需要 |
| 分支（if/else） | 支持 | 不需要 |
| 循环（while/for） | 支持 | 不需要 |
| 局部逻辑短路 | 支持 | 不需要 |
| `using`（配对 Syscall 自动 Cleanup） | 支持（理想主要模式） | 用 `defer` 等价验证 |
| `defer`（手动 Cleanup 逃生舱） | 支持 | 支持 |
| 编译器变量生命周期分析 | 支持 | 不需要 |
| 跨 `await` 变量提升到持久寄存器 | 支持 | 不需要 |
| 调试符号 / 源码映射 | 支持 | 不需要 |
| `[Flow]` / `[Logic]` 视图驱动标记 | 支持（非默认负担） | 不需要 |
| 编译期黑板 Key 静态 ID 分配 | 支持 | 常量表手工映射 |

#### VM 执行层

| 能力 | 理想状态 | 曳光弹范围 |
|------|---------|-----------|
| 寄存器式偏瞬态 VM | 支持 | 支持 |
| ROM/RAM 物理分离 | 支持 | 支持 |
| RAM 为 ECS 纯值类型组件 | 支持 | 支持（`VMInstanceState`） |
| 固定大小寄存器区（64 × 8B = 512B） | 支持 | 支持 |
| 固定深度调用栈（16 层物理上限，编译器静态确定实际深度） | 支持 | 不启用 |
| 固定深度 Cleanup 栈 | 支持 | 支持 |
| `VMSlot` 64 位统一槽位 | 支持 | 当前为 `Number`（8B） |
| 全程 Fix64 确定性数值 | 支持 | 开发期 float，发布期 Fix64 |
| Syscall Table 统一宿主交互 | 支持 | 支持 |
| 句柄化临时数据（Handle64） | 支持 | 不需要 |
| 批处理 Syscall | 支持 | 不需要 |
| 黑板通信（Blackboard） | 支持 | 通过 Syscall 验证 |
| 快照 / 回滚（memcpy 级） | 支持 | 支持（SnapshotRingBuffer） |
| 强制 Kill → Cleanup 路径 | 支持 | **必须验证** |
| 零 GC | 支持 | Phase B 验证 |
| 字节码静态生成 + 回填 | 支持 | 不需要（TreeWalker 阶段） |

#### UI / 编辑器层

| 能力 | 理想状态 | 曳光弹范围 |
|------|---------|-----------|
| 结构化流程图主视图 | 支持 | 不需要 |
| 阶段/时间轴辅助视图 | 支持 | 不需要 |
| 黑盒逻辑节点折叠 | 支持 | 不需要 |
| 断点/单步/变量查看 | 支持 | 不需要 |
| 源码↔流程图双向定位 | 支持 | 不需要 |

#### 工程演进层

| 能力 | 理想状态 | 曳光弹范围 |
|------|---------|-----------|
| 渐进替换旧系统 | 支持 | 不需要 |
| 前端语法可替换 | 支持 | 手写递归下降 Parser |
| VM 平台中立 | 支持 | 不需要 |
| 旧资源→新脚本半自动转换 | 支持 | 不需要 |

---

## 三、核心架构决策与理由

### 3.1 ROM/RAM 分离 + ECS 组件化 RAM

**决策**：静态脚本资产（字节码、常量表、调试信息）为只读 ROM，同一脚本所有实例共享；实例运行状态（IP、WaitFrames、Registers[]）为纯值类型 RAM，直接挂载为 ECS 组件。

**理由**：
- 快照/回滚退化为 `Array.Copy`（memcpy 级），无需深拷贝对象树；
- 与宿主 ECS 帧同步框架物理咬合；
- 战斗中零 GC。

**当前实现**：`VMInstanceState`（~740B blittable struct）、`InstancePool`（确定性 free stack）、`SnapshotRingBuffer`（8 帧环，预分配）。已在 TreeWalker 测试中通过 Save/Load 一致性验证。

### 3.2 `wait` 作为一等语义

**决策**：`wait` 不是语法糖，不依赖宿主协程或行为树 `Running`。执行到 `wait` 时，VM 设置 `WaitFrames`，更新 `IP` 指向下一条指令，立即交出执行权。恢复由宿主调度器驱动。

**理由**：
- 彻底消灭行为树 `Running` 枚举和宿主栈残留；
- 挂起状态完全落在值类型字段中，天然可快照；
- UI 编辑器可以用 `wait` 点自动切分阶段。

**当前实现**：TreeWalker 中通过 `WaitSignal` 异常实现；字节码层设计为 `WAIT` OpCode 直接写 `WaitFrames` 并退出解释循环。

### 3.3 Cleanup 机制：`using`（理想）+ `defer`（逃生舱）

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
| C4 | 编译器 "requires cleanup" 强制检查 | 确定执行（低优先级） | **最晚步骤 10 前** | 标记了 requires_cleanup 的 Syscall 若既未配 `using` 也未配 `defer`，编译报错 |
| C5 | Cleanup 块执行超时保护 | 展望 | 待定 | 防止 Cleanup 块内死循环阻塞实例回收 |
| C6 | 嵌套 `using` 作用域优化（合并相邻 PUSH_CLEANUP） | 展望 | 待定 | 性能优化，非功能阻塞 |

### 3.4 全程 Fix64，表现走 Syscall

**决策**：VM 全程使用确定性数值类型（Fix64）。不区分 float/Fix64 双模式，不引入"非回滚实例"。不需要确定性的表现逻辑通过 Syscall 将值传给宿主 C# 侧，宿主自行使用 float。

**理由**：
- VM 架构最简——所有实例均参与快照回滚，零歧义；
- Fix64 在战斗脚本中的性能差异可忽略（大部分执行时间在 Syscall 和 Wait，而非数学运算）；
- 表现层（插值、动画、UI）本就是"可重建的"，回滚后由确定性状态重新驱动即可，无需在 VM 中运行表现逻辑；
- 兼容路径 A（VM 全确定性）方案。

**开发期妥协**：`Number` 结构保留 `USE_FIXPOINT` 编译符号，开发期用 float 快速迭代。但 float 模式的执行结果不得作为确定性正确性依据——在正式测试和上线构建中必须启用 Fix64。

**未来补全**：如需 VM 编排表现脚本（复杂镜头、特效序列等），可通过标志位将部分实例标记为"不参与快照"，升级为双轨模式。但当前阶段不引入此复杂度。

### 3.5 寄存器定长 + 句柄化 + Syscall 边界

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

### 3.6 手写递归下降 Parser

**决策**：脚本前端采用手写递归下降 Parser，不使用 Roslyn 或其他现有解析器。

**理由**：
- DSL 规模小（<15 种语句类型），手写 Parser 约 500-800 行 C#；
- 零外部依赖；
- 完全自主的错误信息（"VM 脚本不支持 X"而非"C# 不允许 X"）；
- 避免 Roslyn 带来的认知干扰——用户看到 C# 语法会不自觉期望 class、LINQ、泛型等能力；
- 精确控制"只允许什么"而非"禁止什么"。

**曳光弹范围**：不需要 Parser。手写 AST 直接验证 VM。

**未来补全**：在字节码 VM 曳光弹通过后，实现 Lexer → Parser → AST。后续可通过 VS Code LSP 补充 IDE 支持。

### 3.7 黑板 Key 编译期静态 ID

**决策**：黑板 Key 使用编译期分配的唯一整数 ID，禁止运行时 Hash。

**理由**：
- 零碰撞风险——Hash 碰撞导致的跨实例状态污染在战斗系统中是致命错误；
- 零运行时开销；
- 编译器生成静态映射表，运行时按 ID 索引。

**曳光弹范围**：常量表中手工映射 Key ID。

### 3.8 结构化流程图为主 UI（脚本为真理源）

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

---

## 四、曳光弹：范围、目标与验证

### 4.1 曳光弹的业务定义

```
skill TracerBullet
{
    defer {
        SetBlackboard(self, CastingState, 0)
    }

    SetBlackboard(self, CastingState, 1)
    wait 10
    PlayEffect(self, Fx_SimpleCast)
}
```

### 4.2 为什么选这个最小业务

一颗曳光弹同时覆盖：
- `wait`（一等挂起语义）
- ROM/RAM 分离
- ECS 组件化状态
- Cleanup 机制（`defer`）
- Syscall 边界
- Save/Load
- 0 GC

### 4.3 验证分两阶段

**Phase A（曳光弹成立门槛，必须全部通过）：**

1. `VMInstanceState` 是纯值类型，无托管字段
2. `wait 10` 正确挂起/恢复
3. 恢复后仅执行一次 `PlayEffect`
4. 正常结束走 Cleanup（`CastingState` → 0）
5. 强制 Kill 时 Cleanup 仍然执行（`CastingState` → 0、`PlayEffect` 不触发）
6. `Killed` 优先级高于 `WaitFrames > 0`

**Phase B（Phase A 通过后立即验证，不阻塞闭环）：**

7. Save/Load 后执行行为一致
8. 全流程零 GC

### 4.4 OpCode 集

#### Phase 1：曳光弹核心（7 条）

| OpCode | 职责 |
|--------|------|
| `NOP` | 占位/调试对齐/回填辅助 |
| `LOAD_CONST` | 将 ROM 常量装入寄存器 |
| `SYSCALL` | 唯一宿主交互入口 |
| `WAIT` | 唯一挂起入口 |
| `PUSH_CLEANUP` | 注册 Cleanup 入口 IP |
| `POP_CLEANUP` | 正常离开作用域时注销 Cleanup |
| `RETURN` | 结束/Cleanup 驱动切换点 |

#### Phase 2：Step 5 扩展（19 条）

| OpCode | 职责 |
|--------|------|
| `MOVE` | 寄存器间复制 Reg[A] = Reg[B] |
| `JUMP` | 无条件跳转 IP = A |
| `JUMP_IF_ZERO` | 条件跳转：Reg[B] == 0 → IP = A |
| `JUMP_IF_NOT_ZERO` | 条件跳转：Reg[B] != 0 → IP = A |
| `ADD/SUB/MUL/DIV/MOD` | 算术运算 Reg[A] = Reg[B] op Reg[C] |
| `CMP_EQ/NEQ/LT/LTE/GT/GTE` | 比较运算 → 0 或 1 |
| `AND/OR` | 布尔运算（非零为真） |
| `NOT` | 逻辑取反 |
| `NEG` | 算术取负 |

### 4.5 示意字节码

```
 0: PUSH_CLEANUP 8              // defer 块入口 → IP 8
 1: LOAD_CONST r3, Const_One    // 装入 1
 2: SYSCALL SetBlackboard       // SetBlackboard(self=r0, key=r1, value=r3)
 3: WAIT 10                     // 挂起 10 帧
 4: LOAD_CONST r2, Const_Fx     // 装入特效 ID
 5: SYSCALL PlayEffect          // PlayEffect(self=r0, effect=r2)
 6: RETURN                      // 正常结束 → 触发 Cleanup
 7: NOP                         // 对齐
 8: LOAD_CONST r3, Const_Zero   // Cleanup 块：装入 0
 9: SYSCALL SetBlackboard       // SetBlackboard(self=r0, key=r1, value=r3)
10: RETURN                      // Cleanup 结束
```

### 4.6 验证门禁与通过条件

以下验证项为整个 VM 工程的必过门禁。每项均可模块化执行，不需要完整环境，可分阶段插入推进顺序中，只需最终全部通过即可。

#### V1: GC 精确验证 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 确认字节码 Tick 循环内零托管堆分配 |
| 前置 | 曳光弹通过（字节码路径已可运行） |
| 方法 | 使用 `GC.GetAllocatedBytesForCurrentThread()` 精确测量当前线程分配。50 轮预热后，10 个活跃实例执行 SYSCALL + WAIT + Cleanup，连续 100 Tick，断言 0 bytes 分配 |
| 通过条件 | 预热后连续 100 Tick，`GC.Alloc` = 0 bytes（Syscall 注册、VMProgram 构造等预热期分配不计） |
| 结果 | ✅ Test 27 通过：100 ticks alloc = 0 bytes |

#### V2: 回滚正确性验证 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 确认 Save/Load 后执行行为与未中断运行 bit-exact 一致 |
| 前置 | 曳光弹通过 |
| 方法 | 跑 50 帧 → Save → 跑 50 帧（偶向）→ Load → 再跑 100 帧；对比两次从相同帧开始运行的完整 Syscall 调用序列 |
| 通过条件 | Syscall 调用序列完全一致，最终 StateFlags 完全一致 |
| 结果 | ✅ Test 28 通过：syscall sequence bit-exact，StateFlags match |

#### V3: 单实例性能基准 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 摩清 VM 字节码解释与等价宿主 C# 逻辑的性能差距倍率 |
| 前置 | MOVE/JUMP 补完（否则指令序列不具代表性） |
| 方法 | 同一段逻辑（循环 + 分支 + 算术 + Syscall）分别用 VM 字节码和纯 C# 实现，`Stopwatch` 跑 N 轮取平均，输出倍率 |
| 通过条件 | 记录倍率即可（参考值：解释器通常 10-30x，超过 50x 需排查） |
| 结果 | ✅ Test 40 通过：VM = 5913 µs, C# = 1575 µs, ratio = 3.8x（远优于 50x 上限） |

#### V4: N 实例吞吐上限 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 找到帧预算内的最大并发 VM 实例数 |
| 前置 | 同 V3 |
| 方法 | 从 128 → 256 → 512 → 1024 逐级加实例数，每轮跑固定 Tick 数，记录耗时曲线 |
| 通过条件 | 128 实例 × 50 条指令/Tick 总耗时 < 1ms（在目标硬件上） |
| 结果 | ✅ Test 41 通过：128 实例 = 0.391ms, 256 = 0.762ms, 512 = 0.883ms, 1024 = 1.961ms（近线性扩展，128 实例远低于 1ms） |

#### V3-B: 编译脚本性能基准（Compiled Script Benchmark） — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 测量编译器生成的字节码（文本脚本 → 编译 → 执行）与等价 C# 逻辑的性能差距 |
| 前置 | Lexer + Parser + BytecodeCompiler 完成 |
| 区别于 V3 | V3 使用手写/手动构造的字节码（代表 VM 解释器开销下限）；V3-B 使用编译器从文本脚本生成的字节码（代表实际开发者编写脚本时的真实性能） |
| 方法 | 5 组相同逻辑的 FFVM 脚本和 C# 代码（均使用 `Number` 结构体），20 轮预热 + 200 轮测量取平均，`Stopwatch` 计时 |
| 通过条件 | 所有 5 组值匹配（VM 结果 == C# 结果），记录倍率 |
| 自动化 | `run-benchmarks.cmd` 一键执行，输出机器可解析格式，自动生成 `benchmarks/benchmark_results.md` 报告 |
| Unity 运行 | 菜单 `TestVM → RunBenchmarks`，结果输出到 Unity Console |

**5 组基准测试：**

| 编号 | 名称 | 逻辑 | 规模 | 指令数 |
|------|------|------|------|--------|
| B01 | ArithLoop | 算术 + 取模 + 分支（每 3 次调 Syscall） | 10,000 轮 | 32 |
| B02 | Fibonacci | 迭代斐波那契（swap 循环） | fib(25) | 20 |
| B03 | NestedLoop | O(n²) 嵌套循环 + 乘法累加 | 100×100 | 26 |
| B04 | Branching | 4 路 if/else-if 分支链 | 10,000 轮 | 41 |
| B05 | Accumulator | 纯 ADD 累加（最小开销基准线） | 50,000 轮 | 16 |

**最新结果（Release，.NET 6.0，20 核）：**

| Benchmark | VM (µs) | C# (µs) | Ratio |
|-----------|---------|---------|-------|
| B01_ArithLoop | 540.7 | 78.7 | **6.87x** |
| B02_Fibonacci | 0.6 | 0.1 | **6.05x** |
| B03_NestedLoop | 189.9 | 32.4 | **5.86x** |
| B04_Branching | 463.9 | 81.5 | **5.69x** |
| B05_Accumulator | 819.0 | 163.8 | **5.00x** |

**性能分析：**

- **编译脚本 5-7x** vs **手写字节码 1.7x（V3）**：差距来自编译器生成的额外指令——`LOAD_CONST`、`MOVE`、寄存器间搬运、表达式临时寄存器分配等。手写字节码可以最优化寄存器使用，而编译器为通用性牺牲了部分效率。
- **两个数字都有意义**：1.7x 代表 VM 解释器本身的开销下限（天花板不高）；5-7x 代表开发者写脚本时的真实感受。
- **对比其他嵌入式脚本引擎**：Lua 5.4 解释器通常 20-40x，MoonSharp 50-100x，xLua 10-30x。FFVM 编译脚本的 5-7x 已优于多数通用脚本方案。
- **绝对值视角**：50K 次纯累加（B05）约 820µs，单帧预算 16.6ms（60fps），脚本开销占比极低。

#### V5: 帧内 Profiler 验证（真实 Syscall 接入后） — ⚪ 待前置

| 项目 | 内容 |
|------|------|
| 目的 | 确认含 ECS 交互开销的真实 Tick 耗时在帧预算内 |
| 前置 | 真实 Syscall 接入 ECS（技能释放、子弹生成等） |
| 方法 | Unity Profiler Timeline 观察 VM Tick marker，确认 GC.Alloc = 0 且帧耗时可接受 |
| 通过条件 | 帧预算内 GC.Alloc = 0，总耗时在可接受范围 |
| 最早可做 | 第一个真实技能脚本在场景中运行时 |
| 必须通过 | 进入步骤 10（编辑器 UI）前 |

---

## 五、当前实现状态

### 5.1 已完成

| 层级 | 内容 | 状态 |
|------|------|------|
| AST | 44 种节点 + `DeferStmt` + `UsingStmt` | ✅ 完成 |
| 数值 | `Number`（float/Fix64 双模式，8B） | ✅ 完成 |
| 状态 | `VMInstanceState`（blittable，含 StateFlags + CleanupStack） | ✅ 完成 |
| 实例池 | `InstancePool`（确定性 free stack） | ✅ 完成 |
| 快照 | `SnapshotRingBuffer`（8 帧环，零分配） | ✅ 完成 |
| Syscall | `SyscallTable`（256 slot，热替换，配对注册协议） | ✅ 完成 |
| 字节码 | `OpCode`（27 条指令，含 WAIT_FOR）+ `Instruction` + `VMProgram` + `VMModuleTable` | ✅ 完成 |
| 调度 | `VMWorld`（Tick 字节码解释循环 + Spawn/Destroy/Save/Load） | ✅ 完成 |
| 解释器 | `TreeWalker`（Phase 2 原型，含 defer + Kill） | ✅ 完成 |
| 词法分析 | `Lexer`（手写，14 关键字 + 运算符 + 字面量 + 注释） | ✅ 完成 |
| 语法分析 | `Parser`（手写递归下降，source → `ModuleNode` AST，含 using/wait_for/错误恢复） | ✅ 完成 |
| 编译器 | `BytecodeCompiler`（AST → `VMProgram`，寄存器分配：r0-15 scratch / r16-47 locals / r48-63 temps，支持 using 配对 Syscall） | ✅ 完成 |
| 测试 | 237 项 Assert 全部通过（98 TreeWalker + 104 Compiler + 17 Performance + 18 SkillScript），另有 5 项自动化性能基准 | ✅ Step 7 通过 |
| 性能基准 | `BenchmarkRunner`（5 组 VM vs C# 对比基准）+ `run-benchmarks.cmd` 自动化管线 → `benchmark_results.md` | ✅ 完成 |

### 5.2 未完成（按优先级排列）

| 优先级 | 内容 | 阻塞关系 |
|--------|------|----------|
| P0 | V1 GC 精确验证 + V2 回滚正确性验证 | ✅ 通过（Test 27, 28） |
| P1 | `MOVE`/`COPY` OpCode | ✅ 完成（Test 29, 37） |
| P1 | `JUMP`/`JUMP_IF` OpCode + 比较/布尔运算 | ✅ 完成（Test 30-36） |
| P1 | Step 5 新指令零 GC + 快照正确性验证 | ✅ 通过（Test 38, 39） |
| P1 | V3 单实例性能基准 + V4 N 实例吞吐上限 | ✅ 通过（Test 40: 3.8x, Test 41: 0.391ms/128inst） |
| P2 | Lexer + Parser（手写递归下降） | ✅ 完成（Lexer + Parser + BytecodeCompiler，22 项端到端编译器测试 C01-C22 通过） |
| P2 | 自动化性能基准管线（BenchmarkRunner + run-benchmarks.cmd） | ✅ 完成（5 组 VM vs C# 对比，编译脚本 5-7x ratio） |
| P2 | `using` 语法 Parser 解析（C1） | ✅ 完成 → 步骤 7（ParseUsing + UsingStmt AST 节点） |
| P2 | Paired Syscall 注册协议（C2） | ✅ 完成 → 步骤 7（SyscallTable.RegisterPaired / GetPairedSlot / HasPair） |
| P2 | 编译器 emit Cleanup 指令 for `using`（C3） | ✅ 完成 → 步骤 7（CompileUsing：SYSCALL + PUSH_CLEANUP + body + POP_CLEANUP） |
| P2 | `wait_for` Parser + Compiler 接入（G1） | ✅ 完成 → 步骤 7（ParseWaitFor + CompileWaitFor + WAIT_FOR OpCode） |
| P2（低） | 编译器 "requires cleanup" 强制检查（C4） | 确定执行 → **最晚步骤 10 前** |
| P2 | CALL / RET_FUNC OpCode + 跨函数调用 emit（F1-F2） | 确定执行 → 步骤 8 |
| P2 | 函数调用 GC + 快照回滚验证（F3） | 确定执行 → 步骤 8 |
| P2（低） | 编译器寄存器生命周期分析 + 跨 await 变量提升（F4） | 确定执行 → **最晚步骤 10 前** |
| P2 | Parser struct 声明 + 编译器 struct → 寄存器拍平（S1-S3） | 确定执行 → 步骤 9 |
| P3 | V5 帧内 Profiler 验证（真实 Syscall 接入后） | 阻塞编辑器 UI |
| P3 | 编辑器流程图投影 | 不阻塞 VM 核心 |
| — | Cleanup 超时保护（C5）/ 嵌套 using 优化（C6） | 展望项，暂无排期 |
| — | 结构体函数参数传递（S4） | 展望项，最晚步骤 10 前如需编辑器展示 |
| — | Handle64 批处理协议 | 展望项，最晚于真实多目标业务接入前 |

---

## 六、决策妥协表：为什么当前这样，将来如何补全

| 当前妥协 | 理由 | 未来补全路径 |
|----------|------|-------------|
| ~~手写 AST，无 Parser~~ | ~~先验证 VM 物理闭环~~ | ✅ 完成：Lexer + Parser + BytecodeCompiler，端到端文本 → 字节码 → 执行 |
| ~~TreeWalker 代替字节码 VM~~ | ~~Phase 2 快速验证语义正确性~~ | ✅ 完成：字节码解释循环 + 编译器流水线均已实现 |
| ~~`defer` 代替 `using`~~ | ~~`using` 需要 Paired Syscall 注册协议 + 编译器支持~~ | ✅ 完成 → 步骤 7：C1 Parser 解析 + C2 配对协议 + C3 编译器 emit；C4 强制检查最晚步骤 10 前 |
| 开发期 float（`Number`） | 快速迭代，Fix64 调试较痛苦 | float 模式仅用于开发迭代，正式测试和上线构建必须 `USE_FIXPOINT` |
| 无 `MOVE`/`COPY` | 曳光弹不需要寄存器搬运 | ~~曳光弹通过后立即补充~~ ✅ Step 5 完成 |
| 无分支/循环 OpCode | 曳光弹业务不含分支 | ~~`MOVE` 之后补 `JUMP`/`JUMP_IF`~~ ✅ Step 5 完成 |
| 无编辑器 UI | 编辑器依赖稳定 AST 和编译器 | 步骤 8-9 完成后开始流程图主视图（步骤 10） |
| 无 Handle64 批处理 | 曳光弹业务不涉及多目标 | 展望项，最晚于真实多目标业务接入前；依赖 Syscall 协议扩展 |
| 无跨函数 CALL | 曳光弹只需单函数 | 确定执行 → 步骤 8（F1-F3），CallFrame 基础设施已就位 |
| 无结构体语法 | 曳光弹不需要结构体 | 确定执行 → 步骤 9（S1-S3），设计见 VM_Runtime_Layout.md §5.2 |
| ~~CleanupFrames 尚未加入 VMInstanceState~~ | ✅ 已完成 | 曳光弹 Step 1 |
| 黑板 Key 手工映射常量 | 曳光弹只需 1-2 个 Key | 编译器实现后自动分配 ID 并生成静态映射表 |
| AST 节点已超出曳光弹范围 | 过早扩展（if/while/for/struct 等已实现） | 不删除，但冻结新增功能直到字节码曳光弹通过 |

---

## 七、推进顺序（严格串行）

```
1. 补齐 VMInstanceState 中 Cleanup 相关字段                        ✅
      ↓
2. TreeWalker 层实现 defer + Kill Cleanup 验证测试 → Phase A 通过  ✅
      ↓
3. 实现最小 7 指令字节码解释循环（VMWorld.Tick）                ✅
      ↓
4. 字节码路径通过 Phase A + B 全部验证 → 曳光弹成立        ✅
      ↓
  ┌────────────────────────────────────────────────────────┐
  │  V1: GC 精确验证        ← ✅ 通过                 │
  │  V2: 回滚正确性验证      ← ✅ 通过                 │
  └────────────────────────────────────────────────────────┘
      ↓
5. 补充 MOVE/COPY → JUMP/JUMP_IF → 比较/布尔                       ✅
      ↓
  ┌────────────────────────────────────────────────────────┐
  │  V3: 单实例性能基准    ← ✅ 通过 (3.8x)             │
  │  V4: N 实例吞吐上限    ← ✅ 通过 (128inst=0.391ms)   │
  └────────────────────────────────────────────────────────┘
      ↓
6. 设计并确定脚本语法 → 实现 Lexer + Parser + BytecodeCompiler       ✅
      ↓
  ┌────────────────────────────────────────────────────────┐
  │  编译器测试 C01-C22    ← ✅ 70 项 Assert 全部通过    │
  │  自动化性能基准 B01-B05 ← ✅ 编译脚本 5-7x ratio     │
  └────────────────────────────────────────────────────────┘
      ↓
7. 实现 using 语法 + Paired Syscall → 理想 Cleanup 模式             ✅
      确定执行：
        C1. using 语法 Parser 解析                                   ✅
        C2. Paired Syscall 注册协议（SyscallTable 扩展）              ✅
        C3. 编译器 emit PUSH_CLEANUP / POP_CLEANUP for using          ✅
      同步修复：
        G1. wait_for Parser + Compiler 接入（新增 WAIT_FOR OpCode）   ✅
        G2. POP_CLEANUP 首次被编译器生成（using 正常退出路径）         ✅
      确定执行（低优先级，最晚步骤 10 前）：
        C4. 编译器 "requires cleanup" 强制检查                        ⏳ 延至步骤 10 前
      展望项（暂无排期）：
        C5. Cleanup 块执行超时保护                                    ⏳ 展望
        C6. 嵌套 using 作用域优化                                     ⏳ 展望
      ↓
  ┌────────────────────────────────────────────────────────┐
  │  V5: 帧内 Profiler 验证  ← 真实 Syscall 接入 ECS 后    │
  │  通过条件见 §4.6，必须在进入步骤 10 前通过          │
  │  C4 强制检查必须在进入步骤 10 前就位                 │
  └────────────────────────────────────────────────────────┘
      ↓
8. 函数调用 + 固定深度调用栈验证
      （来源：VM_Tracer_Bullet.md §十二 第 3 项；CallFrame 基础设施已就位）
      确定执行：
        F1. CALL / RET_FUNC OpCode（VMWorld.Tick 扩展）
        F2. 编译器跨函数调用 emit（CallExpr → CALL，区别于 SYSCALL）
        F3. GC + 快照回滚验证（CallStack 不破坏 blittable / memcpy）
      确定执行（低优先级，最晚步骤 10 前）：
        F4. 编译器寄存器生命周期分析 + 跨 await 变量提升
            （来源：VM_Tracer_Bullet.md §十二 第 2 项"寄存器复用"）
      ↓
9. 结构体编译期拍平验证
      （来源：VM_Tracer_Bullet.md §十二 第 5 项；设计见 VM_Runtime_Layout.md §5.2）
      确定执行：
        S1. Parser struct 声明 + 字段类型解析
        S2. 编译器 struct → 连续寄存器槽位映射
        S3. 结构体赋值 = 寄存器区块 COPY 验证
      展望项（最晚步骤 10 前，如需编辑器展示结构体节点）：
        S4. 结构体作为函数参数 / 返回值的寄存器传递
      ↓
  ┌────────────────────────────────────────────────────────┐
  │  Handle64 批处理协议（展望项）                       │
  │  来源：VM_Tracer_Bullet.md §十二 第 4 项              │
  │  不阻塞编辑器，最晚于真实多目标业务接入前实现        │
  │  依赖 Syscall 协议扩展，独立于函数调用与结构体       │
  └────────────────────────────────────────────────────────┘
      ↓
10. 编辑器流程图投影
```

每一步的通过标准都由前一步建立的物理约束决定。任何新能力必须先通过 Architecture Rules 的裁决原则。

验证项均可模块化执行，不需要完整环境，可分阶段插入，只需最终全部通过即可。

> **VM_Tracer_Bullet.md §十二 溯源**：§十二 列出 6 项后续展望，现已全部归入本计划。
> ① 简单分支 → ✅ 已在步骤 6（Parser if/while/for + Compiler）完成。
> ② 局部变量与寄存器复用 → ✅ var 声明已在步骤 6 完成；寄存器复用 → F4（步骤 8）。
> ③ 函数调用与调用栈验证 → 步骤 8（F1-F3）。
> ④ Handle64 批处理 → 展望项，最晚于真实多目标业务接入前。
> ⑤ 结构体拍平验证 → 步骤 9（S1-S3）。
> ⑥ DSL 文本语法 → ✅ 已在步骤 6（Lexer + Parser + BytecodeCompiler）完成。

---

## 八、架构硬约束速查

以下是 [VM_Architecture_Rules.md](Refs/VM_Architecture_Rules.md) 中的 20 条硬纪律的浓缩速查：

1. 脚本文本 = 唯一真理源
2. UI = await 驱动的视图投影
3. VM = 偏瞬态寄存器式执行模型
4. RAM = ECS 纯值类型组件
5. ROM / RAM 物理分离
6. 寄存器禁持动态/托管/不定长结构
6b. 全程 Fix64，表现走 Syscall
7. 脚本 = 单体逻辑（AoS）
8. 批处理 = 宿主 Syscall
9. 复杂临时数据 = 句柄化
10. 宿主交互 = 统一 Syscall Table
11. `wait` = 一等语义
12. 快照/回滚 > 表达便利性
13. Cleanup = `using`（理想）+ `defer`（逃生舱）+ 编译器强制检查（实现路线图见 §3.3 C1-C6）
14. 语法服从 VM 物理法则
15. AST 禁悬空高级结构
16. 新提案必须过物理约束校验
17. 每次协作重新注入本文件
18. 文档尽快落为可运行锚点
19. 以可执行验证为准
20. 黑板 Key = 编译期静态 ID

**最终裁决**：任何新提案先问——能否放进定长寄存器和 ECS 纯值 RAM？是否支持零 GC / 快照 / 回滚 / 强制 Cleanup？是否保持 Syscall 边界与句柄化纪律？否定即拒绝。

---

## 九、脚本语言选型决策：为什么使用自定义 DSL

> 详见 [VM_Script_Language_Decision.md](Refs/VM_Script_Language_Decision.md)

**结论**：选择自定义 DSL，语法风格借鉴 Go（`func`/`var`/`defer`/`if`/`for`/`{}`），关键字约 12 个，完整 BNF ~80 行。

**核心理由**：

1. **约束完美贴合**：语法直接为 VM 物理法则设计，`wait`/`defer`/`using` 均为一等关键字
2. **AI 误用率最低**：语法空间极小，无 table/array/class/closure 等违规构造可供 AI 误用
3. **编译器最简**：~800 行递归下降，无需处理"合法但被禁"的语法分支
4. **编辑器投影最稳**：AST 完全受控，每个节点都有明确的字节码映射

**关键洞察**：AI 误用率 >> AI 识别率。给 AI 一个只有 12 个关键字的 DSL + spec，比给它一个有 500 个特性但只允许用其中 15 个的通用语言，效果更好。

**淘汰方案**：Lua 子集、TypeScript 子集、Python 子集（AI 误用率极高，均需禁用大量原生特性）；Go 超小子集（次优，编译器稍复杂）。详细对比矩阵见引用文件。

---

## 十、性能优化展望

> 详见 [VM_Optimization_Outlook.md](Refs/VM_Optimization_Outlook.md)

当前编译脚本性能基准为 5-7x（vs 等价 C#），手写字节码基准为 1.7x。在不改变功能语义的前提下，已识别 14 项优化方向，按 5 个层级排列：

| Tier | 核心优化 | 预期收益 | 复杂度 |
|------|---------|---------|--------|
| **1. 解释器热路径** | 消除逐次 fixed pin（O1）、连续 OpCode 跳转表（O2）、去冗余边界检查（O3） | dispatch **40-60%** 加速 | 低 |
| **2. 编译器优化** | dest-reg 传递（O4）、常量折叠（O5）、peephole pass（O6）、Syscall 直达（O7） | 指令数减少 **15-25%** | 中 |
| **3. 指令编码** | 16B → 4B 紧凑指令（O8） | L1 缓存 **10-20%** | 高 |
| **4. 调度层** | 活跃实例链表（O9）、稀疏快照（O10） | 调度/快照开销按稀疏度大幅降低 | 低-中 |
| **5. 长期** | 函数指针 Syscall（O11）、SIMD Fix64（O14）等 | 特定路径加速 | 中-高 |

**预估目标**：Tier 1 + Tier 2 完成后，编译脚本基准从 5-7x 降至 **2-3x**，手写字节码从 1.7x 降至 **~1.2x**。

最大单一赢利点是 **O1（消除 fixed pin）**，推荐作为第一个实施项。

---

## 十一、已知缺口与改进指引

> 本节由步骤 6 完成后的自审生成，记录当前代码和文档中已确认的缺口，作为后续步骤的输入。

### 11.1 代码缺口

| # | 位置 | 问题 | 优先级 | 建议修复时机 |
|---|------|------|--------|-------------|
| G1 | `Parser` / `BytecodeCompiler` | ~~`wait_for` 仅有 VM 运行时实现，Parser 和 Compiler 尚未接入~~ → 已修复：新增 `ParseWaitFor()` + `CompileWaitFor()` + `WAIT_FOR` OpCode（步骤 7） | 高 | ✅ 已修复 |
| G2 | `BytecodeCompiler` | ~~`POP_CLEANUP` 从未被编译器生成~~ → 已修复：`CompileUsing()` 在 using 块正常退出时 emit `POP_CLEANUP`（步骤 7） | 中 | ✅ 已修复 |
| G3 | `BytecodeCompiler.BinOpCode()` / `UnOpCode()` | ~~遇到未知 `NodeKind` 时静默返回 `NOP`，不报错~~ → 已修复：添加 `_errors.Add(...)` 报告未知操作符 | 中 | ✅ 已修复 |
| G4 | `VMWorld.ExecuteInstance()` | ~~步数上限耗尽时报 `PanicIllegalInstruction`~~ → 已修复：新增 `PanicStepLimitExceeded` 错误码 | 低 | ✅ 已修复 |
| G5 | `BytecodeCompiler` | 编译器缺少 "requires cleanup" 强制检查：标记了 requires_cleanup 的 Syscall 未配 `using`/`defer` 时应编译报错（对应 C4） | 中（低） | 确定执行 → **最晚步骤 10 前** |

### 11.2 测试缺口

| # | 场景 | 当前状态 |
|---|------|---------|
| T1 | `wait_for` 运行时路径 | ✅ 已覆盖（TreeWalkerTests T1：实例 A 等待实例 B 完成后恢复执行） |
| T2 | 除以零 | ✅ 已覆盖（TreeWalkerTests T2a/T2b：DIV/0 和 MOD/0 均返回 0，无 panic） |
| T3 | 步数上限触发 | ✅ 已覆盖（TreeWalkerTests G4 测试） |
| T4 | 实例池满溢 | ✅ 已覆盖（TreeWalkerTests T4：128 实例分配成功，第 129 个返回 -1） |
| T5 | Fix64 模式（`USE_FIXPOINT`） | 需要 `USE_FIXPOINT` 编译标志的独立构建配置，无法在单次构建中验证 |
| T6 | `bool` / `float` 字面量解析 | ✅ 已覆盖（CompilerTests C21/C22：true/false 编译为 1/0，3.5+2.5=6） |
| T7 | `wait_for` 编译器路径 | ✅ 已覆盖（CompilerTests C23：实例 A 通过编译脚本 `wait_for(id)` 等待实例 B 完成后恢复） |
| T8 | `using` 正常退出 + POP_CLEANUP | ✅ 已覆盖（CompilerTests C24：using 块正常退出时 POP_CLEANUP 移除 cleanup frame，release 不执行） |
| T9 | `using` Kill 路径 + cleanup release | ✅ 已覆盖（CompilerTests C25：Kill 触发 cleanup 块执行 release Syscall） |
| T10 | `using` + `defer` 混合 LIFO | ✅ 已覆盖（CompilerTests C26：defer + using 混合，POP_CLEANUP 后仅 defer 在 RETURN 时执行） |
| T11 | `using` 内嵌 wait | ✅ 已覆盖（CompilerTests C27：using 块内 wait 恢复后正常退出，release 不执行） |

### 11.3 文档缺口（来自档案交叉审查）

以下内容存在于 Archive 早期讨论稿中，但尚未合并入本文：

| # | 来源 | 内容 | 是否需要补充 |
|---|------|------|-------------|
| D1 | VMScript.md | "条件→目标→数据效果→视觉效果"技能流水线模式 | 建议补入 §1.2 或 §2 |
| D2 | VMScript2.md | 历史失败教训表（XSLT/XBL/ASP.NET/Flash/AMD/GWT/WebComponents → 设计约束推导） | 建议补入 §9 选型理由 |
| D3 | VMScript4.md | 项目级成功标准（5 类验收维度） | 建议补入 §7 或新增验收标准节 |
| D4 | VMScript4.md | 设计验证递进轴线（曳光弹 → 编辑器 → 实战接入） | 已隐含在 §7 推进顺序中，可补充显式描述 |

---

## 十二、持续集成与跨语言性能对比

### 12.1 GitHub Actions CI 工作流

`.github/workflows/ci.yml` 包含三个自动化 Job：

| Job | 触发 | 内容 |
|-----|------|------|
| **test** | push / PR | 构建 StandaloneRunner → 运行全部 237 个测试断言（TreeWalker 98 + Compiler 104 + Performance 17 + SkillScript 18） |
| **benchmark** | test 通过后 | 运行 B01-B05 VM vs C# 基准，生成 `benchmark_ci.md` artifact |
| **cross-lang** | test 通过后 | 运行 Lua / Python / Node.js 同源基准，生成 `cross_lang_results.md` artifact |

由于 `StandaloneRunner.csproj` 被 `.gitignore` 排除（Unity 约定），CI 中通过 inline `cat >` 自动生成。

### 12.2 跨语言性能基准

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
