# CM: LSP 覆盖矩阵基座（两层结构化穷举）

> 讨论来源：[D_CoverageMatrix.md](../../Discussion/D_CoverageMatrix.md)（D21）
> 前置：DX19 ✅
> 性质：**文档 / 基座**。不涉及 LspServer 行为变更。

## 一、目标

建立"从 AST 文法机械生成覆盖矩阵"的可运转基座，使：

- 新增 `NodeKind` / 字段 / `SymbolKindTag` / LSP 方法 时自动暴露未覆盖点；
- 已知 B 轴遗漏（SyscallExpr / ModuleVariables.Initializer / ParamDecl.DefaultValue 的 call-ref）可在矩阵中被机械定位；
- AI / 人实现 LSP 功能时有"全域地图"可查，不再靠注意力穷举。

## 二、推进清单（Checklist）

### Phase 0：流程固化与首版人工矩阵 【本次推进】

- [x] CM0-1：固化讨论文件（D21 `D_CoverageMatrix.md`）
- [x] CM0-2：向串行需求列表提交 CM 需求（Outlook_And_Risks.md CM 条目）
- [x] CM0-3：创建 CM 子计划 checklist 文件（本文件）
- [x] CM0-4：创建首版人工矩阵 `Map_CM_CoverageMatrix.md`（两层轴 + 已知缺口标注）
- [x] CM0-5：VM_Summary 索引 CM 行

> Phase 0 产出的 Map 文件即可作为"矩阵的第一版真相源"，后续 CM1 生成器产物需与其一致或扩展。

### Phase 1：生成器骨架（CM1，可选）

- [ ] CM1-1：决定生成方式（C# 反射扫描 `FFVM.AST.*` / 单元测试期生成 / 独立 CLI 开关）
- [ ] CM1-2：扫描 AST 类型 → 输出族 / 子位置列表
- [ ] CM1-3：与 Map 文件对齐校对（diff 为空）
- [ ] CM1-4：将生成结果序列化为稳定格式（JSON/Markdown 表）便于 diff

### Phase 2：投影器骨架（CM2，可选）

- [ ] CM2-1：定义语义轴枚举（A/B/C）的代码表示
- [ ] CM2-2：对每行输出 (A,B,C) 投影单元（Covered / N-A / Missing）
- [ ] CM2-3：将 §五 已知 B 轴遗漏作为 Missing 基线验证

### Phase 3：测试标签化 + CI 守卫（CM3/CM4，可选）

- [ ] CM3-1：LSP 测试加矩阵标签（如 `[Function_FunctionBody_References]`）
- [ ] CM3-2：未被任何测试覆盖的矩阵单元产出 Missing 报告
- [ ] CM4-1：新增 `NodeKind` / `SymbolKindTag` / LSP 方法 → 矩阵未更新 → CI 失败

## 三、完成标准

### Phase 0（本次可闭合）

- Map 文件覆盖 AST 节点族 × 子位置 × (A,B,C) 投影。
- 已知 B 轴遗漏在矩阵中以明确 **Missing** 标注显形。
- 链接从 D21 / VM_Summary / Outlook / LSP Overview 可达。

### Phase 1~3（远期，业务驱动激活）

按 Phase 自身列表。**不阻塞** DX 主串行；纳入展望项。

## 四、风险与约束

- **不改动 LspServer.cs**。本计划仅产出文档制品。
- 已知 B 轴遗漏不在本计划内修复（那是 `InMemoryDatabaseExecutionOrchestrator` 级别的独立 Fix，应作为独立需求通过矩阵触发）。
- CM1~CM4 触发阈值：若后续再次出现"新增语法 → 多个 LSP 功能同时遗漏"的模式（笛卡尔积复现），立即激活 CM1 生成器，不等业务。

## 五、后续触发点

| 信号 | 响应 |
|------|------|
| 新增 `NodeKind` 或 AST 字段 | Map 文件增行；若此时 CM1 已存在，走 CI 校验 |
| 新增 `SymbolKindTag` | Map 表头扩列 |
| LSP 新方法请求接入（如 codeAction） | C 轴新取值 → 所有行重评 |
| 再次出现多功能同步遗漏 | 激活 CM1 生成器，不再容忍手工矩阵漂移 |
