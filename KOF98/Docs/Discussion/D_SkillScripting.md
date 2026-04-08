# KOF98 技能 FFS 脚本化讨论

> **状态**：🔄 讨论中（SK1~SK12 已收敛，SK3 待性能验证；SK14 语言需求已整合；第 12.5 轮回复用户对 Q1~Q3 的反馈）
> **来源**：需求讨论 — 将 host-side 技能迁移为 FFS 脚本驱动
> **日期**：2026-04-08（第 12.5 轮更新）

---

## 决议总览

| ID | 主题 | 状态 | 结论（一句话） |
|----|------|------|--------------|
| SK1 | 首批脚本范围 | ✅ | 8 个脚本，覆盖 idle→walk→attack→hit→recover 全生命周期 |
| SK2 | 裁决机制 | ✅ | 分层候选池（姿态分组 + 优先级 + 脚本条件） |
| SK3 | 碰撞框交互方式 | 💬 | 方案 A（脚本推送）vs 方案 B（宿主查询），待性能验证 |
| SK4 | 姿态 Syscall | ✅ | 可新增 SetStance 等 |
| SK5 | 命名规则 | ✅ | `skill_<英文名>.ffs` |
| SK6 | 硬直机制 | ✅ | 方案 A — 宿主时间轴暂停，yield/wait(N) 已支持 |
| SK7 | 技能条件入口 | ✅ | 方案 D′ — 同实例 checkEnter()/step()。VM 无硬障碍，模块变量需求转入 SK14 |
| SK8 | 碰撞框 Syscall | ✅ | SetHitbox/SetHurtbox/ClearHitbox/SetPushBox |
| SK9 | 受击标记 | ✅ | 分组 mask 混合方向正确 |
| SK10 | 连招共用环境 | ✅ | V1: 黑板 Syscall；理想形 3（混合式）由 L1+L2+黑板覆盖，需求转入 SK14 |
| SK11 | 多阶段技能 | ✅ | Phase 是脚本内行为，提供便利但不强制 |
| SK12 | ECS 数据归属 | ✅ | 脚本内闭环 → VM；外部需读取 → Syscall 推送到宿主 |
| SK13 | 属性脚本共享 | ✅ | 方案 A — 预处理器 `include`，全量展开。需求转入 SK14 L2 |
| SK14 | FFS 语言需求整合 | 💬 | L1 模块变量 + L2 include = Phase 1；L3/L4 跨模块 = 远期 |

---

## 一、背景

当前所有技能逻辑均为 host-side C#（`SkillDef.CanActivate`/`CanContinue`/`OnFrame` lambda）。
VM 桥接层已就绪（`GameVMBridge` + `GameSyscalls` ~40 syscall），但 `KOF98/Scripts/` 目录为空，零个 .ffs 脚本被编写或加载。

**目标**：讨论并确定首批用 FFS 脚本实现的技能范围。

---

## SK1: 首批脚本范围 ✅

**结论**：8 个脚本，覆盖主循环各阶段。命名规则 `skill_<英文名>.ffs`。

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
| **有限帧型** | `while f < N { yield }`, 帧计数驱动 | LightPunch, HitHigh | BeginAction, EndAction |
| **物理型** | 初始速度 + 等待落地 | Jump | SetVelocity, IsGrounded |
| **被动型** | 宿主切换层激活, 脚本只播放动作 | HitHigh, Knockdown | BeginAction, SpawnEffectSelf |
| **攻击型** | 帧窗口内命中检测 + 分支处理 | LightPunch, CrouchPunch | CheckAttackHit, ApplyDamage |

<details>
<summary>📋 选择原则</summary>

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

**结论**：方案 A — 宿主时间轴暂停。硬直期间宿主不调用 VM Tick，脚本自然冻结。`yield` / `wait(N)` 已支持，无需新增语法。

**两层配合**：
1. **宿主层**：`ApplyHitstun()` → 设置 `Character.HitstunFrames` → 暂停角色时间轴推进
   - 暂停期间：VM 脚本不 Tick（yield 不消费），动画冻结，碰撞框保持
   - 时间轴暂停天然实现"不可操作"（脚本冻住 = 不处理输入）
2. **技能层**：攻击方 `ApplyHitstun()` 同时附加受击标记 → 宿主在硬直结束后根据标记触发受击技能切换
3. **安全兜底**：`HitstunFrames` 倒计时到 0 后，若没有受击技能接管，自动恢复 idle

<details>
<summary>📋 讨论历史</summary>

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

## SK7: 技能条件入口 🔄

**结论**：方案 D′ — 独立 `checkEnter()` + `step()` 函数，**同一 VM 实例**。

