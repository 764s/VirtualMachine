# Plan: 技能 FFS 脚本化实施计划

> **状态**：⏳ 等待前置任务 (KOF-T1) → ✅ T2-1~T2-5 基础设施就绪 → ✅ T3-1~T3-4 首批脚本就绪 → ✅ T3.5 数据支持(include 基础设施 + 共享常量 + S05~S08 骨架)
> **来源**：[D_SkillScripting.md](../Discussion/D_SkillScripting.md) 讨论结论
> **日期**：2026-04-06

---

## 一、前置条件

- ✅ D_SkillScripting.md 前期讨论完成（SK1~SK12 全部收敛）
- ✅ 现有技能系统 host-driven 基础可用（Idle/Walk/Jump/Crouch/LightPunch）
- ✅ GameSyscalls ~40 个 Syscall 已就绪
- ✅ GameVMBridge 技能激活→VM 实例 spawn 已实现
- ⏳ KOF-T1（界面设计）需先完成

---

## 二、任务拆解

### KOF-T2: 技能脚本化基础设施

> **目标**：让宿主侧具备"用 FFS 脚本驱动技能"的能力

| # | 子步骤 | 说明 | 涉及文件 |
|---|--------|------|---------|
| T2-1 | `SkillDef` 扩展 | 新增 `AllowedStances[]`, `ActivationPriority`, `InterruptPriority`, `VMModuleSlot` 字段 | SkillDef.cs |
| T2-2 | `Stance` 枚举定义 | `Grounded, Airborne, Crouching, Knockdown, Hitstun, Dead` | 新文件或 Types.cs |
| T2-3 | 裁决层重构 | `SkillManager` 实现分层候选池：①姿态分组 → ②优先级排序 → ③VM 条件检查 → ④取消窗口 | SkillManager.cs |
| T2-4 | 条件入口机制 | 宿主调用流程支持专有条件检测阶段（spawn VM → 执行第一帧 → 检测 return/continue） | SkillManager.cs, GameVMBridge.cs |
| T2-5 | 删除旧 `CanActivate` 回调 | 保留兼容接口但标记为 legacy，新技能全部走 VM 条件 | SkillDef.cs |

**完成条件**：能定义一个带 `VMModuleSlot` 的 `SkillDef`，通过分层候选池裁决激活。

---

### KOF-T3: 首批 FFS 脚本 — 前 4 个 (S01~S04)

> **目标**：用 FFS 脚本替代 host-side 的 Idle/Walk/Jump/LightPunch

| # | 脚本 | 模式 | 核心 Syscall | 备注 |
|---|------|------|-------------|------|
| T3-1 | `skill_idle.ffs` | 循环型 | BeginAction(-1), yield | 最简循环脚本 |
| T3-2 | `skill_walk_forward.ffs` | 循环型 | GetInput, SetVelocity, yield | 每帧设速度 |
| T3-3 | `skill_jump.ffs` | 物理型 | SetVelocity, IsGrounded, yield | 初始速度 + 等待落地 |
| T3-4 | `skill_light_punch.ffs` | 攻击型 | BeginAction, CheckAttackHit, ApplyDamage, yield | 帧窗口内命中检测 |

**关键验证**：
- 脚本加载 → VM 实例 spawn → 正确执行 → Syscall 调用 → 宿主状态变化
- 裁决层正确选择脚本驱动的技能（替代 host lambda）
- yield 循环帧数与宿主 Tick 对齐

**完成条件**：4 个 FFS 脚本运行正常，表现与 host-driven 版本一致。

---

### KOF-T3.5: Include 数据支持 + S05-S08 骨架 ✅

> **目标**：利用 Lang-2 include 特性建立共享数据层，消除魔法数字，为全部 8 个脚本提供共享基础设施

| # | 子步骤 | 说明 | 涉及文件 |
|---|--------|------|---------|
| T3.5-1 | 共享常量文件 | `common/constants.ffs`: 伤害类型、特效 ID、Tag、移动参数、输入按钮 | 新文件 |
| T3.5-2 | 输入帮助函数 | `common/input.ffs`: `getMoveDirX()` 等共享方向判断（include constants） | 新文件 |
| T3.5-3 | FileSystemFileResolver | GameVMBridge 新增文件系统级 IFileResolver，支持脚本 include 指令 | GameVMBridge.cs |
| T3.5-4 | 重构 S01-S04 | walk/jump/punch 三个脚本改用 include 共享常量，消除魔法数字 | skill_*.ffs |
| T3.5-5 | S05-S08 骨架 | 4 个脚本骨架：crouch_punch/hit_high/hard_knockdown/stand_up | 新文件 |
| T3.5-6 | 服务脚本模板 | `common/char_service.ffs`: @export + svc.member 模板，为 KOF-T6+ 铺路 | 新文件 |

**语言特性检查结论**：零阻碍。Lang-1~Lang-8 全部已满足 8 个脚本所需能力。

**完成条件**：全部 8 个脚本可编译（include 解析正确），共享常量消除魔法数字。

---

### KOF-T4: Syscall 扩展 + 碰撞框脚本化

> **目标**：新增碰撞框/受击标记相关 Syscall，碰撞框由脚本设置

