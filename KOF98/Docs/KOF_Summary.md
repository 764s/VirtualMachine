# KOF 探索 — 总览

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
| **界面设计** | ✅ | **GameSettings + 控制界面 (Tab 开关, AI/自动复活/重新开始)** |
| 架构文档 | ✅ | D_GameArchitecture.md |

### 已完成子任务（已归档）

以下子任务已在 Phase 0/1 中实际执行完成，跳过后续子计划流程：

| 子任务 | 文件 | 状态 |
|--------|------|------|
| 显示模块切换 (Raylib) | [Plan/Task_DisplayModule.md](Plan/Task_DisplayModule.md) | ✅ 基础完成 |
| 角色控制问题排查 | [Plan/Task_CharacterControl.md](Plan/Task_CharacterControl.md) | ✅ 根因已修 |

---

## 三、串行任务列表

> **流程**：讨论 → 总览排期 → 制定子任务 → 执行

当前位置 → **KOF-T2**

| ID | 任务 | 状态 | 完成条件 | 依赖 | 讨论/计划 |
|----|------|------|---------|------|----------|
| KOF-T1 | 界面设计（常驻 HUD + 控制界面） | ✅ 已完成 | 常驻 HUD 显示角色名/血量/能量；控制界面覆盖场景，含 AI 开关、重新开始、自动复活开关 | — | [D_UIDesign.md](Discussion/D_UIDesign.md) |
| KOF-T2 | 技能脚本化基础设施 | ⏳ 等待中 | SkillDef 扩展（AllowedStances/Priority/VMModuleSlot）+ 裁决层重构（分层候选池）+ 条件入口机制 | KOF-T1 ✅ | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §七 |
| KOF-T3 | 首批 FFS 脚本 — 前 4 个 (S01~S04) | ⬚ 待排期 | 编写 skill_idle/walk_forward/jump/light_punch.ffs，GameVMBridge 加载执行，现有 Syscall 足够 | KOF-T2 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §五 |
| KOF-T4 | Syscall 扩展 + 碰撞框脚本化 | ⬚ 待排期 | 新增 SetHitbox/SetHurtbox/ClearHitbox/SetPushBox + ApplyHitReaction；碰撞框由脚本设置 | KOF-T3 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §八~§九 |
| KOF-T5 | 首批 FFS 脚本 — 后 4 个 (S05~S08) | ⬚ 待排期 | 编写 skill_crouch_punch/hit_high/hard_knockdown/stand_up.ffs；验证完整攻击→受击→倒地→起身流程 | KOF-T4 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §五 |
| KOF-T6 | 多目标命中 + 硬直机制 | ⬚ 待排期 | CheckAttackHit 多目标返回 + 时间轴暂停硬直实现 | KOF-T5 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §六 |
| KOF-T7 | 连招 V1（黑板变量） | ⬚ 待排期 | 通过 GetBlackboard/SetBlackboard 实现连招计数器+递减系数，写法朝理想方案 C 靠拢 | KOF-T5 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §十三.1 |
| KOF-T8 | AI 脚本化 | ⬚ 待排期 | AI 决策 .ffs 脚本 + spawn 技能实例 + wait_for + 多目标威胁评估 | KOF-T5 | — |
| KOF-T9 | 弹幕与效果器脚本化 | ⬚ 待排期 | 弹幕 VM 脚本化 + 持续效果器 VM 脚本化 | KOF-T5 | — |
| KOF-T10 | SK3 碰撞框交互方式验证 | ⬚ 待排期 | 验证 VM↔宿主交互性能；决定 push vs query 最终方案 | KOF-T4 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) §十二 |

---

## 四、讨论区索引

> **定位**：对新需求的讨论。讨论收敛后转化为串行任务列表中的排期项。
> **文件位置**：`KOF98/Docs/Discussion/`

| # | 文档 | 主题 | 状态 | 日期 |
|---|------|------|------|------|
| KD1 | [D_GameArchitecture.md](Discussion/D_GameArchitecture.md) | 游戏架构讨论（帧执行模型、技能状态机、VM 应用分级、视图层设计） | ✅ 已完成 | 2026-04-06 |
| KD2 | [D_UIDesign.md](Discussion/D_UIDesign.md) | 界面设计讨论（常驻 HUD + 控制界面） | ✅ 已完成 | 2026-04-06 |
| KD3 | [D_SkillScripting.md](Discussion/D_SkillScripting.md) | 技能 FFS 脚本化讨论（SK1~SK12 全部收敛，仅 SK3 待验证） | ✅ 讨论完成 | 2026-04-06 |

---

## 五、计划区索引

> **定位**：由串行任务列表 + 讨论结论共同决定的子任务执行计划。
> **文件位置**：`KOF98/Docs/Plan/`

