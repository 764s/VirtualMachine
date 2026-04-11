# FFScript 语法参考

> **定位**：FFScript（`.ffs`）的完整语法参考文档。
>
> **相关文档**：[VM_Summary.md](../VM_Summary.md)（总览）、[VM_Script_Language_Decision.md](VM_Script_Language_Decision.md)（语言选型决策）、[Skills/](Skills/)（技能脚本示例）

---

## 一、概述

FFScript 是为 FFVM 设计的自定义领域特定语言（DSL），语法风格借鉴 Go。

**设计目标**：

- 零 GC、零动态分配
- `wait` / `defer` / `using` 为一等关键字
- 编译期寄存器分配，运行期纯值语义
- 可 memcpy 快照 / 回滚

**文件后缀**：`.ffs`

---

## 二、词法规则

### 2.1 注释

```ffs
// 行注释（直到行尾）

/// 文档注释（附加到紧随其后的函数或结构体声明）
/// @param a 第一个参数
/// @param b 第二个参数
/// @return 返回值说明
```

不支持块注释（`/* ... */`）。

### 2.2 关键字（17 个）与注解（2 个）

| 类别 | 关键字 |
|------|--------|
| 声明 | `func`、`var`、`const`、`struct` |
| 控制流 | `if`、`else`、`while`、`for`、`return` |
| 执行控制 | `wait`、`wait_for`、`yield` |
| 清理 | `defer`、`using` |
| 模块导入 | `include` |
| 布尔字面量 | `true`、`false` |

**注解**（`@` 前缀，不计入关键字）：

| 注解 | 说明 |
|------|------|
| `@export` | 导出模块变量或函数，供跨实例访问（Lang-6） |
| `@inline` | 标记导出函数为内联建议（Lang-8，当前为提示，未来可能自动内联） |

### 2.3 运算符与分隔符

**运算符**（按优先级从低到高）：

| 优先级 | 运算符 | 结合性 | 说明 |
|--------|--------|--------|------|
| 1 | `=` | 右结合 | 赋值 |
| 2 | `\|\|` | 左结合 | 逻辑或 |
| 3 | `&&` | 左结合 | 逻辑与 |
| 4 | `\|` | 左结合 | 按位或（Lang-14） |
| 5 | `^` | 左结合 | 按位异或（Lang-14） |
| 6 | `&` | 左结合 | 按位与（Lang-14） |
| 7 | `==`、`!=` | 左结合 | 等于、不等于 |
| 8 | `<`、`>`、`<=`、`>=` | 左结合 | 比较 |
| 9 | `<<`、`>>` | 左结合 | 左移、右移（Lang-14） |
| 10 | `+`、`-` | 左结合 | 加、减 |
| 11 | `*`、`/`、`%` | 左结合 | 乘、除、取模 |
| 12 | `-`（一元）、`!`、`~` | 右结合 | 取负、逻辑非、按位取反（Lang-14） |
| 13 | `.` | 左结合 | 字段访问 |

**分隔符**：`(`、`)`、`{`、`}`、`,`、`:`、`;`、`.`

### 2.4 字面量

```ffs
42          // 整数字面量（int）
3.14        // 浮点字面量（float）
"hello"     // 字符串字面量
true        // 布尔字面量
false       // 布尔字面量
```

**字符串转义序列**：

| 转义 | 含义 |
|------|------|
| `\n` | 换行 |
| `\t` | 制表符 |
| `\\` | 反斜杠 |
| `\"` | 双引号 |

### 2.5 标识符

以字母或下划线开头，后续可包含字母、数字、下划线：

```
[a-zA-Z_][a-zA-Z0-9_]*
```

---

## 三、类型系统

### 3.1 标量类型

| 类型 | 说明 |
|------|------|
| `int` | 32 位有符号整数 |
| `float` | 浮点数（运行时为 Fix64 定点数） |
| `bool` | 布尔（`true` / `false`） |
| `string` | 字符串（编译期常量池索引，运行时为整数） |

