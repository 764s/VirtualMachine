# DAP Attach 模式：分发机制的功能缺口

> **状态**：✅ 已完成（DIST-8 + DIST-9 已实现）
> **日期**：2026-04-06
> **来源**：DIST-1~DIST-3 完成后复审分发完整性。发现 launch 模式已随 CLI 分发，但 attach 模式的 DAP 服务器仍为 Sandbox 私有实现，外部消费者无法复用。

---

## 一、动机

### 1.1 两种 DAP 模式的本质区别

| 维度 | Launch 模式 | Attach 模式 |
|------|------------|------------|
| 谁启动 VM | 调试器（VS Code 启动 `ffvm-cli dap`） | 宿主应用（游戏/服务器已在运行） |
| 通信方式 | stdio（子进程） | TCP（网络连接） |
| 线程模型 | 单线程：DAP 服务器驱动 VM | 双线程：宿主主线程 + DAP 线程 |
| 执行控制 | DAP 服务器调用 `_world.Tick()` | 宿主帧循环调用 `Tick()`，DAP 从旁介入 |
| 适用场景 | 独立脚本调试、尝鲜试用 | **嵌入宿主调试（核心场景）** |

### 1.2 核心场景需要 attach

FFVM 的主要用途是**嵌入宿主应用**（Unity 游戏、ECS 框架）。在这一场景下：

```
宿主游戏循环 (C#)
  └─ 每帧: world.Tick()          ← 宿主驱动执行
       ├─ 角色 AI (ffs instance 1)
       ├─ 技能状态机 (ffs instance 2)
       └─ 持续效果器 (ffs instance 3)

VS Code ──TCP──> 宿主内嵌 DAP 服务器   ← attach 模式
```

- **VM 不是独立进程**——它嵌在宿主的帧循环里，launch 模式无法适用。
- **多 VM 实例**——一个宿主可能同时运行数十个 ffs 实例，attach 模式天然适合"连到正在运行的系统"。
- **帧同步约束**——宿主决定何时 Tick，调试器不能抢夺执行控制权。

### 1.3 launch 模式的定位

Launch 模式**并非走偏**，它服务于分发架构第 1 层"尝鲜者"场景（`ffvm-cli dap` + stdio），对独立脚本调试和 CI 自动化测试有价值。但它无法覆盖第 2/3 层（NuGet 集成者 / Unity 用户）的核心需求。

---

## 二、现状诊断

### 2.1 已分发的 DAP 能力

| 组件 | 位置 | 命名空间 | 传输 | 分发状态 | 模式 |
|------|------|----------|------|----------|------|
| `DapServer` | `Assets/Scripts/VM/Debug/` | `FFVM.Debug` | stdio | ✅ 已分发（FFVM.csproj） | launch |
| `EmbeddedDapServer` | `Sandbox/` | `Sandbox` | TCP | ❌ 未分发 | attach |

### 2.2 代码重叠分析

`DapServer` 与 `EmbeddedDapServer` 的 DAP 协议处理器（HandleStackTrace、HandleVariables、HandleScopes、HandleBreakpoints 等）**约 65-70% 相同**，但架构范式完全不同：

- **DapServer**：`Run()` 内部驱动 `_world.Tick()` → 单线程阻塞式
- **EmbeddedDapServer**：通过 `ManualResetEventSlim` 协调主线程/DAP 线程 → 双线程协作式

### 2.3 外部消费者的处境

以"第 2 层 .NET 集成者"为例——`dotnet add package FFVM` 后：

- ✅ 可使用 `DapServer`（stdio）：适合 launch 模式，但不适合嵌入场景
- ❌ 无法使用 `EmbeddedDapServer`：它在 Sandbox 命名空间，不在 NuGet 包中
- ❌ 需自行实现约 774 行的 TCP attach DAP 服务器

---

## 三、功能引入方案

### 3.1 提取 EmbeddableDapServer 到 FFVM 库

将 `EmbeddedDapServer` 的 attach 能力提取为 FFVM 分发库的一等公民：

```
FFVM.csproj (分发库)
  └─ Assets/Scripts/VM/Debug/
       ├─ DapServer.cs               (已有, launch 模式)
       ├─ DapServerBase.cs           (新增, 共享 DAP 协议处理器)
       └─ EmbeddableDapServer.cs     (新增, attach 模式 TCP 服务器)
```

### 3.2 外部消费者使用方式

```csharp
// 宿主初始化
var dap = new EmbeddableDapServer(port: 4711);
dap.StartListening();

// 编译并创建 VM
var program = BytecodeCompiler.Compile(source);
var world = new VMWorld();
world.Modules.Load(0, program);
int id = world.SpawnInstance(0, 0);
dap.AttachToWorld(world, program, id, "script.ffs");

// 宿主帧循环
while (running) {
    world.Tick();
    dap.CheckBreakpointAndWait();   // 断点命中时暂停主线程
}
```

