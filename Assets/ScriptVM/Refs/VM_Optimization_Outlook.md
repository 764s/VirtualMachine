# FFVM 性能优化展望

> 本文基于当前 FFVM 实现（Step 6 完成、编译器流水线可用）的代码分析，梳理在**不改变功能语义**前提下的性能优化方向。按预期收益从高到低分层排列。
>
> 相关文档：[VM_Summary.md](../VM_Summary.md)（总览）

---

## 当前性能基线

| 基准 | 含义 | 当前 Ratio |
|------|------|-----------|
| V3（手写字节码） | VM 解释器开销下限 | **1.7x** vs C# |
| V3-B（编译脚本 B01-B05） | 开发者写脚本时的真实性能 | **5-7x** vs C# |

目标：通过以下优化，将编译脚本降至 **2-3x**，手写字节码逼近 **1.2-1.4x**。

---

## Tier 1：解释器热路径（影响最大）

### O1. 消除逐次 `fixed` 钉住 — 最大单一瓶颈

**现状**：每次 `Registers.Get(i)` / `.Set(i, v)` 都执行一次 `fixed (Number* ptr = &R00)` 钉住 + 解钉。

```csharp
// 当前：一条 ADD 触发 3 次 pin/unpin
case OpCode.ADD:
    inst.Registers.Set(op.A,
        inst.Registers.Get(op.B) + inst.Registers.Get(op.C));
```

在 IL2CPP 上每次 pin 生成 GC handle 操作。一条 ALU 指令（ADD/MUL/CMP 等）触发 **3 次 fixed pin/unpin**（2 读 + 1 写），在解释循环中这是单条指令开销占比最高的部分。

**优化方案**：在 `ExecuteInstance` 入口钉住一次，整个循环共享同一个 `Number*`：

```csharp
fixed (Number* regs = &inst.Registers.R00)
{
    while (steps < MaxStepsPerTick)
    {
        // ...
        case OpCode.ADD:
            regs[op.A] = regs[op.B] + regs[op.C];
            break;
    }
}
```

**预估收益**：dispatch 整体 **30-50%** 加速。

**复杂度**：低。仅改 `ExecuteInstance` 一个方法。

---

### O2. OpCode 连续编号 → 强制跳转表

**现状**：OpCode 枚举值为 `0-6, 10, 20-22, 30-34, 40-45, 50-53`，跨度 0-53，共 28 个值。

C# 编译器对稀疏 enum switch 倾向生成**二分查找**（4-5 次比较）而非 O(1) 跳转表。这是解释循环中每条指令都必经的开销。

**优化方案**：重编号为 0-27 连续值。JIT/IL2CPP 对 ≤64 连续值保证生成 jump table。

```csharp
public enum OpCode : byte
{
    NOP = 0, LOAD_CONST = 1, SYSCALL = 2, WAIT = 3,
    PUSH_CLEANUP = 4, POP_CLEANUP = 5, RETURN = 6,
    MOVE = 7,
    JUMP = 8, JUMP_IF_ZERO = 9, JUMP_IF_NOT_ZERO = 10,
    ADD = 11, SUB = 12, MUL = 13, DIV = 14, MOD = 15,
    CMP_EQ = 16, CMP_NEQ = 17, CMP_LT = 18, CMP_LTE = 19, CMP_GT = 20, CMP_GTE = 21,
    AND = 22, OR = 23, NOT = 24, NEG = 25,
    // 未来扩展从 26 开始
}
```

**预估收益**：dispatch 分支 **~20%** 加速。

**复杂度**：低。修改枚举值 + 重新编译所有已有字节码（编译器自动重新生成，无功能变化）。

**注意**：需同步更新所有测试中手写的字节码。

---

### O3. 消除冗余 IP 边界检查

**现状**：

```csharp
if (inst.IP < 0 || inst.IP >= code.Length)   // 手动检查（分支 1）
    ...
ref Instruction op = ref code[inst.IP];       // CLR 数组索引检查（分支 2）
```

每条指令付出两次越界检查。IP 来源可信（编译器保证、JUMP 目标在编译期确定），冗余。

**优化方案**：

- 方案 A：去掉手动检查，保留 CLR 数组边界检查作为兜底
- 方案 B：用 `Unsafe.Add<Instruction>(ref code[0], inst.IP)` 跳过 CLR 检查（需配合编译器保证 IP 合法）

**预估收益**：热路径 **~5-10%**。

**复杂度**：极低。

---

## Tier 2：编译器优化（减少生成指令数）

### O4. 目标寄存器传递（dest-reg hint）

