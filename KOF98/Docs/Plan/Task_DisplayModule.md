# Task: 显示模块切换 (Display Module Switch)

> **状态**：🔄 基础完成, 待优化
> **父任务**：[Overview.md](Overview.md)
> **日期**：2026-04-06

---

## 一、问题背景

原有控制台 ASCII 渲染（ConsoleGameView, 72×16 字符网格）过于简陋：
- 分辨率极低，碰撞框难以精确显示
- 无法用颜色区分不同类型碰撞框
- 控制台输入限制（单键检测、无同时按键）
- 调试信息显示空间不足

## 二、解决方案

引入 **Raylib-cs**（Raylib 的 C# 绑定）作为图形化渲染库：

### 选型理由

| 候选方案 | 0依赖性 | 输入支持 | 复杂度 | 选择 |
|---------|---------|---------|--------|------|
| Raylib-cs | ✅ 单 NuGet 包含原生库 | ✅ IsKeyDown 同时按键 | ⬇️ 最低 | ✅ 选中 |
| SDL2-CS | ❌ 需单独安装 SDL2 | ✅ | ⬆️ 中等 | ❌ |
| SFML.Net | ❌ 需 SFML 原生库 | ✅ | ⬆️ 中等 | ❌ |
| Silk.NET | ✅ NuGet | ✅ | ⬆️ 较高 | ❌ |

### 技术细节

- **包**：Raylib-cs 6.1.1 (NuGet, 含原生 Raylib 库)
- **目标框架**：net8.0 (RollForward=LatestMajor 支持更高版本)
- **窗口**：960×600 像素
- **碰撞框颜色**：半透明填充 + 实线轮廓（详见 Overview.md §五）
- **输入**：`Raylib.IsKeyDown()` 支持同时多键检测

## 三、完成项

- [x] 添加 Raylib-cs 6.1.1 NuGet 依赖到 KOF98.csproj.template
- [x] 创建 `RaylibGameView.cs` 实现 `IGameView` 接口
- [x] 碰撞框颜色可视化（Pushbox/Hurtbox/Hitbox/Blockbox 各有独立配色）
- [x] HUD 渲染（HP条、能量条、回合/帧信息）
- [x] 角色状态指示（WALK/JUMP/CROUCH/ATK/HIT/BLOCK/K.O.）
- [x] 碰撞框图例（Legend）
- [x] 调试信息面板（位置/速度/朝向/技能状态）
- [x] `--raylib` 启动参数切换图形模式
- [x] `RaylibGameView.CollectInput()` 替代 Console.ReadKey

## 四、后续优化

- [ ] 角色精灵渲染（替代矩形简笔画）
- [ ] 碰撞框显示开关（可单独显示/隐藏各类框）
- [ ] 摄像机跟随（当场景需要滚动时）
- [ ] 帧数/性能覆盖显示
- [ ] 暂停/逐帧前进功能（调试用）
- [ ] 特效粒子（命中火花、格挡闪光）
