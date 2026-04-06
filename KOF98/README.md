# KOF98 Practice — FFVM 格斗游戏探索

> **定位**：探索性实践。通过复刻 KOF98 核心机制来验证 FFVM 在实际游戏中的应用点。
> **阶段**：初期框架搭建。将来经过迭代逐步完善 FFVM 集成，最终（确认功能完备后）用 ECS 重构。

---

## 目录结构

```
KOF98/
├── Core/                    ← 基础类型（FVec2, FRect, Direction, GameConstants）
├── Input/                   ← 输入系统（PlayerInput, InputButton）
├── Character/               ← 角色系统（Character, CharacterData, CharacterManager）
├── Skill/                   ← 技能系统（SkillDef, SkillInstance, SkillManager）
├── Physics/                 ← 物理系统（PhysicsBody, CollisionSystem）
├── Combat/                  ← 战斗系统（CombatSystem, HitEvent）
├── Effect/                  ← 效果系统（EffectManager, EffectInstance）
├── Projectile/              ← 弹幕系统（ProjectileManager, ProjectileData）
├── AI/                      ← AI 接口（IAIController, SimpleAI）
├── Scene/                   ← 场景调度（GameScene, SceneInput, SceneCommand）
├── View/                    ← 可替换视图层
│   ├── IGameView.cs             接口定义
│   └── ConsoleGameView.cs       控制台 ASCII 渲染（不依赖 Unity）
├── VM/                      ← FFVM 集成层
│   ├── GameSyscalls.cs          游戏 Syscall 定义（~40 个插槽）
│   └── GameVMBridge.cs          VM 实例 ↔ 游戏实体桥接
├── Scripts/                 ← .ffs 脚本文件目录
├── Docs/                    ← 实践专属文档
│   ├── Discussion/              讨论区
│   └── Plan/                    计划区
├── Program.cs               ← 入口点
├── KOF98.csproj.template    ← 项目模板（csproj 被 gitignore）
└── README.md                ← 本文件
```

---

## 快速开始

```bash
# 1. 从模板生成 .csproj
cp KOF98.csproj.template KOF98.csproj

# 2. 构建
dotnet build KOF98.csproj

# 3. 运行（控制台渲染）
dotnet run --project KOF98.csproj

# 4. 无头模式（仅模拟）
dotnet run --project KOF98.csproj -- --headless --frames 600
```

## 键位（P1）

| 键 | 动作 |
|----|------|
| WASD / 方向键 | 移动 |
| J | 轻拳 (LP) |
| K | 重拳 (HP) |
| U | 轻脚 (LK) |
| I | 重脚 (HK) |

---

## 架构概述

### 帧执行顺序

```
GameScene.Step(SceneInput):
  1. 处理场景命令（创建角色、设置 AI 等）
  2. 收集/应用角色输入（玩家 + AI）
  3. 角色更新 Pass 1: 技能状态转换（deactivate → activate）
  4. 角色更新 Pass 2: 进入帧（更新碰撞框）
  5. 物理: 积分速度、约束场景边界
  6. 物理: 推箱解析（防止角色重叠）
  7. 角色更新 Pass 3: 处理技能（运行 VM 实例）
  8. 战斗: 结算待处理命中事件
  9. 效果: 更新计时器、移除过期
  10. 弹幕: 移动、生命周期、边界检查
  11. 自动朝向对手
  12. 检查回合结束
  13. 推进帧号
```

### VM 应用点

| 应用点 | 映射 | FFVM 实例 | 当前状态 |
|--------|------|-----------|---------|
| 角色 AI | 1 个 ffs 脚本/角色 | ✅ 独立实例 | 预留（SimpleAI 占位） |
| 技能 | 1 个 ffs 脚本/活跃技能 | ✅ 独立实例 | 框架就绪 |
| 持续效果器 | 独立 ffs 实例 | ✅ 独立实例 | 预留 |
| 弹幕行为 | 独立 ffs 实例 | ✅ 独立实例 | 预留 |
| 同步效果器 | Syscall 内联 | ❌ | 已实现（Syscall 方案） |

### 参考架构对应

```
场景逻辑  →  GameScene.Step()          [宿主 C#]
角色 AI   →  IAIController / VM 实例   [FFVM 实例]
技能      →  SkillManager + VM 实例    [FFVM 实例]
效果器    →  EffectManager / VM 实例   [分级处理]
```

### 视图层可替换设计

```
IGameView (接口)
  ├─ ConsoleGameView    ← 当前：ASCII 渲染（角色位置 + 物理框）
  └─ UnityGameView      ← 将来：Unity 精灵 + 动画 + 相机
```

---

## Syscall 插槽分配

| 范围 | 类别 | 示例 |
|------|------|------|
| 0-19 | 动作管理 | BeginAction, EndAction, GetFrame |
| 20-39 | 碰撞检测 | CheckAttackHit, CheckAttackBlocked, HasTargetTag |
| 40-59 | 伤害与效果 | ApplyDamage, ApplyHitstun, ApplyKnockback... |
| 60-79 | 视觉效果 | SpawnEffectHit, SpawnEffectSelf |
| 80-99 | 角色查询 | GetSelfId, GetPosX, GetHP, GetDistance... |
| 100-119 | 角色控制 | SetVelocity, SetFacing, AddPower |
| 120-139 | 输入查询 | GetInput, GetInputDir |
| 140-159 | AI（预留） | FindNearestEnemy, GetDistanceTo, MoveToward |
| 160-179 | VM 实例管理 | SpawnScript (MI-2), KillInstance (MI-3) |
| 180-199 | 黑板 | SetBlackboard, GetBlackboard |
| 200-219 | 工具函数 | print, random, abs, min, max |

---

## 后续路径

1. **当前**：完整 C# 游戏框架 + Host-driven 技能演示
2. **近期**：编写 .ffs 技能脚本，通过 GameVMBridge 运行
3. **中期**：AI 脚本化、弹幕脚本化、效果器脚本化
4. **远期**：ECS 重构（确认 FFVM 功能完备后）
