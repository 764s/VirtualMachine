# KOF98 技能 FFS 脚本化讨论

> **状态**：💬 讨论中
> **来源**：需求讨论 — 将 host-side 技能迁移为 FFS 脚本驱动
> **日期**：2026-04-06

---

## 一、背景

当前所有技能逻辑均为 host-side C#（`SkillDef.CanActivate`/`CanContinue`/`OnFrame` lambda）。
VM 桥接层已就绪（`GameVMBridge` + `GameSyscalls` ~40 syscall），但 `KOF98/Scripts/` 目录为空，零个 .ffs 脚本被编写或加载。

**目标**：讨论并确定首批用 FFS 脚本实现的技能范围。

---

## 二、技能全集分类

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

## 三、首批 FFS 脚本 ✅

### 3.1 选择原则

根据 D_GameArchitecture.md §2.3 VM 应用分级（稳赚/模糊/稳亏）：

| 原则 | 说明 |
|------|------|
| **优先覆盖主循环** | idle → walk → attack → hit → recover，验证完整生命周期 |
| **优先覆盖多样性** | 选择代表不同脚本模式的技能（循环/有限帧/条件驱动/物理驱动） |
| **暂不涉及复杂系统** | 抓投（需双方协调）、闪避（需无敌帧标记）暂不纳入首批 |
| **不以 skill_114/skill_25 为目标** | 提取其参数表示模式作为参考，首批脚本针对基础技能 |

### 3.2 首批脚本（8 个） ✅

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

> 命名规则已确认：`skill_<英文名>.ffs`

### 3.3 脚本模式分类

从上述 8 个脚本中提取出的 FFS 脚本典型模式：

| 模式 | 特征 | 代表技能 | 核心 Syscall |
|------|------|---------|-------------|
| **循环型** | `while true { yield }`, 输入驱动退出 | Idle, Walk | GetInput, SetVelocity |
| **有限帧型** | `while f < N { yield }`, 帧计数驱动 | LightPunch, HitHigh | BeginAction, EndAction |
| **物理型** | 初始速度 + 等待落地 | Jump | SetVelocity, IsGrounded |
| **被动型** | 宿主切换层激活, 脚本只播放动作 | HitHigh, Knockdown | BeginAction, SpawnEffectSelf |
| **攻击型** | 帧窗口内命中检测 + 分支处理 | LightPunch, CrouchPunch | CheckAttackHit, ApplyDamage |

---

## 四、参考脚本参数模式提取

> 从 `skill_114`（飞燕旋风腿）和 `skill_25`（上盘被击中）提取通用参数表示，作为首批脚本的 Syscall 参数设计参考。

### 4.1 动作声明

```
BeginAction(actionId, totalFrames)
defer { EndAction() }
```

- `actionId`: 技能定义 ID，宿主用于查找动画/碰撞框数据
- `totalFrames`: 动作总帧数，宿主用于动画播放
- `defer + EndAction()`: 确保技能结束时清理（无论正常结束还是被 Kill）

### 4.2 伤害参数与受击标记

// 受击标记实际使用独立数字, 但仍使用多个mask, 让一定不会影响的标记在一个mask里, 以紧凑表示.

> ⚠️ 原设计将"伤害参数"和"受击标记"混在 `damageType` 一个字段里，需要拆分。

**两个概念的区分**：

| 概念 | 用途 | 生命周期 |
|------|------|---------|
| **伤害参数** (Damage Params) | 用于计算伤害数值（系数、属性克制等） | 命中瞬间消费 |
| **受击标记** (HitReactionTag) | 攻击方附加到受击方，受击方据此决定进哪种受击技能 | 附加→受击方读取→消费 |

**受击标记的核心问题**：

如果用 bitmask 复合标记（多个攻击效果叠加），mask 只能混入不能保证分离，叠加后容易出现不可预测的冲突。例如：标记 A(0x01) + 标记 B(0x02) 叠加为 0x03，但 0x03 可能被误匹配为完全不同的标记 C。

**两种解决思路**：

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **枚举标记** | 每种受击反应一个唯一 ID（不做 mask 组合） | 确定性强，无叠加冲突 | 标记数量可能多（但格斗游戏受击类型有限） |
| **分层标记** | 将标记拆成独立维度（如：部位×强度×特殊），每个维度独立值 | 语义清晰，可组合 | Syscall 参数增多 |

