# Include As 别名：命名空间隔离的 include

> **状态**：✅ 已完成讨论 → [Step_Lang17_IncludeAs.md](../Plan/Step_Lang17_IncludeAs.md)
> **来源**：Lang-17 — include 文件级别名 + 命名空间隔离
> **日期**：2026-04-11

---

## 一、背景

Lang-2 引入 `include "path"` 后，被包含文件的所有声明（func/var/const/struct/enum）直接合并进当前文件的平坦命名空间。Lang-15 引入 `private`/`public` 减少了无意冲突，Lang-16 引入 `override` 使跨文件替换显式化。

但当一个文件需要 include 两个有同名声明的模块时（如两个格斗招式的 `Do()` 函数），即使有 `private` 和 `override`，仍然无法在同一文件中同时使用两个同名但不同来源的声明。

**核心痛点**：需要一种机制让 include 的声明保持在独立命名空间，通过别名前缀访问。

---

## 二、方案对比

### 方案 A：`include as`（✅ 采纳）

```ffs
include "f1/m" as MF1
include "f2/m" as MF2

override func MF1.Do() { ... }
override func MF2.Do() { ... }

var p: MF1.Pos = MF1.Pos { x: 1, y: 2 }
```

**特点**：
- `include` 是已有的预处理器指令，`as` 是上下文关键字（仅在 include 语句中有效）
- 被包含文件的所有 **public** 声明通过 `Alias.Name` 访问
- private 声明对外不可见（与 Lang-15 一致）
- 向后兼容：`include "path"` 仍为平坦 mixin 模式
- `override func Alias.Name()` 语法替换被包含文件的函数

### 方案 B：纯 `using`（未采纳）

```ffs
using MF1 = include f1/m
using MF2 = include f2/m
using Pos = MF1.Pos

override func MF1.Do() { ... }
```

**特点**：
- 引入 `using` 关键字，所有别名操作统一为 `using X = Y`
- `include` 从语句变成表达式（语义角色模糊）
- 需要一次性引入两个概念（using + include 作为表达式）
- 统一性好但与现有 include 语法断裂

### 决策

**选择方案 A**。理由：

1. **最小变更**：`include` 保持预处理器指令身份，仅增加 `as` 后缀分支
2. **语义清晰**：`include "path" as X` 读作"包含此文件，称之为 X"，直觉自然
3. **与现有 include 一致**：同一关键字两种模式（无别名 = mixin，有别名 = 命名空间）
4. **渐进式**：后续可独立引入 `using` 做类型简写，不需要一次性引入

---

## 三、语法设计

### 3.1 声明语法

```ffs
include "path" as Alias        // 命名空间 include
include "path"                  // 原有 mixin include（向后兼容）
```

- `as` 为上下文关键字（仅在 `include` 后识别，不保留为全局关键字）
- `Alias` 必须是合法标识符
- 同文件多个 `include as` 的别名不能重复

### 3.2 访问语法

```ffs
Alias.funcName(args)            // 函数调用
var x: Alias.StructName = ...   // 结构体类型引用
Alias.EnumName.Member           // 枚举成员访问
Alias.constName                 // 常量读取
Alias.varName                   // 变量读写
```

- `Alias.Name` 在所有表达式和类型位置可用
- 解析为 `FieldAccessExpr`（target = Alias 标识符，field = Name）

### 3.3 Override 语法

```ffs
override func Alias.Do() { ... }           // 替换被包含文件的函数
override const Alias.MAX_HP: int = 200      // 替换被包含文件的常量
override struct Alias.Config { ... }        // 替换被包含文件的结构体
override enum Alias.Mode { ... }            // 替换被包含文件的枚举
```

- `override` + `Alias.Name` 语法明确表示替换哪个命名空间的哪个声明
- 与 Lang-16 override 机制完全兼容

### 3.4 作用域

- **别名仅在声明文件内有效**（文件局部）
- 不传播到 include 本文件的其他文件
- 多文件可以对同一路径使用不同别名

---

## 四、语义规则

1. **include as 不执行 mixin 合并**：被包含文件的声明不进入当前文件的平坦命名空间
2. **仅 public 声明可通过别名访问**：被包含文件的 private 声明不可见（与 Lang-15 一致）
3. **override 只作用于被包含文件的声明**：`override func Alias.X()` 替换 Alias 命名空间中的 X
4. **类型解析**：`Alias.StructName` 解析为对应文件定义的结构体
5. **diamond include**：如果 A.ffs include as M1 且 B.ffs include as M2，M1 和 M2 include 同一 common.ffs，则 common.ffs 的声明分别存在于两个命名空间中（各自独立副本）

---

## 五、实现要点

### 5.1 Lexer
- `as` 作为上下文关键字：不加入全局 Keywords，仅在 Parser 的 include 解析中识别 `TokenType.Identifier` 值为 `"as"` 的 token

### 5.2 AST
- `ImportDecl` 新增 `Alias` 字段（string，null 表示传统 mixin include）
- 新增 `AliasedModule` 概念，保存别名到已解析 ModuleNode 的映射

### 5.3 Parser
- `include "path"` 后检查是否跟随 `as identifier`
- 如果有，设置 `ImportDecl.Alias`

### 5.4 Preprocessor
- 当 `ImportDecl.Alias != null` 时：
  - 不执行 MergeDeclarations（不合并到平坦命名空间）
  - 将解析后的 ModuleNode 存储到 `ModuleNode.AliasedModules[alias]`
- 传统 include 行为不变

### 5.5 BytecodeCompiler
- 编译 `Alias.Name` 表达式时：
  - 查找当前模块的 AliasedModules
  - 在对应别名模块中解析 Name（函数/变量/常量/结构体/枚举）
  - 根据声明类型生成对应字节码
- override + Alias.Name：在别名模块中替换对应声明

### 5.6 LSP
- 补全：输入 `Alias.` 时提供别名模块的 public 声明列表
- hover：显示别名模块中声明的信息
- definition：跳转到别名模块的源文件
- references：识别通过别名访问的引用
