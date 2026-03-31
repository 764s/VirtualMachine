# VM OpCodes Draft

> 本文不是完整 VM 指令大全，而是从当前运行时布局与第一颗曳光弹（Tracer Bullet）反推出来的**最小指令集草案**。目的不是一次性设计完所有能力，而是先定义一组足以支撑 `LoadConst -> RegisterCleanup -> Syscall -> Wait -> Return / KillCleanup` 的底层执行原语。

---

## 一、本文的目标

本文只解决以下问题：

1. **为了跑通第一颗曳光弹，VM 最少需要哪些指令？**
2. **这些指令应该承担怎样的物理职责？**
3. **哪些能力应该是指令，哪些能力应该留给 Syscall？**
4. **这组最小指令如何与 `ExecutionContextComponent`、`VMSlot`、Cleanup 栈、Wait 状态咬合？**

本文故意不做：

- 全量通用指令集设计；
- 表达式系统完备设计；
- 高级控制流宏指令设计；
- 优化型指令与融合指令设计；
- 针对子弹 / Buff 的特化指令设计。

因为在当前阶段，更重要的是先把“第一颗曳光弹”所需的物理原语钉死。

---

## 二、最小设计原则

### 2.1 指令集必须服务运行时物理结构，而不是服务语法想象

当前指令设计必须服从以下前提：

- RAM 已经被钉死为 `ExecutionContextComponent`
- 寄存器是固定大小 `VMSlot[]`
- Handle 是 64 位物理值
- Cleanup 栈是固定深度数组
- `wait` 必须落成显式状态
- Syscall 是宿主交互的唯一边界

所以设计 OpCode 时优先问的是：

- 这条指令是否真的需要单独存在？
- 它是否直接映射到 VM 的物理状态变化？
- 它是否会诱导我们偷偷把复杂能力塞回 VM，而不是交给 Syscall？

---

### 2.2 当前阶段的原则：少而硬，不求全

在第一阶段，指令集应尽量少。

原因是：

- 指令越多，越容易把高层表达问题和底层执行问题混在一起；
- 指令越多，越容易提前引入还没有被业务和 tracer bullet 证明必要的抽象；
- 指令越少，越容易把每条指令的状态变化与回滚语义定义清楚。

因此当前最优策略不是“设计一套看起来完整的 VM”，而是：

> **只保留 tracer bullet 必须存在、且无法由其他已有原语自然表达的指令。**

---

## 三、第一颗曳光弹反推出的最小能力

根据 [VM_Tracer_Bullet.md](Assets/ScriptVM/VM_Tracer_Bullet.md)，最小业务语义是：

1. 注册 Cleanup
2. 写黑板状态
3. Wait 10 帧
4. 恢复后播放特效
5. 正常结束时跑 Cleanup
6. 强制 Kill 时也跑 Cleanup

由此反推，当前最小运行时原语必须至少支持：

1. **将常量装入寄存器**
2. **注册 Cleanup 入口**
3. **调用宿主 Syscall**
4. **进入等待状态并中止本帧继续执行**
5. **正常 Return**
6. **切入 Cleanup 模式并按栈回放**
7. **在 Cleanup 流程中逐步退栈**

注意：

- “恢复执行”本身不一定需要单独指令，它可以是调度器看到 `WaitFrames == 0` 后继续从 `IP` 执行
- “Kill”本身也不一定是字节码指令，它更可能是宿主外力事件，把上下文 `StateFlags` 切到 `Killed`，并在 VM Tick 入口被提升为 `InCleanup`
- `StateFlags` 至少必须能区分 `Killed` 与 `InCleanup`，否则 `WAIT` 与 `RETURN` 的状态机分支会发生混乱

因此，不要把所有运行时现象都误设计成单独 OpCode。

---

## 四、推荐的第一阶段最小 OpCode 集

当前建议只定义以下 7 类底层指令：

1. `NOP`
2. `LOAD_CONST`
3. `SYSCALL`
4. `WAIT`
5. `PUSH_CLEANUP`
6. `POP_CLEANUP`
7. `RETURN`

其中：

- `NOP`：占位 / 调试对齐 / 回填辅助
- `LOAD_CONST`：将 ROM 常量装入寄存器
- `SYSCALL`：唯一宿主交互入口
- `WAIT`：唯一挂起入口
- `PUSH_CLEANUP`：注册清理逻辑
- `POP_CLEANUP`：正常离开作用域时注销清理逻辑
- `RETURN`：当前过程结束；如果仍有 Cleanup 栈，则进入 Cleanup 驱动