### 3.2 结构体类型

用户自定义复合类型，值语义（赋值即复制）：

```ffs
struct Vec2 {
    x: int
    y: int
}

struct Rect {
    min: Vec2
    max: Vec2
}
```

- 字段分隔符 `;` 可选
- 支持嵌套结构体（编译期递归拍平为连续寄存器）
- 不支持循环引用

### 3.3 不支持的类型

不支持数组、字典、闭包、类（class）、泛型。需要批量数据处理时通过 Syscall 委托宿主。

---

## 四、声明

### 4.0 模块级声明

FFScript 模块的顶层可包含以下声明：

- `func` — 函数声明
- `struct` — 结构体声明
- `var` — 模块变量（跨函数共享的可变状态）
- `const` — 模块常量
- `include` — 包含其他 `.ffs` 文件
- `@export` — 导出变量或函数供跨实例调用（Lang-6）

模块变量和常量在模块顶层声明，作用域为整个模块，所有函数均可访问：

```ffs
const MAX_HP: int = 100
var currentHP: int = MAX_HP

func damage(amount: int) {
    currentHP = currentHP - amount
    if currentHP < 0 {
        currentHP = 0
    }
}

func main() {
    damage(30)
    print(currentHP)    // 70
}
```

**结构体类型的模块变量与常量**（Lang-11）：

模块级 `var` / `const` 支持结构体类型，使用结构体字面量初始化：

```ffs
struct Vec2 {
    x: float
    y: float
}

struct Rect {
    min: Vec2
    max: Vec2
}

var pos: Vec2 = Vec2 { x: 10, y: 20 }
const origin: Vec2 = Vec2 { x: 0, y: 0 }
const bounds: Rect = Rect {
    min: Vec2 { x: 0, y: 0 },
    max: Vec2 { x: 100, y: 100 }
}

func main() {
    Report(pos.x)            // 10
    Report(origin.y)         // 0
    pos.x = 99               // var 可以修改字段
    // origin.x = 1          // ← 编译错误：const 不可赋值
}
```

- 支持嵌套结构体字面量初始化（编译器递归折叠字段值）
- `const` 结构体禁止整体赋值和字段赋值（编译错误）
- `var` 结构体字段可读写

**规则**：

- 模块变量使用专用寄存器段（绝对寻址），不受函数调用窗口影响
- 模块常量在编译期求值时不分配寄存器；不可折叠的常量也分配寄存器但禁止赋值
- 局部变量不可与模块变量同名（编译错误）
- 超出内置槽位数的模块变量自动溢出到扩展寄存器（堆分配）

### 4.0.1 Include 指令

`include` 用于将其他 `.ffs` 文件的内容合并到当前模块：

```ffs
include "common/math.ffs"
include "shared/types.ffs"

func main() {
    // 可以使用被包含文件中定义的函数、结构体和模块变量
    var v: Vec2 = Vec2 { x: 1, y: 2 }
    print(lengthSq(v))
}
```

**Include 规则**：

- 递归深度优先展开（支持多级 include）
- 支持菱形依赖（同一文件被多条路径 include 只处理一次）
- 循环引用检测（编译错误）
- 跨文件覆盖：后 include 的声明覆盖先 include 的同名声明
- 同文件重定义：编译错误
- `var` / `const` 交叉覆盖（跨文件用 `var` 覆盖 `const` 或反之）：编译错误

**编译器 API**：使用 include 时需提供 `IFileResolver` 和文件路径：

```csharp
var resolver = new DictionaryFileResolver(new Dictionary<string, string> {
    { "common/math.ffs", mathSource },
    { "shared/types.ffs", typesSource },
});
var result = compiler.Compile(source, "main", syscalls, syscallTable, resolver, "main.ffs");
```

### 4.0.2 @export 声明（跨实例访问）

