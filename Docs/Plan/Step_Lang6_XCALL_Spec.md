# Lang-6: XCALL Spec 设计文档（C-1 XCALL 基线）

> **来源**：VM_Summary.md §七 Lang 表、KOF98/Docs/Discussion/D_SkillScripting.md Q4 核心结论（R18~R26，14 项决策锁定）
>
> **前置**：Lang-1 ✅（模块变量）、Lang-1.1a ✅（常量配置化）、Lang-1.1b ✅（扩展寄存器）、Lang-2 ✅（include）、Lang-3 ✅（黑板 Syscall）
>
> **状态**：🔄 设计文档
>
> **目标**：定义 XCALL / XLOAD_MVAR / XSTORE_MVAR 三个跨实例 OpCode 的编码格式、导出表结构、跨实例寻址协议，作为 Lang-6 实现的技术规格。

---

## 一、设计来源与决策摘要

### 1.1 Q4 收敛决策（14 项，R18~R26 全部锁定）

| 决策 | 结论 | 轮次 |
|------|------|------|
| 服务脚本定位 | FFS 运行时实体，不是 include | R20 ✅ |
| 持有方式 | 方式 C — 语言级引用（instanceId） | R20 ✅ |
| 生命周期管理 | 宿主 C# 创建并注册 | R21 ✅ |
| 服务函数 yield | 禁止 — 纯编译期保证 Y1-Plus（无运行时负担） | R22 ✅ |
| 调用语法 | `svc.member` 统一语法，编译器自动路由 L4/L5 | R23-24 ✅ |
| L4/L5 关系 | 同基线设计：XCALL + XLOAD_MVAR + XSTORE_MVAR 在 C-1 同时实现 | R23 ✅ |
| 导出声明 | **`@export` 唯一形式** | R24-25 ✅ |
| 自动优化 | A1/A2 自动 getter/setter→直接访问退化（C-1.5） | R23 ✅ |
| 用户引导内联 | `@inline`（hint，C-2）+ `@force_inline`（强制，C-3）+ LSP 诊断 | R24 ✅ |
| 实现路径 | C-0 → C-1(XCALL+XL4) → C-1.5(A1/A2+配置) → C-2(语法糖+@inline+LSP) → C-3(A5+@force_inline) | R23-26 ✅ |
| 嵌套调用 | **运行时配置 MaxXCallDepth（默认4）+ Warn/Unlimited 两种策略** | R21→R26 ✅ |
| 性能影响 | 可忽略（< 0.02% 帧预算；深度检查 +1 ns/XCALL；常量 vs 变量无差异） | R20-26 ✅ |
| XCALL 优化 | O1+O2（C-1），A1/A2（C-1.5），O7+@inline（C-2），O4/A5+@force_inline（C-3+） | R22-24 ✅ |
| 优化退化策略 | 编译期自动退化，运行时零决策开销 | R22 ✅ |

### 1.2 C-1 阶段范围

C-1 是 XCALL 的**基线实现**，包含：

| 组件 | 内容 |
|------|------|
| 3 个新 OpCode | XCALL、XLOAD_MVAR、XSTORE_MVAR |
| `@export` 导出声明 | Lexer + Parser + AST + 编译器导出表生成 |
| Y1-Plus | 编译期检查 @export 函数不含 yield/wait |
| 嵌套深度检查 | 运行时 `_xcallDepth` 计数 + Warn 模式（默认 MaxXCallDepth=4） |
| 参数传递协议 | scratch zone r0-rN 复制 |
| 测试套件 | XC01-XC12+（基础 XCALL、跨实例变量读写、嵌套调用等） |

**不包含**（后续阶段）：A1/A2 自动退化（C-1.5）、`svc.member` 语法糖（C-2）、`@inline`（C-2）、`@force_inline`（C-3）。

---

## 二、OpCode 编码

### 2.1 指令格式回顾

当前 FFVM 使用 4 字节固定宽度指令（O8 压缩后）：

```
[StructLayout(LayoutKind.Explicit, Size = 4)]
struct Instruction {
    [FieldOffset(0)] OpCode Code;  // 1 byte (0-255)
    [FieldOffset(1)] byte A;       // 1 byte operand
    [FieldOffset(2)] byte B;       // 1 byte operand
    [FieldOffset(3)] byte C;       // 1 byte operand
}
```

当前 OpCode 编号使用到 51（SENTINEL）。新 OpCode 从 52 起分配。

### 2.2 新 OpCode 定义

```csharp
// --- Lang-6: cross-instance member access (XIMA) ---
XCALL        = 52,  // A=destReg, B=instanceId_reg, C=exportFuncIndex
XLOAD_MVAR   = 53,  // A=destReg, B=instanceId_reg, C=exportVarIndex
XSTORE_MVAR  = 54,  // A=exportVarIndex, B=instanceId_reg, C=srcReg
```

### 2.3 操作数详解

