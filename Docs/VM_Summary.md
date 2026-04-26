# FFEditor 胶水执行器与技能虚拟机：阶段性总结

> 本文是项目的单一入口文档。
> 详细背景与理想目标见 [Reference/VM_Background.md](Reference/VM_Background.md)。
> 核心架构决策详见 [Reference/VM_Core_Decisions.md](Reference/VM_Core_Decisions.md)。
> 实现状态、性能记录与缺口见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)。

---

## ✅ 架构转向（已完成，进入维持期）

“VM 即 C# 对象”心智转向已按 VOM1-VOM11 主链完成并收口。详见：

- 转向定稿：[Discussion/D_VM_ObjectModel_Transition.md](Discussion/D_VM_ObjectModel_Transition.md)
- 理想形态与差距：[Discussion/D_VM_ObjectModel_IdealAndGap.md](Discussion/D_VM_ObjectModel_IdealAndGap.md)

**核心结构（五对象）**：`VMDef` / `CPUData`（执行机，含临时池）/ `VMData`（业务持久态）/ `HostBindings` / `VMInstance`(handle façade) + `InstancePool`(SoA)。

**调用契约（四档静态调用）**：`VMEngine.YieldCall` / `Call` / `ReadOnlyCall` / `StaticReadOnlyCall`，宿主 ABI 统一为 `Span<Number>` 中介的 `Arguments` / `ReturnSlot` ref struct。

**性能天花板**（详见转向定稿）：单次 15-230 ns，batch 摊销后基本回到原始需求成本。

**与本文关系**：与新心智冲突的历史叙述将随推进逐步整理。

---

### 实现流程

严格按照如下顺序执行:
1. (临时讨论)
2. 固化讨论文件
   - 讨论完成前, 文件末尾有专用讨论区用于交流
   - 之后的讨论都通过修改讨论文件达成
3. 向串行需求列表提交需求
4. 为需求规划 checklist 子计划文件
5. 推进子计划至完成

> **推进指令**：使用 `.github/prompts/` 提示模板引导串行推进：
> `#check-and-next`（检查+推进）、`#check`（仅检查）、`#requirement`（评估新需求）。
> AI 通过 `当前位置 →` 标记定位下一步。

---

### 文档目录结构

> 文档区已从 `Assets/ScriptVM/` 迁移至仓库根目录 `Docs/`，以避免 Unity 为 `.md` 文件自动生成 `.meta` 文件。

```
Docs/
  VM_Summary.md              ← 本文（唯一入口文档）
  Discussion/                ← 讨论区：设计探讨 / 方案对比 / 决策记录
  Plan/                      ← 计划区：确定步骤 / 无脑执行
  Emergency/                 ← 紧急区：恶性 / 影响深远缺陷的修复通道
  BugFix/                    ← Bug 修复区：已知 Bug 的修复跟踪
  Practice/                  ← 实践区：探索性实践记录
  Reference/                 ← 参考区：技术规格 / 架构约束 / 示例脚本
```

