# LSP 总览

> 本文件是 LSP 功能的单一总览入口，涵盖 T1-T6 专题定义、覆盖模型、阶段进展与文件结构索引。
> 具体步骤实施细节见各 `Step_` 文件；交叉验证视角见 `Map_` 文件。

## 一、专题定义（T1-T6）

### T1：文件收集

工作区文件发现、include 图构建、增量更新一致性。

- FC-01：工作区扫描只纳入 .ffs 源文件，排除 Library/Temp/obj/bin 等噪音目录。
- FC-02：include 路径解析统一归一化（相对路径、扩展名补全、大小写一致性）。
- FC-02A：include 路径解析必须纳入 .ffproj includePaths / 工作区上下文，不能只按当前文档目录做局部解析。
- FC-03：传递 include 链（A->B->C）完整收集，任一节点变化可触发受影响重建。
- FC-04：菱形 include（A->B, A->C, B/C->D）不重复入库、不重复返回引用。
- FC-05：文件 created/changed/deleted/renamed 后索引及时收敛，无陈旧引用残留。
- FC-06：open buffer 与 watcher 同时更新时，采用一致优先级策略避免状态抖动。

### T2：内联 / include as / 别名

include 扁平合并、include as、别名 override。

- IN-01：普通 include 的声明进入主模块可见域，引用可跨文件命中 OriginFile。
- IN-02：include as Alias 不做扁平合并，符号通过 Alias 命名空间访问。
- IN-03：别名重名冲突可诊断，且不会污染已有 alias 绑定。
- IN-04：override Alias.Name 的绑定目标正确，references 指向被替换后的有效声明。
- IN-05：include 声明本身可参与 references（模块路径声明与使用一致）。
- IN-06：alias 场景下 definition/references/rename 的符号身份保持一致。

### T3：语法结构

可被搜索单元 × 出现位置 的覆盖矩阵。

> 详细模型基线见 [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md)。

| 可被搜索单元 | 类型定义 | 结构体定义 | 枚举定义 | external定义 | 模块 | 函数 | 右值 | 右值嵌套区域 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Include 文件路径（IncludeFile） | - | - | - | - | D/R | - | - | - |
| 类型（StructType） | D/R | D/R | - | R | R | R | R | R |
| 枚举类型（EnumType） | D/R | - | D/R | R | R | R | R | R |
| 值（模块 var/const） | - | - | - | - | D/R | R | R | R |
| 值（局部 var） | - | - | - | - | - | D/R | R | R |
| 参数（Parameter） | - | - | - | - | - | D/R | R | R |
| 枚举值（EnumMember） | - | - | D/R | - | R | R | R | R |
| 函数（func） | - | - | - | - | D/R | D/R | R | R |
| 宿主函数（external func，Function 子类） | - | - | - | D/R | R | R | R | R |
| 结构体字段（StructField，含嵌套） | - | D/R | - | - | R | R | R | R |

### T4：作用域与可见性

private/public、同名冲突、override 仲裁。

- SC-01：private 声明不跨文件泄漏，references 不越权命中。
- SC-02：同名符号（函数/变量/字段/枚举成员）按 kind + parent + scope 稳定区分。
- SC-03：局部变量遮蔽模块变量时，references 必须绑定最近合法声明。
- SC-04：override 仅替换合法目标；非法 override 不应污染引用结果。

### T5：结果质量

去重、稳定排序、失效收敛、错误降级。

- Q-01：同一位置仅返回一次（去重）。
- Q-02：结果顺序稳定（文档、行、列、span）。
- Q-03：includeDeclaration true/false 行为一致且可预测。
- Q-04：无法静态解析的复杂链路要明确降级（不误报优先于强行猜测）。

### T6：性能与可观测性

初始化成本、查询耗时、日志指标。

- OBS-01：初始化记录扫描文件数、入库数、失败数、耗时。
- OBS-02：references 查询记录候选符号数、过滤后结果数、查询耗时。
- OBS-03：大工作区分批入库，避免 initialize 长时间阻塞。
- OBS-04：watcher 高频变更下保证最终一致，不出现长尾陈旧结果。

## 二、CFR 场景列表（覆盖目标）

