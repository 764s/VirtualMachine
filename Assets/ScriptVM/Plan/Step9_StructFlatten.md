# 步骤 9：结构体编译期拍平验证

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序的步骤 9。
> **状态**：⬜ 待开始。
> **前置**：步骤 8 已全部完成（279 项 Assert 通过）。
> **来源**：VM_Tracer_Bullet.md §十二 第 5 项；设计见 VM_Runtime_Layout.md §5.2。
>
> **核心原则**：结构体是纯编译期概念——语言层允许结构体只是为了作者表达舒服。运行时不存在结构体对象，不存在堆实例。编译器将结构体拍平为连续寄存器槽位，结构体赋值退化为寄存器区块复制。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| S4（结构体作为函数参数 / 返回值的寄存器传递）不在本步骤实现 | VM_Summary 标注"展望项，最晚步骤 10 前如需编辑器展示" | 步骤 10 前（如需） |
| 不支持嵌套结构体（struct 字段为另一个 struct） | 先验证单层结构体拍平正确性 | 后续步骤按需扩展 |
| 不支持结构体方法 | Architecture Rule 6 + 14 禁止高级构造 | 无需消除（设计决策） |
| 不支持结构体字面量构造语法 | 先支持逐字段赋值，构造语法后续按需添加 | 后续步骤按需扩展 |
| 结构体字段类型仅支持标量（int/number） | 与 VM 寄存器物理纪律一致——每个字段占 1 个 VMSlot | 嵌套结构体支持时扩展 |

---

## 二、基础设施盘点

以下组件在步骤 1-8 中已就位，步骤 9 直接复用：

| 组件 | 状态 | 说明 |
|------|------|------|
| `StructDecl` AST 节点 | ✅ 已定义 | `ASTNode.cs` — `Name` + `List<StructField>` |
| `StructField` 类 | ✅ 已定义 | `ASTNode.cs` — `Name` + `TypeName` |
| `FieldAccessExpr` AST 节点 | ✅ 已定义 | `ASTNode.cs` — `Target` (Expr) + `FieldName` |
| `NodeKind.StructDecl` | ✅ 已有 | 枚举值已存在 |
| `NodeKind.FieldAccess` | ✅ 已有 | 枚举值已存在 |
| `ModuleNode.Structs` | ✅ 已有 | `List<StructDecl>`，Parser 可直接填充 |
| `VarDeclStmt.TypeName` | ✅ 已有 | 用于编译期解析变量的结构体类型 |
| `ParamDecl.TypeName` | ✅ 已有 | 用于函数参数的结构体类型（S4 展望） |
| `MOVE` OpCode | ✅ 已有 | 用于逐字段寄存器复制（结构体赋值） |
| 寄存器布局 r16-r47 local | ✅ 已有 | 结构体变量分配连续寄存器槽位 |

### 需要新增

| 组件 | 说明 |
|------|------|
| `TokenType.Struct` | Lexer 关键字（当前缺失） |
| `TokenType.Dot` | Lexer 分隔符（当前缺失，用于 `expr.field` 语法） |
| Parser `struct` 声明解析 | `ParseStructDecl()` |
| Parser `.field` 表达式解析 | 在 `ParsePrimary()` / `ParsePostfix()` 中处理 `.` 运算符 |
| 编译器 struct 类型表 | `Dictionary<string, StructDecl>` — 按类型名查找字段列表 |
| 编译器 struct 变量 → 寄存器映射 | 一个 struct 变量占 N 个连续寄存器（N = 字段数） |
| 编译器 FieldAccess → 寄存器偏移 | `base_reg + field_index` |

---

## 三、子任务总览

```
Sub-task A: Lexer 扩展（struct 关键字 + Dot 分隔符）
Sub-task B: Parser 解析 struct 声明（S1）
Sub-task C: Parser 解析字段访问表达式（expr.field）
Sub-task D: 编译器 struct 类型表构建 + struct 变量寄存器分配（S2）
Sub-task E: 编译器 FieldAccess 读写 + struct 赋值（S3）
Sub-task F: 端到端测试 + 回归验证
Sub-task G: 文档更新
```

依赖关系：`A → B, C`；`B → D → E`；`C → E`；`F` 依赖全部完成；`G` 最后。

---

## Sub-task A: Lexer 扩展（struct 关键字 + Dot 分隔符）

### 意图

当前 Lexer 不识别 `struct` 关键字和 `.` 运算符。需要新增以支持 struct 声明和字段访问语法。

### 具体变更

- [ ] A.1 **Lexer.cs — TokenType 枚举**：添加 `Struct`（关键字区）和 `Dot`（分隔符区）
- [ ] A.2 **Lexer.cs — _keywords 字典**：添加 `{ "struct", TokenType.Struct }`
- [ ] A.3 **Lexer.cs — ScanToken()**：在分隔符处理中添加 `'.'` → `TokenType.Dot`

### 验收标准

