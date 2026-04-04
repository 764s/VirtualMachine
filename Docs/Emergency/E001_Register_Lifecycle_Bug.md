# E001 编译器寄存器生命周期 Bug（恶性缺陷）

> **来源**：[P002_Sandbox_Build.md — P1](../Practice/P002_Sandbox_Build.md)
> **等级**：🔴 恶性缺陷 — 必须立即修复
> **状态**：✅ 已修复
> **创建日期**：2026-04-04
> **修复日期**：2026-04-04

---

## 一、缺陷描述

当函数中存在多个局部变量（其中部分在 while 循环前已"死亡"），编译器复用其寄存器给 `sum`/`i` 等循环变量时，在特定对齐条件下产生**错误结果**。

### 最小复现

```ffs
func add(a: int, b: int): int { return a + b }
func main() {
    var a: int = 1
    var b: int = 2     // ← 两个死变量
    var sum: int = 0
    var i: int = 1
    while i <= 100 { sum = sum + i; i = i + 1 }
    print(sum)          // 预期 5050，实际 127
    var r: int = add(1, 2)
}
```

### 关键特征

- 2 个死变量 → 结果 127（❌），1 个或 3 个死变量 → 正确 5050（✅）
- 与是否存在函数调用无关（纯 syscall 也触发）
- `locals` 计数正确，但分配的具体寄存器号导致运行时冲突
- 127 = 2⁷ − 1，疑似 7-bit 截断或 signed-byte 溢出

### 影响范围

任何在单函数中大量使用临时变量 + 循环的脚本均可能触发。**阻塞实际使用**。

---

## 二、用户决策

> **p1**：选择**分析方案**而非掩耳盗铃。

---

## 三、修复计划

### 阶段 1：精确定位

1. **编译产物审计**：针对最小复现脚本，逐条 dump 编译后 bytecode，标注每条指令的源/目标寄存器号
2. **寄存器分配时序追踪**：在 `AllocTemp` / `FreeTemp` 路径插入日志，比较 2 死变量 vs 1 死变量 vs 3 死变量场景下的分配序列差异
3. **back-edge 交互分析**：while 循环 back-edge 是否在某些条件下使编译器误认为寄存器已释放
4. **值 127 逆推**：从 127 = 2⁷ − 1 出发，检查是否存在 8-bit 截断、signed overflow、或寄存器号与值域混淆

### 阶段 2：修复

5. **修正寄存器分配逻辑**：根据阶段 1 定位结果，修复 `FreeTemp` / `AllocTemp` 中与 while 循环 back-edge 的交互问题
6. **添加回归测试**：
   - 2 死变量 + while 循环 → 预期 5050
   - 不同数量死变量的参数化测试
   - 深度嵌套循环 + 多死变量组合

### 阶段 3：验证

7. **全量 Assert 通过**：763 项 Assert × 2 模式全通过
8. **Sandbox 端到端验证**：Sandbox 脚本正确输出 5050
9. **P6 重新评估**：修复后重测 fibonacci(10) 递归场景，确认是否仍触发步数限制

---

## 四、风险点

| ID | 风险 | 缓解措施 |
|----|------|----------|
| ER1 | 修复可能改变已有寄存器分配策略，影响其他测试 | 全量 Assert 回归验证 |
| ER2 | 根因可能不在 AllocTemp/FreeTemp，而在更上层的活跃性分析 | 阶段 1 先精确定位，不盲修 |
| ER3 | 修复后可能暴露之前被 bug 掩盖的其他问题 | 密切关注全量 Assert 中新增的失败 |

---

## 五、根因与修复

### 根因

CompileBlock 中 F4 变量释放逻辑存在**重复释放**缺陷：

1. `TryReleaseVar()` 将变量寄存器加入 `_freeVarRegs`，但**不从 `_liveRanges` 移除**该变量
2. 当被释放的寄存器被新变量复用后，该寄存器从 `_freeVarRegs` 消失
3. 下一次 release check 时，已释放变量仍在 `_liveRanges` 中，其寄存器不在 `_freeVarRegs` → `alreadyFreed` 误判为 `false`
4. 同一寄存器被**二次释放**，导致后续变量再次复用 → 两个活跃变量共享同一寄存器

**2 死变量场景触发路径**：
- `dead0→r16`（释放）→ `sum→r16`（复用）→ `dead0` 二次释放（r16 再入 free list）→ `i→r16`（二次复用）
- `sum` 和 `i` 均在 r16 → `sum = sum + i` 变为 `r16 = r16 + r16`（自身翻倍）
- 循环产生 1→2→3→6→7→14→15→30→31→62→63→126→**127**（超出 100 退出）

### 修复

`BytecodeCompiler.cs` CompileBlock 释放循环末尾，在 `TryReleaseVar()` 之后增加 `_liveRanges.Remove()`，防止同一变量被重复处理：

```csharp
for (int r = 0; r < toRelease.Count; r++)
{
    TryReleaseVar(toRelease[r]);
    _liveRanges.Remove(toRelease[r]);  // E001 fix: prevents double-free
}
```

### 回归测试

CompilerTests.cs 新增 6 个测试（E001-01 到 E001-06）：
- E001-01: 2 死变量 + while（原始复现）→ 5050
- E001-02: 4 死变量 + while → 55
- E001-03: 死变量 + for 循环 → 15
- E001-04: 死变量与活变量交替 → 300
- E001-05: 嵌套 while + 死变量 → 9
- E001-06: 非 entry 函数中死变量 + while → 55

---

## 六、完成标准

- [x] 根因已精确定位并记录
- [x] 修复代码已提交
- [x] 回归测试覆盖最小复现 + 参数化变体（6 个测试）
- [x] 775 项 Assert × 2 模式全通过（763 原有 + 12 新增）
- [ ] Sandbox 端到端输出 5050（待下次 Sandbox 验证）
- [ ] P6 递归场景重新评估完成（可在后续 session 进行）
- [x] 更新本文件状态为 ✅