其中 `LOAD_CONST` 是当前版本必须补上的致命缺口。没有它，`SYSCALL` 前的参数装载就只能依赖宿主偷塞寄存器，无法形成自洽的寄存器虚拟机模型。

这组指令并不“强大”，但它已经足够支撑第一颗 tracer bullet。

---

## 五、每条指令的职责定义

### 5.1 `NOP`

#### `NOP` 的作用

- 占位；
- 调试对齐；
- Jump backpatch 时的过渡占位；
- 避免某些空块强行生成额外语义。

#### `NOP` 对运行时状态的影响

- `IP += 1`
- 不改寄存器
- 不改 Wait
- 不改 Cleanup 栈

#### `NOP` 在当前阶段是否必须

严格说 tracer bullet 不一定必须有 `NOP`，但建议保留。因为：

- 它几乎零复杂度；
- 对调试和后续 backpatching 很有帮助；
- 可以减少一些“空块怎么办”的无谓设计分叉。

---

### 5.2 `LOAD_CONST`

#### `LOAD_CONST` 的作用

将 ROM 常量或立即值装入目标寄存器。

#### `LOAD_CONST` 的语义

- 从常量表或立即数字段读取值
- 将值写入目标寄存器 `dstReg`
- `IP += 1`

#### `LOAD_CONST` 的伪格式

```text
LOAD_CONST dstReg, constId
```

第一阶段推荐优先走常量表索引，而不是把太多值直接塞进 `SYSCALL` 指令本体中。

#### `LOAD_CONST` 在 tracer bullet 中的用途

- 装入 `1`，供 `SetBlackboard(..., 1)` 使用
- 装入 `0`，供 Cleanup 中的 `SetBlackboard(..., 0)` 使用
- 装入 `Fx_SimpleCast`，供 `PlayEffect` 使用

#### 为什么 `LOAD_CONST` 必须是底层指令

因为寄存器 VM 必须自己完成参数装载。`SYSCALL` 负责调用宿主，而不是隐式携带大量业务常量。

---

### 5.3 `SYSCALL`

#### `SYSCALL` 的作用

执行宿主能力调用。

这是 VM 与宿主交互的唯一硬边界。

#### `SYSCALL` 的语义

- 从指令参数中读取 `SyscallId`
- 从约定寄存器区读取参数
- 调用宿主 `SyscallTable[SyscallId]`
- 如有返回值，将结果写回指定寄存器
- `IP += 1`

#### `SYSCALL` 的伪格式

```text
SYSCALL syscallId, argBase, argCount, retReg
```

也可以简化成：

```text
SYSCALL syscallId
```

参数布局固定约定在寄存器协议里，而不在每条指令里重复描述。

当前阶段推荐后者，因为更简单。

#### `SYSCALL` 在 tracer bullet 中的用途

- `SetBlackboard(self, CastingState, 1)`
- `SetBlackboard(self, CastingState, 0)`
- `PlayEffect(self, Fx_SimpleCast)`

#### 为什么 `SYSCALL` 必须是底层指令

因为宿主交互是整个 VM 的物理边界之一，不能被更高层魔法化。

---

### 5.4 `WAIT`

#### `WAIT` 的作用

让 VM 进入显式挂起状态，并立刻交出执行权。

#### `WAIT` 的语义

- 从立即数或寄存器读取等待帧数
- 将 `ExecutionContextComponent.WaitFrames` 设为对应值
- 将 `IP` 更新到下一条指令位置
- 立刻停止当前 Tick 的解释循环

#### `WAIT` 的伪格式

```text
WAIT immediateFrames
```

第一阶段建议先只支持：

```text
WAIT_IMM frames
```

因为 tracer bullet 只需要固定 `wait 10`。

等后续再考虑：

- `WAIT_REG rX`
- `WAIT_EVENT handle`
- `WAIT_UNTIL condition`

#### `WAIT` 的恢复方式

`WAIT` 不负责“恢复自己”。

恢复应由宿主调度器完成：

- 每帧先检查 `StateFlags` 是否包含 `Killed`
- 如果已被 `Killed`，必须绕过普通 `WaitFrames > 0` 的早退逻辑，直接切入 Cleanup 模式
- 只有在未被 `Killed` 的情况下，才执行 `WaitFrames` 递减与是否恢复主流程的判断
- 当 `WaitFrames == 0` 时，再允许从当前 `IP` 继续执行

这是一条必须明确的物理纪律：

