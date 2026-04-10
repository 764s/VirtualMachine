# KOF98 技能 FFS 脚本化讨论

> **状态**：SK15 💬 新增（技能属性声明式提取）。SK3 💬 待性能验证外，其余 SK1~SK14-3 + Q1~Q4 全部 ✅。14 项 Q4 设计决策锁定。C-1 Lang-6 ✅ / C-1.5 Lang-7 ✅ / C-2 Lang-8 ✅ 已实现。`@force_inline` 关键字已取消 — A5 深度内联为远期计划，内联失败严格程度改由编译器配置控制）
> **来源**：需求讨论 — 将 host-side 技能迁移为 FFS 脚本驱动
> **日期**：2026-04-09（实现状态同步更新）

### 文档格式约定

每个议论点（SK、Q 等）统一使用以下结构呈现：

```
                              ┌─ [唤起记忆] 一眼看出在讨论什么
## 议论点名 [状态]            ┘   （过于复杂时可简化为记忆诱导词）

                              ┌─ [唤起记忆] 一眼看出事情最终进度/核心论点
结论短语                      ┘   意简言赅，不冗余描述，不暴露具体细节

<details>                     ┌─ [详细查询] 供继续深入讨论时查阅细节和接续点
<summary>📋 详细设计</summary> ┘   最终方案的完整细节（设计文档级）
</details>

<details>                     ┌─ [历史追踪] 梳理结论怎么得来的、
<summary>📋 讨论历史</summary> ┘   某个看起来不错的办法被放弃的原因
</details>
```

**各区作用**：
- **标题**：快速唤起"这在讨论什么"的记忆
- **结论区（直接暴露）**：仅放最终结论的短语级描述——能让人一眼看出核心结论即可，无需展开细节
- **详细设计折叠区**：最终方案的完整细节，供实现或继续讨论时参考
- **讨论历史折叠区**：中间过程、已放弃方案、轮次记录——辅助追溯结论的推导过程

**粒度原则**：
- 每个 `##` 对应**一个独立可判定的论点**——能用一句话描述结论的才是一个议论点
- 如果一个议题包含多个独立论点，应拆分为子编号（如 SK14-1, SK14-2），每个子编号独立遵循上述结构
- ⚠️ **避免"整合/汇总/全景"型单节**：将多个独立论点聚合为一个 ## 会导致结论区无法一句话概括、详细设计区过于庞杂。应先识别各论点再分别建节

**状态标记**：
标题中的 `[状态]` 使用以下图标，一眼识别议题是否需要继续讨论：

| 图标 | 含义 | 色调 | 说明 |
|------|------|------|------|
| ✅ | 已完成 | 🟢 绿 | 结论已锁定，无需再讨论 |
| 💬 | 讨论中 | 🟡 黄 | 有待决事项或待验证内容 |
| 🔒 | 已关闭 | ⚪ 灰 | 议题已关闭（不再相关、合并到其他议题等） |

**结构规则**：
- 所有议论点（SK、Q）使用 `##` 级标题，**不允许**使用容器 `##` 来嵌套 `###` 议论点（如 ~~"## 第 N 轮议题" → "### Q1"~~）
- 文档排列顺序：**决议总览 → 背景 → 全部议论点（SK + Q）→ 评估区（Syscall/宿主）→ 附录**
- 折叠区命名统一为 `📋 详细设计` 和 `📋 讨论历史`（不使用 `📋 Q1 完整分析` / `📋 Q2 完整讨论历史` 等变体）

---

## 决议总览

| ID | 主题 | 状态 | 结论（一句话） |
|----|------|------|--------------|
| SK1 | 首批脚本范围 | ✅ | 8 个脚本，覆盖 idle→walk→attack→hit→recover 全生命周期 |
| SK2 | 裁决机制 | ✅ | 分层候选池（姿态分组 + 优先级 + 脚本条件） |
| SK3 | 碰撞框交互方式 | 💬 | 方案 A（脚本推送）vs 方案 B（宿主查询），待性能验证 |
| SK4 | 姿态 Syscall | ✅ | 可新增 SetStance 等 |
| SK5 | 命名规则 | ✅ | `skill_<英文名>.ffs` |
| SK6 | 硬直机制 | ✅ | tick/yield 不受硬直影响；帧号从宿主获取（Syscall 或服务脚本）；硬直不执行为纯业务逻辑 |
| SK7 | 技能条件入口 | ✅ | 同实例 checkEnter()/step() 模块级函数共享变量（Lang-1 模块变量）。💬 函数命名待改善；变量重置时机待讨论 |
| SK8 | 碰撞框 Syscall | ✅ | SetHitbox/SetHurtbox/ClearHitbox/SetPushBox |
| SK9 | 受击标记 | ✅ | 分组 mask 混合方向正确 |
| SK10 | 连招共用环境 | ✅ | V1: 黑板 Syscall；理想形 3（混合式）由 L1+L2+黑板覆盖，需求转入 SK14-2 |
| SK11 | 多阶段技能 | ✅ | Phase 是脚本内行为，提供便利但不强制 |
| SK12 | ECS 数据归属 | ✅ | 脚本内闭环 → VM；外部需读取 → Syscall 推送到宿主 |
| SK13 | 属性脚本共享 | ✅ | 方案 A — 预处理器 `include`，全量展开。已由 Lang-2 实现 |
| SK14-1 | 语言需求全景与优先级 | ✅ | 4 痛点（P1~P4）；L1+L2 最紧迫，L3 黑板变通可行，L4 远期。L1/L2 正交互补，L2≠L3（编译期≠运行时） |
| SK14-2 | 连招脚本覆盖度 | ✅ | 理想形 3（混合式: L1+L2+黑板 Syscall）最务实，Phase 1 即可覆盖 |
| SK14-3 | 实施方向选择 | ✅ | 方向 C（折衷分期）被采纳；Phase 1 (L1+L2+L3) + Phase 2 (L4 XCALL) 全部已实现 |
| Q1 | OOP/ECS 数据兼容 | 🔒 | 不冲突 — SK12 原则 + blittable VMInstanceState 天然兼容 ECS |
| Q2 | 硬直+yield 语句级控制 | ✅ | 方案 A — while+GetFrame()+yield，帧区间循环行业标准模式（7/10 语言同构） |
| Q3 | 跨脚本 VM 使用模式 | ✅ | 6 方向渐进路径：阶段1（方向1+2+3）✅ 完成；阶段3（方向5 服务脚本 = Q4）✅ C-1~C-2 已实现 |
| Q4 | FFS 封装 — 服务脚本 | ✅ | 方式 C ✅；Y1-Plus ✅；统一语法 ✅；@export ✅；@inline ✅；嵌套 Warn/Unlimited ✅；14 项决策锁定。C-1~C-2 已实现（Lang-6/7/8 ✅ 1259 tests）。`@force_inline` 已取消，A5 深度内联远期 |
| SK15 | 技能属性声明式提取 | 💬 | `@export var` 替代 `SetSkillMeta()`；`[names]→[indices]→[values]` 批量提取模式。需 Lang-10（→ VM_Summary）；语言方完成后由需求方提示下一步 |

---

## 一、背景

当前所有技能逻辑均为 host-side C#（`SkillDef.CanActivate`/`CanContinue`/`OnFrame` lambda）。
VM 桥接层已就绪（`GameVMBridge` + `GameSyscalls` ~40 syscall），但 `KOF98/Scripts/` 目录为空，零个 .ffs 脚本被编写或加载。

**目标**：讨论并确定首批用 FFS 脚本实现的技能范围。

---

## SK1: 首批脚本范围 ✅

**结论**：8 个脚本，覆盖 idle→walk→attack→hit→recover 全生命周期。命名规则 `skill_<英文名>.ffs`。

<details>
<summary>📋 详细设计</summary>

**首批脚本清单**：

| # | 技能 | 分类 | 脚本模式 | 文件名 |
|---|------|------|---------|--------|
| S01 | 站立待机 (Idle) | 基础移动 | 循环 + 输入监听 | `skill_idle.ffs` |
| S02 | 前进 (WalkForward) | 基础移动 | 循环 + 每帧速度设置 | `skill_walk_forward.ffs` |
| S03 | 跳 (Jump) | 基础移动 | 有限帧 + 物理驱动 | `skill_jump.ffs` |
| S04 | 近拳 (LightPunch) | 基本攻击 | 有限帧 + 命中检测 | `skill_light_punch.ffs` |
| S05 | 蹲拳 (CrouchPunch) | 基本攻击 | 有限帧 + 蹲姿攻击 | `skill_crouch_punch.ffs` |
| S06 | 上受击 (HitHigh) | 受击 | 有限帧 + 被动触发 | `skill_hit_high.ffs` |
| S07 | 硬倒地 (HardKnockdown) | 倒地 | 有限帧 + 无敌 | `skill_hard_knockdown.ffs` |
| S08 | 原地起身 (StandUp) | 起身 | 有限帧 + 恢复 | `skill_stand_up.ffs` |

**脚本模式分类**：

| 模式 | 特征 | 代表技能 | 核心 Syscall |
|------|------|---------|-------------|
| **循环型** | `while true { yield }`, 输入驱动退出 | Idle, Walk | GetInput, SetVelocity |
| **有限帧型** | `while GetFrame() < N { yield }`, 宿主帧号驱动 | LightPunch, HitHigh | BeginAction, EndAction |
| **物理型** | 初始速度 + 等待落地 | Jump | SetVelocity, IsGrounded |
| **被动型** | 宿主切换层激活, 脚本只播放动作 | HitHigh, Knockdown | BeginAction, SpawnEffectSelf |
| **攻击型** | 帧窗口内命中检测 + 分支处理 | LightPunch, CrouchPunch | CheckAttackHit, ApplyDamage |

**选择原则**：

根据 D_GameArchitecture.md §2.3 VM 应用分级（稳赚/模糊/稳亏）：

| 原则 | 说明 |
|------|------|
| **优先覆盖主循环** | idle → walk → attack → hit → recover，验证完整生命周期 |
| **优先覆盖多样性** | 选择代表不同脚本模式的技能（循环/有限帧/条件驱动/物理驱动） |
| **暂不涉及复杂系统** | 抓投（需双方协调）、闪避（需无敌帧标记）暂不纳入首批 |
| **不以 skill_114/skill_25 为目标** | 提取其参数表示模式作为参考，首批脚本针对基础技能 |

</details>

---

## SK6: 硬直实现机制 ✅

**结论**：

1. **tick 与 yield 不受硬直影响**。tick 是宿主驱动入口，不刻意和帧关联（虽然客观上可与宿主世界帧同步）
2. **实际帧号从宿主获取**，可通过 Syscall 或服务脚本。从便利性角度应优先服务脚本（💬 需确认自动退化优化能否使服务脚本调用性能 ≥ Syscall）
3. **硬直不执行为纯业务逻辑**。业务如关心硬直特殊处理，通过判断是否处于硬直中进行分支处理

<details>
<summary>📋 详细设计</summary>

**两层配合**：
1. **宿主层**：`ApplyHitstun()` → 设置 `Character.HitstunFrames` → 暂停角色时间轴推进
   - 暂停期间：VM 脚本不 Tick（yield 不消费），动画冻结，碰撞框保持
   - 时间轴暂停天然实现"不可操作"（脚本冻住 = 不处理输入）
2. **技能层**：攻击方 `ApplyHitstun()` 同时附加受击标记 → 宿主在硬直结束后根据标记触发受击技能切换
3. **安全兜底**：`HitstunFrames` 倒计时到 0 后，若没有受击技能接管，自动恢复 idle

</details>

<details>
<summary>📋 讨论历史</summary>

> ⚠️ 以下为早期讨论（"方案 A 宿主时间轴暂停"），结论已被上方更新取代。保留供参考。

**早期结论**：方案 A — 宿主时间轴暂停。硬直期间宿主不调用 VM Tick，脚本自然冻结。`yield` / `wait(N)` 已支持，无需新增语法。

#### 前置模型：角色逻辑时间轴

**核心设定**：每个角色拥有一条**逻辑时间轴**，以帧为单位推进。角色的所有功能（动画、碰撞、状态）跟随时间轴推进。

```
角色逻辑时间轴:  [0] [1] [2] [3] ...
                  ↑    ↑    ↑    ↑
                动画  碰撞  状态  输入处理
```

影响时间轴推进的机制：

| 机制 | 效果 | 作用范围 |
|------|------|---------|
| **正常推进** | 每游戏帧，角色时间轴 +1 | 单角色 |
| **硬直** | 角色时间轴暂停推进（攻击停顿/受击停顿） | 单角色 |
| **全屏顿帧** (HitStop) | 所有角色时间轴暂停 | 全局 |
| **时间减速** | 时间轴推进速率 < 1（慢动作演出） | 全局或单角色 |

#### 硬直的时间轴语义

硬直 = 角色时间轴暂停推进 N 帧。具体表现：

- **攻击硬直**：攻击命中瞬间，攻击方时间轴暂停（攻击停顿，表现为命中的"打击感"）
- **受击硬直**：受击方时间轴暂停（受击停顿，表现为被击中的"僵硬"）
- **两者独立**：攻击方硬直帧数和受击方硬直帧数可以不同

硬直的附加效果：
- 阻止部分技能开始（硬直中不可发动新技能）
- 可作为某些技能的开始条件（如：硬直结束后自动进入受击恢复）

#### 候选方案

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 宿主时间轴暂停** | 宿主 `HitstunFrames > 0` 时不推进角色 Tick，VM 脚本的 `yield` 也不消费帧 | 硬直期间脚本自然"冻结"，无需额外逻辑 | 需要宿主在 Tick 层面拦截 |
| **B: 受击技能驱动** | 受击技能脚本内循环等待硬直帧数耗尽 | 脚本可自定义硬直期间行为 | 硬直帧数需要传入脚本 |

#### yield 参数化与帧细分

**FFVM 当前支持情况**：

| 语法 | 编译为 | 语义 | 状态 |
|------|--------|------|------|
| `yield` | `WAIT 1` | 暂停 1 个 Tick，下次 Tick 恢复执行 | ✅ 已支持 |
| `wait(N)` | `WAIT N` | 暂停 N 个 Tick，宿主每 Tick 递减 WaitCounter，归零后恢复 | ✅ 已支持 |
| `wait_for(instanceId)` | `WAIT_FOR reg` | 等待另一个 VM 实例执行完毕后恢复 | ✅ 已支持 |

在方案 A 下，硬直期间宿主不调用 `VMWorld.Tick()`，因此 `yield` / `wait(N)` 都不会推进。帧细分由宿主层 Tick 策略决定，不涉及 VM 语言改动。

</details>

---

## SK2: 裁决机制 ✅

**结论**：分层候选池（姿态分组 + 优先级 + 脚本条件）— 工业标准格斗游戏方案。

> 💬 **待完善**：裁决层流程中的打断优先级和启动优先级的具体交互尚未详细描述。

<details>
<summary>📋 详细设计</summary>

```
宿主裁决层 (每帧):
  1. 查询当前姿态 (Stance) → 获取该姿态的候选技能列表
  2. 追加当前技能声明的取消窗口候选 (如有)
  3. 按优先级排序候选列表
  4. 通用规则快速过滤 (硬直中/优先级不足/冷却中 → 跳过)
  5. 对剩余候选, 逐个调用 checkEnter() 条件检查:
     - 通过 → 激活技能
     - 失败 → 尝试下一个
  6. 全部失败 → 保持当前技能
```

**数据结构**：

```csharp
enum Stance { Grounded, Airborne, Crouching, Knockdown, Hitstun, Dead }

class SkillDef {
    Stance[] AllowedStances;     // 可在哪些姿态下成为候选
    int ActivationPriority;      // 启动优先级 (越小越优先)
    int InterruptPriority;       // 打断优先级 (能打断 <= 此值的技能)
    int VMModuleSlot;            // 脚本 slot (条件+执行都在脚本内)
}
```

**性能估算**：
- 每姿态约 15-20 个候选 → 通用规则过滤后约 3-5 个需要 VM 条件检查
- VM 条件检查 = spawn + 执行约 3-5 条指令 + return → 约 1-2μs/次
- 最坏情况: 5 × 2μs = 10μs/帧，完全可接受

</details>

<details>
<summary>📋 讨论历史</summary>

#### 分歧说明

| 观点 | 描述 |
|------|------|
| **原方案（Agent）** | `CanActivate` 保留在宿主 C#，技能条件是纯判断（≤3行），属于"稳亏"场景。脚本假设条件已通过 |
| **用户倾向** | 技能条件也进 VM。并且条件逻辑应在**技能 VM 内部**（而非独立的裁决 VM 实例） |

#### 裁决机制两大方向

> 共识：具体的条件判断都在虚拟机内实现，两种方向无分歧。核心区别在于**宿主如何决定尝试哪些技能**。

| | **方向 A: 状态机转换表** | **方向 B: 优先级分桶** |
|---|---|---|
| **核心思路** | 预定义"状态→状态"转换通道，配合 `any` 特殊转换兜底 | 将技能按优先级分桶，设定启动优先级和打断优先级，配合技能自定义的转换条件 |
| **候选池** | 当前状态的出边列表（预编译，有限集合） | 当前优先级桶内所有技能（动态过滤） |
| **性能** | ✅ 候选池小，遍历开销低 | ⚠️ 优先级抽象性导致大量技能需遍历条件 |
| **可维护性** | ⚠️ 连接数量可能爆炸（N种状态×M种转换），爆炸后难以调整 | ✅ 添加新技能只需设定优先级+条件，不改全局转换表 |
| **灵活性** | 结构化，适合规则明确的系统 | 灵活，适合规则频繁变化的系统 |
| **代表作** | Unreal GAS 的 AbilityTag+BlockTag | KOF98 原版（优先级+条件表） |

#### 工业实践分析：分层候选池

格斗游戏工业标准实际是 A 和 B 的混合体：

1. **姿态分组限制候选池大小** → 解决 B 的"遍历所有技能"性能问题
2. **优先级排序替代 N×N 转换表** → 解决 A 的"连接爆炸"问题
3. **取消窗口是显式 opt-in** → 不增加全局复杂度

> ⚠️ **已知局限**：分层候选池是缓解而非根治。如果姿态拆分不理想，巨型姿态组内仍面临相同的性能/维护问题。

</details>

---

## SK7: 技能条件入口 ✅

**结论**：方案 D′ — 独立条件检查函数 + 执行函数，同一 VM 实例。两者作为模块级函数共享模块变量（Lang-1 已实现）。

> 💬 **待讨论**：
> - `checkEnter`/`step` 名称待改善（需要更好的命名）
> - 模块变量重置时机问题未讨论（当技能实例被复用时，模块变量如何重置？可能属于业务代码范畴）

<details>
<summary>📋 详细设计</summary>

**核心设计**：

| 函数 | 职责 | 宿主调用时机 | 返回语义 |
|------|------|-------------|---------|
| `func checkEnter(): int` | 条件检测 | 裁决层候选池筛选 | `return 0` = 不满足；`return 1` = 满足 |
| `func step()` | 技能执行 | 技能激活后每帧驱动 | `yield` = 继续；`return` = 技能结束 |

> ✅ checkEnter 和 step 为**模块级函数**，通过模块变量（Lang-1）自然共享状态。早期关于"无法复用变量"的讨论已过期。

**通用规则**（宿主侧，所有技能共享）：
- 姿态匹配（候选技能必须在当前姿态的 AllowedStances 中）
- 优先级判断（InterruptPriority >= 当前技能的 ActivationPriority）
- 硬直/倒地/死亡状态互斥

**特殊规则**（脚本侧 `checkEnter()` 内，技能自定义）：
- 具体输入要求（需要某个按键组合）
- 距离/位置条件（如投技需要近距离）
- 资源条件（如超必杀需要能量）
- 连招窗口（如取消窗口内才可衔接）

**向后兼容**：
- 若脚本不含 `checkEnter()`，宿主回退到宿主侧 `CanActivate` 回调（legacy 路径）
- 若脚本不含 `step()` 而有 `main()`，宿主可使用 `main` 作为执行入口（过渡期）
- 最终目标：所有 VM 技能统一使用 `checkEnter()` + `step()` 双入口

**同实例生命周期**：

```
[spawn] → [checkEnter] → return 0 → [destroy]  ← 条件不满足
                       → return 1 → [redirect to step] → [tick] → [tick] → ... → [完成/destroy]
                                     ↑ 同一实例继续      ← 条件满足后执行
```

**宿主侧实现**（⚠️ 仅为示范草案，非最终实现）：

```csharp
var program = World.Modules.Get(def.VMModuleSlot);
if (!program.TryGetFunction("checkEnter", out var checkEntry)) continue;

int vmId = World.SpawnInstance(def.VMModuleSlot, checkEntry.EntryIP);
_instanceToOwner[vmId] = charId;
World.TickInstance(vmId);

ref var inst = ref World.Pool.Instances[vmId];
bool passed = inst.Registers[0].IsNonZero;

if (!passed)
{
    World.DestroyInstance(vmId);
    _instanceToOwner.Remove(vmId);
    continue;
}

if (program.TryGetFunction("step", out var stepEntry))
{
    inst.IP = stepEntry.EntryIP;
    inst.StateFlags &= ~VMStateFlags.Completed;
    skill.VMInstanceId = vmId;
}
```

**FFVM 改动需求**：方案 D′ 需要"实例重定向"能力（`RedirectInstance`，~5 行实现）。

**有状态条件检查**：

| 模式 | 行为 | 适用场景 |
|------|------|---------|
| **无状态检查** | 每次 `checkEnter()` 都检查 A∧B | 简单条件（按键+姿态）— 90%+ 技能 |
| **有状态检查** | 首次 A 满足后记录，后续只检查 B | 蓄力、连招窗口 |

有状态检查通过模块变量（Lang-1）实现。早期讨论的"路径 3 全局变量"即为模块变量的前身。

**脚本示例**：

```ffs
// skill_light_punch.ffs — 方案 D′ 示例
func checkEnter(): int {
    var lpPressed: int = IsInputPressed(4)
    var grounded: int = IsGrounded()
    if lpPressed == 0 || grounded == 0 {
        return 0
    }
    return 1
}

func step() {
    BeginAction(10, 20)
    defer { EndAction() }
    var hitDone: int = 0
    while GetFrame() < 20 {
        if GetFrame() >= 5 && GetFrame() < 10 && hitDone == 0 {
            var target: int = CheckAttackHit(1001)
            if target > 0 {
                ApplyDamage(target, 1.0, 102)
                hitDone = 1
            }
        }
        yield
    }
}
```

</details>

<details>
<summary>📋 讨论历史</summary>

> ⚠️ 以下讨论中关于"全局变量"的部分已由 Lang-1 模块变量实现。关于"跨脚本共享"的部分已由服务脚本（Lang-6/7/8 XCALL）覆盖。保留供参考。

**早期"全局变量"讨论**（第 7~9 轮，现已由 Lang-1 模块变量实现）：

路径 3（全局变量 = 模块变量前身）为优先探索方向 — 符合"数据留 VM 内"原则。路径 1（宿主黑板）降级为补充 — 仅用于跨脚本/跨角色共享。

**早期"checkEnter/step 共享状态"讨论**：

第 8 轮修正：第 7 轮分析误将 checkEnter 和 step 描述为"独立 VM 实例"，这是误解。设计意图始终是同一 VM 实例。第 9 轮确认 VM 运行时无硬阻碍，"无法复用变量"为编译器/语言层限制（现已由 Lang-1 模块变量解决）。

#### 第 5 轮

倾向提供**专有条件检测入口**，由宿主侧的调用结构决定。

#### 第 6 轮

经审查发现原方案 C（条件在 main 开头）存在问题，修正为方案 D：独立条件函数（多入口）。

#### 第 7 轮

追加复合条件分析。提出无状态/有状态两种检查模式。⚠️ 误将 checkEnter 和 step 描述为"独立 VM 实例"。

#### 第 8 轮

修正实例模型：checkEnter 和 step 应为**同一 VM 实例**。提出 `RedirectInstance` API。

#### 第 9 轮

用户反馈：VM 运行时不存在硬阻碍。要求探索全局变量作为优先路径（路径 3），遵循 SK12"数据留 VM 内"原则。

</details>

---

## SK8: 碰撞框数据来源 ✅

**结论**：碰撞数据完全由技能脚本决定，通过 Syscall 推送到宿主。

**需新增 Syscall**：`SetHitbox(groupId, x, y, w, h)`, `SetHurtbox(x, y, w, h)`, `ClearHitbox`, `SetPushBox`

<details>
<summary>📋 详细设计</summary>

**设计原则**：每个技能脚本负责声明自己在各帧的碰撞框（受击框、攻击框、推挤框）。宿主不做碰撞框的静态预定义。

```ffs
// 脚本内碰撞框示例
if f >= 4 && f < 8 {
    SetHitbox(1001, 0.2, 0.3, 0.4, 0.3)   // groupId, x, y, w, h
}
```

> 结构体方案（将碰撞框参数封装为 struct）取决于 FFVM 结构体支持进度（B-γ7 SN1 嵌套结构体）。

</details>

---

## SK3: 碰撞框交互方式 💬

**结论**：暂定方案 A（脚本推送到宿主）。待性能验证后最终定夺。

<details>
<summary>📋 详细设计</summary>

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 脚本推送到宿主** | 每帧通过 Syscall 将碰撞框参数推送到宿主 component | 性能好 | 宿主存冗余数据 |
| **B: 宿主向脚本查询** | 碰撞系统需要时向脚本查询碰撞框 | VM 数据隔离好 | 性能待验证 |

</details>

---

## SK9: 受击标记 (HitReactionTag) ✅

**结论**：分组 mask 混合方向正确。`damageType` 拆为 `ApplyDamage(targetId, coefficient)` + `ApplyHitReaction(targetId, reactionTag)`。详细枚举设计留待后续。

---

## SK12: ECS 纯数据化与数据归属 ✅

**结论**：脚本内闭环 → 留 VM 内；外部系统必须读取 → Syscall 推送到宿主纯数据 component。

<details>
<summary>📋 详细设计</summary>

| 归属 | 条件 | 示例 |
|------|------|------|
| **VM 内** | 脚本内定义、脚本内消费的数据 | Phase index、帧计数器、连招状态、技能内部条件标志 |
| **宿主侧** | 有强烈外部使用需求的数据 | 动画 ID、碰撞框参数、伤害数值、硬直帧数、受击标记 |

**对姿态切换的影响**：

> 📌 **第 8 轮补充**：姿态（Stance）数据归属讨论。
>
> 期望将姿态切换逻辑留在脚本内，但姿态是**跨脚本共享的角色状态**（裁决层需要读取当前姿态来决定候选池）。这在物理上无法做到脚本内闭环——否则每个技能脚本都要冗余维护同一份姿态数据。
>
> **结论**：姿态属于"外部系统必须读取"的数据，应留在宿主侧。技能脚本通过 `SetStance(stanceId)` Syscall 推送姿态变更到宿主 component。裁决层直接读宿主的姿态字段。
>
> 这与 SK12 数据归属原则完全一致：跨脚本共享状态 → 推到宿主。

**影响范围**：

| 受影响项 | 当前状态 | 纯数据化影响 |
|---------|---------|-------------|
| SK6 硬直 | ✅ 时间轴暂停 | `HitstunFrames` 在宿主侧 ✅ 符合 |
| SK10 连招 | 暂用黑板 | 连招状态留在 VM 内 ✅ 符合 |
| SK11 多阶段 | ✅ Phase | Phase index 留在 VM 内 ✅ 符合；`BeginAction` 推送动画/碰撞框到宿主 ✅ 符合 |
| SK8 碰撞框 | 待设计 | 碰撞框参数必须推送到宿主 → Syscall 推送 |
| SK9 受击标记 | 待讨论 | reactionTag 需推送到宿主 → Syscall 推送 |

> 📌 后续所有新增 Syscall 设计都应先自检数据归属：**脚本内闭环 → 留 VM 内；外部系统必须读取 → Syscall 推送到宿主纯数据 component**。

</details>

---

## SK10: 连招共用环境 ✅

**结论**：V1 用黑板变量（key-value Syscall），理想目标用连招描述脚本集中管理。

<details>
<summary>📋 详细设计</summary>

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 外挂独立逻辑** | 宿主维护 `ComboContext` | 简单直接 | 连招逻辑散落 |
| **B: 黑板变量** ✅ V1 | 角色级 key-value，Syscall 读写 | 脚本自主管理 | 需约定 key 命名 |
| **C: 连招描述脚本** ⭐ V2 | 专门的 `combo_xxx.ffs` 管理连招 | 逻辑集中 | 需脚本间协调机制 |

</details>

---

## SK11: 多阶段技能 ✅

**结论**：Phase 是脚本内行为，`BeginAction` 支持多次调用切换动作（非强制）。符合 SK12 数据归属原则。

<details>
<summary>📋 详细设计</summary>

```ffs
func step() {
    // Phase 1: 起手
    BeginAction(101, 10)
    defer { EndAction() }

    var hit: int = 0
    while GetFrame() < 10 {
        if GetFrame() >= 3 && GetFrame() < 7 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 { hit = 1 }
        }
        yield
    }
    if hit == 0 { return }  // 未命中, 技能结束

    // Phase 2: 追加段
    BeginAction(102, 15)
    while GetFrame() < 15 {
        yield
    }
}
```

</details>

---

## SK13: 属性脚本共享机制 ✅

**结论**：方案 A — 预处理器 `include`，全量展开（const + struct + func）。已由 Lang-2 实现。

> 💬 **待讨论**：include 混入后脚本位置移动的处理方式（路径解析倾向项目根相对路径，但尚未最终确认）。

<details>
<summary>📋 详细设计</summary>

### 核心设计

```ffs
// configs/ground_attack.ffs — 共享属性
const ACTIVATION_PRIORITY: int = 200
const INTERRUPT_PRIORITY: int = 500

// shared/input_helpers.ffs — 共享函数
func isDown236(): int {
    var d: int = IsInputHeld(1)
    var r: int = IsInputHeld(3)
    if d == 0 || r == 0 { return 0 }
    return 1
}

// skill_light_punch.ffs — 技能脚本
include "configs/ground_attack"      // 引入共享属性
include "shared/input_helpers"       // 引入共享函数

const ACTIVATION_PRIORITY: int = 150 // 覆盖（跨文件后者覆盖前者 ✅）

func checkEnter(): int { ... }
func step() { ... }
```

### const/func 覆盖规则

```ffs
// === const 重定义规则 ===
// 规则 1：跨文件覆盖 — 后展开的文件覆盖先展开的文件（类型必须一致）
include "base_config"           // const X: int = 10（来源：base_config）
const X: int = 20               // 覆盖为 20 ✅（来源：当前文件）

// 规则 2：同文件禁止重定义
const Y: int = 10
const Y: int = 20               // ❌ 编译错误

// 规则 3：var 不能覆盖 const
include "base_config"           // const X: int = 10
var X: int = 20                  // ❌ 不能用 var 覆盖 const

// === func 重定义规则 ===
// 规则 4：跨文件覆盖 — 允许（主文件覆盖 include 的函数实现）
include "shared/templates"      // func checkEnter(): int { ... }
func checkEnter(): int { ... }  // ✅ 覆盖

// 规则 5：同文件禁止重定义
func foo(): int { return 1 }
func foo(): int { return 2 }    // ❌ 编译错误
```

### 实现路线（精简，跳过 V0）

| 阶段 | 内容 | 改动范围 |
|------|------|---------|
| V1 | Lexer/Parser 支持 `include "path"` + 预处理器递归展开 + const/func 重定义（含来源追踪+覆盖范围限制） | Lexer + Parser + 新 Preprocessor 模块 + BytecodeCompiler |
| V2 | LSP 跟随 include 解析被引入文件 | LSP Server |

### 路径解析

> 📌 **第 8 轮补充**：相对路径存在文件移动后引用失效的风险，手动修改可能遗漏。
>
> **候选方案**：
>
> | 方案 | 示例 | 优点 | 缺点 |
> |------|------|------|------|
> | **相对于当前文件** | `include "../configs/base"` | GLSL/C 惯例 | 文件移动后 include 路径全部失效 |
> | **相对于项目根** | `include "configs/base"` | 路径稳定，不受文件位置变化影响 | 需定义"项目根"概念（编译器/LSP 需配置） |
> | **混合** | 默认项目根，`./` 前缀表示相对当前文件 | 兼顾两者 | 规则复杂 |
>
> **倾向**：**相对于项目根** — 路径稳定性优先。文件移动时只需改被移动文件本身的 include（如果有），不需要改所有引用它的文件。项目根由编译器配置（如 `--script-root KOF98/Scripts/`）。
>
> 💬 待决 — 需确认项目根概念是否与 LSP workspace 对齐。

### 混入需求全景

| 编号 | 场景 | 混入内容 |
|------|------|---------|
| M1 | 属性共享 | const（优先级、姿态等） |
| M2 | 辅助函数共享 | func（指令判定、距离计算等） |
| M3 | 碰撞框模板 | const / func |
| M4 | AI 行为共享 | func |
| M5 | 入口模板 | func checkEnter() 默认实现 |

全部通过 `include` 一个机制统一解决。

### 待决

| 问题 | 选项 | 决定 |
|------|------|------|
| 关键字选择 | `include` / `import` / `mixin` | ✅ `include` |
| 引入范围 | 全部 vs 仅数据 | ✅ **全部**（const+struct+func） |
| 多引入 | 是否支持多条 include | ✅ 支持 |
| 多级引入 | include 的 include | ✅ 支持（递归展开，检测循环） |
| 路径解析 | 相对文件 / 项目根 / 混合 | 💬 倾向项目根，待决 |
| 覆盖范围 | 跨文件 vs 同文件 | ✅ 跨文件允许，同文件禁止 |
| 覆盖标记 | 隐式 / `override` | ✅ 隐式（编译器可选 warning） |
| V0 过渡 | 是否先实现宿主侧合并 | ✅ **跳过** |
| 入口函数 LSP | 强感知 vs 弱感知 | ⏸️ 暂缓讨论，后期优化项 |

</details>

<details>
<summary>📋 讨论历史</summary>

#### 第 6 轮

来源：技能属性（优先级、允许姿态等）全在 C# `SkillDef` 上配置。多个技能共用相同属性集导致重复配置。希望脚本层有机制支持属性共享和覆盖。

问题描述：
```csharp
// Program.cs — 每个技能都要手动设置相同的属性
lpSkill.AllowedStances = new[] { Stance.Grounded };
lpSkill.ActivationPriority = 200;
lpSkill.InterruptPriority = 500;

hpSkill.AllowedStances = new[] { Stance.Grounded };  // 重复
hpSkill.ActivationPriority = 180;
hpSkill.InterruptPriority = 500;  // 重复
```

FFS 设计约束：零 GC、零动态分配、无类/无继承、无模块系统、编译期完成。适合预处理器/编译期合并方案。

候选方案评估：

| 方案 | 实现复杂度 | LSP | 覆盖 | 命名冲突 |
|------|-----------|-----|------|---------|
| A: 预处理器 include | ⭐ 低 | ✅ | 需 const 重定义 | ⚠️ 中 |
| B: 编译期 mixin | ⭐⭐ 中 | ✅ | 同 A | ⭐ 低 |
| C: 宿主侧合并 | ⭐ 最低 | ❌ | 需 const 重定义 | ⚠️ |
| D: 结构体嵌入 | ⭐⭐⭐ 高 | ✅ | - | ⭐ 低 |