`@export` 注解用于将模块变量或函数标记为"可被其他模块实例访问"。编译器会为包含 `@export` 声明的模块生成 **ExportTable**，供跨实例调用（XCALL）使用。

```ffs
// 服务模块（被调用方）
@export var hp: int = 100
@export var mp: int = 50
@export const MAX_HP: int = 999
@export const SPEED: float = 3.5

@export func take_damage(d: int): int {
    hp = hp - d
    return hp
}

@export func get_hp(): int {
    return hp
}

func main() {
    // @export 标记的声明仍然可以在本模块内正常使用
}
```

**规则**：

- `@export var` — 导出可读写变量
- `@export const` — 导出只读常量（Lang-12）。其他模块不可写入（编译期拒绝，运行时防御性检查）。宿主可通过 `ExportTable.GetVarDefault()` 读取编译期默认值，无需 spawn 实例
- `@export func` — 导出函数（其他模块可通过 XCALL 调用）
- 没有 `@export` 的声明仅模块内部可见
- 当前 `@export const` 仅支持基础类型（`int` / `float`），struct 类型后续扩展
- 编译器自动检测导出函数的 **退化模式**（Lang-7）：
  - 纯 getter（0 参数、仅返回模块变量）→ 优化为直接读取（XLOAD_MVAR）
  - 纯 setter（1 参数、仅赋值模块变量）→ 优化为直接写入（XSTORE_MVAR）

### 4.0.3 @inline 注解

`@inline` 注解用于标记函数为"要求内联"。编译器对所有满足条件的函数自动内联（无论是否标注 `@inline`）。`@inline` 不改变内联行为，仅在标注函数无法被内联时触发编译警告。

```ffs
@inline @export func get_hp(): int {
    return hp
}

@export @inline func set_hp(val: int) {
    hp = val
}

// 非导出函数也可标注（不报错，编译器仍自动判断内联可行性）
@inline func helper(x: int): int {
    return x * 2
}
```

- `@inline` 和 `@export` 的顺序不限
- 函数是否实际被内联由编译器自动判定（基于 body 大小、嵌套深度、安全性等）
- 可通过 `CompileOptions.InlineThreshold` / `InlineDepthMax` 调整内联策略
- 可用于 LSP 诊断提示（标注函数无法内联时发出警告）

### 4.0.4 跨实例调用——统一语法（svc.member）

当一个模块需要访问另一个模块的导出成员时，使用 **服务绑定** + **统一语法** `svc.member`：

```ffs
// 调用方模块
var svc: int = 0    // svc 变量保存服务实例 ID（由宿主设置）

func main() {
    // 调用服务的导出函数
    var result: int = svc.add(10, 32)

    // 读取服务的导出变量
    var hp: int = svc.hp

    // 写入服务的导出变量
    svc.hp = 500
}
```

**工作原理**：

1. 宿主编译服务模块，获取其 ExportTable
2. 将服务绑定（`ServiceBinding`）传给调用方编译器
3. 编译器将 `svc.member()` 解析为 XCALL / XLOAD_MVAR / XSTORE_MVAR 指令
4. 运行时通过 `svc` 变量中的实例 ID 定位目标实例

**编译器 API**：

```csharp
// 1. 编译服务模块
var svcResult = compiler.Compile(svcSource, "main", syscalls);

// 2. 创建服务绑定
var bindings = new ServiceBinding[] {
    new ServiceBinding("svc", svcResult.Program.ExportTable)
};

// 3. 编译调用方模块（传入绑定）
var callerResult = compiler.Compile(callerSource, "main", syscalls,
    syscallTable, null, null, bindings);
```

**错误检查**：

- 访问未导出的成员 → 编译错误
- 写入 `@export const` 变量 → 编译错误
- 参数数量不匹配 → 编译错误
- 将导出函数当变量访问（或反之）→ 编译错误

```ffs
func 函数名(参数列表): 返回类型 {
    函数体
}
```

