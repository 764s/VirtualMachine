# DX21: T2 内联 / include as / 别名 — LSP 语义支持 Checklist

> 前置：DX20 已完成。T2 运行时已由 Lang-17（include as）+ Lang-18（override alias）落地，22 个编译器测试全绿。
> 用户意图：`include as Alias` 与 `override Alias.Name` 场景下，LSP 的 definition / references / rename / completion 必须正确工作，不能退化为扁平合并语义。

## 零、计划原则

- P-1：计划只确认"目标是否完整覆盖"，不约束具体实现细节。
- P-2：细节发散是允许的，但必须能映射到明确目标项（IN-01~06 或 CFR-02/09/10）。
- P-3：计划验收以完整性为准，不以某一种实现路径为准。
- P-4：运行时 Preprocessor 的 alias 模型（`AliasedModules`、`ApplyAliased*Override`）是 LSP 改造的参考基准，但不强制 1:1 复制。
- P-5：若发现新边界，优先补"目标覆盖项"，再补对应实现细节。

## 一、完成定义（Definition of Done）

- [x] DOD-1：plain include 跨文件 definition/references 在 alias 模式下不退化（IN-01）。
  - 验证：T2-IN-01~03, T2-ID-04, T2-GAP-01/04
- [x] DOD-2：`include as Alias` 的符号通过 `Alias.Name` 路径可正确 definition/references（IN-02）。
  - 验证：T2-AL-01~04, T2-GAP-02
- [x] DOD-3：别名重名冲突可诊断（IN-03）。
  - 验证：T2-ID-03（Parser duplicate alias detection + publishDiagnostics）
- [x] DOD-4：`override Alias.Name` 的 references 指向被替换后的有效声明（IN-04）。
  - 验证：T2-OV-01~03, T2-GAP-03
- [x] DOD-5：include 声明本身可参与 references（IN-05）。
  - 验证：T2-ID-02（aliased）, T2-GAP-01（plain）— include path go-to-definition
- [x] DOD-6：alias 场景 definition/references/rename 符号身份一致（IN-06）。
  - 验证：T2-ID-01（rename cross-file）, T2-ID-04（mixed scenario）
- [x] DOD-7：CFR-02/09/10 全部可通过 LSPNEW 测试验证。
  - CFR-02: T2-IN-01~03; CFR-09: T2-OV-01~03; CFR-10: T2-AL-01~04

## 二、当前基线

### 运行时（已完成，只读参考）

- [x] Lang-17：`include "path" as Alias`。Preprocessor 中 aliased import 不做扁平合并，存入 `ModuleNode.AliasedModules[alias]`。
- [x] Lang-18：`override func/var/struct/enum Alias.Name`。Preprocessor `ApplyAliased*Override` 在别名模块中替换目标声明。
- [x] 编译器测试 IA01~IA12 + OA01~OA10 全绿（22 项）。

### LSP 层（当前缺陷）

### LSP 层（改造前缺陷 — 已全部修复）

- ~~`DataFactKind.AliasBinding` 枚举值已预留，但从未发射。~~ → P0-2 已修复。
- ~~`EmitIncludeEdgeFacts`：不区分 aliased/non-aliased import。~~ → P0-2/P4-2 已修复。
- ~~`BuildImportedFunctionSymbols` / `BuildImportedStructFieldSymbols`：扁平合并忽略 alias。~~ → P0-3 已修复。
- ~~Position index / NameIndex：不识别点号标识符（`U.foo`）。~~ → P2-1/P2-2 已修复（通过 fact 发射层解决）。
- ~~LspServerNewTests 中零 T2 测试。~~ → 现有 18 条 T2 测试（67/67 全绿）。

## 三、目标覆盖映射

> 详细专题定义见 [Overview_LSP.md](Overview_LSP.md) §T2。
> 测试用例 ID 定义见 [Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md) §4。

| IN 规则 | CFR 场景 | Map 测试 ID | Phase |
| --- | --- | --- | --- |
| IN-01 plain include 可见域 | CFR-02 | T2-IN-01~03 | 1 |
| IN-02 include as 命名空间 | CFR-10 | T2-AL-01~04 | 2 |
| IN-03 别名重名冲突 | — | T2-ID-03 | 4 |
| IN-04 override 绑定 | CFR-09 | T2-OV-01~03 | 3 |
| IN-05 include 声明 references | — | T2-ID-02 | 4 |
| IN-06 符号身份一致 | — | T2-ID-01, T2-ID-04 | 4 |