历史脚本系统参考：C/GLSL #include（预处理器文本替换）、Lua require（运行时模块）、GDScript extends（类继承）、Sass @mixin（编译期展开）。FFS 适合 C #include 风格。

#### 第 7 轮

追加需求：方法共用、多引入、覆盖范围限制、入口函数 LSP 感知、跳过 V0 过渡、混入需求全景评估。

- 方法共用：include 展开全部内容（含 func），方案 A 天然支持
- 多引入：按顺序展开，天然支持
- 覆盖范围限制：跨文件允许，同文件禁止。需编译器增加来源追踪（`_constSources` dict）。成本中等，推荐 V2 阶段
- 入口函数 LSP 感知：推荐弱感知（LSP 硬编码约定名），零语法改动
- 跳过 V0：路线精简为 V1 + V2 两阶段
- 混入全景：M1~M5 全部通过 include 统一解决

#### 第 8 轮（当前）

- 入口函数 LSP 感知暂缓讨论，作为后期优化项
- 路径解析：相对路径存在移动后失效风险，倾向项目根相对路径

</details>

---


## SK14-1: 语言需求全景与优先级 ✅

**结论**：4 痛点（P1~P4）；L1+L2 最紧迫，L3 黑板变通可行，L4 远期。L1/L2 正交互补覆盖 90%+，L2≠L3（编译期≠运行时，不应整合应分层递进）。

> ⚠️ **归档说明**：所有需求已实现（Lang-1~Lang-8），下方仅供追溯。详见 [VM_Summary](../../Docs/VM_Summary.md)。

<details>
<summary>📋 详细设计</summary>

**需求全景**：

| ID | 名称 | 作用域 | 机制 | VM 运行时改动 | 编译器改动 | 来源 |
|----|------|--------|------|--------------|-----------|------|
| **L1** | 模块变量 (file-scope var) | 单个 .ffs 文件内，所有函数共享 | 编译器将模块级 `var` 分配到保留寄存器段（如 r56~r63） | **无** — 寄存器在实例中天然持久 | ⭐⭐ 中 — Parser 顶层 var + 寄存器区域划分调整 | SK7 路径 3 |
| **L2** | include 机制 | 编译期跨文件 | 预处理器文本展开，const/struct/func 全量引入 | **无** | ⭐⭐ 中 — 新增 Preprocessor 模块 + 重定义规则 | SK13 方案 A |
| **L3** | 跨模块共享变量 | 运行时跨脚本 | 需要共享内存区域或黑板 Syscall | ⚠️ **需要** — 新增跨实例数据通道 | ⭐⭐⭐ 高 | SK10 连招 |
| **L4** | 跨模块函数调用 | 运行时跨脚本 | ModuleTable 跨模块函数解析 + CALL 扩展 | ⚠️ **需要** — 跨模块 CALL 协议 | ⭐⭐⭐ 高 | SK10 连招 (理想) |

> **核心观察**：P1+P2 是当前最紧迫的缺失，P3 有成熟变通（黑板 Syscall），P4 是远期理想。

**L1 与 L2 的关系 — 正交互补**：

| 单独使用 | 效果 |
|----------|------|
| 仅 L1 | 同文件内 checkEnter/step 共享状态 ✅，但每个脚本都要重复声明 var |
| 仅 L2 | 共享 const/func ✅，但 include 的 var 在每个脚本中是**独立副本** — 不能跨脚本运行时共享 |
| L1 + L2 | 共享 var 声明模板 + 每个脚本实例内持久 ✅ — **覆盖 90%+ 有状态需求** |

**L2 与 L3 的关系 — 编译期 vs 运行时**：

| 维度 | L2 (include) | L3/L4 (跨模块运行时共享) |
|------|-------------|----------------------|
| **时机** | 编译期 — 源码文本展开 | 运行时 — 实例间数据通道 |
| **var 共享** | 每个脚本有**独立副本** | 多个脚本**同一份**数据 |
| **func 共享** | 代码**复制**（零运行时开销但膨胀） | 代码**不复制**（跨模块 CALL，零膨胀） |
| **类比** | C `#include` / GLSL include | Lua `require` / C# `using` |

结论：不应整合，而是分层递进（Phase 1 编译期 → Phase 2+ 运行时）。

</details>

---

## SK14-2: 连招脚本覆盖度 ✅

**结论**：理想形 3（混合式: L1+L2+黑板 Syscall）最务实，Phase 1 即可覆盖。

<details>
<summary>📋 详细设计</summary>

SK10 连招描述脚本需要哪些语言特性？

| SK10 方案 | 需要的语言特性 | Phase 1 覆盖？ | 说明 |
|-----------|--------------|---------------|------|
| **A: 宿主 ComboContext** | 无 FFS 需求 | ✅ | 完全宿主侧 C# 实现 |
| **B: 黑板变量 (V1)** | 黑板 Syscall | ✅ | 脚本通过 Get/SetBlackboard 读写 |
| **C: 连招描述脚本 (V2 理想形)** | L2 + L3 或 L4 | ⚠️ 部分 | 需进一步分析 |

**三种理想形对比**：

| 理想形 | 逻辑集中度 | 实现复杂度 | Phase 1 可实现 |
|--------|-----------|-----------|---------------|
| 1: 查询式（独立管理器） | ⭐⭐⭐ | ⭐⭐⭐ 高 — 需跨模块通信 | ❌ |
| 2: 声明式（include 模板） | ⭐⭐ | ⭐ 低 — 纯编译期 | ✅ |
| 3: 混合式 | ⭐⭐ | ⭐ 低 — L1+L2+现有 Syscall | ✅ |

**理想形 3 具体分工**：
- 连招关系表（哪些技能可以衔接）→ include 共享 const/func（L2）
- 连招内部状态（当前段数、窗口帧计数）→ 模块变量（L1）
- 跨技能通信（"轻拳命中了"通知"重拳可以衔接"）→ 黑板 Syscall（已有）

</details>

---

## SK14-3: 实施方向选择（A/B/C） ✅

**结论**：方向 C（折衷分期）被采纳并执行完毕 — Phase 1 (L1+L2+L3 = Lang-1/2/3) ✅ + Phase 2 (L4 XCALL = Lang-6/7/8) ✅ 全部已实现。

> ⚠️ **归档说明**：所有阶段已实现，下方仅供追溯。详见 [VM_Summary](../../Docs/VM_Summary.md)。

<details>
<summary>📋 详细设计</summary>

**需求痛点**：

| # | 痛点 | 受阻需求 | 当前变通 | 变通代价 |
|---|------|---------|---------|---------|
| **P1** | **函数间无法共享变量** | SK7 有状态条件、SK11 Phase index | 宿主黑板 Syscall | 额外开销；不符合 SK12 脚本内闭环 |
| **P2** | **脚本间无法复用声明** | SK13 属性共享、连招关系表 | 手动粘贴 | 维护成本高 |
| **P3** | **跨脚本运行时无法共享数据** | SK10 连招 | 黑板 Syscall（成熟） | 可接受 |
| **P4** | **跨脚本无法调用函数** | SK10 理想形 1 | 无直接变通 | 放弃理想形 |

**三个方向对比**：

| 方向 | 范围 | VM 运行时改动 | 优点 | 缺点 |
|------|------|-------------|------|------|
| **A: 长期理想** | L1+L2+L3+L4 全部 | ⚠️ 需要 | 覆盖所有需求 | A-3/A-4 复杂度高，周期长 |
| **B: 最速** | L1+L2+黑板正式化 | 无 | 快速交付 | 放弃 SK10 理想形 1 |
| **C: 折衷** ✅ | Phase 1 = B 全部; Phase 2+ = A 的 A-3/A-4 按需 | 分阶段 | 兼顾速度与远期 | Phase 2/3 可能部分回溯 |

**方向 C 被采纳的实际路线**：

| 阶段 | 步骤 | 内容 | 状态 |
|------|------|------|------|
| **Phase 1** | C-1 | 模块变量 (L1) | ✅ Lang-1 |
| | C-2 | include (L2) | ✅ Lang-2 |
| | C-3 | 黑板 Syscall 正式化 | ✅ Lang-3 |
| **Phase 2** | C-4 | 跨模块函数调用 (L4 = XCALL 服务脚本) | ✅ Lang-6/7/8 |
| **远期** | C-5 | A5 深度内联（`@force_inline` 已取消） | ⏳ Lang-9 待定 |

**语言方细则 Q&A**（已归档 — 全部已在实现中解决）：
1. 方向选择 → C ✅
2. L1 寄存器段 → 按需分配（r56+ 动态段）
3. L1 初始化 → `_init` 代码段
4. L2 路径解析 → 项目根相对路径
5. 实施顺序 → L1 先（Lang-1）→ L2（Lang-2）→ L3（Lang-3）
6. 统一设计 → L4 由 Q4 服务脚本讨论进一步收敛为 XCALL+@export 方案

</details>

---

## Q1: OOP/ECS 数据兼容 🔒

**结论**：不冲突 — SK12 原则 + blittable VMInstanceState 天然兼容 ECS，无需额外考虑。

<details>
<summary>📋 详细设计</summary>


> 用户原话：目前的数据使用原则是在脚本中定义并消费就封闭在脚本内。当将来在 ECS 场景中, 会有让数据进入 ECS 中的 C 的需求。这两个需求看起来会对同一份数据有不同的要求。当前要考虑吗？暂时感觉不用, 因为当前以 OOP 风格熟悉, 会自动兼容 ECS, 我的感觉对吗？

#### 回答：你的感觉基本正确，不需要现在额外考虑

**核心判断**：OOP 风格脚本与 ECS 数据需求之间**不存在根本冲突**，因为两者之间已有一道天然边界 —— **Syscall 推送**。

具体分析：

**1. 当前的数据封闭原则天然与 ECS 兼容**

SK12 已确立的数据归属原则：

| 归属 | 原则 | ECS 对应 |
|------|------|---------|
| **VM 内** | 脚本内定义+消费 → 留在寄存器 | `VMInstanceState` 本身就是一个 blittable struct，已经可以作为 ECS Component 挂载 |
| **宿主侧** | 外部系统需读取 → Syscall 推送 | Syscall 的目标（SetStance, ApplyDamage, SetHitbox…）天然对应 ECS 的写 Component |

这两条路径清晰、正交，数据不会出现"同一份数据有不同要求"的矛盾：
- **Phase index、帧计数器、连招段数** → 脚本内消费 → 留在 VM 寄存器 → 不需要进入 ECS Component
- **动画 ID、碰撞框参数、伤害数值** → 宿主必须读取 → 通过 Syscall 推送 → 写入宿主 ECS Component

**2. "自动兼容"的前提条件**

你说"OOP 风格会自动兼容 ECS"，这在以下前提下成立：

| 前提 | 说明 | 当前状态 |
|------|------|---------|
| Syscall 是唯一的数据出口 | 脚本不直接写宿主内存 | ✅ 已满足（Syscall 边界纪律） |
| VM RAM 是纯值类型 | `VMInstanceState` blittable，可 memcpy | ✅ 已满足（~740B struct） |
| 不存在"延迟推送"需求 | 不需要在 Tick 结束后批量同步脚本内部状态到 ECS | ✅ 当前满足 — Syscall 是即时推送 |

只要继续遵守 SK12 原则（**脚本想输出到世界 → 走 Syscall**），OOP 脚本写法就不会阻碍 ECS 集成。

**3. 唯一可能需要重新审视的场景**

有一种假设性场景值得提前意识到（但不需要现在行动）：

> 某些数据在 OOP 阶段是"脚本内闭环"，但 ECS 阶段需要被外部系统读取。

例如：技能脚本的蓄力进度（charge_progress），OOP 阶段只有脚本自己用（闭环 → 留 VM 内），但 ECS 阶段 UI 系统需要读取来显示蓄力条。

**应对方式**：当这种需求出现时，只需新增一个 Syscall（如 `SetChargeProgress(value)`）将数据推送到宿主 Component。脚本内仍然用模块变量维护蓄力进度（闭环），只是额外多一步推送。这是渐进式改动，不需要改变数据模型。

**4. 结论**

| 问题 | 回答 |
|------|------|
| 当前要考虑 ECS 吗？ | **不用**。SK12 原则 + Syscall 边界已经提供了正确的分离 |
| OOP 风格会自动兼容 ECS 吗？ | **基本正确** — 只要继续遵守"输出走 Syscall"纪律 |
| 有没有陷阱？ | 唯一风险是"闭环数据变成需要外部读取的数据"，但处理方式是增量的（加 Syscall），不需要重构 |
| 需要预留什么？ | 无需预留。VM RAM 已经是 ECS-ready 的 blittable struct |

</details>

---

## Q2: 硬直+yield 语句级控制 ✅

**结论**：方案 A — while+GetFrame()+yield 帧区间循环，行业标准模式（7/10 语言同构），零 VM 改动。

<details>
<summary>📋 详细设计</summary>

| 要素 | 说明 |
|------|------|
| **yield 语义** | 始终 = "等下一个 tick"，与 C# `yield return` 一致 |
| **帧号来源** | 宿主通过 `GetFrame()` Syscall 提供，脚本不维护帧计数器 |
| **帧号推进** | 宿主根据硬直决定是否增加帧号，但不管是否推帧，VM tick 始终照常执行 |
| **样板代码** | 每阶段仅 2 行：`while GetFrame() < N { ...; yield }` |
| **行业共识** | 10 种语言调研，7/10（C#/Lua/GDScript/Kotlin/Wren/Squirrel/Fennel）使用完全同构的 while+yield 模式 |
| **VM/语言改动** | 零 — 当前 FFS 已完全支持 |

**典型写法**：

```ffs
func step() {
    // 阶段1：蓄力（帧 0~9）
    while GetFrame() < 10 {
        CreateParticle()
        yield
    }
    // 阶段2：攻击（帧 10~19）
    while GetFrame() < 20 {
        SetHitbox(0, 10, 20, 30, 40)
        yield
    }
    ClearHitbox()
}
```

**收敛理由**：

1. ✅ 可以收敛 — 算完美了（用户确认）
2. ✅ 帧号推进模型正确 — 宿主根据硬直决定是否增加帧号，VM tick 照常
3. ✅ 零 VM/语言改动、零混淆、零耦合、极低样板、行业共识

</details>

<details>
<summary>📋 讨论历史（第 12~17 轮）</summary>

> **用户修改**（第 16 轮）：想明白了需求 — 不是"从上一个 yield 点重放"，而是"指定一段代码让它循环"。给出示例代码：用 while + yield + GetFrame() 条件 break 来实现帧区间内循环执行。这个粗暴做法完全满足需求。要求：(1) 用 while 循环模拟实现具体场景看看效果；(2) 用特定语法实现看看能简单到什么程度。

用户示例：
```ffs
while true {
   CreateParticle()

   // 还需要合理处理 owner
   if owner.GetFrame() >= 10
      break
   yield
}
```

---

#### 第 16 轮 — 语言方分析：while 循环 vs 专用语法

##### 关键洞察

用户的示例揭示了一个重要认知转变：

> **不需要"硬直重放"这个概念**。需要的仅仅是"在一个帧区间内每 tick 执行某段逻辑"。

这个需求完全可以用 **while + yield + 帧条件** 来表达 — 这就是协程循环，yield 保持"等下一 tick"的直觉，完全不需要新机制。

##### 路径 A：纯 while 循环实现（粗暴做法）

以下是用户示例扩展到几个真实场景的完整模拟：

**场景 1：帧区间内持续产生粒子**（用户原始示例）

```ffs
func step() {
    // ... 前置逻辑 ...
    
    // 帧 5~10：每 tick 产生粒子
    while true {
        CreateParticle()
        if GetFrame() >= 10 {
            break
        }
        yield
    }
    
    // ... 后续逻辑（帧 10 之后继续）...
    yield
}
```

✅ 可读，直觉清晰：while 循环 = 重复，yield = 每 tick 一次，GetFrame() 条件 = 退出。

**场景 2：帧区间内持续伤害 + 播放特效**

> ⚠️ 第 17 轮修正：用户指出场景 2/3 用脚本维护 `f` 的写法与场景 1 矛盾 — 既然方案核心是"脚本只关心 tick/yield，帧号由宿主提供 GetFrame()"，所有场景都应统一使用 `GetFrame()`。

```ffs
func step() {
    // 阶段1：起手（帧 0~4）— 空操作，宿主每 tick 自动推帧
    while GetFrame() < 5 {
        yield
    }
    
    // 阶段2：攻击激活（帧 5~15），每 tick 检测伤害
    while GetFrame() < 16 {
        SetHitbox(0, 10, 20, 30, 40)       // 每帧推送碰撞框
        
        if GetFrame() == 5 {
            PlaySound(SFX_SLASH)            // 第 5 帧播放音效（只一次）
        }
        
        yield
    }
    
    // 阶段3：收招（帧 16~24）
    ClearHitbox()
    while GetFrame() < 25 {
        yield
    }
}
```

✅ 完全用 while + yield + GetFrame()。脚本不维护帧计数器，宿主每 tick 自动推进帧号。样板只有 `while GetFrame() < N { ... yield }`。

**场景 3：多种效果叠加的复杂技能**

```ffs
func step() {
    // 帧 0~9：蓄力期，每 tick 产生粒子
    while GetFrame() < 10 {
        CreateParticle()
        yield
    }
    
    // 帧 10~19：释放期，每 tick 造成伤害 + 每 3 帧闪光
    while GetFrame() < 20 {
        SetHitbox(0, 10, 20, 30, 40)
        ApplyDamageToNearby()
        
        if (GetFrame() - 10) % 3 == 0 {
            FlashScreen()
        }
        
        yield
    }
    
    // 帧 20~29：残留火焰，只产生粒子
    ClearHitbox()
    while GetFrame() < 30 {
        CreateFireParticle()
        yield
    }
}
```

✅ 清晰简洁。模式统一：**每个阶段都是 `while GetFrame() < N { ...; yield }`**。样板只有 2 行（while + yield），主体逻辑占绝对比重。

##### 路径 A 的评估（第 17 轮修正后）

| 维度 | 评价 |
|------|------|
| **与 yield 混淆** | ✅ 零 — yield 始终是"等下一 tick"，循环只是普通 while |
| **直觉** | ✅ C#/Lua 协程用户立刻理解 |
| **职责分离** | ✅ 语言层不感知"帧"，宿主通过 Syscall（GetFrame）提供帧信息并推进帧号 |
| **VM/语言改动** | ⭐ 零 |
| **样板代码** | ✅ 低 — `while GetFrame() < N { ...; yield }` 仅 2 行开销（while + yield） |
| **灵活性** | ✅ 极高 — 任意条件、任意嵌套、混合逻辑全部自然表达 |

**关键改进**：统一使用 `GetFrame()` 后，样板从 4 行（while/f++/SetFrame/yield）降到 2 行（while/yield）。脚本不再维护帧计数器 — 这是宿主的职责。

---

##### 路径 B：专用语法（假设不考虑语言-业务耦合）

如果引入一个专用语法来消除样板，能简单到什么程度？

> **注意**：第 17 轮修正后，路径 A 的样板已从 4 行/阶段降到 2 行/阶段（`while GetFrame() < N { ...; yield }`）。路径 B 的价值相应降低 — 它现在只是把 `while GetFrame() < N` + `yield` 合并为一个块。

**语法设计 B1：`frames(end)` 块**

```ffs
func step() {
    // frames 块 = "每 tick 执行一次，直到帧号达到 end"
    // 隐含 yield — 块内代码每 tick 执行一次
    frames(5) {
        // 起手 — 空
    }
    
    frames(16) {
        SetHitbox(0, 10, 20, 30, 40)
        if GetFrame() == 5 {
            PlaySound(SFX_SLASH)
        }
    }
    
    ClearHitbox()
    frames(25) {
        // 收招 — 空
    }
}
```

编译器将 `frames(n) { body }` 展开为：

```ffs
while GetFrame() < n {
    body
    yield
}
```

**场景 2 对比（第 17 轮修正后）**：

```ffs
// 路径 A（while 循环）                    // 路径 B1（frames 语法）
func step() {                              func step() {
    while GetFrame() < 5 {                     frames(5) {}
        yield                              
    }                                          frames(16) {
                                                   SetHitbox(0, 10, 20, 30, 40)
    while GetFrame() < 16 {                        if GetFrame() == 5 {
        SetHitbox(0, 10, 20, 30, 40)                   PlaySound(SFX_SLASH)
        if GetFrame() == 5 {                       }
            PlaySound(SFX_SLASH)                }
        }                                  
        yield                                  ClearHitbox()
    }                                          frames(25) {}
                                           }
    ClearHitbox()
    while GetFrame() < 25 {
        yield
    }
}
```

差距比修正前小了很多 — 路径 A 只多了 `while` 和 `yield` 两个关键字/阶段。

##### 路径 B 的评估（第 17 轮修正后）

| 维度 | B1 `frames(n)` |
|------|------|
| **与 yield 混淆** | ✅ 零 — frames 块内无显式 yield |
| **直觉** | ✅ 直观："到帧 16 前做这些事" |
| **职责分离** | ⚠️ 语言层耦合 GetFrame() Syscall 名称 |
| **VM 改动** | ⭐ — 纯编译期展开，无新 VM 指令 |
| **语言改动** | ⭐⭐ — 新关键字 + Parser + 编译器展开 |
| **样板消除** | ⚠️ 中 — 仅省去 `while` + `yield`，修正后优势缩小 |

##### ⚠️ 路径 B 的核心问题：投入产出比

第 17 轮修正后，路径 A 的样板已经很低（2 行/阶段）。路径 B1 引入新关键字，只省去这 2 行 — **投入产出比不高**。

更重要的问题：`frames(n)` 需要语言层知道 `GetFrame()` 这个 Syscall 的语义，这打破了"语言不感知业务"的原则。

---

##### 路径 D：跨语言借鉴 — 其他语言如何处理"条件循环+暂停"（第 17 轮新增）

> 用户要求从大众和小众语言中寻找借鉴。以下调研聚焦于"在条件满足前反复暂停/恢复执行"这个核心模式。

**D1. C# — `yield return` + while 循环（大众）**

```csharp
// Unity 协程 — 与 FFS 当前路径 A 几乎相同
IEnumerator AttackRoutine() {
    // 蓄力期
    while (GetFrame() < 10) {
        SpawnParticle();
        yield return null;  // 等下一帧
    }
    // 攻击期
    while (GetFrame() < 20) {
        EnableHitbox();
        yield return null;
    }
    DisableHitbox();
}
```

✅ FFS 路径 A 与 Unity 协程模式完全一致。yield + while 是 C# 游戏开发的标准范式。

**D2. Lua — 协程 + coroutine.yield()（大众）**

```lua
-- LÖVE2D / Defold 风格
function attack_routine()
    -- 蓄力期
    while get_frame() < 10 do
        spawn_particle()
        coroutine.yield()  -- 等下一帧
    end
    -- 攻击期  
    while get_frame() < 20 do
        enable_hitbox()
        coroutine.yield()
    end
    disable_hitbox()
end
```

✅ 与路径 A 同构。Lua 游戏脚本领域的标准模式。

**D3. GDScript (Godot) — `await` + `signal`（大众）**

```gdscript
# Godot 4 — 用 await 等待信号
func attack():
    # 蓄力期
    while get_frame() < 10:
        spawn_particle()
        await get_tree().process_frame  # 等下一帧
    # 攻击期
    while get_frame() < 20:
        enable_hitbox()
        await get_tree().process_frame
```

✅ 同样是 while + 暂停原语。Godot 用 `await signal` 代替 `yield`，但模式相同。

**D4. Kotlin — `suspend fun` + `delay()` / `yield()`（大众）**

```kotlin
// Kotlin 协程
suspend fun attackRoutine() {
    while (getFrame() < 10) {
        spawnParticle()
        yield()  // 让出执行权
    }
    while (getFrame() < 20) {
        enableHitbox()
        yield()
    }
    disableHitbox()
}
```

✅ 同构模式。Kotlin 的 `yield()` 与 FFS 的 `yield` 语义相同。

**D5. Ink (inkle) — 选择/跳转（小众 · 叙事脚本）**

```ink
=== attack ===
~ frame = 0
- (charge_loop)
~ spawn_particle()
~ frame++
{ frame < 10: -> charge_loop }
// 攻击阶段...
```

⚠️ Ink 不是协程模型 — 它用跳转+条件实现循环。暂停靠"等待玩家输入"而非 yield。**不适用于帧循环场景**。

**D6. Wren — Fiber（小众 · 嵌入式脚本）**

```wren
// Wren 的 Fiber 类似协程
var attackRoutine = Fiber.new {
    while (getFrame() < 10) {
        spawnParticle()
        Fiber.yield()   // 等下一帧
    }
    while (getFrame() < 20) {
        enableHitbox()
        Fiber.yield()
    }
    disableHitbox()
}
```

✅ 与路径 A 完全同构。Wren（Fiber）是嵌入式脚本语言中协程的标杆实现。

**D7. Squirrel — Generator（小众 · 游戏脚本）**

```squirrel
// Squirrel 用 yield 的 generator 函数
function attackRoutine() {
    while (getFrame() < 10) {
        spawnParticle()
        ::suspend()   // 等下一帧
    }
    while (getFrame() < 20) {
        enableHitbox()
        ::suspend()
    }
    disableHitbox()
}
```

✅ Squirrel 是 Source 引擎（Valve）使用的脚本语言。同样的 while+suspend 模式。

**D8. Haxe/FlxState — `update()` 状态机（大众 · 游戏框架）**

```haxe
// HaxeFlixel — 不用协程，用状态机
override function update(elapsed:Float) {
    switch (phase) {
        case Charge: 
            if (frame < 10) { spawnParticle(); frame++; }
            else phase = Attack;
        case Attack:
            if (frame < 20) { enableHitbox(); frame++; }
            else { disableHitbox(); phase = Recovery; }
    }
}
```

⚠️ 这是**非协程方案** — 用显式状态机 + update 回调。可读性远低于协程。**佐证了协程方案（路径 A）的价值**。

**D9. Zig — `suspend`/`resume`（小众 · 系统语言）**

Zig 曾有协程（async/suspend/resume），但在 0.11 后被移除。其设计理念是"协程应在用户空间实现"。**不提供语言级暂停原语**。这说明有些语言选择把调度完全交给运行时/宿主。

**D10. Fennel/Janet — 用 Lua/C 协程（小众 · Lisp 方言）**

```fennel
;; Fennel — 编译到 Lua，继承 Lua 协程
(fn attack-routine []
  (while (< (get-frame) 10)
    (spawn-particle)
    (coroutine.yield))
  (while (< (get-frame) 20)
    (enable-hitbox)
    (coroutine.yield))
  (disable-hitbox))
```

✅ Lisp 语法不同，但底层模式完全相同。

##### 跨语言调研总结

| 语言 | 类型 | 模式 | 与 FFS 路径 A 同构？ |
|------|------|------|------|
| **C# (Unity)** | 大众 | `while + yield return null` | ✅ 完全同构 |
| **Lua** | 大众 | `while + coroutine.yield()` | ✅ 完全同构 |
| **GDScript** | 大众 | `while + await signal` | ✅ 同构（await 替代 yield） |
| **Kotlin** | 大众 | `while + yield()` | ✅ 完全同构 |
| **Wren** | 小众 | `while + Fiber.yield()` | ✅ 完全同构 |
| **Squirrel** | 小众 | `while + ::suspend()` | ✅ 完全同构 |
| **Fennel/Janet** | 小众 | `while + coroutine.yield` | ✅ 完全同构 |
| **Haxe (FlxState)** | 大众 | 状态机 + update() | ❌ 非协程，可读性差 |
| **Ink** | 小众 | 跳转 + 条件 | ❌ 不适用于帧循环 |
| **Zig** | 小众 | 已移除协程 | ❌ 不提供语言级暂停 |

**结论**：

> **7/10 的语言（包括所有游戏相关语言）使用完全相同的 `while + yield/suspend/await` 模式**。这不是巧合 — 这是协程驱动的帧循环的事实标准（de facto standard）。

路径 A 不是"粗暴做法"— **它是行业共识**。

##### 有没有语言提供更好的语法糖？

在调研范围内，没有发现主流语言为"帧区间循环"提供专用语法。最接近的是：

1. **Godot 的 `await get_tree().create_timer(seconds)`** — 但这是等待时间，不是帧条件
2. **Unity 的 `yield return new WaitUntil(() => condition)`** — 条件等待，但不执行循环体
3. **RPG Maker 的 Wait(frames)** — 纯等待，不执行逻辑

这些都不是"帧区间内循环执行逻辑"的语法糖 — 它们要么是纯等待（不执行），要么是单次条件等待。

**唯一接近的模式是 Unity 的 `WaitUntil`**，但它只能等待条件，不能在等待期间执行逻辑。而 FFS 的需求是"在等待期间每 tick 执行逻辑"，这天然就是 while 循环。

> 💡 **没有语言为 while+yield 提供专用语法糖，因为 while+yield 本身就足够简洁和直观。**

---

##### 综合对比（第 17 轮修正后）

| | 路径 A（while+GetFrame+yield） | 路径 B1（frames 语法糖） |
|---|---|---|
| **样板代码** | ✅ 低 — 2 行/阶段 | ✅ 极少 — 0~1 行/阶段 |
| **yield 混淆** | ✅ 零 | ✅ 零 |
| **学习成本** | ✅ 零 | ⚠️ 新关键字 |
| **语言-业务耦合** | ✅ 零 | ⚠️ 耦合 GetFrame() |
| **VM/语言改动** | ✅ 零 | ⭐⭐ 中 |
| **灵活性** | ✅ 极高 | ⚠️ 受限于块模型 |
| **行业共识** | ✅ 7/10 语言使用同样模式 | ❌ 无先例 |

##### 推荐（第 17 轮更新）

**路径 A（while + GetFrame() + yield）确认为最终方案**。理由加强：

1. **零改动** — 当前 FFS 已经完全支持，今天就能写
2. **零混淆** — yield 保持 tick 直觉，循环就是循环
3. **零耦合** — 语言层不感知帧概念，全部通过 Syscall
4. **样板极低** — 修正后仅 `while GetFrame() < N { ...; yield }`，2 行开销
5. **🆕 行业共识** — C#/Lua/GDScript/Kotlin/Wren/Squirrel/Fennel 7 种语言使用完全相同的模式
6. **🆕 无先例** — 调研 10 种语言，无一为 while+yield 提供专用语法糖

> Q2 可以视为**基本收敛** — while+yield 是答案，不需要新语法。

#### 第 15 轮旧待确认项（已由第 18 轮收敛覆盖）

（已收敛，见上方结论。）

#### 第 17 轮 — 修正 GetFrame() 一致性 + 跨语言调研

用户指出场景 2/3 的 `f` 变量与 GetFrame() 方向矛盾。语言方修正全部场景统一使用 GetFrame()，样板降为 2 行/阶段。新增 10 种语言调研，7/10 使用 while+yield 同构模式。Q2 基本收敛。

#### 第 16 轮 — 用户想明白需求：while 循环 vs 专用语法

用户认知转变：不需要"重放"概念，只需要"在帧区间内循环执行"。给出 while+yield+GetFrame() 条件 break 的示例代码，要求模拟实现和对比专用语法。

语言方分析：模拟 3 个真实场景，提出路径 A（while 循环，零改动）和路径 B1（frames 语法糖）。推荐路径 A 先上路。关键发现：如果帧号由宿主维护，样板可简化为 `while GetFrame() < N { ...; yield }`。

#### 第 15 轮 — 用户决定 yield 保持 tick 直觉

用户明确选择思路 A：yield 保持 C# 直觉（= 等下一 tick/帧）。硬直帧逻辑通过独立的"帧区间重复"机制处理。三个设计约束：不与 yield 混淆、统一直觉、语言/宿主职责分离。

语言方分析提出 4 个方案（R1~R4），推荐 R1（纯宿主方案 — 独立函数 onHitstun）：零 VM/语言改动，职责清晰，通过模块变量共享状态。

#### 第 14 轮 — 用户接受双帧概念，质疑 yield 语义

用户接受世界帧/角色帧区分、IsLogicTick 判断等核心概念。但指出 X4a 中 yield 的"从上次 yield 恢复执行"在硬直帧中行为违直觉 — C# 中 yield return 恢复意味着"新帧"，X4a 中恢复可能是"同一帧重复"。用户追问"是否暗示问题出在 yield 上"。

语言方分析：用户直觉正确。提出三条思路：
- A：yield 仅等角色帧（C# 一致，但硬直帧不执行）
- B：引入双暂停原语（yield 等角色帧 + tick 等世界帧）
- C：重定义心智模型（yield = 暂停点，不保证新帧）

推荐**方案 X4（双 Tick + IsLogicTick Syscall）**。

| 要点 | 说明 |
|------|------|
| **宿主调度** | 正常帧 `Tick(isLogicTick=true)`；硬直帧 `TickPaused()`（`isLogicTick=false`） |
| **yield 语义** | X4a：硬直帧不递减 `WaitCounter`，但仍执行脚本 |
| **脚本控制** | 用 `if IsLogicTick()` Syscall 保护帧驱动逻辑 |

```ffs
// X4 示例
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        if IsLogicTick() {
            f = f + 1
            if f == 60 { SetSomeFlag() }
        }
        ApplyDamageToNearby()
        yield
    }
}
```

**方案对比**：

| 维度 | X1 标注块 | X2 双游标 | X3 always 协程 | **X4 双Tick** |
|------|----------|----------|---------------|--------------|
| VM 改动 | ⭐⭐⭐ 大 | ⭐⭐⭐ 大 | ⭐⭐ 中 | **⭐ 小** |
| 语言改动 | ⭐⭐⭐ 新语法 | ⭐⭐⭐ 新语法 | ⭐⭐ 新修饰符 | **⭐ 仅 Syscall** |
| 同函数内混合逻辑 | ✅ | ✅ | ❌ 仍需分离 | **✅** |
| 向后兼容 | ✅ | ⚠️ | ✅ | **✅** |

#### 第 14 轮 — 用户接受双帧概念，质疑 yield 语义

用户接受世界帧/角色帧区分、IsLogicTick 判断等核心概念。但指出 X4a 中 yield 的"从上次 yield 恢复执行"在硬直帧中行为违直觉 — C# 中 yield return 恢复意味着"新帧"，X4a 中恢复可能是"同一帧重复"。用户追问"是否暗示问题出在 yield 上"。

语言方分析：用户直觉正确。提出三条思路：
- A：yield 仅等角色帧（C# 一致，但硬直帧不执行）
- B：引入双暂停原语（yield 等角色帧 + tick 等世界帧）  
- C：重定义心智模型（yield = 暂停点，不保证新帧）

#### 第 12 轮 — 原始提案

#### Q2 早期提案: 硬直期间是否让脚本进入（时间轴暂停时的 VM Tick 策略）

