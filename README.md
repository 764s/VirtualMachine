# FFVM — 帧同步友好的轻量脚本虚拟机

FFVM 是一个**寄存器式字节码虚拟机**，为少量单位的帧同步场景提供有限但够用的脚本能力。
配套脚本语言 **FFScript**（`.ffs`）语法类似 Go，内置帧级挂起（`wait` / `yield`）和自动清理（`defer` / `using`），VM 状态可整体快照 / 回滚，将来会提供 UI 可视化编辑方式。

```ffs
func main() {
    BeginAction(114, 56)
    defer { EndAction() }

    var f: int = 0
    while f < 56 {
        if f == 9 {
            ApplyDamage(target, 1.0, 0)
        }
        wait 1          // 暂停 1 帧，下一帧继续
        f = f + 1
    }
}
```

---

## 这个项目想做什么

为少量单位的帧同步游戏提供一个轻量脚本方案：

- 脚本可以 `wait` 若干帧后继续执行，不依赖宿主协程
- VM 全部运行状态为定长值类型，`Array.Copy` 即可完成快照 / 回滚
- 性能过得去（寄存器式字节码，多轮优化后接近 Lua 量级）
- 将来会提供 UI 可视化编辑方式

| 约束 | 说明 |
|------|------|
| **零 GC** | 运行时零垃圾回收 |
| **memcpy 快照 / 回滚** | 定长值类型状态，帧同步友好 |
| **`wait` 一等语义** | 脚本挂起 / 恢复由 VM 管理 |
| **ROM / RAM 分离** | 字节码只读共享，实例状态独立 |
| **Fix64 确定性数值** | 开发期 float，发布期 Fix64 |

---

## 当前状态

**引擎侧已基本成熟**，正在等待宿主游戏 ECS 接入（C 阶段）。

- ✅ 完整编译器流水线：FFScript 源码 → Lexer → Parser → AST → BytecodeCompiler → 字节码执行
- ✅ 1111 项自动化测试全部通过（编译器 610 + TreeWalker 112 + 性能 44 + FFScript 18 + 调试 51 + DAP 97 + LSP 179）
- ✅ 曳光弹全部验证门禁通过（零 GC、回滚 bit-exact、单实例 3.8x vs C#、128 实例 < 0.4ms）
- ✅ DAP 调试器 + VS Code 扩展（断点、单步、变量查看）
- ✅ LSP 语言服务（实时诊断、符号分析、代码补全、参数提示、Syscall 声明）
- ✅ 分发基础设施（NuGet 包 + 单文件 CLI + Sandbox 沙盒环境）
- ✅ 多轮性能优化（Peephole、FORLOOP 超级指令、指令压缩 16B→4B、LICM、跳转表等）

**未完成**：宿主集成（C1）、帧内 Profiler 验证（C2）、帧同步集成验证（C5）、UI 可视化编辑（C6）。

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
| `float` | 浮点数（开发期为 double，发布期切换为 Fix64 定点数以确保帧同步确定性） |
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
```

### 17 个关键字

`func` `var` `const` `struct` `if` `else` `while` `for` `return` `wait` `wait_for` `yield` `defer` `using` `include` `true` `false`

---

## 性能

### VM vs C# 对比（同数据类型 `Number` struct）

| 基准 | VM (μs) | C# (μs) | 倍率 | 循环规模 |
|------|---------|---------|------|---------|
| B01 ArithLoop | 1189 | 116 | 10.2x | 10,000 |
| B02 Fibonacci | 5.3 | 1.1 | 4.7x | 46 |
| B03 NestedLoop | 992 | 19 | 53.6x | 100×100 |
| B05 Accumulator | 2807 | 85 | 33.1x | 50,000 |
| B06 FuncCall | 681 | 19 | 35.5x | 5,000 |

### 跨语言对比（vs C# `Number` 基线 = 1.00x）

| 基准 | C# raw | FFVM | Lua 5.1 | Node.js | Python 3.7 |
|------|--------|------|---------|---------|------------|
| B01 ArithLoop | 0.51x | 13.4x | 9.1x | 0.44x | 31.7x |
| B03 NestedLoop | 1.42x | 37.0x | 22.5x | 1.31x | 235.3x |
| B05 Accumulator | 1.08x | 26.8x | 11.9x | 1.01x | 158.5x |

> **两张表基线不同**：第一张表基线是 C#（使用 `Number` struct），第二张表同样以 C# `Number` 为 1.00x 基线但使用了优化后的跨语言基准测试环境，因此同一基准的倍率有所差异。
>
> FFVM 与 Lua（PUC-Rio）是最接近的架构对等物——都是寄存器式解释器 + 统一 double 数值类型。
> 当前差距主要来自 C# 托管开销（边界检查、方法调用）和安全机制（寄存器窗口、Cleanup 链、调试钩子）。指令编码已从 16B 压缩至 4B，与 Lua 持平。
> 详细基准数据见 [benchmarks/](benchmarks/)。

### 关键性能指标

- **零 GC**：100 Tick 0 bytes 分配（V1 验证通过）
- **快照回滚**：Syscall 序列 bit-exact 一致（V2 验证通过）
- **128 实例吞吐**：0.391ms（V4 验证通过，帧预算充裕）

---

## 目标路线图

```
  ✅ 曳光弹验证                 — 执行模型可行性证明
  ✅ 编译器 + 工具链             — 源码到调试的全链路
  ✅ 性能优化 + 功能完善          — 追平 Lua 级别性能
  ✅ 分发基础设施               — NuGet + CLI + VS Code 扩展
  ⚪ 宿主集成                   — 真实 Syscall 接入（等待宿主就绪）
  ⚪ 帧同步集成验证              — 网络环境下的快照 / 回滚正确性
  ⚪ UI 可视化编辑               — 结构化流程图编辑方式
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

---

## 文档

| 文档 | 说明 |
|------|------|
| [Docs/VM_Summary.md](Docs/VM_Summary.md) | 项目总览（唯一入口文档） |
| [Docs/Reference/FFS_Syntax.md](Docs/Reference/FFS_Syntax.md) | FFScript 完整语法参考 |
| [Docs/Reference/FFS_QuickRef.md](Docs/Reference/FFS_QuickRef.md) | FFScript 快速上手（C# 开发者版） |
| [Docs/DIST_Usage.md](Docs/DIST_Usage.md) | 分发使用说明 |
| [Docs/Reference/VM_Architecture_Rules.md](Docs/Reference/VM_Architecture_Rules.md) | 架构硬约束（20 条纪律） |
| [Docs/Reference/Skills/](Docs/Reference/Skills/) | 技能脚本示例（飞燕旋风腿等） |
