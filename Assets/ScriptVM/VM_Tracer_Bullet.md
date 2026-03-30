# VM Tracer Bullet（第一颗曳光弹：Wait -> Syscall -> Cleanup）

> 本文定义 VM 的第一颗“曳光弹”（Tracer Bullet）：不是为了验证语言有多强，而是为了尽快打通一条**最小、可运行、可测量、可回滚、可强制清理**的垂直闭环。只要这一颗曳光弹没有跑通，就不应该继续扩 DSL、AST 和复杂业务样例。

---

## 一、这颗曳光弹要解决什么

这颗 Tracer Bullet 的目标不是“先做一个小 demo”，而是提前验证以下架构硬约束是否真的能成立：

1. **RAM 是否真的完全落在 ECS 纯值类型组件中**
2. **`wait` 是否真的能以显式状态挂起/恢复**
3. **强制中断时 Cleanup 是否一定会执行**
4. **整个流程是否可以零 GC 运行**
5. **Save / Load 后执行行为是否一致**
6. **Syscall 边界是否足够清晰**

如果这条最小链路都无法成立，那么后续更复杂的：

- 分支
- 循环
- 子弹持续行为
- Buff 事件驱动
- 批量目标句柄
- 结构体拍平

都会建立在不可靠的地基上。

---

## 二、为什么选这个最小业务

这颗曳光弹故意不选复杂业务，而是选一个最小但足够“卡住架构命门”的流程：

> **开始 -> 写入宿主状态 -> 注册 Cleanup -> wait 10 -> 调用特效 Syscall -> 结束**

并要求同时验证两条路径：

1. **正常结束路径**：等待结束后正常播放特效，再执行 Cleanup
2. **强制打断路径**：等待过程中被外力 Kill，但仍然必须先执行 Cleanup

这样一来，一颗小曳光弹就能同时覆盖：

- `wait`
- ROM / RAM 分离
- ECS 组件化状态
- Cleanup 机制
- Syscall 边界
- Save / Load
- 0 GC

---

## 三、业务定义（唯一测试业务）

### 3.1 业务语义

定义一个最小技能执行实例，语义如下：

1. 技能开始时，将宿主黑板中的 `CastingState` 设为 `1`
2. 同时注册 Cleanup：在技能退出时把 `CastingState` 重置为 `0`
3. `wait 10`
4. 等待结束后调用 `PlayEffect` Syscall
5. 正常结束

如果在等待途中被外力强制打断：

- `PlayEffect` 不应执行
- `CastingState` 仍然必须被 Cleanup 重置为 `0`

---

### 3.2 对应的伪脚本形态

这不是最终 DSL，只是帮助统一语义理解：

```text
skill TracerBullet
{
    defer {
        SetBlackboard(self, CastingState, 0)
    }

    SetBlackboard(self, CastingState, 1)
    wait 10
    PlayEffect(self, Fx_SimpleCast)
}
```

这里最关键的不是脚本长相，而是这三个硬点：

- 有宿主可观察状态：`CastingState`
- 有显式挂起：`wait 10`
- 有强制中断仍必须执行的 Cleanup

---

## 四、这颗曳光弹的最小 AST 语义

在当前阶段，不需要先有 parser，可以直接手写 AST。

推荐最小 AST 节点只保留下面几种：

- `Sequence`
- `RegisterCleanup` / `Defer`
- `Syscall`
- `WaitFrames`
- `Return`

对应的语义顺序：

```text
Sequence
├── RegisterCleanup
│   └── Syscall(SetBlackboard, self, CastingState, 0)
├── Syscall(SetBlackboard, self, CastingState, 1)
├── WaitFrames(10)
├── Syscall(PlayEffect, self, Fx_SimpleCast)
└── Return
```

关键点：

- 不需要分支
- 不需要循环
- 不需要函数调用
- 不需要动态列表
- 不需要结构体
- 不需要复杂表达式

也就是说，这颗曳光弹故意只验证**最核心的运行时闭环**。

---

## 五、这颗曳光弹要求的最小运行时状态

