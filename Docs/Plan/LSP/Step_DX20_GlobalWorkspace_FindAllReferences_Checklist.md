# DX20: 全局工作区 Find All References 与嵌套结构体字段支持 Checklist

> 前置：DX19 已完成。
> 用户意图：Find All References 必须是工作区全局能力，而不是已打开文档局部能力；结构体字段（含嵌套路径）也要可全局命中。

## 零、计划原则（目标完整性优先）

- P-1：计划只确认“目标是否完整覆盖”，不约束具体实现细节。
- P-2：细节发散是允许的，但必须能映射到明确目标项（T1-T6 或 CFR）。
- P-3：计划验收以完整性为准，不以某一种实现路径为准。
- P-4：测试模板与实现样例属于细节参考，不作为计划主线阻塞项。
- P-5：若发现新边界，优先补“目标覆盖项”，再补对应实现细节。

## 一、完成定义（Definition of Done）

- [x] DOD-1：textDocument/references 在未打开文件也能返回引用结果。
- [x] DOD-2：跨文件函数/变量引用可稳定命中定义与调用点。
- [x] DOD-3：嵌套结构体字段（例如 player.stats.hp）可跨文件全局命中。
- [x] DOD-4：结果去重且顺序稳定（同输入多次查询返回一致顺序）。
- [x] DOD-5：关键回归与构建全部通过。

## 二、当前基线（已落地）

- [x] B-1：initialize 阶段增加工作区预索引，未打开 .ffs 文件可入库建索引。
- [x] B-2：didChangeWatchedFiles 对 created/changed 的 .ffs 执行文本回灌重建。
- [x] B-3：didChange 增量编辑先归一化为完整文本再入库，避免局部文本覆盖全量状态。
- [x] B-4：LSPNEW-11 证明未打开文件可参与 completion（工作区预索引生效）。

## 三、跨文件查找引用场景矩阵

> T1-T6 专题定义、CFR 场景列表、覆盖模型与完整性校验门槛已提取到总览文件：
> [Overview_LSP.md](Overview_LSP.md)
>
>
> 测试规划入口：
>
> - 地图文件：[Map_T1_T2_TestCompleteness.md](Map_T1_T2_TestCompleteness.md)
> - 前置验证：[Map_V0_SearchableUnit_Position_Model_Verification.md](Map_V0_SearchableUnit_Position_Model_Verification.md)

## 四、推进清单

### Phase 0：基线固化与护栏

- [x] P0-1：固化本 checklist 计划文件（DX20）。
- [x] P0-2：在 LspServerNewTests 中新增 references 的未打开文件场景护栏（LSPNEW-12）。
- [x] P0-3：在 LspDatabaseTests 中补 DB 层基线断言（预索引后 snapshot facts/index 不为空）。

### Phase 1：工作区全局索引机制增强

- [x] P1-1：扫描目录过滤策略（排除 Library、Temp、obj、bin 等非源码目录）。
- [x] P1-2：预索引分批提交与超时预算，避免超大工作区初始化阻塞。
- [x] P1-3：加入可观测性指标（扫描文件数、入库数、耗时、失败数）。
- [x] P1-4：watcher create/change 与 open buffer 冲突时的优先级策略固化（open buffer 优先）。

验收标准：

- [x] A1-1：初始化后不需要 didOpen，references 可命中未打开文件中的符号。
- [x] A1-2：超大工作区下初始化无长时间卡死，且日志可定位慢点。

### Phase 2：跨文件符号绑定（Find All References 全局语义）

- [x] P2-1：在事实提取中补 IncludeEdge 事实，构建可用于跨文件解析的依赖图。
- [x] P2-2：建立跨文件符号绑定表（按 kind/name/scope/parent 组合键，带冲突仲裁）。
- [x] P2-3：引用事实从“文档内局部绑定”升级到“快照级全局绑定”。
- [x] P2-4：QueryReferences 结果统一走全局绑定索引，不依赖单文档 functionSymbols。
- [x] P2-5：新增 LSPNEW-13（跨文件函数 references，includeDeclaration true/false 双断言）。

验收标准：

- [x] A2-1：跨文件 references 返回定义与调用点，且路径归一化一致。
- [x] A2-2：同名符号冲突场景下仲裁稳定，回归不抖动。

### Phase 3：嵌套结构体字段全局引用

- [x] P3-1：补 StructField 定义事实（含 parentName 与完整字段路径标识）。
- [x] P3-2：补 FieldAccessExpr/MemberCallExpr 的字段引用事实生成。
- [x] P3-3：补嵌套访问链解析（a.b.c）到字段符号身份映射。
- [x] P3-4：新增 LSPNEW-14（同文件嵌套字段 references）。
- [x] P3-5：新增 LSPNEW-15（跨文件嵌套字段 references）。

验收标准：

- [x] A3-1：结构体字段 references 覆盖定义、读访问、写访问。
- [x] A3-2：嵌套字段跨文件命中正确，无明显误报。

### Phase 4：收敛、性能、发布门槛

- [x] P4-1：references 结果去重与稳定排序（文档、行列、span）。
- [x] P4-2：回归矩阵跑通（LSPNEW、LspDatabase、关键编译链）。
- [x] P4-3：性能阈值校验（中型工作区初始化、references 查询耗时）。
- [x] P4-4：更新计划与进展文档，标记 DX20 完成。

验收标准：

- [x] A4-1：lsp-new-tests 全绿。
- [x] A4-2：FFVM.Cli 与 StandaloneRunner 编译全绿。
- [x] A4-3：关键性能阈值达标，无明显退化。

## 五、依赖关系

- Phase 0 是基线护栏，先完成。
- Phase 1 是机制底座，Phase 2/3 依赖它。
- Phase 2 与 Phase 3 强耦合：先做跨文件绑定，再做嵌套字段。
- Phase 4 只在前面全部完成后执行。

建议顺序：Phase 0 -> Phase 1 -> Phase 2 -> Phase 3 -> Phase 4。

## 六、回归命令清单

- dotnet run --project StandaloneRunner -- --lsp-new-tests
- dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug
- dotnet build src/FFVM.Cli/FFVM.Cli.csproj -c Debug

## 七、风险与缓解

- 风险 R1：同名跨文件符号仲裁错误导致误报。
  - 缓解：引入 scope/parent/kind 多维键与冲突测试样例。
- 风险 R2：嵌套字段类型链解析不完整导致漏报。
  - 缓解：先覆盖可静态推导路径，对无法推导场景明确降级策略。
- 风险 R3：全局预索引在大仓库耗时过高。
  - 缓解：目录过滤、分批入库、超时预算与指标观测。

## 八、当前迭代建议（立即执行）

- [x] I-1：先落地 P0-2（LSPNEW-12）+ P1-1（目录过滤），快速提升用户可见正确性与稳定性。
- [x] I-2：并行设计 P2-1/P2-2 的数据结构，准备跨文件绑定实现。