**建议重命名**：`damageType` → 拆为两个概念：

```
// 伤害参数：用于数值计算
ApplyDamage(targetId, coefficient)

// 受击标记：通知受击方选择受击技能
ApplyHitReaction(targetId, reactionTag)
```

- `coefficient`: 伤害系数（不是绝对值），宿主用公式换算
  - skill_114: 5 / 7 / 10（三段递增）
  - skill_25: 无（受击脚本不造成伤害）
- `reactionTag`: 受击反应标记（唯一枚举 ID，不做 mask 组合）
  - `HIT_HIGH_LIGHT` = 上盘轻击受击
  - `HIT_HIGH_HEAVY` = 上盘重击受击
  - `HIT_KNOCKDOWN` = 击躺
  - `HIT_GUARD` = 格挡（无受击切换，仅削血）

> 💬 受击标记的详细设计（枚举 vs 分层、叠加规则）需要单独开讨论。登记为 **SK9**。

### 4.3 硬直参数

```
ApplyHitstun(targetId, startFrame, durationFrames, level, shakeFlag)
```

- `startFrame`: 硬直开始的延迟帧（0=立即, 5=延迟5帧）
- `durationFrames`: 硬直持续帧数（skill_114 全部为 12 帧）
- `level`: 硬直等级（0=标准）
- `shakeFlag`: 是否播放震动（1=是）

### 4.4 击退参数

两种击退模式：

```
// 模式 A: 距离+时间 (定距移动)
ApplyHorizKB_Dist(targetId, distance, durationFrames)
// skill_114: 1米/5帧, 0.8米/8帧

// 模式 B: 速度 (直到着地)
ApplyHorizKB_Speed(targetId, speed)
// skill_114: 4.998, 7.998 (空中/击飞时)
```

垂直击退:
```
ApplyVertKB(targetId, speed, durationFrames)
// skill_114: 16.998/90帧, 18/120帧
```

自身位移:
```
ApplySelfHorizKB(distance, durationFrames)
ApplySelfVertKB(initialSpeed, acceleration)
// skill_114: 水平6.5米/29帧, 垂直v0=12/a=9
```

### 4.5 互斥分组

```
var mutex1: int = 0
if mutex1 == 0 {
    // 检测命中...
    mutex1 = 1
}
```

- 用局部变量实现（非 Syscall）
- 同一攻击段只生效一次
- 首批基础技能（单段攻击）不需要互斥，但保留模式认知

### 4.6 能量系数

```
SetEnergyCoeff(multiplier)
```

- 临时修改下次 ApplyDamage 的能量获取倍率
- skill_114: 防御时 x2, 第三段防御 x3
- 首批脚本可暂不使用

---

## 五、首批脚本伪代码示例

### 5.0 脚本书写风格约定

> 对于字面量，适当使用变量声明。但脚本有其特殊性，写多了也啰嗦，因此设定大致标准适当权衡。

**标准**：
- **必须提取为变量**：多次引用的值（帧数 `totalFrames`、攻击窗口边界）
- **可内联**：仅出现一次的常量参数（effectId, groupId, damageType 等），在注释中说明含义
- **特别注意**：`while f < N` 中的 `N` 若在 `BeginAction` 中已声明则应统一变量，避免两处字面量不一致

### S01 — 站立待机 (Idle)

```ffs
func main() {
    BeginAction(1, -1)    // -1 = 无限循环
    defer { EndAction() }

    while true {
        // 空循环, 等待宿主裁决层检测到条件后切换技能
        yield
    }
}
```

**要点**：循环型技能最简形式。退出由宿主裁决层控制（Kill VM 实例）。

### S04 — 近拳 (LightPunch)

```ffs
func main() {
    var frames: int = 20

    BeginAction(101, frames)      // actionId=101
    defer { EndAction() }

    var hit: int = 0
    var f: int = 0
    while f < frames {
        // 攻击窗口: 帧 [4, 8)
        if f >= 4 && f < 8 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 {
                ApplyDamage(t, 3, 102)           // 系数3, 上盘轻击
                ApplyHitstun(t, 0, 10, 0, 1)     // 立即, 10帧硬直
                ApplyHorizKB_Dist(t, 0.5, 5)     // 击退0.5米/5帧
                SpawnEffectHit(3001, 30)
                hit = 1
            }
        }
        f = f + 1
        yield
    }
}
```