**现状**：`CompileExpr` 总是 `AllocTemp()` → `LOAD_CONST temp` → 调用方再 `MOVE temp → varReg`。

```
// var x: int = 42 当前生成：
LOAD_CONST r48, [42]    // temp
MOVE r16, r48           // var x = temp
```

**优化方案**：给 `CompileExpr` 传入可选 `destReg` 提示。当提供时，直接 emit 到目标寄存器：

```
// 优化后：
LOAD_CONST r16, [42]    // 直接写入 var x
```

2 条 → 1 条。所有字面量赋值、简单表达式赋值均受益。

**预估收益**：典型脚本指令数减少 **~15-20%**。

**复杂度**：中。需修改 `CompileExpr` 签名并在各 expr 分支中处理 hint。

---

### O5. 常量折叠（Constant Folding）

**现状**：

```
// 脚本: var x: int = 3 + 5
// 当前生成 3 条指令：
LOAD_CONST r48, [3]
LOAD_CONST r49, [5]
ADD r50, r48, r49
```

**优化方案**：在 AST 层或 emit 层检测两侧均为常量时，直接折叠：

```
// 优化后：1 条指令
LOAD_CONST r48, [8]
```

3 条 → 1 条。

**预估收益**：取决于脚本中常量表达式比例。典型受益：循环边界、初始值、Syscall 参数等。

**复杂度**：低。在 `CompileExpr(BinaryExpr)` 入口检测即可。

---

### O6. Peephole 优化 pass

在 emit 完成后扫描字节码数组，消除冗余模式：

| 模式 | 优化为 | 场景 |
|------|--------|------|
| `MOVE rA, rA` | 删除 | 编译器偶尔生成的自赋值 |
| `LOAD_CONST rX, 0` → `JUMP_IF_ZERO rX` | `JUMP` | 始终成立的条件跳转 |
| `MOVE rA, rB` → `MOVE rB, rA` | 删除第二条 | 冗余回拷 |
| `LOAD_CONST rX, V` → `MOVE rY, rX`（rX 不再使用）| `LOAD_CONST rY, V` | 常见 temp→var 模式 |

**预估收益**：**~5-10%** 指令数减少（与 O4 互补）。

**复杂度**：中。需要实现 post-emit scan + 指令删除/替换 + 跳转目标重新计算。

---

### O7. Syscall 结果直达

**现状**：`CompileSyscallExpr` 在 SYSCALL 后总是 `MOVE r0 → temp`，如调用方存入变量再 `MOVE temp → var`：

```
SYSCALL FindEnemy        // 结果在 r0
MOVE r48, r0             // temp = r0
MOVE r16, r48            // var target = temp
```

**优化方案**：结合 O4 的 dest-reg hint，Syscall 后直接 `MOVE r0 → var`（1 条），或未来支持 Syscall 指定结果寄存器（0 条额外 MOVE）。

**预估收益**：每个带返回值的 Syscall 调用减少 1-2 条指令。

---

## Tier 3：指令编码（L1 缓存友好）

### O8. 指令压缩 16B → 4B

**现状**：

```csharp
struct Instruction {
    OpCode Code;  // 1 byte
    // 3 bytes padding
    int A;        // 4 bytes — 实际只用 6 bits（寄存器索引 0-63）
    int B;        // 4 bytes
    int C;        // 4 bytes
}               // 总计 16 bytes
```

操作数实际范围：

| 操作数 | 最大值 | 所需位数 |
|--------|--------|---------|
| 寄存器索引 | 63 | 6 bits |
| 常量索引 | ~50（典型） | 8 bits 足够 |
| 跳转目标 IP | ~500（典型） | 16 bits 足够 |

**优化方案**：4 字节紧凑编码：

```
[8-bit opcode][8-bit A][8-bit B][8-bit C]
```

- 4× 更小的指令流 → 更好的 L1 icache 利用率
- B05 基准（16 条指令）：从 256B → 64B，完全落入一条缓存行
- 对大型脚本（数百条指令）的缓存友好性提升显著

**代价**：

- 操作数 > 255 需要 `LOAD_CONST_WIDE` 等宽指令变体
- 跳转目标 > 255 需要 `JUMP_WIDE` 或双字节编码
- 已有测试中手写字节码需要适配

**预估收益**：指令缓存命中率提升，大脚本场景可获 **10-20%** 加速。小脚本（B01-B05 级别）收益有限。

**复杂度**：高。涉及 `Instruction` 重构、编译器 emit 逻辑、所有 OpCode switch 路径、所有手写测试。

