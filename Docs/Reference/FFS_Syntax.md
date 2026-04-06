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

### 2.2 关键字（16 个）

| 类别 | 关键字 |
|------|--------|
| 声明 | `func`、`var`、`const`、`struct` |
| 控制流 | `if`、`else`、`while`、`for`、`return` |
| 执行控制 | `wait`、`wait_for`、`yield` |
| 清理 | `defer`、`using` |
| 布尔字面量 | `true`、`false` |

### 2.3 运算符与分隔符

**运算符**（按优先级从低到高）：

| 优先级 | 运算符 | 结合性 | 说明 |
|--------|--------|--------|------|
| 1 | `=` | 右结合 | 赋值 |
| 2 | `\|\|` | 左结合 | 逻辑或 |
| 3 | `&&` | 左结合 | 逻辑与 |
| 4 | `==`、`!=` | 左结合 | 等于、不等于 |
| 5 | `<`、`>`、`<=`、`>=` | 左结合 | 比较 |
| 6 | `+`、`-` | 左结合 | 加、减 |
| 7 | `*`、`/`、`%` | 左结合 | 乘、除、取模 |
| 8 | `-`（一元）、`!` | 右结合 | 取负、逻辑非 |
| 9 | `.` | 左结合 | 字段访问 |

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

### 4.1 函数声明

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
- 清理块内不可使用 `wait` / `yield` / `wait_for`

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
Module          = { FuncDecl | StructDecl } ;

(* ===== 声明 ===== *)
FuncDecl        = 'func' Identifier '(' [ ParamList ] ')' [ ':' TypeName ] Block ;
ParamList       = Param { ',' Param } ;
Param           = Identifier ':' TypeName [ '=' Expression ] ;

StructDecl      = 'struct' Identifier '{' { StructField } '}' ;
StructField     = Identifier ':' TypeName [ ';' ] ;

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
Postfix         = Primary { '.' Identifier } ;

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
| `wait` / `yield` / `wait_for` 不可在 `defer` / `using` 块内使用 | 清理块必须同步执行 |
| `return` 不可在 `defer` / `using` 块内使用 | 防止破坏清理链 |
| 字符串无运行时操作 | 字符串字面量编译为常量池索引 |