> **在 Tick() 入口，`Killed` 的优先级必须高于 `WaitFrames > 0`。**

否则 VM 会在“仍处于等待中”与“已经收到强制终止”之间发生死锁，导致 Cleanup 永远不执行。

#### 为什么 `WAIT` 必须是底层指令

因为 `wait` 是整个系统的第一关键语义，不能退回宿主协程或上层补丁逻辑。

---

### 5.5 `PUSH_CLEANUP`

#### `PUSH_CLEANUP` 的作用

把一个 Cleanup 入口注册到 `CleanupFrames[]` 中。

#### `PUSH_CLEANUP` 的语义

- 读取 Cleanup 入口 IP
- 将其压入 `CleanupFrames[CleanupDepth]`
- `CleanupDepth += 1`
- `IP += 1`

#### `PUSH_CLEANUP` 的伪格式

```text
PUSH_CLEANUP cleanupEntryIp
```

如果未来需要保存额外信息，例如作用域基寄存器、局部 cleanup 参数，也可以扩展为：

```text
PUSH_CLEANUP cleanupEntryIp, baseReg
```

但 tracer bullet 第一阶段只需要最小入口地址。

#### `PUSH_CLEANUP` 在 tracer bullet 中的用途

注册：

```text
defer {
    SetBlackboard(self, CastingState, 0)
}
```

#### 为什么 `PUSH_CLEANUP` 必须是底层指令

因为 Cleanup 不是语法糖，而是强制中断安全线，必须进入运行时状态机。

---

### 5.6 `POP_CLEANUP`

#### `POP_CLEANUP` 的作用

在正常离开作用域时，将最近注册的 Cleanup 从栈中移除。

#### `POP_CLEANUP` 的语义

- `CleanupDepth -= 1`
- `IP += 1`

#### `POP_CLEANUP` 的伪格式

```text
POP_CLEANUP
```

#### 为什么 `POP_CLEANUP` 在 tracer bullet 第一阶段仍然建议保留

严格说，最小 demo 甚至可以依赖“流程结尾统一 cleanup”而暂时不写 `POP_CLEANUP`。

但建议第一阶段就把它定义出来，因为：

- 只要将来出现嵌套作用域，就一定需要它；
- 如果第一颗 tracer bullet 不先把 Cleanup 机制设计成完整 push/pop 结构，后续扩展很容易反向污染语义；
- 它能让编译器模型从第一天起就更接近最终真实结构。

#### `POP_CLEANUP` 在 tracer bullet 中是否一定执行

未必。

如果 tracer bullet 的 cleanup 作用域就是整个函数体，那么正常结束时可能直接通过 `RETURN` 触发 Cleanup 驱动，而不是提前 `POP_CLEANUP`。

所以第一阶段允许：

- 指令已定义；
- 但首个业务样例未必用到它。

---

### 5.7 `RETURN`

#### `RETURN` 的作用

表示当前过程逻辑结束。

#### `RETURN` 的语义

当前阶段推荐的语义不是简单“结束解释器”，而是：

1. 如果当前**不在** Cleanup 模式，且 `CleanupDepth > 0`
   - 设置 `StateFlags |= InCleanup`
   - 跳转到最近 Cleanup 入口
2. 如果当前**不在** Cleanup 模式，且 `CleanupDepth == 0`
   - 标记执行完成
3. 如果当前**已经在** Cleanup 模式
   - 视为当前 Cleanup block 结束
   - 退栈并判断是否仍有更外层 Cleanup
   - 若仍有 Cleanup，则跳转到下一个 Cleanup 入口
   - 若栈已空，则清除 `InCleanup` 并最终标记完成

#### `RETURN` 语义成立的前提

`RETURN` 之所以能同时承担“主流程结束”和“Cleanup block 结束”两种职责，前提是运行时必须有明确的状态位，例如：

- `Killed`
- `InCleanup`
- `Completed`

其中至少必须存在 `InCleanup`，否则解释器无法区分：

- 当前 `RETURN` 是从主流程返回
- 还是从 Cleanup block 返回

如果没有这个状态位，`RETURN` 会非常容易导致：

- 重复清理
- 跳错入口
- 无限循环
- 过早销毁上下文

#### 为什么 `RETURN` 必须这么定义

因为当前 VM 的核心需求不是“函数返回值多优雅”，而是：

- 正常结束也必须 Cleanup
- 强制结束也必须 Cleanup
- Cleanup 必须和主流程在统一状态机里闭环

所以 `RETURN` 在第一阶段更接近：

> **过程结束 / Cleanup 驱动切换点**

