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

### 4.2 伤害参数
// 这里需要区分伤害参数和伤害标记
// 伤害参数指用来计算伤害值的
// 伤害标记的用途是攻击方给受击方附加标记, 轮到受击方执行时, 根据标记决定进不进受击, 进什么受击. 需要单独开区讨论. 这里的问题大致为, 这要求伤害标记特异化, 如果复用容易出现标记叠加然后出现混沌不可测的冲突(指将伤害标记分成多个参数, 利用mask进行叠加, 叠加到最后会出现意想不到的情况, 本质上是因为mask只管混入, 不能保证分离. 特意伤害标记能避免该类问题, 但会导致标记数量碰撞, 无法用简单mask解决)
// 当前的伤害标记名字起的不好, 需要更合理的命名, 避免此类误解.
```
ApplyDamage(targetId, coefficient, damageType)
```

- `coefficient`: 伤害系数（不是绝对值），宿主用公式换算
  - skill_114: 5 / 7 / 10（三段递增）
  - skill_25: 无（受击脚本不造成伤害）
- `damageType`: 伤害类型码
  - `101` = 普通下盘重击
  - `102` = 普通上盘重击
  - `201` = 击躺
  - `0` = 无类型（格挡削血）

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

// 对硬直的实现考虑
// 前置设定: 角色有一个逻辑时间轴, 以帧为单位, 正常情况角色时间轴推进, 其他所有角色相关功能跟随时间轴推进. 技能的会决定该时间轴的长度. 全屏顿帧或者时间减速或硬直都是在对此时间轴的推进做文章.
// 硬直对时间轴的贡献为, 一般情况下, 硬直期间角色时间轴不退进, 表现为攻击停顿或受击停顿
// 硬直还会阻止部分技能开始, 或者作为技能的开始条件

> 来源：Syscall 需求评估时发现硬直机制需要更深入讨论。

### 6.1 当前状态

`ApplyHitstun` 由攻击方脚本调用，宿主侧设置 `Character.HitstunFrames`。
但硬直期间角色的具体行为（不可操作、不可被打断、硬直结束后自动恢复）由谁控制？

### 6.2 两种实现思路

| 方案 | 描述 | 优点 | 缺点 |
|------|------|------|------|
| **A: 宿主倒计时** | 宿主每帧递减 `HitstunFrames`，期间屏蔽输入，结束后自动恢复 idle | 简单、确定性强 | 硬直期间的变化（如硬直中被再次命中）需要额外逻辑 |
| **B: 受击技能驱动** | 受击技能脚本 (`skill_hit_high.ffs`) 的帧循环即为硬直持续时间，脚本结束=硬直结束 | 统一模型、可自定义 | 需要攻击方通知的硬直帧数与受击脚本帧数协调 |

### 6.3 建议方案（待确认）

**两层配合**：
1. 攻击方 `ApplyHitstun()` → 宿主记录硬直参数 → 触发受击技能切换
2. 受击技能脚本的帧数 = 硬直持续帧数（由宿主在 spawn 脚本时通过参数或黑板传入）
3. 宿主 `HitstunFrames` 作为安全倒计时（脚本异常时的兜底）

这样硬直的"不可操作"由受击技能的优先级保证（受击优先级 > 移动/攻击），不需要额外的输入屏蔽逻辑。

---

## 七、讨论：技能条件与裁决机制 💬
// 关于裁决机制
// 两个主要方向: 
// 方向A: 使用状态机和预定义的转换通道, 配合特殊转换兜底(any). 优势是提前避免不必要的性能开销. 缺点是会出现连接数量爆炸, 且爆炸后次生难以调整的问题.
// 方向B: 使用优先级机制, 相当于提前分桶, 对桶进行同时启动优先级以及打断优先级设定, 配合技能上特写的类似方向A的转换逻辑, 这些逻辑甚至可以配合详细的条件. 优势是方便配置, 缺点是性能爆炸, 因为优先级的抽象性决定了大量技能需要遍历进入条件.
// 具体的条件判断都在虚拟机内实现, 这点以上两种方案没有分歧. 本质都是决定当前技能可以转向哪些技能
// 目前在纠结选择哪个, 那么工业方案是什么, 你有解决方向推荐吗

> 来源：对 `CanActivate` 是否迁移到 VM 的分歧。

### 7.1 分歧说明

| 观点 | 描述 |
|------|------|
| **原方案（Agent）** | `CanActivate` 保留在宿主 C#，技能条件是纯判断（≤3行），属于"稳亏"场景。脚本假设条件已通过 |
| **用户倾向** | 技能条件也进 VM。并且条件逻辑应在**技能 VM 内部**（而非独立的裁决 VM 实例） |

### 7.2 技能条件位置的候选方案

