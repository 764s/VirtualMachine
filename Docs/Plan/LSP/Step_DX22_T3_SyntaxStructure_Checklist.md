# DX22: T3 语法结构 — 可搜索单元 × 位置覆盖矩阵 Checklist

> 前置：DX21 已完成（T2 别名语义全绿）。T3 目标：将 V2 模型矩阵中每类可搜索单元的 definition / references 在各出现位置完整覆盖，LSP 查询可全局命中。
> 用户意图：所有结构化语义（模块变量、结构体类型、结构体字段、枚举类型、枚举成员、外部函数、参数、局部变量）的 definition / references / rename / completion 全部正确工作。

## 零、规划原则

- P-1：规划只确认"目标是否完整覆盖"，不约束具体实现细节。
- P-2：细节发散是允许的，但必须能映射到明确目标项（CFR-11~17 或 V2 矩阵单元格）。
- P-3：规划验收以完整性为准，不以某一种实现路径为准。
- P-4：已有 DX20/DX21 落地的 fact 发射逻辑是改造基准，但不强制 1:1 复制。
- P-5：若发现新边界，优先补"目标覆盖项"，再补对应实现细节。

## 一、完成定义（Definition of Done）

- [x] DOD-1：模块变量（var/const）跨文件 definition/references 全局命中（CFR-11）。✅ Phase 0 完成
- [x] DOD-2：结构体类型跨文件 definition/references 全局命中（CFR-12，含声明 / 类型注解 / 字面量）。✅ Phase 0+5 完成（L4/L5/L7 gap 修复）
- [x] DOD-3：同名字段在不同结构体中按 parent 区分，不误并（CFR-15）。✅ Phase 0 完成
- [x] DOD-4：枚举类型 + 枚举成员跨文件 definition/references 全局命中（CFR-16）。✅ Phase 1 完成
- [x] DOD-5：外部函数（external func）声明与调用引用全局命中（CFR-17）。✅ Phase 2 完成
- [x] DOD-6：参数 definition/references 在函数体内正确绑定。✅ Phase 3 完成
- [x] DOD-7：局部变量 definition/references 在函数体内正确绑定，不与模块变量串符号。✅ Phase 4 完成
- [x] DOD-8：V2 矩阵所有 M（必须覆盖）单元格均有对应测试。✅ Phase 5 完成（T3-STR-02 + T3-ENM-03 补齐 L4/L5/L7 gap）

## 二、当前基线

### 已实现（DX20/DX21 落地）

- [x] B-1：函数定义 + 引用（同文件 / 跨文件 / aliased / override）— 全绿。
- [x] B-2：模块变量定义（`EmitModuleVarDefinitions`）+ 标识符引用（`EmitIdentifierReferenceFacts`）— 已发射，无专项测试。
- [x] B-3：结构体类型定义（`EmitStructNameDefinitions`）+ 标识符引用（类型注解 / 字面量）— 已发射，无专项测试。
- [x] B-4：结构体字段定义（`BuildLocalStructFieldSymbols`）+ 字段访问引用（含嵌套链）— 已实现 + LSPNEW-14/15 测试。
- [x] B-5：枚举类型定义（`EmitEnumDefinitions`）+ 标识符引用 — 已发射，无专项测试。
- [x] B-6：Include 文件路径定义 + 引用（`EmitIncludeEdgeFacts`）— 已实现 + 测试。

### 缺口（本轮需补）

