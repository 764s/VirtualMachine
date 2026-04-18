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

- [ ] DOD-1：模块变量（var/const）跨文件 definition/references 全局命中（CFR-11）。
- [ ] DOD-2：结构体类型跨文件 definition/references 全局命中（CFR-12，含声明 / 类型注解 / 字面量）。
- [ ] DOD-3：同名字段在不同结构体中按 parent 区分，不误并（CFR-15）。
- [ ] DOD-4：枚举类型 + 枚举成员跨文件 definition/references 全局命中（CFR-16）。
- [ ] DOD-5：外部函数（external func）声明与调用引用全局命中（CFR-17）。
- [ ] DOD-6：参数 definition/references 在函数体内正确绑定。
- [ ] DOD-7：局部变量 definition/references 在函数体内正确绑定，不与模块变量串符号。
- [ ] DOD-8：V2 矩阵所有 M（必须覆盖）单元格均有对应测试。

## 二、当前基线

### 已实现（DX20/DX21 落地）

- [x] B-1：函数定义 + 引用（同文件 / 跨文件 / aliased / override）— 全绿。
- [x] B-2：模块变量定义（`EmitModuleVarDefinitions`）+ 标识符引用（`EmitIdentifierReferenceFacts`）— 已发射，无专项测试。
- [x] B-3：结构体类型定义（`EmitStructNameDefinitions`）+ 标识符引用（类型注解 / 字面量）— 已发射，无专项测试。
- [x] B-4：结构体字段定义（`BuildLocalStructFieldSymbols`）+ 字段访问引用（含嵌套链）— 已实现 + LSPNEW-14/15 测试。
- [x] B-5：枚举类型定义（`EmitEnumDefinitions`）+ 标识符引用 — 已发射，无专项测试。
- [x] B-6：Include 文件路径定义 + 引用（`EmitIncludeEdgeFacts`）— 已实现 + 测试。

### 缺口（本轮需补）

- [ ] G-1：枚举成员（EnumMember）— `SymbolKindTag.EnumMember` 已定义但从未发射 def/ref fact。
- [ ] G-2：外部函数（external func）— 索引循环 `function.Body == null` 跳过，定义从不入库。
- [ ] G-3：参数（Parameter）— `SymbolKindTag.Parameter` 已定义但从未发射 def/ref fact。
- [ ] G-4：局部变量（Local var）— 函数体内 `VarDeclStmt` 未发射定义 fact；`EmitIdentifierReferenceFacts` 仅解析模块级符号。
- [x] G-5：CFR-11（模块变量）/ CFR-12（结构体类型）/ CFR-15（同名字段）— Phase 0 已补专项测试，全绿。
- [ ] G-6：CFR-16（枚举/枚举成员）/ CFR-17（外部函数）— 阻塞于 G-1/G-2。

## 三、目标覆盖映射

> 详细模型基线见 [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) §V2。
> CFR 场景见 [Overview_LSP.md](Overview_LSP.md) §二。

| CFR 场景 | 可搜索单元 | 当前状态 | Phase |
| --- | --- | --- | --- |
| CFR-11 | 模块变量（var/const） | ✅ Phase 0 全绿 | 0 |
| CFR-12 | 结构体类型 | ✅ Phase 0 全绿 | 0 |
| CFR-15 | 同名字段（不同结构体） | ✅ Phase 0 全绿 | 0 |
| CFR-16 | 枚举类型 + 枚举成员 | 枚举类型有，成员缺发射 | 1 |
| CFR-17 | 外部函数 | 定义被跳过 | 2 |
| — | 参数 | 完全缺失 | 3 |
| — | 局部变量 | 完全缺失 | 4 |

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

### Phase 1：枚举成员 definition / references（CFR-16）

> 依赖 Phase 0 基线稳定。核心改动：为 EnumMember 发射 def/ref facts。