- 返回类型可省略（无返回值时）
- 参数列表可为空

```ffs
// 无参数、无返回值
func main() {
    print(42)
}

// 带参数和返回值
func add(a: int, b: int): int {
    return a + b
}

// 带可选参数（必须位于必填参数之后）
func greet(name: string, times: int = 1) {
    // times 默认为 1
}
```

**文档注释**：

```ffs
/// 将两个整数相加。
/// @param a 第一个加数
/// @param b 第二个加数
/// @return 两数之和
func add(a: int, b: int): int {
    return a + b
}
```

### 4.2 变量声明

```ffs
var 变量名: 类型 = 初始值

var x: int = 10
var name: string = "player"
var pos: Vec2 = Vec2 { x: 5, y: 10 }
```

- 类型标注必填
- 初始值可省略（默认为零值）
- 作用域为所在块（`{}`）

### 4.3 常量声明

```ffs
const 常量名: 类型 = 值

const MAX_HP: int = 100
const PI: float = 3.14159
```

- 初始值必填
- 编译期求值，不分配寄存器

### 4.4 结构体声明

```ffs
struct 结构体名 {
    字段名: 类型
    字段名: 类型
}
```

```ffs
struct DamageInfo {
    target: int
    coeff: float
    dmgType: int
}
```

---

## 五、语句

### 5.1 if / else

```ffs
if 条件表达式 {
    // then 分支
}

if 条件表达式 {
    // then 分支
} else {
    // else 分支
}

if x > 10 {
    Report(2)
} else if x > 5 {
    Report(1)
} else {
    Report(0)
}
```

- 条件表达式不需要括号
- 分支体必须用 `{}`

### 5.2 while

```ffs
while 条件表达式 {
    // 循环体
}

var i: int = 0
while i < 100 {
    i = i + 1
}
```

### 5.3 for

C 风格三段式循环：

```ffs
for 初始化; 条件; 步进 {
    // 循环体
}

for var i: int = 0; i < 10; i = i + 1 {
    print(i)
}
```

- 三段均为可选
- 典型模式（`for var i = INIT; i < LIMIT; i = i + 1`）编译器自动优化为 FORLOOP 超级指令

### 5.4 return

```ffs
return          // 无返回值
return a + b    // 返回表达式值
```

### 5.5 wait

挂起当前脚本实例指定帧数：

```ffs
wait 20         // 挂起 20 帧
wait n          // 挂起 n 帧（n 为变量）
```

- 一等关键字，编译为显式状态（IP + WaitCounter）
- 不可在 `defer` / `using` 块内使用

### 5.6 yield

挂起 1 帧（等价于 `wait 1`）：

```ffs
yield
```

- 不可在 `defer` / `using` 块内使用

### 5.7 wait_for

等待另一个脚本实例完成：

```ffs
wait_for(targetInstanceId)
```

- 不可在 `defer` / `using` 块内使用

### 5.8 defer

注册清理块，在函数退出时以 LIFO（后进先出）顺序执行：

```ffs
func main() {
    defer {
        EndAction()
    }
    BeginAction(114, 56)
    // ... 函数退出时自动执行 EndAction()
}
```

- 多个 defer 按 LIFO 顺序执行
- 清理块内**可以调用函数**（包括自身含 defer 的函数，支持嵌套清理）
- 清理块内不可直接使用 `wait` / `yield` / `wait_for`（运行时自动跳过）
- 被调用函数的返回值在清理完成后正确恢复

```ffs
func cleanupResources() {
    defer { Report(1) }
    Report(2)
}
func main() {
    defer { cleanupResources() }  // 允许：清理函数可以有自己的 defer
    DoWork()
}
// 执行顺序: DoWork() → Report(2) → Report(1)
```

### 5.9 using

配对 Syscall 的资源管理语句。自动在块退出时调用已注册的配对释放 Syscall：