**要点**：`frames` 变量统一 `BeginAction` 和 `while` 循环的帧数。单次参数（damageType=102 等）内联 + 注释。

### S06 — 上受击 (HitHigh)

```ffs
func main() {
    var frames: int = 20

    BeginAction(25, frames)       // actionId=25
    defer { EndAction() }

    SpawnEffectSelf(4001, 60)     // 受击特效

    var f: int = 0
    while f < frames {
        f = f + 1
        yield
    }
}
```

**要点**：纯被动型，最简形式。参考 skill_25 模式。

---

## 六、讨论：硬直实现机制 💬

> 来源：Syscall 需求评估时发现硬直机制需要更深入讨论。

### 6.0 前置模型：角色逻辑时间轴

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

**技能与时间轴的关系**：当前技能决定时间轴的长度。技能脚本的 `yield` 循环每次消费一个时间轴帧。

### 6.1 硬直的时间轴语义

硬直 = 角色时间轴暂停推进 N 帧。具体表现：

- **攻击硬直**：攻击命中瞬间，攻击方时间轴暂停（攻击停顿，表现为命中的"打击感"）
- **受击硬直**：受击方时间轴暂停（受击停顿，表现为被击中的"僵硬"）
- **两者独立**：攻击方硬直帧数和受击方硬直帧数可以不同

硬直的附加效果：
- 阻止部分技能开始（硬直中不可发动新技能）
- 可作为某些技能的开始条件（如：硬直结束后自动进入受击恢复）

### 6.2 实现方案

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 宿主时间轴暂停** | 宿主 `HitstunFrames > 0` 时不推进角色 Tick，VM 脚本的 `yield` 也不消费帧 | 硬直期间脚本自然"冻结"，无需额外逻辑 | 需要宿主在 Tick 层面拦截 |
| **B: 受击技能驱动** | 受击技能脚本内循环等待硬直帧数耗尽 | 脚本可自定义硬直期间行为 | 硬直帧数需要传入脚本 |

### 6.3 决定：方案 A — 时间轴暂停 ✅

> **决定理由**：硬直期间脚本可以有自定义 if 行为（冻结的是宿主 Tick，不是脚本逻辑本身）。

**两层配合**：
1. **宿主层**：`ApplyHitstun()` → 设置 `Character.HitstunFrames` → 暂停角色时间轴推进
   - 暂停期间：VM 脚本不 Tick（yield 不消费），动画冻结，碰撞框保持
   - 时间轴暂停天然实现"不可操作"（脚本冻住 = 不处理输入）
2. **技能层**：攻击方 `ApplyHitstun()` 同时附加受击标记 → 宿主在硬直结束后根据标记触发受击技能切换
3. **安全兜底**：`HitstunFrames` 倒计时到 0 后，若没有受击技能接管，自动恢复 idle

这样硬直的"不可操作"由时间轴暂停天然保证，不需要优先级或输入屏蔽逻辑。

### 6.4 `yield` 参数化与帧细分

> **问题发现**：引入硬直后，需要区分"推进到下一个驱动帧"和"推进到下一个逻辑帧"。可能需要 `yield N` 形式来细分。

**FFVM 当前支持情况**：

| 语法 | 编译为 | 语义 | 状态 |
|------|--------|------|------|
| `yield` | `WAIT 1` | 暂停 1 个 Tick，下次 Tick 恢复执行 | ✅ 已支持 |
| `wait(N)` | `WAIT N` | 暂停 N 个 Tick，宿主每 Tick 递减 WaitCounter，归零后恢复 | ✅ 已支持 |
| `wait_for(instanceId)` | `WAIT_FOR reg` | 等待另一个 VM 实例执行完毕后恢复 | ✅ 已支持 |

**`yield` 就是 `wait(1)` 的语法糖**，底层都是 `WAIT` 指令 + `WaitCounter`。

**硬直期间的帧细分方案**：

在方案 A（时间轴暂停）下，硬直期间宿主不调用 `VMWorld.Tick()`，因此 `yield` / `wait(N)` 都不会推进。区分"驱动帧"和"逻辑帧"的需求由**宿主层控制**：