> ⚠️ **第 8 轮修正**：第 7 轮分析误将 checkEnter 和 step 描述为"独立 VM 实例"，这是**严重误解**。设计意图始终是：一个技能 = 一个 VM 实例贯穿整个生命周期（从条件检查到执行完毕）。

> 📌 **第 9 轮澄清**：VM 运行时层面**无硬阻碍**。一个 `.ffs` 文件编译为一个 `VMProgram`（ROM），多个函数共存于同一字节码流，`TryGetFunction(name)` 只是查出不同的入口 IP。同一实例的 64 个寄存器（r0~r63）在 `checkEnter` → redirect → `step` 全程物理持续存在，值不会被擦除。
>
> 当前"无法复用变量"的限制在 **编译器/语言层面**：FFVM 编译器对每个函数**独立**做寄存器分配（r16+ local zone），`checkEnter` 的 `var a` 和 `step` 的 `var a` 可能编译到同一个 r16，但语义上是两个独立变量。FFS 语言目前没有"模块级 var"语法，因此无法通过语言层面声明跨函数共享的命名变量。详见下方"全局变量"讨论。

### 核心设计

技能脚本包含两个约定函数：

| 函数 | 职责 | 宿主调用时机 | 返回语义 |
|------|------|-------------|---------|
| `func checkEnter(): int` | 条件检测 | 裁决层 Layer 3（候选池筛选） | `return 0` = 不满足；`return 1` = 满足 |
| `func step()` | 技能执行 | 技能激活后每帧驱动 | `yield` = 继续；`return` = 技能结束 |

**同实例生命周期**：

```
[spawn] → [checkEnter] → return 0 → [destroy]  ← 条件不满足
                       → return 1 → [redirect to step] → [tick] → [tick] → ... → [完成/destroy]
                                     ↑ 同一实例继续      ← 条件满足后执行
```

### 宿主侧实现

```csharp
// 1. 条件检测：spawn at checkEnter
var program = World.Modules.Get(def.VMModuleSlot);
if (!program.TryGetFunction("checkEnter", out var checkEntry)) continue;

int vmId = World.SpawnInstance(def.VMModuleSlot, checkEntry.EntryIP);
_instanceToOwner[vmId] = charId;
World.TickInstance(vmId);

// 读取返回值 (r0)
ref var inst = ref World.Pool.Instances[vmId];
bool passed = inst.Registers[0].IsNonZero;

if (!passed)
{
    // 条件不满足 → 销毁实例
    World.DestroyInstance(vmId);
    _instanceToOwner.Remove(vmId);
    continue;
}

// 2. 条件满足 → 同一实例重定向到 step
if (program.TryGetFunction("step", out var stepEntry))
{
    // 重置实例到 step 入口（保留寄存器状态）
    inst.IP = stepEntry.EntryIP;
    inst.StateFlags &= ~VMStateFlags.Completed;  // 清除完成标记
    // 寄存器空间保留 — checkEnter 中计算的值可在 step 中使用

    // 挂载到技能实例，后续每帧 tick
    skill.VMInstanceId = vmId;
}
```

### checkEnter 和 step 共享状态

由于是同一实例，寄存器空间在 checkEnter 和 step 之间**物理上共享**。不过需注意：

| 维度 | 说明 |
|------|------|
| **函数局部变量** | 各自独立声明、独立编译。checkEnter 的 `var a` 和 step 的 `var a` 虽可能占据相同物理寄存器（如 r16），但语义上是两个独立变量 |
| **寄存器残留** | checkEnter 写入的寄存器值在 redirect 后**物理存在**。step 可以通过约定使用相同寄存器编号读取，但缺乏语言层面的命名保证 |
| **根本瓶颈** | **语言/编译器**层面：FFS 没有模块级 `var` 语法，编译器不支持跨函数的命名变量分配。**VM 运行时**层面：无障碍 |

**结论**：VM 运行时完全支持同实例跨函数状态共享（寄存器物理持续）。瓶颈在编译器/语言。解法见下方"全局变量"讨论。

### FFVM 改动需求

方案 D′ 需要 FFVM 提供"实例重定向"能力：

```csharp
/// <summary>
/// 将一个已完成的实例重定向到新入口点，保留寄存器状态。
/// 用于同实例从 checkEnter 过渡到 step。
/// </summary>
public void RedirectInstance(int instanceId, int newEntryIP)
{
    ref var inst = ref Pool.Instances[instanceId];
    inst.IP = newEntryIP;
    inst.StateFlags &= ~VMStateFlags.Completed;
    inst.StackTop = 0;  // 重置调用栈（新函数从零开始）
    // 寄存器保留（不清零）
}
```

> 📌 这是一个轻量 API 新增（~5 行实现），不影响现有 VM 逻辑。

### 复合条件与全局变量（第 7~9 轮议题）