- `struct` 被识别为关键字 Token（而非 Identifier）
- `.` 被识别为 `Dot` Token
- 不破坏现有 Lexer 测试

---

## Sub-task B: Parser 解析 struct 声明（S1）

### 意图

支持在模块顶层声明结构体，语法为：
```
struct TypeName {
    field1: type
    field2: type
}
```
解析结果填入 `ModuleNode.Structs`。

### 具体变更

- [ ] B.1 **Parser.Parse()**：在顶层循环中添加 `case TokenType.Struct` → 调用 `ParseStructDecl()`，结果加入 `module.Structs`
- [ ] B.2 **Parser**：实现 `ParseStructDecl()`：
  - 消费 `struct` → 读取 Identifier（类型名）→ 消费 `{`
  - 循环解析字段：Identifier（字段名）→ `:` → Identifier（类型名）→ 可选 `;` 或换行
  - 消费 `}` → 返回 `StructDecl`
- [ ] B.3 **测试**：struct 声明解析为正确的 `StructDecl` AST 节点（字段名、字段类型）

### 验收标准

- `struct DamageInfo { level: int; ratio: number }` 解析为 `StructDecl("DamageInfo", [StructField("level","int"), StructField("ratio","number")])`
- 不破坏现有 Parser 测试

---

## Sub-task C: Parser 解析字段访问表达式（expr.field）

### 意图

支持 `variable.fieldName` 语法。`.` 作为后缀运算符，优先级高于所有二元运算符。

### 具体变更

- [ ] C.1 **Parser**：在表达式解析中（`ParsePrimary()` 返回后或新增 `ParsePostfix()` 层），处理 `.` Token：
  - 若当前 Token 为 `Dot`，消费 → 读取 Identifier → 返回 `FieldAccessExpr(left, fieldName)`
  - 支持链式访问（`a.b.c`，尽管本步骤只验证单层）
- [ ] C.2 **测试**：`x.level` 解析为 `FieldAccessExpr(IdentifierExpr("x"), "level")`

### 验收标准

- 字段访问可被 Parser 正确解析
- 优先级正确：`x.a + y.b` 解析为 `Add(FieldAccess(x,a), FieldAccess(y,b))`

---

## Sub-task D: 编译器 struct 类型表 + struct 变量寄存器分配（S2）

### 意图

编译器在编译开始前扫描所有 `StructDecl`，建立类型表。当遇到 `var d: DamageInfo` 时，分配 N 个连续寄存器（N = struct 字段数）。

### 设计

```
类型表: Dictionary<string, StructInfo>
  StructInfo:
    Name: string
    Fields: List<StructField>   // 有序字段列表
    SlotCount: int              // = Fields.Count（每字段 1 个 VMSlot）

变量分配（var d: DamageInfo）:
  _variables["d"] = nextVarReg          // 基址寄存器
  _structVarTypes["d"] = "DamageInfo"   // 记录变量的结构体类型
  nextVarReg += slotCount               // 连续占用 N 个寄存器
  例如: d → r16, d.level=r16, d.ratio=r17, d.targetHandle=r18
```

### 具体变更

- [ ] D.1 **BytecodeCompiler**：新增 `Dictionary<string, StructDecl> _structTypes` 类型表
- [ ] D.2 **BytecodeCompiler**：新增 `Dictionary<string, string> _structVarTypes` 记录每个变量的 struct 类型名（非 struct 变量不在此表中）
- [ ] D.3 **BytecodeCompiler.CompileModule()**：在 Pass 1（函数扫描）前，遍历 `module.Structs` 填充 `_structTypes`
- [ ] D.4 **BytecodeCompiler.CompileVarDecl()**：当 `TypeName` 在 `_structTypes` 中时，分配连续 N 个寄存器，记录到 `_structVarTypes`
- [ ] D.5 **BytecodeCompiler**：新增辅助方法 `int GetStructFieldReg(string varName, string fieldName)` → 返回 `base_reg + fieldIndex`
- [ ] D.6 **编译器 callerWindowSize 计算**：struct 变量占 N 个寄存器，`_callerWindowSize` 需正确反映实际局部寄存器占用
- [ ] D.7 **测试**：struct 变量声明后，编译器内部状态正确（可通过端到端 emit 指令序列间接验证）

### 验收标准

- `var d: DamageInfo` 在编译器中分配 3 个连续寄存器（假设 DamageInfo 有 3 个字段）
- 后续字段访问可解析到正确的寄存器偏移

---

## Sub-task E: 编译器 FieldAccess 读写 + struct 赋值（S3）

### 意图

1. **字段读取**：`d.level` 编译为对 `base_reg + fieldIndex` 寄存器的直接读取（无额外指令）
2. **字段写入**：`d.level = expr` 编译为 expr 结果写入 `base_reg + fieldIndex`
3. **结构体整体赋值**：`a = b`（两个同类型 struct 变量）编译为 N 条 `MOVE` 指令，逐字段复制

### 具体变更