#### XCALL（跨实例函数调用）

```
XCALL  A=destReg, B=instanceId_reg, C=exportFuncIndex
```

| 操作数 | 宽度 | 含义 |
|--------|------|------|
| A | 8 bit | 结果存放寄存器（调用方视角，经 Reg() 映射） |
| B | 8 bit | 持有目标实例 ID 的寄存器（调用方视角，经 Reg() 映射）。`regs[Reg(B)]` 的 int 值 = 目标 InstanceId |
| C | 8 bit | 目标模块导出函数表中的索引（0-255）。通过目标 VMProgram.ExportTable 解析为 FunctionEntry |

语义：

```
targetInstanceId = regs[Reg(op.B, rb)].ToInt()
targetInst       = Pool.Instances[targetInstanceId]
targetProgram    = Modules.Get(targetInst.ModuleSlot)
targetFunc       = targetProgram.ExportTable.Functions[op.C]

1. 保存调用方跨调用帧（XCallFrame）
2. 将调用方 scratch zone r0..r(paramCount-1) 复制到目标实例 scratch zone r0..r(paramCount-1)
3. 在目标实例上同步执行 targetFunc（同一 Tick 内完成）
4. 将目标实例 r0（返回值）写入调用方 regs[Reg(op.A, rb)]
5. 恢复调用方状态
```

> 注：C 操作数为 8 bit，支持每模块最多 256 个导出函数。如果未来需要更多，可用 EXTEND_AX 前缀扩展（但预计不会触及此限制）。

#### XLOAD_MVAR（跨实例变量读取）

```
XLOAD_MVAR  A=destReg, B=instanceId_reg, C=exportVarIndex
```

| 操作数 | 宽度 | 含义 |
|--------|------|------|
| A | 8 bit | 结果存放寄存器（调用方视角，经 Reg() 映射） |
| B | 8 bit | 持有目标实例 ID 的寄存器（调用方视角，经 Reg() 映射） |
| C | 8 bit | 目标模块导出变量表中的索引（0-255）。通过目标 VMProgram.ExportTable 解析为 mvarSlot |

语义：

```
targetInstanceId = regs[Reg(op.B, rb)].ToInt()
targetInst       = Pool.Instances[targetInstanceId]
targetProgram    = Modules.Get(targetInst.ModuleSlot)
mvarSlot         = targetProgram.ExportTable.Variables[op.C].MvarSlot

// 直接读取目标实例的模块变量寄存器
regs[Reg(op.A, rb)] = targetRegs[VMConstants.ModuleVarRegBase + mvarSlot]
```

> 如果目标模块变量溢出到扩展寄存器（mvarSlot ≥ ModuleVarSlots），需要走 ExtendedRegs 路径。详见 §4.5。

#### XSTORE_MVAR（跨实例变量写入）

```
XSTORE_MVAR  A=exportVarIndex, B=instanceId_reg, C=srcReg
```

| 操作数 | 宽度 | 含义 |
|--------|------|------|
| A | 8 bit | 目标模块导出变量表中的索引（0-255） |
| B | 8 bit | 持有目标实例 ID 的寄存器（调用方视角，经 Reg() 映射） |
| C | 8 bit | 源值寄存器（调用方视角，经 Reg() 映射） |

语义：

```
targetInstanceId = regs[Reg(op.B, rb)].ToInt()
targetInst       = Pool.Instances[targetInstanceId]
targetProgram    = Modules.Get(targetInst.ModuleSlot)
mvarSlot         = targetProgram.ExportTable.Variables[op.A].MvarSlot

// 直接写入目标实例的模块变量寄存器
targetRegs[VMConstants.ModuleVarRegBase + mvarSlot] = regs[Reg(op.C, rb)]
```

> 编译期安全：未标记 `@export` 的变量不会出现在导出表中，编译器拒绝生成 XLOAD_MVAR/XSTORE_MVAR。`@export const` 写入在编译期报错。

### 2.4 操作数编码一致性

三个 OpCode 共享 B 操作数语义（instanceId 寄存器），与现有 OpCode 设计保持一致：

| OpCode | A | B | C |
|--------|---|---|---|
| XCALL | dest reg | instanceId reg | export func index |
| XLOAD_MVAR | dest reg | instanceId reg | export var index |
| XSTORE_MVAR | export var index | instanceId reg | src reg |

> XSTORE_MVAR 的 A/C 交换是为了与 STORE_MVAR 保持一致（A=slot, B=src），但 B 固定为 instanceId reg。

### 2.5 与现有 OpCode 的对比

| 现有 OpCode | 新 OpCode | 关系 |
|------------|-----------|------|
| LOAD_MVAR (47): A=dest, B=mvarSlot | XLOAD_MVAR (53): A=dest, B=instId, C=exportVarIdx | 跨实例版本 |
| STORE_MVAR (48): A=mvarSlot, B=src | XSTORE_MVAR (54): A=exportVarIdx, B=instId, C=src | 跨实例版本 |
| CALL (27): A=targetIP, B=windowSize | XCALL (52): A=dest, B=instId, C=exportFuncIdx | 跨实例版本 |