```
宿主每物理帧:
  if HitstunFrames > 0:
      HitstunFrames--
      // 不调用角色 VM Tick → 脚本冻结（驱动帧不推进）
      // 但可选: 调用特殊的 HitstunTick → 推进硬直逻辑帧
  else:
      角色正常 Tick → VM 脚本 yield 消费（驱动帧推进）
```

**结论**：`yield N` 的需求已由 `wait(N)` 覆盖，无需新增语法。硬直帧细分由宿主层 Tick 策略决定，不涉及 VM 语言改动。

---

## 七、讨论：技能条件与裁决机制 💬

> 来源：对 `CanActivate` 是否迁移到 VM 的分歧。

### 7.1 分歧说明

| 观点 | 描述 |
|------|------|
| **原方案（Agent）** | `CanActivate` 保留在宿主 C#，技能条件是纯判断（≤3行），属于"稳亏"场景。脚本假设条件已通过 |
| **用户倾向** | 技能条件也进 VM。并且条件逻辑应在**技能 VM 内部**（而非独立的裁决 VM 实例） |

### 7.2 裁决机制两大方向

> 共识：具体的条件判断都在虚拟机内实现，两种方向无分歧。核心区别在于**宿主如何决定尝试哪些技能**。

| | **方向 A: 状态机转换表** | **方向 B: 优先级分桶** |
|---|---|---|
| **核心思路** | 预定义"状态→状态"转换通道，配合 `any` 特殊转换兜底 | 将技能按优先级分桶，设定启动优先级和打断优先级，配合技能自定义的转换条件 |
| **候选池** | 当前状态的出边列表（预编译，有限集合） | 当前优先级桶内所有技能（动态过滤） |
| **性能** | ✅ 候选池小，遍历开销低 | ⚠️ 优先级抽象性导致大量技能需遍历条件 |
| **可维护性** | ⚠️ 连接数量可能爆炸（N种状态×M种转换），爆炸后难以调整 | ✅ 添加新技能只需设定优先级+条件，不改全局转换表 |
| **灵活性** | 结构化，适合规则明确的系统 | 灵活，适合规则频繁变化的系统 |
| **代表作** | Unreal GAS 的 AbilityTag+BlockTag | KOF98 原版（优先级+条件表） |

### 7.3 工业实践分析：分层候选池方案 ✨

**格斗游戏工业标准实际是 A 和 B 的混合体**，我们称之为"分层候选池"：

```
┌─────────────────────────────────────────────────────┐
│ 第 1 层: 姿态分组 (类似 A 的状态机)                      │
│                                                     │
│   [地面] ──→ 可选技能: 移动/攻击/防御/跳             │
│   [空中] ──→ 可选技能: 空中攻击/空中防御             │
│   [倒地] ──→ 可选技能: 起身/受身                     │
│   [硬直] ──→ 可选技能: (无, 或仅限被动受击)          │
│                                                     │
│ 第 2 层: 优先级排序 (类似 B 的优先级机制)               │
│                                                     │
│   在当前姿态的候选池内, 按优先级从高到低尝试:          │
│   受击(1) > 防御(2) > 必杀(3) > 普攻(4) > 移动(5)   │
│                                                     │
│ 第 3 层: 脚本条件检查 (VM 内)                        │
│                                                     │
│   每个候选技能的脚本第一帧执行 CanActivate 条件:       │
│   通过 → 激活 / 失败 → 尝试下一个                     │
│                                                     │
│ 补充: 取消窗口 (Cancel)                              │
│                                                     │
│   当前技能可在特定帧窗口内声明"允许被某些技能打断":      │
│   相当于临时向候选池添加额外技能 (显式 opt-in)         │
└─────────────────────────────────────────────────────┘
```

**为什么这个方案解决了 A 和 B 的问题**：

1. **姿态分组限制候选池大小** → 解决 B 的"遍历所有技能"性能问题
   - 地面姿态约 15-20 个候选技能，不是全部 60+
2. **优先级排序替代 N×N 转换表** → 解决 A 的"连接爆炸"问题
   - 新增技能只需指定 `姿态` + `优先级` + `脚本条件`
