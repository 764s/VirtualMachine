# Lang-18: Override Alias 声明

> **来源**：可组合框架模式 — 引入模板模块 + 定制部分行为（`override func Alias.Name()` 替换别名模块声明）。
>
> **前置**：Lang-17 ✅ Include As 别名完成。1757 测试总计。
>
> **状态**：✅ 完成。OA01-OA10 全通过。1777 测试总计。
>
> **设计讨论**：[D_IncludeAs.md](../Discussion/D_IncludeAs.md)（§3.3 Override 语法）
>
> **核心决策**：
> - `override func Alias.Name()` 语法替换别名模块中的同名 public 函数
> - 同理支持 `override const Alias.Name`、`override struct Alias.Name`、`override enum Alias.Name`
> - Parser 支持 `func Alias.Name()` 点号函数名（仅 override 上下文中有效）
> - Preprocessor 在 MergeMainDeclarations 中拦截 AliasTarget 声明，替换别名模块中的对应声明
> - BytecodeCompiler 无需改动 — Preprocessor 替换后，编译器正常处理别名模块
>
> **性能分析**：
> - **纯编译期特性**：无新 OpCode，无运行时改动
> - **Preprocessor 改动**：4 个新方法（ApplyAliasedFuncOverride / VarOverride / StructOverride / EnumOverride）
> - **编译器改动**：零改动 — 替换发生在预处理阶段

---

## Checklist

### 1. AST：AliasTarget 属性
- [x] `FuncDecl` 新增 `string AliasTarget` 属性（null = 普通声明，非 null = 别名覆写目标）
- [x] `VarDeclStmt` 新增 `string AliasTarget` 属性
- [x] `StructDecl` 新增 `string AliasTarget` 属性
- [x] `EnumDecl` 新增 `string AliasTarget` 属性

### 2. Parser：点号名称支持
- [x] `ParseFuncDecl`：读取名称后检查 `.`，如果 isOverride 则解析 `Alias.Name`
- [x] `ParseVarDecl`：同上，isOverride 时支持 `Alias.Name`
- [x] `ParseStructDecl`：同上
- [x] `ParseEnumDecl`：同上
- [x] 非 override 上下文使用点号名称 → 报错

### 3. Preprocessor：别名覆写应用
- [x] `MergeMainDeclarations` 中，AliasTarget != null 的声明跳过平坦合并
- [x] `ApplyAliasedFuncOverride`：在别名模块的 Functions 列表中替换同名 public 函数
- [x] `ApplyAliasedVarOverride`：在别名模块的 ModuleVariables 列表中替换同名 public const/var
- [x] `ApplyAliasedStructOverride`：在别名模块的 Structs 列表中替换同名 public struct
- [x] `ApplyAliasedEnumOverride`：在别名模块的 Enums 列表中替换同名 public enum
- [x] 错误处理：别名不存在 → 报错，目标声明不存在 → 报错

### 4. BytecodeCompiler
- [x] 无改动 — Preprocessor 替换后，编译器正常处理别名模块函数/常量/枚举/结构体

### 5. 测试（OA01-OA10）
- [x] OA01：`override func Alias.Do()` 替换函数 — 调用覆写版本
- [x] OA02：`override const Alias.MAX_HP` 替换常量 — 读取新值
- [x] OA03：`override enum Alias.Color` 替换枚举 — 使用新枚举值
- [x] OA04：`override struct Alias.Pos` 替换结构体 — 使用新字段
- [x] OA05：覆写不存在的函数 → 报错
- [x] OA06：覆写未知别名 → 报错
- [x] OA07：两个别名各自覆写 — 均生效
- [x] OA08：覆写带参数的函数 — 使用覆写体
- [x] OA09：覆写 private 函数 → 报错（找不到 public 函数）
- [x] OA10：覆写函数调用主模块辅助函数 — 跨作用域访问

### 6. 向后兼容验证
- [x] 所有现有 1757 测试继续通过（override 无 Alias → 平坦 mixin 行为不变）