---

## 三、导出表格式

### 3.1 `@export` 声明语法

```ffs
// 服务脚本 owner_service.ffs

@export var hp = 1000                   // 导出可读写变量
@export const max_hp = 1000             // 导出只读常量
var internal_state = 0                  // 不导出

@export func get_frame() {              // 导出函数
    return _Owner_GetFrame()
}

@export func take_damage(d) {           // 导出函数（带参数）
    hp = hp - d
}

func internal_helper() {                // 不导出
    // ...
}
```

### 3.2 Lexer / Parser 改动

**Lexer**：新增 `Export` TokenType，识别 `@export` 关键字。

**Parser**：在模块顶层声明（var/const/func）前检测 `@export` 前缀：

```
ModuleDecl  → (@export)? VarDecl
            | (@export)? ConstDecl
            | (@export)? FuncDecl
```

**AST 扩展**：

- `VarDeclStmt` 新增 `bool IsExported` 字段
- `FuncDeclStmt`（或等价的函数声明 AST 节点）新增 `bool IsExported` 字段

### 3.3 ExportTable 数据结构

每个 VMProgram 在编译时可选地携带一个导出表：

```csharp
/// <summary>
/// Lang-6: Export table for cross-instance member access (XIMA).
/// Generated by compiler for modules containing @export declarations.
/// Stored in VMProgram (ROM, shared across instances of same module).
/// </summary>
public class ExportTable
{
    /// <summary>Exported variable entries, indexed by export var index (0-based).</summary>
    public readonly ExportVarEntry[] Variables;

    /// <summary>Exported function entries, indexed by export func index (0-based).</summary>
    public readonly ExportFuncEntry[] Functions;

    public ExportTable(ExportVarEntry[] variables, ExportFuncEntry[] functions)
    {
        Variables = variables ?? Array.Empty<ExportVarEntry>();
        Functions = functions ?? Array.Empty<ExportFuncEntry>();
    }
}

/// <summary>Exported variable: maps export index → module variable slot + metadata.</summary>
public struct ExportVarEntry
{
    /// <summary>Variable name (for debugging / LSP / cross-module resolution).</summary>
    public readonly string Name;

    /// <summary>
    /// Module variable slot (0-based offset from ModuleVarRegBase).
    /// For fixed module vars: physical register = ModuleVarRegBase + MvarSlot.
    /// For extended module vars (MvarSlot >= ModuleVarSlots): extended register index = MvarSlot - ModuleVarSlots.
    /// </summary>
    public readonly int MvarSlot;

    /// <summary>True if declared as @export var (read-write). False if @export const (read-only).</summary>
    public readonly bool Writable;

    public ExportVarEntry(string name, int mvarSlot, bool writable)
    {
        Name = name;
        MvarSlot = mvarSlot;
        Writable = writable;
    }
}

/// <summary>Exported function: maps export index → function table entry + metadata.</summary>
public struct ExportFuncEntry
{
    /// <summary>Function name (for debugging / LSP / cross-module resolution).</summary>
    public readonly string Name;

    /// <summary>Index into VMProgram.Functions array.</summary>
    public readonly int FuncTableIndex;

    /// <summary>Parameter count (for argument validation).</summary>
    public readonly int ParamCount;

    public ExportFuncEntry(string name, int funcTableIndex, int paramCount)
    {
        Name = name;
        FuncTableIndex = funcTableIndex;
        ParamCount = paramCount;
    }
}
```

### 3.4 VMProgram 扩展

```csharp
public class VMProgram
{
    // ... 现有字段 ...

    /// <summary>
    /// Lang-6: Export table for cross-instance access. Null if module has no @export declarations.
    /// </summary>
    public readonly ExportTable ExportTable;

    // 构造函数新增 exportTable 参数
}
```

### 3.5 导出索引分配

编译器按声明顺序为 `@export` 的变量和函数分别分配 0-based 索引：

```
@export var hp = 1000        → ExportTable.Variables[0] = { "hp", mvarSlot=0, writable=true }
@export const max_hp = 1000  → ExportTable.Variables[1] = { "max_hp", mvarSlot=1, writable=false }
@export func get_frame()     → ExportTable.Functions[0] = { "get_frame", funcIdx=1, paramCount=0 }
@export func take_damage(d)  → ExportTable.Functions[1] = { "take_damage", funcIdx=2, paramCount=1 }
```

> 变量索引和函数索引各自独立编号。XCALL 使用 exportFuncIndex，XLOAD_MVAR/XSTORE_MVAR 使用 exportVarIndex。

### 3.6 跨模块编译：导出表解析

