# FFS 语言能力清单

> 本文是 FFS 当前已实现语言能力的完整索引，承接自原 [VM_Summary.md](../VM_Summary.md) “语言特性” 章节。
> 语法规范见 [FFS_Syntax.md](FFS_Syntax.md)，速查见 [FFS_QuickRef.md](FFS_QuickRef.md)。

---

## 当前语言能力

| 分类 | 能力 |
|------|------|
| 流程控制 | `if`/`else`、`while`、`for`、`wait N`、`wait_for(id)` |
| 函数 | `func`、`entry`、多参数、可选参数默认值、返回值、递归、CALL/RET + CALL_LEAF/RET_LEAF |
| 结构体 | `struct` 编译期拍平、嵌套 struct、字面量构造 `TypeName { field: expr }` |
| 枚举 | `enum Name { A, B = expr, C }` 语法糖 → 编译期命名整数常量 |
| 变量 | `var`/`const`、模块级变量、扩展寄存器溢出 |
| 模块 | `include "path"`、`include "path" as Alias`、`private`/`public`/`override` 可见性 |
| 跨实例 | `@export` 导出、XCALL/XLOAD_MVAR/XSTORE_MVAR、`svc.member` 统一语法 |
| Cleanup | `using SomeCall(args) { body }`、`defer { body }`、超时保护 |
| 运算 | 算术、比较、逻辑、位运算（`& \| ^ ~ << >>`） |
| 字符串 | 常量字符串字面量（ROM，不支持拼接） |
| 优化 | 常量折叠、Peephole、LICM、CMP-immediate、SWITCH 跳转表、内联（模块内+跨模块+深度链式）、FORLOOP 超级指令、指令压缩 4B |
| 调试 | 源码映射、符号表、断点/单步/变量查看、DAP 协议、VS Code 扩展 |
| 语言服务 | LSP 诊断、补全、hover、definition、references、rename、signatureHelp、语义染色、依赖图、全项目诊断 |
| 分发 | 独立 .NET 类库（netstandard2.1 + net8.0）、CLI 工具、单文件发布 |

---

## Lang 系列实现条目

| 序号 | 步骤 | 状态 | 内容 | 复杂度 |
|------|------|------|------|--------|
| Lang-1 | 模块变量 (L1) | ✅ | Parser 顶层 `var`/`const` + 保留寄存器段 | ⭐⭐ |
| Lang-1.1a | MaxRegisters 配置化 | ✅ | VMConstants 派生常量 | ⭐ |
| Lang-1.1b | 扩展寄存器 | ✅ | LOAD_XREG/STORE_XREG 零开销溢出 | ⭐⭐ |
| Lang-2 | include (L2) | ✅ | 预处理器递归展开 + 重定义规则 | ⭐⭐ |
| Lang-3 | 黑板 Syscall | ✅ | Get/SetBlackboard 标准 Syscall | ⭐ |
| *Lang-4* | *跨模块共享变量* | *⏳* | *按需触发（黑板瓶颈时）* | *⭐⭐⭐* |
| Lang-6 | XCALL 基线 | ✅ | XCALL + XLOAD_MVAR + XSTORE_MVAR + @export | ⭐⭐⭐ |
| Lang-7 | 自动退化 + VMConfig | ✅ | getter/setter 退化 + XCallDepthPolicy | ⭐⭐ |
| Lang-8 | 统一语法 + @inline | ✅ | `svc.member` 点号语法 | ⭐⭐⭐ |
| Lang-9 | 深度内联 P1-P4 | ✅ | 模块内/跨模块/深度链式 | ⭐⭐⭐ |
| Lang-10 | 导出变量默认值 | ✅ | ExportVarEntry.DefaultValue | ⭐⭐ |
| Lang-11 | 模块级 struct 初始化 | ✅ | 模块级 struct var/const 直接初始化 | ⭐⭐ |
| Lang-12 | @export const | ✅ | 基础类型导出常量 | ⭐ |
| Lang-13 | 枚举 (enum) | ✅ | 语法糖 → 编译期命名整数常量 | ⭐⭐ |
| Lang-14 | 位运算 | ✅ | `& \| ^ ~ << >>` 全链路 | ⭐⭐ |
| Lang-15 | Include 可见性 | ✅ | public/private + origin-aware 编译 | ⭐⭐⭐ |
| Lang-16 | Override 关键字 | ✅ | 显式跨文件替换 | ⭐⭐ |
| Lang-17 | Include As 别名 | ✅ | `include "path" as Alias` | ⭐⭐⭐ |
| Lang-18 | Override Alias | ✅ | `override func Alias.Name()` | ⭐⭐ |

> Lang 系列除 Lang-4（跨模块共享变量，按需触发）外全部完成。