> 用户原话：希望角色时间轴暂停时脚本能进入, 这样能有更多自由度。如果真想不执行也是简单跳过, 不会太拖累性能。但是硬直进入虚拟机之后, yield/wait 可能要正确区分时间轴(包含了硬直)暂停期间是否要算在等待帧内的问题。

#### 分析：这是对 SK6 方案 A 的修正提案

SK6 当前结论是 **方案 A（宿主时间轴暂停）**：硬直期间宿主不调用 `VMWorld.Tick()`，脚本自然冻结。用户现在提出**方案 A′** — 硬直期间仍然 Tick 脚本，但引入机制让脚本知道当前处于暂停期。

#### 方案对比

| 维度 | SK6 方案 A（现行） | 方案 A′（用户提案） |
|------|-----------------|-----------------|
| 硬直期间 VM Tick | ❌ 不调用 | ✅ 照常调用 |
| 脚本自由度 | 零 — 完全冻结 | 高 — 脚本可自行决定暂停期行为 |
| yield/wait 语义 | 无歧义 — 不 Tick 就不消费 | ⚠️ 需要区分"逻辑帧"和"物理帧" |
| 性能 | 暂停期零开销 | 暂停期有 Tick 开销（但通常很小） |
| 脚本复杂度 | 简单 — 不用关心暂停 | 稍高 — 需要感知暂停状态 |
| 典型用途 | 纯冻结效果 | 硬直期间播放受击特效、震动、闪烁等 |

#### yield/wait 的帧计数问题

这是方案 A′ 的**核心难点**。当前 VM 中：
- `yield` = `WAIT 1`（等待 1 个 Tick）
- `wait(N)` = `WAIT N`（等待 N 个 Tick）
- `WaitCounter` 每次 `Tick()` 递减 1

如果硬直期间继续 Tick，`wait(10)` 的 10 帧是**包含硬直帧还是只算逻辑帧**？两种语义都有合理的使用场景：

| 场景 | 期望的 wait 语义 | 说明 |
|------|----------------|------|
| 攻击动画等待 10 帧 | wait(10) 应只算**逻辑帧** — 硬直帧不算 | 否则攻击动画会因为硬直被"吃掉"若干帧 |
| 受击闪烁效果持续 5 帧 | wait(5) 应算**物理帧** — 含硬直帧 | 受击特效需要在硬直期间正常播放 |
| 蓄力技能等待 30 帧 | wait(30) 应只算**逻辑帧** | 蓄力不应因对手受击硬直而加速 |

#### 推荐方案：双 Tick 模式（逻辑帧 + 物理帧分离）

如果要采用方案 A′，建议引入"Tick 类型"区分：

**方案 A′-1: 宿主传入 Tick 类型**

```
宿主 Tick 调用:
  正常帧  → VMWorld.Tick()         // isLogicTick = true
  硬直帧  → VMWorld.TickPaused()   // isLogicTick = false
```

VM 内部行为区分：

| 操作 | 逻辑帧 (`Tick`) | 暂停帧 (`TickPaused`) |
|------|----------------|---------------------|
| `yield` / `wait(N)` | WaitCounter 正常递减 | WaitCounter **不递减** |
| 脚本执行 | 正常执行 | 正常执行 |
| Syscall | 全部可用 | 全部可用 |

这样 `wait(10)` 在任何情况下都意味着"等待 10 个逻辑帧"。脚本在暂停帧被调用时，如果执行到 `yield` 或 `wait(N)`，下一个暂停帧**仍然会执行脚本**（因为 WaitCounter 没递减，但也没阻止执行——需要进一步设计）。

这里有一个微妙问题：当前的 `yield` 编译为 `WAIT 1`，如果暂停帧不递减 WaitCounter，那脚本执行到 `yield` 后下一个暂停帧怎么办？

**细化设计**：

```
暂停帧 TickPaused() 的语义:
1. WaitCounter > 0 且是暂停帧 → 不递减，不执行（冻结中的脚本保持冻结）
2. WaitCounter == 0 → 正常执行脚本
3. 脚本执行到 yield/wait → 设置 WaitCounter，return
4. 下一个暂停帧 → 回到 1，WaitCounter 不递减

效果: 
- 正在 wait 中的脚本在暂停帧完全冻结（与方案 A 行为一致）
- 刚被 Spawn 或刚从 wait 恢复的脚本可以在暂停帧执行一次，直到 yield
```

**方案 A′-2: 脚本内感知暂停（Syscall 查询）**

更简单的替代方案 — 保持方案 A（暂停期不 Tick），但提供 Syscall 让脚本在恢复后查询"刚才暂停了多久"：

```ffs
func step() {
    BeginAction(101, 10)
    var f: int = 0
    while f < 10 {
        var paused: int = GetPausedFrames()  // 返回上次 yield 到现在经过的暂停帧数
        if paused > 0 {
            // 可以补偿暂停期间应发生的事情
            SpawnEffect(EFFECT_HIT_FLASH)
        }
        f = f + 1
        yield
    }
}
```

**方案 A′-3: 暂停期间执行独立回调函数**

保持 `step()` 在暂停期冻结，但允许宿主在暂停帧调用脚本的**另一个入口函数**：

```ffs
func step() {
    // 正常逻辑 — 暂停期间冻结
    BeginAction(101, 10)
    var f: int = 0
    while f < 10 { f = f + 1; yield }
}

func onPaused() {
    // 暂停期间每帧调用 — 可选实现
    // 无 yield/wait — 单帧执行完毕
    var flash: int = GetModuleVar(0)  // 或模块变量
    if flash > 0 { SpawnEffect(EFFECT_HIT_FLASH) }
}
```

宿主在暂停帧调用 `TickInstance` 指向 `onPaused` 入口。由于 `onPaused` 不允许 yield/wait，不存在帧计数歧义。

#### 方案对比总结

| 维度 | A′-1 双 Tick 模式 | A′-2 查询补偿 | A′-3 独立回调 |
|------|-----------------|-------------|-------------|
| VM 改动 | ⭐⭐ 中 — 新增 TickPaused + WaitCounter 分支 | ⭐ 小 — 新增 1 个 Syscall | ⭐ 小 — 宿主层调度 |
| yield/wait 歧义 | ⚠️ 有 — 需要仔细设计暂停帧的冻结恢复 | ✅ 无 — wait 语义不变 | ✅ 无 — onPaused 内禁止 yield |
| 脚本自由度 | ⭐⭐⭐ 最高 — 暂停帧可执行任意逻辑 | ⭐⭐ 中 — 只能在恢复后补偿 | ⭐⭐ 中 — 独立回调，但不能修改 step 的 wait 状态 |
| 实现复杂度 | 高 — 双时钟语义贯穿 VM | 低 — 仅 Syscall | 低 — 宿主层调度 |
| 与现有 SK6 兼容 | ⚠️ 修改 Tick 核心逻辑 | ✅ 完全向后兼容 | ✅ 完全向后兼容 |

#### 语言方建议

1. **如果只是为了"硬直期间播放特效/闪烁"**，**A′-2 或 A′-3 已经足够**。A′-3（独立 `onPaused` 回调）与现有 `checkEnter/step` 双入口模式风格一致，推荐优先考虑。

2. **A′-1（双 Tick 模式）** 虽然自由度最高，但引入了"逻辑帧 vs 物理帧"的全局复杂度。`wait(N)` 的语义会变得依赖上下文，增加脚本编写者的心智负担。除非有 A′-2/A′-3 无法满足的硬需求，建议不引入。

3. **性能方面**：用户说"简单跳过不会太拖累性能"是对的。暂停帧进入 VM Tick，如果脚本正在 `wait` 中且 WaitCounter 不递减，只多了一次 `WaitCounter > 0` 检查和一次 `continue`，开销可忽略。但如果要执行脚本逻辑（如 A′-1），则有实际的 ExecuteInstance 开销，虽然对少量实例（格斗游戏典型 2-4）影响极小。

#### 待用户确认

1. **暂停期间脚本需要做什么？** — 仅特效/音效/闪烁，还是需要修改 step 的执行流程？
2. **是否接受 A′-3（onPaused 独立回调）**？如果是，后续设计将保持 yield/wait 语义不变。
3. **如果选 A′-1，是否接受 yield/wait 始终按逻辑帧计数**？（暂停帧中 WaitCounter 不递减）


---

#### 第 12.5 轮 — 确认 yield/wait 模型

> **用户第 12.5 轮反馈（Q2）**：
> - 想法是跟随宿主时间轴比较自然。
> - 但发现一个大坑：类似条件的 `A&B` 和 `A 下一帧& B` 的区分，step 也需要有类似区分。
> - 一个想法是：step 总是打算一次执行完，但遇到 yield/wait 会暂停。**这里正好可以处理宿主时间轴暂停** — 除非宿主时间轴前进指定的帧数，否则暂停不会继续。
> - **[问题]** 但这带来一个新问题：yield/wait 作为语言层的机制需要与"宿主时间轴"这个业务层的概念关联起来。
> - 请确认以上模型是否合适。如果是，则问题转换为对 [问题] 的讨论。如果有严重误解，告诉错误在哪。

#### 语言方回复（第 12.5 轮）

**你的模型完全正确，没有误解。** 逐点确认：

##### 1. "step 总是打算一次执行完，遇到 yield/wait 会暂停" — ✅ 正确

这正是 FFVM 的执行模型。当宿主调用 `Tick()` 或 `TickInstance(id)` 时：

```
ExecuteInstance(ref inst) 开始执行
  → 执行指令 → 执行指令 → … → 遇到 WAIT 指令
  → inst.WaitCounter = N; inst.IP++; return;  ← 交出执行权
  → 控制权回到宿主
```

脚本**每次被 Tick 时都打算执行到底**（直到 return 结束或遇到 yield/wait 暂停）。这与 Unity 协程 `yield return` 的心智模型一致。

##### 2. "除非宿主时间轴前进，否则暂停不会继续" — ✅ 正确

当前实现就是如此。`WaitCounter` 只在 `Tick()` 被调用时递减：


```csharp
// VMWorld.Tick() 内
if (inst.WaitCounter > 0 && !killed)
{
    inst.WaitCounter--;  // 只有宿主调用 Tick() 时才递减
    continue;
}
```

如果宿主不调用 `Tick()`（硬直期间时间轴暂停），`WaitCounter` 就不递减 → yield/wait 的等待帧只算**宿主真正推进的帧**。这天然实现了"硬直期间脚本冻结"的效果。

##### 3. [问题] yield/wait 语言层机制与"宿主时间轴"业务层概念的耦合

这是你提出的核心问题，也是一个好问题。我来分析：

**当前的耦合方式**：

```
语言层            业务层                    物理层
─────────        ─────────────            ─────────
yield (= wait 1) →  "等 1 帧"         →  WaitCounter = 1
wait(N)           →  "等 N 帧"         →  WaitCounter = N
                     ↑
                     何时递减？
                     → 宿主调用 Tick() 时递减
                     → 宿主不调 Tick() = 帧不流逝
```

**关键洞察**：yield/wait **已经** 与宿主时间轴耦合了，只是这个耦合是**隐式的** — 通过"谁调用 Tick()"来体现。

| 耦合方式 | 说明 | 是否需要语言改动 |
|---------|------|----------------|
| **隐式耦合（当前）** | yield/wait 的 "1 帧" = "宿主调用 Tick() 1 次"。宿主控制何时调用 Tick()，从而控制帧的含义 | ❌ 不需要 |
| **显式耦合（如果要）** | 语言层区分 `yield`（等 1 逻辑帧）vs `yield_real`（等 1 物理帧），VM 内部维护双时钟 | ⚠️ 需要语言+VM 改动 |

**结论**：隐式耦合已经满足你描述的需求。**不需要**在语言层引入"宿主时间轴"的概念。

具体来说：

```
场景 1: 正常帧
  宿主: Tick() → Tick() → Tick() → ...
  脚本: yield → 等 1 → 恢复 → yield → 等 1 → 恢复 → ...
  效果: 每帧执行一段脚本逻辑 ✅

场景 2: 硬直 5 帧
  宿主: Tick() → [不调 Tick ×5] → Tick() → ...
  脚本: yield → [冻结 ×5] → 等 1 → 恢复 → ...
  效果: 硬直期间脚本自然冻结，恢复后无感继续 ✅

场景 3: 全屏顿帧
  宿主: [不调任何角色的 Tick ×3] → Tick() → ...
  脚本: [冻结 ×3] → 恢复 → ...
  效果: 全局暂停 ✅
```

**"耦合"不是问题，而是正确的设计**。yield/wait 表达的是"等待逻辑帧"，而"什么算一个逻辑帧"是宿主的职责。这种分层是干净的：

```
┌─────────────────────┐
│ 脚本层               │  yield = "我这一帧的活干完了，等下一帧"
│ (只关心逻辑帧)        │  wait(N) = "我要等 N 个逻辑帧"
├─────────────────────┤
│ 宿主调度层            │  决定"何时算一个逻辑帧" → 调用 Tick()
│ (控制时间轴)          │  硬直 = 不调 Tick()
│                     │  正常 = 每物理帧调 Tick()
├─────────────────────┤
│ VM 引擎层             │  Tick() 被调用 → WaitCounter-- → 为 0 时恢复执行
│ (只关心 Tick 调用)    │  Tick() 不被调用 → 什么都不发生
└─────────────────────┘
```

##### 4. 关于"A&B 和 A 下一帧& B"的区分

你提到的这个大坑，我理解是指：

> **同帧内多个条件/步骤** vs **跨帧的条件/步骤**

在 step 中也存在类似问题：

```ffs
func step() {
    // 场景 A: 这两行在同一帧内执行（同一次 Tick）
    SetHitbox(1, 10, 20, 30, 40)
    BeginAction(101, 10)
    
    // 场景 B: yield 后下一帧才执行
    yield
    SetHitbox(2, 50, 60, 70, 80)  // 这是下一帧
}
```

这在当前模型中已经自然解决了：
- **不 yield** → 同一次 `ExecuteInstance()` 调用内连续执行 → 同帧
- **yield** → 交出执行权 → 下一次 `Tick()` 恢复 → 下一帧

脚本编写者通过 yield 的位置来精确控制"什么在同一帧，什么在下一帧"。**无需额外机制**。

##### 5. 综合结论

| 你的判断 | 语言方确认 |
|---------|----------|
| step 总是打算一次执行完，yield/wait 暂停 | ✅ 正确 — 这就是 FFVM 的执行模型 |
| 宿主不推进时间轴 → yield/wait 不恢复 | ✅ 正确 — WaitCounter 只在 Tick() 中递减 |
| yield/wait 与宿主时间轴有耦合 | ✅ 存在耦合 — 但这是**正确的隐式耦合**，不需要语言改动 |
| 这个模型足以处理硬直 | ✅ 足够 — 宿主不调 Tick() 即冻结 |

**[问题] 的回答**：yield/wait 不需要在语言层面与"宿主时间轴"显式关联。当前的隐式耦合（"1 帧" = "宿主调用 Tick() 1 次"）已经是正确的设计。宿主通过控制是否调用 `Tick()` 来表达时间轴暂停/恢复，脚本无需感知。

**Q2 可以关闭吗？** 如果你接受以上模型（宿主控制 Tick 节奏 = 控制时间轴），则 SK6 方案 A 维持不变，无需新增 VM 机制。第 12 轮提出的 A′-1/A′-2/A′-3 方案仅在你需要"硬直期间脚本仍需执行某些逻辑"时才需要引入。

---

#### 第 13 轮 — 用户深化担忧 + 场景分析

> **用户第 13 轮反馈（Q2 深化）**：
> - 这么解释的前提是：认为宿主时间轴暂停、tick 不推进是不变事实。
> - **[意外情况]** 是否存在另一种情况：同一个招数有一段逻辑要求即使处于硬直中，也会持续对周围的敌人造成伤害？看起来是有可能的。这已经破坏了解释的前提。
> - **[担忧]** 进一步麻烦的地方是：我们可以通过为虚拟机的某个调用配置"是否在硬直中推进"（很好，这看起来是纯业务层配置），但这样我们好像放弃了把"[意外情况]"的逻辑和普通情况配置在一起的权力，天然要求它们在不同的 tick 里。这种放弃是否合理？是否符合直觉？是否回头会配置麻烦？
> - 并且这对用户造成了心智负担：看到 yield，用户需要思考这个 yield 是否会在硬直中执行。（不过另一方面，用户完全可以理解成"这就是虚拟机 tick"而不必考虑是否硬直。）
> - **[更深担忧]** 这种强迫分离函数，导致原本我们想解决的"强制平行配置导致大量重复配置"的感觉又回来了 — 我们被迫为了语法上的不支持，导致要分离书写这两种逻辑。
> - 更害怕这成为物理限制，不得不这么做。


#### 语言方回复（第 13 轮）— 硬直+yield 深度分析

你的担忧是非常好的。让我逐层拆解：

##### 1. [意外情况] 的具体场景确认

先把你说的场景具体化：

```
角色 A 使用"持续伤害场"技能（例如：旋风斩/电场）
→ 技能激活后，每帧对周围敌人造成伤害
→ A 被对手击中，进入硬直
→ 期望：硬直期间，旋风斩仍然持续伤害周围敌人
→ 但如果宿主不 Tick A 的 VM → 旋风斩脚本冻结 → 伤害停止
```

**这个场景确实存在，你说得对 — 它破坏了"硬直=完全冻结"的简单前提。**

但我认为这里有一个关键区分：

| 场景 | 硬直期间行为 | 格斗游戏中的典型性 |
|------|-----------|----------------|
| A. 普通攻击（如轻拳） | 硬直 = 完全冻结。打击感表现 | ⭐⭐⭐ 极常见 |
| B. 持续型效果（如旋风斩/电场/毒雾） | 硬直期间效果继续 | ⭐ 较少见 — 且通常不通过技能脚本 step() 驱动 |

**场景 B 在格斗游戏中通常的处理方式**：持续伤害不是 step() 每帧主动施放的，而是**宿主层的碰撞系统**驱动的。

> **用户第 13.5 轮反驳①**：脚本解决的是技能执行，不管上层是不是动作，例如是 buff，最终 buff 也交给虚拟机实例执行。好在 buff 可以选择硬直时执行虚拟机，但问题又来了：假如 buff 里也包含硬直中不执行的逻辑呢？因此这个例子不能帮助解决问题。

```
具体机制：
step() → SetHitbox(range=120, damage=10, type=PERSISTENT)  // 一次设置
         yield × N                                         // 等待持续时间

宿主碰撞系统每帧：
  foreach hitbox where type == PERSISTENT:
    if 碰撞检测(hitbox, 其他角色):
      ApplyDamage(...)    // 宿主层自动执行，不经过脚本
```

> **用户第 13.5 轮反驳②**：即使由碰撞机制执行，也最终会交给虚拟机实例。直接由碰撞执行在我看来是不健康的设计。

这种设计下，**即使脚本冻结，宿主层的碰撞系统仍在运行**，持续伤害不受影响。脚本只负责"设置 hitbox"和"取消 hitbox"，不负责每帧执行伤害判定。

##### 2. 重新审视：到底需要脚本在硬直中执行什么？

让我用穷举法列出硬直期间可能的需求：

| # | 硬直期间需求 | 是否需要脚本执行 | 推荐处理层 |
|---|------------|----------------|-----------|
| ① | 持续伤害（旋风斩） | ❌ 不需要 | 宿主碰撞系统 — hitbox 持续存在即可 |
| ② | 受击闪烁/特效 | ❌ 不需要 | 宿主 VFX 系统 — 独立于脚本 |
| ③ | 受击音效 | ❌ 不需要 | 宿主 SFX 系统 — 独立于脚本 |
| ④ | 硬直恢复后自动触发受击动作 | ❌ 不需要 | 宿主层 — 硬直结束后触发新技能 |
| ⑤ | 硬直期间读取某些状态/做判断 | ⚠️ 可能需要 | 需要具体场景 |
| ⑥ | 硬直期间修改自身数据（如蓄力进度暂停/重置） | ⚠️ 可能需要 | 可在恢复后补偿（A′-2） |

**关键洞察**：①~④ 都可以在宿主层处理，不需要脚本在硬直期间执行。只有 ⑤⑥ 可能需要脚本参与，但这些场景通常可以用"恢复后补偿"（A′-2）或"独立回调"（A′-3）解决。

##### 3. 直面 [担忧]：分离函数 = 重复配置？

你担心"被迫分离成 step() + onPaused() = 回到强制平行配置"。让我验证这个担忧是否成立：

**情况 A — 旋风斩（持续伤害 + 可能被打中）**

```ffs
// 方案 1: 宿主碰撞系统处理（推荐 — 不需要分离）
func step() {
    BeginAction(ACTION_WHIRLWIND, 30)
    SetHitbox(1, 0, 0, 120, 120)     // 一次设置持续 hitbox
    defer { ClearHitbox(1) }          // 技能结束时清除
    
    var f: int = 0
    while f < 30 {
        // 这里只管动画帧推进
        f = f + 1
        yield   // 硬直时冻结 → 动画暂停、帧计数暂停
                // 但 hitbox 仍存在 → 宿主碰撞系统继续判定伤害
    }
}
// ✅ 不需要 onPaused()
// ✅ 不需要分离逻辑
// ✅ 硬直 = 冻结脚本 → 但伤害由宿主碰撞系统独立驱动
```

**情况 B — 蓄力技能（蓄力进度需要在硬直时暂停）**

```ffs
func step() {
    var charge: int = 0
    while charge < 60 {
        charge = charge + 1
        yield   // 硬直时冻结 → 蓄力自然暂停 → 这正是想要的行为！
    }
    // 蓄满后释放
    ApplyDamage(0, charge * 2)
}
// ✅ 硬直 = 冻结 → 蓄力暂停 → 完美匹配
```

---

#### 第 13.5 轮 — 用户反驳 + X1-X4 方案分析

**情况 C — 假设真的需要硬直期间执行某段逻辑**

> **用户第 13.5 轮反驳③** — 展示核心矛盾场景：
>
> ```ffs
> func step() {
>     BeginAction(ACTION_AURA, 60)
>     var f: int = 0
>     while f < 60 {
>         f = f + 1
>
>         if f == 60 {
>             SetSomeFlag() // 不能在硬直中执行
>         }
>
>         ApplyDamageToNearby() // 需要在硬直中执行
>
>         yield // 此时由于硬直, yield 的意义进入歧义状态
>     }
> }
> ```
>
> 同一个函数体内，有些语句应在硬直中执行，有些不应该 — `yield` 的语义在这种场景下无法同时满足两者。


#### 语言方回复（第 13.5 轮）— 承认核心矛盾，重新分析

你的三个反驳都击中了要害，我逐个回应：

##### 1. 承认反驳①②的合理性

你说得对 — 我之前的回答犯了一个错误：**把"持续伤害"推给宿主碰撞系统是在回避问题**。

你的核心论点是：
- buff、被动技能、持续效果，**最终都应该由 VM 实例执行**
- "让宿主层直接处理"违背了脚本化的初衷 — 如果特殊情况总要回退到宿主层，脚本化就不彻底
- 即使 buff 可以选择"硬直时继续 Tick"，buff 内部也可能有"硬直中不执行"的逻辑 — **问题递归出现**

我接受这个论点。**不应该通过"推给宿主层"来回避，而应该在 VM 层面解决这个问题。**

##### 2. 反驳③的深层含义 — 重新定义问题

你的代码示例完美展示了核心矛盾：

```
同一个 step() 函数体内：
  SetSomeFlag()         → 硬直中不该执行（帧驱动逻辑）
  ApplyDamageToNearby() → 硬直中应该执行（持续效果逻辑）
  yield                 → 歧义：这一帧是"逻辑帧"还是"物理帧"？
```

**这个矛盾说明：问题的粒度不在"函数级"，而在"语句级"。**

我之前的 onPaused() 方案是在**函数级**分离（step 冻结，onPaused 不冻结）。但你的例子表明，需要的是在**同一个函数内**，某些语句受硬直影响，某些不受。

##### 3. 重新审视：语句级硬直控制的可能方案

承认了问题的本质后，让我列出几种解决思路：

**方案 X1：标注块（Annotation Block）**

```ffs
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        f = f + 1

        if f == 60 {
            SetSomeFlag()         // 正常代码 — 硬直冻结
        }

        @always {                 // 标注块：硬直期间也执行
            ApplyDamageToNearby()
        }

        yield
    }
}
```

- `@always { ... }` 内的代码在硬直帧也执行
- yield 本身仍然只在逻辑帧递减 WaitCounter
- 硬直帧进入 step() 时，跳过非 `@always` 代码，只执行 `@always` 块
- **需要 VM + 编译器改动**：引入代码段标记，硬直帧执行时 IP 跳转到 @always 块

**方案 X2：双游标（Dual Cursor）**

```ffs
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        f = f + 1

        if f == 60 {
            SetSomeFlag()
        }

        ApplyDamageToNearby()

        yield
    }
}
```

VM 层面引入"逻辑 IP"和"持续 IP"两个指令游标：
- 正常帧：执行全部代码，两个 IP 同步推进
- 硬直帧：只推进"持续 IP"，跳过非持续语句
- **问题**：编译器需要知道哪些语句是"持续"的 — 回到标注问题

**方案 X3：`always` 并行协程**

```ffs
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        f = f + 1
        if f == 60 { SetSomeFlag() }
        yield
    }
}

always func damageAura() {
    // 无论硬直与否，每帧执行
    // 与 step() 共享模块变量
    ApplyDamageToNearby()
}
```

- `always func` 声明为"不受硬直影响的并行协程"
- 宿主每帧（包括硬直帧）都 Tick 这个协程
- 与 step() 共享模块变量 → 可以读取 step 设置的状态
- **本质**：还是分离，但不是 onPaused（仅硬直期间），而是 always（始终执行）
- **区别于 onPaused**：damageAura 有自己的 yield/wait 和帧计数，是完整的协程

**方案 X4：回到 A′-1（双 Tick），但重新设计 yield 语义**

```
宿主调度：
  正常帧 → Tick(logicTick=true)    // 所有代码执行，WaitCounter 递减
  硬直帧 → Tick(logicTick=false)   // 所有代码执行，WaitCounter 不递减
```

```ffs
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        if IsLogicTick() {        // Syscall：当前是否为逻辑帧
            f = f + 1
            if f == 60 { SetSomeFlag() }
        }

        ApplyDamageToNearby()     // 每帧都执行

        yield                     // 逻辑帧：WaitCounter 递减
                                  // 硬直帧：WaitCounter 不递减 → 下一帧仍从这里恢复
    }
}
```

- yield 在硬直帧不递减 WaitCounter → 脚本每个硬直帧都从 yield 恢复并执行到下一个 yield
- 脚本通过 `IsLogicTick()` 自行决定哪些逻辑在硬直帧跳过
- **yield 语义变化**：不再是"等 1 个逻辑帧"，而是"等 1 个 Tick（可能是逻辑帧，也可能是硬直帧）"
- **心智模型变化**：yield = "交还控制权"，不再隐含"帧推进"

##### 4. 方案对比

| 维度 | X1 标注块 | X2 双游标 | X3 always 协程 | X4 双Tick+IsLogicTick |
|------|----------|----------|---------------|---------------------|
| VM 改动 | ⭐⭐⭐ 大 | ⭐⭐⭐ 大 | ⭐⭐ 中 | ⭐ 小 |
| 语言改动 | ⭐⭐⭐ 新语法 | ⭐⭐⭐ 新语法 | ⭐⭐ 新修饰符 | ⭐ 仅 Syscall |
| 同函数内混合逻辑 | ✅ 支持 | ✅ 支持 | ❌ 仍需分离 | ✅ 支持 |
| yield 语义清晰度 | ✅ 不变 | ⚠️ 复杂 | ✅ 不变 | ⚠️ 变化 — 但可理解 |
| 用户心智负担 | ⭐⭐ 中 — 需要标注 | ⭐⭐⭐ 高 | ⭐ 低 — 但仍分离 | ⭐⭐ 中 — 需要 if 包裹 |
| 向后兼容 | ✅ 旧代码不受影响 | ⚠️ 可能影响 | ✅ 向后兼容 | ✅ 向后兼容（默认 logicTick=true） |
| 解决你的核心场景 | ✅ 完全 | ✅ 完全 | ⚠️ 部分 — 仍分离 | ✅ 完全 |

##### 5. 语言方推荐

**推荐方案 X4（双 Tick + IsLogicTick Syscall）**。理由：

1. **VM 改动最小**：只需在 Tick() 增加一个 `bool isLogicTick` 参数，控制 WaitCounter 是否递减
2. **不需要语言改动**：`IsLogicTick()` 只是一个普通 Syscall
3. **解决你的核心场景**：同一个 step() 内，用 `if IsLogicTick()` 包裹帧驱动逻辑
4. **向后兼容**：宿主不传 isLogicTick（默认 true）→ 行为与现在完全一致
5. **用户选择权**：脚本编写者自己决定哪些逻辑在硬直帧执行/不执行

**X4 下你的例子变成：**

```ffs
func step() {
    BeginAction(ACTION_AURA, 60)
    var f: int = 0
    while f < 60 {
        if IsLogicTick() {
            f = f + 1
            if f == 60 { SetSomeFlag() }
        }

        ApplyDamageToNearby()   // 每帧（包括硬直帧）都执行

        yield   // 逻辑帧：等下一帧（WaitCounter--）
                // 硬直帧：不等（WaitCounter 不变）→ 下一硬直帧继续执行
    }
}
```

**X4 下的 buff 例子（解决递归问题）：**

```ffs
// 毒雾 buff — 持续伤害 + 硬直中不应增加层数
func step() {
    var stacks: int = 1
    while true {
        if IsLogicTick() {
            stacks = stacks + 1       // 只在逻辑帧增长层数
            if stacks > 5 { return }  // 超过5层结束
        }

        ApplyPoison(stacks)           // 每帧（包括硬直帧）都施毒

        yield
    }
}
```

##### 6. X4 的 VM 改动细节预估

```csharp
// VMWorld.cs — 改动极小

// 新增 API
public void TickPaused()  // = Tick() 但 isLogicTick=false
{
    TickInternal(isLogicTick: false);
}

// 内部改动
private void TickInternal(bool isLogicTick = true)
{
    // ... 现有逻辑 ...
    if (inst.WaitCounter > 0 && !killed)
    {
        if (isLogicTick)
            inst.WaitCounter--;  // 逻辑帧：递减
        // 硬直帧：不递减 → 下一 Tick 仍会进入执行
        
        if (inst.WaitCounter > 0)
            continue;            // 还在等 → 跳过
        // WaitCounter 刚到0 → fall through 执行
    }
    // ... ExecuteInstance ...
}
```

等等 — 这里有一个微妙之处需要澄清：

**硬直帧不递减 WaitCounter 时，脚本还会不会执行？**

当前逻辑：`WaitCounter > 0 → WaitCounter-- → continue`（跳过执行）。

X4 中硬直帧：`WaitCounter > 0 → 不递减 → ???`

两种选择：
- **X4a**：硬直帧 WaitCounter > 0 → 不递减，但**仍执行脚本**（从当前 IP 恢复执行到下一个 yield）
- **X4b**：硬直帧 WaitCounter > 0 → 不递减，**不执行**（保持冻结）

你的场景需要 **X4a** — 硬直帧仍然执行脚本，只是 yield 时不递减计数器。这样每个硬直帧，脚本都会"醒来"执行一轮（从上次 yield 到下一次 yield），执行 ApplyDamageToNearby()，然后再次 yield。但 f 不增加（因为被 IsLogicTick() 保护），所以循环不推进。

```csharp
// X4a 精确语义
if (inst.WaitCounter > 0 && !killed)
{
    if (isLogicTick)
    {
        inst.WaitCounter--;
        if (inst.WaitCounter > 0)
            continue;       // 逻辑帧：还在等 → 跳过
        // WaitCounter 刚减到 0 → fall through 执行
    }
    else
    {
        // 硬直帧：不递减，但仍然执行脚本
        // fall through → ExecuteInstance
    }
}
```

这样 yield 在逻辑帧和硬直帧的行为是：

| | 逻辑帧 | 硬直帧 |
|--|--------|--------|
| yield（WaitCounter=1）后 | WaitCounter-- → 0 → 恢复执行 | WaitCounter 不变（仍=1）→ 仍恢复执行 |
| wait(5)（WaitCounter=5）后 | 每帧 WaitCounter-- → 5帧后恢复 | 每硬直帧：WaitCounter 不变 → 每帧都执行 |

**注意**：`wait(5)` 在硬直帧的行为可能不是你想要的（每硬直帧都执行，而不是等 5 帧）。如果需要硬直帧也能等待，需要引入 `wait_real(N)`（物理帧等待）或额外 Syscall。但对于 yield（= wait 1）这是自然的。

##### 7. 综合结论

| 论点 | 回应 |
|------|------|
| 反驳① buff 也交给 VM 执行 | ✅ 接受 — 不应把问题推给宿主层 |
| 反驳② 碰撞直接执行不健康 | ✅ 接受 — 脚本化应该彻底 |
| 反驳③ 同函数内混合硬直逻辑 | ✅ 核心矛盾 — 问题粒度在语句级 |
| 之前的 onPaused 方案 | ⚠️ 不够 — 只解决函数级分离 |
| 推荐方案 | **X4（双 Tick + IsLogicTick）** — VM 改动最小，语言无改动，解决语句级混合 |

#### Q2 第 13.5 轮待用户确认

1. **X4 方案是否匹配你的直觉？** — 同一个 step() 内，用 `if IsLogicTick()` 保护帧驱动逻辑，其余代码硬直帧也执行。
2. **yield 在硬直帧的行为**：X4a（硬直帧 yield 不递减但仍执行）是否符合预期？
3. **wait(N) 的硬直帧行为**：是否需要"硬直帧也能等待 N 帧"的能力？还是只需要 yield（wait 1）在硬直帧正常工作？
4. **是否有更好的心智模型？** — 你在原始反馈中提到"用户完全可以理解成这就是虚拟机 tick"。X4 下 yield 确实变成了"等 1 个 Tick"（不区分逻辑帧/硬直帧），这是否更自然？

</details>

---

## Q3: 跨脚本 VM 使用模式 ✅

**结论**：6 方向渐进路径；阶段 1（方向 1+2+3）✅ + 阶段 3（方向 5 服务脚本）✅ 已完成。

<details>
<summary>📋 详细设计</summary>

| # | 方向 | 数据共享机制 | VM 改动 | 状态 |
|---|------|-----------|--------|------|
| 0 | **宿主直调** | 宿主持有 instanceId，直接 API 操作 | 无 | ✅ 基础 |
| 1 | **黑板中介** | 宿主 key-value Syscall | 无 | ✅ Lang-3 |
| 2 | **Include 共享模板** | 编译期代码复用（数据独立） | 无 | ✅ Lang-2 |
| 3 | **宿主编排** | 宿主 C# 协调 + Syscall | 无 | ✅ SK2 裁决 |
| 4 | **共享变量区** | VM 跨实例共享内存 | ⭐⭐ | ⏳ 需求驱动 |
| 5 | **服务脚本 + 跨模块调用** | VM 跨模块函数调用 | ⭐⭐⭐ | ✅ Lang-6/7/8 |

**渐进路径**：