调用方编译器需要访问目标服务模块的导出表，以便：
1. 将 `svc.member` 语法解析为正确的 OpCode（C-2 语法糖阶段）
2. 验证参数数量匹配（XCALL 的 paramCount 检查）
3. 验证变量可写性（XSTORE_MVAR 只允许 Writable=true 的变量）

**C-1 阶段的简化方案**：

C-1 阶段不实现 `svc.member` 语法糖。调用方通过以下方式使用 XCALL：
1. 宿主 C# 提供目标模块的 export table 信息（编译选项或 preloaded tables）
2. 或编译器接受显式的导出索引（类似 `call(svc, 0)` 风格）

**推荐方案**：编译器新增 Compile 重载参数 `ExportTable[] importedExports`，允许传入外部模块的导出表供编译期解析。这为 C-2 语法糖提供基础设施。

---

## 四、跨实例寻址协议

### 4.1 实例引用

服务脚本实例由宿主 C# 创建并注册：

```csharp
// 宿主创建服务实例
int ownerSvcId = vm.SpawnInstance(ownerServiceModuleSlot, entryIP: 0);

// 业务脚本获取服务实例 ID（通过 Syscall）
table.Register("GetService", (ref VMInstanceState inst) => {
    int serviceKey = inst.Registers.Get(0).ToInt();
    inst.Registers.Set(0, Number.FromInt(serviceRegistry[serviceKey]));
});
```

FFS 侧持有 instanceId 作为普通 Number 值：

```ffs
var owner = GetService(SVC_OWNER)   // Syscall → r0 = instanceId (int)
```

### 4.2 XCALL 执行协议（完整流程）

XCALL 是**同步跨实例调用**，在同一 Tick 的 ExecuteInstance 内完成。不涉及 yield/wait/挂起。

#### 步骤详解

```
调用方执行 XCALL(destReg, instanceIdReg, exportFuncIdx):

Phase 1: 准备
  1. targetInstanceId = regs[Reg(op.B, rb)].ToInt()
  2. 验证 targetInstanceId 有效且实例存活
  3. 查找目标 VMProgram 及其 ExportTable
  4. exportFunc = ExportTable.Functions[op.C]
  5. targetFuncEntry = targetProgram.Functions[exportFunc.FuncTableIndex]

Phase 2: 嵌套深度检查
  6. ++_xcallDepth
  7. if (_xcallDepthWarning && _xcallDepth > _maxXCallDepth) → 发出警告

Phase 3: 保存调用方状态
  8. 保存 XCallFrame {
       CallerInstanceId,
       CallerIP = inst.IP + 1,        // 下一条指令
       CallerModuleSlot = inst.ModuleSlot,
       CallerRegisterBase = inst.RegisterBase,
       CallerCallStackDepth = inst.CallStackDepth,
       CallerCleanupDepth = inst.CleanupDepth,
       DestReg = Reg(op.A, rb)        // 返回值写入位置
     }

Phase 4: 参数传递
  9. 将调用方 r0..r(paramCount-1) 复制到目标实例 r0..r(paramCount-1)
     （scratch zone 是绝对地址 r0-r15，无需 Reg() 映射）

Phase 5: 切换到目标实例并执行
  10. 切换 inst 引用到 Pool.Instances[targetInstanceId]
  11. 设置目标实例 IP = targetFuncEntry.EntryIP
  12. 设置目标实例 RegisterBase = 0（从函数入口开始，新的寄存器窗口）
  13. 同步执行目标函数直到 RET_FUNC/RETURN（在同一执行循环内）

Phase 6: 返回
  14. 将目标实例 r0（返回值）保存到临时变量
  15. 恢复调用方状态（从 XCallFrame）
  16. 将返回值写入调用方 regs[xcallFrame.DestReg]
  17. --_xcallDepth
```

### 4.3 XCallFrame 数据结构

```csharp
/// <summary>
/// Lang-6: Cross-instance call frame. Saved/restored on XCALL entry/exit.
/// Stored in VMWorld (not in VMInstanceState) since it spans instances.
/// </summary>
public struct XCallFrame
{
    public int CallerInstanceId;
    public int CallerIP;
    public int CallerModuleSlot;
    public int CallerRegisterBase;
    public int CallerCallStackDepth;
    public int CallerCleanupDepth;
    public int DestReg;             // 调用方的返回值目标寄存器（已 Reg() 映射）
}
```

XCallFrame 栈存储在 VMWorld 中（不在 VMInstanceState 中），因为跨实例调用是全局概念：

```csharp
// VMWorld 新增字段
private XCallFrame[] _xcallStack = new XCallFrame[MaxXCallDepth];
private int _xcallDepth;
```

> 使用固定大小数组（默认 4 或可配置值）。Warn 模式下超出也能继续（扩展到更大数组或用 List）。

### 4.4 参数传递细节

**约定**：跨实例函数参数通过 scratch zone（r0-r15）传递，与模块内函数调用约定一致。

