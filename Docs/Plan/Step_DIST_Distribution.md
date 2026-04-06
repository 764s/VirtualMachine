# DIST 分发基础设施

> **DIST-1~DIST-3 状态**：✅ 已完成（2026-04-06）
> **DIST-8~DIST-9 状态**：⏳ 待执行
> **Assert 数**：1007（未变化）
> **详细设计**：[Discussion/Step_DIST_Distribution.md](../Discussion/Step_DIST_Distribution.md)
> **Attach 模式讨论**：[Discussion/D_DapAttachMode.md](../Discussion/D_DapAttachMode.md)

---

## 完成内容

### DIST-1：独立 FFVM 类库项目

- `src/FFVM/FFVM.csproj`（net8.0 class library）
- 通过 `<Compile Include>` 引用 `Assets/Scripts/VM/` 下 Core / Compiler / AST / Interpreter / Debug 目录
- 包含 `StandaloneRunner/UnityStub.cs` 作为 Unity API 桩
- `dotnet pack` 产生 `FFVM.0.1.0.nupkg`
- `InternalsVisibleTo` 支持 StandaloneRunner 测试访问 internal 成员
- StandaloneRunner / Sandbox 改为 `<ProjectReference>` 引用（取代 `<Compile Include>` 散引用）
- CI 工作流三个 Job 全部适配新项目结构

**设计决策**：目标框架使用 net8.0 而非 netstandard2.1。原因：代码使用 `AggressiveOptimization`、`StructLayout(Explicit, Size=4)`、`ref struct` with ref fields 等现代 C# 特性，netstandard2.1 不完全支持（风险 R-DIST1-2 已确认）。

### DIST-2：统一 CLI 入口

- `src/FFVM.Cli/FFVM.Cli.csproj`（console app，AssemblyName = `ffvm-cli`）
- 子命令：`run <script.ffs>`、`compile <script.ffs>`、`lsp`、`dap`、`version`、`help`
- 内置 `CliSyscalls`（与 Sandbox 的 SandboxSyscalls 功能等同：print, math, time, control 共 12 个 syscall）
- `--entry <func>` 可选参数支持非 main 入口函数

**妥协**：CliSyscalls 与 SandboxSyscalls 存在代码重复。消除时间点：当 Sandbox 改为依赖 FFVM.Cli 或提取共享 Syscall 包时。当前阶段为临时妥协，两套代码独立维护。

### DIST-3：单文件发布 + VS Code 扩展适配

- `FFVM.Cli.csproj` 配置 `PublishSingleFile` + `SelfContained` + `EnableCompressionInSingleFile`
- `dotnet publish -r linux-x64` 产生 35MB 单文件可执行 `ffvm-cli`
- VS Code 扩展 `package.json` 新增 `ffvm.executablePath` 配置项（默认 `ffvm-cli`）
- `extension.js` 改为通过 `resolveExecutablePath()` 解析可执行路径
  - LSP：调用 `ffvm-cli lsp`（stdio）
  - DAP：launch 模式调用 `ffvm-cli dap`；attach 模式仍连接 TCP 端口
- 移除了对 `StandaloneRunner/bin/Release/...` 硬编码路径的依赖

---

## 待执行步骤

### DIST-8：提取 EmbeddableDapServer 到 FFVM 库

**背景**：DIST-1~DIST-3 完成后，launch 模式 DAP（`DapServer`，stdio）已随 FFVM 库分发，但 attach 模式 DAP（`EmbeddedDapServer`，TCP）仍为 Sandbox 私有实现。FFVM 的核心用户（第 2/3 层：.NET 集成者 / Unity 用户）需要 attach 模式调试嵌入在宿主帧循环中的 VM 实例。详见 [D_DapAttachMode.md](../Discussion/D_DapAttachMode.md)。

**内容**：
1. 提取 `DapServer` 与 `EmbeddedDapServer` 的共享 DAP 协议处理器（约 65-70% 代码重叠）到 `DapServerBase` 基类
2. 在 `Assets/Scripts/VM/Debug/` 新增 `EmbeddableDapServer.cs`（TCP attach 模式，`FFVM.Debug` 命名空间）
3. 重构 `DapServer` 继承 `DapServerBase`

**关键设计——正常 attach 与定制 attach**：

| 行为 | 正常 attach（默认） | 定制 attach（沙盒模式） |
|------|---------------------|----------------------|
| 目标状态 | 以目标执行状态为主，连上时 VM 可能已在运行 | 等待调试器连接后才开始执行 |
| 初始暂停 | 不暂停（除非用户设置 stopOnEntry） | 自动 WaitForConnection + StopOnEntry |
| 断点设置时机 | 动态设置 | 在 StopOnEntry 期间设好再继续 |

分发库默认为正常 attach。宿主通过可选方法调用实现定制行为：
```csharp
// 正常 attach
dap.AttachToWorld(world, program, id, "script.ffs");

// 定制 attach（沙盒模式）
dap.AttachToWorld(world, program, id, "script.ffs");
dap.WaitForConnection();   // 可选：阻塞等连接
dap.StopOnEntry();         // 可选：阻塞等 configurationDone
```

**前置**：DIST-1

**完成条件**：
1. `FFVM.Debug.EmbeddableDapServer` 在 FFVM.csproj 内编译通过
2. 外部项目可通过 NuGet 包使用 attach 模式 DAP
3. 正常 attach 和定制 attach 均可用
4. 远程调试（非 localhost）可通过构造函数参数支持

**复杂度**：中（重构提取 + 保持向后兼容）

---

### DIST-9：Sandbox 改造——消费分发库 attach API

**内容**：
1. 删除 `Sandbox/EmbeddedDapServer.cs`（774 行私有实现）
2. `SandboxRunner` 改用 `FFVM.Debug.EmbeddableDapServer`
3. 保留沙盒定制行为（WaitForConnection + StopOnEntry）

**前置**：DIST-8

**完成条件**：
1. 现有 97 项 DAP 测试全通过
2. Sandbox `--debug` 模式 attach 调试流程不变
3. `Sandbox/EmbeddedDapServer.cs` 文件已删除

**复杂度**：低（API 消费替换，逻辑不变）

**意义**：作为分发库 attach API 的"吃自己狗粮"验证——如果 Sandbox 自身能顺利迁移，外部消费者同样可以。

---

## 功能展望

| ID | 内容 | 触发时机 |
|----|------|----------|
| DIST-4 | `dotnet tool install -g ffvm-cli` 全局工具安装 | NuGet 发布后 |
| DIST-5 | GitHub Release 自动化（CI 产出 win-x64 / linux-x64 / osx-arm64 三平台可执行） | 首次 Release 时 |
| DIST-6 | `vsce package` 产生 `.vsix` 并发布到 Marketplace | 扩展功能稳定后 |
| DIST-7 | UPM 包（Unity Package Manager git URL 安装） | C 阶段 Unity 集成验证后 |

---

## 风险点

| ID | 风险 | 状态 | 说明 |
|----|------|------|------|
| R-DIST1-1 | public API 表面过宽 | 🟡 已知 | 当前所有类型均为 public（历史原因），未来需收窄为仅暴露必要类型 |
| R-DIST1-2 | netstandard2.1 不兼容 | ✅ 已确认 | 使用 net8.0 替代，放弃 .NET Framework 兼容 |
| R-DIST2-1 | CliSyscalls 与 SandboxSyscalls 重复 | 🟡 临时妥协 | 当前可接受，未来提取共享包时消除 |
