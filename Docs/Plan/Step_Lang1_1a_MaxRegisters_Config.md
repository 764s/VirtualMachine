# Lang-1.1a: MaxRegisters 常量配置化

> **来源**：VM_Summary.md §七 Lang 表
>
> **前置**：Lang-1 ✅（模块变量）
>
> **状态**：✅ 完成（MR01-MR08 全通过，1032 测试全通过，B01-B06 无回归）
>
> **目标**：`MaxRegisters` 成为唯一配置点。修改该常量后，寄存器布局、结构体大小、内存预算注释全部自动跟随，无需手动修改其他文件。

---

## 一、当前问题（已解决）

| 硬编码项 | 文件 | 当前值 | 问题 | 状态 |
|----------|------|--------|------|------|
| `VarRegBase = 16` | BytecodeCompiler.cs | 16 | 不跟随 MaxRegisters | ✅ → VMConstants.ScratchZoneSize |
| `TempRegBase = 48` | BytecodeCompiler.cs | 48 | 不跟随 MaxRegisters | ✅ → VMConstants.TempRegBase |
| `r < 16` | VMWorld.cs Reg() | 16 | 不跟随 MaxRegisters | ✅ → VMConstants.ScratchZoneSize |
| `< 16` | ScriptDebugger.cs | 16 | 不跟随 MaxRegisters | ✅ → VMConstants.ScratchZoneSize |
| R00..R63 字段 | VMInstanceState.cs NumberRegisters | 64 字段 | 不跟随 MaxRegisters | ✅ → `fixed long Raw[MaxRegisters]` |
| 内存预算注释 | VMConstants.cs, VMInstanceState.cs | "64 × 8 = 512" | 写死数值 | ✅ → 公式化注释 |

---

## 二、设计方案

### 2.1 VMConstants 新增派生常量

```
ScratchZoneSize = 16                                    // r0..15 固定
TempSlots = (MaxRegisters / 64) * 8                     // 每 64 寄存器保留 8 temp
LocalVarSlots = ModuleVarRegBase - ScratchZoneSize - TempSlots
TempRegBase = ScratchZoneSize + LocalVarSlots           // = ModuleVarRegBase - TempSlots
```

所有编译器/运行时引用均改为 VMConstants 常量，消除硬编码。

### 2.2 NumberRegisters → fixed buffer

将 64 个显式字段 `R00..R63` 替换为 `fixed long Raw[MaxRegisters]`：

- `Number` 为 `LayoutKind.Explicit, Size = 8`，`long Raw` 在 offset 0 → `long*` 与 `Number*` 可安全互转
- `fixed` size buffer 支持 C# 2.0+，netstandard2.1 / net8.0 均兼容
- VMWorld 执行循环 `fixed` 钉住方式同步调整

### 2.3 Reg() 边界常量化

`Reg()` 中 `r < 16` → `r < VMConstants.ScratchZoneSize`。
编译后为相同的 `cmp reg, imm16` 指令，JIT 常量折叠无性能差异。

---

## 三、子任务清单

| # | 内容 | 文件 | 状态 |
|---|------|------|------|
| MR01 | VMConstants 新增 ScratchZoneSize / TempSlots / LocalVarSlots / TempRegBase | VMConstants.cs | ✅ |
| MR02 | NumberRegisters 改为 fixed long Raw[MaxRegisters] + 更新 Get/Set | VMInstanceState.cs | ✅ |
| MR03 | VMWorld.Reg() 和 fixed 钉住改用常量 | VMWorld.cs | ✅ |
| MR04 | ScriptDebugger 边界改用常量 | ScriptDebugger.cs | ✅ |
| MR05 | BytecodeCompiler VarRegBase/TempRegBase 改用 VMConstants | BytecodeCompiler.cs | ✅ |
| MR06 | 内存预算注释更新（VMConstants + VMInstanceState 头部） | 多文件 | ✅ |
| MR07 | 构建 + 全量测试通过（1032 项） | — | ✅ |
| MR08 | B01-B06 benchmark 确认无回归 | — | ✅ |

---

## 四、完成条件

1. ✅ 修改 `VMConstants.MaxRegisters` 为任意 64 的倍数后，无需修改其他代码即可编译通过
2. ✅ 所有现有测试通过（MaxRegisters 保持 64 不变）— 1032 项全通过
3. ✅ B01-B06 benchmark 无性能回归

---

## 五、妥协点

| 妥协 | 原因 | 消除时机 |
|------|------|---------|
| MaxRegisters 必须为 64 的倍数 | ModuleVarSlots 公式依赖整除 | 若需要非 64 倍数时调整公式 |
| ScratchZoneSize 固定 16 | 与 Syscall 参数约定耦合（r0-r15 传参），无需配置化 | 永久 |

---

## 六、功能展望

无。Lang-1.1a 为纯基础设施改造，不引入新功能。

## 七、优化展望

无。`fixed long Raw[MaxRegisters]` 与原显式字段方式产生相同 JIT 代码，无性能差异。

## 八、风险点

无新增风险。`fixed` 缓冲区为 C# 2.0+ 标准特性，netstandard2.1 / net8.0 双目标均已验证通过。