这颗 Tracer Bullet 不需要完整 VM 的所有能力，但至少要求下面这些字段已经存在于 `ExecutionContextComponent` 中：

- `ProgramId`
- `InstructionPointer`
- `WaitFrames`
- `StateFlags`（至少能区分 `Killed`、`InCleanup`、`Completed` 等关键状态）
- `CleanupDepth`
- `CleanupFrames[]`
- `Registers[]`

在这一颗曳光弹中：

- `CallFrames[]` 可以暂时闲置不用
- 但 `CleanupFrames[]` 必须真的工作

---

## 六、推荐寄存器映射

为了尽量贴近后续真实实现，建议即便在第一颗曳光弹里，也明确一份最小寄存器约定。

例如：

- `r0`：`SelfEntityId`（`ulong`）
- `r1`：`CastingStateKeyHash`（`ulong`）
- `r2`：`Fx_SimpleCast` 的资源 ID（`ulong` 或 `uint`，物理上仍落在 64 位槽位）
- `r3`：临时保留

这样做的价值在于：

- 第一颗曳光弹就开始走 64 位 `VMSlot`
- 第一颗曳光弹就开始验证 VM 与宿主 EntityId / KeyHash 的 ABI 咬合
- 后续不会因为“demo 先偷用 int”而把错误习惯带进正式实现

---

## 七、这颗曳光弹要求的最小 Syscall 集

只需要两个宿主能力就够了：

### 7.1 写黑板状态

```csharp
Syscall_SetBlackboardInt(ulong selfEntityId, ulong keyHash, int value)
```

职责：

- 把 `CastingState` 写入宿主黑板
- 让宿主系统可观察到 VM 对外部状态的副作用
- 同时成为 Cleanup 的最小验证对象

---

### 7.2 播放特效

```csharp
Syscall_PlayEffect(ulong selfEntityId, ulong effectId)
```

职责：

- 作为等待结束后的宿主副作用调用
- 验证 `wait` 恢复后能正确继续执行后续 IP
- 验证在被强制打断时此调用不会误触发

---

## 八、最小字节码能力要求

此处不展开完整 OpCode 草案，但这颗曳光弹至少要求 VM 能表达以下运行时原语：

1. **将常量装入寄存器**
2. **注册 Cleanup 入口**
3. **调用 Syscall**
4. **进入等待状态（WaitFrames）**
5. **从等待中恢复后继续执行**
6. **正常 Return**
7. **外力 Kill 时进入 Cleanup 模式并按栈回放**

也就是说，即便你暂时还没写完整 OpCode 文档，至少也要能回答：

- `1`、`0`、`Fx_SimpleCast` 这类业务常量如何装入寄存器
- Cleanup 入口 IP 存哪？
- Wait 后恢复点怎么记录？
- Kill 后 VM 如何切换到 Cleanup 执行？
- Cleanup 跑完后上下文怎么标记结束？

如果这几个问题还答不出来，就说明还不适合继续扩 AST。

---

## 九、执行路径定义

### 9.1 正常路径

执行顺序应为：

1. 初始化执行实例，挂载 `ExecutionContextComponent`
2. 执行 `RegisterCleanup`
3. 调用 `SetBlackboard(self, CastingState, 1)`
4. 执行 `WaitFrames(10)`，保存状态并返回控制权
5. 宿主每帧驱动 VM，直到等待结束
6. 恢复执行，调用 `PlayEffect(self, Fx_SimpleCast)`
7. 正常结束
8. 执行 Cleanup：`SetBlackboard(self, CastingState, 0)`
9. 标记执行上下文结束

这里必须明确一个纪律：

> **正常结束也必须走 Cleanup，不允许只有强制中断才跑 Cleanup。**

---

### 9.2 强制打断路径

执行顺序应为：

