# Task: 角色控制问题排查 (Character Control Investigation)

> **状态**：🔄 根因已修, 待验证完整流程
> **父任务**：[KOF_Summary.md](../KOF_Summary.md)
> **日期**：2026-04-06

---

## 一、问题描述

控制台版本角色输入无响应：
- 角色能正常释放技能（LightPunch 可触发）
- 但角色位置不变，无法移动
- 因此无法测试正常命中逻辑

## 二、根因分析

### 根因 1（主要）：缺少移动技能

`Program.CreateDefaultCharacterData()` 仅定义了 **Idle** 和 **LightPunch** 两个技能。

系统设计为"所有行为都是技能"（SkillDef → SkillManager 状态机），
但没有定义 Walk/Jump/Crouch 技能，导致：

- 按方向键 → `PlayerInput.Held` 有方向标志
- `SkillManager.TryActivateSkill()` 遍历技能目录
- 找不到匹配方向输入的技能 → 无操作
- 角色保持 Idle，速度为零

**修复**：添加 Walk (id=1), Jump (id=2), Crouch (id=3) 技能定义。

### 根因 2（次要）：控制台输入限制

`Console.ReadKey(true)` 每次只读一个按键事件：
- 如果本帧没有按键事件，`p1Held = None`（方向释放）
- 无法检测同时按住多个键
- 按住方向键时，系统依赖操作系统的键重复事件，间隔不稳定

**修复**：
- 控制台模式：改为 `while (Console.KeyAvailable)` 循环读取所有缓冲键事件
- Raylib 模式：使用 `Raylib.IsKeyDown()` 正确检测持续按键

### 所需的架构扩展

为支持移动技能的"持续条件"和"每帧逻辑"，在 `SkillDef` 中添加：

| 新回调 | 用途 | 调用时机 |
|--------|------|---------|
| `CanContinue(ch, input) → bool` | 技能是否应继续活跃 | `TryDeactivateSkill()` 每帧开头 |
| `OnFrame(ch, input)` | 每帧逻辑（设速度等） | `SkillManager.EnterFrame()` |

## 三、完成项

- [x] 诊断根因：缺少移动技能定义
- [x] 诊断次因：Console.ReadKey 单键限制
- [x] 添加 `SkillDef.CanContinue` 回调
- [x] 添加 `SkillDef.OnFrame` 回调
- [x] 更新 `SkillManager.TryDeactivateSkill()` 检查 CanContinue
- [x] 更新 `SkillManager.EnterFrame()` 调用 OnFrame
- [x] 添加 Walk 技能 (方向输入 → 设置水平速度, 释放方向 → 回 Idle)
- [x] 添加 Jump 技能 (Up 键 → 起跳, 落地 → 结束)
- [x] 添加 Crouch 技能 (Down 键 → 蹲下, 释放 → 回 Idle)
- [x] 修复控制台输入为 `while(KeyAvailable)` 循环读取

## 四、后续验证

- [ ] 验证 Walk 技能：按住方向键角色持续移动，释放回 Idle
- [ ] 验证 Jump 技能：按 Up 起跳、可空中方向偏移、落地回 Idle
- [ ] 验证 Crouch 技能：按 Down 蹲下、推箱变小、释放回 Idle
- [ ] 验证技能优先级：Attack > Movement > Idle 的中断逻辑
- [ ] 验证 AI 角色移动：SimpleAI 发送 Left/Right 后角色应移动
- [ ] 验证 Raylib 模式同时按键：方向+攻击可同时生效
- [ ] 验证完整攻击→受击→击退→恢复流程