| 参数位置 | 寄存器 | 说明 |
|---------|--------|------|
| arg0 | r0 | 第 1 个参数 |
| arg1 | r1 | 第 2 个参数 |
| ... | ... | ... |
| argN | rN | 第 N+1 个参数 |
| 返回值 | r0 | 函数返回后存入 r0 |

**参数复制**：

```csharp
// 将调用方 scratch zone 复制到目标实例 scratch zone
// scratch zone 是绝对地址（r0-r15），无需 Reg() 映射
int paramCount = exportFunc.ParamCount;
Number* callerRegs = (Number*)callerRawRegs;
Number* targetRegs = (Number*)targetRawRegs;

for (int i = 0; i < paramCount; i++)
    targetRegs[i] = callerRegs[i];
```

> O2 优化（值得在 C-1 就做）：当 paramCount > 2 时使用 `Unsafe.CopyBlock` 或 `Buffer.MemoryCopy` 代替逐个复制。

**最大参数数量**：由 ScratchZoneSize（16）限制。单个跨实例函数调用最多 16 个参数（与模块内函数一致）。

### 4.5 扩展寄存器兼容

当目标模块变量溢出到扩展寄存器时（`mvarSlot >= ModuleVarSlots`），XLOAD_MVAR/XSTORE_MVAR 需要走 ExtendedRegs 路径：

```csharp
case OpCode.XLOAD_MVAR:
{
    int targetId = regs[Reg(op.B, rb)].ToInt();
    // 验证 targetId...
    ref var targetInst = ref Pool.Instances[targetId];
    var targetProgram = Modules.Get(targetInst.ModuleSlot);
    int mvarSlot = targetProgram.ExportTable.Variables[op.C].MvarSlot;

    if (mvarSlot < VMConstants.ModuleVarSlots)
    {
        // 固定寄存器路径
        fixed (long* targetRaw = targetInst.Registers.Raw)
        {
            Number* targetRegs = (Number*)targetRaw;
            regs[Reg(op.A, rb)] = targetRegs[VMConstants.ModuleVarRegBase + mvarSlot];
        }
    }
    else
    {
        // 扩展寄存器路径
        Number[] xregs = Pool.ExtendedRegs[targetId];
        if (xregs == null) { inst.ErrorFlag = VMError.PanicIllegalInstruction; return; }
        regs[Reg(op.A, rb)] = xregs[mvarSlot - VMConstants.ModuleVarSlots];
    }
    inst.IP++;
    break;
}
```

> 实现注意：XLOAD_MVAR/XSTORE_MVAR 访问的是**目标实例**的寄存器，不是当前实例。这意味着需要 `fixed` pin 目标实例的寄存器数组。这与 LOAD_MVAR（当前实例已 pin）不同。性能影响：每次 XLOAD/XSTORE 多一次 `fixed` pin，约 +1-2 ns。可通过缓存优化（同一 Tick 内对同一目标实例的连续访问复用 pin）。

### 4.6 执行实现方案

XCALL 执行目标函数有两种实现方式：

#### 方案 E1：递归 ExecuteInstance（推荐 ✅）

在 XCALL 的 case 内，递归调用 `ExecuteInstance(ref targetInst)`：

```csharp
case OpCode.XCALL:
{
    // Phase 1-4: 准备、深度检查、保存状态、参数复制...

    // Phase 5: 递归执行目标实例
    ref var targetInst = ref Pool.Instances[targetId];
    targetInst.IP = targetFuncEntry.EntryIP;
    targetInst.RegisterBase = 0;
    ExecuteInstance(ref targetInst);

    // Phase 6: 恢复...
}
```

优点：
- ✅ 实现最简单 — 复用现有 ExecuteInstance 逻辑
- ✅ 目标函数中的 CALL/RET_FUNC/LOAD_MVAR 等指令自然工作
- ✅ 调试器（ScriptDebugger）自然支持跨实例断点

缺点：
- ⚠️ C# 栈深度受限（每层 XCALL 消耗一帧 C# 栈）
- ⚠️ MaxXCallDepth=4 时最多 4 帧递归，可接受

#### 方案 E2：循环内状态切换

不递归，而是在同一 while 循环内切换 inst 引用，用标记识别 XCALL 返回点。

优点：
- ✅ 无 C# 递归（理论上可支持更深嵌套）

缺点：
- ⚠️ 实现复杂度高 — 需要管理多套 code/consts/regs 指针
- ⚠️ `fixed` 语句嵌套问题 — 切换实例需要 unpin 旧实例、pin 新实例

**决策**：选择 **E1 递归 ExecuteInstance**。MaxXCallDepth=4 时 C# 栈消耗约 4KB（每帧约 1KB 局部变量），在可接受范围内。如果未来需要支持更深嵌套（不太可能），可切换到 E2。

### 4.7 XCALL 中目标函数的执行约束