- [x] G-1：枚举成员（EnumMember）— `BuildLocalEnumMemberSymbols` + `BuildImportedEnumMemberSymbols` + `MergeEnumMemberSymbols` + `EmitEnumMemberReferenceFacts` 已实现。✅ Phase 1 完成
- [x] G-2：外部函数（external func）— 函数定义循环本就不跳过 external（无 Body==null 过滤），`BuildImportedFunctionSymbols` 也已包含 external。✅ Phase 2 完成
- [x] G-3：参数（Parameter）— `EmitParameterDefinitionFacts` 已实现，`EmitIdentifierReferenceFacts` 增加参数优先解析。✅ Phase 3 完成
- [x] G-4：局部变量（Local var）— `EmitLocalVarDefinitionFacts` + `CollectVarDeclStmtsFromBody` 已实现，`EmitIdentifierReferenceFacts` 按作用域链解析（局部 > 参数 > 模块）。✅ Phase 4 完成
- [x] G-5：CFR-11（模块变量）/ CFR-12（结构体类型）/ CFR-15（同名字段）— Phase 0 已补专项测试，全绿。
- [x] G-6：CFR-16（枚举/枚举成员）/ CFR-17（外部函数）— ✅ G-1/G-2 已完成，T3-ENM-01/02 + T3-EXT-01 全绿。

## 三、目标覆盖映射

> 详细模型基线见 [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) §V2。
> CFR 场景见 [Overview_LSP.md](Overview_LSP.md) §二。

| CFR 场景 | 可搜索单元 | 当前状态 | Phase |
| --- | --- | --- | --- |
| CFR-11 | 模块变量（var/const） | ✅ Phase 0 全绿 | 0 |
| CFR-12 | 结构体类型 | ✅ Phase 0 全绿 | 0 |
| CFR-15 | 同名字段（不同结构体） | ✅ Phase 0 全绿 | 0 |
| CFR-16 | 枚举类型 + 枚举成员 | ✅ Phase 1 全绿 | 1 |
| CFR-17 | 外部函数 | ✅ Phase 2 全绿 | 2 |
| — | 参数 | ✅ Phase 3 全绿 | 3 |
| — | 局部变量 | ✅ Phase 4 全绿 | 4 |

## 四、推进清单

### Phase 0：已有逻辑的测试补充（CFR-11/12/15） ✅ 完成

> 无需新增 fact 发射，仅补 LSPNEW 测试验证已有逻辑的正确性。

- [x] P0-1：LSPNEW 测试 T3-VAR-01 — 模块变量（var/const）跨文件 definition + references（CFR-11）。✅ T3-VAR-01A/01B 通过
  - 场景：lib.ffs 声明 `var hp = 100`，main.ffs include lib 并在函数体内读写 `hp`。
  - 断言：definition 指向 lib 定义位，references 返回 lib 定义 + main 3 个使用点。
- [x] P0-2：LSPNEW 测试 T3-STR-01 — 结构体类型跨文件 definition + references（CFR-12）。✅ T3-STR-01A/01B 通过
  - 场景：lib.ffs 声明 `struct Vec { x, y }`，main.ffs include lib 并在类型注解 / 字面量中使用 `Vec`。
  - 断言：definition 指向 lib 的 struct 定义位，references 返回所有类型引用点。
- [x] P0-3：LSPNEW 测试 T3-FLD-01 — 同名字段不同结构体不误并（CFR-15）。✅ T3-FLD-01A/01B 通过
  - 场景：同文件 `struct Player { hp }` 和 `struct Enemy { hp }`。
  - 断言：Player.hp references 不包含 Enemy.hp，反之亦然。

验收标准：

- [x] A0-1：T3-VAR-01、T3-STR-01、T3-FLD-01 全绿（6 assertions 全通过）。
- [x] A0-2：现有 LSPNEW 全绿（73/73, 0 failed）— 无退化。

### Phase 1：枚举成员 definition / references（CFR-16） ✅ 完成

> 依赖 Phase 0 基线稳定。核心改动：为 EnumMember 发射 def/ref facts。

