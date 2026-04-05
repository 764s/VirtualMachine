# C0 实战部署架构：VM 分配策略与多实例交互

> **定位**：本文档固化 C 阶段宿主集成前的架构决策，涵盖 VM 实例分配策略、
> 多实例调试、数据读取方案、多 VM 交互协议。所有方案均基于当前 VM 能力评估，
> 标注了需要新增的能力（MI 系列）。
>
> **前置**：B 阶段全部完成（1007 Assert × 2 模式），引擎侧已就绪。
>
> **来源**：VM_Summary.md 串行计划 C 阶段讨论。

---

## 一、VM 实例分配策略

### 1.1 推荐层级映射

```
场景/关卡流程  →  宿主 C# （不需要 ffvm）
     │
角色 AI        →  1 个 ffs 脚本 / 角色  （✅ ffvm 实例）
     │
技能（状态机） →  1 个 ffs 脚本 / 活跃技能  （✅ ffvm 实例）
     │              含子技能逻辑内联
     │
效果器         →  分级处理：
     ├─ 同步效果器  →  Syscall 内联到技能脚本
     ├─ 持续效果器  →  独立 ffs 实例（子弹、DOT 等）
     └─ 表现效果器  →  宿主 C#（不参与回滚）
```

### 1.2 分配平衡点：稳赚 / 模糊 / 稳亏

#### 每实例固定成本

| 开销维度 | 每实例 | 来源 |
|----------|--------|------|
| 内存（活跃） | ~840 B | 64×8B registers + 16×16B callstack + 8×4B cleanup + ~40B control |
| 内存（快照环 8帧） | ~6.7 KB | 840B × 8 |
| Tick — wait 状态 | ~5-10 ns | `WaitCounter-- + continue` |
| Tick — 活跃执行 | ~3 µs / 50条指令 | V4 实测推算 |
| Spawn/Destroy | ~0.1 µs | 数组赋值 + ActiveList append/swap-remove |

#### 判定边界

| 区间 | 脚本逻辑特征 | 判定 | 理由 |
|------|-------------|------|------|
| **稳赚** | ≥10 行、含 `wait`/多帧生命周期/分支/循环 | ✅ 独立 VM 实例 | wait 一等语义 + 快照回滚 + 断点调试 — 在宿主 C# 中等价实现成本远高于 840B 实例 |
| **稳赚** | 需要 Kill → Cleanup 保证 | ✅ 独立 VM 实例 | `defer`/`using` 保证异常清理路径，宿主手写极易遗漏 |
| **稳赚** | 需要帧同步快照/回滚 | ✅ 独立 VM 实例 | memcpy 回滚免费（O10: 只拷贝活跃实例） |
| **模糊** | 5-10 行、单次触发、无 wait | ⚠️ 视情况 | 逻辑足够简单可用 Syscall 链内联；但若需独立调试或复用，仍值得 |
| **稳亏** | ≤3 行、纯"条件→调用"、无生命周期 | ❌ Syscall 内联 | 如 `if (frame==9) { ApplyDamage(...) }` 直接写在父技能 `if` 分支中 |

#### 场景规模估算

| 场景 | 实例数 | 活跃内存 | 快照 8帧 | 每帧 Tick |
|------|--------|---------|---------|----------|
| 轻量：5 角色 × (1AI + 1技能) | 10 | 8 KB | 67 KB | ~0.03 ms |
| 典型：10 角色 × (1AI + 1技能) + 20 持续效果器 | 40 | 33 KB | 262 KB | ~0.1 ms |
| 密集：20 角色 × (1AI + 1技能 + 3效果器) | 100 | 82 KB | 655 KB | ~0.25 ms |
| 极端：128（池上限） | 128 | 105 KB | 840 KB | ~0.4 ms |

> 帧预算 16.6ms（60fps），即使满载 128 实例也仅占 **2.4%**。
> **结论**：性能不是分配 VM 的瓶颈。决策因素是语义收益 vs 管理复杂度。

---

## 二、多 VM 调试与语言服务

### 2.1 语言服务器（LSP）：仅需 1 个实例

