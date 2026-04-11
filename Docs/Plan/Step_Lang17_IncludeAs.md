# Lang-17: Include As 别名（命名空间隔离的 include）

> **来源**：多模块同名声明共存（如两个格斗招式的 `Do()` 函数需在同一文件中共存）。
>
> **前置**：Lang-16 ✅ Override 关键字完成。1734 测试总计。
>
> **状态**：⏳ 进行中。
>
> **设计讨论**：[D_IncludeAs.md](../Discussion/D_IncludeAs.md)
>
> **核心决策**：
> - 方案 A（`include "path" as Alias`）采纳
> - `as` 为上下文关键字（不加入全局 Keywords）
> - 别名模块的 public 声明通过 `Alias.Name` 命名空间访问
> - 无 `as` 的 include 保持原有 mixin 行为（向后兼容）
> - `override func Alias.Name()` 替换别名模块的声明
>
> **性能分析**：
> - **纯编译期特性**：无新 OpCode，无运行时改动，Reg() 热路径零变化
> - **Preprocessor 改动**：新增别名模块存储路径（不参与平坦合并）
> - **编译器改动**：Alias.Name 解析 + 别名模块符号查找
> - **内联**：别名模块的函数可被内联（与跨模块内联同等条件）

---

## Checklist

### 1. Parser：`include "path" as Alias` 语法（~15 行）
- [ ] 在 `ParseImport` 中，解析完 `include "path"` 后检查下一 token 是否为标识符 `"as"`
- [ ] 如果是 `as`，消耗 `as` token，然后期望下一 token 为标识符（别名）
- [ ] 设置 `ImportDecl.Alias = aliasName`
- [ ] 错误处理：`as` 后不是标识符 → 报错

### 2. AST：ImportDecl 扩展（~3 行）
- [ ] `ImportDecl` 新增 `string Alias` 字段（null = 传统 mixin，非 null = 命名空间模式）
- [ ] `ModuleNode` 新增 `Dictionary<string, ModuleNode> AliasedModules` 字段

### 3. Preprocessor：别名模块分支（~30 行）
- [ ] `ResolveRecursive` 中，当 `ImportDecl.Alias != null` 时：
  - [ ] 递归解析被包含文件（复用现有 ResolveRecursive）
  - [ ] 不调用 `MergeDeclarations`（不合并到平坦命名空间）
  - [ ] 将解析后的 ModuleNode 存入 `merged.AliasedModules[alias]`
- [ ] 别名重复检测：同文件两个 `include as` 使用相同别名 → 报错
- [ ] 别名模块中仅 public 声明可被外部通过 Alias.Name 访问

### 4. BytecodeCompiler：Alias.Name 解析（~60 行）
- [ ] `CompileFieldAccessExpr` 中，识别 target 为别名标识符的情况
- [ ] 查找当前模块的 `AliasedModules[alias]`
- [ ] 根据 field 名在别名模块中解析：
  - [ ] 函数 → 编译为对应函数调用（与普通函数调用相同字节码）
  - [ ] 常量 → 编译为 LOAD_CONST（常量折叠）
  - [ ] 变量 → 编译为 LOAD_MVAR / STORE_MVAR
  - [ ] 结构体 → 在类型位置解析为结构体定义
  - [ ] 枚举 → 通过 `Alias.Enum.Member` 三级访问解析
- [ ] 错误处理：别名标识符不是已知别名 → 正常回退（可能是变量/struct 的 field access）

### 5. Override + Alias 支持（~20 行）
- [ ] Parser 中支持 `override func Alias.Name()` 语法
  - [ ] `override` 后遇到 `func Alias.Name` → 设置函数名为别名限定形式
- [ ] Preprocessor 中将 override 声明应用到别名模块的对应声明
- [ ] 错误处理：override 目标在别名模块中不存在 → 报错

### 6. 测试（IA01-IA12+）
- [ ] IA01：基础 `include as` — 别名模块函数调用
- [ ] IA02：别名模块常量访问
- [ ] IA03：别名模块变量读写
- [ ] IA04：别名模块结构体类型引用 + 实例化
- [ ] IA05：别名模块枚举访问（`Alias.Enum.Member`）
- [ ] IA06：两个 include as 同名函数共存
- [ ] IA07：override + Alias.Name 替换函数
- [ ] IA08：override + Alias.Name 替换常量
- [ ] IA09：别名模块 private 声明不可见 → 报错
- [ ] IA10：别名重复 → 报错
- [ ] IA11：传统 include + include as 混用
- [ ] IA12：嵌套 include as（别名模块自身也有 include）

### 7. LSP 支持（~40 行）
- [ ] 补全：`Alias.` 输入后提供别名模块的 public 声明列表
- [ ] hover：`Alias.Name` 显示别名模块中的声明信息
- [ ] definition：`Alias.Name` 跳转到别名模块源文件中的定义位置
- [ ] references：识别通过别名访问的引用

### 8. 向后兼容验证
- [ ] 所有现有测试通过（include 无 as → mixin 行为不变）
- [ ] B01-B06 benchmark 无回归