| # | 子步骤 | 说明 | 涉及文件 |
|---|--------|------|---------|
| T4-1 | 新增 `SetHitbox(groupId, x, y, w, h)` | 设置攻击框 | GameSyscalls.cs |
| T4-2 | 新增 `SetHurtbox(x, y, w, h)` | 设置受击框 | GameSyscalls.cs |
| T4-3 | 新增 `ClearHitbox()` | 清除攻击框 | GameSyscalls.cs |
| T4-4 | 新增 `SetPushBox(x, y, w, h)` | 设置推挤框 | GameSyscalls.cs |
| T4-5 | 拆分 `ApplyDamage` | → `ApplyDamage(targetId, coefficient)` + `ApplyHitReaction(targetId, reactionTag)` | GameSyscalls.cs |
| T4-6 | 碰撞框迁移 | 脚本内通过 Syscall 设置碰撞框，替代 `SkillDef.CollisionFrames` 静态定义 | SkillManager.cs |

**完成条件**：碰撞框完全由脚本 Syscall 控制，`CollisionFrames` 不再使用。

---

### KOF-T5: 首批 FFS 脚本 — 后 4 个 (S05~S08)

> **目标**：覆盖受击/倒地/起身流程，验证完整生命周期

| # | 脚本 | 模式 | 核心 Syscall | 备注 |
|---|------|------|-------------|------|
| T5-1 | `skill_crouch_punch.ffs` | 攻击型 | SetHitbox, CheckAttackHit, ApplyDamage | 蹲姿攻击 |
| T5-2 | `skill_hit_high.ffs` | 被动型 | BeginAction, SpawnEffectSelf | 上受击 |
| T5-3 | `skill_hard_knockdown.ffs` | 被动型 | BeginAction, SetInvincible? | 硬倒地 + 无敌帧 |
| T5-4 | `skill_stand_up.ffs` | 有限帧型 | BeginAction, EndAction | 原地起身 |

**关键验证**：
- 完整流程：攻击 → 命中 → 受击标记附加 → 受击技能激活 → 硬直 → 倒地 → 起身 → idle
- 裁决层正确处理被动型技能激活（宿主检测伤害 → 切换受击技能）
- defer + EndAction 正确清理

**完成条件**：8 个首批脚本全部运行正常，完整生命周期可演示。

---

### KOF-T6: 多目标命中 + 硬直机制

| # | 子步骤 | 说明 |
|---|--------|------|
| T6-1 | `CheckAttackHit` 扩展 | 返回多目标（数组或循环迭代） |
| T6-2 | 时间轴暂停硬直 | `Character.HitstunFrames > 0` 时不推进角色 Tick（§六.3 方案 A） |
| T6-3 | 全屏顿帧 (HitStop) | 所有角色时间轴暂停 N 帧 |

---

### KOF-T7: 连招 V1（黑板变量）

| # | 子步骤 | 说明 |
|---|--------|------|
| T7-1 | 连招计数器 | 通过 `GetBlackboard("combo_count")` / `SetBlackboard("combo_count", n)` 共享 |
| T7-2 | 伤害递减系数 | `GetBlackboard("combo_decay")` 影响 ApplyDamage 系数 |
| T7-3 | 连招重置 | 连招中断后统一重置黑板变量（脚本 defer 或宿主检测） |

---

## 三、依赖关系图

```
KOF-T1 (UI)
    ↓
KOF-T2 (基础设施)
    ↓
KOF-T3 (前4个脚本)
    ↓
KOF-T3.5 (include 数据支持 + S05-S08 骨架) ✅
    ↓
KOF-T4 (Syscall扩展)  →  KOF-T10 (SK3交互验证)
    ↓
KOF-T5 (后4个脚本)
    ↓  ↓  ↓
KOF-T6  KOF-T7  KOF-T8/T9
(多目标) (连招)  (AI/弹幕)
```

---

## 四、讨论已决事项速查

> 完整讨论见 [D_SkillScripting.md](../Discussion/D_SkillScripting.md)

| ID | 决定要点 | 对应任务 |
|----|---------|---------|
| SK1 | 首批 8 个脚本 (S01~S08) | KOF-T3, KOF-T5 |
| SK2 | 分层候选池裁决 | KOF-T2 |
| SK3 | 碰撞框交互方式待验证（推送 vs 查询） | KOF-T10 |
| SK4 | 蹲/倒地姿态 Syscall 可新增 | KOF-T4 |
| SK5 | 文件命名 `skill_<英文名>.ffs` | KOF-T3, KOF-T5 |
| SK6 | 硬直 = 时间轴暂停（方案 A） | KOF-T6 |
| SK7 | 专有条件检测入口，宿主调用流程决定 | KOF-T2 |
| SK8 | 碰撞框暂由脚本推送到宿主 | KOF-T4 |
| SK9 | 受击标记分组 mask 混合 | KOF-T4 |
| SK10 | 连招 V1 黑板变量，理想方案 C | KOF-T7 |
| SK11 | Phase 是脚本内实现行为，不强制 | KOF-T5 |
| SK12 | ECS 纯数据原则：脚本闭环→VM，外部需求→Syscall | 全局 |