1. 初始化执行实例，挂载 `ExecutionContextComponent`
2. 执行 `RegisterCleanup`
3. 调用 `SetBlackboard(self, CastingState, 1)`
4. 执行 `WaitFrames(10)`，保存状态并返回控制权
5. 在等待尚未完成时，宿主发出外力 Kill
6. VM 在下一次 Tick() 入口必须优先检查 `Killed`，其优先级高于 `WaitFrames > 0`
7. VM 不得继续按普通等待逻辑早退，而必须先切换到 Cleanup 模式
8. 执行 Cleanup：`SetBlackboard(self, CastingState, 0)`
9. 标记执行上下文结束
10. `PlayEffect` 不得被调用

这条路径是整颗曳光弹最重要的验证点。

---

## 十、验证清单（必须全部通过）

### 10.1 运行时结构验证

- `ExecutionContextComponent` 是纯值类型
- `VMSlot` 是固定 64 位物理槽位
- `Registers[]` 是固定大小
- `CleanupFrames[]` 是固定大小
- 不存在托管对象字段

---

### 10.2 挂起 / 恢复验证

- 执行 `wait 10` 时，VM 会停止推进后续 IP
- 等待结束后，VM 从正确位置恢复
- 恢复后只执行一次 `PlayEffect`
- 不会重复触发前面已完成的 Syscall
- 若 `Killed` 在等待期间被置位，则下一次 Tick() 必须优先进入 Cleanup，而不是继续按 `WaitFrames > 0` 早退

---

### 10.3 Cleanup 验证

#### 正常结束时

- `CastingState` 最终为 `0`
- Cleanup 只执行一次

#### 强制打断时

- `CastingState` 最终仍为 `0`
- `PlayEffect` 不发生
- Cleanup 只执行一次
- 不允许跳过 Cleanup 直接销毁上下文

---

### 10.4 Save / Load 验证

必须至少做一次中途 Save / Load：

- 在 `WaitFrames > 0` 时保存 `ExecutionContextComponent`
- 恢复后继续推进
- 恢复后的行为必须与未中断运行一致
- 包括：
  - `PlayEffect` 是否触发
  - 触发时机
  - Cleanup 是否正常执行

---

### 10.5 0 GC 验证

需要在 Profiler 或等价工具里验证：

- 进入执行前完成必要 warm-up
- 持续驱动这颗曳光弹若干轮
- 每帧执行不产生托管堆分配
- 强制 Kill 路径也不产生额外 GC

如果这一步失败，就说明“偏瞬态 + 零分配”还没有真正成立。

---

## 十一、失败判定条件

只要出现以下任意一种情况，就应判定这颗曳光弹失败：

1. `ExecutionContextComponent` 之外还藏着关键脚本状态
2. `wait` 依赖宿主协程或对象回调残留
3. Kill 时 Cleanup 无法稳定执行
4. 运行过程中出现 GC 分配
5. Save / Load 后执行结果不一致
6. `PlayEffect` 在被打断路径中误触发
7. 为了跑通这颗曳光弹，被迫引入动态列表、托管引用或对象图状态

换句话说：

> 曳光弹的目标不是“看起来跑起来了”，而是“以正确的物理约束跑起来了”。

---

## 十二、这颗曳光弹通过后，下一步才允许做什么

只有这颗 Tracer Bullet 跑通，才建议继续进入下一层扩展，例如：

1. 加入简单分支
2. 加入局部变量与寄存器复用
3. 加入函数调用与固定深度调用栈验证
4. 加入第一个 Handle64 批处理业务
5. 加入结构体拍平验证
6. 再讨论 DSL 文本语法

也就是说，后续扩展顺序必须建立在这颗最小闭环已经成立的基础上。

---

## 最终结论

这颗曳光弹不是一个“临时 demo”，而是后续所有工作的**物理验收门槛**：

> **只要 VM 还没有可靠地跑通“写黑板 -> wait -> 恢复 -> syscall -> Cleanup / Kill Cleanup”这一颗最小闭环，就不应该继续扩充 AST、DSL 和复杂业务样例。**

因为这条链路一旦成立，后面很多争论都会被压缩成一个更简单的问题：

> 这个新能力，能不能在不破坏这颗曳光弹成立前提的情况下加入？