LSP 服务的是**源文件**，不是运行时实例。10 个角色跑同一个 `ai_melee.ffs`，LSP 只需打开 1 个文件。

```
LSP Server (1 个进程)
  ├─ ai_melee.ffs        → 诊断 + 补全 + 符号
  ├─ skill_114.ffs        → 诊断 + 补全 + 符号
  └─ effect_dot.ffs       → 诊断 + 补全 + 符号
```

**无需改动**，当前 LSP 实现已满足。

### 2.2 调试器（DAP）：需补充多实例选择能力

当前 `ScriptDebugger` 状态：
- `_breakpointLines` 全局共享 — 对所有实例生效 ✅
- `HaltOnBreakpoint` 命中后 `Tick()` 返回 — 全局冻结 ✅（帧同步正确行为）
- `GetVariables`/`GetCallStack` 接受 `ref VMInstanceState` — 支持按实例查看 ✅

缺失能力：

| 需求 | 当前状态 | 需要补充 |
|------|---------|---------|
| 查看**命中实例**的变量/调用栈 | `GetVariables` 支持但 DAP 层未记录 instanceId | **MI-1**：记录 `HitInstanceId` |
| 多个实例间切换查看 | 未实现 | **MI-1**：DAP threads 映射 |
| 按实例过滤断点 | 未实现 | 可选：条件断点 `instanceId == X` |

#### MI-1 实施方案：DAP 多实例支持

**ScriptDebugger 新增**（~20 行）：

```csharp
// 记录最近命中断点的实例 ID
public int HitInstanceId { get; private set; } = -1;

// CheckBreakpoint 中命中时记录
HitInstanceId = instanceId;

// DAP threads 请求 → 返回所有活跃实例作为 "threads"
public List<(int instanceId, string label)> GetActiveInstances(VMWorld world)
{
    var result = new List<(int, string)>();
    for (int i = 0; i < world.Pool.ActiveListCount; i++)
    {
        int id = world.Pool.ActiveList[i];
        ref var inst = ref world.Pool.Instances[id];
        if (!inst.IsAlive) continue;
        var program = world.Modules.Get(inst.ModuleSlot);
        string name = program?.Name ?? $"instance_{id}";
        result.Add((id, $"{name} [#{id}]"));
    }
    return result;
}
```

**DapServer 扩展**（~40 行）：
- `threads` → 每个 VM 实例映射为 DAP Thread（`threadId = instanceId`）
- `stackTrace` → 带 `threadId` 查对应实例
- `variables` → `frameId` 编码 `instanceId + stackFrameIndex`

**改动量**：~60 行。不影响 VM 核心执行引擎。

---

## 三、从 VM 获取内部数据

### 3.1 方案矩阵

| 方案 | 路径 | 现状 | 改动 |
|------|------|------|------|
| **A. Syscall 主动推送** | ffs 脚本 → Syscall → 宿主 Blackboard | ✅ 已支持 | 零 |
| **B. 宿主直接读寄存器** | 宿主 → `inst.Registers.Get(N)` | ✅ 已支持 | 零 |
| **C. 按名查询封装** | 宿主 → `VMInspector.ReadVar(world, id, "f")` | ❌ | MI-4 |

### 3.2 方案 A：Syscall 主动推送（推荐主路径）

```ffs
// 脚本端：主动上报状态到宿主 Blackboard
SetBlackboard(self, HP_CURRENT, hp)
SetBlackboard(self, SKILL_PHASE, phase)
```

```csharp
// 宿主端：Syscall handler
syscalls.Register(10, "SetBlackboard", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    int entityId = args.GetInt(0);
    int key = args.GetInt(1);
    Number value = args.GetNumber(2);
    blackboard[entityId][key] = value;
});
```

**适用**：运行时数据流（AI 决策、技能帧、效果器状态）。数据由脚本显式推送，流向清晰。

### 3.3 方案 B：宿主直接读寄存器（适合编辑器/Inspector）

```csharp
ref VMInstanceState inst = ref world.Pool.Instances[instanceId];

// 已知 r16 = var f（可通过 SymbolTable 查找）
Number currentFrame = inst.Registers.Get(16);

// 或通过调试 API
var vars = debugger.GetVariables(program, ref inst);
```