| 场景ID | 场景 | 期望 |
| --- | --- | --- |
| CFR-01 | 未打开文件中的定义与引用 | initialize 预索引后，references 可直接命中（无需 didOpen） |
| CFR-02 | 直接 include（A include B）跨文件函数引用 | 返回 B 中定义 + A 中调用点 |
| CFR-03 | 传递 include（A->B->C）跨文件引用 | 返回 C 中定义 + A/B 中有效调用点 |
| CFR-04 | 菱形 include（A->B, A->C, B/C->D） | 结果去重，不重复返回 D 中同一位置 |
| CFR-05 | 文件监听 create/change 后新增引用 | 不重启会话即可命中新增引用 |
| CFR-06 | include 删除/重命名后的失效引用 | 结果及时收敛，不残留陈旧引用 |
| CFR-07 | 同名函数跨文件冲突 | 基于可见性/作用域/绑定键稳定仲裁，不串符号 |
| CFR-08 | private 声明跨文件不可见 | references 不应越过可见性边界误报 |
| CFR-09 | override 跨文件替换 | references 指向被替换后的有效声明与调用点 |
| CFR-10 | include as Alias + 别名相关声明 | 别名路径下符号与原符号绑定一致，不漏报 |
| CFR-11 | 模块变量（var/const）跨文件引用 | 定义与读写引用可全局命中 |
| CFR-12 | 结构体类型跨文件引用（声明/类型注解/字面量） | 类型定义与使用点可全局命中 |
| CFR-13 | 结构体字段直接访问（a.hp）跨文件 | 字段定义 + 读写位置全局命中 |
| CFR-14 | 嵌套字段访问（a.stats.hp）跨文件 | 嵌套链末端字段与中间字段都可正确绑定 |
| CFR-15 | 同名字段存在于不同结构体 | 按 parent/type 区分，避免跨结构体误并 |
| CFR-16 | 枚举/枚举成员跨文件引用 | Enum 与 EnumMember 的定义/使用全局命中 |
| CFR-17 | 外部声明（external func）在工作区内的调用引用 | 声明与调用点全局命中（不依赖宿主实现） |
| CFR-18 | 引用结果稳定性 | 多次同请求返回顺序一致（doc/line/char） |

建议优先级：

- P0：CFR-01/02/05/18（先稳住"可用性"）。
- P1：CFR-03/04/07/08/09/11（稳住"跨文件语义"）。
- P2：CFR-12/13/14/15/16/17（完成"结构化语义"）。

## 三、专题与 CFR 对照

- T1 对应：CFR-01/03/04/05/06。
- T2 对应：CFR-02/09/10。
- T3 对应：CFR-11/12/13/14/15/16/17。
- T4 对应：CFR-07/08/09/15。
- T5 对应：CFR-04/06/18。
- T6 对应：Phase 1/Phase 4 的性能与发布门槛项。

## 四、完整性校验门槛

- COV-01：T1-T6 每个专题都至少映射一个 CFR 场景。
- COV-02：每类可搜索单元（IncludeFile/函数/宿主函数语义子类/模块值/局部值/参数/结构体类型/结构体字段/枚举类型/枚举成员）都有定义位与引用位覆盖。
- COV-03：每个位置维度（类型定义/结构体定义/枚举定义/external 定义/模块/函数/右值/右值嵌套区域）都至少出现一次，且 include 路径字面量单独覆盖。
- COV-04：每类风险（错误绑定、越权可见性、重复结果、性能退化）都有对应验收项或缓解项。
- COV-05：每个 Phase 都有可判定的退出条件（A1/A2/A3/A4）。

## 五、V0 校验结论（模型基线）

- V1 高层矩阵方向正确，但存在缺项与边界歧义；已补齐为 V2（见验证文档）。
- 必补单元：IncludeFile、Parameter、局部变量。
- 边界修正：external func 是 Function 的语义子类，不是独立 SymbolKind。
- 前置门槛：未完成 V0 校验与差异收敛前，不进入 T1/T2 的测试代码实现。
- 详见 [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md)。

## 六、阶段进展仪表板

