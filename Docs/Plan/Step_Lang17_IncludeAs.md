# Lang-17: Include As 别名（命名空间隔离的 include）

> **来源**：多模块同名声明共存（如两个格斗招式的 `Do()` 函数需在同一文件中共存）。
>
> **前置**：Lang-16 ✅ Override 关键字完成。1734 测试总计。
>
> **状态**：✅ 完成。IA01-IA12 全通过。1757 测试总计。
>
> **设计讨论**：[D_IncludeAs.md](../Discussion/D_IncludeAs.md)
>
> **核心决策**：
> - 方案 A（`include "path" as Alias`）采纳
> - `as` 为上下文关键字（不加入全局 Keywords）
> - 别名模块的 public 声明通过 `Alias.Name` 命名空间访问
> - 无 `as` 的 include 保持原有 mixin 行为（向后兼容）
>
> **性能分析**：
> - **纯编译期特性**：无新 OpCode，无运行时改动，Reg() 热路径零变化
> - **Preprocessor 改动**：新增别名模块存储路径（不参与平坦合并）
> - **编译器改动**：Alias.Name 解析 + 别名模块符号查找
> - **内联**：别名模块的函数可被内联（与跨模块内联同等条件）

---

## Checklist

### 1. Parser：`include "path" as Alias` 语法
- [x] 在 `ParseIncludeDecl` 中，解析完 `include "path"` 后检查下一 token 是否为标识符 `"as"`
- [x] 如果是 `as`，消耗 `as` token，然后期望下一 token 为标识符（别名）
- [x] 设置 `ImportDecl.Alias = aliasName`
- [x] 错误处理：`as` 后不是标识符 → 报错
- [x] 虚线类型名支持：`var x: Alias.StructName` 解析
- [x] 别名结构体字面量：`Alias.Struct { ... }` 通过 `IsStructLiteralLookahead` 前瞻检测

### 2. AST：ImportDecl 扩展
- [x] `ImportDecl` 新增 `string Alias` 字段（null = 传统 mixin，非 null = 命名空间模式）
- [x] `ModuleNode` 新增 `Dictionary<string, ModuleNode> AliasedModules` 字段

### 3. Preprocessor：别名模块分支
- [x] `ResolveRecursive` 中，当 `ImportDecl.Alias != null` 时：
  - [x] 递归解析被包含文件（复用现有 ResolveRecursive）
  - [x] 不调用 `MergeDeclarations`（不合并到平坦命名空间）
  - [x] 将解析后的 ModuleNode 存入 `merged.AliasedModules[alias]`
- [x] 别名重复检测：同文件两个 `include as` 使用相同别名 → 报错

### 4. BytecodeCompiler：Alias.Name 解析
- [x] `ProcessAliasedModules`：注册别名结构体到 `_structTypes`
- [x] `ProcessAliasedModuleSymbols`：注册别名枚举到 `_enumNames`/`_enumMemberMap`，别名常量到 `_aliasedConstValues`
- [x] `CompileAliasedAccess`：Alias.constName 常量查找 + Alias.EnumName 枚举检查
- [x] `CompileMemberCallExpr`：Alias.func(args) 别名函数调用分发为普通 CALL
- [x] FieldAccessExpr 枚举检查扩展：Alias.Enum.Member 三级访问
- [x] TryFoldConstant 扩展：Alias.constName + Alias.Enum.Member 常量折叠
- [x] 别名函数注册到 `_functionTable` + `_funcDecls`，Pass 2 中编译别名函数体
- [x] `_aliasedConstValues` 独立字典存储，避免 ProcessModuleVariables 重置

### 5. 测试（IA01-IA12）
- [x] IA01：基础 `include as` — 别名模块函数调用
- [x] IA02：别名模块常量访问
- [x] IA03：别名模块枚举访问（`Alias.Enum.Member` 三级）
- [x] IA04：两个 include as 同名函数共存
- [x] IA05：别名重复 → 报错
- [x] IA06：传统 include + include as 混用
- [x] IA07：别名模块 private 函数不可见 → 报错
- [x] IA08：别名模块函数带参数调用
- [x] IA09：向后兼容（无 as 的 include 不受影响）
- [x] IA10：别名结构体类型引用 + 实例化（`var p: T.Pos = T.Pos { ... }`）
- [x] IA11：别名常量在算术表达式中使用（TryFoldConstant）
- [x] IA12：别名枚举在 const 表达式中使用（TryFoldConstant）

### 6. 向后兼容验证
- [x] 所有现有 1734 测试继续通过（include 无 as → mixin 行为不变）
- [x] BB05-BB08 黑板测试回归已修复

### 7. 未来扩展（非本期范围）
- [ ] LSP 支持：`Alias.` 补全、hover、definition、references
- [ ] `override func Alias.Name()` 替换别名模块声明
- [ ] 别名模块非 const 变量读写（需寄存器分配设计）