| 约束 | 描述 | 保证方式 |
|------|------|---------|
| 无 yield/wait | 目标函数不可挂起 | Y1-Plus 编译期保证（§五） |
| 无 cleanup/defer | 目标函数不可使用 using/defer | 编译期检查（简化实现） |
| 单返回值 | 通过 r0 返回 Number | 与现有函数调用一致 |
| 确定性 | 指令流决定调用顺序 | 同步执行，无调度不确定性 |

> 关于 defer/using 限制：C-1 阶段禁止 @export 函数包含 using/defer。这是因为 cleanup 栈是实例级的（VMInstanceState.CleanupStack），跨实例调用会混淆 cleanup 归属。C-2 或之后可考虑解除此限制（需要 cleanup 栈的实例隔离设计）。

### 4.8 错误处理

| 错误场景 | 行为 | 错误码 |
|---------|------|--------|
| targetInstanceId 越界（< 0 或 >= MaxInstances） | 设置 ErrorFlag，终止调用方 | `VMError.PanicInvalidInstanceId` |
| 目标实例不存活 (`!IsAlive`) | 设置 ErrorFlag，终止调用方 | `VMError.PanicInvalidInstanceId` |
| 目标模块无 ExportTable | 设置 ErrorFlag，终止调用方 | `VMError.PanicExportNotFound` |
| exportFuncIndex 越界 | 设置 ErrorFlag，终止调用方 | `VMError.PanicExportNotFound` |
| exportVarIndex 越界 | 设置 ErrorFlag，终止调用方 | `VMError.PanicExportNotFound` |
| XSTORE_MVAR 目标为只读 (`Writable=false`) | 编译期拒绝（不生成指令） | 编译错误 |

> 运行时错误通过 `inst.ErrorFlag` 机制传播，与现有 VM 错误处理一致。编译期能检查的尽量编译期检查（零运行时开销）。

---

## 五、Y1-Plus 编译期 yield 禁止

### 5.1 设计原则

**纯编译期保证，运行时零负担。** `@export` 标记的函数（及其传递调用的所有函数）不生成任何 yield/wait 相关字节码。

### 5.2 算法：Yield-Taint 分析

```
1. 扫描所有函数，标记直接包含 yield/wait/wait_for 语句的函数为 "yields"
2. 传递闭包：如果函数 A 调用函数 B，且 B 标记为 "yields"，则 A 也标记为 "yields"
3. 对 @export func：必须为 "non-yields"，否则报编译错误
```

### 5.3 检查范围

| 检查项 | 编译器行为 | 示例 |
|--------|-----------|------|
| 直接 yield | ❌ 编译错误 | `@export func f() { yield; }` → error |
| yield 在循环/条件中 | ❌ 编译错误 | `@export func f() { while x { yield; } }` → error |
| 调用可能 yield 的函数 | ❌ 编译错误 | `@export func a() { b(); }` + `func b() { yield; }` → error |
| 调用纯函数 | ✅ 允许 | `@export func a() { return b(); }` + `func b() { return 1; }` → ok |
| wait/wait_for | ❌ 编译错误 | 与 yield 同等处理 |

### 5.4 实现位置

在 `BytecodeCompiler` 中，函数编译完成后（所有函数体已解析）执行 yield-taint 分析：

```csharp
// 步骤 1: 标记直接包含 yield/wait 的函数
foreach (var func in functions)
    func.MayYield = ContainsYieldOrWait(func.Body);

// 步骤 2: 传递闭包
bool changed = true;
while (changed) {
    changed = false;
    foreach (var func in functions) {
        if (func.MayYield) continue;
        foreach (var callee in func.CalledFunctions) {
            if (callee.MayYield) {
                func.MayYield = true;
                changed = true;
            }
        }
    }
}

// 步骤 3: 检查 @export 函数
foreach (var func in exportedFunctions) {
    if (func.MayYield)
        Error($"@export function '{func.Name}' may yield (directly or via called functions). " +
              "Service functions must complete synchronously.");
}
```

### 5.5 defer/using 限制（C-1 阶段）

C-1 阶段额外禁止 @export 函数包含 `defer`/`using` 语句：

```ffs
@export func bad_example() {
    using SomeResource() {   // ❌ 编译错误：@export functions cannot use defer/using
        // ...
    }
}
```

原因：cleanup 栈是实例级状态，递归 ExecuteInstance 会在目标实例的 cleanup 栈上操作，返回时需要正确清理。为简化 C-1 实现，禁止 @export 函数使用 defer/using。

---

## 六、嵌套深度检查

### 6.1 运行时计数

```csharp
// VMWorld 新增
private int _xcallDepth;
private int _maxXCallDepth = 4;        // 默认 4
private bool _xcallDepthWarning = true; // 默认 Warn 模式
```

### 6.2 检查逻辑

