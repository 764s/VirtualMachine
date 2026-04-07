# Lang-2: include（预处理器递归展开 + 重定义规则）

> **来源**：VM_Summary.md §七 Lang 表、KOF98/Docs/Discussion/D_SkillScripting.md SK13/SK14
>
> **前置**：Lang-1 ✅（模块变量）、Lang-1.1a ✅（MaxRegisters 常量配置化）、Lang-1.1b ✅（扩展寄存器）
>
> **状态**：⏳ 实施中
>
> **目标**：编译期 `include "path"` 预处理器，递归展开被引入文件的 const/struct/func/var 声明，支持跨文件覆盖。

---

## 一、核心设计

### 1.1 语法

```ffs
include "configs/ground_attack"      // 引入共享属性
include "shared/input_helpers"       // 引入共享函数

const ACTIVATION_PRIORITY: int = 150 // 覆盖 include 中的同名 const
func checkEnter(): int { ... }
```

`include` 语句仅允许出现在模块顶层，所有声明（func/struct/var/const）之前。

### 1.2 路径解析

- **相对于项目根**（脚本根目录），由编译器配置（如 `--script-root KOF98/Scripts/`）。
- 路径不含 `.ffs` 后缀，编译器自动追加。
- `include "configs/base"` → 在脚本根目录下查找 `configs/base.ffs`。

### 1.3 重定义规则

| 规则 | 行为 | 示例 |
|------|------|------|
| 跨文件 const 覆盖 | ✅ 后者覆盖前者（类型必须一致） | `include "A"` 中 `const X: int = 10`，主文件 `const X: int = 20` → X = 20 |
| 同文件 const 重定义 | ❌ 编译错误 | 同一文件中出现两个 `const X` |
| 跨文件 func 覆盖 | ✅ 后者覆盖前者（签名必须一致） | 主文件重定义 include 中的函数 |
| 同文件 func 重定义 | ❌ 编译错误 | 同一文件中出现两个同名函数 |
| 跨文件 struct 覆盖 | ✅ 后者覆盖前者 | 主文件重定义 include 中的 struct |
| 同文件 struct 重定义 | ❌ 编译错误 | 同一文件中出现两个同名 struct |
| 跨文件 var 覆盖 | ✅ 后者覆盖前者 | 主文件 var 覆盖 include 中的 var |
| var 覆盖 const | ❌ 编译错误 | `include "A"` 中 `const X`，主文件 `var X` |
| const 覆盖 var | ❌ 编译错误 | `include "A"` 中 `var X`，主文件 `const X` |

### 1.4 递归展开与循环检测

- include 的文件可以继续 include 其他文件（多级）。
- 深度优先展开：A includes B, B includes C → 先展开 C，再展开 B，最后 A 的声明覆盖所有。
- 循环检测：如果展开路径中出现已访问的文件，报编译错误。

---

## 二、实现方案

### 2.1 文件解析抽象

```csharp
public interface IFileResolver
{
    string ReadFile(string path);  // 返回文件内容，找不到返回 null
}
```

- 测试使用 `DictionaryFileResolver`（内存中映射 path → source）
- 生产使用 `FileSystemResolver`（相对于脚本根目录读取磁盘文件）

### 2.2 Preprocessor 类

新增 `Assets/Scripts/VM/Compiler/Preprocessor.cs`：

```
Preprocessor.Resolve(mainSource, mainFilePath, IFileResolver, syscalls)
  → 返回合并后的 ModuleNode
```

处理流程：
1. 解析主文件 → ModuleNode（含 Imports 列表）
2. 对每个 Import，递归调用 Resolve
3. 合并所有声明（按展开顺序），执行重定义规则
4. 返回最终合并的 ModuleNode

### 2.3 改动范围

| 文件 | 改动 |
|------|------|
| Lexer.cs | 新增 `Include` TokenType + keyword |
| Parser.cs | 新增 `include "path"` 解析 → ImportDecl |
| Preprocessor.cs | **新增** — 递归展开 + 合并 + 重定义规则 |
| BytecodeCompiler.cs | 新增 `Compile` 重载接受 `IFileResolver` |
| ASTNode.cs | 无改动（ImportDecl/ModuleNode.Imports 已存在） |

### 2.4 测试矩阵

| 编号 | 测试 | 覆盖 |
|------|------|------|
| INC01 | 基础 include（const 声明） | 解析 + 展开 |
| INC02 | include func 声明 | func 合并 |
| INC03 | 多条 include | 按序展开 |
| INC04 | 多级 include（A→B→C） | 递归展开 |
| INC05 | 循环 include 检测 | 错误报告 |
| INC06 | 跨文件 const 覆盖 | 后者覆盖前者 |
| INC07 | 同文件 const 重定义 | 编译错误 |
| INC08 | var 不能覆盖 const | 编译错误 |
| INC09 | 跨文件 func 覆盖 | 后者覆盖前者 |
| INC10 | include struct 声明 | struct 合并 |
| INC11 | include module var 声明 | var 合并 |
| INC12 | 全集成（链式 include + 所有声明类型） | 端到端 |
| INC13 | 文件未找到错误 | 错误报告 |
| INC14 | const 不能覆盖 var | 编译错误 |
| INC15 | 跨文件 struct 覆盖 | 后者覆盖前者 |
| INC16 | include 不带 file resolver（向后兼容） | 无 include 时正常工作 |

---

## 三、不变量

- 无 include 语句时，行为与 Lang-1.1b 完全一致（向后兼容）。
- Reg() 热路径无任何改动。
- B01-B06 benchmark 无回归。
- 1055 现有测试全部通过。