3. **取消窗口是显式 opt-in** → 不增加全局复杂度
   - 只有当前技能声明允许取消时，额外候选才加入

### 7.4 推荐方案：分层候选池

> ⚠️ **已知局限**：分层候选池是缓解而非根治。如果姿态拆分不理想，巨型姿态组内仍面临相同的性能/维护问题。当前先按此方案推进，后续迭代中根据实际技能数量评估是否需要更精细的子分组或其他优化。

```
宿主裁决层 (每帧):
  1. 查询当前姿态 (Stance) → 获取该姿态的候选技能列表
  2. 追加当前技能声明的取消窗口候选 (如有)
  3. 按优先级排序候选列表
  4. 通用规则快速过滤 (硬直中/优先级不足/冷却中 → 跳过)
  5. 对剩余候选, 逐个 spawn VM 实例并执行第一帧:
     - 通过 → 脚本继续 (BeginAction...)
     - 失败 → 脚本 return → 宿主检测到立即完成 → 尝试下一个
  6. 全部失败 → 保持当前技能
```

**数据结构草案**：

```csharp
// 姿态定义
enum Stance { Grounded, Airborne, Crouching, Knockdown, Hitstun, Dead }

// 技能定义扩展
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

### 7.5 技能条件位置（已收敛）

条件写在技能脚本开头（方案 C），具体条件判断在 VM 内。

```ffs
// skill_light_punch.ffs
func main() {
    // --- 条件检查 (第一帧) ---
    var input: int = GetInput()
    var grounded: int = IsGrounded()
    if (input & INPUT_LP) == 0 || grounded == 0 {
        return    // 条件不满足, 立即退出
    }

    // --- 条件通过, 执行技能 ---
    var frames: int = 20
    BeginAction(101, frames)
    defer { EndAction() }
    // ...
}
```

**通用规则**（宿主侧，所有技能共享）：
- 姿态匹配（候选技能必须在当前姿态的 AllowedStances 中）
- 优先级判断（InterruptPriority >= 当前技能的 ActivationPriority）
- 硬直/倒地/死亡状态互斥

**特殊规则**（脚本侧，技能自定义）：
- 具体输入要求（需要某个按键组合）
- 距离/位置条件（如投技需要近距离）
- 资源条件（如超必杀需要能量）
- 连招窗口（如取消窗口内才可衔接）

---

## 八、讨论：碰撞框数据来源 💬

> 来源：SK3 决定碰撞框暂时在脚本内设置。

### 8.0 设计原则

**碰撞数据完全由技能决定**。此时技能约等于状态机中的状态，理想情况下每个状态包含完整的碰撞状态定义。

这意味着：
- 每个技能脚本负责声明自己在各帧的碰撞框（受击框、攻击框、推挤框）
- 宿主不做碰撞框的静态预定义（`CollisionFrames` 将被脚本 Syscall 替代）
- 技能切换时，旧技能的碰撞框自动清除（由 `EndAction` 或新技能的 `BeginAction` 保证）

### 8.1 决定

碰撞框数据暂由脚本内 Syscall 设置（而非宿主 `CollisionFrames` 静态定义）。

### 8.2 影响

- 需要新增 Syscall：`SetHitbox(groupId, x, y, w, h)`, `SetHurtbox(x, y, w, h)` 等
- 脚本内碰撞框定义可能较冗长，需考虑**书写便利性**
- 将来可能用到 FFVM 自定义结构体来封装碰撞框参数，减少 Syscall 参数数量

### 8.3 示例：脚本内碰撞框

```ffs
// 方案 A: 逐参数 Syscall
if f >= 4 && f < 8 {
    SetHitbox(1001, 0.2, 0.3, 0.4, 0.3)   // groupId, x, y, w, h
}

