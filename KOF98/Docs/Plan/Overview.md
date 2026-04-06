# KOF98 Practice — 总览计划

> **状态**：🔄 进行中
> **目标**：通过复刻 KOF98 核心机制，探索 FFVM 游戏应用

---

## 阶段路线图

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

### Phase 1: Host-driven 技能演示
- [ ] 实现基础移动技能（idle, walk, crouch, jump）
- [ ] 实现轻拳攻击流程（含碰撞框）
- [ ] 实现受击反应
- [ ] 验证完整攻击→受击→恢复流程
- [ ] 控制台视图优化

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

### Phase 4: 弹幕与效果器
- [ ] 弹幕 VM 脚本化（波动拳类）
- [ ] 持续效果器 VM 脚本化（DOT）
- [ ] 事件总线（MI-5）验证

### Phase 5: 完善与优化
- [ ] 完整角色技能集
- [ ] 多角色对战
- [ ] 帧同步/回滚验证
- [ ] 性能基线测量

### Future: ECS 重构
- [ ] 当 FFVM 功能确认完备后，用 ECS 重构整个框架

---

## 依赖关系

```
Phase 0 (框架) ← 无外部依赖
Phase 1 (Host技能) ← Phase 0
Phase 2 (VM技能) ← Phase 1 + FFVM 编译器
Phase 3 (AI脚本) ← Phase 2 + MI-2 (SpawnScript) + MI-3 (KillInstance)
Phase 4 (弹幕/效果) ← Phase 2
Phase 5 (完善) ← Phase 3 + Phase 4
```