| # | 文档 | 主题 | 状态 | 日期 |
|---|------|------|------|------|
| KP1 | [Task_DisplayModule.md](Plan/Task_DisplayModule.md) | 显示模块切换 (Raylib 图形化视图) | ✅ 基础完成 | 2026-04-06 |
| KP2 | [Task_CharacterControl.md](Plan/Task_CharacterControl.md) | 角色控制问题排查 (移动技能 + 输入修复) | ✅ 根因已修 | 2026-04-06 |
| KP3 | [Step_SkillScripting.md](Plan/Step_SkillScripting.md) | 技能 FFS 脚本化实施计划 (KOF-T2~T10) | ⏳ 等待 KOF-T1 | 2026-04-06 |

---

## 六、阶段路线图

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

### Phase 1: Host-driven 基础 + UI ✅
- [x] 实现基础移动技能（idle, walk, crouch, jump）
- [x] 实现轻拳攻击流程（含碰撞框）
- [x] Raylib 图形化视图（碰撞框颜色可视化）
- [x] 界面设计（常驻 HUD + 控制界面）— **KOF-T1** ✅

### Phase 2: 技能脚本化基础设施
- [ ] SkillDef 扩展（AllowedStances/Priority/VMModuleSlot）— **KOF-T2**
- [ ] 裁决层重构（分层候选池 §七.4）— **KOF-T2**
- [ ] 条件入口机制（专有条件检测阶段 §七.5）— **KOF-T2**

### Phase 3: 首批 FFS 脚本
- [ ] 前 4 个脚本 (S01~S04: idle/walk/jump/light_punch) — **KOF-T3**
- [ ] Syscall 扩展 + 碰撞框脚本化 — **KOF-T4**
- [ ] 后 4 个脚本 (S05~S08: crouch_punch/hit_high/hard_knockdown/stand_up) — **KOF-T5**
- [ ] 验证完整攻击→受击→倒地→起身流程

### Phase 4: 扩展机制
- [ ] 多目标命中 + 时间轴暂停硬直 — **KOF-T6**
- [ ] 连招 V1（黑板变量）— **KOF-T7**
- [ ] SK3 碰撞框交互方式验证 — **KOF-T10**

### Phase 5: AI 脚本化 + 弹幕
- [ ] AI 决策 .ffs 脚本 — **KOF-T8**
- [ ] 弹幕与效果器脚本化 — **KOF-T9**

### Phase 5: 完善与优化
- [ ] 完整角色技能集（飞燕旋风腿、上盘被击中等高级技能复刻）
- [ ] 多角色对战（3v3 验证多目标架构）
- [ ] 帧同步/回滚验证
- [ ] 性能基线测量

### Future: ECS 重构
- [ ] 当 FFVM 功能确认完备后，用 ECS 重构整个框架

---

## 七、展望与风险

### 功能展望

| ID | 展望 | 来源 | 说明 |
|----|------|------|------|
| KO-1 | 角色精灵渲染（替代矩形简笔画） | Task_DisplayModule | 后续优化 |
| KO-2 | 碰撞框显示开关 | Task_DisplayModule | 可单独显示/隐藏各类框 |
| KO-3 | 摄像机跟随 | Task_DisplayModule | 场景滚动时需要 |
| KO-4 | 暂停/逐帧前进 | Task_DisplayModule | 调试用 |
| KO-5 | 特效粒子 | Task_DisplayModule | 命中火花、格挡闪光 |
| KO-6 | 控制界面扩展（切换角色、创建友军/怪物、无限蓝等） | D_UIDesign | 控制界面未来功能 |

### 风险点

| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| KR-1 | ExecuteInstance 不是 VMWorld 公开 API | VMBridge 无法逐实例 Tick | 需确认 API 可用性 |
| KR-2 | float 非确定性 | 帧同步不可靠 | 开发期使用 float，发布期切换 Fix64 |
| KR-3 | 碰撞框数据硬编码 | 维护困难 | 将来从 JSON/资源文件加载 |

---

## 八、碰撞框颜色方案

Raylib 视图中的碰撞框使用以下配色方案（美观变体色）：

| 碰撞框类型 | 用途 | 填充色 (RGBA) | 轮廓色 (RGBA) | 视觉意图 |
|-----------|------|-------------|-------------|---------|
| **Pushbox** (物理碰撞框) | 角色间推挤检测 | (74,144,217,40) 半透明蓝 | (74,144,217,160) 蓝 | 中性、边界感 |
| **Hurtbox** (受击框) | 可被攻击的区域 | (92,184,92,40) 半透明绿 | (92,184,92,160) 绿 | 被动、可被命中 |
| **Hitbox** (攻击框) | 造成伤害的区域 | (217,83,79,50) 半透明红 | (217,83,79,200) 红 | 攻击性、危险感 |
| **Blockbox** (防御框) | 格挡检测区域 | (240,173,78,40) 半透明黄 | (240,173,78,160) 黄 | 防御、警戒感 |

---

## 九、外部依赖

| 依赖 | 版本 | 用途 | 兼容性 |
|------|------|------|--------|
| Raylib-cs | 6.1.1 | 图形渲染 + 键盘输入 | net8.0+ (RollForward 支持更高版本) |
| System.Numerics.Vectors | 4.5.0 | Raylib-cs 传递依赖 | netstandard2.0+ |
| FFVM | (ProjectReference) | 虚拟机核心 | netstandard2.1 / net8.0 双目标 |