| 阶段 | 内容 | 状态 |
|------|------|------|
| **阶段 1** | 方向 1 + 2 + 3 — 黑板 + include + 宿主编排 | ✅ 已完成（Lang-1/2/3） |
| **阶段 2（需求驱动）** | + 方向 4 — 共享变量区 | ⏳ 黑板 Syscall 频率成为瓶颈时触发 |
| **阶段 3** | + 方向 5 — 服务脚本 | ✅ C-1~C-2 已实现（Lang-6 XCALL + Lang-7 自动退化 + Lang-8 统一语法）。A5 深度内联远期（`@force_inline` 已取消） |

**关键确认**：

| 确认项 | 结论 |
|--------|------|
| 模块变量 = 实例级共享数据 | ✅ 类似 C# 实例字段。`var charge` 分配在 r56，checkEnter 和 step 读写同一个 r56 |
| 实例重定向 | ✅ 修改 3 个字段（IP, StateFlags, CallStackDepth）即可让同一实例换函数执行，寄存器保留 |
| 方向 0（宿主直调） | ✅ SpawnInstance → TickInstance → 读寄存器 → DestroyInstance，是所有方向的基础 |

</details>

<details>
<summary>📋 讨论历史（第 12~12.5 轮）</summary>

#### 第 12 轮 — 5 种方向详解

#### Q3 第 12 轮详解: 跨脚本 VM 使用模式推荐

> 用户原话：想要继续讨论虚拟机跨脚本使用的方式, 先帮我推荐几种不同方向的使用方法。

#### 背景回顾

当前语言进度：L1（模块变量）✅、L2（include）✅、L3（黑板 Syscall）✅ 已完成。L4（跨模块共享变量）和 L5（跨模块函数调用）待定。

跨脚本使用的根本需求是：**多个独立编译的 FFS 脚本实例在运行时共享数据或协调行为**。

以下从 5 个不同方向推荐，按机制复杂度从低到高排列：

---

#### 方向 1: 黑板中介（Blackboard Mediator）

**思路**：所有跨脚本通信通过宿主黑板 Syscall 中转。脚本之间互不知道对方的存在，只通过约定的 Key 读写共享状态。

```
┌──────────────┐    SetBB(key,val)    ┌──────────────┐
│ skill_LP.ffs │ ──────────────────→ │   宿主黑板    │
└──────────────┘                      │ (key-value)  │
                                      └──────┬───────┘
┌──────────────┐    GetBB(key)               │
│ skill_HP.ffs │ ←───────────────────────────┘
└──────────────┘
```

**使用模式**：
```ffs
// skill_light_punch.ffs
func step() {
    // ... 攻击帧逻辑 ...
    var hit: int = CheckAttackHit(1001)
    if hit > 0 {
        SetBlackboard(BB_LP_HIT, 1)        // 通知"轻拳命中了"
        SetBlackboard(BB_LP_HIT_FRAME, GetFrame())
    }
    // ...
}

// skill_heavy_punch.ffs
func checkEnter(): int {
    var lpHit: int = GetBlackboard(BB_LP_HIT)
    var lpFrame: int = GetBlackboard(BB_LP_HIT_FRAME)
    if lpHit > 0 && GetFrame() - lpFrame < 15 {
        return 1  // 轻拳命中后 15 帧内可衔接重拳
    }
    return 0
}
```

| 优点 | 缺点 |
|------|------|
| ✅ **已可用** — Lang-3 黑板 Syscall 已实现 | ❌ Key 命名需约定，缺少编译期安全 |
| ✅ 零 VM 运行时改动 | ❌ 逻辑分散在多个脚本中 |
| ✅ 简单、可预测、可调试 | ❌ 每次读写有 Syscall 开销 |
| ✅ 天然支持跨角色（不同角色读同一 Key） | ❌ 缺乏结构化（纯 key-value 平铺） |

**适用场景**：连招衔接通知、全局状态标志（round_start、time_remaining）、简单的跨脚本条件判断。

**当前推荐度**：⭐⭐⭐⭐⭐ — 已实现，覆盖 KOF98 当前全部跨脚本需求。

---

#### 方向 2: Include 共享模板（Compile-time Sharing）

**思路**：多个脚本 include 同一份 `.ffs` 文件，共享 const、struct、辅助函数的**定义**。运行时各脚本实例拥有独立副本。

```
┌──────────────────┐
│ shared/combo.ffs │ ← const + func 定义
└────────┬─────────┘
         │ include (编译期展开)
    ┌────┴────┐
    ↓         ↓
┌─────────┐ ┌─────────┐
│ LP.ffs  │ │ HP.ffs  │  ← 各自拥有独立副本
└─────────┘ └─────────┘
```

**使用模式**：
```ffs
// shared/combo_chain.ffs
const CHAIN_LP_TO_HP: int = 1
const CHAIN_LP_TO_LK: int = 2
const CHAIN_WINDOW: int = 15

func canChain(fromSkill: int, toSkill: int, elapsed: int): int {
    if elapsed > CHAIN_WINDOW { return 0 }
    if fromSkill == SKILL_LP && toSkill == SKILL_HP { return 1 }
    if fromSkill == SKILL_LP && toSkill == SKILL_LK { return 1 }
    return 0
}

// skill_heavy_punch.ffs
include "shared/combo_chain"
func checkEnter(): int {
    var from: int = GetBlackboard(BB_LAST_SKILL)
    var elapsed: int = GetFrame() - GetBlackboard(BB_LAST_HIT_FRAME)
    return canChain(from, SKILL_HP, elapsed)
}
```

| 优点 | 缺点 |
|------|------|
| ✅ **已可用** — Lang-2 include 已实现 | ❌ 运行时各实例数据独立，不能共享状态 |
| ✅ 编译期安全 — const/func 编译检查 | ❌ 修改共享文件需重新编译所有依赖脚本 |
| ✅ 代码复用减少维护成本 | ❌ 共享 var 是各实例的副本（非同一份数据） |
| ✅ 零运行时开销 | |

**适用场景**：连招关系表、技能属性常量、通用辅助函数、伤害计算公式。

**与方向 1 配合**：方向 2 解决"代码复用"，方向 1 解决"数据共享"。两者正交互补，推荐联合使用。

**当前推荐度**：⭐⭐⭐⭐⭐ — 已实现，与方向 1 配合使用为最佳实践。

---

#### 方向 3: 宿主编排（Host Orchestration）

**思路**：跨脚本协调逻辑不在 FFS 脚本内，而在宿主 C# 代码中。宿主作为"导演"读取各脚本输出（通过 Syscall 推送的 Component 数据），执行协调逻辑，然后通过 Spawn/Kill/Syscall 控制各脚本。

```
┌──────────────────────────────────────────────┐
│ 宿主 C# (SkillOrchestrator / CombatSystem)   │
│                                              │
│  1. 读取各 Component (Stance, HitResult, …)  │
│  2. 执行裁决/连招/状态机逻辑                   │
│  3. Spawn/Kill 脚本实例                       │
│  4. 通过黑板 Key 传入配置                      │
└───────┬──────────────┬──────────────┬────────┘
        ↓              ↓              ↓
   ┌─────────┐    ┌─────────┐    ┌─────────┐
   │ LP.ffs  │    │ HP.ffs  │    │ Hit.ffs │
   └─────────┘    └─────────┘    └─────────┘
   (只关心自己     (只关心自己     (只关心自己
    的帧执行)       的帧执行)       的帧执行)
```

**使用模式**：
```csharp
// 宿主 C# — CombatSystem.OnTick()
if (character.LastHitResult.Hit && character.CurrentSkill == SkillId.LightPunch) {
    // 轻拳命中 → 设置黑板允许重拳衔接
    vm.SetBlackboard(character.EntityId, BB_COMBO_ALLOW_HP, 1);
    vm.SetBlackboard(character.EntityId, BB_COMBO_WINDOW_END, frameNumber + 15);
}

// 裁决层 — 已有 SK2 分层候选池
var candidates = GetCandidateSkills(character.Stance);
foreach (var skill in candidates) {
    // checkEnter 只需检查黑板中宿主已设好的标志
    if (ProbeCheckEnter(character, skill)) {
        ActivateSkill(character, skill);
        break;
    }
}
```

| 优点 | 缺点 |
|------|------|
| ✅ 脚本保持简单 — 每个脚本只管自己的帧逻辑 | ❌ 协调逻辑在 C# 中，不享受脚本热更新 |
| ✅ 宿主有全局视角 — 天然适合裁决、连招判定 | ❌ 脚本自由度低 — 不能自主决定跨脚本行为 |
| ✅ 零 VM 改动 | ❌ 与"尽量多的逻辑放脚本"的长远目标矛盾 |
| ✅ 调试直观 — 全在 C# 断点可见 | |

**适用场景**：裁决层（SK2）、全局战斗事件（Round Start/End）、需要全局视角的复杂判定。

**定位**：这不是"跨脚本通信"的替代方案，而是一种**分工策略** — 把"谁和谁通信"的决定权留在宿主。与方向 1/2 不冲突，而是正交的补充。

**当前推荐度**：⭐⭐⭐⭐ — 裁决层已按此模式设计（SK2），不需要额外实现。

---

#### 方向 4: 共享变量区（Shared Variable Zone）— 对应 Lang-4 跨模块共享变量

**思路**：VM 运行时提供一块**跨实例共享的变量区**，多个脚本实例可以直接读写同一份数据，无需经过 Syscall。

```
┌─────────┐   LOAD_SHARED r, idx   ┌───────────────────┐
│ LP.ffs  │ ←─────────────────────→ │  共享变量区        │
└─────────┘   STORE_SHARED idx, r   │  (每角色一块 or   │
                                    │   全局一块)        │
┌─────────┐   LOAD_SHARED r, idx   │                   │
│ HP.ffs  │ ←─────────────────────→ │  slot[0]: LP_hit  │
└─────────┘                         │  slot[1]: combo_n │
                                    │  slot[2]: ...     │
                                    └───────────────────┘
```

**使用模式（假想语法）**：
```ffs
// 编译器层面
shared var lp_hit: int = 0        // 声明共享变量
shared var combo_count: int = 0

// skill_light_punch.ffs
func step() {
    // ...
    if CheckAttackHit(1001) > 0 {
        lp_hit = 1          // 直接写共享区 — 无 Syscall 开销
        combo_count = combo_count + 1
    }
    // ...
}

// skill_heavy_punch.ffs
func checkEnter(): int {
    if lp_hit > 0 { return 1 }  // 直接读共享区
    return 0
}
```

| 优点 | 缺点 |
|------|------|
| ✅ 极低开销 — 直接寄存器/内存读写，无 Syscall | ⚠️ 需要 VM 运行时改动（新增共享区域 + 专用 OpCode） |
| ✅ 脚本内闭环 — 数据不经过宿主 | ⚠️ 共享粒度设计复杂 — 角色级 vs 全局？ |
| ✅ 编译期安全 — 编译器分配 slot | ⚠️ 确定性/回滚需额外处理共享区快照 |
| ✅ 与黑板功能重叠 — 可统一为一个机制 | ⚠️ 多实例并发写入的冲突问题 |

**关键设计问题**：
1. **共享粒度**：每个角色一块（角色级共享）vs 全局一块（跨角色共享）vs 可配置？
2. **与黑板关系**：共享变量区是否取代黑板 Syscall？还是并存？
3. **回滚支持**：共享区需要纳入 SnapshotRingBuffer 吗？
4. **编译期绑定**：多个脚本如何声明同一个共享变量？`include shared_vars.ffs` + `shared var` 语法？

**适用场景**：高频跨脚本数据传递（每帧多次读写）、连招状态共享、角色级全局计数器。

**当前推荐度**：⭐⭐⭐ — 目前黑板 Syscall 已覆盖需求。当黑板 Syscall 的频率成为性能瓶颈时再引入。

---

#### 方向 5: 服务脚本 + 跨模块调用（Service Script + Cross-Module Call）— 对应 Lang-5 跨模块函数调用

**思路**：某些脚本不绑定到技能帧逻辑，而是作为"服务"常驻运行，其他脚本通过跨模块函数调用来查询/请求服务。

```
┌──────────────────────────────┐
│ combo_manager.ffs (服务脚本)  │ ← 常驻实例，管理连招状态
│                              │
│  func queryCanChain(from, to)│ ← 被其他脚本调用
│  func notifyHit(skillId)     │
│  func getComboCount(): int   │
└──────────┬───────────────────┘
           │ CALL_MODULE
     ┌─────┴──────┐
     ↓            ↓
┌─────────┐  ┌─────────┐
│ LP.ffs  │  │ HP.ffs  │  ← 技能脚本调用服务脚本的函数
└─────────┘  └─────────┘
```

**使用模式（假想语法）**：
```ffs
// combo_manager.ffs — 服务脚本
var combo_count: int = 0
var last_hit_skill: int = 0
var last_hit_frame: int = 0

func notifyHit(skillId: int) {
    last_hit_skill = skillId
    last_hit_frame = GetFrame()
    combo_count = combo_count + 1
}

func queryCanChain(toSkill: int): int {
    if GetFrame() - last_hit_frame > 15 { return 0 }
    // 连招表查询...
    return 1
}

func step() {
    // 常驻循环 — 可选，或纯被动（只通过跨模块调用触发）
    while 1 { yield }
}

// skill_light_punch.ffs
import "combo_manager" as combo  // 假想 import 语法

func step() {
    // ...
    if CheckAttackHit(1001) > 0 {
        combo.notifyHit(SKILL_LP)  // 跨模块调用
    }
    // ...
}

// skill_heavy_punch.ffs
import "combo_manager" as combo

func checkEnter(): int {
    return combo.queryCanChain(SKILL_HP)  // 跨模块调用
}
```

| 优点 | 缺点 |
|------|------|
| ✅ 逻辑最集中 — 连招管理器是唯一权威源 | ⚠️ VM 改动最大 — ModuleTable + 跨模块 CALL 协议 |
| ✅ 脚本完全自治 — 宿主无需参与协调 | ⚠️ 复杂度最高 — 跨模块调用栈、寄存器切换 |
| ✅ 真正的"连招描述脚本"理想形（SK10 理想形 1） | ⚠️ 调度复杂 — 服务脚本的生命周期管理 |
| ✅ 可扩展到 AI 脚本、全局事件脚本等 | ⚠️ 确定性 — 跨模块调用顺序需要确定性保证 |

**关键设计问题**：
1. **调用协议**：同步调用（调用方挂起等待返回）vs 消息队列（异步）？
2. **寄存器切换**：跨模块调用时是否切换到目标实例的寄存器窗口？
3. **生命周期**：服务脚本由谁 Spawn？谁 Kill？绑定角色还是全局？
4. **步数预算**：跨模块调用消耗调用方的 MaxStepsPerTick 还是目标方的？
5. **确定性**：帧同步场景下跨模块调用顺序如何保证确定性？

**适用场景**：集中式连招管理器、AI 决策脚本查询角色状态、全局事件广播。

**当前推荐度**：⭐⭐ — 目前无刚性需求，但这是 FFS 脚本生态的终极形态。当项目规模扩大到需要"脚本自治"时是必要的。

---

#### 五种方向总览

| # | 方向 | 数据共享机制 | VM 改动 | 当前可用 | 推荐度 |
|---|------|-----------|--------|---------|--------|
| 1 | 黑板中介 | 宿主 key-value Syscall | 无 | ✅ Lang-3 | ⭐⭐⭐⭐⭐ |
| 2 | Include 共享模板 | 编译期代码复用（数据独立） | 无 | ✅ Lang-2 | ⭐⭐⭐⭐⭐ |
| 3 | 宿主编排 | 宿主 C# 协调 + Syscall | 无 | ✅ SK2 裁决 | ⭐⭐⭐⭐ |
| 4 | 共享变量区 | VM 跨实例共享内存 | ⭐⭐ | ❌ Lang-4 | ⭐⭐⭐ |
| 5 | 服务脚本 + 跨模块调用 | VM 跨模块函数调用 | ⭐⭐⭐ | ❌ Lang-5 | ⭐⭐ |

**推荐的渐进路径**：

```
阶段 1（现在）: 方向 1 + 方向 2 + 方向 3
  → 黑板 + include + 宿主编排联合使用
  → 覆盖 KOF98 全部已知需求

阶段 2（需求驱动）: + 方向 4
  → 当黑板 Syscall 频率成为性能瓶颈
  → 或者脚本间高频数据传递需求出现

阶段 3（远期）: + 方向 5
  → 当需要"脚本自治生态"（多脚本自主协调，宿主极少介入）
  → 或者项目扩展到 NvN、AI 脚本等复杂场景
```

#### 待用户确认

1. **阶段 1 是否满足当前需求？** — 方向 1+2+3 是否覆盖你目前想到的所有跨脚本场景？
2. **有没有具体的跨脚本场景**是上述 5 种方向都不能好好覆盖的？
3. **对方向 4 和方向 5，是否有倾向性？** — 先推进哪个，还是都按需触发？
4. **方向 3（宿主编排）的定位** — 你倾向于尽量多的逻辑放脚本内（减少宿主 C# 协调），还是接受宿主作为"导演"角色？


---

#### 第 12.5 轮 — 补充方向 0、模块变量确认、实例重定向

> **用户第 12.5 轮反馈（Q3）**：
> - 这 5 种方向没有覆盖我最初的直觉方式：**从宿主层获取虚拟机实例，调用虚拟机实例的方法**。
> - 另外需要确认：一个脚本（代表虚拟机）的实例变量或常量，确实如 C# 脚本语言那样是多个函数的上层 scope 同一个数据（实例）。因为看到过"实例重定向"之类的东西，不太能把这个东西和这里关联起来。
> - 另外实例重定向看起来是一个重要的实用功能，请介绍运行机制和实用示范。


#### 语言方回复（第 12.5 轮）

##### 回答 A: "宿主获取 VM 实例，调用方法" — 这正是 FFVM 的核心使用模式

你说的"直觉方式"其实已经是 FFVM 的基本 API，只是第 12 轮的 5 个方向聚焦于"跨脚本"协调而忽略了最基础的使用模式。让我补充说明：

**FFVM 的基本使用模式**：

```csharp
// 宿主 C# 代码

// 1. 编译脚本 → 获得 VMProgram (ROM)
VMProgram program = BytecodeCompiler.Compile(sourceCode, "step", syscalls, syscallTable);

// 2. 加载到 VMWorld 的模块槽
int moduleSlot = 0;
world.Modules.Set(moduleSlot, program);

// 3. 创建实例 — 获得实例 ID
int instanceId = world.SpawnInstance(moduleSlot, program.EntryIP);

// 4. 每帧 Tick — 驱动这个实例执行
world.TickInstance(instanceId);    // 单独 tick 这一个实例
// 或
world.Tick();                      // tick 所有活跃实例

// 5. 读取实例状态
ref VMInstanceState inst = ref world.Pool.Instances[instanceId];
bool isFinished = (inst.StateFlags & VMStateFlags.Completed) != 0;
int returnValue = inst.Registers.Get(0).ToInt();  // r0 = 返回值

// 6. 调用实例的另一个函数（通过 Redirect）
if (program.TryGetFunction("step", out var stepFunc)) {
    inst.IP = stepFunc.EntryIP;
    inst.StateFlags &= ~VMStateFlags.Completed;
    // 现在 TickInstance(instanceId) 会从 step() 开始执行
}

// 7. 销毁
world.DestroyInstance(instanceId);
```

这就是你说的 **"从宿主层获取虚拟机实例，调用虚拟机实例的方法"**。

与 C# 脚本的类比：

| C# 脚本概念 | FFVM 对应 |
|------------|----------|
| `var obj = new MyScript()` | `int id = world.SpawnInstance(moduleSlot, entryIP)` |
| `obj.checkEnter()` | `world.TickInstance(id)` (当 IP 指向 checkEnter) |
| `obj.step()` | redirect IP 到 step → `world.TickInstance(id)` |
| `obj.myField` | `world.Pool.Instances[id].Registers.Get(r56)` (模块变量) |
| `Destroy(obj)` | `world.DestroyInstance(id)` |

**为什么第 12 轮没列这个方向**：因为这不是"跨脚本"模式 — 这是**单脚本**的基本使用方式。第 12 轮的 5 个方向专注于"多个脚本之间如何通信"。但你的直觉方式（宿主获取实例 → 调用方法 → 读取结果）正是所有方向的基础。

**补充为方向 0**：

| # | 方向 | 描述 | 当前可用 |
|---|------|------|---------|
| **0** | **宿主直调（Host Direct Call）** | 宿主 C# 持有 instanceId，直接 SpawnInstance → TickInstance → 读寄存器 → DestroyInstance | ✅ 已可用 |

这是最基本的模式，其他 5 个方向都建立在它之上。

---

##### 回答 B: 模块变量确实是多个函数共享的"实例级数据" — ✅ 确认

你的理解是正确的。让我用具体例子确认：

```ffs
// skill_light_punch.ffs

var charge: int = 0        // 模块变量 — 分配在 r56
var hitCount: int = 0      // 模块变量 — 分配在 r57
const MAX_CHARGE: int = 30 // 编译期常量 — 不占寄存器

func checkEnter(): int {
    // 可以读写 charge 和 hitCount
    if charge > 10 {
        return 1
    }
    return 0
}

func step() {
    // 同一个 charge、同一个 hitCount — 与 checkEnter 共享
    charge = charge + 1
    if charge > MAX_CHARGE {
        hitCount = hitCount + 1
        charge = 0
    }
    yield
}
```

**物理层面的解释**：

```
VMInstanceState (一个实例的全部状态)
├── IP = 当前执行位置
├── Registers[0..63]          ← 64 个寄存器槽
│   ├── r0~r15   — scratch zone (绝对寻址, 函数返回值等)
│   ├── r16~r47  — local zone (窗口化, 每个函数独立)
│   ├── r48~r55  — temp zone (编译器临时变量)
│   └── r56~r63  — module var zone (模块变量 ← 这就是"实例级数据")
│       ├── r56 = charge     ← checkEnter 和 step 都读写同一个 r56
│       └── r57 = hitCount   ← checkEnter 和 step 都读写同一个 r57
├── CallStack[...]
└── CleanupStack[...]
```

关键点：

| 维度 | 说明 |
|------|------|
| **模块变量在哪** | `r56~r63`（ModuleVarRegBase=56，共 8 个槽；超过 8 个会溢出到扩展寄存器 ExtendedRegs） |
| **多个函数共享吗** | ✅ 是 — 模块变量使用 LOAD_MVAR/STORE_MVAR 指令**绝对寻址**，不受函数调用的寄存器窗口(RegisterBase)影响 |
| **与 C# 实例字段类比** | `var charge: int` ≈ C# 的 `private int charge;`。同一个实例的所有函数都读写同一个 `charge` |
| **局部变量呢** | 局部变量在 `r16~r47`（local zone），每个函数独立分配。不同函数的 `var a` 可能编译到同一个 r16，但语义上互不影响（函数调用时寄存器窗口会偏移 RegisterBase） |
| **初始化时机** | SpawnInstance 时编译器生成的入口函数 preamble 会执行模块变量初始化代码（`EmitModuleVarInit`） |

**一句话确认**：模块变量（`var`/`const` 在脚本顶层声明）= 实例级共享数据，所有函数读写同一份。这与 C# 的实例字段行为一致。

---

##### 回答 C: 实例重定向（Instance Redirect）运行机制与实用示范

**"实例重定向"是什么**：在不销毁/重建实例的情况下，将一个已完成（或运行中）的实例的执行位置（IP）跳转到另一个函数入口，同时保留实例的寄存器状态。

**为什么需要它**：同一个技能的 `checkEnter()` 和 `step()` 是同一实例的两个阶段。检查条件时从 `checkEnter` 开始执行；条件通过后不需要销毁重建，只需把 IP 指向 `step` 继续执行即可。

**运行机制（逐步）**：

```
阶段 1: 条件检查
─────────────────
宿主: id = SpawnInstance(moduleSlot, checkEnter.EntryIP)
       → 实例创建, IP = checkEnter 入口
       
宿主: TickInstance(id)
       → ExecuteInstance 从 checkEnter 开始执行
       → checkEnter() 内: 读模块变量、做条件判断…
       → return 1  (条件通过)
       → inst.StateFlags |= Completed
       
宿主: 读 inst.Registers[0] → 返回值 = 1 → 条件通过!

               ┌─────────────────────────────────┐
        此时:  │ IP = checkEnter 末尾 (Completed)  │
               │ r56 (charge) = 某个值             │  ← 寄存器状态保留
               │ r57 (hitCount) = 某个值           │
               └─────────────────────────────────┘


阶段 2: 重定向到 step
─────────────────────
宿主: // 实例重定向 — 核心 3 行
      inst.IP = stepFunc.EntryIP;              // 跳到 step 入口
      inst.StateFlags &= ~VMStateFlags.Completed;  // 清除完成标记
      inst.CallStackDepth = 0;                 // 重置调用栈

               ┌─────────────────────────────────┐
        此时:  │ IP = step 入口 (Active)          │
               │ r56 (charge) = 保留的值           │  ← 没有被清零!
               │ r57 (hitCount) = 保留的值         │
               └─────────────────────────────────┘

宿主: TickInstance(id)  或  Tick() 自动推进
       → step() 开始执行
       → step() 可以读到 checkEnter 阶段写入的模块变量值
       → yield → 下一帧继续 → … → return → 技能结束 → Completed

阶段 3: 销毁
─────────────
宿主: DestroyInstance(id)
```

**实用示范：完整的技能生命周期**

```csharp
// ========== 宿主 C# 代码 ==========
// 第 1 步: 编译脚本
var program = BytecodeCompiler.Compile(skillSource, "checkEnter", syscalls, table);
world.Modules.Set(slot, program);

// 第 2 步: 裁决层 — 检查条件
program.TryGetFunction("checkEnter", out var checkEntry);
int id = world.SpawnInstance(slot, checkEntry.EntryIP);
world.TickInstance(id);  // 执行 checkEnter

ref var inst = ref world.Pool.Instances[id];
if ((inst.StateFlags & VMStateFlags.Completed) != 0 && inst.Registers.Get(0).IsNonZero)
{
    // 条件通过 → 重定向到 step
    program.TryGetFunction("step", out var stepEntry);
    inst.IP = stepEntry.EntryIP;
    inst.StateFlags &= ~VMStateFlags.Completed;
    inst.CallStackDepth = 0;
    
    // 实例保持活跃，后续每帧由 Tick() 驱动 step()
    activeSkills[charId] = id;
}
else
{
    // 条件不通过 → 销毁
    world.DestroyInstance(id);
}

// 第 3 步: 每帧主循环
world.Tick();  // 所有活跃实例自动执行（含 step 中的 yield 恢复）

// 第 4 步: 检测技能结束
ref var skill = ref world.Pool.Instances[activeSkills[charId]];
if ((skill.StateFlags & VMStateFlags.Completed) != 0)
{
    world.DestroyInstance(activeSkills[charId]);
    activeSkills.Remove(charId);
}
```

```ffs
// ========== FFS 脚本 ==========
// skill_light_punch.ffs

var combo_window: int = 0      // 模块变量 r56 — checkEnter 和 step 共享

func checkEnter(): int {
    // 通过黑板查询前置条件
    var canCombo: int = GetBlackboard(BB_LP_ALLOWED)
    if canCombo > 0 {
        combo_window = 15       // 写模块变量 → step 阶段可以读到
        return 1
    }
    return 0
}

func step() {
    // combo_window 已经是 checkEnter 设置的 15！
    BeginAction(ACTION_LIGHT_PUNCH, 10)
    defer { EndAction() }
    
    var f: int = 0
    while f < 10 {
        // 攻击帧逻辑
        if f >= 3 && f < 7 {
            var hit: int = CheckAttackHit(HITBOX_LP)
            if hit > 0 {
                ApplyDamage(hit, 50)
                SetBlackboard(BB_LP_HIT, 1)
            }
        }
        
        // 连招窗口递减
        if combo_window > 0 {
            combo_window = combo_window - 1
        }
        
        f = f + 1
        yield
    }
}
```

**为什么不销毁+重建**：

| 方式 | 操作 | 寄存器状态 | 开销 |
|------|------|-----------|------|
| **重定向（推荐）** | 修改 3 个字段 (IP, StateFlags, CallStackDepth) | ✅ 保留 — checkEnter 写的值 step 直接读 | 几乎为零 |
| **销毁+重建** | DestroyInstance + SpawnInstance | ❌ 丢失 — 新实例寄存器全部清零 | 需要重新初始化模块变量 |

**一句话总结**：实例重定向 = "让同一个实例换一个函数继续执行，但记忆（寄存器/模块变量）保留"。这是同一实例多阶段生命周期（checkEnter → step）的核心机制。

---

#### 第 12.5 轮待用户确认

1. **Q1 已关闭** ✅
2. **Q2**：你的模型（yield/wait 自然跟随宿主时间轴、宿主不 Tick 则冻结）已确认正确。是否还需要硬直期间脚本执行的能力（A′-1/A′-2/A′-3）？还是当前模型已足够？
3. **Q3 方向 0（宿主直调）**：已补充。是否覆盖了你的直觉方式？
4. **Q3 模块变量确认**：已确认模块变量 = 实例级共享数据（类似 C# 实例字段）。清楚了吗？
5. **Q3 实例重定向**：已介绍运行机制和完整示范。清楚了吗？有其他想了解的点吗？

</details>
---

## Q4: FFS 封装 — 服务脚本 ✅

**结论**：方式 C（语言级引用）✅；Y1-Plus ✅；svc.member 统一语法 ✅；@export ✅；@inline ✅；14 项决策锁定。C-1~C-2 已实现（Lang-6/7/8 ✅）。`@force_inline` 已取消，A5 深度内联远期。

<details>
<summary>📋 详细设计</summary>

> **第 18~26 轮。✅ 收敛（第 26 轮）。** 14 项设计决策全部锁定。**C-1（Lang-6）✅ / C-1.5（Lang-7）✅ / C-2（Lang-8）✅ 已实现**（1259 tests pass）。`@force_inline` 已取消，A5 深度内联（Lang-9）为远期。详见 [VM_Summary Lang-6~Lang-8](../../Docs/VM_Summary.md)。

**14 项核心决策**：

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 ✅ |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 ✅ |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | `svc.member` 统一语法，编译器自动路由 L4/L5 | R23-24 ✅ |
| L4/L5 关系 | 同基线设计：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | **`@export` 唯一形式** | R24-25 ✅ |
| 自动优化 | A1/A2 自动 getter/setter→直接访问退化（C-1.5） | R23 ✅ |
| 用户引导内联 | `@inline`（hint，C-2）+ LSP 诊断。`@force_inline` 已取消 — 内联失败严格程度改由编译器配置控制 | R24 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(A1/A2+配置) → C-2(语法糖+@inline+LSP) → 远期(A5 深度内联) | R23-26 ✅ |
| 嵌套调用 | **运行时配置 MaxXCallDepth（默认4）+ Warn/Unlimited 两种策略** | R21→R26 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算；深度检查 +1 ns/XCALL；常量 vs 变量无差异） | R20-26 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7+@inline（C-2），O4/A5 深度内联（远期） | R22-24 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

**OpCode 基线设计（C-1 同时实现）**：

| OpCode | 操作 | 编码 | 开销 |
|--------|------|------|------|
| **XCALL** | 跨实例函数调用 | A=dest, B=instanceId_reg, C=funcIndex | ~15 ns |
| **XLOAD_MVAR** | 跨实例变量读取 | A=dest, B=instanceId_reg, C=mvarIndex | ~3-5 ns |
| **XSTORE_MVAR** | 跨实例变量写入 | A=src, B=instanceId_reg, C=mvarIndex | ~3-5 ns |

**嵌套深度配置（C-1.5）**：

```csharp
public class VMConfig {
    public int MaxXCallDepth = 4;      // 0 = 不限制
    public bool XCallDepthWarning = true; // true = Warn, false = Unlimited
}
```

**实现路径**：

| 阶段 | 内容 | 说明 | 状态 |
|------|------|------|------|
| C-0 | 零改动原型验证 | 宿主 C# 桥接模拟跨实例调用 | ⏭️ 跳过（直接 C-1） |
| C-1 | XCALL + XLOAD_MVAR + XSTORE_MVAR | 三个 OpCode + @export + Y1-Plus 编译期 yield 禁止 | ✅ Lang-6 |
| C-1.5 | A1/A2 自动退化 + MaxXCallDepth 配置 | getter/setter 自动优化为直接变量访问 | ✅ Lang-7 |
| C-2 | `svc.member` 统一语法 + @inline + LSP 诊断 | 语法糖 + 编译期内联 hint + 调用链深度诊断 | ✅ Lang-8 |
| 远期 | A5 深度内联（`@force_inline` 已取消） | 函数体展开优化，内联失败严格程度由编译器配置控制 | ⏳ 远期（Lang-9） |

</details>

<details>
<summary>📋 讨论历史（第 18~26 轮）</summary>

#### 背景

在 Q2 收敛过程中出现了 `GetFrame()` Syscall，更早在 SK7 中出现了 Owner 概念。如果业务展开，宿主侧的参数和方法会不断增加：

- **Owner 域**：GetFrame(), GetHP(), GetStance(), GetPosition(), ...
- **Target 域**：Target_GetHP(), Target_GetDistance(), ...
- **Scene 域**：Scene_GetTime(), Scene_GetEntityCount(), ...
- **Combat 域**：Combat_CheckHit(), Combat_ApplyDamage(), ...

C# 宿主侧自然用对象封装（`owner.GetFrame()`），但 FFS 没有对象。需要在 FFS 侧找到等效的组织方式。

#### 三种手段

##### 手段 1：Syscall 命名前缀 — 隐式绑定 Owner

将 Owner 相关的 Syscall 用前缀分组，运行时自动绑定到当前实例的 Owner：

```ffs
// FFS 侧 — 平坦调用，前缀作命名空间
Owner_GetFrame()
Owner_GetHP()
Owner_SetStance(STANCE_CROUCH)
```

C# 宿主侧：
```csharp
table.Register(SYS_OWNER_GET_FRAME, "Owner_GetFrame", (ref VMInstanceState s) => {
    var owner = GetOwner(ref s);  // 隐式解析当前实例的 Owner
    new SyscallArgs(ref s).SetReturnInt(owner?.Frame ?? 0);
});
```

> **用户反馈**：略麻烦，且绑定 Owner。可能有获取其他实体的需求（如 Target），槽位会不够用。

**评估**：

| 维度 | 评价 |
|------|------|
| 语言改动 | ✅ 零 |
| VM 改动 | ✅ 零 |
| 扩展性 | ⚠️ 每个域 × 每个方法 = 一个槽位。256 个 Syscall 槽位可能不够 |
| 灵活性 | ⚠️ 域是固定的（Owner/Target/Scene），新增域需要大量新 Syscall |

##### 手段 2：Include 组织脚本 — 配套的 API 头文件

> **用户新提案（第 19 轮）**：将 include 头文件升级为"服务脚本"概念 — 不仅是编译期文本展开，而是运行时可寻址的独立脚本实例。"业务脚本"引用多个"服务脚本"，后者可常驻也可动态产生/销毁。用户指出这与 L4/L5（跨模块共享变量/跨模块函数调用）相关。详见 **Q4-A 服务脚本深入分析**。