---

## Tier 4：调度层优化

### O9. 活跃实例链表

**现状**：`Tick()` 扫描全部 128 个 slot：

```csharp
for (int i = 0; i < VMConstants.MaxInstances; i++)
{
    ref VMInstanceState inst = ref Pool.Instances[i];
    if (!inst.IsAlive || ...) continue;  // 大部分检查浪费
    ...
}
```

即使只有 3 个实例存活，也遍历 128 次。

**优化方案**：维护活跃实例索引列表：

```csharp
int[] _activeList;  // 预分配 128
int _activeCount;
```

`Spawn` 时追加，`Destroy/Complete` 时 swap-remove。`Tick` 只遍历 `_activeCount` 个。

**预估收益**：与实例稀疏度成正比。典型战斗场景（10-20 活跃 / 128 总）减少 **~85%** 无效遍历。

**复杂度**：低。需确保 Save/Load 一致性（快照需包含 activeList）。

---

### O10. 快照只拷贝活跃实例

**现状**：`SaveState` memcpy 全部 128 个 `VMInstanceState`（128 × ~768B ≈ **98KB**）。

**优化方案**：只拷贝活跃实例 + 活跃列表元数据。典型 10-20 个活跃实例 → **~7.5-15KB**，减少 **80-90%** 快照开销。

**复杂度**：中。需修改 `SnapshotRingBuffer` 数据结构，存储稀疏快照 + 索引映射。需确保 `LoadState` 能正确恢复（包括清零已销毁实例的 slot）。

---

## Tier 5：长期 / 低优先级

| 编号 | 方向 | 内容 | 预估收益 | 复杂度 |
|------|------|------|---------|--------|
| O11 | Syscall 函数指针 | `delegate` → `delegate*`（C# 9 unsafe），消除委托间接调用开销 | Syscall 调用 ~30% 加速 | 中 |
| O12 | Number 原始字段比较 | `== Number.Zero` 走 `==` 运算符（有分支），改为直接 `RawDouble == 0.0` | 比较指令 ~10% 加速 | 低 |
| O13 | 热/冷字段分离 | `VMInstanceState` 将高频字段（`IP`、`Registers`、`ErrorFlag`）与低频字段（`CleanupStack`、`WaitTargetInstanceId`）分离为两个 struct | 缓存行利用率提升 | 高（影响快照布局）|
| O14 | Fix64 SIMD 加速 | 用 `System.Runtime.Intrinsics` 做 128-bit 乘法，替代当前 6 次 64-bit 乘法拆分 | Fix64 乘法 ~2x 加速 | 中（平台相关）|

---

## 优化收益综合预估

### 按层级

| Tier | 优化项 | 目标 | 复杂度 |
|------|--------|------|--------|
| **Tier 1** | O1 + O2 + O3 | 解释器 dispatch 提速 40-60% | 低 |
| **Tier 2** | O4 + O5 + O6 + O7 | 编译指令数减少 15-25% | 中 |
| **Tier 3** | O8 | 指令缓存友好，大脚本 10-20% | 高 |
| **Tier 4** | O9 + O10 | 调度/快照开销按稀疏度大幅降低 | 低-中 |

### 对基准的影响预估

| 基准 | 当前 | Tier 1 后 | + Tier 2 后 | + Tier 3 后 |
|------|------|----------|------------|------------|
| V3（手写字节码） | 1.7x | ~1.2x | ~1.2x（不涉及编译器） | ~1.1x |
| V3-B（编译脚本） | 5-7x | 3-4x | **2-3x** | ~2x |

### 推荐实施顺序

```
O1（固定 pin）→ O2（连续 OpCode）→ O4（dest-reg hint）→ O5（常量折叠）
    ↓                                      ↓
  benchmark 验证（期望 3-4x）          benchmark 验证（期望 2-3x）
    ↓                                      ↓
O3（边界检查）→ O9（活跃列表）→ O6（peephole）→ 视需要 O8/O10
```

**核心原则**：每做一项就跑 benchmark 验证，用数据驱动后续优化决策。避免过早投入高复杂度优化（O8、O13）。

---

## 约束提醒

所有优化必须遵守 VM 架构硬约束（VM_Architecture_Rules.md §20 条）：

- ✅ 零 GC（不引入托管分配）
- ✅ memcpy 快照/回滚（不破坏 blittable 布局）
- ✅ 确定性执行（不引入平台相关分支差异——O14 需特别注意）
- ✅ Syscall 边界不变（O11 仅改内部调用机制，API 不变）
