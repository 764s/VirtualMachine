# T1 + T2 测试完整性地图（实现前）

> 目的：在推进 T1（文件收集）+ T2（内联/include as/别名）之前，先定义“完整无遗漏”的测试覆盖地图。
> 原则：本文件只定义覆盖目标与判定口径，不承载具体测试代码与夹具细节。

## 零、前置 Gate（先验证模型）

- Gate 文件：Docs/Plan/LSP/Map_V0_SearchableUnit_Position_Model_Verification.md
- Gate 目标：先确认“可被搜索单元 × 位置”模型正确且完整，再推进 T1/T2 测试实现。
- Gate 状态：
  - [x] G0-1 模型正确性已验证（V2 基线已确立）
  - [x] G0-2 模型完整性已验证（缺项已识别并补齐）
  - [ ] G0-3 关键实现缺口（VG-04/VG-05）收敛后，再启用细粒度断言

## 一、范围与边界

- In Scope:
  - T1 文件收集：工作区发现、include 图收集、路径归一化、增量更新收敛。
  - T2 语义模式：普通 include、include as、别名 override、符号身份一致性。
- Out of Scope:
  - T3 结构化语义细节（例如嵌套字段链 a.b.c 的完整行为）
  - 非 T1/T2 的性能优化实现细节

## 二、完整性维度（必须全覆盖）

- D0 模型基线一致性：所有测试场景均可映射到 V0 文档中的 V2 单元与位置维度。
- D1 文件生命周期：初始化扫描、created、changed、deleted、renamed。
- D2 来源优先级：open buffer、watcher、磁盘基线三者一致性。
- D3 include 拓扑：直接链、传递链、菱形、循环、缺失依赖。
- D4 include 形态：普通 include、include as Alias、override Alias.Name。
- D5 路径形态：相对路径、扩展名补全、大小写差异、路径分隔符差异。
- D6 查询形态：includeDeclaration=true/false、定义点发起、引用点发起。
- D7 符号类别：IncludeFile、函数、external 函数（函数子类）、模块变量（var/const）、局部变量、参数、结构体类型、枚举类型、枚举成员。
- D8 质量门槛：去重、稳定排序、失效收敛、错误隔离（不污染其他结果）。

## 三、T1 测试地图（文件收集）

### 3.1 工作区发现与过滤

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T1-WS-01 | 初始化扫描未打开 .ffs | 不需要 didOpen 即可查询命中 | P0 |
| T1-WS-02 | 扫描目录过滤 | Library/Temp/obj/bin 不入库 | P0 |
| T1-WS-03 | 扫描顺序稳定 | 同输入扫描输出顺序一致 | P1 |
| T1-WS-04 | 大工作区分批 | 不漏文件且无明显卡死 | P1 |

### 3.2 include 图收集

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T1-IG-01 | 直接 include A->B | A 查询可命中 B 声明/引用 | P0 |
| T1-IG-02 | 传递 include A->B->C | A/B 查询都能关联 C | P0 |
| T1-IG-03 | 菱形 include A->B,C->D | D 相关结果不重复 | P0 |
| T1-IG-04 | 循环 include | 有诊断，服务不崩溃，不死循环 | P1 |
| T1-IG-05 | include 缺失文件 | 有诊断，且不污染其他文件结果 | P1 |

### 3.3 路径归一化

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T1-PN-01 | 相对路径解析 | 归一到同一文档标识 | P0 |
| T1-PN-02 | 自动补 .ffs | include "x/y" 与 "x/y.ffs" 等价 | P0 |
| T1-PN-03 | 大小写差异（Windows） | 同一文件不重复建索引 | P0 |
| T1-PN-04 | / 与 \\ 分隔符差异 | 归一后查询结果一致 | P1 |

### 3.4 增量更新收敛

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T1-UP-01 | watcher created | 新文件参与查询且可命中 | P0 |
| T1-UP-02 | watcher changed | 旧结果被替换，不残留 | P0 |
| T1-UP-03 | watcher deleted | 相关结果及时消失 | P0 |
| T1-UP-04 | watcher renamed | 新路径生效，旧路径失效 | P1 |
| T1-UP-05 | open buffer 与 watcher 冲突 | open buffer 优先，不抖动 | P0 |
| T1-UP-06 | 批量连续变更 | 最终状态一致，过程不崩溃 | P1 |

## 四、T2 测试地图（内联/include as/别名）

### 4.1 普通 include（内联可见域）

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T2-IN-01 | include 后符号可见 | 使用方能命中被 include 声明 | P0 |
| T2-IN-02 | include 声明自身引用 | include 路径声明与使用可追踪 | P1 |
| T2-IN-03 | 重复 include 同模块 | 结果去重且身份不重复 | P0 |

