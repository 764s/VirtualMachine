# B3 调整型优化 Tier 1：O1 + O2

> **前置**：LSP5 (代码补全) 完成，624 项 Assert × 2 模式全通过。
> **目标**：消除解释器热路径最大瓶颈（O1 逐次 fixed pin + O2 稀疏 switch），dispatch 加速 40-60%。

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

- [ ] **O2：OpCode 连续编号**
  - 重编号 OpCode 枚举为 0-28 连续值（消除 JIT 二分查找 → 跳转表）
  - 验证：624 Assert 全通过
- [ ] **O1：消除逐次 fixed 钉住**
  - `ExecuteInstance` 标记 `unsafe`
  - 入口处 `fixed (Number* regs = &inst.Registers.R00)` 一次性钉住
  - 所有 `inst.Registers.Get(Reg(…))` → `regs[Reg(…)]`
  - 所有 `inst.Registers.Set(Reg(…), v)` → `regs[Reg(…)] = v`
  - 验证：624 Assert 全通过
- [ ] **Benchmark 验证**
  - 运行 B01-B05，对比基线
  - 预期：dispatch 40-60% 加速（ratio 降至 3-4x 范围）
- [ ] **文档更新**
  - 本文追加功能展望 + 优化展望 + 风险点
  - VM_Summary.md 串行计划更新

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