### 3.3 正常 attach 与定制 attach 的区分

| 行为 | 正常 attach | 定制 attach（沙盒模式） |
|------|------------|----------------------|
| 目标状态 | **以目标执行状态为主**——调试器连上时 VM 可能已在运行中 | **等待调试器连接后才开始执行脚本** |
| 初始暂停 | 不暂停（除非用户设置了 stopOnEntry） | 自动 StopOnEntry，等 configurationDone 后才放行 |
| 断点设置时机 | 调试器可随时 setBreakpoints（动态） | 在 StopOnEntry 期间设好所有断点再继续 |
| 适用场景 | 游戏运行中 attach 到某个正在执行的技能脚本 | 开发阶段确保从第一条指令起可调试 |

**当前 Sandbox 的行为属于"定制 attach"**——`WaitForConnection()` + `StopOnEntry()` 是沙盒的特定需求，不是 attach 模式的通用语义。

分发机制需同时支持两种模式。实现方式：

```csharp
// 正常 attach（默认行为）
dap.AttachToWorld(world, program, id, "script.ffs");
// 调试器连接后，VM 继续运行

// 定制 attach（沙盒模式）
dap.AttachToWorld(world, program, id, "script.ffs");
dap.WaitForConnection();     // 阻塞直到调试器连接
dap.StopOnEntry();           // 阻塞直到 configurationDone
// VM 从第一条指令开始可调试
```

关键设计：`WaitForConnection()` 和 `StopOnEntry()` 是**可选调用**，不是 attach 的默认行为。这让宿主可以根据需要选择正常 attach 或定制 attach。

---

## 四、Sandbox 改造

当 `EmbeddableDapServer` 提取到 FFVM 库后，Sandbox 应改为消费分发库的公开 API：

```
改造前：
  Sandbox/EmbeddedDapServer.cs (774 行, Sandbox 命名空间, 私有实现)
  
改造后：
  Sandbox 使用 FFVM.Debug.EmbeddableDapServer (分发库公开 API)
  + 沙盒定制行为 (WaitForConnection + StopOnEntry)
```

改造验证：现有 97 项 DAP 测试 + Sandbox 手动 attach 调试均应保持通过。

---

## 五、展望：attach 模式的天然优势

### 5.1 多实例调试

Attach 模式天然适合多 VM 实例调试：

- 一个 TCP 端口上的 DAP 会话可通过 MI-1（DAP 多实例线程映射）将每个 VM 实例映射为 DAP 伪线程
- 调试器连接到正在运行的宿主后，看到所有活跃实例及其状态
- launch 模式只能调试单个脚本实例

### 5.2 远程调试

Attach 模式的 TCP 传输天然支持远程调试：

- 监听 `0.0.0.0:port` 而非 `127.0.0.1:port` 即可支持远程连接
- VS Code 连接时指定远程 IP + 端口
- DAP 协议层无需任何改动
- 符合 DAP 规范的 attach 语义（连接到远程运行中的目标）

### 5.3 与 DIST 分发层级的对应

| 分发层级 | 主要 DAP 模式 | 说明 |
|---------|-------------|------|
| 第 1 层（尝鲜者） | launch | `ffvm-cli dap` stdio 子进程，独立脚本调试 |
| 第 2 层（.NET 集成者） | **attach** | 宿主进程嵌入 `EmbeddableDapServer`，VS Code TCP 连接 |
| 第 3 层（Unity 用户） | **attach** | Unity 编辑器内嵌 DAP 服务器，VS Code TCP 连接 |

---

## 六、结论

| 问题 | 结论 |
|------|------|
| 分发机制是否走偏？ | **部分走偏**——launch 模式对"尝鲜者"足够，但对核心场景（嵌入宿主）不够 |
| Attach 模式是否更重要？ | **是**——对 FFVM 第 2/3 层用户而言，attach 是唯一可用的调试模式 |
| 是否属于功能缺失？ | **是**——`EmbeddedDapServer` 的能力应从 Sandbox 提取到 FFVM 分发库 |
| 远程调试应走 attach？ | **是**——TCP 传输天然支持远程，符合 DAP 规范的 attach 语义 |
| 多实例调试与 attach 贴合？ | **是**——一个 DAP 会话可服务多个 VM 实例（MI-1 依赖 attach 模式） |
| 需要区分正常/定制 attach？ | **是**——正常 attach 不阻塞执行；定制 attach（沙盒模式）WaitForConnection + StopOnEntry |

**行动项**：新增 DIST-8（提取 EmbeddableDapServer）+ DIST-9（Sandbox 改造），纳入 D 阶段串行计划。