> **第 9 轮方向**：探索全局变量机制作为优先路径。原则：**数据尽量留在 VM 内**（SK12），黑板只在跨脚本/跨角色真正需要时使用。全局变量还能统一解决连招共用环境（SK10）。

> **第 8 轮修正**：第 7 轮分析基于"独立实例"假设得出"路径 2 不可行"的结论。同实例模型下需重新评估。

| 模式 | 行为 | 适用场景 |
|------|------|---------|
| **无状态检查** | 每次 `checkEnter()` 都检查 A∧B | 简单条件（按键+姿态）— 90%+ 技能 |
| **有状态检查** | 首次 A 满足后记录，后续只检查 B | 蓄力、连招窗口 |

**有状态检查的实现路径**：

| 路径 | 机制 | 可行性 | 第 9 轮评估 |
|------|------|--------|-----------|
| **路径 1：宿主黑板** | checkEnter 通过 Syscall 读写宿主 component | ✅ 不依赖实例模型 | 适合跨脚本/跨角色共享数据（如被击数、连招伤害递减） |
| **路径 2：寄存器残留** | checkEnter 将状态写入特定寄存器 | ⚠️ 不可靠 — checkEnter 每次 spawn 会重入函数开头 | ~~不推荐~~ |
| **路径 3：全局变量** ⭐ | FFS 增加模块级 `var` 声明，编译器分配到保留寄存器段 | 需编译器改动，VM 运行时**无需改动** | **优先探索** — 同实例天然受益，符合 SK12 原则 |

#### 路径 3 深入分析：全局变量

**语言层设计**：

```ffs
// 模块级 var — 跨函数可见，同实例内持久
var chargeReady: int = 0
var comboCount: int = 0

func checkEnter(): int {
    if chargeReady == 0 { return 0 }
    if IsInputPressed(5) == 0 { return 0 }
    return 1
}

func step() {
    comboCount = comboCount + 1
    // ... 使用 chargeReady、comboCount
}
```

**编译器实现思路**：

| 层面 | 改动 | 复杂度 |
|------|------|--------|
| **Parser** | 允许模块顶层 `var` 声明（当前仅允许 `const`） | ⭐ 低 |
| **编译器寄存器分配** | 全局 var 分配到保留段（如 r56~r63，8 个全局槽），函数局部变量仍用 r16~r47 | ⭐ 中 — 需调整寄存器区域划分 |
| **VM 运行时** | **零改动** — 寄存器在实例中天然持久 | ⭐ 无 |
| **初始化** | 全局 var 初始值在 SpawnInstance 时由模块的 `_init` 代码段设置（类似构造函数） | ⭐ 中 |

**寄存器布局调整**：

```
当前:  r0~r15 scratch | r16~r47 local | r48~r63 temp
提案:  r0~r15 scratch | r16~r47 local | r48~r55 temp | r56~r63 global (8 slots)
```

8 个全局槽对于技能脚本足够（蓄力标记、连招计数、Phase 编号等）。

**与 SK10 连招共用环境的关系**：

全局变量机制可直接替代 SK10 V1 黑板方案中**同一脚本内**的状态需求。跨脚本共享仍需宿主黑板（或未来 include 共享模块变量）。但 90%+ 的有状态需求（蓄力进度、连招段数、Phase index）属于**单脚本内**跨函数共享，全局变量即可满足。

**结论（第 9 轮）**：

1. **路径 3（全局变量）为优先探索方向** — 符合"数据留 VM 内"原则，VM 运行时零改动
2. **路径 1（宿主黑板）降级为补充** — 仅用于真正需要跨脚本/跨角色共享的数据
3. **连招共用环境（SK10 V1）可被全局变量部分覆盖** — 同脚本内的连招状态无需外推到宿主
4. **实现优先级**：全局变量可纳入 include（SK13）之后的编译器改动批次

> 💬 **待讨论**：全局变量的寄存器段划分（r56~r63 或其他）、初始化时机（SpawnInstance 时执行 `_init` 段 vs 编译器常量填充）、与 include 共享的交互。

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

```ffs
// skill_light_punch.ffs — 方案 D′ 示例

/// 技能进入条件（独立入口，由裁决层调用）
func checkEnter(): int {
    var lpPressed: int = IsInputPressed(4)
    var grounded: int = IsGrounded()
    if lpPressed == 0 || grounded == 0 {
        return 0    // 条件不满足
    }
    return 1        // 条件满足
}

/// 技能执行主体（激活后每帧驱动，同一实例从 checkEnter redirect 而来）
func step() {
    BeginAction(10, 20)
    defer { EndAction() }

    var f: int = 0
    var hitDone: int = 0
    while f < 20 {
        if f >= 5 && f < 10 && hitDone == 0 {
            var target: int = CheckAttackHit(1001)
            if target > 0 {
                ApplyDamage(target, 1.0, 102)
                hitDone = 1
            }
        }
        f = f + 1
        yield
    }
}
```

