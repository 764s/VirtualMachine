# FFScript 快速上手（C# 开发者版）

> **定位**：面向熟悉 C# 的开发者，仅关注"怎么写、怎么跑"。
>
> 完整语法参考见 [FFS_Syntax.md](FFS_Syntax.md)。

---

## 1. 30 秒看懂 FFScript

FFScript 的语法像简化版 Go，加上帧控制关键字。你需要知道的全部"与 C# 不同之处"：

```ffs
// 类型标注在名字后面（Go 风格，不是 C# 风格）
var x: int = 10              // C# 里是 int x = 10;

// 函数声明用 func（不是 C# 的返回类型在前）
func add(a: int, b: int): int {
    return a + b
}

// if / while / for 不需要括号，但大括号必须有
if x > 5 {
    print(x)
}

// for 循环是 C 风格三段式
for var i: int = 0; i < 10; i = i + 1 {
    print(i)
}

// 没有 i++，只能 i = i + 1

// wait / yield：挂起脚本，下一帧继续
wait 5       // 暂停 5 帧
yield         // 暂停 1 帧（等价于 wait 1）

// defer：函数退出时自动执行（类似 Go 的 defer）
defer {
    Cleanup()
}
```

---

## 2. 类型

| FFScript | C# 对应 | 备注 |
|----------|---------|------|
| `int` | `int` | |
| `float` | `float` | 运行时是定点数 |
| `bool` | `bool` | `true` / `false` |
| `string` | `string` | 仅支持字面量，无运行时拼接 |
| `struct` | `struct`（值类型） | 赋值即复制，不支持方法 |

**没有的东西**：数组、字典、类（class）、泛型、闭包、`null`。

---

## 3. 变量与常量

```ffs
// 局部变量（函数内部）
var hp: int = 100          // 可变
var name: string = "player"
var pos: Vec2 = Vec2 { x: 0, y: 0 }

const MAX_HP: int = 100    // 编译期常量
```

### 模块变量（函数外部，顶层声明）

模块变量在所有函数之间共享，类似全局变量：

```ffs
const MAX_HP: int = 100
var currentHP: int = MAX_HP

func damage(amount: int) {
    currentHP = currentHP - amount
}

func main() {
    damage(30)
    print(currentHP)    // 70
}
```

- 模块级 `var` / `const` 写在函数和结构体的外面
- 所有函数都可以读写模块变量
- 局部变量不能和模块变量同名

---

## 4. 函数

```ffs
func main() {
    var r: int = add(1, 2)
    print(r)
}

func add(a: int, b: int): int {
    return a + b
}

// 可选参数（必须放最后）
func hit(target: int, coeff: float = 1.0) {
    ApplyDamage(target, coeff)
}
```

所有函数都是顶层的，不能嵌套定义。

---

## 4.5 Include（文件包含）

```ffs
include "common/math.ffs"
include "shared/types.ffs"

func main() {
    // 可以使用被包含文件中定义的函数、结构体和模块变量
    print(add(1, 2))
}
```

- `include` 写在文件顶部，递归展开
- 支持菱形依赖（同文件多次 include 只处理一次）
- 循环引用会报编译错误
- 跨文件同名声明：后者覆盖前者；同文件重定义报错

---

## 5. 结构体

```ffs
struct Vec2 {
    x: int
    y: int
}

struct Rect {
    min: Vec2
    max: Vec2
}

func main() {
    var p: Vec2 = Vec2 { x: 10, y: 20 }
    p.x = p.x + 5
    print(p.x)

    // 嵌套结构体
    var r: Rect = Rect {
        min: Vec2 { x: 0, y: 0 },
        max: Vec2 { x: 100, y: 100 }
    }
    print(r.min.x)
}
```

---

## 6. 控制流

```ffs
// if / else if / else
if hp <= 0 {
    Die()
} else if hp < 30 {
    Warn()
} else {
    Idle()
}

// while
var i: int = 0
while i < 10 {
    i = i + 1
}

// for（C 风格）
for var i: int = 0; i < 10; i = i + 1 {
    print(i)
}
```

---

## 7. 帧控制（FFScript 特有）

这是 FFScript 与普通语言最大的区别——脚本可以"暂停"，下一帧从暂停处继续。

```ffs
func main() {
    BeginAction(1, 30)
    defer { EndAction() }

    // 逐帧循环：每帧执行一次循环体
    var f: int = 0
    while f < 30 {
        if f == 10 {
            Attack()
        }
        f = f + 1
        yield          // ← 暂停 1 帧，下次 Tick() 继续
    }
}
```

| 关键字 | 作用 | C# 类比 |
|--------|------|---------|
| `yield` | 暂停 1 帧 | `yield return null`（Unity 协程） |
| `wait N` | 暂停 N 帧 | `yield return new WaitForFrames(N)` |
| `wait_for(id)` | 等另一个脚本实例结束 | `await task` |

> ⚠️ `yield` / `wait` / `wait_for` **不能**在 `defer` 或 `using` 块内使用。

---

## 8. 清理保障

### defer（类似 Go）

函数退出时自动执行，多个 defer 按 **后进先出** 顺序运行：

```ffs
func main() {
    defer { print(1) }
    defer { print(2) }
    print(3)
    // 输出：3, 2, 1
}
```

典型用法——确保技能结束时清理状态：