- **Discussion/**：讨论区。设计探讨、方案对比、决策记录。状态：💬 讨论中 / ✅ 已完成讨论（可再次激活）。详见 [Discussion/README.md](Discussion/README.md)。
- **Plan/**：计划区。确定步骤与检查清单，无脑执行。状态：⏳ 等待中 / 🔄 进行中 / ✅ 已完成。
- **Emergency/**：紧急区。恶性/影响深远缺陷的独立修复通道。详见 [Emergency/README.md](Emergency/README.md)。
- **BugFix/**：Bug 修复区。已知 Bug 的修复跟踪。与讨论区平行。详见 [BugFix/README.md](BugFix/README.md)。
- **Practice/**：实践区。串行计划之外的探索性实践。
- **Reference/**：参考区。技术规格、架构约束、示例脚本、背景文档、实现记录等长期引用文档。

---

## 设计原则与哲学

### 一句话目标

> 把技能、子弹、状态效果三套旧系统中已经客观存在但分散隐藏的流程语义，统一抬升到脚本层，并用"脚本文本为真理源 + 结构化流程图为主 UI + 寄存器式 VM 为底层 + `wait` 为一等语义 + 零分配快照回滚"为核心约束，构建一套统一执行架构。

### 核心决策（速览）

| 决策 | 要点 |
|------|------|
| ROM/RAM 分离 | 字节码只读共享，实例状态纯值类型 ECS 组件，快照退化为 memcpy |
| `wait` 一等语义 | 非语法糖，VM 写 WaitFrames + 更新 IP + 交出执行权，值类型天然可快照 |
| `using` + `defer` Cleanup | 配对 Syscall 自动 cleanup（理想）+ defer 逃生舱 + 编译器强制检查 |
| 全程 Fix64 | 确定性数值，表现走 Syscall。开发期 float 快速迭代 |
| 寄存器定长 + 句柄化 | 64 slot 固定，复杂数据 Handle64，Syscall 边界，零 GC |
| 手写递归下降 Parser | DSL 规模小（~21 关键字），零外部依赖，精确控制 |
| 黑板 Key 编译期 ID | 零碰撞，零运行时开销 |
| 脚本为真理源 | 结构化流程图为主 UI，AST 受控投影 |

> 详细理由与实现见 [Reference/VM_Core_Decisions.md](Reference/VM_Core_Decisions.md)

### 架构硬约束速查

> 完整 20 条硬纪律见 [Reference/VM_Architecture_Rules.md](Reference/VM_Architecture_Rules.md)

1. 脚本文本 = 唯一真理源
2. UI = await 驱动的视图投影
3. VM = 偏瞬态寄存器式执行模型
4. RAM = ECS 纯值类型组件
5. ROM / RAM 物理分离
6. 寄存器禁持动态/托管/不定长结构
6b. 全程 Fix64，表现走 Syscall
7. 脚本 = 单体逻辑（AoS）
8. 批处理 = 宿主 Syscall
9. 复杂临时数据 = 句柄化
10. 宿主交互 = 统一 Syscall Table
11. `wait` = 一等语义
12. 快照/回滚 > 表达便利性
13. Cleanup = `using` + `defer` + 编译器强制检查
14. 语法服从 VM 物理法则
15. AST 禁悬空高级结构
16. 新提案必须过物理约束校验
17. 每次协作重新注入本文件
18. 文档尽快落为可运行锚点
19. 以可执行验证为准
20. 黑板 Key = 编译期静态 ID

**最终裁决**：任何新提案先问——能否放进定长寄存器和 ECS 纯值 RAM？是否支持零 GC / 快照 / 回滚 / 强制 Cleanup？是否保持 Syscall 边界与句柄化纪律？否定即拒绝。

---

## 语言特性

完整能力清单与 Lang-1~18 实现条目见 [Reference/FFS_Language_Capabilities.md](Reference/FFS_Language_Capabilities.md)。  
语法规范见 [Reference/FFS_Syntax.md](Reference/FFS_Syntax.md)，速查见 [Reference/FFS_QuickRef.md](Reference/FFS_QuickRef.md)。

实现状态：全链路完成（源码 → Lexer → Parser → AST → BytecodeCompiler → VMProgram → VMWorld.Tick → 执行），约 2140 项 Assert 全通过（详见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)），编译脚本 5-7x vs C#，与 Lua 5.4 同量级。

---

## 串行需求列表

> ✅ = 已完成，⏳ = 待执行，⚪ = 被外部前置条件阻塞。
> 完整历史详情见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)；展望索引见 [Plan/Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)。
> 本节按 **特性区 / 优化区 / 语言服务器区** 三分。

---

### 特性区

#### 已完成

- **A 阶段**（Steps 1-9 + 调试 + CI）：曳光弹建立、字节码核心、Lexer/Parser、Cleanup（using/defer）、函数调用、结构体、寄存器生命周期、调试 Phase 1-3B、LSP Phase 4-5、CI 双模式矩阵 — 全部 ✅
- **B 阶段**（B-α/β/γ/δ 26 步）：Tier1 / Peephole / 叶函数 / 活跃链表 / 嵌套结构体 / 字符串 / 自适应窗口 / 非 entry defer / 热循环 / Benchmark — 全部 ✅
- **Lang 系列**（Lang-1~18，仅 Lang-4 暂缓）：模块变量 / include / 黑板 / @export / XCALL / 内联 / 枚举 / 位运算 / 可见性 / override / 别名 — 全部 ✅
- **D 分发**：DIST-1/2/3/8/9/10 — 全部 ✅

> 详细条目与计划链接见 [Reference/FFS_Language_Capabilities.md](Reference/FFS_Language_Capabilities.md)、[Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)。

#### 待执行（C. 宿主集成侧 — 生产必经路径）

部署架构决策详见 [Step_C0_DeploymentArchitecture.md](Discussion/Step_C0_DeploymentArchitecture.md)。

| 序号 | 状态 | 内容 | 前置条件 |
|------|------|------|----------|
| C0 | ✅ | 部署架构决策（VM 分配策略、多实例交互、数据读取） | — |
| C1 | ⚪ | 真实 Syscall 接入 ECS（stub → 真实宿主实现） | 宿主 ECS 就绪 |
| C2 | ⚪ | V5 帧内 Profiler 验证（含真实 ECS 交互开销） | C1 |
| C3 | ⚪ | 技能资源管线（.ffs 加载/编译/缓存/热更新） | C1 |
| C4 | ⏳ | Handle64 批处理（句柄化多目标数据流转） | C1 |
| C5 | ⚪ | 帧同步集成验证（真实网络环境快照/回滚） | C1+C2 |
| C6 | ⏳ | 编辑器流程图投影（AST → 结构化流程图主视图） | C2 |

#### 转向落地（VOM 系列 — 当前架构转向高优先级）

按 [D_VM_ObjectModel_IdealAndGap §四](Discussion/D_VM_ObjectModel_IdealAndGap.md) 提出的 S1-S9 序列推进；总入口 [Step_VOM_Overview.md](Plan/Step_VOM_Overview.md)。

| 序号 | 状态 | 内容 | 涵盖 S | 前置 |
|------|------|------|--------|------|
| VOM1 | ✅ | [VMInstanceState 切分 + MethodHandle 缓存](Plan/Step_VOM1_StateSplit.md) | S1 + S2 | — |
| VOM2 | � | [Arguments/ReturnSlot ABI + StaticReadOnlyCall + @readonly](Plan/Step_VOM2_CallABI.md) | S3 + S4 | VOM1 |
| VOM3 | � | [TransientInstancePool + Call/ReadOnlyCall + 运行期 ReadOnly 防护](Plan/Step_VOM3_CPUDataPool.md)（关键里程碑） | S5 | VOM2 |
| VOM4 | 🟢 | [YieldCall + YieldHandle](Plan/Step_VOM4_YieldCall.md) | S6 | VOM3 |
| VOM5 | 🟢 | [HostBindings 实例化 + VMInstance façade](Plan/Step_VOM5_HostBindings_Facade.md) | S7 + S8 | VOM3 / VOM4 |
| VOM6 | 🟢 | [Batch 调用入口 + 摊销基准](Plan/Step_VOM6_Batch.md) | S9 | VOM3 |
| VOM7 | 🟢 | [CPUData / VMData / MVarRegisters 类型加法](Plan/Step_VOM7_CPUData_VMData_Types.md) | 妥协 A.1 | VOM6 |
| VOM8 | 🟢 | [VMInstanceState 包装 + VOM8a 内部 ref 局部化](Plan/Step_VOM8_FieldMigration_Engine.md) | 妥协 A.2 | VOM7 |
| VOM9 | 🟢 | [VMInstanceView pass-through API + ExecuteInstance dual-ref](Plan/Step_VOM9_SoA_SyscallBreak.md)（Phase 1+2+4-minimal；Phase 3 取消；Phase 4-full 移交 VOM-Tail） | 妥协 A.3 | VOM8 |
| VOM10 | 🟢 | [façade 精简 + ModuleVar 清理](Plan/Step_VOM10_Facade_ModuleVar_Cleanup.md)（B.4 / C.2 推迟见 Overview §八 D5/D6） | — | VOM9 |
| VOM11 | 🟢 | [Lazy Rent Reset](Plan/Step_VOM11_LazyRentReset.md) — 妥协 B 消除（B08-equiv 1.29 ns / −85%；F1/F2 −10 ns；P03 alloc=0） | 妥协 B | VOM10 |

---

### 优化区

#### 已完成

- **B-ε**（追平 Lua，4/4）：fixed pin + Compare&Branch fusion + const/DCE + FORLOOP 超级指令
- **B-ζ**（分支优化，3/3）：LICM + CMP-immediate + SWITCH 跳转表
- **B-η**（指令压缩）：16B → 4B（O8-1~3, O8-5 ✅；O8-4 临时妥协 ⏸）
- 共 **19 项优化**已实施，详见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md) 与 [Reference/VM_Optimization_Outlook.md](Reference/VM_Optimization_Outlook.md)

#### 未实施

O11-O14 运行时优化、FO2 尾调用、FO3 小函数内联。无强排期。

---

### 语言服务器区

#### 已完成（DX 系列 + 紧急修复）

| 序号 | 状态 | 主要产出 |
|------|------|---------|
| DX4-P0~P4 | ✅ | LSP workspace + .ffproj 项目文件 + CLI 项目编译 + 跨文件符号 + LSP 辅助创建 |
| DX5~DX9 | ✅ | rename + semanticTokens + Include 重命名 + AST 精确位置 + external func + 染色改进 |
| R1 / E003 / E004 | ✅ | LSP 架构重构（4100→3780 行）+ 紧急修复 + 模块级符号导航 |
| DX10 / DX11 | ✅ | 依赖图 + 全项目诊断 + VFS Rename 状态修复 |
| DX13~DX16 | ✅ | KL-01~05：参数 LSP / Rename 完整性 / Private 跨文件过滤 / 变量作用域隔离 |
| DX17 / DX18 | ✅ | 统一符号解析（ResolvedSymbol）+ 统一引用收集（LspServer −242 行） |
| DX19 | ✅ | ResolveSymbol 候选仲裁修复（include/main 同行列冲突） |

详细计划与讨论文档链接见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)。

#### 待执行 / 阻塞

- **DX12** 后台编译调度 ⚪ debounce + 取消 + 缓存（远期待激活）
- 语言易用性审查 D18 → UC-1~UC-7 已纳入 [Outlook §2.10](Plan/Outlook_And_Risks.md)

---

## 临时妥协区

| 当前妙协 | 理由 | 未来补全路径 |
|----------|------|-------------|
| 开发期 float（`Number`） | 快速迭代，Fix64 调试较痛苦 | 正式测试和上线构建必须 `USE_FIXPOINT` |
| 无编辑器 UI | 编辑器依赖稳定 AST + 真实 Syscall | C6 流程图投影（C2 通过后） |
| 无 Handle64 批处理 | 曳光弹不涉及多目标 | C4（真实多目标业务接入前） |
| Paired Syscall 仅无参反向调用 | 覆盖 80%+ 场景 | 如需带参反向调用扩展配对协议 |
| 函数参数上限 = 16 | 与 Syscall 参数传递一致；转向后 `Arguments` ref struct 仍按此寄存器布局走 | 如需更多参数扩展寄存器布局 |
| O8-4 Peephole 适配 ⏸ | EXTEND_AX 在 Peephole 后运行 | 未来按需消除冗余 |

---

## 展望区

> 详见 [Plan/Outlook_And_Risks.md](Plan/Outlook_And_Risks.md)

### 功能展望

| 分组 | 未完成 | 已完成 |
|------|--------|--------|
| 函数调用 | FF2 回调、FF4 多返回值 | FF3 可选参数 ✅、FF5 非 entry defer ✅ |
| 全局 / 跨步骤 | H1 Handle64、PR1 带参配对、DM1 双轨、B1 Editor DAP | BB1 黑板 Key ✅ |
| 部署架构 (MI) | MI-1~MI-5 | — |
| 结构体 | MSV-F1 const 折叠、MSV-F2 @export struct（暂缓） | S4 ✅、SN1 ✅、SN2 ✅ |
| Include | IA-F1 LSP、VIS-1 默认 private（暂缓） | Lang-15~18 ✅ |
| LSP 架构 | DX12 后台编译（远期） | DX10/DX11/DX13~DX19 全 ✅ |
| 转向落地 | S1-S9（按 [IdealAndGap](Discussion/D_VM_ObjectModel_IdealAndGap.md) 调整点序列） | — |

### 优化展望

已执行 19 项优化（详见 [Reference/VM_Implementation_Record.md](Reference/VM_Implementation_Record.md)）。

未实施：O11-O14 运行时优化、FO2 尾调用、FO3 小函数内联。

### 已识别风险（20 项，全部降至低/极低）

| 分组 | 条目 | 数量 | 降级后等级 |
|------|------|------|-----------|
| 步骤 8 — 已缓解 | R1-R4 | 4 | 极低 |
| 步骤 8 — 前瞻 | R5-R7 | 3 | 低 |
| 步骤 9 | SR1-SR4 | 4 | 低~极低 |
| 全局 | GR1-GR3 | 3 | 低~极低 |
| 外部工具对接 | DR1-DR5 | 5 | 低~极低 |

> 风险降级详细措施见 [Outlook_And_Risks.md §六](Plan/Outlook_And_Risks.md#六风险降级计划目标全部--低--极低)。
> 转向引入的新风险点（CPUData 切分回退面、临时池清理成本、HostBindings 迁移路径、只读校验与内联交互、MethodHandle hot-reload）集中记录于 [D_VM_ObjectModel_IdealAndGap §五](Discussion/D_VM_ObjectModel_IdealAndGap.md)。