<details>
<summary>📋 讨论历史</summary>

#### 第 5 轮

倾向提供**专有条件检测入口**，由宿主侧的调用结构决定。宿主架构中角色技能执行可能存在多个阶段（phase），宿主按顺序调用各 phase：
```
for s in skills:
    s.phaseCondition()  // 条件检测阶段
    s.phaseExecute()    // 执行阶段
```

#### 第 6 轮

经审查发现原方案 C（条件在 main 开头、靠 return/yield 信号区分）与第 5 轮补充的"专有条件检测入口"存在矛盾。实际实现中 `ProbeSkillCondition` 虽已就绪但未被调用，所有 4 个已有脚本仍依赖宿主侧 `CanActivate` lambda。

**根本原因**：方案 C 把条件和执行混在同一个 `func main()` 中，导致：
1. 条件检查和技能执行的生命周期耦合
2. 脚本结构不直观（读者难以区分"条件部分"和"执行部分"）
3. 无法独立复用条件逻辑

修正为方案 D：独立条件函数（多入口）。checkEnter() + step() 双入口。

FFVM 编译器和运行时**原生支持**多入口调用：
1. `BytecodeCompiler.Compile(source, entryFunc, ...)` 编译所有 func
2. `VMProgram.TryGetFunction(name, out entry)` 按名称查找入口 IP
3. `VMWorld.SpawnInstance(moduleSlot, entryIP)` 接受任意入口 IP

#### 第 7 轮

追加复合条件分析。提出无状态/有状态两种检查模式。

**⚠️ 误解**：将 checkEnter 和 step 描述为"独立 VM 实例"，据此得出"路径 2（脚本内 var）不可行"的结论。

#### 第 8 轮

修正实例模型：checkEnter 和 step 应为**同一 VM 实例**。提出 `RedirectInstance` API。

#### 第 9 轮（当前）

用户反馈：VM 运行时不存在硬阻碍，"无法复用变量"是编译器/语言层限制。要求探索全局变量作为优先路径（路径 3），遵循 SK12"数据留 VM 内"原则。全局变量机制可覆盖 SK10 连招共用环境的部分需求。

</details>

---

## SK8: 碰撞框数据来源 ✅

**结论**：碰撞数据完全由技能脚本决定，通过 Syscall 推送到宿主。

**设计原则**：每个技能脚本负责声明自己在各帧的碰撞框（受击框、攻击框、推挤框）。宿主不做碰撞框的静态预定义。

**需新增 Syscall**：`SetHitbox(groupId, x, y, w, h)`, `SetHurtbox(x, y, w, h)`, `ClearHitbox`, `SetPushBox`

```ffs
// 脚本内碰撞框示例
if f >= 4 && f < 8 {
    SetHitbox(1001, 0.2, 0.3, 0.4, 0.3)   // groupId, x, y, w, h
}
```

> 结构体方案（将碰撞框参数封装为 struct）取决于 FFVM 结构体支持进度（B-γ7 SN1 嵌套结构体）。

---

## SK3: 碰撞框交互方式 💬

**结论**：待性能验证后定夺。

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 脚本推送到宿主** | 每帧通过 Syscall 将碰撞框参数推送到宿主 component | 性能好 | 宿主存冗余数据 |
| **B: 宿主向脚本查询** | 碰撞系统需要时向脚本查询碰撞框 | VM 数据隔离好 | 性能待验证 |

---

## SK9: 受击标记 (HitReactionTag) ✅

**结论**：分组 mask 混合方向正确。`damageType` 拆为 `ApplyDamage(targetId, coefficient)` + `ApplyHitReaction(targetId, reactionTag)`。详细枚举设计留待后续。

---

## SK12: ECS 纯数据化与数据归属 ✅

**结论**：脚本内闭环 → 留 VM 内；外部系统必须读取 → Syscall 推送到宿主纯数据 component。

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

---

## SK10: 连招共用环境 ✅

**结论**：V1 用黑板变量（key-value Syscall），理想目标用连招描述脚本集中管理。

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 外挂独立逻辑** | 宿主维护 `ComboContext` | 简单直接 | 连招逻辑散落 |
| **B: 黑板变量** ✅ V1 | 角色级 key-value，Syscall 读写 | 脚本自主管理 | 需约定 key 命名 |
| **C: 连招描述脚本** ⭐ V2 | 专门的 `combo_xxx.ffs` 管理连招 | 逻辑集中 | 需脚本间协调机制 |

---

## SK11: 多阶段技能 ✅

**结论**：Phase 是脚本内实现行为。提供 `BeginAction` 支持多次调用切换动作，但不强制所有多阶段技能必须用 Phase 机制。