```csharp
// XCALL 入口
++_xcallDepth;
if (_xcallDepthWarning && _xcallDepth > _maxXCallDepth)
{
    // Warn 模式：发出警告，继续执行
    OnXCallDepthWarning?.Invoke(_xcallDepth, _maxXCallDepth);
}

// XCALL 出口（Phase 6 恢复后）
--_xcallDepth;
```

### 6.3 两种模式

| MaxXCallDepth | XCallDepthWarning | 行为 |
|---------------|-------------------|------|
| 4（默认） | true（默认） | 超过 4 层 → 警告日志，继续执行 |
| 任意 N | true | 超过 N 层 → 警告日志，继续执行 |
| 任意 | false | 不检查，不警告（Unlimited 模式） |

### 6.4 性能影响

| 操作 | 耗时 | 说明 |
|------|------|------|
| `++_xcallDepth` | ~0.3 ns | int increment |
| `_xcallDepthWarning &&` 短路 | ~0.3 ns | bool check |
| `--_xcallDepth` | ~0.3 ns | int decrement |
| **总计** | ~1 ns | XCALL 总 overhead 15→16 ns (+6.7%) |

> Unlimited 模式（`_xcallDepthWarning = false`）：`if` 第一个条件短路，后续 compare 不执行。overhead 与 Warn 模式相同（~1 ns）。

### 6.5 C-1.5 配置化（远期）

C-1 阶段使用硬编码默认值（MaxXCallDepth=4, Warn=true）。C-1.5 阶段引入 VMConfig：

```csharp
public class VMConfig
{
    public int MaxXCallDepth = 4;
    public bool XCallDepthWarning = true;
}
```

---

## 七、实施 Checklist（Lang-6 行动项）

### Phase 1: 基础设施

- [ ] **OpCode 定义**：在 `OpCode.cs` 新增 `XCALL = 52`, `XLOAD_MVAR = 53`, `XSTORE_MVAR = 54`
- [ ] **ExportTable 数据结构**：新增 `ExportTable`, `ExportVarEntry`, `ExportFuncEntry` 类型
- [ ] **VMProgram 扩展**：新增 `ExportTable` 字段，构造函数新增参数
- [ ] **XCallFrame 数据结构**：新增 `XCallFrame` 结构体
- [ ] **VMWorld 新增字段**：`_xcallStack[]`, `_xcallDepth`, `_maxXCallDepth`, `_xcallDepthWarning`
- [ ] **VMError 扩展**：新增 `PanicInvalidInstanceId`, `PanicExportNotFound`

### Phase 2: Lexer / Parser / AST

- [ ] **Lexer**：新增 `Export` TokenType，识别 `@export` 关键字
- [ ] **Parser**：在模块顶层声明前检测 `@export` 前缀
- [ ] **AST 扩展**：VarDeclStmt / FuncDecl 新增 `IsExported` 字段

### Phase 3: 编译器

- [ ] **导出表生成**：收集 @export 声明，生成 ExportTable，传入 VMProgram
- [ ] **Y1-Plus yield-taint 分析**：扫描 + 传递闭包 + @export 函数检查
- [ ] **defer/using 限制**：@export 函数禁止 defer/using（C-1 阶段）
- [ ] **XCALL emit**：编译器生成 XCALL 指令（C-1 阶段通过显式 API 或辅助函数）
- [ ] **XLOAD_MVAR / XSTORE_MVAR emit**：编译器生成跨实例变量访问指令

### Phase 4: VM 执行引擎

- [ ] **XCALL 执行逻辑**：VMWorld.cs 新增 case，实现 §4.2 完整流程
- [ ] **XLOAD_MVAR 执行逻辑**：读取目标实例模块变量（含扩展寄存器兼容）
- [ ] **XSTORE_MVAR 执行逻辑**：写入目标实例模块变量（含扩展寄存器兼容）
- [ ] **嵌套深度检查**：`_xcallDepth` inc/dec + Warn 日志

### Phase 5: 测试套件

| ID | 场景 | 验证点 |
|----|------|--------|
| XC01 | 基础 XCALL | 跨实例函数调用，返回值正确 |
| XC02 | XCALL 带参数 | 多参数传递，scratch zone 复制正确 |
| XC03 | XLOAD_MVAR 基础读取 | 读取目标实例 @export var 值正确 |
| XC04 | XSTORE_MVAR 基础写入 | 写入目标实例 @export var，再读回验证 |
| XC05 | @export const 只读 | XLOAD_MVAR 可读，XSTORE_MVAR 编译错误 |
| XC06 | 多实例独立性 | 两个目标实例的变量互不干扰 |
| XC07 | 嵌套 XCALL | A→B→C 两层嵌套，返回值正确传递 |
| XC08 | 嵌套深度警告 | 超过 MaxXCallDepth 时触发警告回调 |
| XC09 | Y1-Plus yield 禁止 | @export func 含 yield → 编译错误 |
| XC10 | Y1-Plus 传递 yield | @export func 调用含 yield 的函数 → 编译错误 |
| XC11 | 导出表正确性 | 编译后 VMProgram.ExportTable 包含正确的条目 |
| XC12 | 错误处理 | 无效 instanceId / 未导出成员 → ErrorFlag |
| XC13 | 扩展寄存器兼容 | 目标模块变量超过 ModuleVarSlots 时 XLOAD/XSTORE 正确路由 |
| XC14 | defer/using 限制 | @export func 含 defer → 编译错误 |
| XC15 | 与现有功能集成 | XCALL 结合模块变量、include、黑板 Syscall |

