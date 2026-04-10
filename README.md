# FFVM — 帧同步友好的轻量脚本虚拟机（探索中）

> ⚠️ 这是一个**探索向项目**，目前所有代码由 AI 编写，尚未在实际产品中验证。

FFVM 是一个寄存器式字节码虚拟机，配套胶水 DSL **FFScript**（`.ffs`）。

**特点**：零 GC · ROM / RAM 分离 · VM 层快照导入导出 · 确定性执行 · 原生 `wait` / `yield` 帧控制

---

## 这个项目想做什么

为少量单位的帧同步游戏提供一个有限但够用的脚本方案，性能目标追平 Lua。

- **胶水 DSL，不是通用脚本**——平行于行为树编辑器，负责编排宿主提供的 Syscall，大部分逻辑仍在宿主层
- **ROM / RAM 分离**——字节码只读共享，实例状态为定长值类型，VM 层面即可快照导入导出
- **紧急出口**——在限定范围内可以用脚本实现少量细节，也可为提前框定范围的宿主功能提供热更覆盖
- **两种配置方式**——当前以脚本作为配置入口，将来同时支持传统 UI 编辑方式

---

## 当前状态

**VM 侧已基本成熟**，正在等待宿主接入（C 阶段）。

- ✅ 完整编译器流水线：FFScript 源码 → Lexer → Parser → AST → BytecodeCompiler → 字节码执行
- ✅ 1310 项自动化测试全部通过（编译器 809 + TreeWalker 112 + 性能 44 + FFScript 18 + 调试 51 + DAP 97 + LSP 179）
- ✅ 曳光弹全部验证门禁通过（零 GC、回滚 bit-exact、单实例 3.8x vs C#、128 实例 < 0.4ms）
- ✅ DAP 调试器 + VS Code 扩展（断点、单步、变量查看）
- ✅ LSP 语言服务（实时诊断、符号分析、代码补全、参数提示、Syscall 声明、编译器警告）
- ✅ 分发基础设施（NuGet 包 + 单文件 CLI + Sandbox 沙盒环境）
- ✅ 多轮性能优化（Peephole、FORLOOP 超级指令、指令压缩 16B→4B、LICM、跳转表等）
- ✅ 跨实例调用（@export + XCALL + 统一语法 svc.member + 自动 getter/setter 退化 + @inline 提示）

**未完成**：宿主集成 + 热更覆盖（C1）、帧内 Profiler 验证（C2）、帧同步集成验证（C5）、UI 配置编辑（C6）。

---

## 快速开始

> 前提：已安装 [.NET 8.0+ SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

### 沙盒模式（推荐新手）

```bash
# Windows
Sandbox\sandbox-init.cmd          # 一键初始化
Sandbox\run-sandbox.cmd           # 启动沙盒，输入 R 运行脚本

# macOS / Linux
bash Sandbox/sandbox-init.sh
bash Sandbox/run-sandbox.sh
```

编辑 `Sandbox/scripts/main.ffs`，按 `R` 即可重新编译运行。

### CLI 模式

```bash
dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Release
dotnet run --project src/FFVM.Cli -- run path/to/script.ffs
```

### 作为 NuGet 包引用

```bash
dotnet build src/FFVM/FFVM.csproj -c Release
dotnet pack src/FFVM/FFVM.csproj -c Release
# 生成 FFVM.0.1.0.nupkg，支持 netstandard2.1 + net8.0
```

详细使用说明见 [Docs/DIST_Usage.md](Docs/DIST_Usage.md)。

---

## FFScript 语法速览

FFScript 语法类似简化版 Go，完整语法参考见 [Docs/Reference/FFS_Syntax.md](Docs/Reference/FFS_Syntax.md)，快速上手见 [Docs/Reference/FFS_QuickRef.md](Docs/Reference/FFS_QuickRef.md)。

### 类型

| 类型 | 说明 |
|------|------|
| `int` | 32 位整数 |
| `float` | 浮点数（以定点数方式执行，确保确定性） |
| `bool` | `true` / `false` |
| `string` | 编译期常量字符串 |
| `struct` | 值类型结构体（赋值即复制，编译期拍平为连续寄存器） |

没有数组、字典、类、泛型、闭包、`null`。需要批量数据处理时通过 Syscall 委托宿主。

### 核心语法示例

```ffs
// 变量与常量
const MAX_HP: int = 100
var currentHP: int = MAX_HP

// 结构体
struct Vec2 { x: int; y: int }

// 函数（支持可选参数）
func hit(target: int, coeff: float = 1.0) {
    ApplyDamage(target, coeff)
}

// 控制流
if currentHP < 50 {
    Heal(20)
} else {
    Attack()
}

for var i: int = 0; i < 10; i = i + 1 {
    print(i)
}

// 帧控制
wait 5        // 暂停 5 帧
yield          // 暂停 1 帧

// 自动清理
using PlayEffect(vfxId) {
    wait 30   // 30 帧后自动调用配对的 StopEffect
}

defer { EndAction() }   // 函数退出或被 Kill 时自动执行

// 文件包含
include "common/math.ffs"

// 跨实例调用（@export + 服务绑定）
@export var hp: int = 100
@export func get_hp(): int { return hp }

// 调用方通过 svc.member 统一语法访问
var result: int = svc.get_hp()
var currentHP: int = svc.hp
svc.hp = 50
```

### 17 个关键字 + 2 个注解

`func` `var` `const` `struct` `if` `else` `while` `for` `return` `wait` `wait_for` `yield` `defer` `using` `include` `true` `false`

**注解**：`@export` `@inline`

---

## 目标路线图

```
  ✅ 曳光弹验证                 — 执行模型可行性证明
  ✅ 编译器 + 工具链             — 源码到调试的全链路
  ✅ 性能优化 + 功能完善          — 追平 Lua 级别性能
  ✅ 分发基础设施               — NuGet + CLI + VS Code 扩展
  ✅ 跨实例调用                 — @export + XCALL + svc.member 统一语法
  ⚪ 宿主集成                   — Syscall 接入 + 热更覆盖机制
  ⚪ 帧同步集成验证              — 网络环境下的快照导入导出正确性
  ⚪ UI 配置编辑                 — 传统 UI 方式作为脚本配置的替代入口
```

---

## 项目结构

```
src/FFVM/                   FFVM 核心类库（NuGet 包源码）
src/FFVM.Cli/               CLI 工具（ffvm-cli run/compile/lsp/dap）
Assets/Scripts/VM/           VM 实现源码（Core/Compiler/AST/Debug/Tests）
Sandbox/                     沙盒环境（零配置体验 FFScript）
StandaloneRunner/            独立测试运行器
vscode-ffvm-debug/           VS Code 调试 & 语法高亮扩展
benchmarks/                  跨语言性能基准测试
Docs/                        设计文档（VM_Summary.md 为入口）
```

---

## 运行测试

```bash
# 方法 1：命令行
dotnet run --project StandaloneRunner

# 方法 2：VSCode 菜单
# Terminal → Run Task → Run VM Tests

# 方法 3：脚本
run-vm-tests.cmd              # Windows
```
