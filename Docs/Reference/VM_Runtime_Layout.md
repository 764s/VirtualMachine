# VM Runtime Layout

> 本文不是指令集设计，也不是语法设计，而是 VM 的**物理运行时布局草案**。目标是先钉死运行时状态到底长什么样、放在哪里、如何与 ECS / 快照 / Syscall 咬合。后续 AST、编译器、字节码、指令集设计都必须服从这里定义的物理边界。

---

## 一、本文的目标

这份文档只回答 4 个问题：

1. **VM 的实例状态（RAM）到底包含哪些物理字段？**
2. **这些状态必须存在哪里？**
3. **什么数据允许进入寄存器，什么数据绝对不允许？**
4. **VM 如何与宿主 ECS、Syscall、快照/回滚物理咬合？**

本文刻意不展开：

- 完整 OpCode 设计；
- 完整 AST 节点设计；
- DSL 文本语法；
- 编辑器节点图细节；
- 具体业务脚本写法。

因为在这些上层设计开始之前，必须先把“运行时物理法则”焊死。

---

## 二、总原则：ROM / RAM 物理分离

VM 的运行时必须建立在严格的 **ROM / RAM 分离** 上：

### 2.1 ROM：静态脚本资产

ROM 是只读的、可共享的、不会随着某个实例运行而变化的编译产物，例如：

- Bytecode 指令流；
- 常量表；
- 调试符号；
- 源码映射；
- 函数入口表；
- 最大寄存器需求量；
- 最大调用深度；
- Cleanup 段入口表；
- Syscall 索引表。

ROM 的特点是：

- 同一种技能 / 子弹 / Buff 脚本只保留一份；
- 可挂在静态资源系统中；
- 不参与实例快照；
- 不允许混入任何实例级动态状态。

---

### 2.2 RAM：实例运行状态

RAM 是每个执行实例自己的状态，只能由纯值类型字段构成。

最小运行时状态至少包括：

- `InstructionPointer`（当前指令位置）；
- `WaitCounter` / `WaitFrames`（挂起计数）；
- `Registers[]`（寄存器槽位数组）；
- `Flags / StateBits`（少量运行时标志位）；
- `CurrentCleanupDepth` 或等价信息；
- 如保留有限调用栈，则包括固定深度 `CallFrames[]`；
- 如存在宿主句柄交互，则只保存 64 位句柄值，而不是对象本体。

RAM 的特点是：

- 必须可整体复制；
- 必须可整体恢复；
- 必须可直接挂在 ECS 实体上；
- 必须适合作为帧同步 / 回滚状态的一部分；
- 必须不依赖托管对象图。

---

## 三、RAM 的宿主落点：ExecutionContextComponent

### 3.1 关键结论

VM 的 RAM **不能悬空存在**，也不能藏在解释器类、宿主管理器对象或其他间接包装层中。

它必须直接落到 ECS 上，作为类似下面这样的纯值类型组件：

- `ExecutionContextComponent`
- `SkillExecutionContextComponent`
- `BulletExecutionContextComponent`

是否拆成多个具体组件可以后续再定，但物理原则必须先明确：

> **VM 的实例运行状态必须直接映射为 ECS 纯值类型组件。**

这是为什么快照 / 回滚可以退化为 `memcpy` 的前提。

---

### 3.2 组件职责

`ExecutionContextComponent` 的职责不是描述业务语义，而是承载 VM 的物理运行状态。

它应只负责：

- 指向当前脚本 ROM；
- 记录当前执行位置；
- 记录等待状态；
- 保存寄存器；
- 保存固定深度调用信息；
- 保存 Cleanup 栈 / 清理入口信息；
- 保存少量运行期标志位。

它不应承担：

- 动态列表管理；
- 宿主对象引用缓存；
- 复杂事件监听对象；
- 业务层任意扩展字段；
- 依赖 GC 的临时容器。

---

### 3.3 推荐的最小结构草案

下面是运行时布局的**伪代码草案**，目的是钉死字段形状，不是立即可编译的最终代码：