而不仅仅是普通语言意义上的 return。

---

## 六、当前阶段明确不进入 OpCode 的能力

为了避免指令集过早膨胀，以下能力当前阶段不建议定义为底层指令：

### 6.1 `KILL`

不建议做成字节码指令。

原因：

- Kill 通常来自宿主外力（受击打断、死亡、中止技能等）
- 它更像调度器 / 宿主系统对 `ExecutionContextComponent.StateFlags` 的改变
- VM 的责任是收到该状态后切入 Cleanup 流程，而不是靠脚本自己执行一条 `KILL`

---

### 6.2 `RESUME`

不建议做成字节码指令。

原因：

- 恢复不是脚本主动行为
- 恢复是宿主调度器在 `WaitFrames == 0` 后再次进入解释循环
- 用单独 `RESUME` 指令只会让状态机更绕

---

### 6.3 `JUMP / BRANCH`

当前 tracer bullet 不强制需要。

未来肯定会有，但第一阶段可以先不定义。

因为：

- 当前业务没有分支和循环
- 先把 `wait / cleanup / syscall` 跑通更关键
- 否则很容易把指令集讨论重新带偏到“语言功能”而不是“物理闭环”

---

### 6.4 算术与比较指令

当前阶段也不强制需要。

因为第一颗 tracer bullet 不需要表达式计算。

这类能力应该等：

- 最小执行状态已经跑通；
- Save / Load 已验证；
- Cleanup 路径可靠；

再逐步增加。

---

## 七、第一阶段推荐的字节码格式思路

### 7.1 当前不追求压缩率，先追求可解释与可调试

第一阶段建议让每条指令的编码保持清晰，而不是过早追求紧凑编码。

原因：

- tracer bullet 的首要目标是验证语义闭环；
- 指令编码一开始太复杂，会模糊真实问题；
- 调试和打印 bytecode 时，清晰比省几个字节更重要。

---

### 7.2 推荐的概念格式

逻辑上每条指令可视为：

```text
[Opcode][A][B][C]
```

或者：

```text
struct Instruction
{
    OpCode Code;
    int A;
    int B;
    int C;
}
```

当前阶段不需要承诺最终是否真的使用这个实体结构；它只是帮助我们明确：

- 每条指令最多携带少量固定参数
- 所有参数都应是固定宽度、值类型、易于打印和回填的

---

## 八、第一颗曳光弹的示意字节码

针对下面这段伪脚本：

```text
defer {
    SetBlackboard(self, CastingState, 0)
}

SetBlackboard(self, CastingState, 1)
wait 10
PlayEffect(self, Fx_SimpleCast)
return
```

一个概念上更真实的最小 bytecode 序列应当如下：

```text
0: PUSH_CLEANUP 8
1: LOAD_CONST r3, Const_One
2: SYSCALL SetBlackboard(self=r0, key=r1, value=r3)
3: WAIT 10
4: LOAD_CONST r2, Const_FxSimpleCast
5: SYSCALL PlayEffect(self=r0, effect=r2)
6: RETURN
7: NOP
8: LOAD_CONST r3, Const_Zero
9: SYSCALL SetBlackboard(self=r0, key=r1, value=r3)
10: RETURN
```

这里的关键观察点：

- `PUSH_CLEANUP 8` 把 cleanup block 的入口地址压栈
- `LOAD_CONST` 负责把 `1`、`0`、`Fx_SimpleCast` 装入寄存器
- `SYSCALL` 只消费寄存器参数，不再隐式携带业务常量
- 主流程正常执行到 `RETURN` 时，如果 `StateFlags` 中尚未进入 `InCleanup` 且 `CleanupDepth > 0`，则切到 `IP=8`
- Cleanup block 执行完自己的 `RETURN` 时，解释器必须依赖 `InCleanup` 状态位判断当前是在清理流程中
- Cleanup 栈清空后，才允许最终标记实例完成

这也说明：

> Cleanup block 在 bytecode 层本质上是一个普通代码段，只是入口由 Cleanup 栈维护；而常量装载必须由 `LOAD_CONST` 显式完成，不能偷塞进 `SYSCALL`。

---

## 九、Kill 路径如何与这组指令咬合

### 9.1 Kill 不是指令，而是状态切换

建议宿主在等待中发出 Kill 时：

- 设置 `StateFlags |= Killed`
- 清空或忽略后续正常业务推进资格
- 不允许再按普通 `WaitFrames > 0` 早退逻辑把当前实例挂起在等待态中
- 将 VM 调度逻辑切换到 `EnterCleanupIfNeeded()`