```ffs
func step() {
    // Phase 1: 起手
    BeginAction(101, 10)
    defer { EndAction() }

    var f: int = 0
    var hit: int = 0
    while f < 10 {
        if f >= 3 && f < 7 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 { hit = 1 }
        }
        f = f + 1
        yield
    }
    if hit == 0 { return }  // 未命中, 技能结束

    // Phase 2: 追加段
    BeginAction(102, 15)
    var f2: int = 0
    while f2 < 15 {
        f2 = f2 + 1
        yield
    }
}
```

> Phase index 留 VM 内（脚本定义+消费），`BeginAction` 推送动画/碰撞框到宿主。符合 SK12 数据归属原则。

---

## SK13: 属性脚本共享机制 💬

**结论**：方案 A — 预处理器 `include`，全量展开（const + struct + func）。跳过 V0 过渡，直接实现编译器原生 include。

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

## Syscall 需求评估

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

**结论**：前 4 个脚本（S01~S04）可直接用现有 Syscall 实现。S05~S08 及碰撞框脚本化需新增少量 Syscall。

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
- **注意**：`while f < N` 中的 `N` 若在 `BeginAction` 中已声明则应统一变量
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
    var f: int = 0
    while f < frames {
        if f >= 4 && f < 8 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 {
                ApplyDamage(t, 3, 102)
                ApplyHitstun(t, 0, 10, 0, 1)
                ApplyHorizKB_Dist(t, 0.5, 5)
                SpawnEffectHit(3001, 30)
                hit = 1
            }
        }
        f = f + 1
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

    var f: int = 0
    while f < frames {
        f = f + 1
        yield
    }
}
```

---

## SK14: FFS 语言需求整合（第 10 轮新增） 💬

> **背景**：SK7（全局变量）、SK10（连招共用环境）、SK13（include）三个议题各自衍生出语言层需求，分散在不同 SK 小节中。需要整合成统一视图，明确依赖关系和实现优先级，提交 VM_Summary 作为 FFS 语言演进计划。

### 需求全景

| ID | 名称 | 作用域 | 机制 | VM 运行时改动 | 编译器改动 | 来源 |
|----|------|--------|------|--------------|-----------|------|
| **L1** | 模块变量 (file-scope var) | 单个 .ffs 文件内，所有函数共享 | 编译器将模块级 `var` 分配到保留寄存器段（如 r56~r63） | **无** — 寄存器在实例中天然持久 | ⭐⭐ 中 — Parser 顶层 var + 寄存器区域划分调整 | SK7 路径 3 |
| **L2** | include 机制 | 编译期跨文件 | 预处理器文本展开，const/struct/func 全量引入 | **无** | ⭐⭐ 中 — 新增 Preprocessor 模块 + 重定义规则 | SK13 方案 A |
| **L3** | 跨模块共享变量 | 运行时跨脚本 | 需要共享内存区域或黑板 Syscall | ⚠️ **需要** — 新增跨实例数据通道 | ⭐⭐⭐ 高 | SK10 连招 |
| **L4** | 跨模块函数调用 | 运行时跨脚本 | ModuleTable 跨模块函数解析 + CALL 扩展 | ⚠️ **需要** — 跨模块 CALL 协议 | ⭐⭐⭐ 高 | SK10 连招 (理想) |

### L1 与 L2 的关系：正交互补

L1（模块变量）和 L2（include）是**正交**的，且**组合使用**时最强：

```ffs
// shared/combo_state.ffs — 共享模块变量模板
var comboCount: int = 0         // L1: 模块级 var
var lastHitFrame: int = 0

func resetComboState() {        // 共享函数
    comboCount = 0
    lastHitFrame = 0
}

// skill_light_punch.ffs
include "shared/combo_state"     // L2: 引入 var 声明 + func
include "configs/ground_attack"  // L2: 引入 const

func checkEnter(): int { ... }
func step() {
    comboCount = comboCount + 1  // L1: 模块变量跨函数可见
    // ...
}
```

| 单独使用 | 效果 |
|----------|------|
| 仅 L1 | 同文件内 checkEnter/step 共享状态 ✅，但每个脚本都要重复声明 var |
| 仅 L2 | 共享 const/func ✅，但 include 的 var 在每个脚本中是**独立副本** — 不能跨脚本运行时共享 |
| L1 + L2 | 共享 var 声明模板 + 每个脚本实例内持久 ✅ — **覆盖 90%+ 有状态需求** |

### L2 与 L3 的关系：编译期 vs 运行时

用户问到 include (L2) 和全局变量/函数 (L3/L4) "貌似类似但机制不同，能否整合"。

**本质区别**：

| 维度 | L2 (include) | L3/L4 (跨模块运行时共享) |
|------|-------------|----------------------|
| **时机** | 编译期 — 源码文本展开 | 运行时 — 实例间数据通道 |
| **var 共享** | 每个脚本有**独立副本**（各自实例各自寄存器） | 多个脚本**同一份**数据（共享内存或黑板） |
| **func 共享** | 代码**复制**到每个脚本（二进制膨胀但零运行时开销） | 代码**不复制**，跨模块 CALL（需调用协议但零膨胀） |
| **类比** | C `#include` / GLSL include | Lua `require` / C# `using` |
| **VM 改动** | 无 | 需要 |