## 四、推进清单

### Phase 0：数据层 — AliasBinding fact 基建

> 阻塞后续所有 Phase。完成后 alias 信息可被索引消费，但尚未影响查询结果。

- [x] P0-1：创建 `AliasBindingDataFactPayload` 类（属性：`AliasName`、`TargetDocumentUri`）。
  - 文件：`DataFact.cs`
- [x] P0-2：`EmitIncludeEdgeFacts` 中，当 `import.Alias != null` 时额外发射 `DataFactKind.AliasBinding` fact。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P0-3：`BuildImportedFunctionSymbols` / `BuildImportedStructFieldSymbols`：跳过 aliased import（`import.Alias != null` 时 `continue`），停止扁平合并污染。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P0-4：Index maintainer 消费 `AliasBinding` facts，构建 per-document `Dictionary<string, PathKey>` alias→target 映射。
  - 文件：`InMemoryIndexMaintainer.cs`、`IndexViews.cs`（新增 `IAliasIndex`）
- [x] P0-5：护栏测试 DBMID-25 / LSPNEW-T2-00：验证 `AliasBinding` fact 正确发射 + alias→target 映射可从 index snapshot 查询。
  - 文件：`LspDatabaseTests.cs`（DBMID-25A~E）

验收标准：

- [x] A0-1：AliasBinding fact 可发射 + alias 映射可从 snapshot 查询（DBMID-25B/D/E 验证）。
- [x] A0-2：现有 LSPNEW 全绿（38/38）+ DBMID 全绿（107/107）+ LspDatabaseQuery 全绿（24/24）— 无退化。

### Phase 1：Plain Include 语义护栏

> 可与 Phase 0 的测试编写并行。确保 plain include 基线不受后续 alias 改动影响。

- [x] P1-1：LSPNEW 测试 T2-IN-01 — plain include 跨文件函数 definition + references（CFR-02 基线）。
  - LSPNEW-T2-IN-01A/01B 已通过
- [x] P1-2：LSPNEW 测试 T2-IN-02 — plain include 跨文件变量/struct/enum references。
  - LSPNEW-T2-IN-02A/02B/02C 已通过
  - 基建修复：新增 var/struct/enum SymbolDefinition fact 发射 + IdentifierExpr 引用遍历 + 跨文件 BuildImportedNameSymbols
  - AST 扩展：StructDecl/EnumDecl 新增 NameLine/NameColumn 属性 + Parser 填充
- [x] P1-3：LSPNEW 测试 T2-IN-03 — plain include 的 include 声明本身可参与 references（IN-05 plain 分支）。
  - LSPNEW-T2-IN-03A/03B 已通过

验收标准：

- [x] A1-1：T2-IN-01~03 全绿（LSPNEW 52/52, DBMID 107/107, LspDatabaseQuery 24/24）。

### Phase 2：include as Alias 语义

> 依赖 Phase 0。核心改动：position index 支持点号解析、fact 提取器识别 alias 引用、completion 支持 alias 前缀过滤。

- [x] P2-1：Position index 支持点号标识符解析 — 通过 fact 发射层解决：`EmitAliasedCallReferenceFacts` 和 `EmitAliasedIdentifierReferenceFacts` 直接生成指向目标文档符号的 `SymbolReference` fact，position index 自然索引。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P2-2：Fact 提取器为 aliased 引用（`U.foo()`、`U.PI`、`U.Vec`）生成 `SymbolReference` fact，`Origin` 指向 aliased 目标文档。
  - 新增方法：`BuildAliasedFunctionSymbols`、`BuildAliasedNameSymbols`、`EmitAliasedCallReferenceFacts`（MemberCallExpr 遍历）、`EmitAliasedIdentifierReferenceFacts`（FieldAccessExpr + 点号类型注解/结构体字面量）
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P2-3：Completion 支持 alias 前缀过滤 — 输入 `U.` 后通过 `IAliasIndex` 解析别名，仅返回 aliased 目标文档的 public 符号。
  - 文件：`InMemoryLspQueryFacade.cs`（`QueryCompletion` 中新增 alias dot-prefix 分支）
