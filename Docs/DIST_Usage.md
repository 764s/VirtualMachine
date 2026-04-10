# FFVM 分发使用说明

> **适用对象**：想在 Unity 外独立使用 FFVM 的开发者。
> **前提**：已安装 [.NET 8.0+ SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

---

## 无脑模式（推荐新手）

> 只需双击两次即可跑通。

### Windows

```
1. 双击  Sandbox\sandbox-init.cmd    ← 自动完成全部初始化
2. 双击  Sandbox\run-sandbox.cmd     ← 编译并启动沙盒
3. 输入 R 回车                        ← 运行脚本
```

### macOS / Linux

```bash
1. bash Sandbox/sandbox-init.sh       # 一键初始化
2. ./run-sandbox.sh                   # 启动沙盒
3. 输入 R 回车                         # 运行脚本
```

初始化脚本自动完成：
- 从模板生成 `.csproj`
- 编译项目（Release）
- 生成启动脚本
- 配置 VS Code 调试（可选）
- 安装 VS Code FFVM 插件（可选，需 npm + code CLI）

### 开始写代码

编辑 `Sandbox/scripts/main.ffs`，然后在沙盒中按 `R` 即可重新编译运行。

```ffs
func main() {
    print(42)
}
```

---

## 标准模式

适合理解 .NET 项目结构的开发者，提供完整控制。

### 场景 A：尝鲜者 — 直接运行 FFScript

```bash
# 1. 构建 CLI 工具
dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Release

# 2. 运行脚本
dotnet run --project src/FFVM.Cli -- run path/to/script.ffs

# 3. 仅编译检查（不运行）
dotnet run --project src/FFVM.Cli -- compile path/to/script.ffs

# 4. 启动 LSP 语言服务器（供编辑器使用）
dotnet run --project src/FFVM.Cli -- lsp

# 5. 启动 DAP 调试服务器
dotnet run --project src/FFVM.Cli -- dap
```

### 场景 B：.NET 集成者 — 在自己的项目中嵌入 FFVM

```bash
# 1. 添加 FFVM 包引用（本地开发时使用 ProjectReference）
dotnet add reference path/to/src/FFVM/FFVM.csproj
```

```csharp
// 2. 三行代码跑通
using FFVM;
using FFVM.Compiler;

// 编译脚本
var compiler = new BytecodeCompiler();
var syscallMap = new Dictionary<string, int> { { "print", 0 } };
var syscallTable = new SyscallTable();
syscallTable.Register(0, "print", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    Console.WriteLine(args.GetNumber(0));
});
var result = compiler.Compile(source, "main", syscallMap, syscallTable);

// 运行
var world = new VMWorld();
world.Modules.Load(0, result.Program);
world.Syscalls.Register(0, "print", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    Console.WriteLine(args.GetNumber(0));
});
int instanceId = world.SpawnInstance(0, 0);
while (true)
{
    world.Tick();
    ref var inst = ref world.Pool.Instances[instanceId];
    if ((inst.StateFlags & VMStateFlags.Completed) != 0) break;
}
```

### 场景 C：Sandbox 交互开发

```bash
# 1. 从模板生成项目文件
cp Sandbox/Sandbox.csproj.template Sandbox/Sandbox.csproj

# 2. 交互模式
dotnet run --project Sandbox/Sandbox.csproj

# 3. 命令行模式
dotnet run --project Sandbox/Sandbox.csproj -- --run       # 编译并运行
dotnet run --project Sandbox/Sandbox.csproj -- --compile   # 仅编译

# 4. 带 DAP 调试服务器的交互模式
dotnet run --project Sandbox/Sandbox.csproj -- --debug
```

### 场景 D：KOF98 格斗游戏实践

```bash
# 一键运行（推荐）
# Windows: 双击 KOF98\kof98-init.cmd

# 手动运行
cp KOF98/KOF98.csproj.template KOF98/KOF98.csproj
dotnet run --project KOF98/KOF98.csproj

# Raylib 图形化窗口模式
dotnet run --project KOF98/KOF98.csproj -- --raylib

# 无头模式（仅模拟）
dotnet run --project KOF98/KOF98.csproj -- --headless --frames 600

# 调试模式（Raylib + DAP 调试器，端口 4711）
dotnet run --project KOF98/KOF98.csproj -- --raylib --debug
```

#### VS Code 调试（C# + FFScript 同时断点）

> `kof98-init.cmd` 自动生成 `.vscode/launch.json`，支持 C# + FFVM 双调试器。

```
1. 运行  KOF98\kof98-init.cmd          ← 初始化 + 生成调试配置
2. 在 VS Code 打开仓库根目录
3. 在 .cs 和 .ffs 文件中设置断点
4. F5 → 选择 "KOF98: C# + FFVM Debug"  ← 同时启动 C# 和 FFVM 调试器
5. 游戏窗口打开，C# 和 FFScript 断点均可触发
```

### 场景 E：运行 VM 单元测试

```bash
# 方法 1：使用仓库根目录脚本
run-vm-tests.cmd                                    # Windows
dotnet run --project StandaloneRunner                # 跨平台

# 方法 2：使用 ffvm.cmd 主菜单
ffvm.cmd test
```

---

## 项目结构速查

```
VirtualMachine/
├── src/
│   ├── FFVM/              ← 核心类库（双目标 netstandard2.1 + net8.0）
│   └── FFVM.Cli/          ← CLI 工具（ffvm-cli：run / compile / lsp / dap）
├── Sandbox/               ← 脚本沙盒（交互式测试环境）
├── KOF98/                 ← 格斗游戏实践（FFVM 应用示例）
├── StandaloneRunner/      ← 测试运行器（1259 个断言）
├── Assets/Scripts/VM/     ← VM 核心源码（被 FFVM.csproj 引用）
├── vscode-ffvm-debug/     ← VS Code 调试扩展
├── ffvm.cmd               ← 主命令菜单（sandbox / test / bench）
└── run-vm-tests.cmd       ← 快速测试
```

---

## 常见问题与解决方案

### Q1：`dotnet` 命令找不到

**症状**：`'dotnet' is not recognized` 或 `command not found`

**解决**：
1. 下载并安装 [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
2. 确认安装成功：`dotnet --version`
3. 如果命令仍找不到，重启终端/命令行窗口

---

### Q2：构建失败 — 找不到 .csproj 文件

**症状**：`Could not find project file` 或 `The project file does not exist`

**原因**：`.csproj` 文件被 `.gitignore` 排除（Unity 项目惯例），首次使用需从模板生成。

**解决**：
```bash
# Sandbox
cp Sandbox/Sandbox.csproj.template Sandbox/Sandbox.csproj

# KOF98
cp KOF98/KOF98.csproj.template KOF98/KOF98.csproj

# StandaloneRunner（CI 自动生成，本地需手动）
# 使用 sandbox-init.cmd 会自动处理
```

> **提示**：使用无脑模式的 `sandbox-init.cmd` 或 `kof98-init.cmd` 会自动生成。

---

### Q3：构建失败 — TargetFramework 版本不匹配

**症状**：`The framework 'net10.0' is not supported` 或类似错误

**原因**：部分模板使用 `net10.0`，但本机安装的是 .NET 8.0 SDK。

**解决**：
1. **方案 A**（推荐）：编辑生成的 `.csproj`，将 `<TargetFramework>net10.0</TargetFramework>` 改为 `<TargetFramework>net8.0</TargetFramework>`
2. **方案 B**：安装 [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

> **说明**：FFVM 核心库是双目标（`netstandard2.1;net8.0`），兼容 .NET 8.0+。Sandbox/StandaloneRunner 的模板可能指向更高版本，按需修改即可。

---

### Q4：VS Code 扩展不工作（无语法高亮 / 无补全）

**症状**：`.ffs` 文件无语法高亮，无自动补全

**解决**：
1. 确保已安装 FFVM VS Code 扩展：
   ```bash
   cd vscode-ffvm-debug
   npm install
   npx @vscode/vsce package --no-dependencies -o ffvm-debug.vsix
   code --install-extension ffvm-debug.vsix
   ```
2. 配置 `ffvm.executablePath`（VS Code 设置）：
   - 默认值 `ffvm-cli`（需在 PATH 中）
   - 或指定完整路径，如 `C:\path\to\ffvm-cli.exe`
3. 重启 VS Code

---

### Q5：DAP 调试连接失败

**症状**：VS Code 按 F5 后报错 `Cannot connect to runtime`

**解决**：
- **Launch 模式**（stdio）：确保 `ffvm-cli` 可执行，检查 `.vscode/launch.json` 中的 `program` 路径
- **Attach 模式**（TCP 端口 4711）：确保目标程序已启动 `EmbeddableDapServer`（如 Sandbox 使用 `--debug` 参数）
- 检查端口是否被占用：`netstat -an | findstr 4711`

---

### Q6：Sandbox / KOF98 运行后立刻退出

**症状**：双击 `.cmd` 脚本后窗口一闪而过

**解决**：
1. 改用命令行手动运行，查看错误信息：
   ```cmd
   cd path\to\VirtualMachine
   dotnet run --project Sandbox\Sandbox.csproj
   ```
2. 检查是否缺少 `.csproj` 文件（见 Q2）
3. 检查 .NET SDK 版本（见 Q1、Q3）

---

### Q7：Unity 项目中使用 FFVM

**当前推荐方式**：
1. 将 `Assets/Scripts/VM/` 目录复制到 Unity 项目的 `Assets/` 下
2. 需要 `StandaloneRunner/UnityStub.cs`（非 Unity 环境）或 Unity 自带的 API（Unity 环境自动可用）
3. FFVM 核心代码已零 Unity API 依赖，可直接编译

**NuGet DLL 方式**（实验性）：
1. `dotnet pack src/FFVM/FFVM.csproj`
2. 取 `netstandard2.1` 目标的 DLL 放入 Unity `Assets/Plugins/`

---

### Q8：跨平台注意事项

| 平台 | CLI 构建 | 单文件发布 |
|------|---------|-----------|
| Windows x64 | `dotnet build` | `dotnet publish -r win-x64 src/FFVM.Cli/FFVM.Cli.csproj` |
| Linux x64 | `dotnet build` | `dotnet publish -r linux-x64 src/FFVM.Cli/FFVM.Cli.csproj` |
| macOS arm64 | `dotnet build` | `dotnet publish -r osx-arm64 src/FFVM.Cli/FFVM.Cli.csproj` |

> 单文件发布产出约 35MB 自包含可执行文件，无需目标机器安装 .NET 运行时。

---

## 分发架构概览

```
┌─────────────────────────────────────────────────────────────┐
│ 第 1 层：尝鲜者 — ffvm 单文件可执行                          │
│   下载 ffvm-cli → ffvm-cli run script.ffs                   │
│   内含 Compiler + VM + 运行时 + 内置 Syscall                 │
│   同时提供 ffvm-cli lsp / ffvm-cli dap 子命令                │
├─────────────────────────────────────────────────────────────┤
│ 第 2 层：.NET 集成者 — NuGet 包 / ProjectReference           │
│   dotnet add package FFVM → 3 行 C# 跑通                    │
│   LSP/DAP 工具链：ffvm-cli lsp / ffvm-cli dap               │
├─────────────────────────────────────────────────────────────┤
│ 第 3 层：Unity 用户 — 源码编译 / NuGet DLL                   │
│   Assets/Scripts/VM/ 复制到 Unity 项目                       │
│   双目标库：netstandard2.1（Unity 兼容）+ net8.0（优化）      │
└─────────────────────────────────────────────────────────────┘
```