每个实际脚本配套一个 include 头文件，提供常量定义和薄 wrapper 函数：

```ffs
// skill_api.ffs — 技能 API 组织脚本
const STANCE_STAND = 0
const STANCE_CROUCH = 1

func get_frame() { return _GetFrame() }
func get_hp() { return _GetHP() }
func get_target_hp(id) { return _GetTargetHP(id) }
```

```ffs
// skill_light_punch.ffs
#include "skill_api.ffs"

func step() {
    while get_frame() < 10 {
        // ...
        yield
    }
}
```

> **用户反馈**：跟我想到一起去了。我也在想每个实际脚本是否需要一个配套的组织脚本。

**评估**：

| 维度 | 评价 |
|------|------|
| 语言改动 | ✅ 零 — Lang-2 include 已支持 |
| VM 改动 | ✅ 零 |
| 扩展性 | ✅ 无限 — 头文件可定义任意多个 wrapper |
| 灵活性 | ✅ 高 — 不同类型脚本可 include 不同 API 头文件 |
| Syscall 节省 | ✅ 底层只需少量泛用 Syscall，头文件负责组织 |

##### 手段 3：泛用参数化 Syscall — 一个 Syscall + 域 ID + 属性 ID

> **用户反馈**：ECS 阶段可能会用到，不过此时传入 span 之类的可能更合适。

用少量泛用 Syscall 替代大量专用 Syscall：

```ffs
// 底层：一个 Syscall 解决所有 Get 需求
// GetAttr(entityId, attrId) → value
const SELF = 0
const ATTR_FRAME = 1
const ATTR_HP = 2

func get_frame() { return GetAttr(SELF, ATTR_FRAME) }
func get_hp() { return GetAttr(SELF, ATTR_HP) }
func get_target_hp(target_id) { return GetAttr(target_id, ATTR_HP) }
```

> **用户反馈**：算是手段 1 的泛用化。

**评估**：

| 维度 | 评价 |
|------|------|
| 语言改动 | ✅ 零 |
| VM 改动 | ✅ 零 |
| Syscall 槽位 | ✅ 极少 — GetAttr/SetAttr/CallMethod 即可覆盖大部分需求 |
| 扩展性 | ✅ 极高 — 新增属性只需加 const，不需要新 Syscall |
| 类型安全 | ⚠️ 弱 — attrId 是 int，编译期无法检查 |
| 调试友好度 | ⚠️ 中 — 错误消息只能说"attrId 42 未知"，不如"Owner_GetHP not found" |

#### 用户关注：寄存器分配的兜底机制

> 用户指出：扩展方向由业务决定，等于做死了，需要兜底机制。

这里的核心担忧是：**如果宿主参数组织完全由业务侧自行扩展（命名前缀、include 文件等），没有 VM/编译器层面的保护，可能导致资源耗尽**。

潜在风险与兜底方案：

| 风险 | 场景 | 兜底机制 |
|------|------|---------|
| **Syscall 槽位耗尽** | 手段 1 按域×方法分配，256 槽位不够 | 手段 2/3 减少底层 Syscall 数量；或扩展 SyscallTable 到 512/1024 |
| **模块变量溢出** | include 头文件引入大量 const/var | Lang-1.1b 扩展寄存器已兜底（溢出到 ExtendedRegs） |
| **函数数量爆炸** | 大量 wrapper 函数占 ROM 空间 | 编译器可优化内联简单 wrapper（未来 peephole 扩展） |
| **编译时间膨胀** | include 展开后源码过大 | Preprocessor 已有 cycle detection；可加文件大小上限警告 |

**结论**：Lang-1.1b 的 ExtendedRegs 已为模块变量溢出提供了兜底。Syscall 槽位是当前唯一硬上限（256），如果业务确实需要更多，扩展 SyscallTable 是简单的 VM 改动。

#### 综合推荐

| 层次 | 推荐 | 理由 |
|------|------|------|
| **底层 Syscall** | 手段 3（泛用参数化） | 最省槽位，灵活性最高 |
| **脚本组织** | 手段 2（include 组织脚本） | 已有基础，零改动，提供类型友好的 API 层 |
| **命名约定** | 手段 1（前缀命名）用于少量高频 Syscall | 如 GetFrame()、SetHitbox() 等无需参数化的核心 API |

组合模式：**底层用少量泛用 Syscall（手段 3）+ include 头文件提供友好 wrapper（手段 2）+ 少量高频操作保留专用 Syscall（手段 1）**。

```
┌─────────────────────────────────────────────┐
│  skill_light_punch.ffs                       │
│  #include "skill_api.ffs"                    │
│  func step() { while get_frame() < 10 ... } │
└─────────────┬───────────────────────────────┘
              │ include 展开
┌─────────────▼───────────────────────────────┐
│  skill_api.ffs  (组织脚本/API 头文件)        │
│  func get_frame() { return GetFrame() }      │  ← 高频专用 Syscall
│  func get_hp() { return GetAttr(SELF,HP) }   │  ← 泛用 Syscall + wrapper
│  func get_target_hp(id) {                    │
│      return GetAttr(id, HP)                  │  ← 泛用 Syscall + wrapper
│  }                                           │
└─────────────┬───────────────────────────────┘
              │ Syscall 边界
┌─────────────▼───────────────────────────────┐
│  C# SyscallTable (256 slots)                 │
│  [0-15] 专用高频: GetFrame, SetHitbox, ...   │
│  [16-31] 泛用: GetAttr, SetAttr, CallAction  │
│  [32-63] 黑板: GetBB, SetBB                  │
│  [64+] 扩展保留                              │
└─────────────────────────────────────────────┘
```

#### 兜底机制建议

> **用户反馈**：在"服务脚本"设想中，完美缓解 Syscall 紧张问题 — 每个服务脚本有自己的 SyscallTable，不再共享 256 槽位。

1. **Syscall 槽位**：如果 256 不够，SyscallTable 可扩展为 `ushort` 索引（65536 槽位），VM 改动极小（SYSCALL 指令的 B 字段从 byte 改为 ushort）
2. **模块变量**：ExtendedRegs 已兜底（Lang-1.1b）
3. **函数表**：当前无上限（动态数组），不需要额外兜底
4. **编译期检查**：编译器在 include 展开后统计 Syscall 使用数量，超过阈值发出警告

#### 第 18 轮待确认项回顾

> 用户回复（第 19 轮）：

| # | 问题 | 用户回复 |
|---|------|---------|
| 1 | 手段 2+3 组合路线是否符合直觉？ | ✅ 符合直觉 |
| 2 | "每个脚本配套组织脚本"的具体设想？ | 就是服务脚本/业务脚本的方向（"看来我们想到一块去了"） |
| 3 | 兜底机制的优先级？ | 如果服务脚本设想通过，自动解决 |
| 4 | 泛用 Syscall 的粒度？ | 看起来可以随业务进行，是 Syscall 定义的问题 |

**结论**：手段 2+3 组合确认 ✅。讨论重心转向**服务脚本设想**的可行性分析。

---

#### Q4-A："服务脚本"设想深入分析（第 19 轮）

##### 用户核心提案

> **用户澄清（第 20 轮）**：include 是 mixin/继承类似的作用（编译期文本展开）。"服务脚本"是一个**独立的待定功能**，不一定通过 `#include` 关键字实现。两者是不同层次的概念。
用户将手段 2（include 组织脚本）升级为一个更深层的架构设想：

| 概念 | 定义 | 生命周期 |
|------|------|---------|
| **业务脚本** | 实际执行技能/行为的脚本（如 `skill_light_punch.ffs`） | 由宿主按需创建/销毁 |
| **服务脚本** | 提供域 API 封装的脚本（如 `owner_service.ffs`） | 常驻存在，但允许动态产生/销毁 |

业务脚本可引用多个服务脚本。服务脚本提供的不仅是编译期的 include 文本展开，而是运行时可寻址的服务接口。

**用户原始问题**：
1. 服务脚本如何被业务脚本持有？
2. 服务脚本的自动产生/销毁机制（真的需要自动产生销毁吗？）
3. 服务脚本可能和某个流程绑定，但流程由变化的上下文决定（这可能是自动销毁产生的来源）
4. 服务脚本如何机制轻量化，需要硬规则吗？

##### 关键洞察：两个层次的"服务脚本"

用户的设想实际上包含两个层次，需要区分：

| 层次 | 描述 | 实现手段 | VM 改动 |
|------|------|---------|---------|
| **L2 层：编译期服务** | include 头文件提供 wrapper 函数和常量 | 当前 Lang-2 已完全支持 | ✅ 零 |
| **L4/L5 层：运行时服务** | 独立 VM 实例提供可调用的 API，有自己的状态和生命周期 | 需要跨模块调用（L4/L5） | ⚠️ 需要新机制 |

这是一个重要的分层：

- **L2 层已经解决了"API 组织"问题** — include 头文件把泛用 Syscall 包装成友好的函数名，编译期展开，零运行时开销。这就是第 18 轮讨论的手段 2。
- **L4/L5 层解决的是"运行时服务依赖"问题** — 一个脚本实例需要调用另一个脚本实例的函数、共享另一个实例的状态。这是更深层的需求。

用户的设想似乎在描述 L4/L5 层 — 服务脚本作为运行时实体，有独立的生命周期和可寻址的接口。

##### 跨语言对比：其他语言如何处理此类需求

> 按用户要求，覆盖高级语言在受限框架下的局部办法，以及天生受限语言的处理方式。

**A. 高级语言在受限框架下的局部方案**

| 语言/框架 | 模式 | 描述 | 对应 FFS 层次 |
|-----------|------|------|-------------|
| **C# / Unity** | MonoBehaviour 组件 | `GetComponent<T>()` 获取同 GameObject 上的服务组件。组件有独立生命周期（Awake/Start/OnDestroy）。引用通过字段持有。 | L4/L5 运行时服务 |
| **Lua / Redis** | 模块表 | `local json = require("cjson")` 返回模块表。模块常驻 `package.loaded`。无独立生命周期。 | L2 编译期服务 |
| **GDScript / Godot** | Autoload 单例 | `Global.get_service("combat")` 全局单例。场景切换时持久化。 | L4/L5（宿主管理生命周期） |
| **Kotlin / Android** | Dagger/Hilt DI | `@Inject lateinit var service: CombatService`。框架管理生命周期（Singleton/Scoped/Provider）。 | L4/L5（框架管理生命周期） |

**B. 天生受限语言的处理方式**

| 语言 | 限制 | 处理方式 | 对应 FFS 层次 |
|------|------|---------|-------------|
| **Wren** | 无全局状态，纯 Fiber | 通过宿主 C API 桥接：`foreign class CombatService { ... }`。宿主管理实例，脚本通过外部类访问。 | L4/L5（宿主桥接） |
| **Squirrel** | 表驱动，无类型系统 | 宿主注入全局表：`combatService <- {}; combatService.getHP <- function() { ... }`。宿主填充表内容。 | L2（宿主注入） + 手段 1（Syscall） |
| **Ink (inkle)** | 纯叙事脚本，无函数 | 通过 external function 和标签让宿主处理所有逻辑。脚本只描述流程。 | 完全手段 1（所有逻辑在宿主） |
| **Fennel** | 编译到 Lua，受 Lua 限制 | 继承 Lua 的 require/模块表。编译期宏提供语法糖。 | L2 编译期 |
| **汇编 / 嵌入式 C** | 无模块系统 | 头文件 + 链接时符号解析。"服务"就是一组函数 + 全局状态。 | L2（头文件）+ 手段 3（函数指针表） |

**C. 共性模式总结**

| 模式 | 生命周期管理 | 发现机制 | 适用场景 |
|------|------------|---------|---------|
| **模块导入**（Lua require, C# using） | 首次导入时创建，永久驻留 | 名称查找 | 无状态工具库 |
| **依赖注入**（Dagger, Unity DI） | 框架管理（Singleton/Scoped） | 声明式注解 | 有状态服务，生命周期复杂 |
| **组件模型**（Unity GetComponent） | 跟随宿主对象 | 类型查找 | 同一实体的多能力 |
| **全局单例**（Godot Autoload） | 应用级常驻 | 全局名称 | 跨场景持久服务 |
| **外部函数**（Ink external, Wren foreign） | 宿主完全控制 | 宿主注册 | 脚本无法自行管理的能力 |

##### 对 FFS "服务脚本"的分析

###### 问题 1：服务脚本如何被业务脚本持有？

三种可能的持有方式：

> **用户反馈（第 20 轮）**：
> - 偏好方式 C（语言级引用）
> - 关于方式 A：我们最初想解决的是 FFS 缺乏组织能力的问题，希望 Syscall 足够优雅好用，服务脚本正是解决办法。方式 A 把问题又推回了 C# Syscall 层，等于没解决
> - 方式 B 也有同样的问题
> - 用户希望讨论重点放在**服务脚本（方式 C）的可行性、性能、易用度、心智负担**上

| 方式 | 描述 | VM 改动 | 复杂度 |
|------|------|---------|--------|
| **A. 宿主持有** | 宿主为每个业务脚本实例配套创建服务脚本实例，业务脚本通过 Syscall 间接访问 | 零 | ⭐ 低 |
| **B. 实例 ID 持有** | 业务脚本持有服务脚本的 instanceId（整数），通过跨实例 Syscall 调用 | ⭐ 小 — 需要新 Syscall | ⭐⭐ 中 |
| **C. 语言级引用** | FFS 引入"实例引用"类型，`var svc = GetService("combat")`，可直接 `svc.getHP()` | ⭐⭐⭐ 大 — 需要 L4/L5 | ⭐⭐⭐ 高 |

**推荐方式 A**（宿主持有）：零 VM 改动，宿主 C# 代码天然具备对象引用能力，可以为每个角色维护一组服务实例。FFS 脚本通过 Syscall 访问，完全不感知"服务实例"的存在。

```
┌─────────────────────────────────────────────┐
│  C# Host                                     │
│                                              │
│  class CharacterScriptHost {                  │
│      VMWorld vm;                              │
│      int skillInstanceId;     // 业务脚本     │
│      OwnerService ownerSvc;   // "服务" — 纯 C# │
│      CombatService combatSvc; //               │
│                                              │
│      void RegisterSyscalls() {               │
│          // Syscall 内部引用 ownerSvc          │
│          table.Register("GetFrame",           │
│              (ref s) => ownerSvc.GetFrame()); │
│      }                                       │
│  }                                           │
└─────────────────────────────────────────────┘
```

这就是"服务脚本在 C# 层，FFS 脚本通过 Syscall 消费"的模式。实际上，**当前的 Syscall 机制已经是这个模式** — SyscallTable 的每个 handler 就是一个 C# 方法，背后可以是任意复杂的服务对象。

###### 问题 2：自动产生/销毁机制

> 真的需要自动产生销毁吗？

| 场景 | 是否需要自动管理 | 理由 |
|------|----------------|------|
| **角色生命周期内的固定服务**（Owner、Combat） | ❌ 不需要 | 跟随角色创建/销毁，宿主 C# 直接管理 |
| **战斗中临时服务**（buff 效果器、临时 AI） | ⚠️ 可能需要 | 生命周期与游戏事件绑定，但宿主同样可以管理 |
| **跨角色共享服务**（全局状态管理器） | ❌ 不需要 | 全局单例，应用级生命周期 |

**关键观察**：在 FFS 的设计中，**VM 不管理游戏对象的生命周期 — 宿主管理**。这是 SK12（ECS 数据归属）的结论：脚本内闭环的数据归 VM，外部需读取的数据通过 Syscall 推送到宿主。

因此，"服务"的生命周期也应该由宿主管理。VM/FFS 层面不需要自动产生/销毁机制。

###### 问题 3：服务与流程绑定

用户指出：服务脚本可能和某个流程绑定，而流程由变化的上下文决定。

这在 C# 宿主侧是自然的：

```csharp
// 上下文变化时切换服务实现
void OnStanceChanged(Stance newStance) {
    // 更新 Syscall 背后的服务对象
    currentCombatService = newStance == Stance.Air 
        ? airCombatService 
        : groundCombatService;
}
```

FFS 脚本完全不感知服务切换 — 它调用的 Syscall 名称不变，只是背后的 C# 实现切换了。这就是**接口多态在 Syscall 层的自然表达**。

###### 问题 4：轻量化与硬规则

如果"服务脚本"保持在 C# 层（方式 A），则：
- ✅ **天然轻量** — 没有 VM 实例开销，纯 C# 对象
- ✅ **无需硬规则** — C# 的 GC 和对象生命周期管理已经成熟
- ✅ **无性能担忧** — Syscall handler 是直接 C# 方法调用，无跨实例开销

如果未来真的需要"服务脚本"在 FFS 层运行（方式 C），那确实需要：
- ⚠️ 实例池上限（防止无限创建）
- ⚠️ 引用计数或 GC（防止泄漏）
- ⚠️ 跨实例调用协议（L4/L5）

但 **KOF98 范围内不需要方式 C**。方式 A 已完全覆盖所有已知需求。

##### 与 L4/L5 的关系

| 语言演进 | 描述 | 对应服务脚本需求 | 当前状态 |
|---------|------|----------------|---------|
| **L1~L3** | 模块变量 + include + 黑板 | 编译期服务（方式 A）完全覆盖 | ✅ 已完成 |
| **L4** | 跨模块共享变量 | 多个 FFS 实例共享数据 | ❌ 未实现，当前无刚性需求 |
| **L5** | 跨模块函数调用 | FFS 实例间直接调用函数 | ❌ 未实现，当前无刚性需求 |

用户提到的"服务脚本"如果定义为"FFS 脚本实例"，则确实需要 L4/L5。但如果定义为"宿主侧 C# 服务对象 + Syscall 接口"，则 **当前 Phase 1 已完全支持**。

##### 综合结论

```
 实际架构（推荐）                     概念映射
 ═══════════════                     ═════════
 
 skill_light_punch.ffs ──┐           业务脚本
   #include "skill_api.ffs"          
                         │ Syscall   
 C# CharacterHost ───────┤           服务层
   ├─ OwnerService       │           "Owner 服务脚本"
   ├─ CombatService      │           "Combat 服务脚本"  
   └─ SceneService       │           "Scene 服务脚本"
                         │
 VM 运行时 ──────────────┘           基础设施
```

1. **"服务脚本"的核心需求已被当前架构覆盖** — C# 宿主侧的服务对象 + Syscall 接口 = 事实上的"服务脚本"
2. **FFS include 头文件 = 服务脚本的"接口定义"** — 提供友好的函数名、常量、wrapper
3. **不需要 FFS 层的运行时服务实例** — 在 KOF98 范围内，C# 宿主天然是服务提供者
4. **L4/L5 作为远期储备** — 如果未来出现"FFS 脚本需要调用其他 FFS 脚本"的刚性需求，再引入

##### 待用户确认（第 19 轮）

1. **"服务脚本 = C# 宿主服务 + Syscall 接口"是否匹配你的设想？** — 还是说你确实在想 FFS 层的运行时服务实例（需要 L4/L5）？
2. **include 头文件作为"服务接口定义"是否足够？** — `skill_api.ffs` 定义 `get_frame()` 等 wrapper，业务脚本 include 后直接调用
3. **生命周期管理放在 C# 宿主是否可接受？** — 宿主根据角色状态创建/切换/销毁服务对象，FFS 脚本不感知
4. **是否有具体场景需要 FFS 实例间直接通信？** — 这将决定 L4/L5 的优先级

> **第 19 轮问题回顾**：用户回答了 1 — 确实在想 FFS 层的运行时服务实例（方式 C），不是 C# 宿主服务。方式 A/B 都把问题推回了 Syscall 层。详见 Q4-B。

---

#### Q4-B：方式 C（语言级服务脚本）可行性深入分析（第 20 轮）

> **讨论重心**：用户明确服务脚本是 FFS 运行时实体，偏好方式 C（语言级引用），要求聚焦**可行性、性能、易用度、心智负担**四个维度。

##### 前提纠偏

第 19 轮分析存在一个关键误解：将 `#include`（编译期 mixin）和"服务脚本"（运行时实体）混为一谈。用户的真实意图是：

| 概念 | 本质 | 实现层 |
|------|------|--------|
| `#include "skill_api.ffs"` | 编译期文本展开，类似 C 头文件 / mixin | Lang-2，已完成 |
| 服务脚本 `owner_service` | 独立运行时实体，有自己的状态和生命周期，可被业务脚本引用和调用 | **新机制，需设计** |

两者**正交**：include 解决代码复用，服务脚本解决**运行时组织能力**。用户的核心诉求是：

> FFS 作为无对象语言，缺乏组织能力。Syscall 是底层管道，不应直接暴露给业务逻辑。服务脚本作为中间层，让 FFS 脚本之间能够**优雅地组织和调用**，而不是所有交互都退化为 Syscall。

##### 维度 1：可行性分析

###### 方式 C 需要什么？

从 VM 当前架构出发，实现"业务脚本引用服务脚本实例并调用其函数"需要：

| 组件 | 当前状态 | 需要的改动 | 复杂度 |
|------|---------|-----------|--------|
| **多实例** | ✅ InstancePool 支持 128 个实例 | 无需改动 | — |
| **多模块** | ✅ VMModuleTable 支持 64 个模块 | 无需改动 | — |
| **跨实例引用** | ❌ 不支持 | 业务实例持有服务实例 ID（一个 Number 值） | ⭐ 低 |
| **跨实例函数调用** | ❌ 不支持 | 新 OpCode `XCALL`：切换到目标实例执行函数，返回后恢复 | ⭐⭐ 中 |
| **服务实例发现** | ❌ 不支持 | Syscall `GetService(nameId)` 返回 instanceId | ⭐ 低 |
| **服务注册** | ❌ 不支持 | 宿主预创建服务实例，注册名称→instanceId 映射 | ⭐ 低 |

核心技术挑战是 **XCALL（跨实例函数调用）**。以下是两种可能的实现：

**方案 C1：同步跨实例调用（类似函数调用）**

```
XCALL 指令语义：
  1. 保存当前实例状态（IP, RegisterBase, CallStackDepth）到跨调用帧
  2. 切换到目标实例
  3. 将参数从调用方的 r0-rN 复制到目标的 r0-rN
  4. 执行目标函数（在 RunInstance 的同一个 Tick 内）
  5. 将返回值从目标的 r0 复制回调用方的 r0
  6. 恢复调用方状态
```

FFS 脚本侧用法：
```ffs
// skill_light_punch.ffs — 业务脚本
var owner = GetService(SVC_OWNER)   // Syscall 返回 instanceId (整数)

func step() {
    var frame = call(owner, "get_frame")    // 跨实例调用
    while frame < 10 {
        call(owner, "set_hitbox", 1, 20, 30)
        yield
        frame = call(owner, "get_frame")
    }
}
```

**方案 C2：基于 Syscall 的跨实例桥接（不改 VM 核心）**

```
不引入 XCALL OpCode，而是用一组 Syscall 实现跨实例调用：
  SVC_Call(targetInstanceId, funcNameId, arg0, arg1, ...) → result
  
宿主 C# 实现：
  1. 暂停当前实例
  2. 在目标实例上执行指定函数（调用 RunInstance 直到完成）
  3. 取目标的 r0 作为返回值
  4. 恢复当前实例，写返回值到 r0
```

FFS 脚本侧用法（与 C1 相同，但 `call` 是 Syscall 而非 OpCode）：
```ffs
var owner = GetService(SVC_OWNER)
var frame = SvcCall(owner, FN_GET_FRAME)
```

**两种方案对比：**

| 维度 | C1（XCALL OpCode） | C2（Syscall 桥接） |
|------|-------------------|-------------------|
| VM 改动 | ⭐⭐ 中 — 新 OpCode + 跨实例调用逻辑 | ⭐ 低 — 仅宿主 C# Syscall handler |
| 性能 | ✅ 最优 — 寄存器直接复制，零 Syscall 开销 | ⚠️ 每次调用经过 Syscall dispatch | 
| 安全性 | ⭐⭐ 需要防递归、防死锁 | ⭐ 同样需要，但可在 C# 层控制 |
| 实现周期 | ~3 天 | ~1 天 |
| 扩展到 yield | ⚠️ 复杂 — 被调方 yield 时调用方也挂起 | ⚠️ 同样复杂 |

**可行性结论**：✅ **两种方案都可行**。C2 更轻量（可作为 prototype 快速验证），C1 更优雅（长期目标）。

###### 关键技术问题

| 问题 | 分析 | 建议 |
|------|------|------|
| **递归调用** | A 调用 B 的函数，B 的函数又调用 A → 可能栈溢出 | 跨实例调用深度限制（如 max 4 层） |
| **yield 穿透** | 服务脚本函数内 yield 意味着什么？ | 两种选择：禁止（服务函数必须同步完成），或支持（调用方自动挂起） |
| **参数传递** | 跨实例 r0-rN 复制 | MaxArgs 限制（如 8 个参数），已有 ScratchZone 16 个寄存器足够 |
| **返回值** | 目标 r0 → 调用方 r0 | 单返回值（Number），与现有函数一致 |
| **实例生命周期** | 调用正在执行的实例（它自己在 yield） | ✅ 可以 — yield 中的实例状态完整保存，可响应 XCALL |
| **确定性** | 跨实例调用顺序影响回放 | ✅ 确定性 — XCALL 是同步的，调用顺序由指令流决定 |

##### 维度 2：性能分析

以 KOF98 典型场景为基础：

**场景假设**：
- 60 FPS，每帧 Tick 一次
- 20 个活跃角色，每个角色 1 个业务脚本实例 + 3 个服务脚本实例（Owner/Combat/Scene）
- 每个业务脚本每帧调用服务脚本 5 次

**内存开销**：

| 项目 | 当前 | 方式 C 后 |
|------|------|----------|
| 实例数量 | 20 业务 | 20 业务 + 60 服务 = 80 实例 |
| 实例池容量 | 128（足够） | 128（仍然足够） |
| 单实例 RAM | ~720 B（寄存器 512B + 调用栈 + 状态） | 相同 |
| 总实例 RAM | 14.4 KB | 57.6 KB |
| 增量 | — | **+43.2 KB** |

> 注：服务脚本实例如果只提供**纯函数**（无 yield、无状态），可优化为"无寄存器"实例 — 只需要 ROM（指令）不需要 RAM（寄存器），因为每次调用使用调用方的临时寄存器。但这是进阶优化，初期可先不做。

**CPU 开销**：

| 操作 | 方案 C1（XCALL） | 方案 C2（Syscall 桥接） | 原生函数调用（baseline） |
|------|-----------------|----------------------|----------------------|
| 调用开销 | ~15 ns（保存/恢复状态 + 参数复制） | ~25 ns（Syscall dispatch + 参数复制 + RunInstance） | ~5 ns（CALL 指令） |
| 每帧 100 次跨实例调用 | ~1.5 μs | ~2.5 μs | — |
| 占帧预算（16.6 ms） | 0.009% | 0.015% | — |

> **结论**：性能开销**可忽略不计**。即使每帧 1000 次跨实例调用也不到帧预算的 0.2%。

**服务脚本实例的特殊优化空间**：

如果服务脚本的函数是**无状态纯函数**（只读取参数，不依赖模块变量），可以做：
1. **共享实例**：多个角色共享同一个服务脚本实例（Owner 服务例外，因为有 Owner 状态）
2. **零分配调用**：使用调用方的寄存器空间执行服务函数，无需切换实例
3. **内联展开**：编译器将服务函数内联到调用方（类似 include 但运行时发生）

但这些是**进阶优化**，初期实现不需要。

##### 维度 3：易用度分析

**与三种方式的对比（从 FFS 脚本作者视角）**：

**场景**：业务脚本需要获取 Owner 的帧号、HP，设置 hitbox

**方式 A（宿主 Syscall）**：
```ffs
// 直接调用 Syscall — 命名平坦，无组织
var frame = Owner_GetFrame()
var hp = Owner_GetHP()
Owner_SetHitbox(1, 20, 30)
// 问题：Syscall 命名空间只有前缀，无层次感
// 新增 API 需要注册新 Syscall（C# 改动）
```

**方式 B（include wrapper + Syscall）**：
```ffs
#include "owner_api.ffs"
var frame = get_frame()
var hp = get_hp()
set_hitbox(1, 20, 30)
// 改善：函数名更自然
// 问题：本质仍是 Syscall，include 只是语法糖
// 新增 API 仍需注册新 Syscall + 修改 header
```

**方式 C（服务脚本）**：
```ffs
var owner = GetService(SVC_OWNER)

func step() {
    var frame = call(owner, FN_GET_FRAME)
    var hp = call(owner, FN_GET_HP)
    call(owner, FN_SET_HITBOX, 1, 20, 30)
    // 组织性：owner 是一个可寻址的服务
    // 新增 API 只需在服务脚本中加函数
}
```

**如果 FFS 未来支持点号语法糖（语法前端改动）**：
```ffs
var owner = GetService(SVC_OWNER)

func step() {
    var frame = owner.get_frame()       // 编译为 call(owner, FN_GET_FRAME)
    var hp = owner.get_hp()
    owner.set_hitbox(1, 20, 30)
    // 极高可读性，接近 OOP 体验
}
```

**易用度评估表**：

| 维度 | A. Syscall | B. Include+Syscall | C. 服务脚本 | C+语法糖 |
|------|-----------|-------------------|-----------|---------|
| **API 组织** | ❌ 平坦前缀 | ⚠️ 文件级分组 | ✅ 实例级分组 | ✅ OOP 风格 |
| **新增 API** | 需 C# 改动 | 需 C# + header | 只改服务脚本 | 只改服务脚本 |
| **类型安全** | ❌ 无 | ❌ 无 | ⚠️ 函数名查找 | ⚠️ 编译期可检查 |
| **IDE 支持** | ❌ Syscall 无智能提示 | ⚠️ 函数可提示 | ✅ 函数可提示 | ✅ `.` 触发补全 |
| **调试** | ✅ Syscall handler 可断点 | ✅ 同上 | ✅ 两个脚本都可断点 | ✅ 同上 |

##### 维度 4：心智负担分析

**谁承担什么负担？**

| 角色 | A. Syscall | B. Include+Syscall | C. 服务脚本 |
|------|-----------|-------------------|-----------|
| **VM 开发者**（我们） | ✅ 零 | ✅ 零 | ⚠️ 实现 XCALL 或 Syscall 桥接 |
| **宿主集成者**（C# 侧） | ⚠️ 注册大量 Syscall | ⚠️ 注册 Syscall + 维护 header | ✅ 创建服务实例，注册发现机制 |
| **脚本作者**（FFS 侧） | ⚠️ 记忆 Syscall 名 | ✅ include + 调用函数 | ✅ GetService + 调用函数 |

**心智模型对比**：

| 方式 | 脚本作者需要理解的概念 | 认知负担 |
|------|---------------------|---------|
| **A** | Syscall 列表、参数约定、返回值约定 | ⭐⭐ — 需要查表 |
| **B** | include 机制 + 函数签名 | ⭐⭐ — 与 A 类似但有抽象层 |
| **C** | "服务"概念 + 实例引用 + 函数调用 | ⭐⭐ — 新概念但符合直觉 |
| **C+语法糖** | `owner.get_frame()` — 与 OOP 一致 | ⭐ — 最低，与 C#/Lua/GDScript 一致 |

**一个关键洞察**：方式 C 的心智负担看似更多（多了"服务实例"概念），但**实际上更低**，因为：

1. **组织性补偿了学习成本** — 20 个分散的 Syscall 比 3 个有结构的服务更难记忆
2. **发现性** — `owner.` 后按 Tab 看到所有可用方法，vs 翻文档查 Syscall 列表
3. **一致性** — 所有交互都通过 `call(service, func, args...)` 统一模式，无需区分 Syscall/函数/方法

##### 方式 C 的分层实现路径

考虑到复杂度，建议分阶段实现：

| 阶段 | 内容 | VM 改动 | FFS 脚本侧 | 优先级 |
|------|------|---------|-----------|--------|
| **C-0** | Syscall 桥接原型 | 零 — 纯 C# Syscall | `SvcCall(instanceId, funcId, args...)` | ⭐ 可立即做 |
| **C-1** | XCALL OpCode | 新指令 + 编译器支持 | `call(svc, FN_xxx, args...)` | ⭐⭐ 验证 C-0 后做 |
| **C-2** | 点号语法糖 | 编译器前端 | `svc.get_frame()` | ⭐⭐⭐ 验证 C-1 后做 |
| **C-3** | 服务自动发现/注册 | VM 或宿主 | `@service("owner")` 声明 | ⭐⭐⭐⭐ 按需 |

**C-0 阶段（零 VM 改动）的可能实现**：

```ffs
// owner_service.ffs — 服务脚本
func get_frame() {
    return _Owner_GetFrame()   // 内部用 Syscall
}

func get_hp() {
    return _Owner_GetHP()
}

func set_hitbox(active, w, h) {
    _Owner_SetHitbox(active, w, h)
}
```

```ffs
// skill_light_punch.ffs — 业务脚本
var owner_id = GetServiceInstance(SVC_OWNER)   // Syscall: 获取服务实例 ID

func step() {
    var frame = SvcCall(owner_id, 0)           // Syscall: 跨实例调用 func#0
    while frame < 10 {
        SvcCall(owner_id, 2, 1, 20, 30)        // 调用 set_hitbox
        yield
        frame = SvcCall(owner_id, 0)
    }
}
```

C# 宿主侧：
```csharp
// SvcCall handler: 暂停当前实例 → 在目标实例执行函数 → 返回结果
table.Register("SvcCall", (ref VMInstanceState caller) => {
    var args = new SyscallArgs(ref caller);
    int targetId = args.GetInt(0);
    int funcIdx = args.GetInt(1);
    // 将 arg2-argN 复制到目标实例的 r0-rN
    // 调用 vm.RunInstanceFunction(targetId, funcIdx)
    // 将目标的 r0 复制回 caller 的 r0
});
```

**C-0 阶段的价值**：
- ✅ 零 VM 改动，纯宿主 C# 实现
- ✅ 立即可验证"服务脚本"的**概念可行性**和**易用度**
- ✅ 确认后再投入 C-1 优化（XCALL OpCode）
- ⚠️ 性能略低于 C-1（每次经过 Syscall dispatch），但如前述分析，差异 < 0.01% 帧预算

##### 与 L4/L5 的关系（重新定位）

| 原定义 | 服务脚本视角的重新定义 |
|--------|---------------------|
| L4: 跨模块共享变量 | 服务脚本的状态对业务脚本可见（如 owner.hp 可读） |
| L5: 跨模块函数调用 | 业务脚本调用服务脚本的函数（`call(owner, FN_GET_FRAME)`） |

服务脚本的核心需求就是 **L5（跨模块函数调用）**。L4（共享变量）是可选的加分项。

如果聚焦 L5，实现范围收窄为：
- **XCALL 指令**（或 Syscall 桥接）
- **实例引用**（Number 值存储 instanceId）
- **函数索引**（编译器为每个导出函数生成 index）

L4（共享变量）可以通过 L5 间接实现：`call(owner, FN_GET_HP)` 等同于读取 owner 的 HP 变量。因此 **L5 单独就够用**。

