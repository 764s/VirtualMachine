# DIST-8 + DIST-9: EmbeddableDapServer 提取 + Sandbox 改造

> **状态**：✅ 已完成
> **日期**：2026-04-06
> **前置条件**：DIST-1（独立 FFVM 类库）

---

## 一、设计决策

### 1.1 继承层次

```
DapServerBase (abstract)          ← 共享 DAP 协议处理器
  ├─ DapServer (launch mode)      ← 单线程 stdio，编译+执行
  └─ EmbeddableDapServer (attach) ← 多线程 TCP，嵌入宿主
```

### 1.2 基类提供的共享处理器

| 方法 | 说明 |
|------|------|
| `HandleInitialize(bool supportsEval)` | 返回 DAP capabilities |
| `HandleThreads()` | 单线程 FFVM 报告 |
| `HandleStackTrace()` | 调用栈帧 + 源码映射 |
| `HandleScopes()` | Locals 作用域 |
| `HandleVariables(args)` | 局部变量 + 结构体展开 |
| `HandleEvaluate(args)` | hover 表达式求值 |
| `HandleSetBreakpointsCore(args)` | 断点验证 + 源码映射 |
| `SendResponse() / SendEvent()` | DAP 消息发送（委托 WriteMessage） |
| `FormatNumber()` | Number → 字符串格式化 |
| `OnBreakpointHitCallback()` | 断点命中回调 |

### 1.3 子类差异化扩展点

| 扩展点 | DapServer | EmbeddableDapServer |
|--------|-----------|---------------------|
| `WriteMessage()` | 直接写 stdout | 带 null 检查 + lock 写 TCP stream |
| `OnBreakpointNotVerifiable()` | 返回 false（不缓冲） | 缓冲到 `_bufferedBreakpointLines`，返回 true |
| `OnClearBufferedBreakpoints()` | no-op | 清空缓冲列表 |
| 执行控制 | `RunUntilBreakpoint()` 单线程循环 | `ResumeExecution()` 信号主线程 |

---

## 二、实施清单

- [x] DIST-8.1: 创建 `DapServerBase.cs` 抽象基类
- [x] DIST-8.2: 重构 `DapServer.cs` 继承基类（删除 ~200 行重复代码）
- [x] DIST-8.3: 创建 `EmbeddableDapServer.cs` 在 `FFVM.Debug` 命名空间
- [x] DIST-8.4: 验证 1007 项 Assert 全通过
- [x] DIST-9.1: 删除 `Sandbox/EmbeddedDapServer.cs`（774 行）
- [x] DIST-9.2: `SandboxRunner.cs` 改用 `FFVM.Debug.EmbeddableDapServer`
- [x] DIST-9.3: Sandbox 构建验证（0 错误 0 警告）
- [x] DIST-9.4: 1007 项 Assert 全通过

---

## 三、关键产出

| 指标 | 值 |
|------|-----|
| 新增文件 | `DapServerBase.cs`（321 行）、`EmbeddableDapServer.cs`（476 行） |
| 删除文件 | `Sandbox/EmbeddedDapServer.cs`（774 行） |
| 重构文件 | `DapServer.cs`（578→290 行）、`SandboxRunner.cs`（+1 行 using） |
| 净代码变化 | +490 / -631 = -141 行 |
| 测试 | 1007 Assert 全通过（97 DAP 测试无变化） |
| 公开 API | `FFVM.Debug.EmbeddableDapServer`（NuGet 可用） |

---

## 四、外部消费者使用方式

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

// 正常 attach（默认行为，不阻塞）
// 定制 attach（开发模式）：
//   dap.WaitForConnection();
//   dap.StopOnEntry();

// 宿主帧循环
while (running) {
    world.Tick();
    dap.CheckBreakpointAndWait();
}

dap.DetachFromWorld();
dap.Dispose();
```