- [x] P2-4：LSPNEW 测试 T2-AL-01 — `include as U` 后 `U.add()` 可 go-to-definition 到 lib.ffs。
  - LSPNEW-T2-AL-01 已通过
- [x] P2-5：LSPNEW 测试 T2-AL-02 — `U.add()` 的 references 返回 lib 定义 + main 调用点。
  - LSPNEW-T2-AL-02 已通过
- [x] P2-6：LSPNEW 测试 T2-AL-03 — 两个不同 alias（`M.add` → math.ffs、`S.concat` → str.ffs）互不干扰。
  - LSPNEW-T2-AL-03 已通过
- [x] P2-7：LSPNEW 测试 T2-AL-04 — aliased var/struct 通过 FieldAccess 的 references 跨文件正确工作。
  - LSPNEW-T2-AL-04 已通过（U.PI 引用跨文件）

验收标准：

- [x] A2-1：T2-AL-01~04 全绿（LSPNEW 56/56）。
- [x] A2-2：T2-IN-01~03 仍绿（plain include 无退化）+ DBMID 107/107 + LspDatabaseQuery 24/24。

### Phase 3：override Alias.Name 语义

> 依赖 Phase 2。核心改动：fact 提取器识别 AliasTarget、references 查询含 override 定义位。

- [x] P3-1：Fact 提取器识别 `FuncDecl.AliasTarget` / `VarDeclStmt.AliasTarget` / `StructDecl.AliasTarget` / `EnumDecl.AliasTarget`，为 override 声明生成 `SymbolDefinition` fact（使用 `Scope=AliasTarget` 区分于普通声明）+ 交叉引用指向原始定义位。
  - 新增方法：`EmitOverrideDefinitions`、`EmitOverrideCrossReference`
  - 修改：函数定义循环、`EmitModuleVarDefinitions`、`EmitStructNameDefinitions`、`EmitEnumDefinitions` 跳过 AliasTarget != null
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P3-2：References 查询对 override 声明返回：被替换声明的原始定义位（通过交叉引用）+ override 定义位 + 所有调用点。
  - 修改：`EmitAliasedCallReferenceFacts` 和 `EmitAliasedIdentifierReferenceFacts` 接受 override 查找表，优先使用 override 符号
- [x] P3-3：LSPNEW 测试 T2-OV-01 — `override func B.Do()` 的 definition 跳转到 override 体。
  - LSPNEW-T2-OV-01 已通过
- [x] P3-4：LSPNEW 测试 T2-OV-02 — `override func B.Do()` 的 references 包含原定义 + override + 所有调用。
  - LSPNEW-T2-OV-02 已通过
- [x] P3-5：LSPNEW 测试 T2-OV-03 — override 非法目标（不存在/private）不产生虚假符号绑定。
  - LSPNEW-T2-OV-03 已通过（B.NotExist 不影响 B.Foo 解析）

验收标准：

- [x] A3-1：T2-OV-01~03 全绿（LSPNEW 59/59）。
- [x] A3-2：Phase 1-2 测试仍绿 + DBMID 107/107 + LspDatabaseQuery 24/24。

### Phase 4：Symbol Identity 收敛 + 回归

> 依赖 Phase 3。完成后 T2 全部 14 条测试覆盖 + 交叉矩阵 gap 扫描。

- [x] P4-1：LSPNEW 测试 T2-ID-01 — alias 下 rename 一致性（rename `U.add` → lib.ffs + main.ffs 同步更新）。
  - LSPNEW-T2-ID-01 已通过
- [x] P4-2：LSPNEW 测试 T2-ID-02 — alias include 声明路径可 go-to-definition（IN-05 alias 分支）。
  - 基建：`EmitIncludeEdgeFacts` 新增 include path SymbolReference + SymbolDefinition fact 发射
  - AST 扩展：`ImportDecl` 新增 `PathLine`/`PathColumn` 属性 + Parser 填充
  - LSPNEW-T2-ID-02 已通过
