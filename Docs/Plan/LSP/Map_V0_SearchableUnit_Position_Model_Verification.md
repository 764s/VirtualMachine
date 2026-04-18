# V0: 可被搜索单元 × 位置模型正确性与完整性验证

> 目标：在推进 T1 + T2 之前，先验证“可被搜索单元 × 位置”模型是否正确、是否完整。
> 范围：模型验证（语义目标层），不等同于实现已覆盖。

## 一、验证结论（摘要）

- 结论 V0-1（模型正确性）：通过。
- 结论 V0-2（模型完整性）：通过（已补齐 V1 缺项并形成 V2 基线）。
- 结论 V0-3（实现覆盖度）：未通过（当前新数据库事实提取仅覆盖函数定义/函数调用引用，尚未覆盖全部单元）。

解释：

- 本次已确认“目标模型”完整可用，可作为 T1/T2 及后续测试设计的唯一语义基线。
- 但“实现现状”仍有明显覆盖缺口，需在后续工程阶段收敛。

## 二、事实来源（代码证据）

- 符号种类基线：SymbolKindTag 定义了 Function / Variable / Struct / Parameter / Enum /
  IncludeFile / StructField / EnumMember。
  - 证据：Assets/Scripts/VM/Debug/Lsp/Database/Contracts/SymbolKindTag.cs
- AST 提供了精确位置承载：NameLine/NameColumn、TypeNameLine/TypeNameColumn、FieldNameLine/FieldNameColumn、ExternalLine/ExternalColumn。
  - 证据：Assets/Scripts/VM/AST/ASTNode.cs
- 语法支持 include as 与 alias override（AliasTarget）、external func、结构体字段访问与成员调用。
  - 证据：Assets/Scripts/VM/Compiler/Parser.cs
- 查询层将 IncludeFile、Parameter、StructField、EnumMember 纳入查询/重命名语义。
  - 证据：Assets/Scripts/VM/Debug/Lsp/Database/Query/InMemoryLspQueryFacade.cs
- 旧 LSP 引用收集覆盖 include、类型注解、字段访问、枚举成员等位置类型。
  - 证据：Assets/Scripts/VM/Debug/LspServer.cs
- 新数据库事实提取当前仅构建函数定义与 CallExpr 引用。
  - 证据：Assets/Scripts/VM/Debug/Lsp/Database/Operations/InMemoryDatabaseExecutionOrchestrator.cs

## 三、V2 模型（可被搜索单元 × 位置）

标记约定：

- M = 必须覆盖
- O = 可选覆盖（按语义能力开关）
- - = 不适用

位置维度：

- L1：include 路径字面量
- L2：类型/结构体/枚举定义体
- L3：external 声明头（external 关键词 + external func 声明）
- L4：模块声明位（模块 var/const、顶层声明）
- L5：函数签名位（参数名、参数类型、返回类型）
- L6：函数体声明位（局部 var 声明）
- L7：右值表达式
- L8：右值嵌套区域

| 可搜索单元 | L1 | L2 | L3 | L4 | L5 | L6 | L7 | L8 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| IncludeFile | M | - | - | M | - | - | - | - |
| Function | - | - | O | M | O | - | M | M |
| ExternalFunction（Function 子类） | - | - | M | O | M | - | M | M |
| Variable（Module） | - | - | - | M | - | - | M | M |
| Variable（Local） | - | - | - | - | - | M | M | M |
| Parameter | - | - | - | - | M | - | M | M |
| StructType | - | M | O | M | M | M | M | M |
| StructField | - | M | - | O | - | O | M | M |
| EnumType | - | M | O | M | M | M | M | M |
| EnumMember | - | M | - | O | - | O | M | M |

说明：

- external func 在模型中是 Function 的语义子类，不应与 Function 完全割裂。
- IncludeFile 是一等单元，不能仅作为 include 机制细节。

## 四、本次发现并修正的模型差异

- G-01：V1 矩阵缺失 IncludeFile、Parameter、局部变量维度。
  - 处理：已在 DX20 高层矩阵补齐。
- G-02：V1 将 external func 隐式当作独立 kind，边界不清。
  - 处理：修正为 Function 子类语义。
- G-03：DX20 中测试地图路径仍指向旧地址 Plan/LSP。
  - 处理：改为 Docs/Plan/LSP。
- G-04：函数返回类型当前缺少独立行列位置信息（只有 ReturnType 字符串）。
  - 处理建议：后续补 ReturnTypeLine/ReturnTypeColumn，避免返回类型位置断言退化。
- G-05：新数据库事实提取覆盖面过窄（函数定义 + CallExpr 引用）。
  - 处理建议：按 V2 单元扩展事实提取器，再落测试代码。

## 五、T1 + T2 前置门槛（Gate）

- [x] VG-01：模型单元集合已完整定义（V2）。
- [x] VG-02：位置维度已完整定义（L1-L8）。
- [x] VG-03：DX20 与测试地图路径已统一到 Docs/Plan/LSP。
- [ ] VG-04：返回类型精确位置元数据补齐（G-04）。
- [ ] VG-05：新数据库事实提取覆盖扩展到 V2 所需核心单元（G-05）。

执行策略：

- 允许推进 T1/T2 的“测试计划与用例设计”。
- 暂不推进依赖 VG-04/VG-05 的“细粒度实现断言代码”，避免伪失败。

## 六、与现有计划文件的关系

- 主计划：Docs/Plan/LSP/Step_DX20_GlobalWorkspace_FindAllReferences_Checklist.md
- T1/T2 测试地图：Docs/Plan/LSP/Map_T1_T2_TestCompleteness.md
- 本文档职责：给主计划与测试地图提供统一模型基线，不承载具体测试代码。
