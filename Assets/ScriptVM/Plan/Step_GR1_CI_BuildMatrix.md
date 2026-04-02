# GR1 理想方案：CI 构建矩阵 — USE_FIXPOINT 自动验证

> **在整体计划中的位置**：本文档对应 VM_Summary.md §七 推进顺序中 Debug Phase 2 ✅ 之后的 **GR1 CI 构建矩阵**。
> **状态**：⏳ 待实施
> **前置**：Debug Phase 2 ✅ 已完成（412 项 Assert：112 TW + 214 Compiler + 17 Perf + 18 SkillScript + 51 Debug）
> **来源**：
> - [Outlook_And_Risks.md §八.1](Outlook_And_Risks.md#81-总览时间线) — 串行计划中 GR1 位置
> - [Outlook_And_Risks.md §四.3](Outlook_And_Risks.md#43-全局风险) — GR1 风险定义
> - [VM_Summary.md §七](../VM_Summary.md#七推进顺序严格串行) — 串行计划 GR1 条目
>
> **核心目标**：在现有 CI 工作流中增加 `USE_FIXPOINT` 构建配置，每次提交自动验证 Fix64 模式下的编译和测试通过，
> 消除 GR1 风险（Fix64 模式长期无验证导致积累隐性破坏）。

---

## 一、整体阶段中的临时妥协

| 妥协 | 理由 | 消除时间点 |
|------|------|-----------|
| 仅验证 `USE_FIXPOINT` 编译 + 测试通过 | 不验证 Fix64 数值精度与 float 的等价性 | 业务接入后补充精度对比测试 |
| 不引入 `FFVM_SCRIPT_DEBUG` 矩阵维度 | 当前调试代码通过 null-check 隔离，无条件编译 | Phase 3A（DAP 适配器）时引入 |
| CI 中仅 Release 配置验证 USE_FIXPOINT | Dev 模式使用 float，USE_FIXPOINT 仅 Release 模式使用 | 与实际构建配置矩阵一致 |

---

## 二、现有 CI 基础设施盘点

| 组件 | 状态 | 说明 |
|------|------|------|
| `.github/workflows/ci.yml` | ✅ 已有 | 3 个 Job：test、benchmark、cross-lang |
| StandaloneRunner.csproj 动态生成 | ✅ 已有 | CI 中通过 `cat >` 生成（.csproj 被 .gitignore 排除） |
| `Number.cs` USE_FIXPOINT 分支 | ✅ 已有 | `#if USE_FIXPOINT` → Fix64 (Q31.32)；否则 float |
| 412 项测试 | ✅ 已有 | 全部通过（float 模式） |

### 需要新增 / 修改

| 组件 | 说明 | 子任务 |
|------|------|--------|
| CI workflow 矩阵化 | 将 `test` job 从单一配置改为矩阵（含 USE_FIXPOINT） | A |
| .csproj 动态注入 DefineConstants | USE_FIXPOINT 通过 `-p:DefineConstants=USE_FIXPOINT` 传入 | A |
| Fix64 数值测试兼容性检查 | 确认现有 412 项测试在 Fix64 模式下可通过 | B |
| 本地验证脚本 | 提供本地运行 USE_FIXPOINT 构建的命令 | C |

---

## 三、子任务清单

### A. CI 工作流矩阵化

- [ ] **A1**. 修改 `.github/workflows/ci.yml`，将 `test` job 改为矩阵策略
  - 矩阵维度：`fixpoint: [false, true]`
  - `fixpoint: false` — 现有行为（float 模式）
  - `fixpoint: true` — 在 .csproj 中注入 `<DefineConstants>USE_FIXPOINT</DefineConstants>` 或通过 `dotnet build -p:DefineConstants=USE_FIXPOINT`
  - Job 名称区分：`Build & Test (float)` / `Build & Test (Fix64)`
- [ ] **A2**. 确保 `benchmark` job 的 `needs: test` 仅依赖 float 模式（benchmark 不需要在 Fix64 下重复运行）
- [ ] **A3**. 验证 CI 推送后两个矩阵 Job 均绿色通过

### B. Fix64 模式本地验证

- [ ] **B1**. 本地生成 .csproj 并带 USE_FIXPOINT 编译
  ```
  dotnet build StandaloneRunner/StandaloneRunner.csproj -c Release -p:DefineConstants=USE_FIXPOINT
  ```
- [ ] **B2**. 运行全部 412 项测试，确认在 Fix64 模式下通过
  - 记录需要调整的测试（如浮点精度对比断言）
- [ ] **B3**. 如有测试需调整，修改测试使其同时兼容 float 和 Fix64 模式
  - 策略：使用 `Number` 类型 API 进行比较，避免直接比较 float 字面量

### C. 文档更新

- [ ] **C1**. 更新 `VM_Summary.md §七` — 标记 GR1 ✅
- [ ] **C2**. 更新 `Outlook_And_Risks.md §四.3` — GR1 状态更新为已完成
- [ ] **C3**. 更新 `Outlook_And_Risks.md §八.1` — 串行计划中 GR1 标记 ✅

### D. 回归验证

- [ ] **D1**. CI 推送验证 — float 模式 412 项测试通过（零回归）
- [ ] **D2**. CI 推送验证 — Fix64 模式全部测试通过
- [ ] **D3**. Benchmark job 不受影响，正常生成报告

---

## 四、验收标准

| 验收项 | 通过标准 |
|--------|---------|
| CI 矩阵双配置 | `test` job 在 float 和 Fix64 模式下均绿色 |
| Fix64 测试覆盖 | 412 项（或调整后同等数量）Assert 在 Fix64 下全部通过 |
| 零回归 | float 模式测试结果不变 |
| Benchmark 无影响 | benchmark 和 cross-lang job 正常运行 |

---

## 五、风险评估

| 风险 | 影响 | 缓解 |
|------|------|------|
| Fix64 模式下部分数值测试断言失败 | 测试不通过 | B3 子任务处理：改用 Number API 进行精度容忍比较 |
| Fix64 编译错误（Number.cs 之外的 float 硬编码） | 编译失败 | 逐一修复；当前代码应已通过 Number 类型统一 |
| CI 矩阵增加运行时间 | 构建时间翻倍 | 可接受：两个轻量级 .NET 构建（各 ~30s） |

---

## 六、预估工作量

| 子任务 | 预估 |
|--------|------|
| A. CI 矩阵化 | 小（YAML 修改） |
| B. Fix64 本地验证 + 测试调整 | 中（可能有数值精度差异需处理） |
| C. 文档更新 | 极小 |
| D. 回归验证 | 自动（CI 运行） |