然后 VM 做：

1. 在 Tick() 入口最高优先级检查 `Killed`
2. 若已 `Killed` 且尚未 `InCleanup`，则进入 Cleanup 模式
3. 检查 `CleanupDepth`
4. 若大于 0，则跳到最近 Cleanup 入口
5. 在 Cleanup 模式中运行 bytecode
6. Cleanup 跑完后最终结束实例

这里必须明确一条物理纪律：

> **Tick() 的入口优先级必须是 `Killed` 高于 `WaitFrames > 0`。**

否则 VM 会在“仍处于等待中”和“已经收到强制终止”之间形成死锁，导致 Cleanup 永远不会被执行。

---

### 9.2 为什么这很重要

如果 Kill 也被做成脚本内普通指令，就会模糊责任边界：

- 谁负责强制中断？
- 谁负责禁止主流程继续？
- 谁负责切 Cleanup 模式？

当前最清晰的责任划分是：

- **宿主负责发出外力中断**
- **VM 负责在状态机内执行 Cleanup 收尾**

---

## 十、最小解释循环需要回答的问题

即便当前阶段还不写正式实现，也必须能口头上回答下面这些问题：

1. `WAIT` 执行后，解释循环如何提前退出？
2. 下次 Tick 进入时，为什么 `Killed` 的判断优先级必须高于 `WaitFrames > 0`？
3. `RETURN` 如何依赖 `InCleanup` 状态位判断当前是在主流程还是 Cleanup 流程？
4. Cleanup block 的 `RETURN` 如何知道要不要继续更外层 Cleanup？
5. Kill 时谁来把 `IP` 切到 Cleanup 入口？
6. `LOAD_CONST` 如何把业务常量装入寄存器，而不是让 `SYSCALL` 偷带参数？
7. Save / Load 时是否需要把 `StateFlags`、`CleanupDepth`、`CleanupFrames` 一并保存？

如果这些问题还答不出来，就说明这组 OpCode 虽然列出来了，但还没真正形成可执行模型。

---

## 十一、第一阶段通过标准

只有当下面这些都成立时，才能认为最小指令集成立：

1. 能将 tracer bullet 编译成上述同等级别的 bytecode 序列
2. `WAIT` 能正确挂起并恢复
3. `SYSCALL` 能稳定调用宿主
4. 正常 `RETURN` 会触发 Cleanup
5. 强制 Kill 后也会触发 Cleanup
6. Cleanup 执行完成后上下文能正确结束
7. 全流程不需要动态列表、托管对象或协程残留
8. Save / Load 不破坏 IP、Wait、Cleanup 栈一致性

---

## 十二、后续扩展顺序建议

当前这组 OpCode 草案通过后，才建议按下面顺序扩展：

1. `MOVE / COPY` — **最高优先级**：没有寄存器间搬运指令，分支/循环/函数调用都无法实现。曳光弹通过后应立刻补上，优先级高于 `JUMP`。
2. `JUMP / JUMP_IF`
3. 简单比较与布尔运算
4. 函数调用与固定深度 `CALL / RETURN`
5. Handle64 驱动的批处理相关调用习惯
6. 结构体拍平与局部寄存器分配优化

这里的顺序很重要：

- 先补足最基础的数据搬运；
- 再补控制流；
- 再补调用；
- 最后再处理复杂数据与优化。

这样才能避免“还没把基础状态机跑稳，就提前做成一个看起来很强的迷你高级语言 VM”。

---

## 最终结论

当前阶段最合理的最小 OpCode 集不是“大而全”的，而是：

- `NOP`
- `LOAD_CONST`
- `SYSCALL`
- `WAIT`
- `PUSH_CLEANUP`
- `POP_CLEANUP`
- `RETURN`

这 7 条已经足够支撑第一颗曳光弹，并把最关键的物理约束落实到可执行状态机中：

- `LOAD_CONST` 负责寄存器参数装载
- `wait` 是显式状态
- `Killed` 必须在 Tick() 入口高于 `WaitFrames > 0`
- `RETURN` 必须依赖 `InCleanup` 状态位完成主流程 / Cleanup 流程分流
- 宿主交互只走 `SYSCALL`
- Cleanup 是一等运行时机制
- Kill 由宿主触发、由 VM 收尾

也就是说，后续任何新增指令都应先回答一个问题：

> **它是在补足这个最小状态机，还是在绕开它？**

如果是后者，就不应该进入第一阶段实现。