// 方案 B: 结构体参数 (需要 FFVM 结构体支持)
// var box: HitboxDef = { groupId: 1001, x: 0.2, y: 0.3, w: 0.4, h: 0.3 }
// SetHitbox(box)
```

> 结构体方案取决于 FFVM 当前的结构体支持程度。B-γ7 (SN1 嵌套结构体) 已在计划中。

---

## 九、新增 Syscall 需求评估

// 合理, 但需要考虑将尽可能多的内容留在虚拟机内, 最大化利用虚拟机的回滚机制(序列化机制)

对照首批 8 个脚本所需的 Syscall，与当前已有的 ~40 个 Syscall 对比：

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
| 蹲姿切换 | — | ❓ 可能需要 `SetStance` 或 `SetPushBox` |
| 倒地状态 | — | ❓ 可能需要 `SetKnockdownState` / `SetInvincible` |
| 技能条件查询 | GetInput, IsGrounded 等 | ✅ 足够（条件在脚本内） |

**结论**：前 4 个脚本（S01~S04）可直接用现有 Syscall 实现（碰撞框用现有 `CollisionFrames` 过渡）。S05~S08 及碰撞框脚本化需要新增少量 Syscall。

---

## 十、宿主侧变更评估

将技能从 host-side 迁移到 FFS 脚本需要宿主侧的配合：

| 变更 | 说明 | 影响范围 |
|------|------|---------|
| `SkillDef.VMModuleSlot` 设置 | 技能定义指向已编译的 .ffs 模块 | CharacterData 技能注册 |
| `CanActivate` → 脚本内条件 | 技能条件迁移到脚本第一帧检查（§七.5） | SkillManager 需检测"第 0 帧完成"并回退 |
| `CanContinue` → 脚本控制 | 循环技能退出由脚本决定（通过结束/return）或宿主 Kill | SkillManager 裁决层 |
| `OnFrame` → 删除 | 每帧逻辑由 FFS 脚本 `yield` 循环驱动 | 不再需要 |
| `CollisionFrames` → 脚本内设置 | 碰撞框数据迁移到脚本 Syscall（§八） | 需新增 Syscall |
| `GameVMBridge.ActivateSkillVM` | 技能激活时自动 spawn VM 实例 | 已实现 |
| 裁决层改造 | 实现分层候选池机制：姿态分组+优先级+脚本条件（§七.4） | SkillManager 重构 |
| `SkillDef` 扩展 | 新增 `AllowedStances`, `ActivationPriority`, `InterruptPriority` | 技能定义 |
| `ApplyDamage` → 拆分 | 拆为 `ApplyDamage` (数值) + `ApplyHitReaction` (标记)（§4.2） | Syscall 重新设计 |

---

## 十一、已决事项汇总

| ID | 问题 | 决定 |
|----|------|------|
| SK1 | 首批脚本范围 | ✅ 8 个，范围合适 |
| SK4 | 蹲/倒地等姿态 Syscall | ✅ 可以新增 |
| SK5 | 脚本文件命名规则 | ✅ `skill_<英文名>.ffs`，例: `skill_idle.ffs` |
| SK6 | 硬直实现机制 | ✅ 方案 A — 时间轴暂停（§六.3），`yield`/`wait(N)` 已支持 |

---

## 十二、待决

| ID | 问题 | 候选方案 | 当前倾向 |
|----|------|---------|---------|
| SK2 | 裁决机制详细设计 | §七.4 分层候选池（姿态分组+优先级+脚本条件） | 推荐分层候选池 |
| SK3 | 碰撞框 Syscall 参数设计 | A: 逐参数 / B: 结构体参数 | 待 FFVM 结构体支持确认 |
| SK7 | 脚本条件失败时的宿主回退逻辑 | §七.5 的"第 0 帧 return"检测 | 待实现验证 |
| SK8 | 碰撞框 Syscall 新增范围 | SetHitbox / SetHurtbox / ClearHitbox / SetPushBox | 待设计 |
| SK9 | 受击标记(HitReactionTag)设计 | 枚举标记 / 分层标记 / mask 混合 | 需要单独讨论 |
| SK10 | 连招共用环境 | §十三.1 — 连招序列的共享状态与衔接 | 需要讨论 |
| SK11 | 多阶段技能（多时间轴） | §十三.2 — 技能内多 Phase 切换 vs 技能拆分 | 需要讨论 |

---

## 十三、新增讨论方向

### 13.1 连招共用环境 💬 (SK10)

> **问题**：格斗游戏中连招（combo）是一系列技能在某个维度上的整体。除了技能衔接（取消窗口），还涉及**连招序列的共用环境**——例如连招计数器、伤害递减系数、连招中断后的统一重置。当前方案只讨论了单技能之间的转换，没有涉及"一组技能作为整体"的共享状态。

**候选方案**：

// 理想方案C, 暂时使用B, 后期可能需要类似 xxx_logic.ffs 来专门处理, 可能涉及宿主侧支持, 需要纳入kof串行计划表, 并且写法上应该朝理想方案靠近.

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 外挂独立逻辑** | 宿主维护 `ComboContext`（连招计数、递减系数等），技能通过 Syscall 读写 | 简单直接 | 连招逻辑散落在宿主和多个脚本中 |
| **B: 黑板变量** | 角色级黑板（key-value），技能脚本通过 `GetBlackboard` / `SetBlackboard` Syscall 共享状态 | 脚本自主管理，灵活 | 需要约定 key 命名规范，调试困难 |
| **C: 连招描述脚本** | 一个专门的 `combo_xxx.ffs` 脚本管理连招流程，子技能作为 Phase 被调用 | 连招逻辑集中，可调试 | 需要设计脚本间调用/协调机制 |

> 兜底用方案 A。理想方案待讨论。

### 13.2 多阶段技能 / 多时间轴 💬 (SK11)

> **问题**：当前设定默认一个技能只有一条时间轴（一个 `totalFrames` + 一套碰撞框 + 一个动画）。当角色动作在一个"理想技能"中需要变化（例如：蓄力→释放、起手→命中后追加段），被迫拆分为 `skill_xx_p1` / `skill_xx_p2`，导致：
> - 大量重复配置（两个技能共享相同的角色参数、优先级、条件）
> - 衔接逻辑复杂（p1 结束时需要精确传递状态给 p2）

**候选方案**：

// 选择方案B

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 技能拆分** | 现有方案，`skill_xx_p1` → 取消窗口内切换到 `skill_xx_p2` | 与现有裁决机制兼容 | 配置重复、状态传递麻烦 |
| **B: 技能内 Phase** | 一个技能内定义多个 Phase（各自有 totalFrames、碰撞框、动画），脚本内通过 `BeginPhase(phaseId)` 切换 | 单技能文件管理所有阶段，无配置重复 | 需要新的 Phase 切换 Syscall，技能内部逻辑变复杂 |
| **C: 动态 BeginAction** | 允许脚本在执行中多次调用 `BeginAction(actionId, frames)` 切换动作声明 | 最小改动，复用现有 Syscall | 语义不够清晰（"一个技能多次 BeginAction"是否合理？） |

**方案 B 与回滚的关系**：

> 利用 FFVM 自带的回滚/序列化机制，只需简单追踪 Phase index（一个 int），即可轻松实现确定性重播。这是方案 B 的优势之一。
>
> 麻烦点：原来可以借助技能系统的转换规则（裁决层自动处理），现在需要在技能脚本内实现一套专用的 Phase 转换逻辑。

```ffs
// 方案 B 概念示例
func main() {
    // Phase 1: 起手
    var phase1Frames: int = 10
    BeginAction(101, phase1Frames)   // actionId=101, Phase 1
    defer { EndAction() }

    var f: int = 0
    var hit: int = 0
    while f < phase1Frames {
        if f >= 3 && f < 7 && hit == 0 {
            var t: int = CheckAttackHit(1001)
            if t > 0 {
                hit = 1
                // 命中 → 进入 Phase 2
                goto phase2
            }
        }
        f = f + 1
        yield
    }
    return   // 未命中, 技能结束

    // Phase 2: 追加段
    :phase2
    var phase2Frames: int = 15
    BeginAction(102, phase2Frames)   // actionId=102, Phase 2
    var f2: int = 0
    while f2 < phase2Frames {
        // Phase 2 逻辑...
        f2 = f2 + 1
        yield
    }
}
```

> ⚠️ 上述使用 `goto` 仅为概念演示。FFS 目前不支持 `goto`，实际实现可用函数调用或 Phase 状态变量。

[重要发现追加] 角色数据在游戏层面将会是纯数据, 为了方便后期的ecs化, 因此这些方法宿主方法需要考虑 1. 是否留在虚拟机内 2. 决定放在宿主内时需要考虑合理的调用方式
这涉及到以上大量讨论, 几乎所有的待决项和已决定项的数据保存.
原则大致为: 脚本内定义, 消费的, 留在脚本内. 有强烈外部使用需求的放在宿主侧.