**适用**：调试、编辑器预览。利用已有 `SymbolTable` 做名称解析。

### 3.4 MI-4：VMInspector 便捷查询（~30 行 helper）

```csharp
public static class VMInspector
{
    /// <summary>按变量名读取指定实例的当前变量值。</summary>
    public static Number ReadVar(VMWorld world, int instanceId, string varName)
    {
        ref var inst = ref world.Pool.Instances[instanceId];
        var program = world.Modules.Get(inst.ModuleSlot);
        if (program?.SymbolTable == null) return Number.Zero;

        for (int i = 0; i < program.SymbolTable.Length; i++)
        {
            ref readonly var sym = ref program.SymbolTable[i];
            if (sym.Name == varName)
            {
                int physReg = sym.Register < 16
                    ? sym.Register
                    : sym.Register + inst.RegisterBase;
                return inst.Registers.Get(physReg);
            }
        }
        return Number.Zero;
    }

    /// <summary>读取 struct 变量的所有字段值。</summary>
    public static (string[] fieldNames, Number[] values) ReadStruct(
        VMWorld world, int instanceId, string varName)
    {
        ref var inst = ref world.Pool.Instances[instanceId];
        var program = world.Modules.Get(inst.ModuleSlot);
        if (program?.SymbolTable == null)
            return (null, null);

        for (int i = 0; i < program.SymbolTable.Length; i++)
        {
            ref readonly var sym = ref program.SymbolTable[i];
            if (sym.Name == varName && sym.FieldCount > 0)
            {
                int physReg = sym.Register < 16
                    ? sym.Register
                    : sym.Register + inst.RegisterBase;
                var values = new Number[sym.FieldCount];
                for (int f = 0; f < sym.FieldCount; f++)
                    values[f] = inst.Registers.Get(physReg + f);
                return (sym.FieldNames, values);
            }
        }
        return (null, null);
    }
}
```

**改动量**：独立 helper 类 ~30 行。VM 核心零改动。

---

## 四、多 VM 实例交互

### 4.1 已有机制

#### wait_for（父→子等待，已完成）

```ffs
var bulletId: int = SpawnBullet(targetX, targetY)
wait_for(bulletId)
// 子弹完成后继续
```

VM 层实现：`VMWorld.Tick()` 中每帧检查 `WaitTargetInstanceId` 对应实例是否 Completed。

| 适用 | 限制 |
|------|------|
| 父等子完成（子弹飞到终点、持续效果结束） | 只能等 Completed，不能等中间状态 |
| 零开销（每帧 1 次 if 检查） | 单向（父等子，子不能反向通知父） |

#### Blackboard（共享数据通道，已完成）

```ffs
// AI 脚本写入决策
SetBlackboard(self, CURRENT_TARGET, enemyId)

// 技能脚本读取 AI 决策
var target: int = GetBlackboard(self, CURRENT_TARGET)
```

| 适用 | 限制 |
|------|------|
| 松耦合数据共享（AI→技能、技能→效果器） | 非调用语义，无请求/响应 |
| 不破坏 VM 封装 | 需约定 Key 命名 |

### 4.2 需新增机制

#### MI-2：SpawnScript Syscall（脚本侧 Spawn 子实例）

当前 `SpawnInstance` 是 `VMWorld` 的 C# API，脚本无法直接调用。

```csharp
// Syscall handler 注册（~10 行）
syscalls.Register(slot, "SpawnScript", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    int moduleSlot = args.GetInt(0);
    int entryIP = args.GetInt(1);
    int newId = world.SpawnInstance(moduleSlot, entryIP);
    args.SetReturnInt(newId);
});
```

```ffs
// 技能脚本 spawn 子弹效果器
var bulletId: int = SpawnScript(BULLET_MODULE, 0)
SetBlackboard(bulletId, PARAM_TARGET_X, targetX)
wait_for(bulletId)
```

**注意**：Tick 内 Spawn 对 ActiveList 遍历的影响。当前 `InstancePool.Allocate` 在 `ActiveList` 末尾追加，而 `Tick()` 正向遍历，已 spawn 的新实例在当前帧**不会**被 Tick（因为 `i < Pool.ActiveListCount` 中的上限在循环开始时读取后不变——但实际代码中 `ActiveListCount` 是实时读取的）。