- [ ] P1-1：`EmitEnumMemberDefinitions` — 遍历 `EnumDecl.Members`，为每个枚举成员发射 `SymbolKindTag.EnumMember` 定义 fact。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P1-2：`EmitEnumMemberReferenceFacts` — 遍历表达式树，识别枚举成员访问（`Dir.Up`）并发射 `SymbolReference` fact。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P1-3：LSPNEW 测试 T3-ENM-01 — 枚举成员跨文件 definition + references（CFR-16）。
  - 场景：A.ffs 声明 `enum Dir { Up, Down }`，B.ffs include A 并引用 `Dir.Up`。
  - 断言：`Dir` 的 definition/references 命中类型，`Dir.Up` 的 definition/references 命中成员。
- [ ] P1-4：LSPNEW 测试 T3-ENM-02 — 同名枚举成员不同枚举不误并。
  - 场景：`enum A { X }` 和 `enum B { X }`，`A.X` 和 `B.X` 引用不串。

验收标准：

- [ ] A1-1：T3-ENM-01、T3-ENM-02 全绿。
- [ ] A1-2：Phase 0 测试仍绿 + DBMID / LspDatabaseQuery 全绿。

### Phase 2：外部函数 definition（CFR-17）

> 依赖 Phase 0 基线稳定。核心改动：索引循环不再跳过 external func。

- [ ] P2-1：函数定义发射循环移除 `function.Body == null` 跳过逻辑（或新增 external 分支），为 `IsExternal` 函数发射 `SymbolKindTag.Function` 定义 fact。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P2-2：验证 `BuildImportedFunctionSymbols` 已包含 external func（跨文件调用引用匹配）。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P2-3：LSPNEW 测试 T3-EXT-01 — external func 声明与调用引用（CFR-17）。
  - 场景：A.ffs 声明 `external func print(msg)`，B.ffs include A 并调用 `print("hello")`。
  - 断言：definition 指向 A 的 external 声明位，references 返回声明 + 调用点。

验收标准：

- [ ] A2-1：T3-EXT-01 全绿。
- [ ] A2-2：Phase 0-1 测试仍绿 + DBMID / LspDatabaseQuery 全绿。

### Phase 3：参数 definition / references

> 依赖 Phase 0 基线稳定。核心改动：为 Parameter 发射 def/ref facts + 函数体内作用域解析。

- [ ] P3-1：`EmitParameterDefinitions` — 遍历 `FuncDecl.Parameters`，为每个参数发射 `SymbolKindTag.Parameter` 定义 fact（scope 限定到函数体）。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P3-2：`EmitIdentifierReferenceFacts` 增加参数符号解析 — 函数体内标识符优先匹配参数表。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P3-3：LSPNEW 测试 T3-PRM-01 — 参数 definition + references（同文件）。
  - 场景：`func add(a, b) { return a + b }`。
  - 断言：`a` 的 definition 指向参数声明，references 返回声明 + 函数体内使用点。
- [ ] P3-4：LSPNEW 测试 T3-PRM-02 — 参数与模块变量同名时绑定最近声明。
  - 场景：模块 `var x = 1`，`func f(x) { return x }`。
  - 断言：函数体内 `x` 的 references 仅指向参数，不串模块变量。

验收标准：

- [ ] A3-1：T3-PRM-01、T3-PRM-02 全绿。
- [ ] A3-2：Phase 0-2 测试仍绿 + DBMID / LspDatabaseQuery 全绿。

### Phase 4：局部变量 definition / references

> 依赖 Phase 3（参数作用域解析就位后扩展到局部变量）。核心改动：函数体内 VarDeclStmt 发射定义 fact + 局部作用域解析。

- [ ] P4-1：`EmitLocalVarDefinitions` — 遍历函数体内 `VarDeclStmt`，为每个局部变量发射 `SymbolKindTag.Variable` 定义 fact（scope 限定到函数体，与模块变量区分）。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P4-2：`EmitIdentifierReferenceFacts` 增加局部变量符号解析 — 函数体内标识符按作用域链解析（局部 > 参数 > 模块）。
  - 文件：`InMemoryDatabaseExecutionOrchestrator.cs`
- [ ] P4-3：LSPNEW 测试 T3-LOC-01 — 局部变量 definition + references（同文件）。
  - 场景：`func f() { var count = 0; count = count + 1; return count }`。
  - 断言：`count` 的 definition 指向 `var count` 声明，references 返回声明 + 赋值 + 读取点。