- [x] P1-1：`BuildLocalEnumMemberSymbols` + `BuildImportedEnumMemberSymbols` + `MergeEnumMemberSymbols` — 遍历 `EnumDecl.Members`，为每个枚举成员发射 `SymbolKindTag.EnumMember` 定义 fact，支持跨文件导入合并。✅ 已实现
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P1-2：`EmitEnumMemberReferenceFacts` — 遍历表达式树，识别 `FieldAccessExpr`（`Dir.Up`）并发射 `SymbolReference` fact。✅ 已实现
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P1-3：LSPNEW 测试 T3-ENM-01 — 枚举成员跨文件 definition + references（CFR-16）。✅ T3-ENM-01A/01B/01C 通过
  - 场景：lib.ffs 声明 `enum Dir { Up, Down, Left, Right }`，main.ffs include lib 并引用 `Dir.Up`、`Dir.Down`。
  - 断言：`Dir.Up` definition 指向 lib 定义位，references 返回 lib 定义 + main 使用点；`Dir` 类型引用包含 lib + main。
- [x] P1-4：LSPNEW 测试 T3-ENM-02 — 同名枚举成员不同枚举不误并。✅ T3-ENM-02A/02B 通过
  - 场景：`enum Color { Red, Blue }` 和 `enum Priority { Red, Green }`，`Color.Red` 和 `Priority.Red` 引用不串。

验收标准：

- [x] A1-1：T3-ENM-01、T3-ENM-02 全绿（5 assertions 全通过）。
- [x] A1-2：Phase 0 测试仍绿 + 全量 LSPNEW 全绿（80/80, 0 failed）。

### Phase 2：外部函数 definition（CFR-17） ✅ 完成

> 依赖 Phase 0 基线稳定。核心发现：索引循环本就不跳过 external func（定义循环无 Body==null 过滤）。

- [x] P2-1：验证函数定义发射循环已包含 external func — 定义循环无 `Body==null` 跳过，`IsExternal` 函数定义 fact 已自然发射。✅ 无需改动
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P2-2：验证 `BuildImportedFunctionSymbols` → `TryReadImportedFunctionSymbols` 已包含 external func — 无 Body==null 过滤。✅ 确认已包含
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P2-3：LSPNEW 测试 T3-EXT-01 — external func 声明与调用引用（CFR-17）。✅ T3-EXT-01A/01B 通过
  - 场景：lib.ffs 声明 `external func print(msg: string)`，main.ffs include lib 并调用 `print("hello")`、`print("world")`。
  - 断言：definition 指向 lib 的 external 声明位，references 返回声明 + 2 个调用点。

验收标准：

- [x] A2-1：T3-EXT-01 全绿（2 assertions 全通过）。
- [x] A2-2：Phase 0-1 测试仍绿 + 全量 LSPNEW 全绿（80/80, 0 failed）。

### Phase 3：参数 definition / references ✅ 完成

> 依赖 Phase 0 基线稳定。核心改动：`EmitParameterDefinitionFacts` 发射参数定义 fact，`EmitIdentifierReferenceFacts` 增加参数优先解析。

- [x] P3-1：`EmitParameterDefinitionFacts` — 遍历 `FuncDecl.Parameters`，为每个参数发射 `SymbolKindTag.Parameter` 定义 fact（scope = funcName.paramName）。✅ 已实现
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P3-2：`EmitIdentifierReferenceFacts` 增加参数符号解析 — 函数体内标识符按作用域链解析（局部 > 参数 > 模块 > 导入）。✅ 已实现
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P3-3：LSPNEW 测试 T3-PRM-01 — 参数 definition + references（同文件）。✅ T3-PRM-01A/01B 通过
  - 场景：`func add(a: int, b: int) { var sum: int = a + b; wait 1 }`。
  - 断言：`a` 的 definition 指向参数声明 (line 0, col 9)，references 返回声明 + 函数体内使用点。
- [x] P3-4：LSPNEW 测试 T3-PRM-02 — 参数与模块变量同名时绑定最近声明。✅ T3-PRM-02A/02B 通过
  - 场景：模块 `var x: int = 1`，`func f(x: int) { return x }`，`func g() { return x }`。
  - 断言：函数 f 内 `x` 的 references 仅指向参数，函数 g 内 `x` 指向模块变量。