**风险 R-MI2-1**：需确认 Tick 循环中 `Pool.ActiveListCount` 是动态读取还是缓存。如果是动态的，Tick 内 Spawn 的实例可能在同一帧被执行。建议在 Tick 入口缓存 `int tickCount = Pool.ActiveListCount`，Spawn 的实例延迟到下一帧 Tick。

#### MI-3：KillInstance Syscall（脚本侧 Kill 子实例）

```csharp
// Syscall handler 注册（~8 行）
syscalls.Register(slot, "KillInstance", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    int targetId = args.GetInt(0);
    ref var target = ref world.Pool.Instances[targetId];
    if (target.IsAlive && (target.StateFlags & VMStateFlags.Completed) == 0)
        target.StateFlags |= VMStateFlags.Killed;
});
```

```ffs
// 技能被中断时 Kill 所有子效果器
defer {
    KillInstance(bulletId)
    EndAction()
}
```

Kill 后目标实例在下一帧进入 cleanup 路径（defer 块执行），符合已有语义。

#### MI-5：事件总线（子→父通知，生产方案）

对于子弹命中目标后通知父技能等场景，有两种方案：

**方案 A：Blackboard 轮询（零新机制，推荐起步）**

```ffs
// 子弹脚本：命中时写标记
if hitDetected > 0 {
    SetBlackboard(self, BULLET_HIT_FLAG, 1)
    SetBlackboard(self, BULLET_HIT_TARGET, targetId)
}

// 技能脚本：每帧检查
var hit: int = GetBlackboard(bulletId, BULLET_HIT_FLAG)
if hit > 0 { ... }
```

每帧 1 次 Syscall 轮询，适合少量父子对。

**方案 B：宿主事件队列（适合广播场景）**

```csharp
// 宿主侧维护确定性事件队列
struct VMEvent { public int SenderId; public int EventType; public Number Payload; }
List<VMEvent> _eventQueue = new List<VMEvent>();

// EmitEvent Syscall
syscalls.Register(slot, "EmitEvent", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    _eventQueue.Add(new VMEvent {
        SenderId = s.InstanceId,
        EventType = args.GetInt(0),
        Payload = args.GetNumber(1)
    });
});

// PollEvent Syscall — 查找并消费第一个匹配事件
syscalls.Register(slot, "PollEvent", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    int eventType = args.GetInt(0);
    for (int i = 0; i < _eventQueue.Count; i++)
    {
        if (_eventQueue[i].EventType == eventType)
        {
            args.SetReturn(_eventQueue[i].Payload);
            _eventQueue.RemoveAt(i);
            return;
        }
    }
    args.SetReturnInt(-1); // 无匹配事件
});
```

**帧同步约束**：事件队列必须在 `SaveState`/`LoadState` 中参与快照（宿主侧管理）。

### 4.3 交互模式总结

| 交互模式 | 机制 | 现状 | 需新增 |
|----------|------|------|--------|
| **父等子完成** | `wait_for(childId)` | ✅ 已有 | — |
| **共享数据** | Blackboard Syscall | ✅ 已有 | — |
| **父→子传参** | Blackboard / 初始寄存器 | ✅ 已有 | — |
| **父 Spawn 子** | `SpawnScript()` | ❌ | MI-2（~10 行 Syscall） |
| **父 Kill 子** | `KillInstance()` | ❌ | MI-3（~8 行 Syscall） |
| **子→父通知** | Blackboard 轮询 | ✅ 已有 | — |
| **广播事件** | 宿主事件队列 | ❌ | MI-5（~40 行 Syscall） |

### 4.4 嵌套 VM 生命周期管理

典型嵌套关系：

```
AI 实例 (角色生命周期)
  ├─ Spawn → 技能实例 A (单次技能释放)
  │    ├─ Spawn → 子弹实例 1 (持续飞行)
  │    └─ Spawn → DOT 实例 2 (持续伤害)
  └─ wait_for(A) → 技能完成后 AI 继续决策
```

