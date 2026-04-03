# 技能脚本复现 — 分析与实现

## 一、技能执行流程推测

根据归档文件 (`Archive/VMScript.md`) 中描述的遗留系统层次结构：

```
SkillBehaviour (技能管理器)
  └─ Skill (技能实例)
       └─ SubSkill (子技能 — 真正的效果执行体)
            └─ SubskillEffectProcessor
                 └─ EffectProcessor (条件 → 目标 → 数据效果 → 视觉效果)
```

### 每帧执行流程

```
1. Step_SwitchSkill (切换层)
   ├─ 递增当前动作帧
   ├─ 判断当前技能是否应结束
   └─ 尝试根据输入启动新技能 (检查启动条件)

2. Step_StepSkill (执行层)
   ├─ Skill.Step()
   │   ├─ 准备共享缓存
   │   └─ 遍历所有 SubSkill.OnStep()
   │       ├─ 准备 SubEffectPerformer
   │       └─ 遍历 SubskillEffectProcessor[]
   │           └─ EffectProcessor.Execute()
   │               ├─ ① 条件检查 (组间OR, 组内AND)
   │               ├─ ② 目标选择 (自己/敌人)
   │               ├─ ③ 数据效果 (伤害/硬直/击退)
   │               └─ ④ 视觉效果 (特效/音效)
   └─ 处理技能附加流程
```

### 关键机制

| 机制 | 说明 |
|------|------|
| **条件分组** | 组间 OR, 组内 AND — 任一组全部通过即触发 |
| **攻击框判定** | 检查攻击碰撞组是否与目标受击/防御框重叠 |
| **效果互斥Id** | 同一互斥组内, 对同一目标仅首次生效 |
| **技能帧条件** | 基于当前动作帧进行判定 (如 `帧 >= 9`) |
| **目标Tag检查** | 检查目标当前技能标签 (如 `空中状态`) |
| **启动条件** | 受击技能由宿主切换层在实例化前检查 (伤害类型+自身状态+Buff标记) |

---

## 二、当前 VM 能力评估

### ✅ 已具备 — 可直接使用

| 能力 | 对应语法 | 技能中的用途 |
|------|----------|-------------|
| 帧推进与挂起 | `wait N` / `yield` | 逐帧推进技能时间线 |
| 条件分支 | `if / else if / else` | 条件分组 (组间OR → 组内AND) |
| 比较与逻辑运算 | `>=`, `<`, `&&`, `==` | 帧范围判定、互斥标记检查 |
| 变量与赋值 | `var x: int = 0` | 帧计数器、互斥标记 |
| 循环 | `while` / `for` | 主循环遍历所有帧 |
| 宿主调用 | `Syscall(args)` | 碰撞检测、伤害、击退、特效等全部通过 Syscall |
| 清理保障 | `defer { }` | 技能结束时清理 (EndAction) |
| 浮点字面量 | `0.3`, `4.998` | 伤害系数、击退速度等精确数值 |

### ⚠️ 需通过 Syscall 间接实现

| 能力 | 当前状态 | Syscall 方案 |
|------|---------|-------------|
| 碰撞框检测 | 无内建碰撞系统 | `CheckAttackHit(groupId)` → 返回目标Id |
| 伤害类型判定 | 无枚举/Tag类型 | 整数常量编码, 宿主侧解码 |
| 目标Tag查询 | 无直接读取 | `HasTargetTag(targetId, tagId)` → 0/1 |
| 多目标迭代 | 无数组类型 | 宿主侧批量处理, 脚本处理首个目标 |
| 效果互斥 (多目标) | 变量仅跟踪单目标 | 宿主侧维护 per-target 互斥表 |
| 启动条件 | 脚本内无受击上下文 | 宿主在实例化前检查, 脚本假设条件已通过 |

### ❌ 当前缺失 — 但对本次复现非阻塞

| 缺失特性 | 影响 | 说明 |
|----------|------|------|
| 用户自定义函数调用 | 低 | 编译器仅编译 `main`, 子技能逻辑需内联; 对复杂技能可读性下降 |
| `using` 配对清理语法 | 低 | `defer` 可替代; Step 7 开发中 |
| `wait_for` 编译支持 | 无 | 本次技能不涉及实例间等待 |
| 数组/结构体 | 中 | 多碰撞框参数需展开为独立 Syscall 参数 |
| 读取当前帧号 | 低 | 用变量手动计数等效; 可考虑增加 `GetFrame()` Syscall |

---

## 三、结论: 可以复现

当前 VM **可以**复现这两个技能的核心执行逻辑, 理由:

1. **帧时间线控制**: `while` + `yield` 提供精确的逐帧推进
2. **条件分支**: `if/else` + 比较运算符 完整覆盖"组间OR, 组内AND"条件模型
3. **Syscall 边界**: 碰撞检测、伤害、击退、特效等"重活"天然属于宿主侧, 符合架构规则 ("batch via Syscall", "Handle64 for complex data")
4. **互斥机制**: 变量标记实现单目标互斥, 多目标场景由宿主扩展
5. **清理保障**: `defer` 确保技能异常中断时仍执行清理

### 局限性说明

