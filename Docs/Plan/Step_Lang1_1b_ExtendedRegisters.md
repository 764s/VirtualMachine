# Lang-1.1b: 扩展寄存器（Extended Registers）

> **来源**：VM_Summary.md §七 Lang 表
>
> **前置**：Lang-1 ✅（模块变量）、Lang-1.1a ✅（MaxRegisters 常量配置化）
>
> **状态**：🔄 进行中
>
> **目标**：独立于 `NumberRegisters` 的按需扩展寄存器池（`Number[]` 堆数组）+ 专用 opcode 访问。不使用时零开销（不在 `fixed` 指针路径上）。

---

## 一、当前问题

| 瓶颈 | 限制 | 影响 |
|------|------|------|
| 模块变量槽位 | ModuleVarSlots = 8（r56~r63） | 超过 8 个模块变量编译失败 |
| 局部变量槽位 | LocalVarSlots = 32（r16~r47） | 超过 32 个局部变量编译失败（有 F4 复用缓解） |
| 表达式临时寄存器 | TempSlots = 8（r48~r55） | 极复杂表达式编译失败 |

**优先级**：模块变量溢出 > 局部变量溢出 > 临时寄存器溢出。本步骤实现模块变量溢出到扩展寄存器的能力。

---

## 二、设计方案

### 2.1 存储位置：InstancePool 级别

扩展寄存器存储在 `InstancePool.ExtendedRegs` 而非 `VMInstanceState` 内部，保持 VMInstanceState 为纯值类型（可 memcpy 快照）：

```
InstancePool
├── Instances: VMInstanceState[128]     ← 现有
├── ExtendedRegs: Number[][128]         ← 新增（每实例一个堆数组，null = 未使用）
└── ...
```

### 2.2 零开销保证

| 场景 | 开销 |
|------|------|
| 程序不使用扩展寄存器 | 零 — ExtendedRegs 全部为 null，不分配堆内存 |
| 程序使用扩展寄存器 | SpawnInstance 时一次 `new Number[N]` 分配 |
| 快照/回滚 | 仅深拷贝已分配的扩展数组 |

### 2.3 新增 OpCode

| OpCode | 编码 | 语义 |
|--------|------|------|
| `LOAD_XREG` | A=dest, B=xidx_lo, C=xidx_hi | `regs[Reg(A,rb)] = extRegs[B \| (C<<8)]` |
| `STORE_XREG` | A=xidx_lo, B=src, C=xidx_hi | `extRegs[A \| (C<<8)] = regs[Reg(B,rb)]` |

扩展寄存器索引范围：0~65535（16 位），足够覆盖任何实际场景。

### 2.4 VMProgram 新增字段

```
RequiredExtendedRegisters: int  — 编译器告知运行时需要的扩展寄存器数量（0 = 不使用）
```

### 2.5 编译器变更

`_moduleVarRegisters` 字典中，值 >= MaxRegisters 表示扩展寄存器：
- 固定模块变量：`reg ∈ [ModuleVarRegBase, MaxRegisters)` → LOAD_MVAR/STORE_MVAR
- 扩展模块变量：`reg >= MaxRegisters`，xidx = reg - MaxRegisters → LOAD_XREG/STORE_XREG

新增辅助方法 `EmitLoadModuleVar(dest, reg)` / `EmitStoreModuleVar(reg, src)` 统一处理。

### 2.6 快照变更

`VMWorldSnapshot` 新增 `Number[][] ExtendedRegSnapshots`。SaveState 深拷贝已分配数组，LoadState 恢复。

---

## 三、子任务清单

| # | 内容 | 文件 | 状态 |
|---|------|------|------|
| XR01 | OpCode 新增 LOAD_XREG / STORE_XREG | OpCode.cs | ⏳ |
| XR02 | VMProgram 新增 RequiredExtendedRegisters | VMProgram.cs | ⏳ |
| XR03 | InstancePool 新增 ExtendedRegs + Allocate 联动 | InstancePool.cs | ⏳ |
| XR04 | VMWorld.SpawnInstance 预分配 + ExecuteInstance 处理器 | VMWorld.cs | ⏳ |
| XR05 | Snapshot 深拷贝扩展寄存器 | Snapshot.cs | ⏳ |
| XR06 | 编译器辅助方法 + ProcessModuleVariables 溢出 | BytecodeCompiler.cs | ⏳ |
| XR07 | 编译器所有模块变量 emission site 改用辅助方法 | BytecodeCompiler.cs | ⏳ |
| XR08 | Peephole 优化器 InstructionDestReg 支持新 opcode | BytecodeCompiler.cs | ⏳ |
| XR09 | ScriptDebugger 扩展寄存器读取 | ScriptDebugger.cs | ⏳ |
| XR10 | 测试用例 XR01-XR06 | Tests/ | ⏳ |
| XR11 | 全量测试 + B01-B06 benchmark 无回归 | — | ⏳ |
| XR12 | VM_Summary.md 更新 | Docs/ | ⏳ |

---

## 四、完成条件

1. 超过 8 个模块变量的程序可正确编译和执行
2. 不使用扩展寄存器的程序零开销（无堆分配）
3. 所有现有测试通过
4. B01-B06 benchmark 无性能回归

---

## 五、妥协点

| 妥协 | 原因 | 消除时机 |
|------|------|---------|
| 仅覆盖模块变量溢出 | 局部变量有 F4 复用机制，溢出极罕见 | 如需则后续 Lang-1.1c |
| 不覆盖表达式临时寄存器溢出 | 8 个临时槽已足够所有现有场景 | 同上 |

---

## 六、风险点

| 风险 | 等级 | 缓解 |
|------|------|------|
| 快照深拷贝性能 | 低 | 仅拷贝已分配数组；典型场景扩展数组很小 |
| GC 压力 | 极低 | Number[] 一次分配长期存活，不触发频繁 GC |
