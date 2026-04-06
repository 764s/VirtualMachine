# KOF 探索 — 专用总览 (Overview)

> **状态**：🔄 进行中
> **目标**：通过复刻 KOF98 核心机制，探索 FFVM 游戏应用
> **日期**：2026-04-06

---

## 一、定位与核心设计原则

### 1.1 虽然实现的是 KOF98，但必须面向多目标问题设计

本探索项目以 **KOF98** 为实现目标，但架构设计需考虑以下多目标场景：

| 维度 | KOF98 当前实现 | 多目标扩展考虑 |
|------|--------------|---------------|
| **敌人数量** | 1v1 | 1vN, NvN (同屏多敌) |
| **目标选择** | 固定对手 | 选择敌人 (最近/最远/最弱/指定) |
| **目标过滤** | 按 Team | 按 Tag/阵营/距离/状态过滤 |
| **攻击判定** | 单目标命中 | 多目标命中 (AOE/群体技能) |
| **AI 决策** | 单对手评估 | 多敌人威胁评估、目标优先级 |
| **碰撞检测** | 两两检测 | 空间分区优化 (多实体场景) |

**已有的多目标基础**：
- `CharacterManager.FindNearestOpponent(charId)` — 遍历所有对手选择最近
- `CollisionSystem.CheckAttackHit` — 遍历所有对手检测命中
- `MaxCharacters = 4` — 已预留多角色容量
- `Character.Team` — 阵营区分

**需要补充的多目标能力**：
- `FindOpponents(filter)` — 按条件批量筛选敌人
- `FindBestTarget(criteria)` — 按评分标准选择最优目标
- AOE 命中判定 — 一次攻击命中多个目标
- AI 多目标威胁评估 — 距离+血量+攻击状态的综合评分

### 1.2 技术约束

- 不要求 ECS（当前为初期探索，但结构可映射为 ECS Component）
- 视图层可替换（`IGameView` 接口）
- 所有角色状态统一在技能系统下实现
- 为 FFVM 留出明确的应用点
- 最小化外部依赖（Raylib-cs 为唯一渲染依赖）

---

## 二、当前状态

### 已完成 ✅

| 模块 | 状态 | 说明 |
|------|------|------|
| 核心类型 | ✅ | FVec2, FRect, GameConstants, Direction |
| 输入系统 | ✅ | PlayerInput, InputButton (含边沿检测) |
| 角色系统 | ✅ | Character, CharacterData, CharacterManager |
| 技能系统 | ✅ | SkillDef (含 CanContinue/OnFrame 回调), SkillInstance, SkillManager |
| 物理系统 | ✅ | PhysicsBody, CollisionSystem, 推箱解析 |
| 战斗系统 | ✅ | CombatSystem, HitEvent, 击退/受击硬直 |
| 效果系统 | ✅ | EffectManager |
| 弹幕系统 | ✅ | ProjectileManager |
| AI 接口 | ✅ | IAIController, SimpleAI |
| 场景调度 | ✅ | GameScene (13步帧执行), SceneInput, SceneCommand |
| 控制台视图 | ✅ | ConsoleGameView (ASCII, 72×16) |
| **Raylib 视图** | ✅ | **RaylibGameView (图形化, 碰撞框颜色可视化)** |
| FFVM 集成 | ✅ | GameSyscalls (~40), GameVMBridge |
| **基础移动** | ✅ | **Walk/Jump/Crouch 技能 (host-driven)** |
| 轻拳攻击 | ✅ | LightPunch (含碰撞框数据) |
| 架构文档 | ✅ | D_GameArchitecture.md |

### 进行中 🔄 / 待开始 ⬚

详见子任务文件：

| 子任务 | 文件 | 状态 |
|--------|------|------|
| 显示模块切换 | [Task_DisplayModule.md](Task_DisplayModule.md) | 🔄 基础完成, 待优化 |
| 角色控制问题排查 | [Task_CharacterControl.md](Task_CharacterControl.md) | 🔄 根因已修, 待验证完整流程 |

---

## 三、阶段路线图