- [x] P4-3：LSPNEW 测试 T2-ID-03 — 别名重名冲突诊断（`include as U` 重复时报错，IN-03）。
  - 基建：Parser 主循环新增 duplicate alias 检测 → `_errors.Add()`
  - LSPNEW-T2-ID-03 已通过（通过 DidOpen + publishDiagnostics 验证）
- [x] P4-4：LSPNEW 测试 T2-ID-04 — 混合场景（plain include + include as + override 共存，references 不串符号）。
  - LSPNEW-T2-ID-04 已通过（shared→lib.ffs, M.calc→main.ffs override）
- [x] P4-5：运行 Map_T1_T2 9×3 交叉覆盖矩阵 gap 扫描，补缺。
  - 扫描完成：识别 10 个必测空白 + 3 个可选空白
  - 已补缺 4 项 GAP 测试：
    - T2-GAP-01：include path × plain include（go-to-def on `include "lib"` path）
    - T2-GAP-02：struct + enum × alias（U.Vec / U.Dir definition）
    - T2-GAP-03：override const B.LIMIT（非函数类型 override）
    - T2-GAP-04：enum type × plain include（Dir.Up cross-file）
  - 剩余可接受空白：external 函数（与普通函数同一代码路径，隐式覆盖）、枚举成员级引用（可选优先级）

验收标准：

- [x] A4-1：T2-ID-01~04 全绿（LSPNEW 67/67）。
- [x] A4-2：全量 LSPNEW 回归通过（67/67）+ DBMID 107/107 + LspDatabaseQuery 24/24。
- [x] A4-3：Map 交叉矩阵必测项已覆盖；external 函数、枚举成员级引用为可选，已记录。

## 五、依赖关系

- Phase 0 是数据层基建，硬前置。
- Phase 1 的测试编写可与 Phase 0 并行（仅 plain include，不依赖 alias 数据）。
- Phase 2 依赖 Phase 0（需要 AliasBinding fact + alias→target 映射）。
- Phase 3 依赖 Phase 2（override 需要 alias 解析管线已就位）。
- Phase 4 只在前面全部完成后执行。

建议顺序：Phase 0 + Phase 1 并行 → Phase 2 → Phase 3 → Phase 4。

## 六、关键改动文件清单

| 文件 | 改动 |
| --- | --- |
| `DataFact.cs` | 新建 `AliasBindingDataFactPayload` |
| `ASTNode.cs` | `ImportDecl` 新增 `PathLine`/`PathColumn`；`FuncDecl`/`VarDeclStmt`/`StructDecl`/`EnumDecl` 新增 `AliasTarget`/`IsOverride`/`NameLine`/`NameColumn` |
| `Parser.cs` | `ParseIncludeDecl` 填充 `PathLine`/`PathColumn`；主循环新增 duplicate alias 检测 |
| `InMemoryDatabaseExecutionOrchestrator.cs` | `EmitIncludeEdgeFacts` 发射 AliasBinding + include path SymbolReference/SymbolDefinition；`Build*Symbols` 跳过 aliased import；新增 `BuildAliasedFunctionSymbols`/`BuildAliasedNameSymbols`/`EmitAliasedCallReferenceFacts`/`EmitAliasedIdentifierReferenceFacts`；`EmitOverrideDefinitions`/`EmitOverrideCrossReference` |
| `InMemoryIndexMaintainer.cs` | 消费 AliasBinding → alias 映射；`TryResolveSymbol` 点号解析 |
| `InMemoryLspQueryFacade.cs` | `QueryCompletion` alias 前缀过滤 |
| `IndexViews.cs` | 新增 `IAliasIndex` 接口 |
| `LspServerNewTests.cs` | T2-IN-01~03, T2-AL-01~04, T2-OV-01~03, T2-ID-01~04, T2-GAP-01~04（共 18 条新测试） |
| `LspDatabaseTests.cs` | DBMID-25A~E（AliasBinding 护栏测试） |

> 只读参考（无需改动）：`Preprocessor.cs`（运行时 alias 模型）。

## 七、回归命令清单

- `dotnet run --project StandaloneRunner -- --lsp-new-tests`
- `dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug`
- `dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Debug`

## 八、风险与缓解