- **多目标互斥**: 当前脚本用 `var mutex1` 跟踪单目标互斥。若同一帧多个敌人被命中, 需宿主侧维护 per-target 互斥表, 并通过 Syscall 查询 `CheckMutex(mutexId, targetId)`.
- **启动条件外置**: 受击技能的启动条件 (伤害类型+自身Tag+Buff标记) 由宿主技能切换层在实例化前检查, 不由脚本本身负责. 这是合理的——归档文件中 `Skill.CheckCondition()` 也是在 `SkillBehaviour` 层执行.
- **碰撞框数据**: 碰撞框定义在 ActionAsset (动作资源) 中, 不由脚本定义. 脚本仅查询碰撞结果, 不创建碰撞框.

---

## 四、Syscall 协议

以下是两个技能脚本所需的全部 Syscall 定义:

### 动作管理

| Syscall | 参数 | 返回 | 说明 |
|---------|------|------|------|
| `BeginAction(actionId, totalFrames)` | r0=动作Id, r1=总帧数 | — | 开始播放动作, 激活碰撞框 |
| `EndAction()` | — | — | 结束动作, 清除碰撞框 |

### 碰撞检测

| Syscall | 参数 | 返回 | 说明 |
|---------|------|------|------|
| `CheckAttackHit(groupId)` | r0=攻击组Id | r0=目标Id (0=未命中) | 攻击框与敌人受击框碰撞判定 |
| `CheckAttackBlocked(groupId)` | r0=攻击组Id | r0=目标Id (0=未被防) | 攻击框与敌人防御框碰撞判定 |
| `HasTargetTag(targetId, tagId)` | r0=目标Id, r1=TagId | r0=0/1 | 检查目标当前技能Tag |

### 伤害与效果

| Syscall | 参数 | 返回 | 说明 |
|---------|------|------|------|
| `ApplyDamage(targetId, coeff, dmgType)` | r0-r2 | — | 应用伤害 (系数+类型) |
| `SetEnergyCoeff(coeff)` | r0=系数 | — | 设置下次伤害的能量系数 |
| `ApplyHitstun(targetId, startF, durF, level, shake)` | r0-r4 | — | 硬直 (起始帧/持续帧/等级/抖动) |
| `ApplyHorizKB_Dist(targetId, dist, durF)` | r0-r2 | — | 水平击退: 固定距离模式 |
| `ApplyHorizKB_Speed(targetId, speed)` | r0-r1 | — | 水平击退: 固定速度(直到着地) |
| `ApplyVertKB(targetId, speed, durF)` | r0-r2 | — | 垂直击退: 固定速度模式 |
| `ApplyCornerKBSelf(dist, durF)` | r0-r1 | — | 角落击退(自身) |

### 自身效果

| Syscall | 参数 | 返回 | 说明 |
|---------|------|------|------|
| `ApplySelfHitstun(startF, durF, level)` | r0-r2 | — | 自身硬直 |
| `ApplySelfHorizKB(dist, durF)` | r0-r1 | — | 自身水平位移 |
| `ApplySelfVertKB(speed, accel)` | r0-r1 | — | 自身垂直位移 (匀变速) |

### 视觉效果

| Syscall | 参数 | 返回 | 说明 |
|---------|------|------|------|
| `SpawnEffectHit(effectId, durF)` | r0-r1 | — | 在击中位置生成特效 |
| `SpawnEffectSelf(effectId, durF)` | r0-r1 | — | 在自身位置生成特效 |

### 常量编码

```
// Tag Id
TAG_AIR_STATE = 1      // 空中状态

// 伤害类型
DMG_NORMAL_LOWER = 101 // 普通攻下+下盘重击
DMG_NORMAL_UPPER = 102 // 普通地面+上盘重击
DMG_KNOCKDOWN    = 201 // 击躺

// 特效 Id
FX_QINGJITEXIAO    = 3001 // 轻击特效
FX_ZHENGPING_XIAO  = 3002 // 震屏(小)
FX_ZHONGJIYINXIAO  = 3003 // 重击音效
FX_FANGYUTEXIAO    = 3004 // 防御特效
FX_ZHONGJITEXIAO   = 3005 // 重击特效
FX_7FEIYANJIFENGTUI = 7001 // 飞燕疾风腿音效
FX_CGH_SHOUZHONGJI = 4001 // 受击特效
```

---

## 五、文件清单

| 文件 | 说明 |
|------|------|
| `skill_114feiyanxuanfengtui.ffs` | 飞燕旋风腿 — 56帧攻击技能脚本 |
| `skill_25shangpanbeijizhong.ffs` | 上盘被击中 — 30帧受击技能脚本 |
| `README.md` | 本文档: 执行流程分析 + 能力评估 + Syscall协议 |

---

## 六、后续建议

1. **增加 `GetFrame()` Syscall** — 让脚本可直接读取当前动作帧, 消除手动计数
2. **用户函数编译** — 允许子技能逻辑提取为独立函数, 提升可读性
3. **多目标批量 Syscall** — `ProcessAttackGroup(groupId, subSkillConfig)` 一次处理所有目标
4. **互斥表宿主化** — 提供 `TryAcquireMutex(mutexId, targetId)` Syscall, 统一管理
5. **碰撞框声明语法** — 考虑在脚本头部声明碰撞框数据, 替代 ActionAsset 分离配置