##### 综合评估

| 维度 | 评估 | 备注 |
|------|------|------|
| **可行性** | ✅ 高 | C-0 零改动即可验证；C-1 改动范围可控（一个新 OpCode + 编译器） |
| **性能** | ✅ 可忽略 | 80 实例 ≈ 57.6 KB RAM；每帧 100 次 XCALL ≈ 1.5 μs（< 0.01% 帧预算） |
| **易用度** | ✅ 高 | 比 Syscall 更有组织感；C-2 语法糖可达 OOP 级体验 |
| **心智负担** | ✅ 低 | "服务实例 + 函数调用"模型符合直觉；统一模式替代分散 Syscall |

##### 待用户确认（第 20 轮）

> **用户回复（第 21 轮）**：
> 1. 需要确认嵌套 XCALL 的性能消耗；XCALL 是通用编程概念吗？如果是，参考其他实现
> 2. 无 yield ✅。追问：如何保证（编译期？运行时？）
> 3. 语法糖满意 ✅
> 4. 宿主创建 ✅
1. **C-0 Syscall 桥接作为原型验证是否可接受？** — 零 VM 改动，先验证概念和手感，确认后再做 C-1（XCALL OpCode）
2. **服务函数是否都是同步的（无 yield）？** — 如果是，实现大幅简化（无需处理跨实例 yield 穿透）
3. **`call(svc, FN_xxx, args...)` 的调用风格是否可接受？** — 还是必须要 `svc.get_frame()` 语法糖才满意？
4. **服务脚本实例由谁创建？** — 宿主 C# 创建并注册到 VM（推荐），还是 FFS 脚本自行创建？

---

#### Q4-C：XCALL 跨语言参考 + 无 yield 保证 + 设计收敛（第 21 轮）

> 用户确认：方式 C ✅、无 yield ✅、语法糖 ✅、宿主创建 ✅。
> 追问两个关键问题：(1) XCALL 是通用概念吗？嵌套 XCALL 性能如何？(2) 无 yield 如何保证？

##### 回应 1：XCALL 是通用编程概念吗？

**是的。** 跨模块/跨实例同步函数调用是一个广泛存在的概念，不同语言/VM 有不同命名：

| 语言/VM | 概念名称 | 机制 | 性能特征 |
|---------|---------|------|---------|
| **Lua** | `require` + 函数调用 | 所有模块共享同一 VM state，跨模块调用 = 普通函数调用 | ≈ 普通 CALL，零额外开销 |
| **Erlang/BEAM** | 消息传递（`!`） | 完全异步，跨进程复制数据 | 较高（消息复制 + 调度） |
| **WebAssembly** | `call_indirect` / Host import | 跨模块通过 import/export 表 | ≈ 1.5× 普通 call（间接跳转） |
| **JVM** | `invokevirtual` / `invokeinterface` | 虚方法表查找 + 跳转 | ≈ 1.2× 普通 call（vtable 查找） |
| **CLR (.NET)** | 虚方法调用 / 委托调用 | 虚表 or 委托间接调用 | ≈ 1.1-1.5× 普通 call |
| **Python** | 对象方法调用 | 属性查找 + 函数对象调用 | 较高（动态查找） |
| **Wren** | `foreign method` | 宿主函数注册 + C 桥接调用 | ≈ 2× 普通 call（FFI 开销） |
| **Unity Burst** | `FunctionPointer<T>` | 编译期已知签名，间接调用 | ≈ 普通 call（已优化） |

**FFVM 的 XCALL 最接近哪个？**

XCALL 最接近 **WebAssembly 的跨模块调用** 和 **Wren 的 foreign method**：
- 目标函数在另一个"实例"中（不同的寄存器文件、不同的 IP）
- 参数通过约定区域传递（scratch zone r0-r15）
- 同步执行，返回后恢复调用方

但 FFVM XCALL 比 WebAssembly 更简单，因为：
- 无需类型检查（FFS 是动态类型 Number）
- 无需内存隔离（所有实例共享宿主内存模型）
- 参数在固定寄存器（r0-rN），不需要栈操作

##### 嵌套 XCALL 性能分析

**嵌套场景**：业务脚本 A → 调用服务 B 的函数 → B 内部又调用服务 C 的函数

```
A.step()
  └─ XCALL B.get_combo_damage()           // 第 1 层 XCALL
       └─ XCALL C.get_attack_multiplier() // 第 2 层 XCALL（嵌套）
```

**每层 XCALL 的开销**：

| 操作 | 指令数 | 耗时（估） | 说明 |
|------|--------|-----------|------|
| 保存调用方状态 | 3 次写入（IP, RegBase, instanceId） | ~3 ns | 写入跨调用帧 |
| 参数复制 r0-rN | N 次 Number 复制（8B each） | ~2-8 ns | 取决于参数数量 |
| 切换到目标实例 | 1 次指针切换 | ~1 ns | `ref inst = ref Pool.Instances[targetId]` |
| 执行目标函数 | 目标函数体 | 变化 | 取决于函数复杂度 |
| 复制返回值 r0 | 1 次 Number 复制 | ~1 ns | |
| 恢复调用方状态 | 3 次读取 + 切换 | ~4 ns | |
| **单层总 overhead** | — | **~12-18 ns** | 不含函数体本身 |

**嵌套性能**：

| 嵌套深度 | 每次 overhead | 每帧 50 次调用 | 占帧预算 |
|---------|-------------|--------------|---------|
| 1 层 | ~15 ns | ~0.75 μs | 0.005% |
| 2 层 | ~30 ns | ~1.5 μs | 0.009% |
| 3 层 | ~45 ns | ~2.25 μs | 0.014% |
| 4 层（上限） | ~60 ns | ~3.0 μs | 0.018% |

> **结论**：即使 4 层嵌套、每帧 50 次，开销也不到帧预算的 0.02%。**嵌套 XCALL 性能完全不是问题。**

**与本 VM 内部 CALL 对比**（参考 `VMWorld.cs:552-571`）：

| 操作 | CALL（本模块内） | XCALL（跨实例） | 差异来源 |
|------|---------------|----------------|---------|
| CallFrame 保存 | 写入 CallStack | 写入 XCallFrame | 相似（同一数据量） |
| 寄存器窗口移动 | `RegBase += op.B` | 切换到新实例的寄存器 | XCALL 多了实例切换 |
| IP 跳转 | `IP = op.A` | `IP = targetFuncEntry` | 相似 |
| 参数传递 | 已在寄存器窗口中 | 需复制 r0-rN | **XCALL 额外 ~8 ns** |
| **单次总 overhead** | **~5 ns** | **~15 ns** | **≈ 3× CALL** |

3× 的倍数看似大，但绝对值（10 ns 差距）在 16.6 ms 帧预算中完全可忽略。

##### 回应 2：无 yield 如何保证？

用户问了一个关键的工程问题。有三个层次的保证方案：

###### 方案 Y1：编译期禁止（推荐 ✅）

**原理**：服务脚本编译时，编译器标记模块为 `@service`（或通过编译选项），然后**拒绝编译包含 yield 的函数**。

```ffs
// owner_service.ffs — 服务脚本
@service   // 编译器标记：此模块的函数不允许 yield

func get_frame() {
    return _Owner_GetFrame()
}

func do_something() {
    yield   // ❌ 编译错误："service script functions cannot yield"
}
```

**实现**：在编译器 `BytecodeCompiler` 中：
1. 解析到 `@service` 装饰器（或编译选项 `isService = true`）时设置标记
2. 编译函数体时如果遇到 `yield` 语句，报编译错误

```
编译器改动量：~10 行代码（一个 bool 标记 + 一个 if 检查）
```

优点：
- ✅ **零运行时开销** — 编译期完全拦截
- ✅ **明确的错误信息** — 脚本作者立即知道原因
- ✅ **不可绕过** — 编译器不生成 yield 字节码

###### 方案 Y2：运行时检测

**原理**：XCALL 执行目标函数时，如果目标函数执行到 YIELD 指令，不挂起而是报错。

```
XCALL 执行时：
  设置标记 inst.StateFlags |= InXCall
  执行目标函数...
  如果目标执行到 YIELD → 检测到 InXCall 标记 → 报 VMError
```

**实现**：
```csharp
case OpCode.YIELD:
    if ((inst.StateFlags & VMStateFlags.InXCall) != 0)
    {
        inst.ErrorFlag = VMError.YieldInServiceCall;
        return;
    }
    // ... 正常 yield
```

优点：
- ✅ 兜底保护 — 即使编译器漏检也能捕获
- ⚠️ 有少量运行时开销（每次 YIELD 多一个 if 检查）

###### 方案 Y3：双保险（编译期 + 运行时）

**推荐方案**：Y1 + Y2 同时使用。

| 层次 | 保证 | 开销 |
|------|------|------|
| **编译期**（Y1） | `@service` 模块禁止 yield | 零运行时开销 |
| **运行时**（Y2） | XCALL 期间遇 YIELD 报错 | 每次 YIELD 多一个 flag 检查（~1 ns） |

编译期是主力，运行时是安全网。即使未来有人用其他编译器（或绕过 `@service` 标记），运行时仍能阻止 yield 穿透。

##### 回应 3 & 4：已确认

| 问题 | 用户回答 | 状态 |
|------|---------|------|
| 语法糖 `svc.get_frame()` | 满意 ✅ | C-2 阶段实现 |
| 服务脚本由宿主创建 | 确认 ✅ | C-0/C-1 阶段即支持 |

##### 已收敛的设计决策

经过第 19-21 轮讨论，以下设计决策已收敛：

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 编译期 + 运行时双保险 | R21 ✅ |
| 调用语法 | C-2 语法糖 `svc.func()` | R21 ✅ |
| 实现路径 | C-0 → C-1 → C-2 → C-3 分阶段 | R20 |
| 嵌套调用 | 允许，最大深度 4 层 | R21 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算） | R20-21 ✅ |

##### 剩余开放问题

| 问题 | 描述 | 优先级 |
|------|------|--------|
| **函数索引 vs 名称** | `call(svc, 0)` 用整数索引还是 `call(svc, "get_frame")` 用字符串？整数更快但不直观，字符串需编译期查找表 | ⭐⭐ 中 |
| **服务注册 API** | 宿主 C# 侧如何注册服务？`vm.RegisterService("owner", instanceId)` 还是绑定到角色实体？ | ⭐ 低（C# 侧设计） |
| **错误处理** | XCALL 目标实例不存在或已销毁时的行为 — 返回 0？panic？ | ⭐ 低 |
| **C-0 原型时机** | 是否需要等 KOF98 ECS 集成再验证，还是可以先独立实现 | ⭐ 取决于路线图 |

##### 待用户确认（第 21 轮）

> **用户回复（第 22 轮）**：
> 1. 最好要求编译器保证，包括 UI，不想运行时负担 → **Y1 纯编译期保证**
> 2. 语法糖终极形态 → **直接瞄准 `svc.func()` C-2 语法糖**
> 3. 关于 XCALL 优化：初期几个方向 + 内部退化方案需要考虑；自动退化方案在稳定前提下以性能为第一优先因素（仍需考虑风险解决成本带来的性能损耗）。内联看起来已很理想但仍需走完形式以免遗漏
> 4. 考虑当前方案和 L4/L5 的关联，是否"正教"？L4/L5 实现时是否需要对此纳入考虑

1. **无 yield 保证：编译期 `@service` 标记 + 运行时兜底（Y3 方案）是否满意？** — 还是觉得只需要其中一个？
2. **函数索引 vs 名称**：偏好 `call(svc, 0)` 整数索引（性能最优但不直观），还是 `call(svc, FN_GET_FRAME)` 编译期常量（推荐，兼顾性能和可读性），还是 `svc.get_frame()` 语法糖（最终形态，需 C-2）？
3. **Q4 是否可以认为已基本收敛？** — 上面的"已收敛的设计决策"表是否完整反映了你的期望？如果是，下一步可以转入设计文档或实现计划。

---

#### Q4-D：编译期纯保证 + XCALL 优化方向 + L4/L5 正教性分析（第 22 轮）

> 用户明确四点：(1) yield 保证纯编译期，不要运行时负担；(2) 直接瞄准语法糖终极形态；(3) XCALL 优化需要系统性分析，内联很理想但要走完形式；(4) 当前设计是否是通往 L4/L5 的"正教"路径。

##### 回应 1：纯编译期 yield 禁止（Y1 升级版）

用户明确：**不要运行时兜底**。将 Y3（双保险）改为 **Y1-Plus**：纯编译期，但覆盖更全面。

###### Y1-Plus 设计

**核心原则**：凡是标记为 `@service` 的模块，编译器**不生成任何 yield 相关字节码**。运行时零额外检查。

| 检查项 | 编译器行为 | 示例 |
|--------|-----------|------|
| 直接 yield | ❌ 编译错误 | `func get_hp() { yield; }` → error |
| yield 在循环中 | ❌ 编译错误 | `while x { yield; }` → error |
| yield 在嵌套块中 | ❌ 编译错误 | `if cond { yield; }` → error |
| 调用可能 yield 的函数 | ❌ 编译错误 | `func a() { b(); } func b() { yield; }` → error |
| 调用非服务模块函数 | ✅ 允许（本模块内） | `func a() { b(); } func b() { return 1; }` → ok |

**关键**：第 4 项需要**跨函数分析** — 编译器需要标记每个函数是否"可能 yield"（taint analysis）：

```
编译器 yield-taint 分析算法：
1. 扫描所有函数，标记直接包含 yield 的函数为 "yields"
2. 传递闭包：如果函数 A 调用函数 B，且 B 标记为 "yields"，则 A 也标记为 "yields"
3. 对 @service 模块：所有函数必须为 "non-yields"，否则报编译错误
```

**实现复杂度**：

| 组件 | 改动量 | 说明 |
|------|--------|------|
| 模块属性解析 | ~5 行 | Parser 识别 `@service` 模块装饰器 |
| 函数 yield-taint 标记 | ~20 行 | 两遍扫描（直接标记 + 传递闭包） |
| 编译期错误检查 | ~10 行 | `@service` 模块中函数有 yield-taint → 报错 |
| 运行时改动 | **零** | 不生成 YIELD opcode = 运行时无需检查 |

**与 UI 的关系**：用户提到"包括 UI"。此处理解为 **IDE/LSP 层面也要给出诊断**：
- LSP（已有 179 个 LSP 测试）可复用编译器的 yield-taint 分析
- 在编辑器中实时标红 `yield` 出现在 `@service` 模块中的位置
- 零运行时开销，纯静态分析

**决策变更**：

| R21 决策 | R22 更新 | 原因 |
|---------|---------|------|
| Y3 双保险（编译期 + 运行时） | **Y1-Plus 纯编译期** | 用户明确不要运行时负担 |
| `@service` + InXCall flag | **仅 `@service`** | 去掉运行时 flag 检查 |

##### 回应 2：直接瞄准语法糖终极形态

用户选择 **C-2 语法糖 `svc.func()`** 作为目标。

这意味着函数索引 vs 名称的问题自然解决：

| 调用方式 | 编译器生成 | 用户感知 |
|---------|-----------|---------|
| `svc.get_frame()` | 编译为 `XCALL instanceId, funcIndex` | 用户写点号调用，编译器负责解析 |

编译器内部流程：
```
svc.get_frame()
→ Parser 识别 "变量.标识符(参数)" 模式
→ 查找 svc 的类型信息（服务脚本模块）
→ 在目标模块的导出函数表中查找 "get_frame" → index 0
→ 生成 XCALL svc_reg, 0, argCount
```

**分阶段不变**，但心智模型变了：

| 阶段 | 对外呈现 | 内部实现 |
|------|---------|---------|
| C-0 | `SvcCall(id, 0, args)` Syscall | 纯 C# 桥接 |
| C-1 | `call(svc, FN_xxx, args)` | XCALL OpCode |
| **C-2（目标）** | **`svc.get_frame()`** | 编译器语法糖 → XCALL |
| C-3 | `@service("owner")` 自动注册 | 编译器 + VM |

用户明确终极形态是 C-2，那么 C-0/C-1 是**内部实现细节**，对脚本作者不可见。设计文档和 API 应以 C-2 为目标描述。

##### 回应 3：XCALL 优化方向系统性分析

用户要求：即使内联看起来理想，也要**走完形式**分析所有方向，避免遗漏。下面列举所有可考虑的优化方向，每个给出可行性评估和退化路径。

###### 优化方向总览

| 编号 | 方向 | 原理 | 预期收益 | 风险 | 退化方案 |
|------|------|------|---------|------|---------|
| **O1** | 基线 XCALL | 保存/恢复 + 参数复制 + 实例切换 | 基线 ~15 ns | 零（最保守） | — |
| **O2** | 参数复制优化 | r0-rN memcpy 替代逐个复制 | -3~5 ns | 极低 | 退化到逐个复制 |
| **O3** | 零拷贝参数（共享 scratch zone） | 调用方和被调方共享 r0-r15 | -8 ns | ⭐⭐ 中 — 需证明无别名冲突 | 退化到 O1 拷贝 |
| **O4** | 内联展开 | 编译期将服务函数体嵌入调用方 | 消除 XCALL 开销 ~15 ns → 0 | ⭐⭐ 中 — 跨模块编译 | 退化到 O1 |
| **O5** | 实例切换优化 | 缓存最近使用的实例引用 | -2 ns | 低 | 退化到每次查 Pool |
| **O6** | 批量调用合并 | 同一帧对同一服务的多次调用合并为一次 | 减少切换次数 | ⭐⭐⭐ 高 — 语义变化 | 不退化（不做） |
| **O7** | 编译期常量折叠 | `svc.get_max_hp()` 如果返回常量 → 编译期内联为常量 | 消除整个调用 | 低 | 退化到 O1 |

###### 各方向详细分析

**O1 基线 XCALL（必做 ✅）**

这是 C-1 阶段的标准实现，作为所有优化的退化基线：
```
保存 CallerState(IP, RegBase, InstanceId) → 3 writes ~3 ns
复制参数 r0-rN → N × 8B copies ~2-8 ns
切换实例 inst = Pool[targetId] → 1 deref ~1 ns
执行目标函数 → 变化
复制返回值 r0 → 1 copy ~1 ns
恢复 CallerState → 3 reads + 1 switch ~4 ns
总计 ~12-18 ns
```

**结论**：基线已经足够好。后续优化都是锦上添花。

**O2 参数复制优化（值得做 ✅）**

当前 FFVM 参数通过 scratch zone r0-r15 传递。XCALL 需要复制调用方的 r0-rN 到目标的 r0-rN。

优化方式：使用 `Buffer.BlockCopy` 或 `Unsafe.CopyBlock` 代替逐字段复制。

```csharp
// 逐个复制（基线）
for (int i = 0; i < argCount; i++)
    target.Regs[i] = caller.Regs[i];

// 批量复制（O2）
Unsafe.CopyBlock(ref target.Regs[0], ref caller.Regs[0], (uint)(argCount * 8));
```

| 参数数量 | 逐个复制 | 批量复制 | 节省 |
|---------|---------|---------|------|
| 1-2 | ~2 ns | ~2 ns | ≈ 0 |
| 4-6 | ~5 ns | ~3 ns | ~2 ns |
| 8+ | ~8 ns | ~3 ns | ~5 ns |

**退化**：如果 Unsafe 不可用（netstandard2.1 限制），退化到逐个复制（O1）。
**结论**：低风险，值得在 C-1 阶段就做。

**O3 零拷贝参数 — 共享 scratch zone（暂不做 ⚠️）**

原理：如果调用方和被调方的 scratch zone 是同一块物理内存，参数无需复制。

前提：需要证明**在 XCALL 期间，调用方不会读写 scratch zone**。这在当前架构下**不成立**：
- XCALL 恢复后，调用方可能需要读取自己 scratch zone 中的局部状态
- 被调方写 scratch zone（返回值 r0）会覆盖调用方的值

**退化**：完全不做，退化到 O1/O2。
**结论**：风险远超收益（~8 ns 节省 vs 潜在别名 bug）。**不推荐。**

**O4 内联展开（远期目标 ⭐）**

原理：编译期将服务函数体直接嵌入调用方的字节码中，消除所有 XCALL 开销。

```ffs
// 编译前
var frame = svc.get_frame()

// 内联展开后（编译器生成）
var frame = _Owner_GetFrame()   // 直接调用底层 Syscall
```

**前提条件**：
1. 编译器能访问服务脚本的 AST/字节码（跨模块编译）
2. 服务函数体足够小（内联阈值：如 < 10 条指令）
3. 服务函数无副作用或副作用可控
4. 服务函数内不引用服务模块的 module variables（否则寄存器映射冲突）

**条件 4 是关键障碍**：服务脚本可能有自己的 module variables（如 `var cached_hp`）。内联后这些变量的寄存器地址属于服务实例，不属于调用方。

解决方案：
- 限制：只内联**无模块变量依赖**的纯函数
- 或：内联时将模块变量访问转为 LOAD_MVAR/STORE_MVAR 指向目标实例（复杂）

**退化**：不满足内联条件时，退化到 O1 基线 XCALL。自动退化完全安全。

**自动退化策略**（用户要求"稳定前提下性能第一"）：
```
编译器内联决策：
1. 目标函数 < 内联阈值（如 10 条指令）？→ 否则退化
2. 目标函数无 module variable 访问？→ 否则退化
3. 目标函数无间接调用（不调用其他服务函数）？→ 否则退化
4. 满足全部条件 → 内联
5. 任一不满足 → 退化到 O1 XCALL（零风险）
```

**结论**：理想的终极优化，但实现复杂度高，适合 C-3 或更远期。退化路径安全。

**O5 实例切换优化（自然优化 ✅）**

当前 XCALL 每次通过 `Pool.Instances[targetId]` 查找目标实例。如果同一帧多次调用同一服务，可以缓存引用。

```csharp
// 基线
ref var target = ref Pool.Instances[targetId];

// 优化：如果 targetId == lastTargetId，复用 ref
// 但 ref struct 不能缓存跨调用...实际上 C# 编译器可能已经优化了
```

**结论**：收益极小（~1-2 ns），且 C# JIT 可能已自动完成。**不主动做，依赖 JIT。**

**O6 批量调用合并（不做 ❌）**

原理：同一帧对 `owner.get_frame()` 和 `owner.get_hp()` 合并为一次切换。

问题：语义不等价 — 两次调用之间可能有其他状态变化。这会改变程序行为。

**结论**：语义风险不可接受，**不做。**

**O7 编译期常量折叠（条件性 ✅）**

如果服务函数返回编译期可知的常量：
```ffs
// owner_service.ffs
func get_max_hp() { return 1000; }
```

编译器可以直接将 `svc.get_max_hp()` 替换为常量 `1000`。

**前提**：编译器能访问服务脚本源码（跨模块编译），且能证明函数是纯常量函数。

**退化**：不确定时，退化到 O1。安全。

###### 优化路线图

按"稳定前提下性能第一"原则排序：

| 阶段 | 优化 | 理由 |
|------|------|------|
| **C-1** | O1 基线 + O2 参数批量复制 | 零风险，基础性能保障 |
| **C-2** | O7 常量折叠（有跨模块编译时） | 低风险，编译器自然支持 |
| **C-3+** | O4 函数内联（条件性） | 高收益但需跨模块分析，退化安全 |
| **不做** | O3 零拷贝, O5 缓存, O6 合并 | 风险/收益不匹配 |

**用户关心的"自动退化"**：所有优化项（O2/O4/O7）都有清晰的退化路径回 O1 基线。退化判定在编译期完成，**运行时零决策开销**。这与 Y1-Plus 的"纯编译期"理念一致。

##### 回应 4：当前方案与 L4/L5 的"正教性"

用户问了一个架构层面的关键问题：当前 XCALL 方案是否是通往 L4/L5 的正统路径？

###### L4/L5 回顾

| 层级 | 定义 | 本质 |
|------|------|------|
| **L4** | 跨模块共享变量 | 实例 A 读/写实例 B 的 module variable |
| **L5** | 跨模块函数调用 | 实例 A 调用实例 B 的函数 |

###### XCALL 是 L5 的正教实现吗？

**是的。** XCALL 就是 L5 的精确实现：

| L5 要素 | XCALL 如何满足 |
|---------|---------------|
| 调用者知道目标 | instanceId 引用 ✅ |
| 调用特定函数 | 函数索引 ✅ |
| 传递参数 | scratch zone r0-rN ✅ |
| 获取返回值 | r0 ✅ |
| 同步执行 | 无 yield 保证 ✅ |
| 确定性（回放安全） | 指令流决定调用顺序 ✅ |

XCALL 不是 L5 的"近似"或"妥协"——它**就是** L5。

###### L4 如何实现？两条路径

**路径 A：通过 L5 间接实现（当前方案已支持 ✅）**

```ffs
// 通过服务函数间接读写
var hp = svc.get_hp()           // L5 调用 → 读取 L4 变量
svc.set_hp(hp - damage)         // L5 调用 → 写入 L4 变量
```

优点：零额外 VM 改动，完全复用 XCALL 基础设施。
缺点：每次读/写都经过函数调用开销（~15 ns）。

**路径 B：直接跨实例寄存器访问（真 L4）**

```ffs
// 假设的 L4 语法
var hp = svc.hp                 // 直接读取目标实例的 module variable
svc.hp = svc.hp - damage        // 直接写入
```

需要新 OpCode：
```
XLOAD_MVAR  dest, instanceId, mvarIndex   // 读取目标实例的 module variable
XSTORE_MVAR src, instanceId, mvarIndex    // 写入目标实例的 module variable
```

优点：跳过函数调用开销，直接读/写 ~3-5 ns。
缺点：需要新 OpCode + 编译器支持 + 安全性检查（可变性控制）。

###### L4/L5 实现时是否需要纳入考虑？

| 考虑项 | 结论 | 原因 |
|--------|------|------|
| XCALL 指令设计 | ✅ 无需改动 | XCALL 就是 L5，设计天然兼容 |
| 编译器 `@service` | ✅ 无需改动 | L4/L5 是独立模块属性，可并存 |
| 实例引用（instanceId） | ✅ 无需改动 | L4 和 L5 共享同一引用机制 |
| 语法糖 `svc.func()` | ⚠️ **需预留** | `svc.var` 和 `svc.func()` 语法需区分（有无括号） |
| 导出声明 | ⚠️ **需预留** | L4 需要 `@export var hp` 声明哪些变量可跨实例访问 |
| OpCode 空间 | ✅ 够用 | 当前 OpCode < 52，256 空间充裕 |

**关键预留**：

1. **语法预留**：`svc.xxx` 当 xxx 后面有 `()` 时是函数调用（L5），没有 `()` 时是变量访问（L4）。编译器的 Parser 需要预留这个区分。当前 C-2 语法糖设计时可以直接考虑这个分支。

2. **导出声明预留**：`@export var hp` 或类似声明，让编译器知道哪些 module variable 可以被外部读/写。这与 `@service` 是不同的模块属性，不冲突。

3. **安全性预留**：L4 直接访问目标寄存器需要思考：
   - 只读 vs 读写？（推荐默认只读，`@export var` 只读，`@export mut var` 可写）
   - 并发安全？（FFVM 单线程，不存在此问题 ✅）

###### 正教性结论

```
当前 XCALL 方案
    │
    ├── 就是 L5 ✅（精确实现，非近似）
    │
    ├── L4 可通过 L5 间接实现 ✅（零额外改动）
    │
    ├── 未来真 L4 只需新增 XLOAD_MVAR/XSTORE_MVAR
    │   └── 与 XCALL 完全独立，不冲突
    │
    └── 语法糖需预留 svc.var vs svc.func() 区分
        └── C-2 设计时即可纳入
```

**回答用户的问题**：

1. **是否正教？** — **是**。XCALL = L5 精确实现。不是绕路、不是近似、不是临时方案。
2. **L4/L5 实现时是否需要纳入？** — L5 已被 XCALL 覆盖。L4（如果需要真 L4）只需新增 2 个 OpCode，与 XCALL 独立。**唯一需要在 C-2 阶段预留的是 `svc.var` vs `svc.func()` 的语法区分**。

##### 更新后的收敛决策表

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — **纯编译期保证 Y1-Plus**（无运行时负担） | R22 ✅ |
| 调用语法 | **直接瞄准 C-2 语法糖 `svc.func()`** | R22 ✅ |
| 实现路径 | C-0 → C-1 → C-2 → C-3 分阶段（C-2 为目标形态） | R20-22 |
| 嵌套调用 | 允许，最大深度 4 层 | R21 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算） | R20-21 ✅ |
| XCALL 优化 | O1基线 + O2参数优化（C-1），O7常量折叠（C-2），O4内联（C-3+） | R22 ✅ |
| 优化退化策略 | 编译期自动退化到 O1 基线，运行时零决策开销 | R22 ✅ |
| L4/L5 正教性 | XCALL = L5 精确实现；L4 可通过 L5 间接实现或未来新增 OpCode | R22 ✅ |
| 语法预留 | C-2 需预留 `svc.var`（L4）vs `svc.func()`（L5）区分 | R22 ✅ |

##### 待用户确认（第 22 轮）

> **用户回复（第 23 轮）**：
> 1. Y1-Plus OK ✅
> 2. XCALL 优化路线图 OK ✅
> 3. L4 需要更早考虑，不能推迟到 C-2。直觉是 **L4 和 XCALL 至少同一基线**，但可能有更有效的自动优化方案
> 4. 等 3 解决后再定

1. **Y1-Plus 纯编译期 yield 禁止是否满意？** — 包含跨函数 yield-taint 分析 + LSP 实时诊断，运行时零负担
2. **XCALL 优化路线图（O1→O2→O7→O4）是否符合预期？** — 退化策略："编译期自动退化，运行时零决策"
3. **L4 语法预留：`svc.var` vs `svc.func()` 在 C-2 阶段预留是否 OK？** — 还是需要更早考虑？
4. **Q4 现在是否可以认为已基本收敛？** — 如果是，下一步可以输出设计文档（XCALL spec + 编译器改动点 + 分阶段实施计划）

---

#### Q4-E：L4/XCALL 统一基线设计 + 自动优化方案分析（第 23 轮）

> 用户关键观点：L4（跨实例变量访问）不应推迟到 C-2，而应**和 XCALL（L5）共享同一基线设计**。且可能存在比"XLOAD_MVAR 独立 OpCode"更有效的自动优化方案。

用户的直觉非常敏锐。让我们重新审视 L4/L5 的关系，从"统一基线"视角设计。

##### 为什么 L4 应该和 XCALL 同基线？

R22 中我将 L4 定位为"远期可选"，理由是 L5（函数调用）可以间接实现 L4（变量读写）。但用户指出这个定位有问题：

| R22 思路（分离） | 用户直觉（统一） |
|-----------------|----------------|
| L5 先做（XCALL），L4 后补（XLOAD_MVAR） | L4/L5 同时设计，共享基础设施 |
| `svc.func()` 是核心，`svc.var` 是衍生 | 两者都是"跨实例成员访问"，本质相同 |
| L4 通过 L5 间接实现（getter/setter） | 间接实现是退化方案，不是正教 |

**为什么统一更好？**

1. **概念一致性**：`svc.xxx` 无论是函数还是变量，对脚本作者来说都是"访问服务的成员"。如果函数调用有专门优化（XCALL），变量访问却只能走 getter/setter（间接 XCALL），这对用户是一个不必要的认知割裂。

2. **性能对称性**：`svc.get_hp()` 走 XCALL ~15 ns，但 `svc.hp` 如果也走 XCALL（因为没有直接路径），开销完全相同。然而变量访问**应该比函数调用更快**——这是所有编程语言的基本直觉。

3. **编译器优化空间**：如果 L4/L5 共享基线，编译器可以做更多自动优化（下面详述）。

##### L4/XCALL 统一基线设计

###### 统一模型：跨实例成员访问（Cross-Instance Member Access, XIMA）

将 L4/L5 统一为一个抽象：

```
svc.member        → XIMA 读取（可能是变量或无参函数）
svc.member = val  → XIMA 写入（可能是变量或有副作用的 setter）
svc.member(args)  → XIMA 调用（带参函数）
```

**编译器统一解析**：

```
svc.xxx
  ├── xxx 是导出变量 (@export var) → L4 路径
  │     ├── 读取 → XLOAD_MVAR
  │     └── 写入 → XSTORE_MVAR
  ├── xxx 是导出函数 → L5 路径
  │     └── 调用 → XCALL
  └── xxx 是只读属性 (编译器推断) → 自动优化路径
        └── 可能内联/折叠
```

**关键**：Parser 不需要区分 `svc.var` 和 `svc.func()` — 它只解析 `svc.member`，然后由**语义分析**阶段根据目标模块的导出表决定走哪条路径。这比"有无括号"的语法区分更自然。

###### OpCode 基线设计

同一基线意味着 L4 和 L5 的 OpCode 在 C-1 阶段就一起设计：

| OpCode | 操作 | 编码 | 开销 |
|--------|------|------|------|
| **XCALL** | 跨实例函数调用 | A=dest, B=instanceId_reg, C=funcIndex | ~15 ns |
| **XLOAD_MVAR** | 跨实例变量读取 | A=dest, B=instanceId_reg, C=mvarIndex | ~3-5 ns |
| **XSTORE_MVAR** | 跨实例变量写入 | A=src, B=instanceId_reg, C=mvarIndex | ~3-5 ns |

三个 OpCode 共享相同的 B 操作数语义（instanceId 引用）和相同的实例查找逻辑。

**XLOAD_MVAR 实现原理**（~3-5 ns）：

```csharp
case OpCode.XLOAD_MVAR:
{
    int targetInst = (int)regs[Reg(op.B)].AsLong;
    int mvarIdx = op.C;
    // 直接读取目标实例的 module variable 寄存器
    ref var targetRegs = ref pool.Instances[targetInst].Regs;
    regs[Reg(op.A)] = targetRegs.Raw[VMConstants.ModuleVarRegBase + mvarIdx];
    break;
}
```

对比 XCALL 走 getter：
```csharp
case OpCode.XCALL:
{
    // 保存调用者状态 ~3 ns
    // 复制参数 ~2-8 ns
    // 切换实例 ~1 ns
    // 执行 get_hp() 函数体 ~5 ns
    // 复制返回值 ~1 ns
    // 恢复状态 ~4 ns
    // 总计 ~15 ns
}
```

**性能对比**：XLOAD_MVAR 比 XCALL getter 快 **3-5×**。这不是微优化——当角色每帧需要读取多个服务属性时（如 hp, mp, frame, state），差异累积显著。

##### 自动优化方案分析

用户提到"可能有更有效的自动优化方案"。让我系统分析编译器可以做的自动优化：

###### A1：自动 L5→L4 退化（编译器自动推断）

**核心思想**：如果用户写 `svc.get_hp()`，但 `get_hp()` 的函数体只是 `return hp;`（单纯的 getter），编译器可以**自动将 XCALL 替换为 XLOAD_MVAR**。

```ffs
// 服务脚本
@export func get_hp() { return hp; }   // 纯 getter

// 调用方写
var hp = svc.get_hp()

// 编译器自动优化：
// 检测 get_hp() 是纯 getter → 替换为直接变量读取
// 生成 XLOAD_MVAR 而非 XCALL
```

