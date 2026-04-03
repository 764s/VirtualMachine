# FFScript Sandbox

独立于 Unity 的 FFScript 脚本测试环境。用于验证脚本功能完整性和性能。

## 一键初始化（推荐）

首次检出仓库后，只需两步即可运行沙盒：

**Windows:**
1. 双击 `Sandbox\sandbox-init.cmd`（自动完成全部初始化）
2. 双击新生成的 `run-sandbox.cmd`，输入 `R` 回车即可编译运行

**macOS / Linux:**
1. 在仓库根目录执行 `bash Sandbox/sandbox-init.sh`
2. 执行 `./run-sandbox.sh`，输入 `R` 回车即可编译运行

初始化脚本会自动完成以下工作：
- 从模板生成 `Sandbox.csproj`
- 编译 Sandbox 项目（Release）
- 生成 `run-sandbox` 可执行脚本
- 配置 `.vscode/` 目录（launch.json + tasks.json）
- 构建 StandaloneRunner（DAP/LSP 调试服务器）
- 安装 VS Code 调试插件（需要 npm + code CLI，可选）

---

## 手动初始化

如果一键初始化不适用，可手动完成：

### 1. 生成项目文件

Sandbox 的 `.csproj` 文件是 gitignore 的（Unity 项目惯例），首次使用需手动创建：

**Windows:**
```cmd
cd Sandbox
copy Sandbox.csproj.template Sandbox.csproj
```

**macOS / Linux:**
```bash
cd Sandbox
cp Sandbox.csproj.template Sandbox.csproj
```

### 2. 编译运行

```bash
# 交互模式（推荐）
dotnet run --project Sandbox/Sandbox.csproj

# 命令行模式
dotnet run --project Sandbox/Sandbox.csproj -- --compile   # 仅编译
dotnet run --project Sandbox/Sandbox.csproj -- --run       # 编译并运行
```

### 3. 编写脚本

编辑 `Sandbox/scripts/main.ffs`，或在 `sandbox.json` 中修改入口脚本路径：

```json
{
    "entryScript": "scripts/main.ffs",
    "entryFunction": "main"
}
```

---

## 断点调试（VSCode）

### 前置条件

1. 安装 VSCode 扩展：打开 `vscode-ffvm-debug/` 目录，按 F5 或手动安装
2. 确保已构建 StandaloneRunner：
   ```bash
   dotnet build StandaloneRunner/StandaloneRunner.csproj -c Release
   ```

### 调试步骤

1. 在 VSCode 中打开**仓库根目录**（不是 Sandbox 子目录）
2. 打开 `Sandbox/scripts/main.ffs`
3. 在需要的行上点击行号左侧，设置断点（红色圆点）
4. 按 `F5` 或打开 Run and Debug 面板
5. 选择 **"Debug FFScript (Sandbox)"** 配置
6. 调试器将启动并在断点处暂停
7. 使用调试工具栏：
   - ▶ Continue (`F5`)
   - ⏭ Step Over (`F10`)
   - ⏬ Step Into (`F11`)
   - ⏫ Step Out (`Shift+F11`)
8. 在 Variables 面板查看局部变量值

> **提示**：确保 `.vscode/launch.json` 中配置的 `program` 路径指向你要调试的 `.ffs` 文件。
> 如果使用了 Sandbox 的 launch.json，需要将其复制到仓库根目录的 `.vscode/launch.json`。

### 将调试配置添加到根目录

如果仓库根目录已有 `.vscode/launch.json`，将以下配置添加到 `configurations` 数组中：

```json
{
    "name": "Debug FFScript (Sandbox)",
    "type": "ffvm",
    "request": "launch",
    "program": "${workspaceFolder}/Sandbox/scripts/main.ffs"
}
```

---

## SysCall 参考

| Slot | 名称 | 签名 | 功能 |
|------|------|------|------|
| 0 | `print` | `print(value)` | 打印数值到控制台 |
| 1 | `print_str` | `print_str(labelId, value)` | 打印带标签的数值 |
| 2 | `time` | `time() → int` | 返回运行开始后的毫秒数 |
| 3 | `delta_time` | `delta_time() → int` | 返回上一 Tick 到当前的毫秒差 |
| 4 | `random` | `random(upperBound) → int` | 返回 [0, upperBound) 的随机整数 |
| 5 | `abs` | `abs(value) → number` | 绝对值 |
| 6 | `min` | `min(a, b) → number` | 较小值 |
| 7 | `max` | `max(a, b) → number` | 较大值 |
| 8 | `clamp` | `clamp(value, lo, hi) → number` | 限制范围 |
| 9 | `sqrt` | `sqrt(value) → number` | 平方根（近似） |
| 10 | `frame_count` | `frame_count() → int` | 当前帧号 |
| 11 | `exit` | `exit()` | 请求退出运行循环 |

### print_str 标签表

| labelId | 标签 |
|---------|------|
| 0 | result |
| 1 | value |
| 2 | count |
| 3 | time |
| 4 | delta |
| 5 | frame |
| 6 | error |
| 7 | debug |
| 8 | x |
| 9 | y |
| 10 | sum |
| 11 | diff |
| 12 | product |
| 13 | quotient |
| 14 | min |
| 15 | max |

### LSP 支持

`sandbox.ffvm.d.json` 提供了所有 SysCall 的签名声明，VSCode 的 FFVM 语言服务会自动加载此文件，提供：
- SysCall 名称补全
- 参数签名提示（输入 `(` 或 `,` 时弹出）
- 悬停显示签名和说明

---

## 长期运行模式

默认情况下，如果脚本使用了 `wait` 语句，Sandbox 会以 60fps 的帧率持续运行，每帧调用一次 `Tick()`。

示例（持续运行脚本）：

```ffs
func main() {
    var i: int = 0
    while i < 300 {
        var f: int = frame_count()
        print_str(5, f)

        var t: int = time()
        print_str(3, t)

        wait 1
        i = i + 1
    }
    exit()
}
```

- `wait 1` 使脚本在每帧暂停一次，下一帧继续
- `exit()` 在循环结束后请求退出
- 按 `Ctrl+C` 可随时中断

---

## 目录结构

```
Sandbox/
├── sandbox.json              # 配置文件（入口脚本、入口函数）
├── Sandbox.csproj.template   # .csproj 模板（copy → Sandbox.csproj）
├── Program.cs                # 可执行程序入口
├── SandboxSyscalls.cs        # 预定义 SysCall 实现
├── SandboxRunner.cs          # 编译+运行引擎
├── sandbox.ffvm.d.json       # SysCall 声明文件（LSP 补全用）
├── scripts/                  # FFScript 脚本区
│   └── main.ffs              # 示例入口脚本
├── .vscode/                  # VSCode 调试配置
│   ├── launch.json           # DAP 调试配置
│   └── tasks.json            # 构建任务
└── README.md                 # 本文档
```

## 已知限制

1. **仅数值类型** — FFScript 只支持 `Number` 类型（整数或定点数），没有字符串。`print_str` 通过标签 ID 映射打印文本。
2. **单文件编译** — 当前编译器为单文件模式，不支持 `#include` 或多模块导入。
3. **无热重载** — 修改脚本后需手动重新编译并运行。
4. **无文件 I/O** — 沙盒聚焦于纯逻辑和性能验证，不提供文件读写 SysCall。
5. **DAP 调试不含 Sandbox SysCall** — DAP 模式下脚本中的 SysCall 调用会因为未注册而报错。调试纯逻辑脚本（不含 SysCall）时无此限制。
