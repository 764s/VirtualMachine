# FFVM 分发计划（DIST 系列）

> **来源**：2026-04-05 讨论。C 阶段宿主阻塞期间，推进引擎侧可独立完成的分发基础设施。
> **目标**：让其他人在自己的项目中使用 FFVM 时，心智负担和操作负担最小化——力求单文件 / 单次点击。

---

## 一、现状诊断

当前 FFVM 的消费方式是"克隆整个 VirtualMachine 仓库 + 手动配置 .csproj 引用 VM 源码"。
消费者需要理解 Unity 目录结构、手写 UnityStub、注册 SyscallTable、手动 build StandaloneRunner、
手动安装未打包的 VS Code 扩展。这与"单文件单次点击"的目标差距巨大。

### 关键发现

- **VM 核心代码（Core / Compiler / AST / Interpreter）已零 Unity 依赖**：无 `using UnityEngine`，无 `Debug.Log`。
  Unity 依赖仅存在于 Tests（通过 `UnityStub.cs` 桩解决）。
- **P0（Logger 解耦）已天然完成**：核心代码无需改造。
- Sandbox 已具备完整的独立运行时（SandboxRunner + SandboxSyscalls + DAP + LSP）。
- VS Code 扩展已功能完整（语法高亮 + DAP attach + LSP），但未打包 .vsix。

---

## 二、三层分发架构

### 2.1 架构总览

```
分发层级：
┌─────────────────────────────────────────────────────────────┐
│ 第 1 层：尝鲜者 — ffvm 单文件可执行                         │
│   下载 ffvm.exe → ffvm run script.ffs                      │
│   内含 Compiler + VM + Sandbox 运行时 + 内置 Syscall        │
│   同时提供 ffvm lsp / ffvm dap 子命令                       │
├─────────────────────────────────────────────────────────────┤
│ 第 2 层：.NET 集成者 — NuGet 包                             │
│   dotnet add package FFVM → 3 行 C# 跑通                   │
│   LSP/DAP 工具链：dotnet tool install -g ffvm               │
├─────────────────────────────────────────────────────────────┤
│ 第 3 层：Unity 用户 — UPM 包                                │
│   Package Manager → Add git URL → 完成                     │
│   LSP/DAP 工具链：同第 1/2 层的 ffvm CLI                    │
└─────────────────────────────────────────────────────────────┘

工具链归属（关键决策）：
  LSP / DAP 服务器 = ffvm CLI 的子命令（ffvm lsp / ffvm dap）
  VS Code 扩展 = 薄客户端，启动 ffvm lsp 子进程 + stdio 通信
  扩展设置：ffvm.executablePath（默认在 PATH 中查找）
```

### 2.2 语言服务器归属决策

> **结论**：LSP/DAP 服务器是 `ffvm` CLI 可执行文件的子命令。

**理由**：
1. LSP/DAP 实现（`LspServer.cs` / `DapServer.cs`）依赖完整的编译器和 VM 运行时。
2. 这些代码必须编译进一个可执行进程——不可能只放在 VS Code 扩展里。
3. 统一为 `ffvm lsp` / `ffvm dap` 子命令后：
   - 三个分发层级共享同一个工具链入口
   - VS Code 扩展只需 `spawn("ffvm", ["lsp"])` + stdio，无需内嵌运行时
   - 与 rust-analyzer、gopls、clangd 等主流语言服务器模式一致

**VS Code 扩展配置**：
```jsonc
// settings.json
{ "ffvm.executablePath": "ffvm" }  // 默认 PATH 查找；可手动指定
```

扩展修改点：`package.json` 中 DAP `program` 字段从硬编码 `StandaloneRunner/bin/...` 改为读取 `ffvm.executablePath` 设置。

---

## 三、串行步骤

### DIST-1：创建独立 FFVM 类库项目

**内容**：在仓库中创建 `src/FFVM/FFVM.csproj`（class library，`netstandard2.1`），
将 `Assets/Scripts/VM/` 中 Core / Compiler / AST / Interpreter 的源码通过 `<Compile Include>` 引用。
明确 public / internal 边界：公有 API 仅暴露 `VMWorld`、`VMProgram`、`BytecodeCompiler`、`SyscallTable`、`SyscallArgs`、`Number`。

**完成条件**：
1. `dotnet build src/FFVM/FFVM.csproj` 成功
2. `dotnet pack` 产生 `FFVM.x.y.z.nupkg`
3. StandaloneRunner / Sandbox 可通过 ProjectReference 引用此项目（替代 `<Compile Include="../Assets/...">` 散引用）

**复杂度**：低（纯项目配置，无代码改动——核心代码已零 Unity 依赖）

**风险**：
| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| R-DIST1-1 | public API 表面过宽暴露内部类型 | 消费者依赖内部实现 | 首版仅标记必要类型为 public，其余 internal |
| R-DIST1-2 | netstandard2.1 缺少 Unsafe/Span API | 编译失败 | 确认当前代码 polyfill 或改用 net8.0 multi-target |

