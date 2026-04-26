# D_VM_ObjectModel_Transition

状态: 💬 讨论中（架构转向进行中）

## 背景

当前正在进行一个重要架构转向：将 VM 的使用心智统一为“普通 C# 对象”，并保留 VM 特有的 yield 语义。

目标不是改变 VM 物理本质，而是统一调用模型与宿主心智，降低使用复杂度。

## 结论

VMData 和 CPUData 是为迎合心智模型作的分离, 除非将来出现性能问题, 否则将继续保持此分离方式来帮助理解.

初版期待: VMData 作为重要持久数. CPUData 作为临时运行数据.
实际情况: 在YieldCall下, 部分 CPUData 需要作为持久数据.

1. CPUData 只用于虚拟机执行的读写, 外部不可见. CPUData 必须是 class 池化或 ref struct 借用.
2. VMData 在任何调用类型下都可以被宿主直接读写
3. Yield阻断任何重入, 此项为实例级约束
4. 隐式临时 CPUData 池
5. ReadOnlyCall 拒绝任何写入. (当然除了对临时隐式CPUData的写入)
6. StaticReadOnlyCall 同cs 的 纯 static function, 赞同你的说法, 但它允许访问 constant 字段
7. Arguments, ReturnSlot 对外分离, 对内统一
8. HostBindings 暂定不可切换
9. Span<Number> 作为cs和vm的数据交流形式, 未来用类似 ByteWriterReader 配合约定来实现更复杂的数据映射 


```cs
struct InstanceHandle { int id; }

class InstancePool
{
    VMData[]       vmDatas;
    CPUDataHandle[] cpuHandles;
    VMDef[]        defs;
    HostBindings[] bindings;
}


readonly struct VMInstance
{
    readonly InstancePool pool;
    readonly int id;

    public ref VMData Data => ref pool.GetVMData(id);
    public VMDef Def => pool.GetDef(id);
    public HostBindings Bindings { get; set; }  // 通过 pool 访问
    // CPUData 不暴露
}

static class VMEngie
{
	static YieldCall(MethodHandle, Arguments, ReturnSlot, CPUData, VMData, VMDef); // 可Yieldd调用
	static Call(MethodHandle, Arguments, ReturnSlot, VMData, VMDef); // 不可Yield调用. 隐式 CPUData 池
	ReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMDef, VMData) // 隐式 CPUData
	StaticReadOnlyCall(MethodHandle, Arguments, ReturnSlot, VMDef) // // 隐式 CPUData 池
}

ref struct Arguments
{
    Span<Number> slots;      // 长度 = 入参数量，约定布局
}

ref struct ReturnSlot
{
    Span<Number> slots;      // 长度 = 返回值数量
}
```

调用示例
```cs
// 调用者使用
Span<Number> argBuf = stackalloc Number[2];
argBuf[0] = Number.FromInt(playerId);
argBuf[1] = Number.FromFix(damage);

Span<Number> retBuf = stackalloc Number[1];

VMEngine.Call(handle, new Arguments(argBuf), new ReturnSlot(retBuf), vmData, vmDef);

int result = retBuf[0].AsInt();
```


## 转向目标

1. 将 VM 实例视为对象实例（vmdata）
2. 将 VMProgram 视为对象定义（vmdefinition）
3. 方法调用语义向对象方法靠拢
4. 明确区分“普通调用”与“yield continuation 恢复”

## 已确认共识

1. VMInstanceState 基本对应 vmdata（扩展寄存器位于 InstancePool.ExtendedRegs）
2. VMProgram + VMModuleTable 对应 vmdefinition
3. 安全红线是 continuation 所有权，不是“是否允许副作用”本身
4. 允许更宽松对象语义，但必须约束同实例 continuation 冲突

## 已放弃方向

+ 放弃使用 CurrentIP 进行分桶
    1. 执行顺序会难以预料
    2. 收益不稳定
    3. 主流 async 设计没有实现此类策略

## 关键约束（当前草案）

1. 单活跃 continuation：同实例同一时刻只能有一个活跃 yield 链
2. continuation 恢复必须串行
3. 非 continuation 调用可允许重入与写入（按策略）
4. same-continuation 重入需明确策略：Reject 或 Queue
5. 调用契约需区分：普通调用 / 查询调用 / continuation 恢复

## 性能天花板参考（排除实现低效）

