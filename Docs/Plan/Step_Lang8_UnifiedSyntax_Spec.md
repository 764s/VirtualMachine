# Lang-8: Unified Syntax Spec 设计文档（C-2 统一语法 + @inline + LSP）

> **来源**：VM_Summary.md §七 Lang 表、D_SkillScripting.md Q4 核心结论（R23-R26）
>
> **前置**：Lang-6 ✅（XCALL/XLOAD_MVAR/XSTORE_MVAR + @export + ExportTable）、Lang-7 ✅（A1/A2 自动退化 + VMConfig）
>
> **状态**：🔄 设计文档
>
> **目标**：实现 `svc.member` 统一点号语法（编译器自动路由 XCALL/XLOAD_MVAR/XSTORE_MVAR），`@inline` hint 声明，LSP 深度/内联诊断。

---

## 一、语法设计

### 1.1 统一点号语法

```ffs
// 调用服务函数 → 编译为 XCALL（或 A1/A2 退化）
var result = svc.add(10, 32)

// 读取服务变量 → 编译为 XLOAD_MVAR
var hp = svc.hp

// 写入服务变量 → 编译为 XSTORE_MVAR
svc.hp = 500
```

**区分规则**：
- `svc.name(args)` → 函数调用（有括号）
- `svc.name` → 变量访问（无括号）

### 1.2 与结构体字段访问共存

`svc.member` 使用的 AST 节点与 `struct.field`（FieldAccessExpr）不同。
编译器根据 target 变量是否绑定为服务引用来区分：
- **服务引用变量**：路由到 XCALL/XLOAD_MVAR/XSTORE_MVAR
- **结构体变量**：走现有 ResolveFieldAccess 路径

### 1.3 @inline hint 关键字

```ffs
@inline @export func get_hp(): int {
    return hp
}
```

- `@inline` 为编译 hint，不改变语义
- 仅对 `@export` 函数有效（编译器在非 @export 函数上忽略 @inline）
- `ExportFuncEntry` 新增 `IsInlineHint` 字段

---

## 二、跨模块编译协议

### 2.1 服务绑定（ServiceBinding）

调用方编译器需要知道服务模块的导出表。设计方案：**传入预编译 ExportTable**。

```csharp
/// <summary>
/// Service binding: maps a local variable name to a target module's ExportTable.
/// The compiler uses this to resolve svc.member syntax.
/// </summary>
public class ServiceBinding
{
    public string VarName;      // caller 中的变量名（如 "svc"）
    public ExportTable Exports;  // 目标模块的 ExportTable
}
```

### 2.2 新 Compile 重载

```csharp
public CompileResult Compile(string source, string entryFunc,
    Dictionary<string, int> syscalls, SyscallTable syscallTable,
    IFileResolver fileResolver, string filePath,
    ServiceBinding[] serviceBindings)
```

### 2.3 编译器状态

编译器内部维护 `_serviceBindings`：`Dictionary<string, ServiceBinding>`（varName → binding）。

变量在 `_serviceBindings` 中 → 为服务引用，`svc.member` 走 XCALL/XLOAD/XSTORE 路径。

---

## 三、AST 扩展

### 3.1 新 AST 节点

```csharp
// 跨实例函数调用：svc.func(args)
public class MemberCallExpr : Expr
{
    public string TargetVarName;     // 服务引用变量名
    public string MemberName;        // 目标函数名
    public List<Expr> Arguments;     // 调用参数
}

// 跨实例变量读取：svc.var（读取上下文）
// 不需要单独节点 — FieldAccessExpr 已可表达 svc.var
// 编译器根据 _serviceBindings 区分服务 vs 结构体
```

**设计决策**：函数调用需要新节点 `MemberCallExpr`（因为现有 `CallExpr` 只有 `FunctionName` 字符串）。
变量访问复用 `FieldAccessExpr`，编译器在 CompileExpr 中检查 target 是否为服务引用。

### 3.2 Parser 改动

在 `ParsePostfix()` 中，当解析到 `ident.member` 时：
- 如果后面跟 `(`，生成 `MemberCallExpr`
- 否则生成 `FieldAccessExpr`（与现有一致，编译器区分）

---

## 四、编译器路由决策树