### Phase 6: 回归验证

- [ ] **B01-B06 benchmark 确认无回归**
- [ ] **1111 项现有测试全通过**
- [ ] **Reg() 热路径无改动验证**（新 OpCode 不修改 Reg()）

---

## 八、风险与回归防护

### 8.1 Reg() 热路径不变量

**强制约束**：三个新 OpCode（XCALL, XLOAD_MVAR, XSTORE_MVAR）**不修改 Reg() 函数**。

当前 Reg()：
```csharp
private static int Reg(int r, int regBase)
{
    return r < VMConstants.ScratchZoneSize ? r : r + regBase;
}
```

新 OpCode 通过以下方式访问寄存器：
- 调用方寄存器：使用 `Reg(op.X, rb)` 映射（与现有 OpCode 一致）
- 目标实例寄存器：通过 `Pool.Instances[targetId].Registers.Raw` 直接访问（绝对地址），或递归 ExecuteInstance 时由目标实例的 `rb` 自行映射

> 这遵循 Lang-1 确立的**专用指令优化原则**：任何需要特殊寄存器寻址的语言特性，必须使用专用 OpCode 而非在 Reg() 热路径中增加分支。

### 8.2 性能影响分析

| 影响面 | 分析 | 结论 |
|--------|------|------|
| ExecuteInstance switch-case 增加 3 个 case | JIT 跳转表增大（51→54 项）。对热循环影响：L1 icache 压力微增 | 可忽略 |
| XCALL 本身开销 | ~15-18 ns/次。每帧 50 次 = 0.75-0.9 μs = 0.005% 帧预算 | 可忽略 |
| XLOAD_MVAR/XSTORE_MVAR 开销 | ~3-8 ns/次（含目标实例 fixed pin）。远快于 XCALL 走 getter | 比 XCALL getter 快 3-5× |
| 嵌套深度检查 | +1 ns/XCALL（3 条 int 指令） | 可忽略 |
| 新字段 `_xcallDepth` 等 | VMWorld 增加 ~20 bytes。L1 cache 影响极小 | 可忽略 |

### 8.3 快照/回滚兼容

- **XCallFrame 栈**：存储在 VMWorld 中，不在 VMInstanceState 中。由于 XCALL 是同步的（不 yield），XCallFrame 栈在 Tick 结束时必然为空。因此**无需快照/恢复 XCallFrame**。
- **ExportTable**：存储在 VMProgram（ROM）中。不参与快照。
- **`_xcallDepth`**：Tick 结束时必然为 0（所有 XCALL 已返回）。无需快照。

### 8.4 调试器兼容

- ScriptDebugger 需要支持跨实例调用栈显示（递归 ExecuteInstance 时断点可能在目标实例中触发）
- C-1 阶段最小支持：断点在 XCALL 目标函数内正常工作（递归 ExecuteInstance 自然支持）
- 完整的跨实例调用栈显示为增强项，可后续实现

---

## 附录 A：OpCode 空间规划

| 范围 | 用途 | 状态 |
|------|------|------|
| 0-51 | 现有 OpCode（NOP ~ SENTINEL） | ✅ 已使用 |
| 52-54 | Lang-6: XCALL, XLOAD_MVAR, XSTORE_MVAR | ⏳ 本 Spec |
| 55-255 | 预留 | 可用 |

> 256 个 OpCode 槽位中已使用 55 个（含 Lang-6），剩余 201 个。充裕。

## 附录 B：与后续阶段的接口

| 后续阶段 | 依赖的 C-1 基础设施 | 说明 |
|---------|------|------|
| C-1.5 (A1/A2 自动退化) | ExportTable 的函数/变量信息 | 编译器 AST 分析纯 getter/setter → 替换 XCALL 为 XLOAD/XSTORE |
| C-1.5 (VMConfig) | `_maxXCallDepth` / `_xcallDepthWarning` 字段 | 从硬编码改为 VMConfig 配置读取 |
| C-2 (svc.member 语法) | ExportTable + 跨模块导出表解析 | Parser 解析 `svc.member`，编译器查 ExportTable 路由 |
| C-2 (@inline) | ExportTable.Functions 的 AST 访问 | 内联需要访问目标函数体 AST |
| C-2 (LSP 诊断) | ExportTable 结构 | LSP 遍历 ExportTable 提供补全/诊断 |
| C-3 (@force_inline) | C-2 @inline 基础 | 升级为编译错误 |