验收标准：

- [x] A3-1：T3-PRM-01、T3-PRM-02 全绿（4 assertions 全通过）。
- [x] A3-2：Phase 0-2 测试仍绿 + 全量 LSPNEW 全绿（88/88, 0 failed）。

### Phase 4：局部变量 definition / references ✅ 完成

> 与 Phase 3 同步实现。核心改动：`EmitLocalVarDefinitionFacts` + `CollectVarDeclStmtsFromBody` 递归收集函数体内 VarDeclStmt 并发射定义 fact，`EmitIdentifierReferenceFacts` 作用域链已含局部变量。

- [x] P4-1：`EmitLocalVarDefinitionFacts` + `CollectVarDeclStmtsFromBody` — 递归遍历函数体（Block/If/While/For/Defer/Using），收集所有 `VarDeclStmt`，发射 `SymbolKindTag.Variable` 定义 fact（scope = funcName.varName）。✅ 已实现
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P4-2：`EmitIdentifierReferenceFacts` 作用域链已实现 — 局部 > 参数 > 模块 > 导入。✅ 与 Phase 3 同步完成
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [x] P4-3：LSPNEW 测试 T3-LOC-01 — 局部变量 definition + references（同文件）。✅ T3-LOC-01A/01B 通过
  - 场景：`func f() { var count: int = 0; count = count + 1; wait 1 }`。
  - 断言：`count` 的 definition 指向 `var count` 声明 (line 1, col 8)，references 返回声明 + 赋值目标 + 表达式使用。
- [x] P4-4：LSPNEW 测试 T3-LOC-02 — 局部变量遮蔽模块变量。✅ T3-LOC-02A/02B 通过
  - 场景：模块 `var n: int = 1`，`func f() { var n: int = 2; return n }`，`func g() { return n }`。
  - 断言：函数 f 内 `n` 的 references 仅指向局部声明，函数 g 内 `n` 指向模块变量。

验收标准：

- [x] A4-1：T3-LOC-01、T3-LOC-02 全绿（4 assertions 全通过）。
- [x] A4-2：Phase 0-3 测试仍绿 + 全量 LSPNEW 全绿（88/88, 0 failed）。

### Phase 5：V2 矩阵收敛 + 回归 ✅ 完成

> 依赖 Phase 0-4。完成后所有 M（必须覆盖）单元格均有测试验证。

- [x] P5-1：V2 矩阵 gap 扫描 — 逐行逐列校验每个 M 单元格是否已被某个 LSPNEW 测试覆盖。✅ 扫描发现 5 个 M gap（StructType@L4/L5/L7, EnumType@L4/L5）
- [x] P5-2：补缺测试（若 gap 扫描发现 Phase 0-4 未覆盖的 M 单元格）。✅ 3 个代码修复 + T3-STR-02(A/B/C/D) + T3-ENM-03(A/B/C) 共 7 assertions
- [x] P5-3：全量 LSPNEW 回归通过。✅ 95/95 全绿
- [x] P5-4：FFVM.Cli 与 StandaloneRunner 编译全绿。✅ 0 errors
- [x] P5-5：更新 Overview_LSP.md 仪表板，标记 DX22 完成。✅

验收标准：

- [x] A5-1：V2 矩阵所有 M 单元格已覆盖（测试可追溯到具体 LSPNEW ID）。✅
- [x] A5-2：全量 LSPNEW / DBMID / LspDatabaseQuery 全绿。✅ 95/95
- [x] A5-3：FFVM.Cli + StandaloneRunner 编译全绿。✅

## 五、依赖关系

- Phase 0 是已有逻辑的测试补充，无代码改动，可立即开始。
- Phase 1 与 Phase 2 互相独立，可并行（分别处理枚举成员与外部函数）。
- Phase 3 依赖 Phase 0 基线稳定（参数作用域影响标识符解析逻辑）。
- Phase 4 依赖 Phase 3（局部变量解析是参数解析的扩展）。
- Phase 5 只在前面全部完成后执行。