**Kill 级联问题**：AI 被 Kill 时，技能实例 A 应该也被 Kill，进而 Kill 子弹和 DOT。

**方案：defer 级联 Kill**

```ffs
// AI 脚本
func main() {
    while true {
        var skillId: int = SpawnScript(SKILL_MODULE, 0)
        defer { KillInstance(skillId) }
        wait_for(skillId)
    }
}

// 技能脚本
func main() {
    var bulletId: int = SpawnScript(BULLET_MODULE, 0)
    defer { KillInstance(bulletId) }
    // ...
    wait_for(bulletId)
}
```

AI 被 Kill → defer 触发 KillInstance(skillId) → 技能的 defer 触发 KillInstance(bulletId)。
级联 Kill 在连续帧中展开（每帧一层 cleanup），符合已有语义。

**替代方案（宿主侧）**：宿主维护实例树，Kill 根实例时批量 Kill 子树。
但 defer 级联方案更可控（脚本自身管理子实例生命周期），推荐优先使用。

---

## 五、需补充能力清单（MI 系列）

| # | 能力 | 类型 | 改动范围 | 改动量 | VM 核心影响 |
|---|------|------|---------|--------|------------|
| **MI-1** | DAP 多实例支持（threads 映射） | 调试增强 | ScriptDebugger + DapServer | ~60 行 | 无 |
| **MI-2** | SpawnScript Syscall | 实例管理 | Syscall handler | ~10 行 | 无 |
| **MI-3** | KillInstance Syscall | 实例管理 | Syscall handler | ~8 行 | 无 |
| **MI-4** | VMInspector helper（按名读变量） | 数据读取 | 新 helper 类 | ~30 行 | 无 |
| **MI-5** | 事件总线 Syscall（EmitEvent/PollEvent） | 子→父通知 | Syscall handler + 宿主队列 | ~40 行 | 无 |

**关键结论**：MI-2~MI-5 全部是 Syscall 层扩展，VM 核心零改动。MI-1 仅涉及调试层。

---

## 六、已识别风险

| ID | 风险 | 影响 | 缓解 |
|----|------|------|------|
| **R-MI2-1** | Tick 循环中 `ActiveListCount` 动态读取 → Tick 内 Spawn 的实例可能在同帧被执行 | 确定性风险 | Tick 入口缓存 count，或 Spawn 标记延迟激活 |
| **R-MI3-1** | KillInstance 在 Tick 中间触发 → 被 Kill 实例可能在同帧已执行过 | 语义正确但时序不直觉 | Kill 只设标志，cleanup 延迟到下帧（与 VMWorld 已有行为一致） |
| **R-MI5-1** | 事件队列未参与 SaveState/LoadState → 回滚后事件丢失 | 帧同步不确定性 | 队列由宿主管理快照，或限制事件仅在帧末产生 |
| **R-MI5-2** | 事件 RemoveAt 的 O(n) 开销 | 大量事件时性能退化 | 典型战斗场景事件数 <20/帧，无须优化；如需可改 swap-remove |

---

## 七、实施建议

### C1-α 最小接入路径（推荐起步）

1. 实现 5 个核心 Syscall（`BeginAction`, `EndAction`, `CheckAttackHit`, `ApplyDamage`, `SpawnEffectHit`）
2. 在 Unity PlayMode 中跑通"普通攻击"技能脚本
3. 不需要 MI-2~MI-5（单技能无子实例交互）

### C1-β 扩展到完整技能

1. 补齐碰撞/击退/硬直 Syscall（参考 Skills/README.md 协议）
2. 跑通"飞燕旋风腿"完整 56 帧技能
3. 仍不需要 MI-2~MI-5

### C1-γ 接入 AI 层 + 多实例交互

1. 实现 MI-2（SpawnScript）+ MI-3（KillInstance）
2. 实现 AI Syscall（FindNearestEnemy, GetDistance, MoveToward）
3. AI spawn 技能实例 + wait_for + defer kill
4. 此时需要 MI-1（DAP 多实例）提升调试体验

### V5 + MI-5

1. 帧内 Profiler 验证
2. 按需实现事件总线（MI-5）处理子弹命中回调等场景