**结论：不应整合，而是分层递进**。

L2 是编译期文本机制，L3/L4 是运行时通信机制，解决不同层面的问题。正确策略是：

1. **Phase 1**: L1（模块变量）+ L2（include）— 纯编译器改动，VM 运行时零变更
2. **Phase 2**: L3（跨模块共享变量）— 需要时再引入，可先用 **黑板 Syscall** 作为过渡
3. **Phase 3**: L4（跨模块函数调用）— 最高复杂度，仅在"连招描述脚本"理想形真正需要时

### 连招描述脚本覆盖度分析

> 用户问：**当前需求点覆盖了连招描述脚本了吗？连招描述脚本的理想形是什么？**

**当前 SK10 三种方案的语言需求映射**：

| SK10 方案 | 需要的语言特性 | Phase 1 覆盖？ | 说明 |
|-----------|--------------|---------------|------|
| **A: 宿主 ComboContext** | 无 FFS 需求 | ✅ | 完全宿主侧 C# 实现，最简单 |
| **B: 黑板变量 (V1)** | 黑板 Syscall（已有框架） | ✅ | 脚本通过 `GetBlackboard`/`SetBlackboard` Syscall 读写，无语言层改动 |
| **C: 连招描述脚本 (V2 理想形)** | L2 + L3 或 L4 | ⚠️ 部分 | 需要进一步分析 |

**方案 C 的理想形分析**：

连招描述脚本的核心诉求是：**一个集中管理连招链的地方，决定当前哪些后续技能可以激活**。

```
理想形 1: 查询式
─────────────────
combo_ground.ffs 是一个常驻运行的"连招管理器"实例
↓ 技能脚本通过 Syscall 查询:
  skill_light_punch.ffs → QueryComboAllow("heavy_punch") → 1/0
                        
需要: L3 (跨模块共享状态) + 新 Syscall 协议
VM 改动: 中等
```

```
理想形 2: 声明式（include 模板）
─────────────────────────────────
shared/combo_ground_chain.ffs 定义连招表:
  const CHAIN_LP_TO_HP: int = 1
  func canChainTo(skillId: int): int { ... }

每个技能 include 这个连招表:
  include "shared/combo_ground_chain"
  func checkEnter(): int {
      if canChainTo(SKILL_HEAVY_PUNCH) == 0 { return 0 }
      ...
  }

需要: L1 + L2 (仅 Phase 1)
VM 改动: 无
```

```
理想形 3: 混合式
─────────────────
连招窗口判断在脚本内 (L1 模块变量追踪帧数)
连招关系表通过 include 共享 (L2)
跨技能的连招计数器通过黑板 Syscall 共享 (已有)

需要: L1 + L2 + 黑板 Syscall
VM 改动: 无
```

**评估**：

| 理想形 | 逻辑集中度 | 实现复杂度 | Phase 1 可实现 |
|--------|-----------|-----------|---------------|
| 1: 查询式（独立管理器） | ⭐⭐⭐ 最集中 | ⭐⭐⭐ 高 — 需跨模块通信 | ❌ |
| 2: 声明式（include 模板） | ⭐⭐ 集中 | ⭐ 低 — 纯编译期 | ✅ |
| 3: 混合式 | ⭐⭐ 集中 | ⭐ 低 — L1+L2+现有 Syscall | ✅ |

**结论**：**理想形 3（混合式）是最务实的推荐**。

- 连招关系表（哪些技能可以衔接）→ include 共享 const/func（L2）
- 连招内部状态（当前段数、窗口帧计数）→ 模块变量（L1）
- 跨技能通信（"轻拳命中了"通知"重拳可以衔接"）→ 黑板 Syscall（已有）
- Phase 1（L1+L2）即可覆盖，无需等 L3/L4

### 向 VM/语言方提交的需求背景与建议方向

> 📌 本节面向 **语言方**（FFVM 编译器/运行时设计者）。需求侧只提供背景、痛点和建议方向；最终方案由语言方从语言视角取舍，或根据需求完全整合。
>
> 待语言方确认后，同步更新到 `Docs/VM_Summary.md` §5.2 / §7。

#### 一、需求痛点：当前 FFS 语言缺失导致的问题