- [ ] P4-4：LSPNEW 测试 T3-LOC-02 — 局部变量遮蔽模块变量。
  - 场景：模块 `var n = 1`，`func f() { var n = 2; return n }`。
  - 断言：函数体内 `n` 的 references 仅指向局部声明，模块 `n` 不受干扰。

验收标准：

- [ ] A4-1：T3-LOC-01、T3-LOC-02 全绿。
- [ ] A4-2：Phase 0-3 测试仍绿 + DBMID / LspDatabaseQuery 全绿。

### Phase 5：V2 矩阵收敛 + 回归

> 依赖 Phase 0-4。完成后所有 M（必须覆盖）单元格均有测试验证。

- [ ] P5-1：V2 矩阵 gap 扫描 — 逐行逐列校验每个 M 单元格是否已被某个 LSPNEW 测试覆盖。
- [ ] P5-2：补缺测试（若 gap 扫描发现 Phase 0-4 未覆盖的 M 单元格）。
- [ ] P5-3：全量 LSPNEW 回归通过。
- [ ] P5-4：FFVM.Cli 与 StandaloneRunner 编译全绿。
- [ ] P5-5：更新 Overview_LSP.md 仪表板，标记 DX22 完成。

验收标准：

- [ ] A5-1：V2 矩阵所有 M 单元格已覆盖（测试可追溯到具体 LSPNEW ID）。
- [ ] A5-2：全量 LSPNEW / DBMID / LspDatabaseQuery 全绿。
- [ ] A5-3：FFVM.Cli + StandaloneRunner 编译全绿。

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
| `InMemoryDatabaseExecutionOrchestrator.cs` | 新增 `EmitEnumMemberDefinitions`/`EmitEnumMemberReferenceFacts`/`EmitParameterDefinitions`/`EmitLocalVarDefinitions`；修改函数定义循环（external 分支）；扩展 `EmitIdentifierReferenceFacts`（参数 + 局部变量作用域链） |
| `LspServerNewTests.cs` | T3-VAR-01, T3-STR-01, T3-FLD-01, T3-ENM-01/02, T3-EXT-01, T3-PRM-01/02, T3-LOC-01/02（共 10 条新测试） |
| `LspDatabaseTests.cs` | 可选：EnumMember / Parameter / LocalVar fact 发射护栏测试 |

> 只读参考：`ASTNode.cs`（已有 NameLine/NameColumn 等位置属性）、`SymbolKindTag.cs`（EnumMember/Parameter 已预定义）。

## 七、回归命令清单

- `dotnet run --project StandaloneRunner -- --lsp-new-tests`
- `dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug`
- `dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Debug`

## 八、风险与缓解

- R1：枚举成员访问语法（`Dir.Up`）与 alias 点号访问（`U.foo`）形态相同，可能产生冲突解析。
  - 缓解：枚举成员解析应先查当前文档已声明枚举，alias 解析查 alias 映射；两者按优先级仲裁。
- R2：参数 / 局部变量作用域解析引入函数体遍历，可能影响 fact 发射性能。
  - 缓解：作用域解析限定在函数体内，不跨函数；fact 发射为预索引阶段，不影响查询响应时间。
- R3：局部变量遮蔽逻辑与 T4（SC-03）有交叠，可能导致重复工作。
  - 缓解：T3 仅做"可工作的基线实现"（参数 > 模块、局部 > 参数 > 模块）；T4 细化多层嵌套作用域、块级作用域等复杂场景。
- R4：external func 定义入库后，可能与运行时宿主注册产生重复符号。
  - 缓解：external func 仍使用 `SymbolKindTag.Function`，通过 `IsExternal` 属性标记区分；LSP 层无宿主概念，不冲突。

## 九、参考资料

- [Overview_LSP.md](Overview_LSP.md) — T3 专题定义、CFR-11~17
- [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) — V2 矩阵基线
- [Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md) — T1/T2 测试地图（参考格式）
- [Step_DX21_T2_IncludeAlias_Checklist.md](Step_DX21_T2_IncludeAlias_Checklist.md) — DX21 前置（已完成）
- [Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md](Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md) — DX20 前置（已完成）