```ffs
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()     // 无论正常结束还是被打断，都会执行
    }
    // ... 技能逻辑 ...
}
```

### using（配对 Syscall）

自动在块退出时调用配对的释放函数（需要宿主注册配对关系）：

```ffs
using SetBB(1) {
    // 块内 SetBB(1) 已生效
    wait 10
}
// 退出时自动调用配对的 ClearBB()
```

---

## 9. Syscall（宿主调用）

Syscall 是宿主（C#）注册的函数，在脚本中直接当函数调用：

```ffs
func main() {
    print(42)                              // Syscall：打印
    var t: int = CheckAttackHit(2001)      // Syscall：碰撞检测，有返回值
    if t > 0 {
        ApplyDamage(t, 5, 101)             // Syscall：造成伤害
    }
}
```

Syscall 列表由宿主决定，不是语言内置的。

---

## 10. C# 宿主集成

### 最小示例

```csharp
using FFVM;
using FFVM.Compiler;

// ① 注册 Syscall
var syscallTable = new SyscallTable();
syscallTable.Register(0, "print", (ref VMInstanceState s) => {
    Console.WriteLine(new SyscallArgs(ref s).GetNumber(0));
});

// ② 编译
var compiler = new BytecodeCompiler();
string source = "func main() { print(42) }";
var result = compiler.Compile(source, "main",
    new Dictionary<string, int> { { "print", 0 } },
    syscallTable);

// ③ 运行
var world = new VMWorld();
world.Modules.Load(0, result.Program);
SyscallTable worldSyscalls = world.Syscalls;
worldSyscalls.Register(0, "print", (ref VMInstanceState s) => {
    Console.WriteLine(new SyscallArgs(ref s).GetNumber(0));
});
int id = world.SpawnInstance(0, 0);
while (true) {
    world.Tick();
    ref var inst = ref world.Pool.Instances[id];
    if ((inst.StateFlags & VMStateFlags.Completed) != 0) break;
}
```

### Syscall 注册模式

```csharp
// 无返回值
syscallTable.Register(0, "print", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    Console.WriteLine(args.GetNumber(0));       // 读 r0
});

// 有返回值
syscallTable.Register(1, "random", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    int max = args.GetInt(0);                   // 读 r0
    args.SetReturnInt(Random.Shared.Next(max)); // 写 r0
});

// 读字符串参数（从编译期常量池查找）
syscallTable.Register(2, "log", (ref VMInstanceState s) => {
    var args = new SyscallArgs(ref s);
    string label = args.GetString(0, program.StringConstants);
    int value = args.GetInt(1);
    Console.WriteLine($"{label} = {value}");
});

// 配对 Syscall（供 using 使用）
syscallTable.RegisterPaired(
    10, "SetBB",   (ref VMInstanceState s) => { /* 获取资源 */ },
    11, "ClearBB", (ref VMInstanceState s) => { /* 释放资源 */ }
);
```

### 编译时传给编译器的名称→槽位映射

```csharp
var syscallMap = new Dictionary<string, int> {
    { "print",    0 },
    { "random",   1 },
    { "log",      2 },
    { "SetBB",   10 },
    { "ClearBB", 11 },
};
var result = compiler.Compile(source, "main", syscallMap, syscallTable);
```

---

## 11. 完整技能脚本示例

```ffs
func main() {
    BeginAction(114, 56)
    defer {
        EndAction()
    }

    SpawnEffectSelf(7001, 60)

    var mutex1: int = 0
    var f: int = 0
    while f < 56 {
        // 帧 9：启动位移
        if f == 9 {
            ApplySelfHorizKB(6.5, 29)
        }

        // 帧 [9,13)：第一段攻击判定
        if f >= 9 && f < 13 && mutex1 == 0 {
            var target: int = CheckAttackHit(2001)
            if target > 0 {
                ApplyDamage(target, 5, 101)
                ApplyHitstun(target, 5, 12, 0, 1)
                SpawnEffectHit(3001, 60)
                mutex1 = 1
            }
        }

        f = f + 1
        yield
    }
}
```

---

## 12. 速查对照表

| 你想做的事 | FFScript | C# |
|-----------|----------|-----|
| 声明局部变量 | `var x: int = 0` | `int x = 0;` |
| 声明局部常量 | `const N: int = 10` | `const int N = 10;` |
| 模块变量 | `var hp: int = 100`（顶层） | `static int hp = 100;` |
| 模块常量 | `const MAX: int = 100`（顶层） | `static const int MAX = 100;` |
| 文件包含 | `include "path.ffs"` | `using` / `#include` |
| 函数 | `func f(a: int): int {}` | `int f(int a) {}` |
| 结构体 | `struct S { x: int }` | `struct S { public int x; }` |
| 实例化结构体 | `S { x: 1 }` | `new S { x = 1 }` |
| for 循环 | `for var i: int = 0; i < n; i = i + 1 {}` | `for (int i = 0; i < n; i++) {}` |
| 暂停 1 帧 | `yield` | `yield return null;` |
| 暂停 N 帧 | `wait N` | — |
| 退出清理 | `defer { ... }` | `try/finally` |
| 配对资源 | `using F() { ... }` | `using var x = ...;` |
| 调用宿主函数 | `print(42)` | 由宿主 `SyscallTable.Register` 注册 |
