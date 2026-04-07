# KOF98 技能 FFS 脚本化讨论

> **状态**：🔄 讨论中（SK1~SK12 已收敛，其中 SK3 待性能验证、SK7 第 8 轮修正同实例模型；SK13 第 7 轮深化，待路径解析决议）
> **来源**：需求讨论 — 将 host-side 技能迁移为 FFS 脚本驱动
> **日期**：2026-04-07（第 8 轮更新）

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
| SK7 | 技能条件入口 | 🔄 | 方案 D′ — 独立 checkEnter()/step() 函数，**同一 VM 实例** |
| SK8 | 碰撞框 Syscall | ✅ | SetHitbox/SetHurtbox/ClearHitbox/SetPushBox |
| SK9 | 受击标记 | ✅ | 分组 mask 混合方向正确 |
| SK10 | 连招共用环境 | ✅ | V1: 黑板变量；理想: 连招描述脚本 |
| SK11 | 多阶段技能 | ✅ | Phase 是脚本内行为，提供便利但不强制 |
| SK12 | ECS 数据归属 | ✅ | 脚本内闭环 → VM；外部需读取 → Syscall 推送到宿主 |
| SK13 | 属性脚本共享 | 💬 | 方案 A — 预处理器 `include`，全量展开（const+struct+func） |

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

// 这里和我想的还是由出入, 理想情况应该是 一个脚本编译出一个虚拟机 rom. 
// 对虚拟机调用不同的方法大致为 vm.funcs[stepip], vm.funcs[conditionip], 为什么会有无法复用变量的结果论. 当前虚拟机本身的对此存在硬阻碍吗

**结论**：方案 D′ — 独立 `checkEnter()` + `step()` 函数，**同一 VM 实例**。

> ⚠️ **第 8 轮修正**：第 7 轮分析误将 checkEnter 和 step 描述为"独立 VM 实例"，这是**严重误解**。设计意图始终是：一个技能 = 一个 VM 实例贯穿整个生命周期（从条件检查到执行完毕）。

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
| **函数局部变量** | 各自独立声明、独立编译。checkEnter 的 `var a` 和 step 的 `var a` 是不同的寄存器槽位 |
| **寄存器残留** | checkEnter 写入的寄存器值在 redirect 后仍存在。step 可以通过约定使用特定寄存器读取 |
| **实际可用性** | 当前 FFVM 不支持跨函数的命名变量共享。如需跨函数传值，应通过 Syscall 读写宿主黑板 |

**结论**：同实例保留了"将来支持跨函数状态共享"的可能性（如全局变量机制），但当前阶段跨函数数据传递仍推荐走宿主黑板 Syscall。

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

### 复合条件（第 7 轮议题修正）
// 理想情况是路径3: 全局变量, 匹配全局方法. 另外连招共用环境可能被全局机制包括在内了
// 探索全局解决办法, 遵循数据尽量放在虚拟机内的原则

> **第 8 轮修正**：第 7 轮分析基于"独立实例"假设得出"路径 2 不可行"的结论。同实例模型下需重新评估。

| 模式 | 行为 | 适用场景 |
|------|------|---------|
| **无状态检查** | 每次 `checkEnter()` 都检查 A∧B | 简单条件（按键+姿态）— 90%+ 技能 |
| **有状态检查** | 首次 A 满足后记录，后续只检查 B | 蓄力、连招窗口 |

**有状态检查的实现路径**：

| 路径 | 机制 | 同实例下可行性 | 评估 |
|------|------|--------------|------|
| **路径 1：宿主黑板** | checkEnter 通过 Syscall 读写宿主 component | ✅ 不依赖实例模型 | **推荐** — 跨脚本可见、重置逻辑集中 |
| **路径 2：寄存器残留** | checkEnter 将状态写入特定寄存器，下次调用时读取 | ⚠️ 理论可行但不可靠 — checkEnter 每次被 spawn 为新调用，寄存器会被初始化覆盖 | 不推荐 |
| **路径 3：未来全局变量** | FFVM 增加脚本级全局变量（跨函数、跨调用持久） | ⚠️ 需 VM 改动，当前不存在 | 预留可能性 |

**结论**：有状态检查仍推荐**路径 1（宿主黑板）**。同实例模型的真正优势不在于跨函数状态共享，而在于：
1. **效率**：条件通过后无需 destroy + re-spawn，直接 redirect
2. **语义清晰**：一个技能 = 一个实例 = 一个完整生命周期
3. **未来扩展**：若 FFVM 支持全局变量，同实例模型天然受益

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

#### 第 8 轮（当前）

修正实例模型：checkEnter 和 step 应为**同一 VM 实例**。详见本节主结论。

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

### 脚本书写风格约定

- **必须提取为变量**：多次引用的值（帧数 `totalFrames`、攻击窗口边界）
- **可内联**：仅出现一次的常量参数（effectId, groupId 等），注释说明含义
- **注意**：`while f < N` 中的 `N` 若在 `BeginAction` 中已声明则应统一变量

### S01 — 站立待机 (Idle)

```ffs
func main() {
    BeginAction(1, -1)    // -1 = 无限循环
    defer { EndAction() }

    while true {
        yield
    }
}
```

### S04 — 近拳 (LightPunch)

```ffs
func main() {
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
func main() {
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