```csharp
public unsafe struct ExecutionContextComponent
{
    // 指向静态脚本资产的轻量引用
    public int ProgramId;

    // 当前执行位置
    public int InstructionPointer;

    // 挂起计数，0 表示当前不在 Wait 中
    public int WaitFrames;

    // 运行状态位，例如 Active / Completed / Killed / InCleanup
    public uint StateFlags;

    // 当前调用深度
    public byte CallDepth;

    // 当前 Cleanup 深度
    public byte CleanupDepth;

    // 预留字节，用于对齐或未来扩展
    public ushort Reserved;

    // 固定深度调用栈
    public fixed VMCallFrame CallFrames[VMConstants.MaxCallDepth];

    // 固定深度 Cleanup 栈
    public fixed VMCleanupFrame CleanupFrames[VMConstants.MaxCleanupDepth];

    // 固定大小寄存器区
    public fixed VMSlot Registers[VMConstants.MaxRegisterCount];
}
```

重点不是字段名本身，而是以下约束：

- 全部字段必须是定长、可复制、无托管引用的；
- 运行时状态必须一次性落在 ECS 组件中；
- 调用栈与 Cleanup 栈都必须是**固定深度数组**；
- 寄存器区必须是**固定大小槽位数组**；
- 不允许运行期扩容。

---

## 四、VMSlot：统一物理槽位

### 4.1 为什么必须有 VMSlot

语言层允许出现：

- `int`
- `float`
- `bool`
- 枚举
- 句柄
- 结构体字段拍平后的标量槽位

但在 VM 物理层，不能让寄存器区变成一个“托管泛型容器”。

因此需要统一的物理槽位类型：`VMSlot`。

它的职责是：

- 把不同脚本标量类型压平到统一槽位中；
- 保持寄存器数组的连续内存布局；
- 保证状态可整体复制；
- 为结构体拍平和寄存器分配提供固定物理基础。

---

### 4.2 推荐的物理语义

`VMSlot` 应被设计成一个**显式布局联合体**，只容纳固定宽度值，不容纳托管引用。

这里建议直接将 `VMSlot` 的**基础物理位宽固定为 64 位（8 字节）**。这意味着：

- 所有寄存器槽位统一按 8 字节对齐；
- Handle 统一按 64 位处理，避免与宿主 ECS 的 EntityId、句柄或高精度时间戳交互时发生截断；
- Syscall ABI 可以围绕统一 64 位槽位设计，避免高低位拆分与拼装；
- 用少量 padding 成本换取更稳定的宿主兼容性与更低的长期维护风险。

推荐支持的物理视图：

- `int`
- `uint`
- `float`
- `bool`（底层仍走整数位表达）
- `long`
- `ulong`
- `double`
- `handle64`

伪代码示意：

```csharp
[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct VMSlot
{
    // 32 位视图
    [FieldOffset(0)] public int Int32;
    [FieldOffset(0)] public uint UInt32;
    [FieldOffset(0)] public float Float32;

    // 64 位视图
    [FieldOffset(0)] public long Int64;
    [FieldOffset(0)] public ulong UInt64;
    [FieldOffset(0)] public double Float64;

    // 句柄视图
    [FieldOffset(0)] public ulong Handle;
}
```

这里要明确区分两层含义：

- **物理层**：统一使用 64 位槽位；
- **语言语义层**：不等于脚本 v1 必须全面开放 `Int64` / `Double` 作为常规业务类型。

也就是说，物理 64 位是为了宿主兼容性和 ABI 稳定性；语言层是否默认暴露全部 64 位数值能力，可以作为后续语义设计单独决定。

---

### 4.3 VMSlot 的禁止事项

`VMSlot` 中**绝对不允许**出现：

- `object`
- `string`
- `List<T>`
- `Array`
- 委托
- 闭包环境
- 任意托管引用
- 不定长结构

换句话说：

> `VMSlot` 是物理槽位，不是小型动态对象系统。

---

## 五、寄存器布局规则

### 5.1 固定上限，而不是运行期扩容

寄存器数量不能由脚本运行时决定，必须由编译结果或 VM 常量提前决定。

推荐方式：

- 全局 VM 常量固定一个上限，例如 `32 / 64 / 128` 个槽位；
- 编译器为每个程序计算 `RequiredRegisterCount`；
- 若编译结果超过上限，则编译期直接报错；
- 运行时始终分配固定大小寄存器区。

