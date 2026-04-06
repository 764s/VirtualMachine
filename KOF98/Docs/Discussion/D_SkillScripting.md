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

## 三、首批 FFS 脚本建议

### 3.1 选择原则

根据 D_GameArchitecture.md §2.3 VM 应用分级（稳赚/模糊/稳亏）：

| 原则 | 说明 |
|------|------|
| **优先覆盖主循环** | idle → walk → attack → hit → recover，验证完整生命周期 |
| **优先覆盖多样性** | 选择代表不同脚本模式的技能（循环/有限帧/条件驱动/物理驱动） |
| **暂不涉及复杂系统** | 抓投（需双方协调）、闪避（需无敌帧标记）暂不纳入首批 |
| **不以 skill_114/skill_25 为目标** | 提取其参数表示模式作为参考，首批脚本针对基础技能 |

### 3.2 建议首批脚本（8 个）

| # | 技能 | 分类 | 脚本模式 | 选择理由 |
|---|------|------|---------|---------|
| S01 | 站立待机 (Idle) | 基础移动 | 循环 + 输入监听 | 最基础状态，验证循环脚本 + yield |
| S02 | 前进 (WalkForward) | 基础移动 | 循环 + 每帧速度设置 | 验证 SetVelocity + 输入条件退出 |
| S03 | 跳 (Jump) | 基础移动 | 有限帧 + 物理驱动 | 验证垂直运动 + IsGrounded 条件 |
| S04 | 近拳 (LightPunch) | 基本攻击 | 有限帧 + 命中检测 | 验证 CheckAttackHit + ApplyDamage 完整攻击流程 |
| S05 | 蹲拳 (CrouchPunch) | 基本攻击 | 有限帧 + 蹲姿攻击 | 验证蹲状态下的攻击判定 |
| S06 | 上受击 (HitHigh) | 受击 | 有限帧 + 被动触发 | 验证受击脚本（参考 skill_25 模式） |
| S07 | 硬倒地 (HardKnockdown) | 倒地 | 有限帧 + 无敌 | 验证倒地状态 + 起身衔接 |
| S08 | 原地起身 (StandUp) | 起身 | 有限帧 + 恢复 | 验证倒地→起身→idle 完整恢复流程 |

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

### S01 — 站立待机 (Idle)

```ffs
func main() {
    BeginAction(1, -1)    // -1 = 无限循环
    defer { EndAction() }

    while true {
        // 空循环, 等待宿主切换层检测到输入后切换技能
        yield
    }
}
```

**要点**：循环型技能最简形式。宿主 `SkillManager.TryActivateSkill()` 负责切换。

### S04 — 近拳 (LightPunch)

```ffs
func main() {

  // 对于字面量, 适当使用变量声明. 但脚本有其特殊性, 写多了也啰嗦, 因此设定大致标准适当权衡.

    BeginAction(101, 20)   // actionId=101, 20帧
    defer { EndAction() }

    var hit: int = 0
    var f: int = 0
    while f < 20 {
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

**要点**：单段攻击不需要互斥变量（只有一个窗口），`hit` 变量防止重复命中。

### S06 — 上受击 (HitHigh)

```ffs
func main() {
    BeginAction(25, 20)   // actionId=25, 20帧
    defer { EndAction() }

    SpawnEffectSelf(4001, 60)   // 受击特效

    var f: int = 0
    while f < 20 {
        f = f + 1
        yield
    }
}
```

**要点**：纯被动型，最简形式。参考 skill_25 模式。

---

## 六、新增 Syscall 需求评估

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
| 蹲姿切换 | — | ❓ 可能需要 `SetPushBox` 或 `SetStance` |
| 倒地状态 | — | ❓ 可能需要 `SetKnockdownState` / `SetInvincible` |
| 起身衔接 | — | ❓ 可能需要 `RequestSkillChange` |

// 需要补充讨论硬直的实现机制

**初步结论**：前 4 个脚本（S01~S04）可直接用现有 Syscall 实现。S05~S08 可能需要少量新增。

---

## 七、宿主侧变更评估

将技能从 host-side 迁移到 FFS 脚本需要宿主侧的配合：

| 变更 | 说明 | 影响范围 |
|------|------|---------|
| `SkillDef.VMModuleSlot` 设置 | 技能定义指向已编译的 .ffs 模块 | CharacterData 技能注册 |
| `CanActivate` 保留 | 技能切换条件仍由宿主 C# 判断 | 不变 |
| `CanContinue` 保留/迁移 | 循环技能的退出条件；可选保留在宿主或迁移到脚本 | 待定 |
| `OnFrame` 移除 | 每帧逻辑由 FFS 脚本 `yield` 循环驱动 | 不再需要 |
| `CollisionFrames` 保留 | 碰撞框数据仍由宿主静态定义 | 不变 |
| `GameVMBridge.ActivateSkillVM` | 技能激活时自动 spawn VM 实例 | 已实现 |

**关键设计选择**：`CanActivate`（技能切换条件）是否也迁移到 FFS？

// 技能条件进虚拟机. 另外我发现你默认技能条件在独立的虚拟机里. 我原先默认技能条件在技能虚拟机里. 看起来这里我们有分歧, 需要讨论
- **建议保留在宿主**：切换条件是纯条件判断（≤3行），属于"稳亏"场景
- FFS 脚本假设条件已通过，只负责技能执行（与 skill_25 头注释一致）

---

## 八、待决

| ID | 问题 | 候选方案 | 当前倾向 |
|----|------|---------|---------|
| SK1 | 首批脚本范围是否合适？ | 8个 / 减少到4个 / 增加 | 8 个（覆盖主要模式） |
| SK2 | 循环技能（Idle/Walk）退出由谁控制？ | A: 宿主 CanContinue / B: 脚本内 GetInput 判断 / C: 两者配合 | A（保持现有机制） |
| SK3 | 碰撞框数据来源？ | A: 宿主 CollisionFrames / B: 脚本内 Syscall 设置 / C: 外部 JSON | A（现阶段） |
| SK4 | 蹲/倒地等姿态切换是否需要新增 Syscall？ | 新增 SetStance / 复用 Tag 系统 | 待首批实现时确认 |
| SK5 | 脚本文件命名规则？ | `skill_idle.ffs` / `s01_idle.ffs` / `idle.ffs` | 待定 |
SK1: 合适
SK2: 宿主提供进入退出功能, 进入后由脚本控制细节. 因此需要讨论裁决机制. 裁决由宿主提供, 也可以考虑脚本(倾向), 裁决思路大致为分为通用+特殊
SK3: 暂时脚本内设置, 因此需要略微考虑书写方便(可能要用到自定义结构体)
SK4: 可
SK5: skill_idle.ffs