```ffs
using SetBB(1) {
    // 主体
    wait 10
}
// 块退出时自动调用配对的 ClearBB()
```

- 要求 Syscall 在 SyscallTable 中注册了配对释放函数
- 块内不可使用 `return`

### 5.10 表达式语句

任何表达式均可作为语句使用（通常为函数调用）：

```ffs
print(42)
ApplyDamage(target, 5, 101)
x = x + 1
```

---

## 六、表达式

### 6.1 算术

```ffs
a + b       // 加
a - b       // 减
a * b       // 乘
a / b       // 除（整数除法）
a % b       // 取模
-a          // 一元取负
```

### 6.2 比较

```ffs
a == b      // 等于
a != b      // 不等于
a < b       // 小于
a > b       // 大于
a <= b      // 小于等于
a >= b      // 大于等于
```

### 6.3 逻辑

```ffs
a && b      // 逻辑与（短路求值）
a || b      // 逻辑或（短路求值）
!a          // 逻辑非
```

### 6.4 赋值

```ffs
x = 10              // 标量赋值
pos.x = 20          // 字段赋值
x = y = z = 5       // 链式赋值（右结合）
```

### 6.5 函数调用

```ffs
add(1, 2)                   // 用户定义函数
print_str("result", 100)    // Syscall
```

- 参数按位置传递
- 返回值可用于表达式中：`var r: int = add(1, 2)`

### 6.6 结构体字面量

```ffs
Vec2 { x: 10, y: 20 }

Rect {
    min: Vec2 { x: 0, y: 0 },
    max: Vec2 { x: 100, y: 100 }
}
```

- 字段名必须与声明顺序一致
- 逗号分隔（末尾逗号可选）

### 6.7 字段访问

```ffs
var px: int = pos.x         // 读取字段
pos.y = 30                  // 写入字段
var mx: int = rect.min.x    // 嵌套字段访问
```

### 6.8 括号

```ffs
var r: int = (a + b) * (c - d)
```

---

## 七、Syscall（宿主调用）

Syscall 是宿主在 SyscallTable 中预注册的运行时函数，在脚本中以普通函数调用语法使用：

```ffs
print(42)
var t: int = CheckAttackHit(2001)
ApplyDamage(t, 5, 101)
```

- 编译期通过名称解析到 SyscallTable 中的索引
- 参数通过寄存器 r0..rN 传递，返回值在 r0
- 部分 Syscall 支持 `using` 配对清理

常见 Syscall 示例（实际列表由宿主决定）：

| Syscall | 说明 |
|---------|------|
| `print(value)` | 打印数值 |
| `print_str(label, value)` | 打印带标签数值 |
| `abs(x)` | 绝对值 |
| `min(a, b)` / `max(a, b)` | 最小 / 最大值 |
| `clamp(val, min, max)` | 钳位 |
| `sqrt(x)` | 平方根 |
| `random(max)` | 随机数 |
| `time()` / `frame_count()` | 时间 / 帧计数 |

---

## 八、EBNF 文法