| # | 痛点 | 受阻需求 | 当前变通 | 变通代价 |
|---|------|---------|---------|---------|
| **P1** | **函数间无法共享变量** — FFS 无模块级 `var`，checkEnter() 和 step() 即使在同一实例内也无法通过语言层共享状态 | SK7 有状态条件（蓄力、连招窗口）、SK11 多阶段 Phase index | 宿主黑板 Syscall 绕行 | 每次读写额外 Syscall 开销；状态散落宿主侧，不符合 SK12"脚本内闭环"原则 |
| **P2** | **脚本间无法复用声明** — 无 include/import，多个技能脚本的共享属性（优先级 const、辅助函数）必须手动复制 | SK13 属性共享（M1~M5 混入需求）、连招关系表复用 | 每个脚本手动粘贴相同 const/func | 维护成本高，修改时易遗漏；脚本数量增长后不可持续 |
| **P3** | **跨脚本运行时无法共享数据** — 技能 A 的命中结果无法直接通知技能 B 的 checkEnter | SK10 连招共用环境（"轻拳命中→重拳可衔接"） | 宿主黑板 Syscall（已有框架） | 可接受 — 黑板 Syscall 是成熟方案，但逻辑分散于宿主与脚本两侧 |
| **P4** | **跨脚本无法调用函数** — 无法从技能脚本调用另一个模块的函数 | SK10 理想形 1（连招管理器独立脚本） | 无直接变通，只能用理想形 2/3 绕过 | 放弃"连招逻辑集中到一个脚本"的理想形 |

> **核心观察**：P1+P2 是当前最紧迫的缺失，P3 有成熟变通（黑板 Syscall），P4 是远期理想。

#### 二、三个建议方向

##### 方向 A: 长期理想（最完整）

**思路**：一步到位，让 FFS 具备模块变量、include、跨模块共享变量、跨模块函数调用全部能力。

| 步骤 | 对应痛点 | 内容 | 改动范围 | 复杂度 |
|------|---------|------|---------|--------|
| A-1 | P1 | **模块变量 (L1)** — Parser 顶层 `var` + 编译器保留寄存器段 | 编译器 | ⭐⭐ |
| A-2 | P2 | **include (L2)** — 预处理器递归展开 + const/struct/func/var 重定义规则 | 编译器（新 Preprocessor 模块） | ⭐⭐ |
| A-3 | P3 | **跨模块共享变量 (L3)** — 共享内存区域或专用寄存器段，多实例可读写同一份数据 | ⚠️ 编译器 + VM 运行时 | ⭐⭐⭐ |
| A-4 | P4 | **跨模块函数调用 (L4)** — ModuleTable + CALL 指令扩展 | ⚠️ 编译器 + VM 运行时 | ⭐⭐⭐ |

**优点**：覆盖所有需求，含 SK10 理想形 1（连招管理器独立脚本）。
**缺点**：A-3/A-4 需要 VM 运行时改动，复杂度高，周期长。

**细则**：
- A-1 寄存器段选项：固定段（r56~r63，8 槽）vs 编译器按需分配（更灵活但复杂度更高）
- A-1 初始化：SpawnInstance 时执行 `_init` 代码段 vs 编译器将初始值写入常量表并静态填充
- A-2 路径解析：项目根相对路径（推荐）vs 当前文件相对路径
- A-3 共享机制：专用共享寄存器段 vs 黑板 Syscall 升级为语言内建 vs 显式 `shared var` 语法
- A-4 调用协议：编译期链接（include 变体，代码复制）vs 运行时分派（真跨模块 CALL）

---

##### 方向 B: 最速/改动最小

**思路**：只解决最紧迫的 P1+P2，P3 用已有黑板 Syscall 覆盖，P4 暂不处理。

| 步骤 | 对应痛点 | 内容 | 改动范围 | 复杂度 |
|------|---------|------|---------|--------|
| B-1 | P1 | **模块变量 (L1)** — 同 A-1 | 编译器 | ⭐⭐ |
| B-2 | P2 | **include (L2)** — 同 A-2 | 编译器 | ⭐⭐ |
| B-3 | P3 | **黑板 Syscall 正式化** — Get/SetBlackboard(key, value) 作为标准 Syscall | 宿主 Syscall 注册 | ⭐ |

**优点**：VM 运行时零改动，纯编译器工作，可快速交付。P3 用黑板 Syscall 过渡，实测证明可行（KOF98 连招 V1 方案）。
**缺点**：放弃 SK10 理想形 1。连招逻辑分散在技能脚本 + 宿主黑板两侧。

**细则**：
- B-1/B-2 细则同 A-1/A-2
- B-3 黑板 Key 类型：int ID（编译期静态分配）vs string key（灵活但有 hash 开销）
- B-1 和 B-2 无依赖关系，可并行实施

---

##### 方向 C: 折衷（长期理想 + 最速分期）

