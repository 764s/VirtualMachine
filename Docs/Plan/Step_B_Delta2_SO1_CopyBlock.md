# B-δ2 SO1 COPY_BLOCK OpCode

> **状态**: ✅ 完成（2026-04-05）
> **依赖**: struct ✅
> **测试**: 945 项 Assert × 2 模式全通过（float + Fix64）

## 一、目标

新增 `COPY_BLOCK` 指令替代 N×MOVE 的结构体赋值，将 ≥3 字段的 struct 整体拷贝从 N 条指令减少为 1 条。

## 二、设计

### 2.1 新 OpCode

```
COPY_BLOCK = 31   // A=destReg, B=srcReg, C=count → Reg[A..A+C-1] = Reg[B..B+C-1]
SENTINEL   = 32   // (bumped from 31)
```

### 2.2 阈值策略

| 字段数 | 策略 | 理由 |
|--------|------|------|
| 1 | 单条 MOVE | 无需批量 |
| 2 | 2×MOVE | COPY_BLOCK 循环开销与 2×MOVE 持平，不值得 |
| ≥3 | COPY_BLOCK | 单条指令 + 内部循环，减少 dispatch 开销 |

### 2.3 编译器变更

新增 `EmitStructCopy(destBase, srcBase, count)` 方法，替代以下 5 处 N×MOVE 循环：

1. **函数参数绑定**（struct parameter prologue copy）
2. **var 声明初始化**（`var b: Vec3 = a`）
3. **整体赋值**（`b = a`）
4. **子 struct 字段拷贝**（`a.inner = b.inner`）
5. **标识符到子字段拷贝**（`a.inner = varOfSameType`）

### 2.4 VM 执行器

```csharp
case OpCode.COPY_BLOCK:
{
    int dst = Reg(op.A, rb);
    int src = Reg(op.B, rb);
    int count = op.C;
    for (int ci = 0; ci < count; ci++)
        regs[dst + ci] = regs[src + ci];
    inst.IP++;
    break;
}
```

## 三、妥协

| 妥协 | 原因 | 类型 |
|------|------|------|
| 未使用 `Buffer.MemoryCopy` | `Number*` 寄存器已 fixed pin，逐元素循环在 JIT 中的性能与 memcpy 相当（8B × 3-5 = 24-40B），且避免 unsafe 指针计算 | 永久 |
| SENTINEL 值从 31 变为 32 | COPY_BLOCK 占用 31 号位 | 永久（无兼容性影响，SENTINEL 仅在运行时使用） |

## 四、测试清单

| ID | 测试 | 验证点 |
|----|------|--------|
| SO1-01 | 3 字段 struct 初始化 | COPY_BLOCK 被发射 + count=3 + 执行正确 |
| SO1-02 | 2 字段 struct 初始化 | 无 COPY_BLOCK（使用 N×MOVE） + 执行正确 |
| SO1-03 | 4 字段 struct 整体赋值 | 执行正确 |
| SO1-04 | 嵌套 struct (4 flat fields) | COPY_BLOCK(4) + 执行正确 |
| SO1-05 | COPY_BLOCK + rollback | wait 后 save/load 不影响 struct 数据 |

## 五、风险点

| 风险 | 影响 | 当前状态 |
|------|------|----------|
| SR2（N×MOVE 大 struct 性能退化） | 已消除 | ✅ SO1 完成，≥3 字段 struct 赋值为单条指令 |