### Phase 0: 框架搭建 ✅
- [x] 核心类型（FVec2, FRect, GameConstants）
- [x] 输入系统（PlayerInput, InputButton）
- [x] 角色系统（Character, CharacterData, CharacterManager）
- [x] 技能系统（SkillDef, SkillInstance, SkillManager）
- [x] 物理系统（PhysicsBody, CollisionSystem）
- [x] 战斗系统（CombatSystem, HitEvent）
- [x] 效果系统（EffectManager）
- [x] 弹幕系统（ProjectileManager）
- [x] AI 接口（IAIController, SimpleAI）
- [x] 场景调度（GameScene, SceneInput, SceneCommand）
- [x] 可替换视图（IGameView, ConsoleGameView）
- [x] FFVM 集成层（GameSyscalls, GameVMBridge）
- [x] 入口点 + 项目模板
- [x] 架构文档

### Phase 1: Host-driven 技能演示 🔄
- [x] 实现基础移动技能（idle, walk, crouch, jump）
- [x] 实现轻拳攻击流程（含碰撞框）
- [x] Raylib 图形化视图（碰撞框颜色可视化）
- [ ] 实现受击反应技能
- [ ] 验证完整攻击→受击→恢复流程
- [ ] 多目标命中测试（扩展 CheckAttackHit 返回多个目标）

### Phase 2: VM-driven 技能
- [ ] 编写第一个 .ffs 技能脚本（轻拳）
- [ ] 通过 GameVMBridge 加载和执行
- [ ] 验证 Syscall 桥接正确性
- [ ] 复刻飞燕旋风腿（skill_114）
- [ ] 复刻上盘被击中（skill_25）

### Phase 3: AI 脚本化
- [ ] 编写 AI 决策 .ffs 脚本
- [ ] AI spawn 技能实例 + wait_for
- [ ] defer 级联 Kill 验证
- [ ] 多目标威胁评估 AI（选择敌人 + 目标过滤）

### Phase 4: 弹幕与效果器
- [ ] 弹幕 VM 脚本化（波动拳类）
- [ ] 持续效果器 VM 脚本化（DOT）
- [ ] 事件总线（MI-5）验证
- [ ] AOE 弹幕命中多目标

### Phase 5: 完善与优化
- [ ] 完整角色技能集
- [ ] 多角色对战（3v3 验证多目标架构）
- [ ] 帧同步/回滚验证
- [ ] 性能基线测量

### Future: ECS 重构
- [ ] 当 FFVM 功能确认完备后，用 ECS 重构整个框架

---

## 四、依赖关系

```
Phase 0 (框架) ← 无外部依赖                           ✅ DONE
Phase 1 (Host技能) ← Phase 0                          🔄 IN PROGRESS
Phase 2 (VM技能) ← Phase 1 + FFVM 编译器
Phase 3 (AI脚本) ← Phase 2 + MI-2 (SpawnScript) + MI-3 (KillInstance)
Phase 4 (弹幕/效果) ← Phase 2
Phase 5 (完善) ← Phase 3 + Phase 4
```

---

## 五、碰撞框颜色方案

Raylib 视图中的碰撞框使用以下配色方案（美观变体色）：

| 碰撞框类型 | 用途 | 填充色 (RGBA) | 轮廓色 (RGBA) | 视觉意图 |
|-----------|------|-------------|-------------|---------|
| **Pushbox** (物理碰撞框) | 角色间推挤检测 | (74,144,217,40) 半透明蓝 | (74,144,217,160) 蓝 | 中性、边界感 |
| **Hurtbox** (受击框) | 可被攻击的区域 | (92,184,92,40) 半透明绿 | (92,184,92,160) 绿 | 被动、可被命中 |
| **Hitbox** (攻击框) | 造成伤害的区域 | (217,83,79,50) 半透明红 | (217,83,79,200) 红 | 攻击性、危险感 |
| **Blockbox** (防御框) | 格挡检测区域 | (240,173,78,40) 半透明黄 | (240,173,78,160) 黄 | 防御、警戒感 |

---

## 六、外部依赖

| 依赖 | 版本 | 用途 | 兼容性 |
|------|------|------|--------|
| Raylib-cs | 6.1.1 | 图形渲染 + 键盘输入 | net8.0+ (RollForward 支持更高版本) |
| System.Numerics.Vectors | 4.5.0 | Raylib-cs 传递依赖 | netstandard2.0+ |
| FFVM | (ProjectReference) | 虚拟机核心 | netstandard2.1 / net8.0 双目标 |