- [ ] E.1 **BytecodeCompiler.CompileExpr()**：处理 `NodeKind.FieldAccess` — 查找变量基址 + 字段偏移，返回对应寄存器号
- [ ] E.2 **BytecodeCompiler.CompileAssign()**：当赋值目标为 `FieldAccessExpr` 时，将表达式结果写入字段对应的寄存器
- [ ] E.3 **BytecodeCompiler.CompileAssign()**：当赋值两端都是 struct 变量时，emit N 条 `MOVE`（逐字段复制）
- [ ] E.4 **BytecodeCompiler.CompileVarDecl()**：struct 变量若有初始化器且初始化器为另一个 struct 变量，emit N 条 `MOVE`
- [ ] E.5 **测试 S3a**：端到端 — struct 声明 + 字段逐个赋值 + 字段读取用于算术运算，验证结果正确
- [ ] E.6 **测试 S3b**：端到端 — 两个同类型 struct 变量，`a = b` 整体赋值后字段值相等
- [ ] E.7 **测试 S3c**：端到端 — struct 字段在 if/while/for 中正确读写

### 验收标准

- `d.level = 10` 编译为 `LOAD_CONST` 到字段对应寄存器
- `var x: int = d.level + d.ratio` 编译为从对应寄存器读取 + ADD
- `a = b`（struct 赋值）编译为 N 条 MOVE（字段数 = N）
- 字节码执行结果与预期一致

---

## Sub-task F: 端到端测试 + 回归验证

### 意图

确保 struct 功能完整可用，且不破坏现有 279 项 Assert。

### 具体变更

- [ ] F.1 **CompilerTests**：新增测试 — struct 声明 + 字段赋值 + 字段读取 + 算术（最小端到端）
- [ ] F.2 **CompilerTests**：新增测试 — struct 变量整体赋值（a = b），验证 N 条 MOVE
- [ ] F.3 **CompilerTests**：新增测试 — struct 字段用于 Syscall 参数
- [ ] F.4 **CompilerTests**：新增测试 — struct 字段用于条件分支（if d.level > 0）
- [ ] F.5 **CompilerTests**：新增测试 — 多个 struct 变量共存，寄存器不冲突
- [ ] F.6 **CompilerTests**：新增测试 — 编译错误：使用未声明的 struct 类型 → 编译报错
- [ ] F.7 **CompilerTests**：新增测试 — 编译错误：访问不存在的字段名 → 编译报错
- [ ] F.8 **GC 验证**：struct 操作不引入堆分配（复用现有 V1 GC 验证框架）
- [ ] F.9 **回归**：全部现有 279 项 Assert 仍通过

### 验收标准

- 所有新增测试通过
- 所有现有 279 项测试无回归
- GC 验证通过（struct 操作零分配）

---

## Sub-task G: 文档更新

### 具体变更

- [ ] G.1 **VM_Summary.md §五 当前实现状态**：更新 AST、编译器、测试数量
- [ ] G.2 **VM_Summary.md §七 推进顺序**：标记步骤 9 S1-S3 为 ✅
- [ ] G.3 **VM_Summary.md §4.4 OpCode 集**：如有新增 OpCode 则更新（预计无新增，struct 操作复用 MOVE）
- [ ] G.4 **本文件**：更新状态为 ✅，记录最终测试数量

---

## 四、风险分析

| # | 风险 | 影响 | 缓解措施 |
|---|------|------|---------|
| R1 | struct 变量占用多个寄存器，加速耗尽 local 区（r16-r47 = 32 槽） | 中 | 编译器检查：struct 变量分配超出 local 区上限时报错 |
| R2 | struct 赋值生成 N 条 MOVE，大 struct 性能退化 | 低 | 业务场景 struct 通常 2-5 字段；后续可考虑 COPY_BLOCK OpCode |
| R3 | struct 变量与函数调用寄存器窗口交互 | 中 | 本步骤不涉及 struct 作为函数参数（S4 展望）；局部 struct 变量随窗口偏移自然正确 |
| R4 | 字段访问解析与 Syscall 调用语法歧义（`a.b(c)` 是方法调用还是字段+函数调用？） | 低 | 本步骤不支持方法调用；`a.b` 只能是字段访问，`b()` 只能是函数/Syscall 调用 |

---

## 五、验收总览

| 条目 | 来源 | 描述 |
|------|------|------|
| S1 | VM_Summary §七 | Parser struct 声明 + 字段类型解析 |
| S2 | VM_Summary §七 | 编译器 struct → 连续寄存器槽位映射 |
| S3 | VM_Summary §七 | 结构体赋值 = 寄存器区块 COPY 验证 |
| 回归 | — | 现有 279 项 Assert 无回归 |
| GC | V1 框架 | struct 操作零 GC 分配 |

全部通过后，步骤 9 闭环。下一步：评估 S4（struct 函数参数传递）+ C4/F4 延期项，然后进入步骤 10（编辑器流程图投影）。