```ebnf
(* ===== 顶层 ===== *)
Module          = { IncludeDecl | ExportDecl | FuncDecl | StructDecl | ModuleVarDecl | ModuleConstDecl } ;

(* ===== Include ===== *)
IncludeDecl     = 'include' StringLiteral ;

(* ===== Export / Inline 注解 ===== *)
ExportDecl      = '@export' ( ModuleVarDecl | ModuleConstDecl | [ '@inline' ] FuncDecl )
                | '@inline' '@export' FuncDecl ;

(* ===== 声明 ===== *)
FuncDecl        = [ '@inline' ] 'func' Identifier '(' [ ParamList ] ')' [ ':' TypeName ] Block ;
ParamList       = Param { ',' Param } ;
Param           = Identifier ':' TypeName [ '=' Expression ] ;

StructDecl      = 'struct' Identifier '{' { StructField } '}' ;
StructField     = Identifier ':' TypeName [ ';' ] ;

ModuleVarDecl   = 'var' Identifier ':' TypeName [ '=' Expression ] ;
ModuleConstDecl = 'const' Identifier ':' TypeName '=' Expression ;

TypeName        = Identifier ;

(* ===== 语句 ===== *)
Block           = '{' { Statement } '}' ;

Statement       = VarDecl
                | ConstDecl
                | IfStmt
                | WhileStmt
                | ForStmt
                | ReturnStmt
                | WaitStmt
                | WaitForStmt
                | YieldStmt
                | DeferStmt
                | UsingStmt
                | Block
                | ExprStmt ;

VarDecl         = 'var' Identifier ':' TypeName [ '=' Expression ] ;
ConstDecl       = 'const' Identifier ':' TypeName '=' Expression ;

IfStmt          = 'if' Expression Block [ 'else' ( IfStmt | Block ) ] ;
WhileStmt       = 'while' Expression Block ;
ForStmt         = 'for' [ VarDecl | ExprStmt ] ';' [ Expression ] ';' [ Expression ] Block ;
ReturnStmt      = 'return' [ Expression ] ;
WaitStmt        = 'wait' Expression ;
WaitForStmt     = 'wait_for' '(' Expression ')' ;
YieldStmt       = 'yield' ;
DeferStmt       = 'defer' Block ;
UsingStmt       = 'using' Identifier '(' [ ExprList ] ')' Block ;

ExprStmt        = Expression ;

(* ===== 表达式（优先级从低到高） ===== *)
Expression      = Assignment ;

Assignment      = LogicalOr [ '=' Assignment ] ;
LogicalOr       = LogicalAnd { '||' LogicalAnd } ;
LogicalAnd      = Equality { '&&' Equality } ;
Equality        = Comparison { ( '==' | '!=' ) Comparison } ;
Comparison      = Addition { ( '<' | '>' | '<=' | '>=' ) Addition } ;
Addition        = Multiplication { ( '+' | '-' ) Multiplication } ;
Multiplication  = Unary { ( '*' | '/' | '%' ) Unary } ;
Unary           = ( '-' | '!' ) Unary | Postfix ;
Postfix         = Primary { '.' Identifier [ '(' [ ExprList ] ')' ] } ;

Primary         = IntLiteral
                | FloatLiteral
                | StringLiteral
                | 'true' | 'false'
                | Identifier [ '(' [ ExprList ] ')' ]     (* 函数调用 *)
                | Identifier '{' [ FieldInit { ',' FieldInit } ] '}'  (* 结构体字面量 *)
                | '(' Expression ')' ;

FieldInit       = Identifier ':' Expression ;
ExprList        = Expression { ',' Expression } ;

(* ===== 词法 ===== *)
IntLiteral      = Digit { Digit } ;
FloatLiteral    = Digit { Digit } '.' Digit { Digit } ;
StringLiteral   = '"' { Character | EscapeSeq } '"' ;
EscapeSeq       = '\n' | '\t' | '\\' | '\"' ;
Identifier      = ( Letter | '_' ) { Letter | Digit | '_' } ;
Letter          = 'a'..'z' | 'A'..'Z' ;
Digit           = '0'..'9' ;
LineComment     = '//' { any except newline } ;
DocComment      = '///' { any except newline } ;
```

---

## 九、完整示例

### 9.1 基础 — 函数调用与循环

```ffs
func add(a: int, b: int): int {
    return a + b
}

func main() {
    print(42)
    var result: int = add(17, 25)
    print_str("result", result)

    var sum: int = 0
    for var i: int = 1; i <= 100; i = i + 1 {
        sum = sum + i
    }
    print_str("sum", sum)
}
```

### 9.2 结构体

```ffs
struct Vec2 {
    x: int
    y: int
}

func main() {
    var pos: Vec2 = Vec2 { x: 10, y: 20 }
    pos.x = pos.x + 5
    print_str("x", pos.x)
    print_str("y", pos.y)
}
```