### 4.2 include as Alias

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T2-AL-01 | Alias 命名空间访问 | C.Name 绑定到 Alias 模块内符号 | P0 |
| T2-AL-02 | Alias 与全局同名冲突 | 不串符号，冲突可诊断 | P0 |
| T2-AL-03 | Duplicate Alias | 重名 alias 报错且不污染其他绑定 | P1 |
| T2-AL-04 | Alias 传递依赖 | 别名模块内传递 include 可正确解析 | P1 |

### 4.3 override Alias.Name

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T2-OV-01 | 合法 alias override | 查询命中替换后有效声明 | P0 |
| T2-OV-02 | 非法 alias override | 报错且不污染既有声明关系 | P0 |
| T2-OV-03 | override 后 references 稳定 | includeDeclaration 两种模式结果可预测 | P1 |

### 4.4 符号身份一致性

| ID | 场景 | 关键断言 | 优先级 |
| --- | --- | --- | --- |
| T2-ID-01 | definition 与 references 一致 | 同一符号 identity 不漂移 | P0 |
| T2-ID-02 | rename 与 references 一致 | rename 编辑集合与 references 对齐 | P0 |
| T2-ID-03 | 同名异类符号并存 | kind + scope + parent 稳定仲裁 | P0 |
| T2-ID-04 | 结果排序稳定 | 同输入多次查询顺序一致 | P1 |

## 五、T1 + T2 交叉覆盖矩阵（防漏检）

- 判定规则：每个单元格至少有 1 个正向样例；高风险单元格还需 1 个负向样例。

| 查询对象 \ include 形态 | 普通 include | include as Alias | override Alias.Name |
| --- | --- | --- | --- |
| Include 文件路径 | 必测 | 必测 | 不适用 |
| 函数 | 必测 | 必测 | 必测 |
| 模块变量（var/const） | 必测 | 必测 | 必测 |
| 局部变量 | 必测 | 必测 | 必测 |
| 参数 | 必测 | 必测 | 必测 |
| 结构体类型 | 必测 | 必测 | 必测 |
| 枚举类型 | 必测 | 必测 | 必测 |
| 枚举成员 | 必测 | 必测 | 可选（如语义支持成员级覆盖则改必测） |
| external 函数 | 必测 | 必测 | 可选（按语义是否允许 override） |

## 六、质量与回归门槛

- G-01：每个 P0 场景至少有 1 条自动化回归用例。
- G-02：每个维度 D1-D8 都至少映射到 1 个 P0/P1 场景。
- G-03：每次实现迭代必须回归：去重、排序稳定、失效收敛三项。
- G-04：错误场景必须验证“隔离性”：局部错误不影响无关符号查询。
- G-05：T1/T2 完成后再进入 T3 细化测试脚本设计。

## 七、执行顺序建议（仅目标级）

1. 先完成 Gate-0（V0 模型验证）并锁定 V2 基线。
2. 再完成 T1-WS + T1-IG + T1-UP 的 P0 场景。
3. 然后完成 T2-IN + T2-AL + T2-OV 的 P0 场景。
4. 最后补齐 T1/T2 的 P1 场景与交叉矩阵空洞。

## 八、状态跟踪（更新）

- T1：in-progress（P0 核心场景持续落地，已补齐 T1-UP-01/02/03）
- T2：not-started
- 交叉矩阵空洞：未扫描
- 自动化回归覆盖率：T1 P0 持续扩展（见“九”）
- T1-UP 待补：T1-UP-04（renamed）、T1-UP-05（open buffer 冲突扩展）、T1-UP-06（批量连续变更）

## 九、T1 交叉测试落地（本轮）

- [x] T1-IG-02（传递链）
  - LSPNEW-16A：同轮验证 C->B 与 B->A 两段 references 可查询且顺序稳定。
- [x] T1-IG-03（菱形去重）
  - LSPNEW-16B：共享依赖场景 references 去重与稳定排序。
- [x] T1-PN-02（扩展名补全）
  - LSPNEW-16D：`include "modules/lib"` 命中 `modules/lib.ffs` 并返回跨文件引用。
- [x] T1-UP-01（watcher created）
  - LSPNEW-17：创建后新文件符号可被查询，definition/completion 收敛到新状态。
- [x] T1-UP-02（watcher changed）
  - LSPNEW-18：变更后符号集合替换生效，不保留旧 completion 标签。
- [x] T1-UP-03（watcher deleted）
  - LSPNEW-19：删除后旧符号解析失效，且无关文档查询保持可用。

已有相关护栏复用：

- LSPNEW-12A：直接 include 跨文件 references。
- LSPNEW-12D：工作区扫描目录过滤（Library 排除）。
- LSPNEW-12E：open buffer 与 watcher changed 优先级。
