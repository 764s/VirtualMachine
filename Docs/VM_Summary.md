# FFEditor 胶水执行器与技能虚拟机：阶段性总结

> 本文是 VMScript.md ~ VMScript4.md、VM_Architecture_Rules.md、VM_Runtime_Layout.md、VM_OpCodes_Draft.md、VM_Tracer_Bullet.md 及后续讨论的压缩总结。目标是作为单一入口文档，完整描述理想意图、当前曳光弹范围、每项决策的理由与妥协，以及未来补全路径。

### 文档目录结构

> 文档区已从 `Assets/ScriptVM/` 迁移至仓库根目录 `Docs/`，以避免 Unity 为 `.md` 文件自动生成 `.meta` 文件。

```
Docs/
  VM_Summary.md              ← 本文（唯一入口文档）
  Refs/                      ← 子文件：当前活跃的引用文档
    VM_Architecture_Rules.md         架构硬约束（20 条纪律）
    VM_Runtime_Layout.md             运行时内存布局
    VM_OpCodes_Draft.md              OpCode 设计草案
    VM_Tracer_Bullet.md              曳光弹验证方案
    VM_Optimization_Outlook.md       性能优化展望（14 项通用优化方向）
    VM_Script_Language_Decision.md   脚本语言选型决策（候选对比 + AI 友好性分析）
  Plan/                      ← 计划文件
    TracerBullet_Checklist.md        曳光弹检查清单
    Step7_Using_PairedSyscall_Checklist.md   步骤 7 检查清单
    Step8_FunctionCall.md            步骤 8 完整文档（设计 + 实施 + 展望 + 风险）
    Step9_StructFlatten.md           步骤 9 检查清单（结构体编译期拍平）
    Step10_Pre_CompilerSemanticChecks.md  步骤 10 前置（C4+G6 编译器语义检查）
    Step_F4_RegisterLifecycle.md     F4 寄存器生命周期 + 自然优化 + 调试 Phase 1 + 风险理想方案
    Step_Debug_Decisions.md          脚本调试决策文档（全部决策理由 + 方案对比）
    Outlook_And_Risks.md             功能展望 + 优化展望 + 风险点 + 扩展串行计划
    Step_B3_Optimization_Tier1.md    B3 调整型优化 Tier 1（O1 fixed pin + O2 连续 OpCode）
    Step_R1_FFScript_Rename.md       B-R1 FFScript 正式命名 + .ffs 后缀统一
    Step_B_Gamma7_SN1_NestedStruct.md  B-γ7 SN1 嵌套结构体（递归拍平 + 循环引用检测）
  Skills/                    ← 技能脚本复现示例
    skill_114feiyanxuanfengtui.ffs    飞燕旋风腿（56 帧攻击技能）
    skill_25shangpanbeijizhong.ffs    上盘被击中（30 帧受击技能）
    README.md                        Syscall 协议 + 能力评估
  Emergency/                 ← 紧急独立任务区（恶性 / 影响深远缺陷的修复通道）
    README.md                        工作流定义 + 任务总览 + 风险汇总
    E001_Register_Lifecycle_Bug.md   编译器寄存器生命周期 Bug
    E002_Syscall_Register_Convention.md  Syscall 寄存器约定隐患
  Archive/                   ← 归档：早期讨论稿（已被本文压缩替代）
    VMScript.md ~ VMScript4.md   初期需求与设计讨论
```

