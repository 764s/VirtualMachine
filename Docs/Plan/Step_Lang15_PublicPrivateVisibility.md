# Lang-15: Include 可见性（public / private 修饰符）

> **来源**：include mixin 名称冲突减少 + 模块封装。
>
> **前置**：Lang-14 ✅ 位运算完成。1675 测试总计。
>
> **设计讨论**：[D_PublicPrivateVisibility.md](../Discussion/D_PublicPrivateVisibility.md)
>
> **核心决策**：
> - public/private 与 @export **完全正交**（两轴隔离）
> - private = 名称隔离（不影响 mixin 运行语义）
> - Phase A（本期）：默认 public（向后兼容零 breaking）
> - 实现方案：origin-aware lookup（方案 B）
>
> **性能分析**：
> - **纯编译期特性**：无新 OpCode，无运行时改动，Reg() 热路径零变化
> - **Preprocessor 改动**：合并规则调整（private 符号不参与跨文件覆盖）
> - **编译器改动**：符号查找增加 origin 过滤（仅影响编译期函数/变量解析）
> - **内联自动支持**：private 函数仍可被同文件代码内联（CanInline 判定不受影响）

---

## Checklist

### 1. Lexer（~5 行）
- [ ] 新增 2 个 TokenType：`Public`, `Private`
- [ ] Keywords 字典添加 `"public"` → `TokenType.Public`, `"private"` → `TokenType.Private`

### 2. AST（~15 行）
- [ ] `VarDeclStmt`：新增 `IsPrivate` 属性 + 构造器参数
- [ ] `StructDecl`：新增 `IsPrivate` 属性 + 构造器参数
- [ ] `EnumDecl`：新增 `IsPrivate` 属性 + 构造器参数
- [ ] `FuncDecl`：`IsPrivate` 已存在（当前永远 false），无需修改
- [ ] 所有声明类型新增 `OriginFile` 属性（`string`，默认 null）— Preprocessor 合并时赋值

### 3. Parser（~60 行）
- [ ] 顶层解析循环：识别 `private` / `public` 前缀修饰符
  - `private func` / `private var` / `private const` / `private struct` / `private enum`
  - `public func` / `public var` / `public const` / `public struct` / `public enum`
- [ ] `private` + `@export` / `@export` + `private` 组合：合法（两轴正交）
- [ ] `public` + `@export` / `@export` + `public` 组合：合法
- [ ] `private` + `@inline` / `@inline` + `private` 组合：合法
- [ ] 完整组合矩阵：`[private|public] [@export] [@inline] func/var/const/struct/enum`
- [ ] 未标注 = public（Phase A 默认）
- [ ] 更新错误消息包含 `'private'`/`'public'` 作为合法前缀

### 4. Preprocessor（~80 行）
- [ ] `MergeDeclarations` / `MergeMainDeclarations`：为合并后的声明设置 `OriginFile`
- [ ] `MergeFunc` 调整：
  - private vs private（不同文件）→ 均保留（内部 qualified name）
  - private vs public（不同文件）→ 均保留（互不干扰）
  - public vs public（不同文件）→ 跨文件覆盖（现有规则不变）
  - private vs public（同文件）→ 同文件重定义报错
- [ ] `MergeModuleVariable` 同理调整
- [ ] `MergeStruct` 同理调整
- [ ] enum 合并同理调整
- [ ] 合并后的 `ModuleNode.Functions` 可能包含同名 private 函数（来自不同文件）— 需支持

### 5. BytecodeCompiler 符号查找（~40 行）
- [ ] 函数查找（CompileCallExpr 等）：跳过 OriginFile ≠ 当前编译上下文 的 private 函数
- [ ] 模块变量查找（ProcessModuleVariables 等）：跳过 private 跨 origin 变量
- [ ] struct 查找：跳过 private 跨 origin struct
- [ ] enum 查找（ProcessEnums）：跳过 private 跨 origin enum
- [ ] 确定"当前编译上下文的 origin"：通过正在编译的函数/变量声明的 OriginFile 推导
- [ ] 顶层代码（entry body）的 origin = 主文件

### 6. 内联适配
- [ ] 模块内内联（TryInlineCall）：private 函数仅对同 origin 调用者可内联（自然满足，因为跨 origin 的调用在符号查找时就已被过滤）
- [ ] 跨模块内联（TryInlineMemberCall）：不受影响（@export 函数独立于 public/private）
- [ ] BuildModuleInlineInfo：确认 private 函数是否应包含在 InlineInfo 中（结论：包含，因为同 origin 调用可内联）

### 7. LSP 适配（~30 行）
- [ ] documentSymbol：为 private 声明添加标记（如 SymbolKind 信息）
- [ ] completion：跨文件补全时排除 private 符号
- [ ] hover：private 声明显示可见性信息
- [ ] definition / references：private 声明的引用搜索限制在同 origin 文件内
- [ ] TextMate grammar：`private` / `public` 关键字染色（keyword.other 或 storage.modifier）

### 8. 测试（~20 个测试用例）
- [ ] **PV01**：`private func` 基础 — 同文件调用成功
- [ ] **PV02**：`private func` 跨文件不可见 — include 方调用报错
- [ ] **PV03**：`private var` 基础 — 同文件访问成功
- [ ] **PV04**：`private var` 跨文件不可见
- [ ] **PV05**：`private const` 基础
- [ ] **PV06**：`private struct` 跨文件不可见
- [ ] **PV07**：`private enum` 跨文件不可见
- [ ] **PV08**：同名 private func 不同文件不冲突
- [ ] **PV09**：同名 private var 不同文件不冲突
- [ ] **PV10**：public vs public 跨文件覆盖（现有行为不变）
- [ ] **PV11**：`public` 显式标注等价于无标注（Phase A 默认 public）
- [ ] **PV12**：`private` + `@export` 组合 — 编译成功，ExportTable 包含
- [ ] **PV13**：`public` + `@export` 组合
- [ ] **PV14**：`private` + `@inline` 组合
- [ ] **PV15**：private 函数内联验证（同 origin 调用者可内联）
- [ ] **PV16**：三层 include 链 — private 隔离正确
- [ ] **PV17**：向后兼容 — 现有无修饰符代码行为不变
- [ ] **PV18**：错误消息 — private 符号跨文件引用时报 "undefined" 而非泄漏
- [ ] **TW-PV01**：TreeWalker 基础 private 验证

### 9. 文档更新
- [ ] FFS_Syntax.md：添加 `private`/`public` 关键字文法
- [ ] FFS_QuickRef.md：添加可见性修饰符说明
- [ ] VM_Summary.md：更新完成状态 + 测试总计

---

## 功能展望

| ID | 内容 | 触发时机 |
|----|------|----------|
| VIS-1 | Phase B：默认切换为 private（breaking change + 迁移诊断） | 社区/团队评估后 |
| VIS-2 | LSP 诊断：跨文件引用无修饰符符号时建议添加 `public` | Phase B 准备阶段 |

## 风险点

| ID | 风险 | 缓解 |
|----|------|------|
| VR1 | 同名 private 函数并存可能影响编译器内部假设（函数名唯一） | 使用 `(OriginFile, Name)` qualified key |
| VR2 | Preprocessor 合并后 Functions 列表可能有同名项，影响下游遍历 | 编译器查找统一走 origin-aware 路径 |
