# DX24: T5 结果质量 Checklist

> 前置：DX23 完成（T4 全绿 121/121）。
> T5 目标：去重、稳定排序、includeDeclaration 行为、异常降级均达到生产质量。

## 零、规划原则

- P-1：Q-01~Q-04 四个子目标各对应一个 Phase。
- P-2：Q-01/Q-02/Q-03 已有实现+部分测试，本轮补测加固为主。
- P-3：Q-04 是主要工作项 — 补异常场景测试。
- P-4：每 Phase 新增测试不超过 5 条。

## 一、完成定义

- [x] DOD-1：菱形 include 去重回归测试（CFR-04）。✅ T5-Q01-01
- [x] DOD-2：同一请求多次执行结果一致（CFR-18 幂等性）。✅ T5-Q02-01
- [x] DOD-3：includeDeclaration true/false 边界场景覆盖。✅ T5-Q03-01
- [x] DOD-4：畸形源码/循环 include/空源码下 references 优雅降级。✅ T5-Q04-01/02/03
- [x] DOD-5：Q-01~Q-04 均有 LSPNEW 测试覆盖。✅ 128/128 全绿

## 二、当前基线

### 已实现

| 编号 | 能力 | 位置 |
| --- | --- | --- |
| B-1 | HashSet 去重 key=`doc\|line\|char\|spanLen` | QueryFacade L567-597, L637-648 |
| B-2 | 6 级确定性排序器 doc>line>char>spanLen>spanStart>factId | QueryFacade L598-636 |
| B-3 | 索引构建阶段 references 预排序 | IndexMaintainer L283 |
| B-4 | Dictionary 迭代排序化 `orderedDocuments.Sort` | IndexMaintainer L181-182 |
| B-5 | includeDeclaration 参数读取+过滤 | VsCodeBridge L349, IndexMaintainer L635-663 |
| B-6 | 循环 include 防护 `visitedTargets` | Orchestrator L1687/2598/3029 |
| B-7 | 解析失败返回空 facts | Orchestrator null module guards |
| B-8 | 未解析符号返回 NotFound | QueryFacade L62/79/189/417 |

### 已有测试

| 编号 | 测试 | 位置 |
| --- | --- | --- |
| T-1 | LSPNEW-16B 菱形 include 去重+排序唯一 | Tests L1115 |
| T-2 | LSPNEW-13A/B/C includeDeclaration toggle | Tests L736 |
| T-3 | INC05 循环 include（编译层，非 LSP） | CompilerTests |
| T-4 | LSPNEW-03/04 畸形源码诊断发布 | Tests L92 |

### 缺口

- [x] G-1：CFR-18 幂等性无测试 → ✅ T5-Q02-01 已补。
- [x] G-2：循环 include LSP references 层无覆盖 → ✅ T5-Q04-02 已补。
- [x] G-3：畸形源码的 references 查询行为无覆盖 → ✅ T5-Q04-01 已补。
- [x] G-4：空源码 LSP 查询行为无覆盖 → ✅ T5-Q04-03 已补。
- [x] G-5：includeDeclaration 缺失 context 字段时的默认行为无测试 → ✅ T5-Q03-01 已补。

## 三、目标覆盖映射

| CFR/Q | 可搜索单元 | 状态 | Phase |
| --- | --- | --- | --- |
| CFR-04/Q-01 | 菱形 include 去重 | ✅ 已实现+已测+加固 | 0 |
| CFR-18/Q-02 | 稳定排序+幂等性 | ✅ 排序+幂等均已测 | 1 |
| Q-03 | includeDeclaration | ✅ 已实现+已测+加固 | 2 |
| Q-04 | 优雅降级 | ✅ 实现+测试均完成 | 3 |

## 四、推进清单

### Phase 0：Q-01 去重加固（CFR-04）

> LSPNEW-16B 已覆盖菱形去重。本 Phase 审查 + 补回归测试。

- [x] P0-1：审查 `BuildReferenceDedupeKey`（QueryFacade L637-648）— 4 字段 key 对所有 fact kind 充分。✅
- [x] P0-2：LSPNEW T5-Q01-01 — 菱形 include 去重回归，Count==3 + `AreLocationsSortedAndUnique`。✅

验收：✅ A0-1 T5-Q01-01 绿。✅ A0-2 全量 128/128 无退化。

### Phase 1：Q-02 稳定排序+幂等性（CFR-18）

> 排序已确定性实现，本 Phase 补幂等性测试。

- [x] P1-1：LSPNEW T5-Q02-01 — 同一 references 请求发送两次，逐元素一致。✅
- [x] P1-2：LSPNEW T5-Q02-02 — 跨文件 references 排序验证，`AreLocationsSortedAndUnique`。✅

验收：✅ A1-1 T5-Q02-01/02 绿。✅ A1-2 含 Phase 0 全量 128/128 无退化。

### Phase 2：Q-03 includeDeclaration 加固

> LSPNEW-13A/B/C 已覆盖主路径。本 Phase 补缺失 context 边界。

- [x] P2-1：LSPNEW T5-Q03-01 — references 请求无 context 字段，行为等同 includeDeclaration=false。✅
- [x] P2-2：审查 VsCodeBridge L349 — null context / missing field 安全回退 false。✅

验收：✅ A2-1 T5-Q03-01 绿。✅ A2-2 含 Phase 0-1 全量 128/128 无退化。

### Phase 3：Q-04 优雅降级

> 异常场景下 references 不崩溃不误报。T5 最主要新增工作。

- [x] P3-1：LSPNEW T5-Q04-01 — 畸形源码 references 查询返回部分结果，不抛异常。✅
  - 场景：文件含语法错误（`func broken(`），对有效位置查 references。
- [x] P3-2：LSPNEW T5-Q04-02 — 循环 include 下 references 不死循环。✅
  - 场景：两文件互相 include，查询函数 references。
- [x] P3-3：LSPNEW T5-Q04-03 — 空源码文件 references 查询返回结果不崩溃。✅
  - 场景：include 空文件，对主文件符号查 references。

验收：✅ A3-1 T5-Q04-01/02/03 绿。✅ A3-2 含 Phase 0-2 全量 128/128 无退化。

### Phase 4：收敛+回归

- [x] P4-1：全量 LSPNEW 回归通过。✅ 128/128
- [x] P4-2：FFVM.Cli + StandaloneRunner 编译全绿。✅ 0 errors
- [x] P4-3：更新 Overview_LSP.md 仪表板标记 DX24 完成。✅

验收：✅ A4-1 全量 LSPNEW 128/128 全绿。✅ A4-2 编译全绿。

## 五、依赖与顺序

- Phase 0-2 相互独立，可并行。
- Phase 3 建议在 0-2 完成后执行。
- Phase 4 最后执行。
- 推荐：Phase 0 → 1 → 2 → 3 → 4。

## 六、改动文件预估

| 文件 | 改动 |
| --- | --- |
| `InMemoryLspQueryFacade.cs` | 审查 `BuildReferenceDedupeKey`，预计无改动 |
| `InMemoryIndexMaintainer.cs` | 审查 `includeDeclaration` 路径，预计无改动 |
| `LspServerNewTests.cs` | 新增 7 条测试：T5-Q01-01, Q02-01/02, Q03-01, Q04-01/02/03 |

> 只读：`DatabaseBackedVsCodeBridge.cs`、`InMemoryDatabaseExecutionOrchestrator.cs`。

## 七、回归命令

```
dotnet run --project StandaloneRunner -- --lsp-new-tests
dotnet build StandaloneRunner/StandaloneRunner.csproj -c Debug
```