```
CompileExpr(MemberCallExpr):
  1. 在 _serviceBindings 中查找 TargetVarName
  2. 在 ExportTable.Functions 中按名称查找 MemberName
  3. 根据 DegradationType：
     a. Getter → 发射 XLOAD_MVAR(dest, svcReg, DegradeMvarSlot)
     b. Setter → 发射 XSTORE_MVAR(DegradeMvarSlot, svcReg, arg0Reg)
     c. None → 发射参数到 r0..rN + XCALL(dest, svcReg, funcIdx)

CompileExpr(FieldAccessExpr where target is service ref):
  1. 在 _serviceBindings 中查找 target 变量名
  2. 在 ExportTable.Variables 中按名称查找 FieldName
  3. 发射 XLOAD_MVAR(dest, svcReg, varIdx)

CompileAssign(target = FieldAccessExpr where target is service ref):
  1. 在 _serviceBindings 中查找
  2. 在 ExportTable.Variables 中查找
  3. 检查 Writable
  4. 发射 XSTORE_MVAR(varIdx, svcReg, valueReg)
```

---

## 五、@inline 处理

### 5.1 Lexer

`ScanAtKeyword` 增加 `"inline"` → `TokenType.Inline`。

### 5.2 Parser

`@inline` 必须在 `@export` 前或后（都支持），最终标记到 `FuncDecl.IsInline`。

### 5.3 ExportTable

`ExportFuncEntry` 新增 `bool IsInlineHint` 字段。

### 5.4 编译器

当前阶段 `@inline` 仅存储 hint 标记，不改变编译行为（A5 深度内联为远期计划）。
LSP 可利用此标记提供诊断建议。

---

## 六、LSP 诊断扩展

### 6.1 调用链深度诊断

当编译器在 MemberCallExpr 路由时检测到 XCALL（非退化），发出 Warning：
```
"Cross-instance call 'svc.func()' generates XCALL (depth-sensitive). Consider @inline for simple getters/setters."
```

Severity = 2 (Warning)，不阻止编译。

### 6.2 内联建议

当 ExportFuncEntry.Degradation != None 且没有标记 @inline 时，发出 Hint：
```
"Getter 'get_hp' is auto-degraded to XLOAD_MVAR (fast path). @inline hint is not needed."
```

当函数未退化但标记了 @inline 时：
```
"@inline hint on 'compute' — function not eligible for auto-degradation (complex body)."
```

---

## 七、测试用例

| ID | 名称 | 描述 |
|----|------|------|
| US01 | 基础函数调用 | `svc.add(10, 32)` → XCALL → 结果 42 |
| US02 | 基础变量读取 | `svc.hp` → XLOAD_MVAR → 值 999 |
| US03 | 基础变量写入 | `svc.hp = 500` → XSTORE_MVAR → 验证 |
| US04 | getter 退化 | `svc.get_hp()` → XLOAD_MVAR（A1 退化） |
| US05 | setter 退化 | `svc.set_hp(500)` → XSTORE_MVAR（A2 退化） |
| US06 | 多参数函数 | `svc.compute(a, b, c)` → XCALL |
| US07 | 混合调用 | 同一函数中读变量 + 调函数 + 写变量 |
| US08 | @inline hint 解析 | `@inline @export func` 编译成功，ExportFuncEntry.IsInlineHint = true |
| US09 | @inline 非 export 忽略 | `@inline func` → warning（非 @export 无效） |
| US10 | 未导出成员错误 | `svc.unknown()` → 编译错误 |
| US11 | 只读变量写入错误 | `svc.constVar = 1` → 编译错误 |
| US12 | 服务绑定缺失错误 | 无 ServiceBinding 时用 svc.xxx → 走普通 FieldAccess 路径 |
| US13 | 结构体字段共存 | 同一函数中 `pos.x`（结构体）和 `svc.hp`（服务）共存 |
| US14 | LSP 诊断 | 验证 XCALL 深度 Warning 和内联建议 |

---

## 八、性能影响

- **纯编译期特性**：`svc.member` → XCALL/XLOAD/XSTORE 映射在编译期完成
- **ExecuteInstance 热循环无改动**：不新增 OpCode，复用 Lang-6 的 XCALL/XLOAD_MVAR/XSTORE_MVAR
- **Reg() 无改动**
- **预期**：B01-B06 benchmark 零回归
