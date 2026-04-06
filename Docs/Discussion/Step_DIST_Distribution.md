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

---

## 七、.NET 多版本兼容策略（DIST-10）

> 来源：2026-04-06 推演。审查三层分发架构在不同 .NET 版本消费者场景下的兼容性。

### 7.1 现状诊断

| 分发层 | 消费方式 | TFM 现状 | .NET 版本问题 |
|--------|---------|----------|--------------|
| L1 CLI 单文件 | 下载 `ffvm` 可执行 | `net8.0` + SelfContained | **无**——运行时内嵌，无外部依赖 |
| L2 NuGet 库 | `dotnet add package FFVM` | `net8.0` only | **❌ 缺口**——`netstandard2.1` / `net6.0` 消费者无法引用；未来 `net10.0` LTS 切换后 `net8.0` 出支持期 |
| L2 dotnet tool | `dotnet tool install -g ffvm` | 框架依赖 `net8.0` | **❌ 缺口**——未配置 `RollForward`，在仅安装 `net9.0+` SDK 的环境找不到 `net8.0` 运行时 |
| L3 UPM 源码 | Package Manager git URL | 源码编译 | **⚠️ 部分已处理**——`SyscallArgs` 的 `ref field`（C# 11）已有 `FFVM_REF_FIELD` / `FFVM_LEGACY_CSHARP` 条件编译；但缺少整体审计与文档 |
| CI 验证 | GitHub Actions | `dotnet-version: '8.0.x'` | **❌ 缺口**——仅在 `net8.0` 测试，不验证 `netstandard2.1` 目标或更高版本运行时兼容性 |

### 7.2 代码特性依赖审计

| 特性 | 最低要求 | netstandard2.1 可用 | 当前使用位置 | 处理方案 |
|------|---------|--------------------|-----------|---------| 
| `unsafe` / `fixed` | C# 1.0 + AllowUnsafeBlocks | ✅ | VMWorld、VMInstanceState、SyscallArgs | 无需处理 |
| `ref struct` | C# 7.2 | ✅ | SyscallArgs | 无需处理 |
| `ref field`（`ref T _field`） | C# 11 / net7.0+ | ❌ | SyscallArgs 现代路径 | ✅ 已有条件编译 `FFVM_REF_FIELD` / `FFVM_LEGACY_CSHARP` |
| `StructLayout` / `FieldOffset` | .NET Standard 1.0+ | ✅ | Number、Instruction、VMInstanceState | 无需处理 |
| `MethodImpl(AggressiveOptimization)` | .NET Core 3.0 / netstandard2.1 | ✅ | VMWorld.ExecuteInstance | 无需处理（enum 值 512 在 netstandard2.1 ref assembly 中已定义） |
| `MethodImpl(AggressiveInlining)` | .NET 4.5+ | ✅ | BenchmarkRunner（测试代码，不入库） | 无需处理 |
| `Array.Empty<T>()` | .NET 4.6+ / netstandard1.3+ | ✅ | SyscallSignature 等 | 无需处理 |

**结论**：核心代码的 `netstandard2.1` 兼容性障碍为零（`ref field` 已有条件编译）。可安全添加 `netstandard2.1` TFM。

### 7.3 对策方案

#### A. FFVM.csproj 双目标（核心动作）

```xml
<!-- 改前 -->
<TargetFramework>net8.0</TargetFramework>

<!-- 改后 -->
<TargetFrameworks>netstandard2.1;net8.0</TargetFrameworks>
```

效果：
- `netstandard2.1`：覆盖 .NET Core 3.0+ / .NET 5+ / .NET 6~10+ / Unity 2021+ 包引用场景
- `net8.0`：保留完整优化（JIT 内联提示、TieredPGO 等），CI 主测试路径
- NuGet 包自动包含两个目标的程序集，消费者 SDK 自动选最优匹配

条件编译策略（`FFVM.csproj` 已有模式可复用）：
```xml
<PropertyGroup Condition="'$(TargetFramework)' == 'netstandard2.1'">
  <DefineConstants>$(DefineConstants);FFVM_LEGACY_CSHARP</DefineConstants>
</PropertyGroup>
```

- `SyscallArgs` 的 `ref field` 路径已通过 `FFVM_LEGACY_CSHARP` 切换到 `unsafe` 指针路径 ✅
- 未来若引入更多 net8.0+ 专属 API，统一用 `#if !FFVM_LEGACY_CSHARP` 隔离

#### B. CLI dotnet tool RollForward

```xml
<!-- FFVM.Cli.csproj 追加 -->
<RollForward>LatestMajor</RollForward>
```

效果：`dotnet tool install -g ffvm` 在仅安装 `net9.0` 或 `net10.0` SDK 的环境也能运行。
不影响 SelfContained 单文件发布（该属性对 self-contained 无效）。

#### C. CI 多版本验证（低成本高收益）

在现有 CI matrix 中追加一个 `net9.0`（或未来 `net10.0`）构建验证 job：

```yaml
strategy:
  matrix:
    dotnet-version: ['8.0.x', '9.0.x']
```

验证内容：
1. `dotnet build src/FFVM/FFVM.csproj`（双目标）构建通过
2. StandaloneRunner 在 `net9.0` 运行时上测试通过（前向兼容）
3. `dotnet pack` 产生的 nupkg 包含两个 TFM 的程序集

#### D. .NET 版本生命周期感知

| .NET 版本 | 类型 | 支持截止 | FFVM 影响 |
|-----------|------|---------|----------|
| net6.0 | LTS | 2024-11-12 ❌ 已过期 | 不支持（消费者应升级） |
| net7.0 | STS | 2024-05-14 ❌ 已过期 | 不支持 |
| net8.0 | LTS | 2026-11-10 | ✅ 当前主目标 |
| net9.0 | STS | 2026-05-12 | ✅ 前向兼容（消费 net8.0 目标） |
| net10.0 | LTS | ~2028-11 | ✅ netstandard2.1 + net8.0 双目标自动适配 |

策略：
- 当 `net8.0` 接近 EOL 时（~2026 Q3），将 `net8.0` 升级为 `net10.0`（新 LTS）
- `netstandard2.1` 作为永久兜底目标，确保老版本 + Unity 兼容性
- 单文件 CLI 发布同步切换到 `net10.0` 目标（SelfContained 不受影响）

### 7.4 复杂度评估

| 子任务 | 复杂度 | 改动范围 |
|--------|--------|---------|
| FFVM.csproj 双目标 + 条件编译 | 极低 | ~5 行 csproj 改动 |
| FFVM.Cli RollForward | 极低 | 1 行 |
| CI matrix 扩展 | 低 | ci.yml ~3 行 |
| 测试验证（双目标 build + pack） | 低 | 运行现有测试即可 |

**总复杂度**：低。无核心代码改动，纯项目配置 + CI 配置。

### 7.5 风险

| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| R-DIST10-1 | netstandard2.1 目标缺少某些 API（如未来新增的 net8.0+ 专属调用） | 编译失败 | CI 双目标构建自动拦截；条件编译隔离 |
| R-DIST10-2 | Unity 2021 的 C# 9 编译器不支持某些语法 | UPM 用户编译失败 | 条件编译 `FFVM_LEGACY_CSHARP` 已覆盖已知特性；Unity 路径为源码编译而非 NuGet，TFM 无关 |
| R-DIST10-3 | dotnet tool RollForward 到 net10.0 运行时行为差异 | 极低概率的运行时行为变化 | CI 多版本测试覆盖；SelfContained CLI 作为稳定后备 |
