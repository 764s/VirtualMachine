# Step Lang-1: 模块变量 (Module Variables)

| 项目 | 内容 |
|------|------|
| 目标 | 实现文件级模块变量（`var`/`const`），解决 P1 痛点（函数间无法共享变量） |
| 前置 | B-δ5 (1007 tests)，SK14 Round 11 |
| 范围 | AST、Parser、Compiler、VM Runtime、LSP |
| 总测试 | 1037 (↑30: +14 Compiler L01~L14, +15 LSP L1-LSP-01~06, +1 existing remap) |

## 1. 设计决策

### 寄存器布局 (更新后)
```
r0~r15   Scratch Zone  — 函数参数/syscall 参数/返回值（绝对寻址）
r16~r47  Local Zone    — 函数局部变量（窗口寻址 r + RegisterBase）
r48~r55  Temp Zone     — 编译器临时寄存器（窗口寻址，FO6 重映射）
r56~r63  Module Zone   — 模块变量（绝对寻址，跨函数共享）
```

### 关键设计选择
- **绝对寻址**：r56~r63 不受函数调用窗口偏移影响（与 r0~r15 scratch 同策略）
- **VM 运行时 1 行改动**：`Reg()` 方法 `return (r < 16 || r >= 56) ? r : r + regBase;`
- **编译器初始化**：模块变量初始化代码作为入口函数 preamble 发射
- **影子检测**：函数内禁止声明与模块变量同名的局部变量

## 2. 变更清单

### AST (`ASTNode.cs`)
- `ModuleNode.ModuleVariables`: 新增 `List<VarDeclStmt>` 字段

### Parser (`Parser.cs`)
- `ParseModule()`: 在函数/结构体之间允许顶层 `var`/`const` 声明
- 模块级 `VarDeclStmt` 添加到 `ModuleVariables` 列表

### VM Runtime (`VMWorld.cs` / `VMConstants.cs`)
- `VMConstants.ModuleVarRegBase = 56`: 新常量
- `Reg()`: 绝对寻址 r0~r15 和 r56~r63

### Compiler (`BytecodeCompiler.cs`)
| 改动点 | 说明 |
|--------|------|
| `ModuleVarRegBase = 56` | 新常量，模块变量起始寄存器 |
| `ProcessModuleVariables()` | 分配 r56~r63，支持标量/结构体/const |
| `EmitModuleVarInit()` | 入口函数 preamble 发射初始化代码 |
| `CompileModule()` | 调用 ProcessModuleVariables，pre-populate 函数作用域 |
| `CompileFunction()` | 注入 `_moduleVariables` 和 `_moduleConstValues` |
| `CompileVarDecl()` | 影子检测：禁止 local 与 module var 同名 |
| FO6 remap | `a >= TempRegBase && a < ModuleVarRegBase` 排除模块变量 |
| FO7 overflow | `availableSlots = ModuleVarRegBase - VarRegBase` (40 而非 48) |
| P2 peephole | 安全检查增加 `&& ins.A < ModuleVarRegBase` |

### LSP (`LspServer.cs`)
| 功能 | 说明 |
|------|------|
| Document Symbols | 模块变量 → kind 13 (Variable)，模块常量 → kind 14 (Constant) |
| Hover | 声明和使用处显示 `(module var/const) name: type` |
| Completion | 模块变量/常量出现在函数体补全列表 |
| Go-to-Definition | 使用处 → 模块变量声明行 |
| Find References | 包含模块变量声明位置 |

## 3. 测试矩阵

### Compiler Tests (L01~L14)
| 测试 | 场景 |
|------|------|
| L01 | 基础模块变量读写（单函数，两次递增） |
| L02 | 跨函数共享（checkEnter 写，step 验证） |
| L03 | 显式初始化器（多变量） |
| L04 | yield 跨帧持久化 |
| L05 | 溢出检测（>8 模块变量报错） |
| L06 | 模块级 const |
| L07 | 模块变量 + 函数调用（窗口正确性） |
| L08 | 快照/回滚 |
| L09 | 局部变量影子检测（编译错误） |
| L10 | 默认初始化（零值） |
| L11 | 模块 const 跨函数可见性 |
| L12 | 模块级结构体变量 |
| L13 | 重复模块变量声明检测 |
| L14 | 混合位置（函数前后均可声明） |

### LSP Tests (L1-LSP-01~06)
| 测试 | 场景 |
|------|------|
| L1-LSP-01 | Document symbols 包含模块变量和常量 |
| L1-LSP-02 | Hover 模块变量声明 |
| L1-LSP-03 | Hover 模块变量使用处 |
| L1-LSP-04 | Completion 包含模块变量/常量 |
| L1-LSP-05 | 诊断信息（影子错误） |
| L1-LSP-06 | Go-to-definition 使用处→声明处 |

## 4. 性能验证

B01~B06 benchmark 全部通过，无回归。`Reg()` 热路径改动为单条件表达式扩展，对 dispatch 循环影响极小。

## 5. 后续

- **Lang-2 (include)**：可直接基于 Lang-1 实施。include 文件中的模块变量将被合并到主模块的 `ModuleVariables` 列表。
- **KOF98 脚本**：可立即在 `skill_*.ffs` 脚本中使用模块变量实现有状态技能条件（checkEnter + step 共享状态）。