这是一种典型的“用一点 padding 浪费换确定性”的策略。

---

### 5.2 结构体必须拍平成槽位区

语言层允许结构体，只是为了作者表达舒服。

但在运行时：

- 结构体不作为对象存在；
- 结构体不作为堆实例存在；
- 结构体必须被编译器拍平为连续寄存器槽位；
- 结构体传值本质上是复制一段槽位区。

例如：

```text
struct DamageInfo
{
    int level;
    float ratio;
    ulong targetHandle;
}
```

在运行时更接近：

```text
r10 = level
r11 = ratio
r12 = targetHandle64
```

而不是一个对象引用。

---

### 5.3 生命周期分析与寄存器复用

编译器应尽量区分：

- 跨 `await` 持续存活的变量；
- 不跨 `await` 的临时局部变量。

基本策略：

- 跨 `await` 变量必须占用稳定寄存器槽位；
- 短生命周期临时量可复用寄存器；
- 但这种优化不能破坏调试映射与 Save/Load 一致性。

也就是说，寄存器复用是编译器优化，不应改变 VM 的物理纪律。

---

## 六、调用栈：有限深度，而不是无限栈

### 6.1 为什么不能使用无限调用栈

无限调用栈通常意味着：

- 动态分配；
- 更复杂的快照内容；
- 更高的不确定性；
- 更容易在长线业务演化中偷偷退回“高级语言运行时”。

这与当前目标冲突。

---

### 6.2 推荐方案：固定深度调用栈

如果 VM 允许函数调用，则调用信息也必须是定长结构。

推荐定义：

```csharp
public struct VMCallFrame
{
    public int ReturnIP;
    public short BaseRegister;
    public short Reserved;
}
```

然后在 `ExecutionContextComponent` 中使用固定深度数组：

```csharp
fixed VMCallFrame CallFrames[MaxCallDepth];
```

约束：

- `MaxCallDepth` 是 VM 常量；
- 超出深度时编译期优先报错，必要时运行时防御报错；
- 不允许以动态容器承载调用帧。

---

### 6.3 纯逻辑快速路径

为了减轻调用栈压力，可允许：

- 编译期内联简单 `[Flow]` / 可挂起函数；
- 对不含 `await` 的纯逻辑块，走宿主快速执行路径；
- 但这不应破坏调试、Cleanup 与语义一致性。

换句话说：

- 有限栈是底线；
- 内联和快速路径是优化手段；
- 不能靠“反正以后优化”来模糊物理边界。

---

## 七、Cleanup 栈：强制中断前的状态安全防线

### 7.1 为什么 Cleanup 必须进入运行时布局

偏瞬态 VM 没有对象析构函数。

如果没有显式 Cleanup 栈，那么以下场景就会出问题：

- 技能写入黑板状态后被打断；
- 播放中的表现状态需要回收；
- 宿主句柄需要归还；
- Buff 附加的短期标志未重置。

因此 Cleanup 不是语法细节，而是运行时布局的一部分。

---

### 7.2 推荐结构

推荐将 Cleanup 也建模为固定深度帧：

```csharp
public struct VMCleanupFrame
{
    public int CleanupEntryIP;
    public short BaseRegister;
    public short Reserved;
}
```

在 `ExecutionContextComponent` 中固定存放：

```csharp
fixed VMCleanupFrame CleanupFrames[VMConstants.MaxCleanupDepth];
```

它的意义是：

- 每进入一个需要注册清理逻辑的作用域，就压入一帧；
- 正常离开作用域时弹出；
- 被强制中断时，VM 进入 Cleanup 模式，按后进先出执行清理入口；
- 所有 Cleanup 执行完毕后，才能真正回收上下文或标记实例结束。

---

### 7.3 Cleanup 的硬约束

- Cleanup 栈必须定长；
- Cleanup 注册必须可快照、可恢复；
- Cleanup 执行不能依赖宿主异常机制；
- 强制 Kill 不得跳过 Cleanup；
- Cleanup 本身也必须遵守零 GC、句柄化、Syscall 边界纪律。

---

## 八、句柄与宿主临时内存池

### 8.1 为什么句柄必须进入运行时模型

脚本业务不可避免会产生一些复杂临时结果，例如：