| 阶段 | 描述 | 状态 | 步骤文件 |
| --- | --- | --- | --- |
| LSP Phase4 | LSP Server 核心 + 实时诊断 | ✅ 完成 | [Step_LSP_Phase4.md](Step_LSP_Phase4.md) |
| LSP4 | documentSymbol + hover + definition + references | ✅ 完成 | [Step_LSP4_Symbols.md](Step_LSP4_Symbols.md) |
| LSP5 | textDocument/completion | ✅ 完成 | [Step_LSP5_Completion.md](Step_LSP5_Completion.md) |
| LSP6 | Syscall 声明协议 .ffvm.d.json | ✅ 完成 | [Step_B_Alpha1_LSP6_SyscallDecl.md](Step_B_Alpha1_LSP6_SyscallDecl.md) |
| LSP7 | signatureHelp | ✅ 完成 | [Step_B_Alpha2_LSP7_SignatureHelp.md](Step_B_Alpha2_LSP7_SignatureHelp.md) |
| DX11 | 连续 rename 状态修复 | ✅ 完成 | [Step_DX11_VFS_RenameState.md](Step_DX11_VFS_RenameState.md) |
| DX13 | 参数 LSP 完整支持 | ✅ 完成 | [Step_DX13_ParameterLsp.md](Step_DX13_ParameterLsp.md) |
| DX14 | Rename 完整性补全 | ✅ 完成 | [Step_DX14_RenameCompleteness.md](Step_DX14_RenameCompleteness.md) |
| DX15 | Private 跨文件补全过滤 | ✅ 完成 | [Step_DX15_PrivateCompletionFilter.md](Step_DX15_PrivateCompletionFilter.md) |
| DX16 | 变量引用作用域隔离 | ✅ 完成 | [Step_DX16_ScopeIsolatedRefs.md](Step_DX16_ScopeIsolatedRefs.md) |
| DX17 | 统一符号解析 | 🔧 进行中 | [Step_DX17_UnifiedSymbolResolution.md](Step_DX17_UnifiedSymbolResolution.md) |
| DX18 | 统一引用收集 | ✅ 完成 | [Step_DX18_UnifiedRefCollection.md](Step_DX18_UnifiedRefCollection.md) |
| DX19 | ResolveSymbol 候选仲裁修复 | ✅ 完成 | [Step_DX19_ResolveSymbolCandidateResolution.md](Step_DX19_ResolveSymbolCandidateResolution.md) |
| DX20 | 全局工作区 Find All References | ✅ 完成 | [Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md](Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md) |
| DX21 | T2 内联/include as/别名 LSP 语义 | ✅ 完成 | [Step_DX21_T2_IncludeAlias_Checklist.md](Step_DX21_T2_IncludeAlias_Checklist.md) |
| DX22 | T3 语法结构覆盖矩阵 | ✅ 完成 | [Step_DX22_T3_SyntaxStructure_Checklist.md](Step_DX22_T3_SyntaxStructure_Checklist.md) |
| DX23 | T4 作用域与可见性 | ⚪ 未开始 | [Step_DX23_T4_ScopeVisibility_Checklist.md](Step_DX23_T4_ScopeVisibility_Checklist.md) |

## 七、文件结构索引

本目录遵循三类文件命名约定：

| 前缀 | 用途 | 说明 |
| --- | --- | --- |
| `Overview_` | 总览 | 单文件，T1-T6 全流程阶段、覆盖模型、进展仪表板 |
| `Step_` | 步骤实施 | 多文件，具体流程阶段的 checklist（不含测试代码） |
| `Map_` | 辅助地图 | 步骤实施的交叉验证，完整性视角，用于编写测试代码 |

### 当前文件清单

### 总览

- [Overview_LSP.md](Overview_LSP.md)（本文件）

### 步骤实施（按时间顺序）

- [Step_LSP_Phase4.md](Step_LSP_Phase4.md) — LSP Server 核心
- [Step_LSP4_Symbols.md](Step_LSP4_Symbols.md) — 符号分析
- [Step_LSP5_Completion.md](Step_LSP5_Completion.md) — 代码补全
- [Step_B_Alpha1_LSP6_SyscallDecl.md](Step_B_Alpha1_LSP6_SyscallDecl.md) — Syscall 声明
- [Step_B_Alpha2_LSP7_SignatureHelp.md](Step_B_Alpha2_LSP7_SignatureHelp.md) — 参数提示
- [Step_DX11_VFS_RenameState.md](Step_DX11_VFS_RenameState.md) — 连续 rename 修复
- [Step_DX13_ParameterLsp.md](Step_DX13_ParameterLsp.md) — 参数 LSP
- [Step_DX14_RenameCompleteness.md](Step_DX14_RenameCompleteness.md) — Rename 完整性
- [Step_DX15_PrivateCompletionFilter.md](Step_DX15_PrivateCompletionFilter.md) — Private 过滤
- [Step_DX16_ScopeIsolatedRefs.md](Step_DX16_ScopeIsolatedRefs.md) — 作用域隔离
- [Step_DX17_UnifiedSymbolResolution.md](Step_DX17_UnifiedSymbolResolution.md) — 统一符号解析
- [Step_DX18_UnifiedRefCollection.md](Step_DX18_UnifiedRefCollection.md) — 统一引用收集
- [Step_DX19_ResolveSymbolCandidateResolution.md](Step_DX19_ResolveSymbolCandidateResolution.md) — 候选仲裁
- [Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md](Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md) — 全局引用
- [Step_DX21_T2_IncludeAlias_Checklist.md](Step_DX21_T2_IncludeAlias_Checklist.md) — T2 别名 LSP 语义
- [Step_DX22_T3_SyntaxStructure_Checklist.md](Step_DX22_T3_SyntaxStructure_Checklist.md) — T3 语法结构覆盖矩阵
- [Step_DX23_T4_ScopeVisibility_Checklist.md](Step_DX23_T4_ScopeVisibility_Checklist.md) — T4 作用域与可见性

### 辅助地图

- [Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md) — T1+T2 测试完整性地图
- [Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md) — 可搜索单元×位置模型验证