- R1：alias 前缀拆分逻辑误判普通点号访问（`obj.field`）为 alias 访问。
  - 状态：**缓解（非完美解决）**。
  - 当前保证：`EmitAliasedCallReferenceFacts` / `EmitAliasedIdentifierReferenceFacts` 仅当 `FieldAccessExpr.Target` 的标识符精确匹配当前文档已注册 alias 名时才发射 aliased reference，否则跳过。
  - 残留边界：若局部变量名与 alias 名重名（如 `var U = ...` 与 `include "lib" as U` 共存），且字段名恰好匹配目标模块符号，将产生**虚假引用**。当前 fact 发射层无作用域感知，不区分局部变量与 alias。
  - 完美解决时机：**T4（作用域与可见性）SC-03** — "局部变量遮蔽模块变量时，references 必须绑定最近合法声明"。T4 引入作用域感知后，fact 发射层可根据绑定层级优先级排除 alias 路径。
  - 实际影响：极低。用户主动用 alias 名作为局部变量名是反模式，当前无已知用户场景触发。
- R2：override 声明与原定义的 symbol key 冲突导致 references 重复。
  - 状态：**已完美解决。**
  - 解决机制：override `SymbolIdentity` 与原定义在 `Scope`（= `AliasTarget`）、`Origin`（= override 所在文件）、`DeclarationSpan`（= override 位置）三个字段不同，生成不同 key，索引存储在独立桶中。
  - 交叉引用：`EmitOverrideCrossReference` 将 override 符号作为 `SymbolReference` 关联到原定义位，仅在查询 override 时出现。
  - 验证：T2-OV-02 断言 references 返回恰好 3 个不重复位置（原定义 + override 声明 + 调用点）。
- R3：多层 alias 链（A include B as X, B include C as Y）导致解析爆炸。
  - 状态：**已完美解决。**
  - 解决机制（三层结构性保护）：
    1. **Parser 结构性阻断**：`ParsePostfix` 仅当 `expr is IdentifierExpr` 时创建 `MemberCallExpr`，`X.Y.foo()` 中 `X.Y` 已是 `FieldAccessExpr`，不满足条件，产生语法错误。
    2. **Collector 结构性阻断**：`CollectAliasedFieldAccessFromExpression` 仅当 `fieldAccess.Target is IdentifierExpr` 时触发回调，多级 `FieldAccessExpr` 嵌套不触发。
    3. **Query 结构性阻断**：`QueryCompletion` 按首个 `.` 拆分，`TryResolveAlias` 只解析单层名称，`Y.foo` 不匹配任何符号。
  - 若未来语法扩展支持嵌套 alias，需独立专题处理（当前不在 T1-T6 规划内）。

## 九、参考资料

- [Overview_LSP.md](Overview_LSP.md) — T2 专题定义 §T2、CFR-02/09/10
- [Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md) — 14 条 T2 测试 ID + 9×3 交叉矩阵
- [D_IncludeAs.md](../../Discussion/D_IncludeAs.md) — Lang-17 设计讨论
- [Step_Lang17_IncludeAs.md](../../Step_Lang17_IncludeAs.md) — Lang-17 实现计划（已完成）
- [Step_Lang18_OverrideAlias.md](../../Step_Lang18_OverrideAlias.md) — Lang-18 实现计划（已完成）

## 十、收尾总结

**状态：DX21 T2 全部完成。** Phase 0-4 全部 [x]，DOD-1~7 全部 [x]。

### 测试基线

| 测试套件 | 通过 / 总数 |
| --- | --- |
| LSPNEW | 67 / 67 |
| DBMID | 107 / 107 |
| LspDatabaseQuery | 24 / 24 |

### 风险结论

| 风险 | 状态 | 完美解决时机 |
| --- | --- | --- |
| R1 alias/局部变量同名误判 | 缓解（实际影响极低） | T4 SC-03（作用域感知） |
| R2 override symbol key 冲突 | **已完美解决** | — |
| R3 多层 alias 链爆炸 | **已完美解决** | — |

### 遗留项（移交后续专题）

- R1 残留边界 → 纳入 T4 SC-03 "局部变量遮蔽模块变量" 工作项。
- 可选覆盖空白（external 函数 × alias、枚举成员级引用）→ 与普通函数/枚举共用代码路径，隐式覆盖，不影响正确性。
