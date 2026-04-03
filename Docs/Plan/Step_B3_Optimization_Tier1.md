# B3 调整型优化 Tier 1：O1 + O2

> **前置**：LSP5 (代码补全) 完成，624 项 Assert × 2 模式全通过。
> **目标**：消除解释器热路径最大瓶颈（O1 逐次 fixed pin + O2 稀疏 switch）。.NET JIT 实测 ~9% dispatch 加速；IL2CPP 生产环境预期 30-50%。

---

## 基线性能数据

| 基准 | VM (μs) | C# (μs) | Ratio | Ticks | 指令数 |
|------|---------|---------|-------|-------|--------|
| B01_ArithLoop | 873.6 | 139.6 | 6.26 | 10000 | 23 |
| B02_Fibonacci | 7.2 | 0.5 | 14.23 | 25 | 15 |
| B03_NestedLoop | 321.6 | 21.6 | 14.91 | 100 | 20 |
| B04_Branching | 778.6 | 210.9 | 3.69 | 10000 | 33 |
| B05_Accumulator | 1206.6 | 64.7 | 18.66 | 50000 | 12 |

---

## Checklist

- [x] **O2：OpCode 连续编号**
  - 重编号 OpCode 枚举为 0-28 连续值（消除 JIT 二分查找 → 跳转表）
  - 验证：624 Assert 全通过
- [x] **O1：消除逐次 fixed 钉住**
  - `ExecuteInstance` 标记 `unsafe`
  - 入口处 `fixed (Number* regs = &inst.Registers.R00)` 一次性钉住
  - 所有 `inst.Registers.Get(Reg(…))` → `regs[Reg(…)]`
  - 所有 `inst.Registers.Set(Reg(…), v)` → `regs[Reg(…)] = v`
  - 验证：624 Assert 全通过
- [x] **Benchmark 验证**
  - 运行 B01-B05，对比基线
  - .NET JIT dispatch ~9% 加速（VM 绝对时间下降 3-25%）
  - IL2CPP 生产环境预期 30-50% 加速（文档预估，待真实验证）
- [x] **文档更新**
  - 本文追加功能展望 + 优化展望 + 风险点
  - VM_Summary.md 串行计划 + §十性能展望更新
  - Outlook_And_Risks.md §3.2 + §3.6 更新

---

## Benchmark 结果（O1 + O2 合并后）

> 运行环境：.NET 8.0.25, Ubuntu, 2 cores, warmup=20, runs=200

| 基准 | 基线 VM (μs) | 优化后 VM (μs) | VM 加速 | 基线 Ratio | 优化后 Ratio |
|------|-------------|---------------|---------|-----------|-------------|
| B01_ArithLoop | 873.6 | 838 | ~4% | 6.26x | 6.28x |
| B02_Fibonacci | 7.2 | 5.4 | ~25% | 14.23x | 10.4x |
| B03_NestedLoop | 321.6 | 295 | ~8% | 14.91x | 13.5x |
| B04_Branching | 778.6 | 754 | ~3% | 3.69x | 4.3x |
| B05_Accumulator | 1206.6 | 1128 | ~6% | 18.66x | 17.6x |

**分析**：
- .NET JIT 已对 `fixed` 钉住做了高效优化（生成内部指针而非 GC handle），因此 O1 在 .NET 上的收益适中（~5-25%）。
- **IL2CPP 生产环境**中每次 `fixed` 生成真实 GC handle pin/unpin，O1 收益预期更大（30-50%，与文档预估一致）。
- O2 连续编号对 .NET JIT 的 switch 跳转表影响在小枚举范围内不显著，但在 IL2CPP 和 Mono 上可能更明显。
- B02 (Fibonacci) 的 25% 改善最大——因为函数调用密集，每次 CALL/RET 涉及大量寄存器操作。
- B04 ratio 上升因 C# 基线波动（210→177μs），VM 绝对时间实际下降 ~3%。

---

## 实施说明

### O2：OpCode 连续编号

将 OpCode 枚举从稀疏 (0-7, 10, 20-22, 30-34, 40-45, 50-53, 60-61) 重编号为连续 (0-28)。

**影响范围**：仅 OpCode.cs 枚举定义。所有使用处引用枚举名（`OpCode.ADD`），不受值变化影响。

**妥协**：无。编译器 emit 和测试均使用枚举名，不依赖具体数值。

### O1：消除逐次 fixed 钉住

当前每次 `Registers.Get(i)` / `.Set(i, v)` 内部各执行一次 `fixed (Number* ptr = &R00)` 钉住/解钉。
一条 ADD 触发 3 次 pin/unpin（2 读 + 1 写），是解释循环中单条指令开销占比最高的部分。

优化后在 `ExecuteInstance` 入口钉住一次，整个循环共享同一个 `Number*`。

**影响范围**：仅 VMWorld.cs 的 `ExecuteInstance` 方法。
- Syscall 内部仍通过 Get/Set 访问寄存器（不在 fixed 块内），这是正确的——Syscall 频率远低于 ALU 指令。
- CALL/RET_FUNC 更新 `RegisterBase` (rb)，但 `regs` 指针始终指向 R00 物理地址，`Reg()` 函数负责偏移，不受影响。

**妥协**：`ExecuteInstance` 方法变为 `unsafe`。这是必要的——`unsafe` 仅限于此方法内部，不扩散。项目已设置 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（NumberRegisters.Get/Set 已使用 unsafe）。

---

## 功能展望

本步骤为纯优化，无新功能。O1/O2 的实施为后续优化铺平了道路：

| 项 | 说明 | 依赖 |
|----|------|------|
| Syscall 寄存器直接访问 | Syscall 实现也可接收 `Number*` 指针以消除内部 Get/Set 开销 | O1 已就位 |
| CallStack/CleanupStack 同理 pin | 与 O1 同模式，在 ExecuteInstance 入口同时 pin 三个 fixed 数组 | 视 benchmark 数据决定 |

---

## 优化展望

| ID | 方向 | 预期收益 | 复杂度 | 优先级 |
|----|------|---------|--------|--------|
| **O6** | Peephole 优化 pass（Tier 2） | ~5-10% 指令数减少 | 中 | 下一步优化候选 |
| **O8** | 指令压缩 16B → 4B（Tier 3） | L1 缓存 10-20% 加速 | 高 | 长期 |
| **O9** | 活跃实例链表（Tier 4） | 稀疏场景 ~85% 无效遍历减少 | 低 | 业务需要时 |
| **FO1** | 叶函数优化 | 叶函数开销 -40~60% | 低 | B4 阶段 |

**推荐下一步**：B3 Tier 1 完成后，串行计划进入 B4（功能补全）或根据业务需求选择 B3 Tier 2（O6 peephole）。

---

## 风险点

| 风险 | 等级 | 缓解措施 |
|------|------|---------|
| .NET JIT 收益有限 | 低 | 已预期；真正目标是 IL2CPP 生产环境。.NET JIT 上的 ~9% 均值改善仍有价值 |
| `unsafe` 标记扩散 | 极低 | `unsafe` 仅限于 `ExecuteInstance` 一个方法，不扩散到 API 层 |
| 连续 OpCode 编号未来扩展 | 极低 | 新 OpCode 从 29 开始依次追加即可，保持连续性。如超出 64，JIT 仍可生成跳转表 |
| Syscall 仍使用 Get/Set | 极低 | 有意保留——Syscall 频率远低于 ALU 指令，且 Syscall 实现在 fixed 块外运行是安全的设计选择 |