---

### DIST-2：统一 CLI 入口（ffvm 可执行）

**内容**：创建 `src/FFVM.Cli/` 项目（console app），整合：
- `run <script.ffs>` — 编译 + 运行（复用 SandboxRunner + SandboxSyscalls）
- `compile <script.ffs>` — 仅编译检查
- `lsp` — 启动 LSP 服务器（stdio）
- `dap [--port N]` — 启动 DAP 服务器（stdio / TCP）
- `version` — 版本信息

**前置**：DIST-1

**完成条件**：
1. `dotnet run --project src/FFVM.Cli -- run script.ffs` 可编译执行 .ffs 脚本
2. `dotnet run --project src/FFVM.Cli -- lsp` 可启动 LSP stdio 通信
3. `dotnet run --project src/FFVM.Cli -- dap` 可启动 DAP stdio 通信
4. 单元测试验证 CLI 参数解析

**复杂度**：中（需整合 Sandbox/StandaloneRunner 的 DAP/LSP 启动逻辑）

---

### DIST-3：单文件发布 + VS Code 扩展打包

**内容**：
1. 配置 `FFVM.Cli` 的 `PublishSingleFile` + `SelfContained`（或 `dotnet tool`）
2. GitHub Actions / 手动脚本产生 `ffvm.exe`（win-x64）、`ffvm`（linux-x64/osx-arm64）
3. `vscode-ffvm-debug/` 执行 `vsce package` 产生 `.vsix`
4. 扩展 `package.json` 改为读取 `ffvm.executablePath` 设置

**前置**：DIST-2

**完成条件**：
1. 跨平台单文件可执行正常运行 `ffvm run / lsp / dap`
2. `.vsix` 安装后，VS Code 打开 `.ffs` 文件获得语法高亮 + 补全 + 调试
3. README 更新分发说明

**复杂度**：中（跨平台发布配置 + 扩展打包）

---

## 四、理想最终用户旅程

```
场景 A: 策划/脚本手想试用 FFScript
  1. 下载 ffvm.exe（GitHub Releases 单文件）
  2. 写 hello.ffs → ffvm run hello.ffs
  3. 安装 VS Code 扩展 → 语法高亮 / 补全 / 调试自动就绪
     （扩展通过 ffvm.executablePath 找到 ffvm.exe，调用 ffvm lsp）

场景 B: 程序员想在自己的 .NET 项目中嵌入 FFVM
  1. dotnet add package FFVM
  2. 3 行 C# 初始化 + 注册 Syscall → world.Tick()
  3. dotnet tool install -g ffvm → VS Code 扩展调用 ffvm lsp

场景 C: Unity 项目想接入 FFVM
  1. Package Manager → Add git URL（UPM 包）
  2. 注册真实 Syscall
  3. ffvm CLI（同 A/B）→ VS Code 编辑体验
```

| 场景 | 运行时来源 | LSP/DAP 来源 | VS Code 扩展 |
|------|-----------|-------------|-------------|
| A 尝鲜者 | `ffvm` CLI 内置 | 同一个 `ffvm` CLI | .vsix / Marketplace |
| B .NET 集成者 | NuGet 包 `FFVM`（库） | `dotnet tool install -g ffvm` | 同上 |
| C Unity 用户 | UPM 包（源码编译进 Unity） | `ffvm` CLI（下载或 dotnet tool） | 同上 |

---

## 五、与现有计划的关系

- DIST 系列**不阻塞 C 阶段**：VM 核心代码不变，仅新增项目配置和 CLI 入口。
- DIST-1/DIST-2 可在 C1 宿主阻塞期间推进。
- DIST-3 的 UPM 包部分依赖 C 阶段 Unity 集成验证（可后置）。
- 三个 DIST 步骤完成后，C3（技能资源管线）的 .ffs 加载/缓存策略可基于 FFVM NuGet 包的 public API 设计。

---

## 六、Attach 模式缺口补全（DIST-8~DIST-9）

> 来源：[D_DapAttachMode.md](D_DapAttachMode.md)（2026-04-06 讨论）

DIST-1~DIST-3 完成后发现：launch 模式 DAP（stdio）已随库分发，但 attach 模式 DAP（TCP）仍为 Sandbox 私有实现。第 2/3 层用户（.NET 集成者 / Unity 用户）嵌入宿主的核心场景无法使用 launch 模式，需要 attach。

**行动**：
- **DIST-8**：提取 `EmbeddableDapServer` 到 FFVM 库（`FFVM.Debug` 命名空间），含共享协议基类 + TCP attach 服务器
- **DIST-9**：Sandbox 改造为消费分发库 attach API，删除私有实现

详细步骤见 [Plan/Step_DIST_Distribution.md](../Plan/Step_DIST_Distribution.md)。