| 方案 | 描述 | 示意 |
|------|------|------|
| **A: 宿主 C#** | 条件由 `SkillDef.CanActivate` lambda 判断，与当前实现一致 | `(ch, input) => input.HasButton(BTN_LP) && ch.IsGrounded` |
| **B: 独立裁决 VM** | 一个专门的"裁决脚本"每帧运行，决定切换到哪个技能 | `skill_arbiter.ffs` → `SpawnScript("skill_light_punch")` |
| **C: 技能内条件** | 条件写在技能脚本开头，不满足则立即退出 | 脚本第一帧检查条件，失败 → `return`，宿主回退到 idle |

### 7.3 方案 C 展开：技能内条件

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

**优点**：
- 条件和执行在同一脚本中，便于阅读和调试
- 不需要额外的裁决 VM 实例
- 条件变化时只改一个文件

**需要解决**：
- 条件不满足时脚本立即 return，宿主需要检测"脚本在第 0 帧就结束了"并回退
- 多个技能的条件评估顺序（优先级）仍需宿主裁决

### 7.4 裁决机制设计 💬

> 用户倾向：宿主提供进入/退出功能，进入后由脚本控制细节。裁决思路分为通用 + 特殊。

**裁决 = 宿主决定"尝试哪些技能" + 脚本决定"是否真正激活"**

```
宿主裁决层 (每帧):
  1. 按优先级排序候选技能列表
  2. 通用规则过滤 (例: 硬直中不可切换攻击, 空中不可使用地面技能)
  3. 尝试 spawn 最高优先级技能的 VM 实例
  4. 脚本第一帧执行条件检查:
     - 通过 → 脚本继续 (BeginAction...)
     - 失败 → 脚本 return → 宿主检测到立即完成 → 尝试下一个候选
  5. 全部失败 → 保持当前技能
```

**通用规则**（宿主侧，所有技能共享）：
- 优先级判断（高优先级可打断低优先级）
- 状态互斥（硬直中、倒地中、死亡中的技能切换限制）
- 基础标签检查（空中/地面状态）

**特殊规则**（脚本侧，技能自定义）：
- 具体输入要求（需要某个按键组合）
- 距离/位置条件（如投技需要近距离）
- 资源条件（如超必杀需要能量）
- 连招窗口（如取消窗口内才可衔接）

这样裁决机制既有宿主的统一管控，又有脚本的灵活定制。

---

## 八、讨论：碰撞框数据来源 💬

// 澄清, 碰撞数据完全由技能决定. 这里的考虑是此时的技能约等于状态机, 理想情况下状态机中的状态包含了碰撞状态.

> 来源：SK3 决定碰撞框暂时在脚本内设置。

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

对照首批 8 个脚本所需的 Syscall，与当前已有的 ~40 个 Syscall 对比：

| 需求 | 现有 Syscall | 是否需要新增 |
|------|------------|------------|
| 动作管理 | BeginAction, EndAction, GetFrame | ✅ 足够 |
| 命中检测 | CheckAttackHit, CheckAttackBlocked | ✅ 足够 |
| 伤害/硬直 | ApplyDamage, ApplyHitstun, ApplyHorizKB_* | ✅ 足够 |
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
| `CanActivate` → 脚本内条件 | 技能条件迁移到脚本第一帧检查（§七方案 C） | SkillManager 需检测"第 0 帧完成"并回退 |
| `CanContinue` → 脚本控制 | 循环技能退出由脚本决定（通过结束/return）或宿主 Kill | SkillManager 裁决层 |
| `OnFrame` → 删除 | 每帧逻辑由 FFS 脚本 `yield` 循环驱动 | 不再需要 |
| `CollisionFrames` → 脚本内设置 | 碰撞框数据迁移到脚本 Syscall（§八） | 需新增 Syscall |
| `GameVMBridge.ActivateSkillVM` | 技能激活时自动 spawn VM 实例 | 已实现 |
| 裁决层改造 | 实现通用+特殊裁决机制（§七.4） | SkillManager 重构 |

---

## 十一、已决事项汇总

| ID | 问题 | 决定 |
|----|------|------|
| SK1 | 首批脚本范围 | ✅ 8 个，范围合适 |
| SK4 | 蹲/倒地等姿态 Syscall | ✅ 可以新增 |
| SK5 | 脚本文件命名规则 | ✅ `skill_<英文名>.ffs`，例: `skill_idle.ffs` |

---

## 十二、待决

| ID | 问题 | 候选方案 | 当前倾向 |
|----|------|---------|---------|
| SK2 | 裁决机制详细设计 | §七.4 的"宿主通用+脚本特殊"方案 | 倾向脚本侧裁决 |
| SK3 | 碰撞框 Syscall 参数设计 | A: 逐参数 / B: 结构体参数 | 待 FFVM 结构体支持确认 |
| SK6 | 硬直实现机制 | §六的两层配合方案 | 待确认 |
| SK7 | 脚本条件失败时的宿主回退逻辑 | §七.3 的"第 0 帧 return"检测 | 待实现验证 |
| SK8 | 碰撞框 Syscall 新增范围 | SetHitbox / SetHurtbox / ClearHitbox / SetPushBox | 待设计 |
