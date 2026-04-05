# B-δ4 SN2 结构体字面量构造语法

> 状态：✅ 完成
> 依赖：struct ✅（S1-S3 + SN1 已完成）
> 来源：[VM_Summary.md §七](../VM_Summary.md) B-δ4 + [Step9_StructFlatten.md §SN2](Step9_StructFlatten.md)

---

## 一、目标

支持结构体字面量构造语法，替代逐字段赋值：

```ffs
struct Vec2 { x: int; y: int; }

// 当前写法（已支持）
var a: Vec2;
a.x = 1;
a.y = 2;

// 新写法（SN2 目标）
var b: Vec2 = Vec2 { x: 1, y: 2 };

// 嵌套结构体字面量
struct Rect { min: Vec2; max: Vec2; }
var r: Rect = Rect { min: Vec2 { x: 1, y: 2 }, max: Vec2 { x: 3, y: 4 } };

// 赋值位置
var c: Vec2;
c = Vec2 { x: 5, y: 6 };
```

纯编译器 sugar，不影响运行时。编译后与逐字段 `LOAD_CONST`/表达式求值等价。

---

## 二、妥协点

| 妥协 | 原因 | 消除时间点 |
|------|------|-----------|
| 字段顺序必须匹配声明顺序 | 简化编译器，无需 field-name→offset 运行时查找；当前结构体字段少（2-5），顺序记忆无负担 | 当字段数常规超过 5 时可考虑支持乱序 |
| 不支持部分字段省略（必须全部提供） | 与默认零初始化语义一致，避免"半初始化"歧义 | 若需要可扩展为省略字段→零值 |

---

## 三、子任务 Checklist

### 3.1 AST 层

- [x] **SN2-A1**: 新增 `StructLiteralExpr` AST 节点
  - `string TypeName`：结构体类型名
  - `List<(string FieldName, Expr Value)> Fields`：字段名→值表达式列表
  - `NodeKind.StructLiteral` 枚举值

### 3.2 Parser 层

- [x] **SN2-P1**: 在 `ParseExpression` 路径中识别结构体字面量
  - 语法：`TypeName { field1: expr1, field2: expr2 }`
  - 触发条件：当前 token 是 Identifier 且下一个 token 是 `{`，且 Identifier 是已知结构体类型名
  - 注意：需要区分 block `{ ... }` 和 struct literal `TypeName { ... }`
  - Parser 需要知道已声明的 struct 类型名（从已解析的 StructDecl 收集）

- [x] **SN2-P2**: 支持嵌套字面量
  - 字段值位置可以递归出现 `TypeName { ... }`
  - 由 `ParseExpression` 自然递归处理

### 3.3 Compiler 层

- [x] **SN2-C1**: `CompileVarDecl` 处理 `StructLiteralExpr` 作为初始化器
  - 验证类型名匹配
  - 验证字段名和数量匹配（使用 `_flatStructInfo`）
  - 对每个字段值调用 `CompileExpr` 并 emit 到对应寄存器偏移

- [x] **SN2-C2**: `CompileExpr` 处理 `StructLiteralExpr`（用于赋值表达式 `a = Vec2 { ... }`）
  - 在 `AssignExpr` 的 struct 赋值分支中识别 `StructLiteralExpr`
  - 编译各字段值到目标寄存器偏移

- [x] **SN2-C3**: 嵌套字面量编译
  - 递归处理嵌套 `StructLiteralExpr` 的字段映射
  - 使用 `_flatStructInfo` 获取拍平后的寄存器偏移

- [x] **SN2-C4**: 编译错误检查
  - 类型名不存在 → 报错
  - 字段数量不匹配 → 报错
  - 字段名不匹配 → 报错
  - 子字段类型与预期不一致 → 报错

### 3.4 F4 生命周期分析

- [x] **SN2-F1**: `AnalyzeVariableLifetimes` 的 `WalkExpr` 中处理 `StructLiteralExpr`
  - 递归 walk 各字段值表达式

### 3.5 LSP 层

- [x] **SN2-L1**: LSP 符号分析适配（如有需要）
  - `StructLiteralExpr` 中的标识符引用不应干扰符号解析
  - hover / completion 不需要特殊处理（字面量内部不触发补全）

### 3.6 测试

- [x] **SN2-T1**: CS31 — 基础 struct 字面量 `var v: Vec2 = Vec2 { x: 1, y: 2 }; sys_log(v.x + v.y)` → 3
- [x] **SN2-T2**: CS32 — 字段值为表达式 `var v: Vec2 = Vec2 { x: 1 + 2, y: 3 * 4 }` → x=3, y=12
- [x] **SN2-T3**: CS33 — 嵌套字面量 `var r: Rect = Rect { min: Vec2 { x: 1, y: 2 }, max: Vec2 { x: 3, y: 4 } }` → sum=10
- [x] **SN2-T4**: CS34 — 赋值位置字面量 `var v: Vec2; v = Vec2 { x: 5, y: 6 }` → 11
- [x] **SN2-T5**: CS35 — 字段数不匹配编译错误
- [x] **SN2-T6**: CS36 — 字段名不匹配编译错误
- [x] **SN2-T7**: CS37 — 未知类型编译错误
- [x] **SN2-T8**: CS38 — 三层嵌套字面量

---

## 四、完成条件

1. ✅ `StructLiteralExpr` AST 节点 + `NodeKind.StructLiteral`
2. ✅ Parser 识别 `TypeName { field: expr, ... }` 语法
3. ✅ Compiler 在 VarDecl 和 AssignExpr 中正确处理 struct literal
4. ✅ 嵌套 struct literal 编译正确
5. ✅ 编译错误检查（字段不匹配 / 类型不匹配）
6. ✅ CS31-CS38 测试通过
7. ✅ 全部 990 Assert × 2 模式全通过（无回退）

---

## 五、功能展望

| ID | 内容 | 触发条件 |
|----|------|---------|
| SN2-F1 | 支持字段乱序（按名称匹配而非位置匹配） | 当字段数常规超过 5 时 |
| SN2-F2 | 支持部分字段省略（省略字段 → 零值） | 有业务需求时 |
| SN2-F3 | LSP 结构体字面量内字段名补全 | LSP 增强需求时 |

## 六、优化展望

| ID | 内容 | 预期收益 |
|----|------|---------|
| SN2-O1 | 常量折叠：全常量字面量编译为单次批量 LOAD_CONST | 减少指令数，大 struct 初始化更快 |

## 七、风险点

无新增风险。struct literal 为纯编译器 sugar，不影响运行时语义、快照、回滚、GC。