建议顺序：Phase 0 → Phase 1 + Phase 2 并行 → Phase 3 → Phase 4 → Phase 5。

## 六、关键改动文件清单（预估）

| 文件 | 改动 |
| --- | --- |
| `InMemoryDatabaseExecutionOrchestrator.cs` | 新增 `EmitParameterDefinitionFacts`/`EmitLocalVarDefinitionFacts`/`CollectVarDeclStmtsFromBody`/`BuildLocalEnumMemberSymbols`/`BuildImportedEnumMemberSymbols`/`MergeEnumMemberSymbols`/`EmitEnumMemberReferenceFacts`；扩展 `EmitIdentifierReferenceFacts`（参数 + 局部变量作用域链） |
| `LspServerNewTests.cs` | T3-VAR-01, T3-STR-01, T3-FLD-01, T3-ENM-01/02, T3-EXT-01, T3-PRM-01/02, T3-LOC-01/02（共 10 条新测试，18 assertions） |
| `LspDatabaseTests.cs` | 可选：EnumMember / Parameter / LocalVar fact 发射护栏测试 |

> 只读参考：`ASTNode.cs`（已有 NameLine/NameColumn 等位置属性）、`SymbolKindTag.cs`（EnumMember/Parameter 已预定义）。

## 七、回归命令清单

- `dotnet run --project StandaloneRunner -- --lsp-new-tests`
- `dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug`
- `dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Debug`

## 八、风险与缓解

| 风险 | 描述 | 缓解措施 | 状态 | 未处理部分的处理时机 |
| --- | --- | --- | --- | --- |
| R1 | 枚举成员访问语法（`Dir.Up`）与 alias 点号访问（`U.foo`）形态相同，可能产生冲突解析。 | 枚举成员解析使用独立的 `mergedEnumMemberSymbols` 字典（key=`EnumName.MemberName`），alias 解析查 alias 映射表，两者在 `BuildDocumentFacts` 中按独立路径运行，互不干扰。T3-ENM-01/02 已验证不串。 | ✅ 完全处理 | — |
| R2 | 参数 / 局部变量作用域解析引入函数体遍历，可能影响 fact 发射性能。 | 作用域解析按函数隔离，每函数独立构建 `paramSymbols` + `localSymbols` 字典，O(n) 遍历。`CollectVarDeclStmtsFromBody` 递归一次收集完毕。Fact 发射为预索引阶段（`didOpen`/workspace scan），不在查询响应路径上。 | ✅ 已缓解 | 极大函数体（数千行）性能压力 → **T5 性能优化专项** |
| R3 | 局部变量遮蔽逻辑与 T4（SC-03）有交叠，可能导致重复工作。 | T3 实现了基线作用域链 `局部 > 参数 > 模块/导入`（函数级扁平字典）。T3-LOC-02 + T3-PRM-02 验证单层遮蔽正确。 | ✅ 基线完成 | 块级作用域（`if`/`while`/`for` 内 `var`）、同名局部多次声明精度 → **T4 SC-03** |
| R4 | external func 定义入库后，可能与运行时宿主注册产生重复符号。 | `external func` 使用 `SymbolKindTag.Function` 正常入库，LSP 层无宿主概念。T3-EXT-01 验证 definition/references 全局命中。 | ✅ 完全处理 | — |

## 九、参考资料

- [Overview_LSP.md](Overview_LSP.md) — T3 专题定义、CFR-11~17
- [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) — V2 矩阵基线
- [Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md) — T1/T2 测试地图（参考格式）
- [Step_DX21_T2_IncludeAlias_Checklist.md](Step_DX21_T2_IncludeAlias_Checklist.md) — DX21 前置（已完成）
- [Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md](Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md) — DX20 前置（已完成）