**思路**：先交付方向 B 全部内容解决紧迫需求，为方向 A 的 A-3/A-4 预留设计空间但不立即实现。

| 阶段 | 步骤 | 内容 | 时间线 |
|------|------|------|--------|
| **Phase 1（立即）** | C-1 = B-1 | 模块变量 (L1) | 近期 |
| | C-2 = B-2 | include (L2) | 近期 |
| | C-3 = B-3 | 黑板 Syscall 正式化 | 近期（或随 C1 宿主集成） |
| **Phase 2（需求驱动）** | C-4 = A-3 | 跨模块共享变量 (L3) | 当 P3 黑板 Syscall 被证明不够时 |
| **Phase 3（远期）** | C-5 = A-4 | 跨模块函数调用 (L4) | 仅在连招管理器独立脚本被确认为必要时 |

**优点**：兼顾速度和远期扩展。Phase 1 纯编译器改动，风险低。Phase 2/3 按需触发，避免过度设计。
**缺点**：Phase 2/3 可能需要对 Phase 1 设计做部分回溯调整（如寄存器段布局变更）。

**预留设计空间建议**（供语言方参考）：
- L1 寄存器段划分时，预留"未来可能扩展为跨实例共享段"的可能性
- include 的重定义规则设计时，考虑未来 `import` 语义是否需要与 include 区分
- 黑板 Syscall 的 Key 分配机制，考虑未来编译器可能接管自动分配

---

#### 三、需求侧推荐

**推荐方向 C（折衷）**，理由：

1. Phase 1（L1+L2）已覆盖 KOF98 当前全部需求 — SK7 有状态条件、SK11 Phase、SK13 属性共享、SK10 连招（混合式理想形 3）
2. P3 黑板 Syscall 是成熟且已验证的方案，不阻塞业务推进
3. P4 跨模块函数调用在 KOF98 范围内尚无刚性需求
4. Phase 2/3 按需触发 — 如果未来项目（1vN、NvN）证明黑板不够，再升级

**但最终决策权在语言方** — 语言方可能发现 A-3/A-4 的某些底层机制可以和 A-1/A-2 统一设计，一次性实现更优于分期。这种情况下方向 A 可能整体成本更低。

#### 四、待语言方回复

1. **方向选择**：A / B / C / 或语言方自己的整合方案？
2. **L1 寄存器段**：固定段（r56~r63）vs 按需分配？
3. **L1 初始化**：`_init` 代码段 vs 编译器常量填充？
4. **L2 路径解析**：项目根 vs 当前文件相对？
5. **实施顺序**：L1 先 / L2 先 / 并行？
6. **是否有语言层面的统一设计**：能否将 L1~L4 中若干项合并为一个统一机制？

---

## 第 12 轮：OOP/ECS 数据兼容、硬直期脚本进入、跨脚本使用模式

> **日期**：2026-04-08
> **来源**：用户提出 3 个新议题 — (1) OOP 数据原则与未来 ECS 兼容性；(2) 硬直期间是否让脚本继续进入；(3) 跨脚本 VM 使用模式推荐。

---

### Q1: OOP 数据封闭原则与未来 ECS Component 需求是否冲突？ ✅ 已关闭

> **用户第 12.5 轮反馈**：确认无害，丢弃。
>
> **处理**：Q1 确认关闭，无后续行动。

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

---

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

// 这么解释的前提是: 认为宿主时间轴暂停, tick 不推进是不变事实.
// [意外情况]是否存在另一种情况, 同一个数组有一段逻辑要求即使处于硬直中, 也会持续对周围的敌人造成伤害. 看起来是有可能的. 这已经破坏了解释的前提.
// [担忧]进一步麻烦的地方是, 我们可以通过为虚拟机的某个调用配置`是否在硬直中推进`(很好, 这看起来是纯业务层的配置), 但是这样我们好像放弃了把`[意外情况]`的原定效果和普通情况配置在一起的权力, 天然要求他们在不同的tick里. 这种放弃是否合理, 是否符合直觉, 是否回来配置麻烦.
// 并且这对用户造成了心智负担. 看到yield用户需要思考这个yield 是否会在硬直中执行. (不过另一方面, 用户完全可以理解成, 这就是虚拟机tick 而不必考虑是否硬直)
// `[担忧]` 有进一步的担忧, 这种强迫分离函数, 导致原想我们想解决的<强制平行配置导致大量重复配置>的感觉又回来了 - 我们被迫为了语法上的不支持, 导致要分离书写这两种逻辑. 
// 更害怕这成为物理限制, 不得不这么做

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
### Q2: 硬直期间是否让脚本进入（时间轴暂停时的 VM Tick 策略）

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
### Q3: 跨脚本 VM 使用模式推荐

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

<details>
<summary>📋 讨论历史</summary>

#### 第 12.5 轮（当前）

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