**可行性分析**：

| 条件 | 检查方式 | 复杂度 |
|------|---------|--------|
| 函数体只有一条 return 语句 | AST 检查 | 低 |
| return 表达式是单个 module variable | AST 检查 | 低 |
| 函数无副作用 | 已被 return-only 保证 | — |
| 目标模块可被编译器访问 | 跨模块编译（需要） | 中 |

**退化条件**：如果不满足上述任一条件，退化到 XCALL。安全。

这是非常有效的优化：用户可以**只写 `svc.get_hp()`** 风格的代码（统一的函数调用接口），编译器自动检测哪些是纯 getter 并替换为直接变量访问。**用户不需要知道 L4/L5 的区别。**

###### A2：自动 setter 识别与 L4 退化

类似地，`svc.set_hp(val)` 如果函数体只是 `hp = val;`：

```ffs
// 编译器自动检测：
svc.set_hp(100)
→ 检测 set_hp(v) 函数体为 { hp = v; }
→ 替换为 XSTORE_MVAR
```

###### A3：属性语法糖（Property Sugar）

将 A1+A2 结合，提供属性风格的语法糖：

```ffs
// 用户写（属性风格）
var hp = svc.hp         // 自动映射到 get_hp() 或 XLOAD_MVAR
svc.hp = 100            // 自动映射到 set_hp(100) 或 XSTORE_MVAR

// 或者用户也可以写（函数风格）
var hp = svc.get_hp()   // 编译器自动退化到 XLOAD_MVAR（如果是纯 getter）
svc.set_hp(100)         // 编译器自动退化到 XSTORE_MVAR（如果是纯 setter）
```

**关键**：两种写法**生成完全相同的字节码**。编译器统一优化。

###### A4：批量访问优化（Batch Access）

当同一帧内对同一服务读取多个属性时：

```ffs
var hp = svc.hp
var mp = svc.mp
var frame = svc.frame
```

编译器可以合并实例查找：

```
// 未优化：3 次 XLOAD_MVAR（各含 1 次 pool.Instances[id] 查找）
XLOAD_MVAR r1, r_svc, 0   // 查找实例 + 读 hp
XLOAD_MVAR r2, r_svc, 1   // 查找实例 + 读 mp
XLOAD_MVAR r3, r_svc, 2   // 查找实例 + 读 frame

// 优化：VM 运行时已经缓存了 svc 的引用（r_svc 就是 instanceId）
// 实际上单次 XLOAD_MVAR 就是直接索引，不需要额外合并
// 因为 pool.Instances[id] 已经是 O(1) 数组访问
```

结论：XLOAD_MVAR 已经足够快（~3 ns），批量优化收益极小。**不需要专门做。**

###### A5：内联+折叠级联优化

最激进的优化：如果 `svc.get_combo_modifier()` 的函数体是：

```ffs
func get_combo_modifier() {
    return base_modifier + combo_count * 0.1;
}
```

编译器可以将整个表达式内联并用 XLOAD_MVAR 替换变量引用：

```
// 内联前
XCALL r0, r_svc, FN_GET_COMBO_MODIFIER

// 内联后
XLOAD_MVAR r_tmp1, r_svc, IDX_base_modifier     // 读 base_modifier
XLOAD_MVAR r_tmp2, r_svc, IDX_combo_count        // 读 combo_count
MUL r_tmp3, r_tmp2, r_const_0_1                   // combo_count * 0.1
ADD r0, r_tmp1, r_tmp3                            // base_modifier + ...
```

这将一次 XCALL（~15 ns）替换为 2 次 XLOAD_MVAR + 2 次算术（~6+2 = ~8 ns），快了近一倍。

**退化条件**：函数包含分支/循环/递归/yield → 不内联，退化到 XCALL。

##### 自动优化方案总结

| 编号 | 方案 | 原理 | 收益 | 前提 | 退化 |
|------|------|------|------|------|------|
| **A1** | 自动 getter→XLOAD | 纯 getter 函数替换为直接读 | ~15→3 ns | 跨模块 AST 访问 | XCALL |
| **A2** | 自动 setter→XSTORE | 纯 setter 函数替换为直接写 | ~15→3 ns | 跨模块 AST 访问 | XCALL |
| **A3** | 属性语法糖 | `svc.hp` = 编译器自动路由 | 统一用户体验 | A1/A2 | 函数调用 |
| **A4** | 批量访问合并 | 多次读取合并实例查找 | 极小 | — | 不做 |
| **A5** | 内联+替换 | 函数体内联，变量引用→XLOAD | ~15→8 ns | 跨模块+内联条件 | XCALL |

**用户提到的"更有效的自动优化方案"就是 A1/A2**：编译器自动检测纯 getter/setter 并替换为直接变量访问。这比我在 R22 中列的 O4（函数内联）更精准、更简单、收益更大。

##### 修订后的分阶段设计

将 L4 提前到与 XCALL 同基线：

| 阶段 | 内容 | 变化 |
|------|------|------|
| **C-0** | Syscall 桥接原型 | 不变 |
| **C-1** | XCALL + XLOAD_MVAR + XSTORE_MVAR | ⚡ **L4 提前到 C-1** |
| **C-1.5** | A1/A2 自动 getter/setter 退化 | ⚡ **新增：编译器自动优化** |
| **C-2** | `svc.member` 统一语法糖 | 简化：不再需要区分 var/func |
| **C-3** | A5 内联+替换（条件性） | 原 O4，收窄范围 |

**关键变化**：

1. **C-1 同时实现 XCALL + XLOAD_MVAR + XSTORE_MVAR**（3 个 OpCode 共享基础设施）
2. **C-1.5 新增 A1/A2 自动优化**（编译器自动将纯 getter/setter 退化为直接变量访问）
3. **C-2 的语法糖变得更简单**：`svc.member` 统一解析，编译器根据导出表自动路由。不需要"有无括号"来区分 L4/L5 — 编译器全部自动处理

##### `@export` 声明设计

L4 需要模块声明哪些变量可被外部访问：

```ffs
// owner_service.ffs
@service
@export var hp = 1000         // 可被外部读写
@export const max_hp = 1000   // 可被外部读取（只读）
var internal_state = 0        // 不可被外部访问

@export func get_frame() { ... }  // 可被外部调用
func internal_helper() { ... }    // 不可被外部调用
```

**`@export` 规则**：

| 声明 | 外部可读 | 外部可写 | 导出表类型 |
|------|---------|---------|-----------|
| `@export var x` | ✅ | ✅ | L4 变量 |
| `@export const x` | ✅ | ❌（编译错误） | L4 常量 |
| `@export func f()` | ✅ 可调用 | — | L5 函数 |
| `var x`（无 @export） | ❌（编译错误） | ❌ | 不导出 |
| `func f()`（无 @export） | ❌（编译错误） | — | 不导出 |

**安全性**：
- 未导出的成员在跨实例访问时编译错误
- `@export const` 的写入在编译期报错
- 运行时无额外检查（编译期已保证安全）

##### 编译器导出表

每个 `@service` 模块在编译时生成一个**导出表（Export Table）**：

```
ExportTable {
    vars: [
        { name: "hp", mvarIndex: 0, writable: true },
        { name: "max_hp", mvarIndex: 1, writable: false }
    ],
    funcs: [
        { name: "get_frame", funcIndex: 0 },
        { name: "take_damage", funcIndex: 1 }
    ]
}
```

调用方编译器通过这个导出表解析 `svc.member`：
- `svc.hp` → 查表 → vars[0] → XLOAD_MVAR/XSTORE_MVAR
- `svc.get_frame()` → 查表 → funcs[0] → XCALL
- `svc.get_hp()` → 查表 → funcs → 检测是否纯 getter → A1 自动退化

##### 与 R22 的对比

| 方面 | R22 设计 | R23 统一设计 |
|------|---------|------------|
| L4 时机 | C-2 预留，远期做 | **C-1 同基线** |
| L4/L5 关系 | 独立的 OpCode 族 | **共享基础设施** |
| 语法区分 | `svc.var` vs `svc.func()` 靠括号 | **`svc.member` 统一，编译器自动路由** |
| 优化策略 | O1→O2→O7→O4 独立方向 | **A1/A2 自动退化 + O 系列优化** |
| 用户心智 | 需知道 L4 和 L5 的区别 | **不需要——统一的 `svc.member` 语法** |
| @export | 未设计 | **C-1 即支持** |

##### 更新后的收敛决策表

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | **`svc.member` 统一语法**，编译器自动路由 L4/L5 | R23 ✅ |
| L4/L5 关系 | **同基线设计**：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | `@export var/const/func` — C-1 即支持 | R23 ✅ |
| 自动优化 | **A1/A2 自动 getter/setter→直接访问退化**（C-1.5） | R23 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(自动优化) → C-2(语法糖) → C-3(内联) | R23 ✅ |
| 嵌套调用 | 允许，最大深度 4 层 | R21 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算） | R20-21 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7（C-2），O4/A5（C-3+） | R22-23 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

##### 待用户确认（第 23 轮）

> **用户回复（第 24 轮）**：
> 1. OK ✅
> 2. 是的，想到一块去了 ✅
> 3. OK — 确认用户使用 `m.property`, `m.field`, `m.method()`, `m.function()`，但不区分底层机制
> 4. 略微倾向 `public`（从使用便捷度考虑，低权重）。但认识到 `@export` 和 `@service` 保持一致性
> 5. **未收敛** — 希望支持用户主动请求内联优化时，提供编译器辅助途径

1. **L4/XCALL 同基线（C-1 同时实现 3 个 OpCode）是否符合预期？** — XCALL + XLOAD_MVAR + XSTORE_MVAR 共享实例查找基础设施
2. **A1/A2 自动 getter/setter 退化是否就是你说的"更有效的自动优化方案"？** — 编译器自动检测纯 getter/setter 并替换为直接变量访问
3. **`svc.member` 统一语法（不靠括号区分）是否更好？** — 编译器通过导出表自动路由 L4/L5
4. **`@export` 声明设计是否 OK？** — `@export var`（读写）、`@export const`（只读）、`@export func`（可调用）
5. **Q4 现在是否可以认为已基本收敛？** — 如果是，下一步输出设计文档

---

#### Q4-F：统一语法确认 + @export/public 命名 + 用户引导内联优化（第 24 轮）

##### Q3 确认：统一成员访问语法

用户确认的正是我们的设计意图：

```ffs
// 用户视角 — 统一的 svc.member 语法
var hp = svc.hp             // 可能是 property, 可能是 field — 用户不关心
var frame = svc.frame       // 同上
svc.take_damage(50)         // method 调用
var result = svc.calc(a,b)  // function 调用
```

**底层机制对用户完全透明**：

| 用户写法 | 底层可能路径 | 用户需要知道？ |
|---------|------------|-------------|
| `svc.hp` | XLOAD_MVAR（直接读变量） | ❌ |
| `svc.hp = 100` | XSTORE_MVAR（直接写变量） | ❌ |
| `svc.get_hp()` | A1 自动退化 → XLOAD_MVAR | ❌ |
| `svc.take_damage(50)` | XCALL（函数调用） | ❌ |
| `svc.calc(a, b)` | XCALL 或 A5 内联（如果编译器判定可内联） | ❌ |

**编译器通过导出表自动决策**，用户只需写自然的成员访问语法。这正是 **Q3 ✅ 确认**。

##### Q4：`@export` vs `public` 命名分析

用户提出了一个有价值的命名考虑。让我们分析：

| 候选 | 优点 | 缺点 |
|------|------|------|
| `@export` | 与 `@service` 一致（都是 `@` 前缀模块属性）；语义精确（"导出给外部"） | 较长（7 字符）；对 C#/Unity 用户可能不如 `public` 直觉 |
| `public` | 短（6 字符）；C#/Java/TS 用户极其熟悉；使用便捷 | 与 `@service` 风格不一致（一个有 `@`，一个没有）；在 FFS 中可能暗示类/OOP 的访问控制语义 |
| `@public` | 与 `@service` 一致；熟悉度兼顾 | 不常见；混合了两种风格 |

**分析**：

1. **一致性维度**：`@export` 和 `@service` 都是"模块级声明属性"，用 `@` 前缀表示"这是给编译器的元数据指令"。如果用 `public`（无 `@`），就变成了"关键字"而非"属性"，语法地位不同。

2. **语义维度**：`public` 在 OOP 语言中表示"类成员访问控制"（public/private/protected）。但 FFS 没有类，只有模块。用 `public` 可能暗示未来会有 `private`/`protected`，而实际上 FFS 的设计是"不加 `@export` 就是不导出"——二元选择，不需要完整的访问控制体系。

3. **用户便捷度维度**：`public` 确实更短更熟悉。但在 FFS 脚本中，`@export` 声明通常只写一次（在服务模块定义时），不是高频输入。

**结论**：保持 `@export`，原因是一致性和语义精确性。但这是一个**可逆的低风险决策**——如果在实际使用中发现 `public` 更顺手，C-2 语法糖阶段可以引入 `public` 作为 `@export` 的别名。

```ffs
// C-1：@export（规范形式）
@service
@export var hp = 1000
@export func take_damage(d) { hp = hp - d; }

// C-2 可选：如果社区反馈强烈，public 作为 @export 别名
@service
public var hp = 1000
public func take_damage(d) { hp = hp - d; }
```

##### Q5：用户引导的内联优化（User-Directed Inline Hints）

这是用户提出的新需求，非常有价值。当前 A5 内联是**纯自动**的（编译器自行判断），但用户说：**如果用户意识到自己想做内联优化，应该提供编译器辅助途径。**

这里有两个层面：

###### 层面 1：用户请求内联（Inline Hint）

用户可以通过提示告诉编译器："我认为这个调用应该内联"：

```ffs
// 方案 H1：@inline 属性（标记在函数声明上）
@export @inline func get_combo_modifier() {
    return base_modifier + combo_count * 0.1;
}

// 方案 H2：调用点 inline 提示（标记在调用处）
var mod = @inline svc.get_combo_modifier()

// 方案 H3：编译器 pragma（文件级或块级）
@pragma inline_threshold(20)  // 提高内联阈值到 20 条指令
```

**推荐 H1**（声明侧标记），原因：

| 方案 | 优点 | 缺点 |
|------|------|------|
| **H1 @inline** | 声明一次、所有调用点生效；与 @export 语法一致 | 不能对特定调用点差异化 |
| H2 调用点 | 精确控制每个调用点 | 每个调用处都要写；跨模块需知道内部实现 |
| H3 pragma | 全局控制简单 | 不够精确；影响范围不可控 |

**H1 语义**：

```ffs
@export @inline func get_combo_modifier() {
    return base_modifier + combo_count * 0.1;
}
```

`@inline` 告诉编译器：
- **请尝试在调用点内联此函数**（Best-effort hint，不是强制指令）
- 如果函数体过大/包含循环/递归/yield → 编译器发出**警告**（不是错误），退化到 XCALL
- 如果函数体符合内联条件 → 编译器在调用点展开函数体，替换变量引用为 XLOAD_MVAR

###### 层面 2：编译器反馈（Inline Diagnostics）

用户请求内联后，编译器应该告诉用户**是否真的内联了**，以及**为什么没有**：

```
// 编译器输出（Verbose 模式或 LSP 诊断）
[INFO] svc.get_combo_modifier() → inlined (2 XLOAD_MVAR + 2 arithmetic)
[WARN] svc.complex_calc() → NOT inlined: function body has 25 instructions (threshold: 16)
[WARN] svc.recursive_func() → NOT inlined: function contains recursion
```

这正是 LSP 可以做的事情——**IDE 内实时显示内联决策**：

```ffs
// LSP hover 提示
var mod = svc.get_combo_modifier()
//        ^^^^^^^^^^^^^^^^^^^^^^^^
//        ✅ Inlined: 2 XLOAD_MVAR + MUL + ADD (estimated ~8 ns vs ~15 ns XCALL)

var result = svc.complex_calc(x, y)
//           ^^^^^^^^^^^^^^^^^^^^^^^^^^
//           ⚠️ Not inlined: 25 instructions exceed threshold (16).
//              Add @inline to force attempt, or increase threshold.
```

###### 层面 3：~~强制内联（Force Inline）~~ 已取消

> **设计变更**：`@force_inline` 关键字已取消。`@inline` 和 `@force_inline` 都不影响实际优化决策，区别仅在于诊断级别（warning vs error）。这种粒度差异不值得引入新关键字，内联失败的严格程度改由编译器配置控制（类似 VMConfig 的 `XCallDepthPolicy` 模式）。

~~对于高级用户，提供强制内联选项：~~

```ffs
// 已取消的设计（仅供历史参考）：
// @force_inline：编译器必须内联，否则编译错误
// @export @force_inline func get_hp() { return hp; }
//
// 替代方案：通过编译器配置 InlineFailurePolicy 控制 @inline 失败时的严格程度
// InlineFailurePolicy: Warn（默认）| Error（全局升级为编译错误）
```

###### 内联优化方案总结

| 机制 | 触发方式 | 编译器行为 | 失败处理 |
|------|---------|-----------|---------|
| **自动内联（A5）** | 编译器自动检测 | 条件满足→内联，否则 XCALL | 静默退化 |
| **@inline 提示** | 用户在函数声明标记 | 尝试内联 + 诊断反馈 | 警告 + 退化（可通过编译器配置升级为错误） |
| ~~**@force_inline**~~ | ~~用户强制要求~~ | ~~必须内联~~ | ~~**编译错误**~~（已取消） |
| **LSP 诊断** | IDE 实时显示 | 每个 XCALL 调用点显示内联决策 | 信息提示 |

###### 分阶段实现

| 阶段 | 内联机制 | 说明 |
|------|---------|------|
| C-1 | 无内联 | XCALL 基线 |
| C-1.5 | A1/A2 自动 getter/setter 退化 | 不需要 @inline（纯自动） |
| C-2 | `@inline` 提示 + LSP 诊断 | 用户引导 + 编译器反馈 |
| 远期 | A5 自动内联 | 函数体展开（`@force_inline` 已取消，严格程度由编译器配置控制） |

**关键设计原则**：
1. **渐进式**：不加 @inline 也能享受 A1/A2 自动优化；加了 @inline 获得更激进的优化 + 反馈
2. **安全退化**：@inline 是 hint 不是命令；失败严格程度由编译器配置控制（类似 VMConfig 模式）
3. **透明性**：LSP 让用户清楚看到每个调用点的内联决策，不是黑箱

##### 更新后的收敛决策表

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | `svc.member` 统一语法，编译器自动路由 L4/L5 | R23-24 ✅ |
| L4/L5 关系 | 同基线设计：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | `@export var/const/func` — C-1 即支持；`public` 可作为 C-2 别名 | R23-24 ✅ |
| 自动优化 | A1/A2 自动 getter/setter→直接访问退化（C-1.5） | R23 ✅ |
| 用户引导内联 | `@inline`（hint，C-2）+ LSP 诊断。`@force_inline` 已取消 | R24 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(A1/A2) → C-2(语法糖+@inline) → 远期(A5 深度内联) | R23-24 ✅ |
| 嵌套调用 | 允许，最大深度 4 层 | R21 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算） | R20-21 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7+@inline（C-2），O4/A5 深度内联（远期） | R22-24 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

##### 待用户确认（第 24 轮）

> **用户回复（第 25 轮）**：
> 1. OK ✅ — 统一语法确认
> 2. 使用 @export，放弃 public（比 R24 提议的"C-2 别名"更果断）
> 3. OK ✅ — @inline 分层设计确认
> 4. 等新疑惑 — 先解决嵌套深度问题
> **新疑惑**：最大嵌套 4 层中"4"的含义？考虑可配置的可能。可配置对性能的影响。可配置后过大消耗的警告。以及兜底处理（允许无限，性能退化）

1. **统一成员语法确认**：`svc.property`/`svc.field`/`svc.method()`/`svc.function()` — 底层机制透明，编译器自动路由。是否就是你 Q3 所确认的？
2. **@export 保持，public 作为 C-2 可选别名** — 是否接受这个折衷？
3. **@inline 提示 + LSP 诊断分层设计**是否满足"用户引导内联优化"的需求？（`@force_inline` 已取消）
4. **Q4 现在是否可以认为已基本收敛？** — 如果是，下一步输出 XCALL spec 设计文档

---

#### Q4-G：XCALL 嵌套深度 — 含义、可配置、性能、警告、兜底（第 25 轮）

##### R24 确认总结

| 问题 | 用户回复 | 结论 |
|------|---------|------|
| 统一语法 | OK | ✅ 锁定 |
| @export vs public | 使用 @export，放弃 public | ✅ 锁定（**比 R24 更强**：不保留 public 别名） |
| @inline 分层设计 | OK | ✅ 锁定 |
| Q4 是否收敛 | 等新疑惑 | 继续讨论 |

##### "最大嵌套 4 层" — 含义解析

**"4"的来源**：R21 提出跨实例递归调用深度限制 `max 4 层`，这是一个**保守的经验值**：

```
层 0: 业务脚本 A.step()           ← 不计入嵌套，这是调用起点
层 1: A → XCALL B.func()          ← 第 1 层嵌套
层 2: B → XCALL C.func()          ← 第 2 层嵌套
层 3: C → XCALL D.func()          ← 第 3 层嵌套
层 4: D → XCALL E.func()          ← 第 4 层嵌套（上限）
层 5: E → XCALL F.func()          ← ❌ 超出限制
```

**"4"不是什么**：
- ❌ 不是语言级 CALL 的递归深度（那由 VM 的 CallStack 大小控制，目前约 256 层）
- ❌ 不是同一实例内部函数调用的限制
- ✅ 只限制**跨实例 XCALL 链的深度**——A 调 B，B 调 C，C 调 D...

**为什么选 4？**
- KOF 格斗游戏中，典型的调用链是：技能脚本 → 伤害服务 → 属性服务。这是 2 层
- 3 层已经覆盖绝大多数合理的服务组合模式
- 4 层留了一层余量
- 超过 4 层通常意味着服务依赖设计有问题（过度解耦/间接调用链太深）

##### 可配置方案

用户提出的是一个很好的设计问题。让我们分析几种方案：

###### 方案 N1：编译期硬限制（当前方案）

```ffs
// 编译器内置 MAX_XCALL_DEPTH = 4
// 不可配置
```

| 优点 | 缺点 |
|------|------|
| 实现最简单 | 不灵活 |
| 编译器可以在编译期静态分析调用链深度 | 不同项目可能有不同需求 |
| 零运行时开销 | 需要改编译器才能调整 |

###### 方案 N2：宿主可配置 + 运行时检查

```csharp
// C# 宿主侧配置
var vmConfig = new VMConfig {
    MaxXCallDepth = 8  // 默认 4，可调
};
var vm = new VMWorld(vmConfig);
```

```ffs
// FFS 运行时，每次 XCALL 检查：
// if (currentXCallDepth >= maxXCallDepth) → 运行时错误
```

| 优点 | 缺点 |
|------|------|
| 灵活，宿主按项目需求配置 | 每次 XCALL 多一次深度检查（1 次 compare + branch） |
| 不需要改编译器 | 运行时才发现问题（而非编译期） |

**性能影响**：

| 操作 | 不配置 | 配置（N2） | 差异 |
|------|-------|-----------|------|
| XCALL 单次 overhead | ~15 ns | ~16 ns | +1 ns（一次 int compare + branch） |
| 每帧 50 次 XCALL | ~0.75 μs | ~0.80 μs | +0.05 μs |
| 占帧预算 | 0.005% | 0.005% | **几乎无差异** |

**结论**：运行时检查的性能开销可忽略（每次 XCALL +1 ns）。

###### 方案 N3：可配置 + 编译期静态分析 + 运行时兜底

这是最完整的方案，分三层：

**层 1：编译期静态分析（LSP 诊断）**

编译器通过分析导出表的调用依赖图，可以在编译期检测出**可确定的嵌套深度**：

```ffs
// service_a.ffs
@service
@export func step() {
    var dmg = damage_svc.calc_damage(10)  // 编译器知道这是 1 层 XCALL
}

// damage_service.ffs
@service
@export func calc_damage(base) {
    var mult = attr_svc.get_multiplier()  // 编译器知道这是 2 层
    return base * mult
}
```

LSP 诊断：
```
[INFO] step() → damage_svc.calc_damage() → attr_svc.get_multiplier()
       确定嵌套深度: 2 层 (限制: 4 层) ✅
       
[WARN] step() → a.f() → b.g() → c.h() → d.i() → e.j()
       确定嵌套深度: 5 层 (限制: 4 层) ⚠️ 超出限制
```

**层 2：运行时深度计数（兜底）**

对于编译期无法静态确定的调用链（如通过变量动态持有的服务引用），运行时维护一个 XCALL 深度计数器：

```csharp
// VMWorld 中
int _xcallDepth;       // 当前嵌套深度
int _maxXCallDepth;    // 配置的上限（默认 4）

// XCALL 指令执行时
case OpCode.XCALL:
    if (++_xcallDepth > _maxXCallDepth) {
        // 兜底处理 — 见下方策略
    }
    // ... 正常执行 XCALL ...
    // 返回时 --_xcallDepth
```

**层 3：兜底策略（超出限制时的处理）**

用户提到了两种思路，让我们分析：

| 策略 | 行为 | 适用场景 |
|------|------|---------|
| **B1 硬错误** | `_xcallDepth > max` → 运行时错误，终止脚本 | 生产环境，严格安全 |
| **B2 警告+继续** | `_xcallDepth > max` → 发出警告，但继续执行 | 开发调试阶段 |
| **B3 无限（退化）** | `max = 0` 表示不限制，允许任意深度 | 用户明确选择 |

**推荐组合**：

```csharp
var vmConfig = new VMConfig {
    MaxXCallDepth = 4,           // 默认 4
    XCallDepthPolicy = Policy.Error  // Error / Warn / Unlimited
};
```

| Policy | 行为 |
|--------|------|
| `Error`（默认） | 超出 → 运行时错误，脚本停止 |
| `Warn` | 超出 → 日志警告，继续执行（开发用） |
| `Unlimited` | 不检查深度（用户自行承担风险） |

##### 过大消耗的警告

即使在 `Warn` 或 `Unlimited` 模式下，也可以提供**性能退化警告**：

```csharp
// 可选：深度超过 threshold 时发出性能警告
if (_xcallDepth > _warnThreshold) {  // 如 _warnThreshold = 8
    OnPerformanceWarning?.Invoke(
        $"XCALL depth {_xcallDepth} exceeds recommended maximum {_warnThreshold}. " +
        $"Estimated overhead: ~{_xcallDepth * 15} ns per call chain."
    );
}
```

LSP 也可以在编译期提供类似的警告：

```
[PERF] 调用链 A→B→C→D→E→F→G→H 嵌套 8 层。
       估算每帧开销：8层 × 15ns × 50次 = 6.0 μs (0.036% 帧预算)
       建议：简化服务依赖，或确认这是有意设计。
```

##### 方案推荐

**推荐 N3（可配置 + 静态分析 + 运行时兜底）**，分阶段实现：

| 阶段 | 嵌套深度机制 | 说明 |
|------|------------|------|
| C-1 | 运行时硬限制（默认 4） | `_xcallDepth` 计数 + Error 策略 |
| C-1.5 | + 宿主可配置 `MaxXCallDepth` + Policy | 三种策略：Error/Warn/Unlimited |
| C-2 | + 编译期静态分析 + LSP 诊断 | 调用链深度分析 + 性能估算 |

**关键设计原则**：
1. **默认安全**：`MaxXCallDepth = 4, Policy = Error` — 不配置就安全
2. **宿主可控**：C# 侧可以按项目需求调整限制和策略
3. **渐进发现**：LSP 在开发阶段就能提示嵌套深度问题
4. **允许突破**：`Unlimited` 模式允许高级用户自行管理

##### 运行时开销分析

| 检查 | 指令 | 耗时 | 影响 |
|------|------|------|------|
| `++_xcallDepth` | 1 次 int increment | ~0.3 ns | 可忽略 |
| `> _maxXCallDepth` | 1 次 int compare | ~0.3 ns | 可忽略 |
| `--_xcallDepth`（返回时） | 1 次 int decrement | ~0.3 ns | 可忽略 |
| **总计** | 3 条指令 | ~1 ns | XCALL 总 overhead 15→16 ns (+6.7%) |

**`Unlimited` 模式**：可以用 `_maxXCallDepth = int.MaxValue` 实现，compare 仍然执行但永远不触发，开销与 `Error` 模式完全相同（~1 ns）。无需额外分支。

##### 更新后的收敛决策表

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | `svc.member` 统一语法，编译器自动路由 L4/L5 | R23-24 ✅ |
| L4/L5 关系 | 同基线设计：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | **`@export` 唯一形式**（不保留 `public` 别名） | R24-25 ✅ |
| 自动优化 | A1/A2 自动 getter/setter→直接访问退化（C-1.5） | R23 ✅ |
| 用户引导内联 | `@inline`（hint，C-2）+ LSP 诊断。`@force_inline` 已取消 | R24 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(A1/A2+配置) → C-2(语法糖+@inline+LSP) → 远期(A5 深度内联) | R23-25 ✅ |
| 嵌套调用 | 默认最大 4 层，**宿主可配置 MaxXCallDepth + Policy(Error/Warn/Unlimited)** | R21→R25 |
| 性能影响 | 可忽略（< 0.02% 帧预算；深度检查 +1 ns/XCALL） | R20-25 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7+@inline（C-2），O4/A5 深度内联（远期） | R22-24 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

##### 待用户确认（第 25 轮）

> **用户回复（第 26 轮）**：
> 1. 清楚。但希望可配置的是常量部分，兜底是运行时 fallback，类似扩展寄存器。前提是常量配置对性能确实有显著提升。如果没区别使用推荐方案即可。
> 2. 同上
> 3. 仅 Unlimited + Warn 即可（不需要 Error）
> 4. 如果 Q1 确定是推荐方案，则收敛

1. **嵌套深度"4"的含义**是否清楚？—— 只限制跨实例 XCALL 链深度，不影响模块内 CALL
2. **方案 N3（可配置 + 静态分析 + 运行时兜底）**是否满足需求？
3. **三种策略（Error/Warn/Unlimited）**是否覆盖了你说的"兜底处理（允许无限，性能退化）"？
4. **Q4 现在是否可以认为已基本收敛？**

---

#### Q4-H：编译期常量 vs 运行时配置 — 性能分析 + Q4 收敛（第 26 轮）

##### R25 确认总结

| 问题 | 用户回复 | 结论 |
|------|---------|------|
| 嵌套深度含义 | 清楚 | ✅ |
| 可配置方案 | 希望常量可配 + 运行时 fallback（类似扩展寄存器） | 需分析 |
| 策略选择 | 只要 Unlimited + Warn | ✅ 简化（删除 Error） |
| Q4 收敛 | 如果确认推荐方案，则收敛 | 待本轮确认 |

##### 编译期常量 vs 运行时配置：性能对比

用户的类比很精准——扩展寄存器的模式是：**编译期常量决定 fast path，超出部分走 slow path（运行时 fallback）**。

对于嵌套深度，同样的模式意味着：
- **编译期常量 `MaxXCallDepth = 4`**：编译器可以做静态优化
- **运行时 fallback**：超出常量限制时走通用检查路径

让我们分析这对嵌套深度检查是否有意义：

**扩展寄存器为何受益于编译期常量**：

```csharp
// 扩展寄存器：编译期常量决定 fast path vs slow path
// ModuleVarSlots = 8（编译期常量）
if (varIndex < ModuleVarSlots) {
    // FAST: LOAD_MVAR 直接寄存器访问，~0 ns overhead
    // 编译器生成 LOAD_MVAR 指令，运行时直接操作 fixed 数组
} else {
    // SLOW: LOAD_XREG 间接访问 ExtendedRegs 数组，~3-5 ns overhead
    // 运行时走堆分配的 Number[][] 间接寻址
}
```

关键：ModuleVarSlots 是编译期常量，因为它决定了**不同的指令生成**（LOAD_MVAR vs LOAD_XREG），两条路径的**执行代价完全不同**。

**嵌套深度检查的情况完全不同**：

```csharp
// 嵌套深度：无论限制值是编译期常量还是运行时变量
// 检查逻辑完全相同：
case OpCode.XCALL:
    if (++_xcallDepth > maxDepth) { /* warn/unlimited */ }
    // ... 执行 XCALL ...
    // 返回时 --_xcallDepth
```

| 方面 | 编译期常量 `const int MAX = 4` | 运行时变量 `int _maxDepth = 4` |
|------|-------------------------------|-------------------------------|
| 指令数 | 3（inc + cmp + dec） | 3（inc + cmp + dec） |
| compare 操作 | `cmp reg, 4`（立即数） | `cmp reg, [mem]`（内存读） |
| 差异 | 立即数 compare ~0.3 ns | 内存 compare ~0.3 ns（L1 cache hit） |
| **总差异** | — | **< 0.1 ns**（在 L1 cache 中） |

**为什么差异可忽略**：
1. `_maxDepth` 是 VMWorld 的 int 字段，在执行循环中频繁使用的对象，**永远在 L1 cache 中**
2. L1 cache hit 的内存读取 ~1-2 cycles = ~0.3-0.6 ns
3. 立即数 compare 也是 ~1 cycle = ~0.3 ns
4. 差异 < 0.1 ns/XCALL，即使每帧 50 次 XCALL = 每帧 5 ns 差异 → 0.00003% 帧预算

**结论**：编译期常量 vs 运行时变量对嵌套深度检查**无性能差异**。这与扩展寄存器不同，因为：
- 扩展寄存器的常量决定了**不同的指令路径**（fast/slow opcode）
- 嵌套深度的常量只决定了**一个 compare 的操作数来源**（立即数 vs 缓存内存）

**因此：推荐使用运行时配置方案（R25 推荐的 N3 简化版）。**

##### 简化后的最终方案

根据用户反馈，删除 Error 策略，只保留 **Warn + Unlimited**：

```csharp
// VMConfig
public class VMConfig {
    /// <summary>
    /// XCALL 嵌套深度限制。默认 4。
    /// 超出时行为由 XCallDepthWarning 控制。
    /// 设为 0 = 不限制（Unlimited）。
    /// </summary>
    public int MaxXCallDepth = 4;
    
    /// <summary>
    /// 是否在超出 MaxXCallDepth 时发出警告。
    /// true（默认）= Warn 模式：超出时发出警告，继续执行。
    /// false = Unlimited 模式：不检查，不警告。
    /// </summary>
    public bool XCallDepthWarning = true;
}
```

两种模式的行为：

| MaxXCallDepth | XCallDepthWarning | 行为 |
|---------------|-------------------|------|
| 4（默认） | true（默认） | 超过 4 层 → 警告，继续执行 |
| 8 | true | 超过 8 层 → 警告，继续执行 |
| 0 | false | 不检查，不警告（Unlimited） |
| 任意 | false | 不检查，不警告（Unlimited） |

运行时实现：

```csharp
// VMWorld 内部
int _xcallDepth;
int _maxXCallDepth;      // 从 VMConfig 读取
bool _xcallDepthWarning;  // 从 VMConfig 读取

case OpCode.XCALL:
    ++_xcallDepth;
    if (_xcallDepthWarning && _xcallDepth > _maxXCallDepth) {
        OnWarning?.Invoke($"XCALL depth {_xcallDepth} exceeds limit {_maxXCallDepth}");
    }
    // ... 正常执行 ...
    // 返回时 --_xcallDepth
```