- 目标列表；
- 批量筛选结果；
- 空间查询结果；
- 特定宿主批处理上下文。

这些东西不能直接进寄存器，也不能直接挂在 ECS RAM 上。

因此运行时必须承认：

> **复杂临时数据存在，但它们只能存在于宿主侧，并通过 `Handle64` 与 VM 交互。**

---

### 8.2 推荐规则

- Handle 在 VM 中本质就是一个 `ulong` 槽位视图；
- Handle 指向宿主帧级内存池或句柄表中的临时对象；
- Handle 生命周期由宿主管理；
- Handle 无业务语义，只是索引或票据；
- 若脚本跨帧持有 Handle，必须额外定义它的稳定性规则；默认应优先避免跨帧持有短命 Handle。

---

### 8.3 推荐的最小宿主协议

宿主至少需要提供类似下面的能力：

- `CreateTargetGroupHandle(...)`
- `FilterTargetGroupHandle(...)`
- `ReleaseHandle(ulong handle)`
- `BatchApplyDamage(ulong handle, ...)`
- `TryResolveHandleType(ulong handle)`

具体名字未来可调整，但原则不可变：

- VM 只看见 64 位句柄值；
- 宿主负责真实数据；
- 批处理在宿主执行；
- 句柄泄漏必须可追踪、可清理。

---

## 九、快照 / 回滚边界

### 9.1 什么必须进入快照

下列内容必须天然纳入实例快照：

- `InstructionPointer`
- `WaitFrames`
- `StateFlags`
- `CallDepth`
- `CleanupDepth`
- `CallFrames[]`
- `CleanupFrames[]`
- `Registers[]`

也就是说，只要它属于 `ExecutionContextComponent`，原则上就应该天然可被 ECS 快照系统整体复制。

---

### 9.2 什么不应直接进入快照

以下内容通常不应直接复制进实例快照：

- ROM 静态字节码本体；
- 调试符号；
- 宿主托管对象；
- 帧级短命临时列表；
- 句柄背后的具体复杂结构。

这类内容应通过：

- 静态资源查表；
- 宿主系统重建；
- 或句柄再解析策略；

来解决，而不是偷偷塞进快照对象中。

---

## 十、推荐常量集合

为避免上层设计无限膨胀，建议尽早设定一组 VM 常量上限，例如：

```csharp
public static class VMConstants
{
    public const int MaxRegisterCount = 64;
    public const int MaxCallDepth = 4;
    public const int MaxCleanupDepth = 8;
}
```

这组数字未来可以调整，但**“存在固定上限”**这件事必须先定死。

因为只有这样，以下讨论才有锚点：

- 这个 AST 会不会把寄存器打爆？
- 这个调用链会不会超出固定深度？
- 这个业务要不要改写成 Syscall？
- 这个 Cleanup 设计会不会爆栈？

---

## 十一、当前阶段的最小可运行锚点

在进入指令集和 AST 设计前，建议先把以下最小锚点变成代码：

1. `VMSlot` 定义
2. `VMCallFrame` 定义
3. `VMCleanupFrame` 定义
4. `ExecutionContextComponent` 定义
5. 一个最小 `ProgramId + IP + Wait + Registers` 的 Save/Load 测试
6. 一个最小 tracer bullet：
   - 开始
   - `wait 10`
   - syscall 播放特效
   - 结束
7. 一个强制 Kill 触发 Cleanup 的测试
8. 一个验证 0 GC 的回归测试

只要这几个锚点没跑通，就不应该继续扩 DSL 和业务铺量。

---

## 十二、最终约束结论

这份运行时布局文档的最终目的只有一个：

> 在讨论语法、AST、字节码、业务之前，先把 VM 的“物理身体”钉死。

因此后续任何设计，都必须服从以下判断顺序：

1. 它是否能放进 `ExecutionContextComponent` 这类纯值类型 ECS RAM？
2. 它是否仍然只让寄存器持有标量值和 64 位句柄？
3. 它是否仍然允许 Save/Load 接近 `memcpy`？
4. 它是否仍然支持强制中断前的 Cleanup？
5. 它是否仍然保持 ROM / RAM、VM / Syscall 的物理边界？

只要有任意一项答案是否定的，该设计就不应该继续推进。