说明：以下为架构级理论区间，用于指导后续实现方式与验收阈值，不作为当前实现实测值。

基线：

- C# 方法调用基线：约 3.84 ns/次（B06）
- VM B06 当前实测参考：约 136 ns/次（35.53x）

### 固有成本分解

“原始需求成本” 指调用语义本身不可消除的最小开销。
“实现方案成本” 指本设计选择（handle 化 + Span 中介 + 池借用 + ref struct ABI）引入的额外固定成本。

| 成本项 | 原始需求成本 | 实现方案成本 | 说明 |
|--------|-------------|-------------|------|
| MethodHandle 解引用 | 0.5-1 ns | — | 已缓存的稳定信息 |
| 参数寄存器写入 / 返回值读取 | 0.3-0.5 ns/字段 | — | 任何 ABI 都需要 |
| 解释器分派与栈帧切换 | 8-20 ns | — | 解释执行不可避免 |
| Yield 挂起 + 恢复一次 | 50-180 ns | — | 持久 CPUData 状态保留 |
| `Arguments` / `ReturnSlot` ref struct 包装 | — | 1-2 ns | Span 包装与边界检查 |
| `stackalloc Number[N]` | — | 0.5-1 ns | 栈帧上展开 |
| `InstancePool` handle 解引用（VMData / VMDef） | — | 1-2 ns | 数组索引 + 引用读取 |
| 隐式 CPUData 池借用 + 归还 | — | 5-15 ns | freelist + Reset；非 yield 调用专用 |
| 持久 CPUData 借用（一次 yield 周期内一次） | — | 2-5 ns | 仅 YieldCall 首次进入 |
| HostBindings 间接访问 | — | 0-1 ns | 多数调用不触发 |

### 单次调用天花板

| 调用类型 | 原始需求天花板 | 加方案成本后天花板 | 相对 C# 倍率 |
|---------|---------------|------------------|-------------|
| StaticReadOnlyCall | 12-25 ns | **15-31 ns** | 3.9x-8.1x |
| ReadOnlyCall | 15-35 ns | **18-41 ns** | 4.7x-10.7x |
| Call | 18-45 ns | **21-51 ns** | 5.5x-13.3x |
| YieldCall（一次挂起+恢复周期） | 80-220 ns | **85-230 ns** | 22.1x-59.9x |

### Batch 摊销天花板

batch 场景下 CPUData 借用、stackalloc、handle 解引用可在批内复用，方案成本基本被摊薄。YieldCall 不参与 batch 摊销（continuation 不能共享借用）。

| 调用类型 | 单次新天花板 | batch=64 摊销后 | 备注 |
|---------|-------------|---------------|------|
| StaticReadOnlyCall | 15-31 ns | **13-26 ns** | 接近原始需求成本 |
| ReadOnlyCall | 18-41 ns | **15-35 ns** | 接近原始需求成本 |
| Call | 21-51 ns | **18-45 ns** | 接近原始需求成本 |
| YieldCall | 85-230 ns | 不适用 | continuation 不能共享借用 |

### 解读

1. 方案成本主要集中在 `Call` / `ReadOnlyCall` 的 CPUData 池借用，单次抬升 5-15%。
2. YieldCall 因周期本身较大，方案成本占比 < 5%。
3. Batch 场景下基本回到原始需求天花板，反向印证 Span<Number> + handle 化 + 单引擎多上下文的设计。
4. 后续可选优化：StaticReadOnlyCall 配专用轻量 CPUData 池，将其下界回压到 13-15 ns；不在主线。

## 对 VM_Summary 的影响

当前 VM_Summary 中与“固定单入口启动心智”相关的叙述，后续将按新模型做一轮整理：

1. 保留与物理约束一致的内容
2. 删除或改写与新对象化心智冲突的部分
3. 新增“普通调用 vs continuation 恢复”的契约章节

## 完成条件（本讨论）

本讨论在满足以下条件后转为 ✅ 已完成讨论：

1. Host 调用契约定稿（调用类型、重入、yield、错误语义）
2. 性能目标定稿（普通调用 SLA、continuation SLA）
3. VM_Summary 完成对应整理并移除冲突叙述

---

## 讨论区（进行中）

- 本文件为当前转向讨论的单点固化文档。
- 后续讨论应优先直接修改本文件，而非分散到临时对话。