# 讨论区

> **定位**：设计探讨、方案对比、决策记录。讨论收敛后的结论转化为计划区步骤或参考区文档。
>
> **状态标记**：
> - 💬 **讨论中** — 活跃讨论，待结论
> - ✅ **已完成讨论** — 讨论已收敛并形成决策/行动（可被再次激活为 💬 讨论中）
>
> **文件命名**：自由格式。从其他分区迁入的文件保留原名。

---

## 索引

| # | 文档 | 主题 | 状态 | 日期 | 备注 |
|---|------|------|------|------|------|
| D1 | [VMScript.md](VMScript.md) | 初始需求与设计讨论 | ✅ 已完成 | 早期 | 内容已压缩入 VM_Summary.md |
| D2 | [VMScript2.md](VMScript2.md) | 设计迭代（历史教训推导） | ✅ 已完成 | 早期 | 同上 |
| D3 | [VMScript3.md](VMScript3.md) | 设计迭代（架构约束） | ✅ 已完成 | 早期 | 同上 |
| D4 | [VMScript4.md](VMScript4.md) | 设计迭代（成功标准 + 验证轴线） | ✅ 已完成 | 早期 | 同上 |
| D5 | [Step_C0_DeploymentArchitecture.md](Step_C0_DeploymentArchitecture.md) | 实战部署架构（VM 分配策略 + 多实例交互） | 💬 讨论中 | 2026-04-05 | C 阶段前置讨论 |
| D6 | [Step_DIST_Distribution.md](Step_DIST_Distribution.md) | 分发计划（独立类库 + CLI + 单文件发布 + attach 模式） | ✅ 已完成 | 2026-04-05 | 三层分发架构讨论，DIST-1~DIST-3 已实现；§六 补充 attach 缺口 |
| D7 | [D_RuntimeCompilation.md](D_RuntimeCompilation.md) | 游戏内运行时动态编译 | 💬 讨论中 | 2026-04-05 | 当前 DIST 架构对动态编译的支持评估 |
| D8 | [D_TracerBullet.md](D_TracerBullet.md) | 曳光弹：范围、目标与验证 | ✅ 已完成 | 早期 | 从 VM_Summary.md §四 抽取 |
| D9 | [VM_Tracer_Bullet.md](VM_Tracer_Bullet.md) | 曳光弹原始详细设计讨论 | ✅ 已完成 | 早期 | 从 Reference/ 迁入 |
| D10 | [D_DapAttachMode.md](D_DapAttachMode.md) | DAP Attach 模式：分发机制的功能缺口 | ✅ 已完成 | 2026-04-06 | DIST-8+DIST-9 已实现：DapServerBase + EmbeddableDapServer 提取到 FFVM 库，Sandbox 消费分发库 API |
| D11 | [Step_DIST_Distribution.md](Step_DIST_Distribution.md) §七 | .NET 多版本兼容策略：分发机制对不同 .NET 版本的应对 | ✅ 已完成 | 2026-04-06 | DIST-10 ✅：双目标 TFM + CLI RollForward + KOF98 覆盖验证。AggressiveOptimization 条件编译修复 |
| D12 | [D_DeepInlining.md](D_DeepInlining.md) | A5 深度内联展开可行性分析 | ✅ 已完成 | 2026-04-10 | Lang-9 可行性分析。核心结论：编译器总是主动内联，`@inline` 仅控制诊断。分阶段路径 P1~P4，✅ 可行。→ [Step_Lang9](../Plan/Step_Lang9_DeepInlining.md) |
| D13 | [D_PublicPrivateVisibility.md](D_PublicPrivateVisibility.md) | Include 可见性：public / private 修饰符 | ✅ 已完成 | 2026-04-11 | Lang-15 设计讨论。public/private 与 @export 完全隔离。private = 名称隔离不影响 mixin 运行语义。origin-aware lookup 方案。→ [Step_Lang15](../Plan/Step_Lang15_PublicPrivateVisibility.md) |
| D14 | [D_IncludeAs.md](D_IncludeAs.md) | Include As 别名：命名空间隔离的 include | ✅ 已完成 | 2026-04-11 | Lang-17 设计讨论。`include "path" as Alias` 语法 + Alias.Name 命名空间访问 + override Alias.Name 替换。方案 A（include as）vs 方案 B（using =），采纳方案 A。→ [Step_Lang17](../Plan/Step_Lang17_IncludeAs.md) |
