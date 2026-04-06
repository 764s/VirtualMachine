# 游戏内运行时动态编译

> **状态**：💬 讨论中
> **日期**：2026-04-05
> **来源**：关于当前 DIST 分发架构能否支持游戏内动态编译脚本的需求讨论。

---

## 一、需求描述

游戏运行过程中，需要动态编译 FFScript 脚本并执行，例如：
- 编辑器模式下修改 `.ffs` 脚本后热重载
- 运行时从文件系统 / 网络加载新脚本
- 手动构建实时编译（程序化生成脚本文本后即时编译执行）

核心问题：**当前 DIST 三层分发架构能否顾及这一需求？**

---

## 二、结论：完全支持

当前架构天然支持运行时动态编译，无需额外改造。

### 2.1 技术依据

| 条件 | 现状 |
|------|------|
| 编译器可用性 | `BytecodeCompiler` 已在 DIST-1 public API 列表中 |
| 运行时依赖 | 编译器零 Unity 依赖，纯 C# 实现 |
| 分发层级覆盖 | 三层（CLI / NuGet / UPM）均包含完整编译器 |
| 确定性保证 | 编译器使用 Fix64 定点数学，运行时编译不破坏帧同步确定性 |
| ROM 复用 | 编译产物 `VMProgram` 是不可变 ROM，可跨多个 VM 实例复用 |

### 2.2 运行时使用模式

```csharp
// 1. 编译源码 → 获得不可变 ROM
string sourceText = LoadFromFileOrNetwork("skill_new.ffs");
VMProgram program = BytecodeCompiler.Compile(sourceText);

// 2. 加载到 VM 世界并执行
world.LoadProgram(program);
world.Tick();

// 3. 同一个 program 可复用于多个实例
world2.LoadProgram(program);  // 不需要重新编译
```

### 2.3 各场景适配

| 场景 | 做法 | 注意事项 |
|------|------|---------|
| **编辑器热重载** | 文件变更 → 重新 Compile → 替换 VMProgram | 需处理运行中实例的安全替换（等待 yield 点） |
| **网络加载** | 下载 .ffs 文本 → Compile → LoadProgram | 考虑编译错误处理（CompileResult 包含诊断信息） |
| **程序化生成** | 拼接字符串 → Compile → 执行 | 确保生成的代码符合 FFScript 语法 |
| **预编译缓存** | 首次 Compile → 缓存 VMProgram → 后续直接复用 | VMProgram 是值类型快照，可安全缓存 |

---

## 三、无需改造的原因

DIST 架构的核心设计决策：**将编译器纳入库的 public API**，而非仅作为 CLI 工具的内部实现。
这意味着消费者（无论是 .NET 项目还是 Unity 项目）都能在运行时调用编译器，
不需要通过进程间通信或外部工具链。

引用 DIST-1 的 public API 清单：
> 公有 API 仅暴露 `VMWorld`、`VMProgram`、`BytecodeCompiler`、`SyscallTable`、`SyscallArgs`、`Number`。

`BytecodeCompiler` 赫然在列，动态编译是一等公民。

---

## 四、潜在扩展（非必须）

以下为未来可能考虑的增强项，当前不阻塞：

| ID | 方向 | 说明 |
|----|------|------|
| RC-1 | 二进制序列化 VMProgram | 将编译产物缓存为二进制文件（.ffb），避免重复解析/编译。适用于发布包预编译。 |
| RC-2 | 增量编译 | 仅重新编译变更的函数/脚本，减少热重载延迟。当前编译速度足够快，暂不需要。 |
| RC-3 | 安全沙箱评估 | 动态加载外部脚本时的安全考量（Syscall 白名单、执行步数限制）。已有 maxSteps 机制。 |