`Unlimited` 模式（`_xcallDepthWarning = false`）：`if` 第一个条件就 short-circuit，compare 不执行。overhead ~0.3 ns（仅 inc/dec）。

##### 实现路径更新

| 阶段 | 嵌套深度机制 | 说明 |
|------|------------|------|
| C-1 | 运行时计数 + Warn（默认 4 层） | `_xcallDepth` inc/dec + 日志警告 |
| C-1.5 | + 宿主可配置 `MaxXCallDepth` + `XCallDepthWarning` | 两种模式：Warn / Unlimited |
| C-2 | + LSP 编译期静态分析 | 调用链深度诊断 + 性能估算 |

##### ✅ Q4 最终收敛决策表

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 ✅ |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 ✅ |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | `svc.member` 统一语法，编译器自动路由 L4/L5 | R23-24 ✅ |
| L4/L5 关系 | 同基线设计：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | **`@export` 唯一形式** | R24-25 ✅ |
| 自动优化 | A1/A2 自动 getter/setter→直接访问退化（C-1.5） | R23 ✅ |
| 用户引导内联 | `@inline`（hint，C-2）+ LSP 诊断。`@force_inline` 已取消 — 内联失败严格程度改由编译器配置控制 | R24 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(A1/A2+配置) → C-2(语法糖+@inline+LSP) → 远期(A5 深度内联) | R23-26 ✅ |
| 嵌套调用 | **运行时配置 MaxXCallDepth（默认4）+ Warn/Unlimited 两种策略** | R21→R26 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算；深度检查 +1 ns/XCALL；常量 vs 变量无差异） | R20-26 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7+@inline（C-2），O4/A5 深度内联（远期） | R22-24 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

**Q4 服务脚本设计 — ✅ 收敛（第 26 轮）**

所有 14 项设计决策已确认。**C-1~C-2 已全部实现**（Lang-6/7/8 ✅，1259 tests pass）。`@force_inline` 已取消，A5 深度内联（Lang-9）为远期。

##### 第 26 轮待确认项（已全部确认）

1. **编译期常量 vs 运行时配置无性能差异**的分析是否认可？→ 确认使用运行时配置方案
2. **Q4 收敛** — 14 项决策全部锁定，下一步输出 XCALL Spec？

---

</details>

<details>
<summary>📋 讨论轮次总览</summary>

#### 第 26 轮（最终轮 — Q4 收敛）

用户回应第 25 轮 4 个确认问题：
- Q1: 嵌套深度含义清楚。希望常量可配 + 运行时 fallback（类似扩展寄存器），但前提是常量配置对性能有显著提升
- Q2: 同 Q1
- Q3: 仅需 Unlimited + Warn（删除 Error 策略）
- Q4: 如果确认推荐方案，则收敛

语言方回应：
1. 编译期常量 vs 运行时配置性能对比：扩展寄存器受益于常量因为决定不同指令路径（LOAD_MVAR vs LOAD_XREG）；嵌套深度检查只影响 compare 操作数来源（立即数 vs L1 cache），差异 < 0.1 ns → 无性能差异
2. 确认使用运行时配置方案（N3 简化版）
3. 策略简化：Warn（默认）+ Unlimited（XCallDepthWarning=false）两种模式
4. Q4 收敛：14 项决策全部锁定

#### 第 25 轮

用户回应第 24 轮 4 个确认问题 + 新疑惑：
- Q1: 统一语法 OK ✅
- Q2: @export 唯一形式，放弃 public（比 R24 提议更果断）✅
- Q3: @inline 分层设计 OK ✅
- Q4: 等新疑惑
- 新疑惑：嵌套 4 层含义？可配置？性能影响？过大消耗警告？兜底（允许无限+退化）？

语言方回应：
1. 嵌套 4 层含义解析：只限跨实例 XCALL 链深度，不影响模块内 CALL。4 = KOF 典型 2 层 + 1 层裕量 + 1 层余量
2. 三种方案分析：N1（硬限制）→ N2（宿主可配置+运行时）→ N3（可配置+静态分析+兜底）
3. 推荐 N3：MaxXCallDepth（默认4）+ Policy（Error/Warn/Unlimited）+ LSP 深度诊断
4. 运行时开销：+1 ns/XCALL（3 条指令：inc/cmp/dec），可忽略
5. @export 锁定为唯一形式（删除 public 别名）
6. 收敛决策表更新至 14 项

#### 第 24 轮

用户回应第 23 轮 5 个确认问题：
- Q1: L4/XCALL 同基线 OK ✅
- Q2: A1/A2 自动优化 — "想到一块去了" ✅
- Q3: 统一语法 OK ✅ — 确认 m.property/m.field/m.method()/m.function()，底层机制透明
- Q4: 略微倾向 `public`（便捷度，低权重），但认可 @export 与 @service 一致性
- Q5: 未收敛 — 希望用户可主动请求内联优化，编译器提供辅助途径

语言方回应：
1. 统一语法确认：svc.member 底层 5 种路径（XLOAD/XSTORE/A1退化/XCALL/A5内联）对用户完全透明
2. @export 保持为规范形式；public 可作为 C-2 别名（可逆低风险决策）
3. 用户引导内联设计：@inline（hint，C-2）+ LSP 内联诊断。`@force_inline` 已取消 — 内联失败严格程度改由编译器配置控制
4. 收敛决策表更新至 14 项

#### 第 23 轮

用户回应第 22 轮 4 个确认问题：
- Q1: Y1-Plus OK ✅
- Q2: XCALL 优化路线图 OK ✅
- Q3: L4 需要更早考虑，和 XCALL 至少同一基线。可能有更有效的自动优化方案
- Q4: 等 Q3 解决后再定

语言方回应：
1. 认同 L4 应与 XCALL 同基线。提出统一模型 XIMA（Cross-Instance Member Access）
2. C-1 同时实现 3 个 OpCode：XCALL + XLOAD_MVAR + XSTORE_MVAR，共享实例查找基础设施
3. 5 个自动优化方案分析（A1-A5）：A1/A2（自动 getter/setter→直接访问退化）最有效
4. `svc.member` 统一语法 — 编译器通过导出表自动路由 L4/L5，不靠括号区分
5. `@export` 声明设计：@export var（读写）、@export const（只读）、@export func（可调用）
6. 修订分阶段路径：C-0→C-1(XCALL+XL4)→C-1.5(A1/A2)→C-2(语法糖)→C-3(内联)
7. 收敛决策表更新至 13 项

#### 第 22 轮

用户回应第 21 轮 3 个确认问题 + 补充 1 个新问题：
- Q1: yield 保证要纯编译期，包括 UI/LSP，不要运行时负担 → Y1-Plus
- Q2: 直接瞄准语法糖终极形态 `svc.func()` → C-2 为目标
- Q3: XCALL 优化需系统性分析，内联理想但要走完形式；退化方案性能优先
- Q4（新）: 当前方案与 L4/L5 关联，是否"正教"，L4/L5 实现时是否需纳入考虑

语言方回应：
1. Y1-Plus 纯编译期 yield 禁止：跨函数 yield-taint 分析 + LSP 实时诊断，运行时零改动
2. 直接瞄准 C-2 语法糖，C-0/C-1 为内部实现细节
3. XCALL 7 个优化方向系统性分析（O1-O7）：O1 基线 + O2 参数优化（C-1）→ O7 常量折叠（C-2）→ O4 内联（C-3+）；O3/O5/O6 不做。退化策略：编译期自动退化，运行时零决策
4. L4/L5 正教性确认：XCALL = L5 精确实现（非近似），L4 可通过 L5 间接实现。C-2 需预留 `svc.var` vs `svc.func()` 语法区分
5. 收敛决策表更新至 12 项

#### 第 21 轮

用户回应第 20 轮 4 个确认问题：
- Q1: 需要确认嵌套 XCALL 性能；问 XCALL 是否通用概念，要求参考其他实现
- Q2: 无 yield ✅，追问如何保证（编译期/运行时？）
- Q3: 语法糖满意 ✅
- Q4: 宿主创建 ✅

语言方回应：
1. XCALL 跨语言参考 8+ 语言/VM：Lua（零额外开销）、WebAssembly（call_indirect ≈1.5×）、JVM（invokevirtual ≈1.2×）、Wren（foreign ≈2×）等。FFVM XCALL 最接近 WebAssembly 跨模块调用 + Wren foreign method
2. 嵌套 XCALL 性能分析：单层 ~15 ns，4 层嵌套 ~60 ns，每帧 50 次 < 0.02% 帧预算
3. 与本 VM CALL 对比：XCALL ≈ 3× CALL（多了参数复制 + 实例切换），但绝对值可忽略
4. 无 yield 保证：Y3 双保险方案 — 编译期 `@service` 禁止 yield + 运行时 InXCall flag 检测
5. 汇总已收敛的 8 项设计决策
6. 剩余 4 个开放问题（函数索引 vs 名称、服务注册 API、错误处理、C-0 原型时机）

#### 第 20 轮

用户澄清服务脚本定位：
- `#include` 是编译期 mixin，与服务脚本是不同概念
- 服务脚本是独立的运行时功能，不一定通过 include 实现
- 偏好方式 C（语言级引用），认为方式 A/B 把问题推回了 Syscall 层，未真正解决 FFS 组织能力问题
- 要求讨论重点：可行性、性能、易用度、心智负担

语言方回应：
1. 纠偏：承认第 19 轮混淆了 include 和服务脚本。明确两者正交
2. 四维度深入分析方式 C：
   - **可行性** ✅：两种实现路径（C1 XCALL OpCode / C2 Syscall 桥接），建议分阶段 C-0→C-1→C-2→C-3
   - **性能** ✅：80 实例 57.6 KB RAM，每帧 100 次 XCALL ≈ 1.5μs（< 0.01% 帧预算）
   - **易用度** ✅：比 Syscall 更有组织感，C-2 语法糖可达 OOP 级体验
   - **心智负担** ✅：统一模式替代分散 Syscall，组织性补偿学习成本
3. 提出分层实施路径：C-0 零改动原型验证 → C-1 XCALL OpCode → C-2 点号语法糖 → C-3 自动发现
4. 重新定位 L4/L5：服务脚本核心需求就是 L5（跨模块函数调用），L4 可通过 L5 间接实现

#### 第 19 轮

用户对 Q4 三种手段的详细回复：
- 手段 2+3 组合确认 ✅ 符合直觉
- 提出"服务脚本/业务脚本"分层概念：对一组封装使用一个"服务脚本"，业务脚本引用多个服务脚本，服务脚本常驻但允许动态产生/销毁
- 指出与 L4/L5 相关
- 提出 4 个子问题：持有方式、自动产生销毁、流程绑定、轻量化硬规则
- 手段 3 在 ECS 阶段可能更适合 span 传入方式
- 兜底机制：服务脚本设想可完美缓解 Syscall 紧张
- 修正：`作` → `做`

语言方回应：
1. 区分两个层次：L2 编译期服务（include 头文件，已支持）vs L4/L5 运行时服务（需要跨模块调用）
2. 跨语言调研 10+ 语言/框架：Unity 组件模型、Lua require、Godot Autoload、Kotlin DI、Wren foreign、Squirrel 表注入等
3. 分析 4 个子问题：推荐"宿主持有"方式（零 VM 改动），生命周期由宿主管理，服务切换通过 Syscall 背后的 C# 多态实现
4. 结论：当前架构已事实覆盖"服务脚本"需求 — C# 宿主服务 + Syscall 接口 = 服务脚本。L4/L5 作为远期储备。

#### 第 18 轮

用户确认 Q2 收敛：方案 A 完美。帧号推进由宿主负责正确。新增 Q4 讨论方向：FFS 无对象语言的宿主参数封装。

用户对三种手段的反馈：
- 手段 1（前缀命名）：略麻烦，绑定 Owner，槽位会不够用
- 手段 2（include 组织脚本）：与用户想法一致，每个脚本配套组织脚本
- 手段 3（泛用参数化）：手段 1 的泛用化
- 寄存器分配：扩展方向由业务决定等于作死，需要兜底机制

语言方回应：
1. Q2 正式标记 ✅ 收敛，折叠历史
2. Q4 正式化：推荐手段 2+3 组合（include 头文件 + 泛用 Syscall）
3. 兜底机制：分析 4 类风险并提出对策；Syscall 槽位可扩展到 ushort，ExtendedRegs 已兜底模块变量

#### 第 17 轮

用户指出小失误：场景 2/3 用脚本维护 `f` 的写法与场景 1 矛盾 — 既然方案核心是"脚本只关心 tick/yield，帧号由宿主提供 GetFrame()"，所有场景应统一。大方向正确。要求从其他语言（大众+小众）借鉴。

语言方回应：
1. 修正场景 2/3 全部改用 `GetFrame()`，样板从 4 行降为 2 行/阶段
2. 跨语言调研 10 种语言：C#/Lua/GDScript/Kotlin（大众），Wren/Squirrel/Fennel/Ink/Zig/Haxe（小众）
3. 结论：7/10 语言使用完全相同的 while+yield/suspend/await 模式 — 这是行业事实标准
4. 无语言为 while+yield 提供专用语法糖 — 因为 while+yield 本身就足够简洁

Q2 基本收敛。

#### 第 16 轮

用户想明白了需求：不是"从上一个 yield 点重放"，而是"指定一段代码让它循环"。给出 while+yield+GetFrame() 条件 break 的示例。要求模拟实现 + 对比专用语法的简洁度。

语言方回应：模拟了 3 个真实场景。提出 3 条路径：
- 路径 A（while 循环）：零改动、零混淆、灵活性极高，样板代码 ~3 行/阶段
- 路径 B1（frames 语法糖）：消除 ~40% 样板，但语言-业务耦合
- 路径 C（辅助函数/宏）：当前 FFS 不支持高阶函数和宏，暂不可行

推荐路径 A 先上路，B1 作为未来样板痛点方案备选。关键发现：如果帧号由宿主维护（GetFrame()），样板可进一步简化为 `while GetFrame() < N { ...; yield }`。

#### 第 15 轮

用户明确决定：yield 保持随 tick 直觉（= C# yield return 一致）。新增独立的"帧区间重复"机制处理硬直帧逻辑。三个设计约束：不与 yield 混淆、统一直觉、语言/宿主层职责分离。

语言方回应：提出 4 个方案（R1 纯宿主 / R2 语言标注块 / R3 Syscall 回调 / R4 VM 自动重放），推荐 R1（独立函数 onHitstun，宿主在硬直帧直接调用）。

#### 第 14 轮

用户接受世界帧/角色帧双帧概念，接受 IsLogicTick 判断机制。但指出 X4a 中 yield 在硬直帧"从上次 yield 恢复执行"违反 C# 直觉 — 追问是否暗示问题出在 yield 语义本身。

语言方回应：用户直觉正确。提出三条解决思路（A: yield=角色帧 / B: 双暂停原语 / C: 重定义心智模型），待用户选择核心诉求方向。

#### 第 13.5 轮

用户对第 13 轮做出 3 个关键反驳：
- 反驳①：buff 等也由 VM 实例执行 → "推给宿主碰撞系统"回避了问题；且 buff 也可能有混合硬直逻辑 → 问题递归
- 反驳②：碰撞系统直接执行伤害是不健康设计 → 脚本化应该彻底
- 反驳③：**核心矛盾场景** — 同一个 step() 内 SetSomeFlag() 不该在硬直执行，ApplyDamageToNearby() 应在硬直执行 → yield 歧义

回应：
- 承认三个反驳全部成立 — 之前的 onPaused 方案只解决函数级分离，不够
- 问题粒度在**语句级**而非函数级
- 提出 4 个新方案（X1 标注块 / X2 双游标 / X3 always 协程 / X4 双Tick+IsLogicTick）
- **推荐 X4**：VM 改动最小（Tick 加 bool 参数 + 1 个 Syscall），语言无改动，脚本内用 `if IsLogicTick()` 保护帧驱动逻辑

#### 第 13 轮

用户对 Q2（硬直+yield 模型）做出深层反馈：
- [意外情况]：硬直中可能仍需持续伤害 → 破坏"不Tick=冻结"前提
- [担忧]：分离 step/onPaused = 回到"强制平行配置重复" 
- [心智负担]：yield 语义是否受硬直影响
- [更深担忧]：分离是否成为物理限制

回应：
- [意外情况] 在格斗游戏中通常由宿主碰撞系统处理（hitbox 持续存在），不需要脚本在硬直中执行
- 分离 step/onPaused 不等于重复配置 — 两者正交，不需要同步修改
- yield 对脚本编写者完全透明 — "等下一帧"，不需要关心硬直
- 分离是语义必然（帧驱动 ≠ 时间无关），不是物理限制

#### 第 12.5 轮

用户对第 12 轮 Q1~Q3 做出回应：
- Q1: 确认无害，丢弃 → **已关闭**
- Q2: 用户模型（step 打算一次执行完 → yield/wait 暂停 → 宿主不推进时间轴则冻结）已确认正确。yield/wait 与宿主时间轴的耦合是**隐式的**且**正确的** — 不需要语言层改动
- Q3: 补充方向 0（宿主直调 = 基本 API 使用模式）。确认模块变量 = 实例级共享数据。详细介绍实例重定向（Instance Redirect）运行机制与完整示范

#### 第 12 轮

用户提出 3 个新议题：
- (1) OOP 数据封闭原则与 ECS Component 需求是否冲突 → 结论：不冲突，Syscall 边界天然分离，不需要现在额外考虑
- (2) 硬直期间脚本是否应继续进入 → 3 个子方案（A′-1 双 Tick / A′-2 查询补偿 / A′-3 独立回调），推荐 A′-3
- (3) 跨脚本 VM 使用模式 → 5 个方向（黑板中介 / include 模板 / 宿主编排 / 共享变量区 / 服务脚本），推荐渐进路径 1+2+3 → 4 → 5

#### 第 11 轮

用户反馈：语言演进路线应以需求背景为主视角 — 哪些需求实现不了/难以达到理想状态，给出多个建议方向（长期理想/最速最小改动/折衷），最终由语言方从语言视角取舍或完全整合。

重构为需求侧 → 语言方的正式提案格式：
- 4 个痛点（P1~P4）按紧迫度排序
- 3 个建议方向（A 完整 / B 最速 / C 折衷）各含细则
- 推荐方向 C 但决策权在语言方
- 6 个待语言方回复的具体问题

#### 第 10 轮

用户反馈：语言级需求（文件变量、全局变量/函数、include）分散在多个 SK 小节中，需要整合。同时追问连招描述脚本的覆盖度和理想形。

分析结论：
- L1（模块变量）与 L2（include）正交互补，Phase 1 即可实现
- L2（include）与 L3/L4（跨模块运行时共享）不应整合 — 编译期 vs 运行时，分层递进
- 连招描述脚本推荐理想形 3（混合式）— L1+L2+现有黑板 Syscall，Phase 1 可覆盖
- 语言演进路线 Lang-1~Lang-5 提出，待同步到 VM_Summary

</details>

## SK15: 技能属性声明式提取 💬

**结论**：用 `@export var` 模块变量 + 默认值替代 `SetSkillMeta()` Syscall 调用。宿主通过 `[names]→[indices]` 一次性解析名字列表固化索引，后续 `[indices]→[values]` 批量读取默认值（也可包装为 `GetVarDefault` 便捷 API）。免去双方常量同步、临时实例假执行。需 VM 层支持（→ Lang-10）。排期：语言方完成 Lang-10 后由需求方提示下一步。

<details>
<summary>📋 详细设计</summary>

**现状痛点**：

当前技能配置通过 `SetSkillMeta(key, value)` Syscall 声明：

```ffs
// skill_walk_forward.ffs — 当前方式
func main() {
    SetSkillMeta(META_TOTAL_FRAMES, -1)
    SetSkillMeta(META_PRIORITY, 1)
    SetSkillMeta(META_TAGS, TAG_BIT_WALK)
    SetSkillMeta(META_IS_LOOPING, 1)
    // ... 10+ 行 Syscall
    // ── Execution ──
    while 1 { ... yield }
}
```

宿主侧 `ExtractSkillDef()` 需要 spawn 临时实例、tick 一次、捕获 meta、destroy（`GameVMBridge.cs:154-231`）。

三个痛点：
1. **双方常量同步**：`META_TOTAL_FRAMES = 1` 在 `skill_base.ffs` 和 `GameConstants.cs` 都要定义且保持一致
2. **假执行提取**：spawn 临时实例只为捕获配置，语义不直觉
3. **属性 ≠ Syscall**：技能配置是静态数据声明，但用了运行时 Syscall 调用来表达

**建议方案**：

```ffs
// skill_walk_forward.ffs — 建议方式
@export var totalFrames: int = -1
@export var priority: int = 1
@export var tags: int = TAG_BIT_WALK
@export var isLooping: int = 1
@export var activationPriority: int = 500
@export var interruptPriority: int = 500
@export var allowedStances: int = STANCE_GROUNDED
@export var requireGrounded: int = 1
@export var requireInputHeld: int = INPUT_BIT_ANY_DIR_H
@export var requireNotInputHeld: int = INPUT_BIT_UP

func main() {
    // 直接进入执行逻辑，无需 SetSkillMeta 调用
    defer { SetVelocity(0.0, 0.0) }
    while 1 { ... yield }
}
```

宿主侧提取（两阶段模式）：

```csharp
// ── Phase A: 一次性解析名字 → 索引（程序启动时 / 模块加载时）──
var program = World.Modules.Get(moduleSlot);
var exports = program.ExportTable;

// 名字列表由宿主定义，与脚本 @export var 名对应
string[] names = { "totalFrames", "priority", "tags", "isLooping",
                   "activationPriority", "interruptPriority",
                   "allowedStances", "requireGrounded",
                   "requireInputHeld", "requireNotInputHeld" };

int[] indices = exports.ResolveVarIndices(names);
// indices 可缓存复用 — 同模块所有实例共享

// ── Phase B: 批量读取默认值（按索引，O(1) 数组访问）──
Number[] values = exports.ReadVarDefaults(indices);
// values[0] = totalFrames (-1), values[1] = priority (1), ...

// 或使用便捷包装：
int totalFrames = exports.GetVarDefault("totalFrames", -1);
int priority    = exports.GetVarDefault("priority", 0);
// GetVarDefault 内部 = 线性查找 + 读默认值（适合低频 / 少量调用）
```

**两种 API 互补**：
- `ResolveVarIndices` + `ReadVarDefaults`：**批量高频**场景（如加载全部技能配置），名字→索引一次性解析后缓存
- `GetVarDefault`：**单次便捷**场景（如调试 / 少量查询），无需预缓存

**好处**：
- 免去双方常量定义（宿主直接按名查找，脚本用变量名即属性名）
- 免去不和谐的初次 tick（编译后的导出表已包含变量默认值）
- 属性以字段的形式存在，符合 C# 直觉
- 基类属性可通过 `include` 共享，子技能脚本只覆盖需要的字段

**VM 需求（→ Lang-10）**：

| 需求 | 说明 | 现状 |
|------|------|------|
| 名字→索引批量解析 | `ExportTable.ResolveVarIndices(string[] names) → int[]`（一次性，结果可缓存） | ❌ 不支持（`ExportVarEntry` 已有 `Name` 字段，但无查找方法） |
| 索引→默认值批量读取 | `ExportTable.ReadVarDefaults(int[] indices) → Number[]`（O(1) 数组访问） | ❌ 不支持（当前仅记录 slot，不记录默认值） |
| 导出变量默认值存储 | 编译期求值 `@export var x: int = <const_expr>` 并存入 `ExportVarEntry.DefaultValue` | ❌ 不支持 |
| 便捷单次 API | `ExportTable.GetVarDefault(name, fallback)`（内部 = 线性查找 + 读默认值） | ❌ 不存在 |

> 现有 `ExportVarEntry` 已包含 `Name` 和 `MvarSlot` 字段（Lang-6 引入），但缺少：
> 1. 名字→索引解析方法（当前仅按 export index 顺序访问）
> 2. 默认值存储（编译期常量求值结果）
> 3. 批量 / 便捷读取 API

**分阶段路径**：

| 阶段 | 内容 | 依赖 | 触发 |
|------|------|------|------|
| Phase 1 | Lang-10：ExportTable 默认值存储 + `ResolveVarIndices` / `ReadVarDefaults` / `GetVarDefault` API | VM 编译器改动 | 已提交串行计划 |
| Phase 2 | 宿主侧 `ExtractSkillDef` 重构为直接读取导出表 | Phase 1 | **需求方在 Lang-10 完成后提示** |
| Phase 3 | 脚本侧迁移：`SetSkillMeta()` → `@export var` 声明 | Phase 2 | 需求方驱动 |

**向后兼容**：
- `SetSkillMeta()` 路径保留，新旧并存
- 优先检查导出变量，回退到 `SetSkillMeta()` 捕获

</details>

<details>
<summary>📋 讨论历史</summary>

**2026-04-10 第 2 轮**

用户反馈（第 1 轮方案细化）：
1. 提取方式改为 `[names]→[values]` 加速模式 — 通过名字列表一次性固化索引列表（`ResolveVarIndices`），后续批量读取值（`ReadVarDefaults`）
2. 也可包装成 `GetVarDefault` 便捷方式（单次查询场景）
3. 排期调整：语言方完成 Lang-10 后，需求方（KOF）会主动提示下一步（Phase 2 宿主侧重构），无需语言方追踪 KOF 进度

分析：
- `[names]→[indices]` 缓存模式 = 名字解析成本一次摊平，后续全部 O(1) 数组访问。对多技能批量加载（8+ 脚本）更高效
- `GetVarDefault` 作为便捷包装保留（内部线性查找，适合低频场景）
- 两种 API 互补：批量高频 vs 单次便捷
- Phase 2/3 排期由需求方驱动，语言方只需完成 Lang-10 即可

**2026-04-10 第 1 轮**

用户建议：通过 C# 属性/字段名数组提取技能配置。具体路径为：
1. 脚本侧：技能属性以 `@export var` 字段形式存在，给予默认值
2. 宿主侧：C# 属性/字段名数组 → 从 ExportTable 获取 index 并缓存 → 绑定以 field indices 为准
3. 好处：免去双方常量定义、免去繁琐的技能内 SetSkillMeta 设置、免去不和谐的初次 tick

分析：建议方向与已有的 `@export var` 基础设施（Lang-6/8 ExportTable）天然契合。`ExportVarEntry` 已有 `Name` + `MvarSlot` 字段，差距仅为：
- 缺少按名查找 API
- 缺少编译期默认值存储
- 宿主侧缺少声明式提取 API

判断为 Lang-10 级别改动（编译器 + ExportTable 扩展），复杂度 ⭐⭐。已提交至 VM_Summary 串行计划。

</details>

## Syscall 需求评估

**结论**：前 4 个脚本（S01~S04）可直接用现有 Syscall 实现。S05~S08 及碰撞框脚本化需新增少量 Syscall。

<details>
<summary>📋 详细设计</summary>

| 需求 | 现有 Syscall | 是否需要新增 |
|------|------------|------------|
| 动作管理 | BeginAction, EndAction, GetFrame | ✅ 足够 |
| 命中检测 | CheckAttackHit, CheckAttackBlocked | ✅ 足够 |
| 伤害/硬直 | ApplyDamage, ApplyHitstun, ApplyHorizKB_* | ⚠️ ApplyDamage 需拆分（§4.2），新增 ApplyHitReaction |
| 速度控制 | SetVelocity | ✅ 足够 |
| 输入查询 | GetInput, GetInputDir | ✅ 足够 |
| 落地检测 | IsGrounded | ✅ 足够 |
| 效果 | SpawnEffectHit, SpawnEffectSelf | ✅ 足够 |
| 碰撞框设置 | — | 🆕 需要 `SetHitbox`, `SetHurtbox`, `ClearHitbox` |
| 蹲姿/姿态切换 | — | 🆕 需要 `SetStance` |
| 倒地状态 | — | ❓ 可能需要 `SetKnockdownState` / `SetInvincible` |

> 📌 **宿主前置依赖**：需先实现上述新增 Syscall 的宿主侧逻辑（碰撞框管理、姿态系统、倒地状态系统），然后才能在脚本中使用。

</details>

---

## 宿主侧变更评估

| 变更 | 说明 | 影响范围 |
|------|------|---------|
| `CanActivate` → 脚本 `checkEnter()` | 宿主通过 `TryGetFunction("checkEnter")` 获取入口 IP，spawn + tick + 读返回值 | GameVMBridge + SkillManager Layer 3 |
| `OnFrame` → `step()` 函数 | 条件通过后同实例 redirect 到 `step()` 入口，每帧 tick | GameVMBridge |
| `CanContinue` → 脚本控制 | 循环技能退出由脚本 return 或宿主 Kill | SkillManager |
| `CollisionFrames` → 脚本内设置 | 碰撞框数据迁移到脚本 Syscall | 需新增 Syscall |
| `ProbeSkillCondition` → redirect 模型 | 重构为 checkEnter + redirect（非 yield/return 信号） | 需修改 |
| `VMWorld.RedirectInstance` | 新增 API：重定向完成实例到新入口 | FFVM 新增 ~5 行 |
| 裁决层改造 | 实现分层候选池机制 | SkillManager 重构 |
| `ApplyDamage` → 拆分 | 拆为 `ApplyDamage` (数值) + `ApplyHitReaction` (标记) | Syscall 重设计 |

---

## 附录 A：技能全集分类

> 方括号 `[]` 表示可选/高级变体，非基础集合。

| 分类 | 技能列表 |
|------|---------|
| **基础移动** | 站立待机, 前进, 后退, 前跑, 蹲, 跳, 前跳, 后跳, [前影跳], [后影跳] |
| **防御** | 站防御, 蹲防御, [空中防御] |
| **闪避** | 前闪避, 后闪避, [原地侧闪], [前影闪], [后影闪] |
| **基本攻击** | 近拳, 远拳, 蹲拳, 跳拳, 近脚, 远脚, 蹲脚, 跳脚 |
| **抓投** | 前投, 后投, 空中投, 指令投起手, 拆投 |
| **受击** | 上受击, 中受击, 下受击, 被绊倒 |
| **空中受击** | 被吹飞, 浮空, 旋转吹飞 |
| **倒地** | 硬倒地, 软倒地, 仰面倒地, 趴伏倒地 |
| **起身** | 原地起身, 受身(快速起身), 前滚起身, 后滚起身 |
| **特殊** | 被破防, 眩晕, 瘫软, 版边反弹 |
| **死亡** | 基础死亡, 属性死亡, 磨血死亡, 剧情死亡 |
| **流程** | 开始, 胜利, 战败, 续关, 完美胜利 |
| **挑衅** | 基础挑衅, 击败挑衅 |

---

## 附录 B：参考脚本参数模式提取

> 从 `skill_114`（飞燕旋风腿）和 `skill_25`（上盘被击中）提取通用参数表示，作为首批脚本的 Syscall 参数设计参考。

### 动作声明

```
BeginAction(actionId, totalFrames)
defer { EndAction() }
```

- `actionId`: 技能定义 ID，宿主用于查找动画/碰撞框数据
- `totalFrames`: 动作总帧数
- `defer + EndAction()`: 确保技能结束时清理

### 伤害参数与受击标记

两个概念的区分：

| 概念 | 用途 | 生命周期 |
|------|------|---------|
| **伤害参数** (Damage Params) | 用于计算伤害数值（系数、属性克制等） | 命中瞬间消费 |
| **受击标记** (HitReactionTag) | 攻击方附加到受击方，受击方据此决定进哪种受击技能 | 附加→受击方读取→消费 |

```
// 伤害参数：用于数值计算
ApplyDamage(targetId, coefficient)

// 受击标记：通知受击方选择受击技能
ApplyHitReaction(targetId, reactionTag)
```

- `coefficient`: 伤害系数（不是绝对值），宿主用公式换算
- `reactionTag`: 受击反应标记（唯一枚举 ID，不做 mask 组合）

### 硬直参数

```
ApplyHitstun(targetId, startFrame, durationFrames, level, shakeFlag)
```

### 击退参数

```
// 模式 A: 距离+时间 (定距移动)
ApplyHorizKB_Dist(targetId, distance, durationFrames)

// 模式 B: 速度 (直到着地)
ApplyHorizKB_Speed(targetId, speed)

// 垂直击退
ApplyVertKB(targetId, speed, durationFrames)

// 自身位移
ApplySelfHorizKB(distance, durationFrames)
ApplySelfVertKB(initialSpeed, acceleration)
```

### 互斥分组

```ffs
var mutex1: int = 0
if mutex1 == 0 {
    // 检测命中...
    mutex1 = 1
}
```

用局部变量实现（非 Syscall）。同一攻击段只生效一次。

### 能量系数

```
SetEnergyCoeff(multiplier)
```

临时修改下次 ApplyDamage 的能量获取倍率。首批脚本可暂不使用。

---

## 附录 C：首批脚本伪代码示例

> 第 9 轮更新：所有示例已改为 `checkEnter()` + `step()` 双入口结构（方案 D′）。

### 脚本书写风格约定

- **必须提取为变量**：多次引用的值（帧数 `totalFrames`、攻击窗口边界）
- **可内联**：仅出现一次的常量参数（effectId, groupId 等），注释说明含义
- **帧号获取**：统一使用 `GetFrame()` Syscall 从宿主获取帧号，**不在脚本内自行维护帧计数器**（参见 Q2 结论）
- **结构约定**：所有 VM 技能脚本包含 `func checkEnter(): int`（条件）+ `func step()`（执行）

### S01 — 站立待机 (Idle)

```ffs
/// Idle 不需要条件检查 — 作为最低优先级兜底，总是可进入
func checkEnter(): int {
    return 1
}

func step() {
    BeginAction(1, -1)    // -1 = 无限循环
    defer { EndAction() }

    while true {
        yield
    }
}
```

### S04 — 近拳 (LightPunch)

```ffs
func checkEnter(): int {
    var lpPressed: int = IsInputPressed(4)   // 4 = LP button
    var grounded: int = IsGrounded()
    if lpPressed == 0 || grounded == 0 {
        return 0
    }
    return 1
}

func step() {
    var frames: int = 20

    BeginAction(101, frames)
    defer { EndAction() }

    var hit: int = 0
    while GetFrame() < frames {
        if GetFrame() >= 4 && GetFrame() < 8 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 {
                ApplyDamage(t, 3, 102)
                ApplyHitstun(t, 0, 10, 0, 1)
                ApplyHorizKB_Dist(t, 0.5, 5)
                SpawnEffectHit(3001, 30)
                hit = 1
            }
        }
        yield
    }
}
```

### S06 — 上受击 (HitHigh)

```ffs
/// 受击技能由宿主裁决层触发，checkEnter 总是通过
func checkEnter(): int {
    return 1
}

func step() {
    var frames: int = 20

    BeginAction(25, frames)
    defer { EndAction() }

    SpawnEffectSelf(4001, 60)

    while GetFrame() < frames {
        yield
    }
}
```

---