### 9.3 帧控制与清理 — 技能脚本

```ffs
func main() {
    BeginAction(25, 30)
    defer {
        EndAction()
    }

    SpawnEffectSelf(4001, 300)

    var f: int = 0
    while f < 30 {
        f = f + 1
        yield
    }
}
```

### 9.4 复杂条件分支 — 攻击技能

```ffs
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()
    }

    var mutex1: int = 0
    var f: int = 0
    while f < 56 {
        if f >= 9 && f < 13 && mutex1 == 0 {
            var target: int = CheckAttackHit(2001)
            if target > 0 {
                ApplyDamage(target, 5, 101)
                ApplyHitstun(target, 5, 12, 0, 1)
                mutex1 = 1
            }
        }
        f = f + 1
        yield
    }
}
```

更多完整示例见 [Skills/](Skills/) 目录。

### 9.5 模块级结构体变量 — 配置化技能参数

```ffs
struct HitPhaseDef {
    startFrame: int
    endFrame: int
    hitboxId: int
}

struct Vec2 {
    x: float
    y: float
}

const hitPhase: HitPhaseDef = HitPhaseDef { startFrame: 9, endFrame: 13, hitboxId: 2001 }
var velocity: Vec2 = Vec2 { x: 6.5, y: 0 }

func main() {
    BeginAction(114, 56)
    defer { EndAction() }

    var mutex: int = 0
    var f: int = 0
    while f < 56 {
        if f == hitPhase.startFrame {
            ApplySelfHorizKB(velocity.x, 29)
        }
        if f >= hitPhase.startFrame && f < hitPhase.endFrame && mutex == 0 {
            var target: int = CheckAttackHit(hitPhase.hitboxId)
            if target > 0 {
                ApplyDamage(target, 5, 101)
                mutex = 1
            }
        }
        f = f + 1
        yield
    }
}
```

### 9.6 跨实例调用 — 服务模块与调用方

**服务模块**（提供导出接口）：

```ffs
@export var hp: int = 100
@export var mp: int = 50
@export const MAX_HP: int = 999

@export func take_damage(d: int): int {
    hp = hp - d
    if hp < 0 {
        hp = 0
    }
    return hp
}

@inline @export func get_hp(): int {
    return hp
}

func main() {
}
```

**调用方模块**（通过服务绑定访问）：

```ffs
var svc: int = 0    // 服务实例 ID，由宿主在运行时设置

func main() {
    // 读取服务变量
    var currentHP: int = svc.hp

    // 调用服务函数
    var afterHP: int = svc.take_damage(30)

    // 写入服务变量
    svc.mp = 25

    // getter 函数（编译器自动退化为直接变量读取）
    var hp2: int = svc.get_hp()

    Report(afterHP)
}
```

---

## 十、限制与约束

| 约束 | 说明 |
|------|------|
| 无数组 / 字典 | 批量数据通过 Syscall 委托宿主处理 |
| 无闭包 / 匿名函数 | 所有函数均为顶层具名函数 |
| 无类（class）/ 继承 | 仅支持结构体（值类型） |
| 无泛型 | 所有声明为单态 |
| 无动态内存分配 | 零 GC，运行期无托管堆分配 |
| 无块注释 | 仅支持 `//` 行注释和 `///` 文档注释 |
| `wait` / `yield` / `wait_for` 不可在 `defer` / `using` 块内使用 | 清理块必须同步执行（运行时跳过） |
| `return` 不可在 `defer` / `using` 块内使用 | 防止破坏清理链 |
| `defer` / `using` 清理块内可调用函数 | 支持嵌套清理（Level 3，类似 Go） |
| 字符串无运行时操作 | 字符串字面量编译为常量池索引 |
| `@export const` 仅支持基础类型 | 当前仅支持 `int` / `float`，struct 类型后续扩展 |