- **Refs/**：VM_Summary.md 引用的子文档，包含各专题的详细说明。
- **Plan/**：计划与检查清单。
- **Skills/**：真实技能脚本的复现示例与分析。
- **Emergency/**：紧急独立任务区，恶性/影响深远缺陷的修复通道。详见 §十五。
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

#### 技能效果器流水线模式

> 来源：Archive/VMScript.md §7.1

现有技能系统的内层已经形成了一条稳定的**效果器执行流水线**：

`SkillBehaviour → Skill → SubSkill → SubskillEffectProcessor → EffectProcessor`

其中 `EffectProcessor` 执行的标准三段流水为：

> **条件检查 → 目标生成 → 数据效果 → 视觉效果**

- **条件**：普通条件 + 潜在目标筛选，最终组装为 `ConditionAnd<EffectContext>`。
- **数据效果**：`IDataEffect[]`，包含伤害/能量/治疗等默认效果及资源定义的附加效果。
- **视觉效果**：`VisualEffectDefault`，内部为条件任务树，运行上下文为 `VisualEffectContext`。

这说明战斗主流程和局部效果结算本来就是两种不同粒度的逻辑。新 VM 保留了这一事实：
- **外层**（技能流程调度）→ FFScript 的 `entry`/`wait`/阶段切换；
- **内层**（效果器流水线）→ FFScript 的 Syscall 调用链（`sys_damage`、`sys_vfx` 等）。

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

#### Phase 3：Step 8 函数调用（2 条）

| OpCode | 职责 |
|--------|------|
| `CALL` | A=目标函数入口 IP, B=callerWindowSize → 压入 CallFrame + 寄存器窗口偏移 + jump |
| `RET_FUNC` | 弹出 CallFrame → 恢复 IP + RegisterBase → 返回 caller |

#### Phase 4：结构体优化 + 叶函数（3 条）

| OpCode | 职责 |
|--------|------|
| `CALL_LEAF` | 叶函数优化调用（跳过 CallFrame push/pop） |
| `RET_LEAF` | 叶函数返回（从 inst 字段恢复） |
| `COPY_BLOCK` | A=dest, B=src, C=count → 批量寄存器拷贝（≥3 字段结构体赋值） |

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
| 编译器 | `BytecodeCompiler`（AST → `VMProgram`，寄存器分配：r0-15 scratch / r16-47 locals / r48-63 temps，支持 using 配对 Syscall，支持多函数编译 + CALL emit，支持 struct 编译期拍平 → 连续寄存器槽位映射，F4 寄存器生命周期分析 + 复用，O4 dest-reg hint，O5 常量折叠，O7 Syscall 结果直达，FO5 返回值直达，FO7 调用栈深度静态分析，R7 >50 函数回填 Dictionary 切换，R8 Cleanup 块禁止函数调用） | ✅ 完成 |
| 调试信息 | `VMProgram.SourceMap`（DBG1：IP→行号平行数组）+ `VMProgram.SymbolTable`（DBG2：变量名→寄存器+struct字段信息） | ✅ 完成 |
| 运行时调试 | `ScriptDebugger`（DBG3 断点桥接 + DBG5 变量查看适配器 + DBG6 调用栈查看，Gate 0 命令行调试能力，HaltOnBreakpoint + SkipNextCheck DAP 暂停支持，DBG4 单步映射：临时断点 + FindNextLineIP/FindStepIntoIP/FindStepOutIP） | ✅ 完成 |
| DAP 适配器 | `DapServer`（DBG7-A：12 消息 DAP 最小协议 + DBG7-B：next/stepIn/stepOut handler，stdin/stdout JSON-RPC + ContentLengthStream 分帧 + JsonHelper 手写 JSON），`StandaloneRunner --dap` 模式，Gate 1 + Gate 2 自动化验证通过 | ✅ Phase 3B 完成 |
| VS Code 扩展 | `vscode-ffvm-debug/`（package.json + TextMate grammar + language-configuration.json + launch.json 模板） | ✅ 完成 |
| 测试 | 1007 项 Assert 全部通过（112 TreeWalker + 506 Compiler + 44 Performance + 18 FFScript + 51 Debug + 97 DAP + 179 LSP），另有 6 项自动化性能基准 | ✅ B-δ5 通过 |
| 性能基准 | `BenchmarkRunner`（6 组 VM vs C# 对比基准）+ `run-benchmarks.cmd` 自动化管线 → `benchmark_results.md` | ✅ 完成 |
| 语言服务 | `LspServer`（LSP2 诊断 + LSP1 核心 + LSP3 实时诊断 + LSP4 符号分析 + LSP5 代码补全 + LSP6 Syscall 声明 + LSP7 参数提示） | ✅ 完成 |

### 5.2 未完成（按优先级排列）

| 优先级 | 内容 | 阻塞关系 |
|--------|------|----------|
| P3 | V5 帧内 Profiler 验证（真实 Syscall 接入后） | 阻塞编辑器 UI |
| P3 | 编辑器流程图投影 | 不阻塞 VM 核心 |
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
| 无编辑器 UI | 编辑器依赖稳定 AST 和编译器 | 步骤 9 完成后开始流程图主视图（步骤 10） |
| 无 Handle64 批处理 | 曳光弹业务不涉及多目标 | 展望项，最晚于真实多目标业务接入前；依赖 Syscall 协议扩展 |
| 无跨函数 CALL | 曳光弹只需单函数 | 确定执行 → 步骤 8（F1-F3），CallFrame 基础设施已就位 |
| ~~无结构体语法~~ | ~~曳光弹不需要结构体~~ | ✅ 完成 → 步骤 9（S1-S3），struct 编译期拍平为连续寄存器 |
| ~~CleanupFrames 尚未加入 VMInstanceState~~ | ✅ 已完成 | 曳光弹 Step 1 |
| 黑板 Key 手工映射常量 | 曳光弹只需 1-2 个 Key | 编译器实现后自动分配 ID 并生成静态映射表 |
| AST 节点已超出曳光弹范围 | 过早扩展（if/while/for/struct 等已实现） | 不删除，但冻结新增功能直到字节码曳光弹通过 |
| Paired Syscall 仅支持"无参反向调用" | 覆盖 80%+ 场景（SetBB/ResetBB, PlayEffect/StopEffect） | 如需带参反向调用，后续扩展 SyscallTable 配对协议 |
| 不支持跨模块函数调用 | 步骤 8 聚焦同模块内函数调用 | 后续步骤按需扩展 ModuleTable 跨模块解析 |
| 函数参数上限 = 16（r0-r15 Scratch Zone） | 与 Syscall 参数传递一致，覆盖绝大多数场景 | 如需更多参数，后续扩展寄存器布局 |
| 无调试符号 / 源码映射 | 先保证 VM 正确性与性能 | ✅ DBG1 源码映射 + DBG2 符号表已随 F4 合并完成；下一步：调试走真实宿主断点 + DAP 协议接入外部 IDE，语言智能走 LSP |

---

## 七、推进顺序（串行计划）

> **阅读指南**：
> - 本节为项目唯一的串行执行计划，所有任务严格串行推进。
> - ✅ = 已完成，⏳ = 待执行，⚪ = 被外部前置条件阻塞。
> - 已完成步骤仅保留摘要，详情见各步骤子文档链接。
> - 展望项（暂无排期）的完整索引见 [Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)。
> - **推进指令**：使用 `.github/prompts/` 提示模板引导串行推进：
>   `#check-and-next`（检查+推进）、`#check`（仅检查）、`#requirement`（评估新需求）。
>   AI 通过 `当前位置 →` 标记定位下一步。

---

### 7.0 成功标准与验收维度

> 来源：Archive/VMScript4.md §1.4

新系统如果要被认为是成功的，至少应满足以下五个维度：

1. **业务覆盖**：能同时覆盖技能主流程、子弹持续行为、Buff 事件反应三类核心业务；能表达生命周期入口、等待、阶段切换、嵌套效果调用与局部逻辑计算。
2. **执行模型**：`wait`/`await` 成为一等语义；挂起被压缩为显式状态，而不是宿主栈残留或行为树 `Running`。
3. **性能模型**：战斗中零运行时 GC；快照/回滚接近纯内存拷贝；宿主 + VM 综合效率不低于纯 Lua 封闭逻辑方案。
4. **工具链**：编辑器能稳定显示流程、阶段、当前执行位置；支持断点、单步、变量查看、源码映射。
5. **工程落地**：可以渐进替换旧系统，而不是必须一次性重写；前端语法可演进，但核心 AST/VM 模型尽量稳定。

### 7.0b 设计验证递进轴线

> 来源：Archive/VMScript4.md §六 → 实际执行见下方 A/B 区间

项目按 **曳光弹 → 编辑器/工具链 → 实战接入** 三阶段递进验证：

1. **曳光弹阶段**（Steps 1–4）：VMInstanceState → TreeWalker defer/Kill → 7 指令字节码解释循环 → Phase A+B 全验证（GC=0，rollback bit-exact）。目的：用最小实现证明执行模型可行。
2. **编译器 + 工具链阶段**（Steps 5–9 + Debug + LSP）：完整编译器 → using/defer → 函数调用 → struct → DAP 调试器 → LSP 语言服务。目的：验证从源码到调试的全链路工具链。
3. **优化 + 实战接入阶段**（B 区间 → C 区间）：性能 Tier 1-2 优化 → 功能完整性（嵌套 struct、常量字符串等）→ 真实 Syscall 接入 ECS → 帧同步集成。目的：在真实业务中验证性能与工程可行性。

---

### A. 已完成阶段（Steps 1–9 + 调试 + CI）

下表按实际执行顺序列出所有已完成步骤。

| # | 步骤 | 关键产出 | 测试数 | 详情 |
|---|------|---------|--------|------|
| 1 | VMInstanceState Cleanup 字段 | blittable struct + CleanupStack | — | — |
| 2 | TreeWalker defer + Kill 验证 | Phase A 通过 | — | — |
| 3 | 最小 7 指令字节码解释循环 | VMWorld.Tick | — | — |
| 4 | 字节码 Phase A+B 全部验证 | **曳光弹成立** | — | [TracerBullet_Checklist.md](Plan/TracerBullet_Checklist.md) |
| — | V1 GC 精确验证 | 100 Tick 0 bytes alloc | — | §4.6 |
| — | V2 回滚正确性验证 | Syscall 序列 bit-exact | — | §4.6 |
| 5 | MOVE/JUMP/比较/布尔 | 19 条 Phase 2 指令 | — | — |
| — | V3 单实例性能基准 | 3.8x vs C# | — | §4.6 |
| — | V4 N 实例吞吐上限 | 128 实例 = 0.391ms | — | §4.6 |
| 6 | Lexer + Parser + BytecodeCompiler | 端到端文本→字节码→执行 | C01-C22 | — |
| — | 自动化性能基准 B01-B05 | 编译脚本 5-7x ratio | — | §4.6 |
| 7 | using + Paired Syscall (C1-C3, G1-G2) | 理想 Cleanup 模式 | — | [Step7](Plan/Step7_Using_PairedSyscall_Checklist.md) |
| 8 | 函数调用 + 调用栈 (F1-F3) | CALL/RET_FUNC + GC/回滚验证 | 279 | [Step8](Plan/Step8_FunctionCall.md) |
| 9 | 结构体编译期拍平 (S1-S3) | struct → 连续寄存器 | 303 | [Step9](Plan/Step9_StructFlatten.md) |
| — | Step 10 前置 (C4+G6) | requires_cleanup + Cleanup 块禁 wait | 315 | [Step10_Pre](Plan/Step10_Pre_CompilerSemanticChecks.md) |
| — | F4 + 自然优化 + 调试 Phase 1 | 寄存器生命周期 + O4/O5/O7/O3 + DBG1/DBG2 + R7/R8 | 361 | [Step_F4](Plan/Step_F4_RegisterLifecycle.md) |
| — | 调试 Phase 2 (Gate 0) | ScriptDebugger: DBG3+DBG5+DBG6 命令行调试 | 412 | [Phase2](Plan/Step_Debug_Phase2.md) |
| — | GR1 CI 构建矩阵 | float + Fix64 双模式自动验证 | 412×2 | [GR1](Plan/Step_GR1_CI_BuildMatrix.md) |
| — | 调试 Phase 3A (Gate 1) | DAP Server 核心 + VS Code 扩展 | 470 | [Phase3A](Plan/Step_Debug_Phase3A_DAP.md) |
| — | **调试 Phase 3B (Gate 2)** | **DAP 单步 next/stepIn/stepOut** | **505** | [Phase3B](Plan/Step_Debug_Phase3B_DAP_SingleStep.md) |
| — | **语言服务 Phase 4 (LSP2+LSP1+LSP3)** | **TextMate Grammar 修复 + LSP Server 核心 + 实时诊断** | **546** | [Phase4](Plan/Step_LSP_Phase4.md) |
| — | **语言服务 LSP4 (符号分析)** | **documentSymbol + hover + definition + references** | **586** | [LSP4](Plan/Step_LSP4_Symbols.md) |
| — | **语言服务 LSP5 (代码补全)** | **textDocument/completion：关键字+函数+变量+结构体+Syscall+字段补全** | **624** | [LSP5](Plan/Step_LSP5_Completion.md) |
| — | **B3 Tier 1 (O1+O2)** | **OpCode 连续编号 0-28 + ExecuteInstance unsafe fixed 寄存器钉住** | **624** | [B3-T1](Plan/Step_B3_Optimization_Tier1.md) |
| — | **B-R1 FFScript 正式命名** | **脚本正式命名 FFScript + 源文件后缀 `.vm` → `.ffs` 全局统一** | **624** | [B-R1](Plan/Step_R1_FFScript_Rename.md) |
| — | **B-α1 LSP6 Syscall 声明协议** | **SyscallTable 签名元数据 + .ffvm.d.json 声明文件加载 + 补全增强** | **644** | [B-α1](Plan/Step_B_Alpha1_LSP6_SyscallDecl.md) |
| — | **B-α2 LSP7 参数提示 (signatureHelp)** | **textDocument/signatureHelp：用户函数 + Syscall 参数提示 + 嵌套括号 + activeParameter 追踪** | **676** | [B-α2](Plan/Step_B_Alpha2_LSP7_SignatureHelp.md) |
| — | **B-β1 O6 Peephole 优化 pass** | **P1 自赋值消除 + P2 dest-redirect + P4 jump-to-next + NOP 压缩，指令数减少 ≥5%** | **676** | [B-β1](Plan/Step_B_Beta1_O6_Peephole.md) |
| — | **B-β2 FO1 叶函数优化** | **CALL_LEAF/RET_LEAF 指令对 + 叶函数跳过 CallFrame push/pop + 调试器透明降级** | **700** | [B-β2](Plan/Step_B_Beta2_FO1_LeafFunction.md) |
| — | **B-β3 O9 活跃实例链表** | **ActiveList 替代全量遍历 + swap-remove O(1) + 稀疏场景验证 + Snapshot 一致性** | **711** | [B-β3](Plan/Step_B_Beta3_O9_ActiveList.md) |
| — | **B-γ1 FO6 自适应寄存器窗口** | **编译后 temp 重映射紧接 locals + CALL 窗口 = locals+temps + 累计窗口溢出检测 + 嵌套 ~3→~6** | **721** | [B-γ1](Plan/Step_B_Gamma1_FO6_AdaptiveWindow.md) |
| — | **B-γ2 FF5 非 entry 函数 defer** | **RET_FUNC cleanup 链对齐 + CleanupBase 作用域边界 + r0 返回值保护 + Kill 逐层展开** | **763** | [B-γ2](Plan/Step_B_Gamma2_FF5_NonEntryDefer.md) |
| — | **B-γ3 BM1 Benchmark 基础设施改进** | **WarmupRuns=100 + B02 fib(250) + B05 dispatch 说明 + B06 FuncCall 基准 + 环境指纹对比 + CI 自我基线** | **795** | [B-γ3](Plan/Step_B_Gamma3_BM1_Benchmark.md) |
| — | **B-γ4 O15 ExecuteInstance 热循环优化** | **SENTINEL 哨兵操作码 + AggressiveOptimization + MaxStepsPerTick 局部缓存，VM 时间 -32%~-80%** | **795** | [B-γ4](Plan/Step_B_Gamma4_O15_HotLoop.md) |
| — | **B-γ7 SN1 嵌套结构体** | **递归拍平为连续寄存器 + 循环引用检测 + 子 struct 赋值 + LSP 嵌套补全** | **884** | [B-γ7](Plan/Step_B_Gamma7_SN1_NestedStruct.md) |
| — | **B-γ8 GR3 文档缺口 D1-D4** | **D1 技能流水线→§1.2 + D2 失败教训→§9.2 + D3 成功标准→§7.0 + D4 递进轴线→§7.0b** | **884** | — |
| — | **B-γ9 STR1 常量字符串** | **Lexer StringLiteral + StringConstants ROM + LOAD_CONST 索引 + SyscallArgs.GetString + 不支持拼接 + 转义序列 + 快照安全** | **913** | — |
| — | **B-δ1 O10 快照只拷贝活跃实例** | **SaveState/LoadState 仅遍历 ActiveList 拷贝活跃 VMInstanceState + LoadState 先清 IsAlive 防止幽灵实例** | **929** | [B-δ1](Plan/Step_B_Delta1_O10_SnapshotActiveOnly.md) |
| — | **B-δ2 SO1 COPY_BLOCK OpCode** | **COPY_BLOCK(dst,src,count) 替代 N×MOVE 结构体赋值 + ≥3 字段阈值 + 编译器 EmitStructCopy** | **945** | [B-δ2](Plan/Step_B_Delta2_SO1_CopyBlock.md) |
| — | **B-δ3 FF3 可选参数与默认值** | **ParamDecl.DefaultValue + Parser `= expr` + Compiler 缺省值填充 scratch zone + LSP 签名/hover/补全显示默认值** | **969** | [B-δ3](Plan/Step_B_Delta3_FF3_OptionalParams.md) |
| — | **B-δ4 SN2 结构体字面量构造语法** | **StructLiteralExpr AST + Parser `TypeName { field: expr }` + Compiler sugar 展开 + 嵌套字面量 + CS31-CS38 测试** | **990** | [B-δ4](Plan/Step_B_Delta4_SN2_StructLiteral.md) |
| — | **B-δ5 C5 Cleanup 超时保护** | **MaxCleanupSteps 每块步数预算 + 超时跳过当前块继续剩余 cleanup + C5-01~C5-04 测试** | **1007** | [B-δ5](Plan/Step_B_Delta5_C5_CleanupTimeout.md) |

**B 阶段全部完成。1007 项 Assert × 2 模式全通过。B-ε 性能优化串行计划全部完成（4/4）。当前位置 → C 阶段。**

---

### B. 已完成阶段（脚本引擎侧 — 全部 20 步已迁入 A 区）

B 阶段 20 个步骤（B-R1 → B-δ5）全部完成，已归入上方 A 区表格。
原 B-δ6（B1 Unity Editor DAP）为可选项，已转入功能展望，见 [Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)。

> **剩余展望项**（暂无排期，业务驱动激活）：B1 Unity Editor DAP、FF1 跨模块调用、FF2 函数回调、FF4 多返回值、
> O8 指令压缩、O11-O14 运行时优化、FO2 尾调用、FO3 小函数内联、
> BB1 黑板 Key 编译期 ID、PR1 带参 Paired Syscall、DM1 双轨编排模式。
> 完整索引见 [Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)。

---

### B-ε. 性能优化串行计划（追平 Lua 目标）

> 背景：跨语言 benchmark（§12.3）显示 FFVM 比 Lua 5.4 慢 2-3x。
> 根因分析：指令密度差距 2.0x（循环控制 4 条 vs Lua FORLOOP 1 条）× 逐指令开销差距 1.5x（托管数组边界检查 + Reg() 分支）≈ 3.0x。
> 每步完成后运行 B01-B05 benchmark 确认收益，再推进下一步。

| 序号 | 步骤 | 状态 | 内容 | 预期收益 | 复杂度 |
|------|------|------|------|---------|--------|
| B-ε1 | fixed pin 消除边界检查 | ✅ | `fixed (Instruction* codeBase = code)` pin 指令/常量数组，指针算术替代托管索引，消除 CLR bounds check。实测 .NET 10 + 现代 CPU 分支预测下差异在噪声内（±2%），但消除了分支预测器饱和时的性能悬崖 | 理论 ~1 cycle/instr；实测噪声内 | 极低（~5 行） |
| B-ε2 | Compare&Branch fusion | ✅ | Peephole P5：`CMP_* tmp,B,C` + `JUMP_IF_ZERO tgt,tmp` → `JUMP_IF_* tgt,B,C`；+6 OpCode + 6 VMWorld case + liveness-based dead-register 检查（FO6 remap 后 temp 不在 TempRegBase 以上） | 实测 B01-B06 全面提升 12-21%，每个 if/while/for 省 1 条指令 | 中（~120 行） |
| B-ε3 | const + 常量传播 + 条件 DCE | ✅ | `const` 关键字 + `TryFoldConstant` 识别 const 标识符 + `CompileIf`/`CompileWhile` 常量条件消除死分支；赋值 const 报错；LSP 自动支持 | 编译期消除 LOAD_CONST + 不可达代码 | 中（~60 行） |
| B-ε4 | FORLOOP 超级指令 | ✅ | `FORLOOP loopTopIP, counterReg, limitReg`；编译器 pattern-match `for(var i=init; i<limit; i=i+1)` → JUMP_IF_GTE 初始检查 + body + FORLOOP；benchmark 全部改用 `for` 循环 | 实测 B01-B06: -22%/-50%/-34%/-22%/-46%/-29%，指令数 -2/-2/-4/-2/-2/-2 | 中（~80 行） |

**当前位置 → B-ε 性能优化串行计划完成。**

---

### C. 待执行阶段（宿主集成侧 — 生产必经路径）

以下步骤依赖真实游戏宿主环境，是从"引擎可用"到"生产上线"的关键差距。

| 序号 | 步骤 | 状态 | 内容 | 前置条件 | 说明 |
|------|------|------|------|----------|------|
| C1 | 真实 Syscall 接入 ECS | ⚪ | 将 stub Syscall 替换为真实宿主实现（碰撞检测、伤害、击退、特效、黑板读写等） | 宿主 ECS 框架就绪 | **最关键的生产差距**：当前全部 Syscall 均为 mock，技能脚本无法与真实游戏世界交互 |
| C2 | V5 帧内 Profiler 验证 | ⚪ | 含真实 ECS 交互开销的 Tick 耗时测量，确认帧预算可行 | C1 完成 | Unity Profiler Timeline 观察 VM Tick marker，GC.Alloc = 0 |
| C3 | 技能资源管线 | ⚪ | .ffs 文件加载/编译/缓存/热更新策略 | C1 完成 | 编译后 VMProgram 的序列化与缓存，运行时按需加载 |
| C4 | Handle64 批处理协议 | ⏳ | H1：句柄化多目标数据流转 | C1 完成 | 最晚于真实多目标业务（AOE 技能）接入前实现 |
| C5 | 帧同步集成验证 | ⚪ | 真实网络环境下快照/回滚正确性验证 | C1+C2 完成 | V2 已验证离线正确性，此处验证网络帧同步场景 |
| C6 | 编辑器流程图投影 | ⏳ | 步骤 10：AST → 结构化流程图主视图 | C2 通过 | 依赖 V5 + 真实 Syscall/ECS 接入 |

---

### D. 生产差距总结

| 差距领域 | 计划覆盖 | 说明 |
|----------|---------|------|
| 真实 Syscall 接入 ECS | C1 ✅ 已纳入 | 原计划中隐含于 V5 前置条件，现已显式列为 C1 |
| V5 帧内 Profiler | C2 ✅ 已纳入 | 原 V5，现归入宿主集成阶段 |
| 技能资源管线（加载/缓存/热更新） | C3 🆕 新增 | 原计划**未覆盖**。编译后 VMProgram 如何序列化、运行时如何按需加载，需要明确方案 |
| Handle64 批处理 | C4 ✅ 已纳入 | 原为展望项 H1 |
| 帧同步集成验证 | C5 🆕 新增 | 原计划**未覆盖**。V2 验证了离线快照回滚正确性，但真实网络帧同步场景未验证 |
| 编辑器流程图 | C6 ✅ 已纳入 | 原步骤 10 |
| LSP 语言服务 | B-α ✅ 已纳入 | LSP6 声明协议 + LSP7 参数提示，细化为 B-α1/α2 |
| 调整型优化 | B-β ✅ 已纳入 | O6 peephole + FO1 叶函数 + O9 活跃链表，细化为 B-β1/β2/β3 |
| 功能完整性 | B-γ ✅ 已纳入 | FO6 + FF5 + S4 + C6 + SN1 + GR3，细化为 B-γ1~γ6 |
| 按需补全 | B-δ ✅ 全部完成 | O10 + SO1 + FF3 + SN2 + C5 已完成；B1 转入展望 |
| 性能优化（追平 Lua） | B-ε ⏳ 进行中 | Unsafe.Add + Compare&Branch fusion + const/DCE + FORLOOP，细化为 B-ε1~ε4 |

每一步的通过标准都由前一步建立的物理约束决定。任何新能力必须先通过 Architecture Rules 的裁决原则。

验证项均可模块化执行，不需要完整环境，可分阶段插入，只需最终全部通过即可。

> **VM_Tracer_Bullet.md §十二 溯源**：§十二 列出 6 项后续展望，现已全部归入本计划。
> ① 简单分支 → ✅ 已在步骤 6（Parser if/while/for + Compiler）完成。
> ② 局部变量与寄存器复用 → ✅ var 声明已在步骤 6 完成；寄存器复用 → F4（步骤 8）。
> ③ 函数调用与调用栈验证 → 步骤 8（F1-F3）。
> ④ Handle64 批处理 → 展望项，最晚于真实多目标业务接入前。
> ⑤ 结构体拍平验证 → ✅ 步骤 9（S1-S3）完成。
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

### 9.2 历史失败教训与约束推导

> 来源：Archive/VMScript2.md §五

以下历史失败尝试直接推导出了本项目的设计约束：

| 失败尝试 | 核心教训 | 对本项目的约束 |
|----------|----------|---------------|
| XSLT | 复杂的转换语言不可维护 | 禁止为 Logic 设计独立子语言，必须与 Flow 共享表达式语法 |
| XBL / HTC | 隐式绑定导致调试困难 | 所有组件组合必须显式调用，禁止隐式"魔法" |
| ASP.NET ViewState | 自动序列化导致性能灾难 | 状态快照必须由作者显式触发，且仅拷贝连续值类型 |
| Flash 可视化脚本 | 可视化与代码混合导致混乱 | UI 严格分离：主视图为结构化流程图，复杂逻辑降级为黑盒 |
| AMD/CMD 模块 | 冗余语法增加心智负担 | 直接采用宿主语言的模块系统 |
| GWT | 抽象泄漏 + 调试困难 | 保留源码映射，VM 支持调试信息，不隐藏宿主特性 |
| Web Components v0 | 单一实现 + 生态不足 | VM 设计必须跨平台（C# / C++ / 其他），不依赖特定引擎 API |

这些约束直接体现在当前设计中：FFScript 使用单一表达式语法（无子语言分裂）、显式 `wait`/`defer`/`using` 而非隐式挂起、值类型寄存器快照（无 GC 序列化）、AST 到流程图的稳定投影、以及完全受控的 12 关键字语法空间。

---

## 十、性能优化

> 通用 VM 优化详见 [VM_Optimization_Outlook.md](Refs/VM_Optimization_Outlook.md)
> 函数调用路径专项优化详见 [Step8_FunctionCall.md §七](Plan/Step8_FunctionCall.md#七性能优化展望)
> 结构体路径潜在优化详见 [Step9_StructFlatten.md §七](Plan/Step9_StructFlatten.md#七性能优化展望)
> 全部展望与风险的统一索引详见 [Plan/Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)

### 10.1 已执行优化记录

当前编译脚本性能基准为 5-7x（vs 等价 C# Number），跨语言对比约 2-3x Lua（见 §12.3）。
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

### 10.2 优化展望（未实施）

#### 串行优化计划

以跨语言 benchmark（§12.3）追平 Lua 为目标，按 **收益/成本比** 排序的串行实施计划：

| # | 名称 | 核心改动 | 预期收益 | 复杂度 | 前置 |
|---|------|---------|---------|--------|------|
| **P0** | Unsafe.Add 消除边界检查 | `ref Unsafe.Add(ref GetArrayDataReference(code), IP)` 替代 `ref code[IP]` | 每条指令省 1 次 CLR bounds check | 极低（~3 行） | 无 |
| **P1** | Compare&Branch fusion | Peephole P5：`CMP_* tmp,B,C` + `JUMP_IF_ZERO tgt,tmp` → `JUMP_IF_* tgt,B,C`；+6 OpCode + 6 VMWorld case | 分支指令数 2→1，热循环 -20~30% | 中（~80 行） | P0 |
| **P2** | const + 常量传播 + 条件 DCE | `const` 关键字 + 编译期传播 + 死分支消除 | 减少 LOAD_CONST + 消除不可达代码 | 中（~150 行） | P1 |
| **P3** | FORLOOP 超级指令 | `FORLOOP dst,limit,step,loopTop`；编译器 pattern-match `for(init;cmp;step)` | 循环控制 4 指令→1，数值循环 -40~50% | 中-高（~200 行） | P2 |

> 每步完成后运行 B01-B05 benchmark 确认收益，再推进下一步。

#### 其他展望

| Tier | 核心优化 | 预期收益 | 复杂度 | 状态 |
|------|---------|---------|--------|------|
| **3. 指令编码** | 16B → 4B 紧凑指令（O8） | L1 缓存 **10-20%** | 高 | ⏳ |
| **5. 长期** | 函数指针 Syscall（O11）、SIMD Fix64（O14）等 | 特定路径加速 | 中-高 | ⏳ |
| 函数调用 | FO2 尾调用消除、FO3 小函数内联 | 尾调用不增长调用深度；小函数 -80% 指令 | 中-高 | ⏳ |

> **B3 Tier 1 实施详情**：[Step_B3_Optimization_Tier1.md](Plan/Step_B3_Optimization_Tier1.md)

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
| G5 | `BytecodeCompiler` | ~~编译器缺少 "requires cleanup" 强制检查~~ → 已修复：`SyscallTable.RequiresCleanup()` + `CompileSyscallVoid()`/`CompileSyscallExpr()` 检查（步骤 10 前置） | 中（低） | ✅ 已修复 |
| G6 | `BytecodeCompiler` | ~~`defer`/`using` Cleanup 块内未禁止 `wait`/`wait_for`~~ → 已修复：`_inCleanupBlock` 标志 + `CompileWait()`/`CompileWaitFor()` 检查（步骤 10 前置） | 中 | ✅ 已修复 |

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
| T12 | struct 声明 + 字段读写 | ✅ 已覆盖（CompilerTests CS01：struct 声明 + 字段赋值 + 字段读取算术） |
| T13 | struct 整体赋值 | ✅ 已覆盖（CompilerTests CS02：a = b 逐字段 MOVE） |
| T14 | struct 字段作为 Syscall 参数 | ✅ 已覆盖（CompilerTests CS03：Apply(d.target, d.level, d.ratio)） |
| T15 | struct 字段条件分支 | ✅ 已覆盖（CompilerTests CS04：if s.hp > 0） |
| T16 | struct 未知字段编译错误 | ✅ 已覆盖（CompilerTests CS07：v.z → 编译报错） |

### 11.3 文档缺口（来自档案交叉审查）

以下内容存在于 Archive 早期讨论稿中，已全部合并入本文（B-γ8 完成）：

| # | 来源 | 内容 | 合并位置 |
|---|------|------|---------|
| D1 | VMScript.md | "条件→目标→数据效果→视觉效果"技能流水线模式 | ✅ §1.2「技能效果器流水线模式」 |
| D2 | VMScript2.md | 历史失败教训表（XSLT/XBL/ASP.NET/Flash/AMD/GWT/WebComponents → 设计约束推导） | ✅ §9.2「历史失败教训与约束推导」 |
| D3 | VMScript4.md | 项目级成功标准（5 类验收维度） | ✅ §7.0「成功标准与验收维度」 |
| D4 | VMScript4.md | 设计验证递进轴线（曳光弹 → 编辑器 → 实战接入） | ✅ §7.0b「设计验证递进轴线」 |

---

## 十二、持续集成与跨语言性能对比

### 12.1 GitHub Actions CI 工作流

`.github/workflows/ci.yml` 包含三个自动化 Job：

| Job | 触发 | 内容 |
|-----|------|------|
| **test** | push / PR | 构建 StandaloneRunner → 运行全部 315 个测试断言（TreeWalker 112 + Compiler 168 + Performance 17 + FFScript 18） |
| **benchmark** | test 通过后 | 运行 B01-B05 VM vs C# 基准，生成 `benchmark_ci.md` artifact；push 到 main/master 时自动追加历史记录 |
| **cross-lang** | test 通过后 | 运行 Lua / Python / Node.js 同源基准，生成 `cross_lang_results.md` artifact |

由于 `StandaloneRunner.csproj` 被 `.gitignore` 排除（Unity 约定），CI 中通过 inline `cat >` 自动生成。

### 12.2 性能历史追踪

每次 push 到 main/master 后，benchmark Job 自动执行以下流程：

1. 运行 B01-B05 基准测试
2. 调用 `benchmarks/update-history.sh` 解析结果并追加到 [`benchmarks/performance_history.md`](../benchmarks/performance_history.md)
3. 与上一次记录对比，计算 VM 时间和 Ratio 的变化量（Δ）
4. 若任一 Benchmark 的 Ratio 退化超过 10%，标注 ⚠️ 回归警告
5. 自动 commit 并 push 更新后的历史文件（`[skip ci]` 避免循环触发）

**历史文件格式**：每条记录包含日期、commit SHA、运行环境、各 Benchmark 的绝对值与 Δ 变化。最新记录在最上方。

手动生成历史记录：

```bash
dotnet run --project StandaloneRunner/StandaloneRunner.csproj -c Release -- --bench 2>&1 | tee bench-raw.txt
bash benchmarks/update-history.sh bench-raw.txt
```

### 12.3 跨语言性能基准

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

## 十三、展望与风险汇总

> 详见 [Plan/Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)

### 功能展望

| 分组 | 未完成 | 已完成 |
|------|--------|--------|
| 函数调用 | FF1 跨模块、FF2 回调、FF4 多返回值 | FF3 可选参数 ✅、FF5 非 entry defer ✅ |
| 全局 / 跨步骤 | H1 Handle64、BB1 黑板 Key、PR1 带参配对、FIX1、DM1 双轨、B1 Editor DAP | — |
| Cleanup / using | — | C5 超时 ✅、C6 合并 ✅ |
| 结构体 | — | S4 参数 ✅、SN1 嵌套 ✅、SN2 字面量 ✅ |
| 脚本调试 | — | DBG1-DBG7 全部 ✅ |
| 语言服务 | — | LSP1-LSP7 全部 ✅ |

### 优化展望

已执行优化见 §10.1（16 项）。未实施优化见 §10.2（O8/O11-O14/FO2/FO3）。

### 已识别风险（20 项，全部降至低/极低）

| 分组 | 条目 | 数量 | 降级后等级 |
|------|------|------|-----------|
| 步骤 8 — 已缓解 | R1-R4 | 4 | 极低 |
| 步骤 8 — 前瞻 | R5-R8 | 4 | 低 |
| 步骤 9 | SR1-SR4 | 4 | 低~极低 |
| 全局 | GR1-GR3 | 3 | 低~极低 |
| 外部工具对接 | DR1-DR5 | 5 | 低~极低 |

> 风险降级详细措施见 [Outlook_And_Risks.md §六](Plan/Outlook_And_Risks.md#六风险降级计划目标全部--低--极低)。

---

## 十四、实践专区

> **职责说明**：
> 实践专区记录串行计划之外的探索性实践。每次实践产出独立文档，记录背景、发现的问题、
> 建议的改进方向。实践本身不直接修改代码——所有代码级改动须经讨论后纳入串行计划（新步骤）、
> 展望条目或风险点，走正式流程推进。
>
> **文件命名**：`Docs/Practice/P{NNN}_{简短英文标题}.md`，编号递增。

### 实践处理标准流程

当实践文档中提出的问题或优化建议被正式处理后，按以下流程标记：

1. **逐条标注处理结果**：在每个条目的「建议」段落后追加引用块 `> **处理结果**：…`，使用以下标记：
   - ✅ **完全处理** — 问题已通过紧急任务、串行步骤或直接修复完全解决。附简要说明和链接。
   - 🟡 **部分处理** — 核心问题已解决，但仍有残留待后续步骤跟进。附说明哪些部分未完成。
   - 🔵 **回收至次优先级** — 非紧急项回收到紧急任务区 🔵 列表或展望计划，待后续评估分流。
   - ⏭️ **已跳过** — 在实践过程中已直接解决，或经评估后认为不需要单独行动。附原因。

2. **更新实践文档开头状态**：将文档头部的 `状态` 字段更新为以下之一：
   - `✅ 已处理（日期）` — 所有条目已分流并标注处理结果
   - `🟡 部分处理（日期）` — 部分条目已处理，其余待跟进
   - `实践记录（待讨论）` — 初始状态，尚未处理

3. **更新总结表**：将文档末尾的总结表中「建议归属」列替换为「处理结果」列，体现最终去向。

4. **更新索引表（本节）**：在下方索引表的「状态」列标注当前处理进度。

### 索引

| # | 实践文档 | 主题 | 日期 | 产出建议去向 | 状态 |
|---|---------|------|------|------------|------|
| P001 | [P001_Performance_Baseline_Rebuild.md](Practice/P001_Performance_Baseline_Rebuild.md) | 性能基线重建 + 执行循环优化 | 2026-04-03 | → 串行计划 B-γ3（BM1）、B-γ4（O15） | ✅ 已处理 |
| P002 | [P002_Sandbox_Build.md](Practice/P002_Sandbox_Build.md) | Sandbox 构建实践 | 2026-04-03 | → 紧急独立任务区 E001, E002；次优先级 T001-T003 | ✅ 已处理 |

---

## 十五、紧急独立任务区

> **职责说明**：
> 独立于串行计划的紧急修复通道。恶性缺陷和影响深远缺陷在此跟踪和修复，
> 修复期间串行计划暂停推进。缺陷全部修复后，次优先级任务归还串行计划或展望。
>
> **工作流详情与任务总览**：[Emergency/README.md](Emergency/README.md)
>
> **文件命名**：`Docs/Emergency/E{NNN}_{简短英文标题}.md`，编号递增。

### 当前状态：✅ 已清空（串行计划已恢复）

| 等级 | ID | 缺陷 | 状态 | 详细文件 |
|------|-----|------|------|---------|
| 🔴 恶性 | E001 | 编译器寄存器生命周期 Bug — 2 死变量 + while 循环产生错误结果 | ✅ 已修复 | [E001](Emergency/E001_Register_Lifecycle_Bug.md) |
| 🟠 深远 | E002 | Syscall 寄存器约定隐患 — 手动指定易错、无冲突检测、DAP 不支持 | ✅ 已修复 | [E002](Emergency/E002_Syscall_Register_Convention.md) |

### 次优先级（缺陷修复后处理）

| ID | 内容 | 来源 | 预期去向 |
|----|------|------|---------|
| T001 | Number 智能格式化 | P002-P5 | 展望计划 |
| T002 | 深度递归验证 | P002-P6 | 依赖 E001 修复 |
| T003 | Sandbox 回归测试 | P002-P7 | 工作流优化项（单独提出） |

### 新增风险点（修复后纳入 Outlook_And_Risks.md）

| ID | 所属 | 风险 |
|----|------|------|
| ER1-ER3 | E001 | 分配策略变更影响 / 根因层级不确定 / 被掩盖问题暴露 |
| ER4-ER7 | E002 | 抽象层开销 / 兼容性 / no-op 掩盖错误 / API 变更影响 |
