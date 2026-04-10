# KOF98 Practice — FFVM 格斗游戏探索

> **定位**：探索性实践。通过复刻 KOF98 核心机制来验证 FFVM 在实际游戏中的应用点。
> **总览**：[KOF_Summary.md](Docs/KOF_Summary.md)（总状态、串行任务列表、阶段路线图）

---

## 快速开始

### 一键运行（推荐，Windows）

1. 双击 `KOF98\kof98-init.cmd`（首次运行，自动完成初始化）
2. 双击生成的 `KOF98\run-kof98.cmd`（后续运行）

### 手动运行

```bash
# 1. 从模板生成 .csproj
cp KOF98.csproj.template KOF98.csproj

# 2. 构建
dotnet build KOF98.csproj

# 3. 运行（Raylib 图形化渲染）
dotnet run --project KOF98.csproj -- --raylib

# 4. 运行（控制台渲染）
dotnet run --project KOF98.csproj

# 5. 无头模式（仅模拟）
dotnet run --project KOF98.csproj -- --headless --frames 600

# 6. 调试模式（Raylib + DAP 调试器，端口 4711）
dotnet run --project KOF98.csproj -- --raylib --debug
```

### VS Code 调试（C# + FFScript 同时断点）

> 前提：已安装 [C# Dev Kit](https://marketplace.visualstudio.com/items?itemName=ms-dotnettools.csdevkit) 和 FFVM 扩展。

1. 运行 `kof98-init.cmd`（自动生成 `.vscode/launch.json`）
2. 用 VS Code 打开仓库根目录
3. 在 `.cs` 和 `.ffs` 文件中设置断点
4. 按 F5，选择 **"KOF98: C# + FFVM Debug"**
5. 游戏窗口打开后，C# 和 FFScript 断点均可触发

### 键位（P1）

| 键 | 动作 |
|----|------|
| WASD / 方向键 | 移动 |
| J | 轻拳 (LP) |
| K | 重拳 (HP) |
| U | 轻脚 (LK) |
| I | 重脚 (HK) |

---

## 文档结构

```
KOF98/Docs/
├── KOF_Summary.md           ← KOF 总览（总状态、串行任务列表、阶段路线图）
├── Discussion/              ← 讨论区（新需求讨论，收敛后排进总览）
│   ├── D_GameArchitecture.md    游戏架构讨论
│   └── D_UIDesign.md           界面设计讨论
└── Plan/                    ← 计划区（由串行任务 + 讨论结论决定）
    ├── Task_DisplayModule.md    显示模块切换（已完成）
    └── Task_CharacterControl.md 角色控制排查（已完成）
```

---

## 提示命令

| 命令 | 用途 |
|------|------|
| `/check-kof` | 查看 KOF 串行任务列表当前状态 |
| `/check-and-next-kof` | 检查并推进 KOF 下一个任务 |
| `/requirement-kof` | 提交新需求并排期到 KOF 串行任务列表 |
