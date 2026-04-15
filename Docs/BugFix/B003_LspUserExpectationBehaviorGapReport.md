# B003: LSP 功能行为偏差诊断报告（用户操作 × 语言特性 × 预期结果）

> **状态**：🔧 修复中
> **来源**：KOF98 脚本实战 + LspServer 行为审查（2026-04-15）
> **目标**：将“能编译”与“编辑器体验正确”对齐，补齐用户视角的行为一致性

---

## 背景

当前 LSP 实现已覆盖定义/引用/悬停/补全/重命名/语义高亮等核心能力，但在以下场景仍存在“用户操作与语言特性组合后，结果不符合直觉”的偏差。

典型业务脚本示例：
- KOF98 站起技能中对 `EndAction()` 的跨文件导航预期（`skill_stand_up.ffs`）
- 碰撞模块中 `Box4`/`HitPhaseDef`/嵌套字段的导航与重命名预期（`skill_collision.ffs`）

---

## 诊断矩阵

| ID | 用户操作 | 语言特性 | 用户预期结果 | 当前偏差 | 严重级别 |
|---|---|---|---|---|---|
| G01 | Find References on include path | include | 返回工作区所有 include 引用 | 仅扫描当前文件 imports | 高 |
| G02 | Definition/Hover/References on `Lib.func()` | include-as alias + 成员调用 | 跳转/悬停到 alias 模块内函数 | LSP 基本不解析 MemberCall 目标符号 | 高 |
| G03 | Dot completion on `p.` where `p: Alias.Struct` | Lang-17 点类型名 | 补全结构体字段 | 类型匹配多处按全等，别名点类型易失效 | 中高 |
| G04 | Go to Definition / Rename on variable | var/const 声明 | 作用到变量名 token | 多处使用 `VarDeclStmt.Column`（关键字列）而非 `NameColumn` | 高 |
| G05 | PrepareRename on variable/parameter | rename 预检 | 返回精确 range | 缺 Variable/Parameter 专用起始列逻辑，fallback 偏移不稳 | 高 |
| G06 | Rename struct field | struct field + 字面量字段 | 仅改同结构字段名 | 字段匹配过宽 + 字面量字段位置不精确 | 高 |
| G07 | Rename module variable with local shadowing | module var + 局部同名 | 仅改模块变量及其引用 | 非 scope-isolated 路径会混入局部同名标识符 | 高 |
| G08 | Completion at module/out-of-function position | 作用域补全 | 不出现不在当前作用域的局部变量/参数 | `FindContainingFunction` 采用“最近声明”近似，可能误归属 | 中 |
| G09 | Hover on `Enum.Member` line | enum member hover | 仅光标在 member token 上触发 | 同行命中逻辑偏宽，可能误触发 member hover | 中 |
| G10 | Diagnostics for include chain | 跨文件诊断归属 | 错误与告警归属策略一致 | errors 过滤 cross-file，warnings 不过滤 | 中 |
| G11 | didChange/didOpen cascade on Windows | include 依赖图 | 被 include 文件改动后稳定级联 | 路径规范不统一（分隔符/大小写）易漏触发 | 中高 |
| G12 | willRenameFiles include replacement | 文件重命名 | 路径匹配遵循平台习惯 | renameMap 使用 Ordinal，大小写敏感 | 中 |
| G13 | Path/URI interop | file URI | 路径含空格/特殊字符时仍可导航 | `PathToFileUri` 不做 escape，和 `FilePathToUri` 不一致 | 中 |
| G14 | JSON-RPC compatibility | 协议 id 类型 | 字符串/数字 id 都兼容 | 请求 id 仅按 int 读取 | 低中 |
| G15 | didClose cleanup | 依赖图生命周期 | 关闭文档后无陈旧依赖边 | 仅删 content/AST，依赖边未同步清理 | 低中 |

---

## 关键根因（按模块）

### 1) 符号识别与位置精度

- 变量声明位置信息在多处未使用 `NameLine/NameColumn`，而是退回 `Line/Column`。
- `prepareRename` 对 Variable/Parameter 没有专门分支，导致 range 依赖不稳定 fallback。
- struct literal 字段名没有独立位置信息，重命名时只能粗粒度定位，易误改。

### 2) 语义模型覆盖不完整

- `MemberCallExpr`（如 `Lib.func()`）在导航/悬停/签名帮助路径缺少完整符号解析。
- include-as 的 alias 模块在编译器语义存在，但 LSP 侧利用不足。
- dotted type（`Alias.Struct`）在补全与类型推断链路中未统一按“基类型 + alias 解析”处理。

### 3) 跨文件行为一致性

- include 引用查询默认仅当前文档，未做工作区扫描。
- 错误与告警跨文件归属策略不一致，用户感知“同类问题行为不同”。
- 依赖图键值规范化不足，Windows 下路径风格差异会导致级联失效。

### 4) 协议与基础设施

- URI 构造和解析策略不完全对称。
- JSON-RPC id 兼容面窄（仅 int）。
- 文档关闭后依赖边残留，可能带来长期噪声与性能问题。

---

## 与当前 KOF98 脚本直接相关的风险

1. `skill_stand_up.ffs` 中 `EndAction()`：
   - 期望可稳定定义跳转到 `common/skill_base.ffs`。
   - 若后续引入 alias/member 调用风格，现有 MemberCall 路径会出现导航缺失。

2. `skill_collision.ffs` 中 `Box4`/`HitPhaseDef`/`phase.box.*`：
   - 字段 rename 与 nested 字段引用收集存在误命中风险。
   - dotted type / alias type 扩展后，dot completion 和 definition 容易退化。

---

## 必要测试覆盖点（新增/强化）

> 建议新增到 `Assets/Scripts/VM/Tests/LspTests.cs`，命名区间使用 `B003-XX`。

### A. 导航与引用（P0）

- B003-01: include path references 跨文件
  - 场景：`a.ffs`、`b.ffs` 同时 `include "common/utils"`
  - 操作：在 `a.ffs` 的 include 路径上 references
  - 断言：返回包含 `a.ffs` 与 `b.ffs` 两处

- B003-02: alias member call definition (`Lib.fn()`)
  - 场景：`include "lib" as Lib`; `Lib.fn()`
  - 操作：definition on `fn`
  - 断言：跳转到 `lib.ffs` 中 `fn` 定义

- B003-03: alias member call hover/signatureHelp
  - 操作：hover/signatureHelp on `Lib.fn(`
  - 断言：返回函数签名与文档

### B. 重命名与 PrepareRename 精度（P0）

- B003-04: variable definition character 精度
  - 操作：definition on local var usage
  - 断言：`range.start.character` 命中变量名列，不是 `var` 关键字列

- B003-05: prepareRename on variable/parameter
  - 操作：prepareRename（声明处 + 使用处）
  - 断言：range 精确覆盖 symbol token（不同光标位置下稳定）

- B003-06: struct field rename with same field name in two structs
  - 场景：`A.x`, `B.x`
  - 操作：rename `A.x`
  - 断言：仅 `A.x` 声明与引用被改；`B.x` 不变

- B003-07: struct literal field rename position
  - 操作：rename field used in literal `Type { x: ... }`
  - 断言：修改字段名 `x` token，不修改 `Type` token

- B003-08: module var rename with local shadowing
  - 场景：module `speed` + function local `speed`
  - 操作：rename module `speed`
  - 断言：仅 module declaration/refs 改动，local 不受影响

### C. 补全语义一致性（P1）

- B003-09: completion at module scope should not include function-local vars
  - 操作：在函数外空行触发 completion
  - 断言：无参数/局部变量项

- B003-10: dot completion with dotted type `Alias.Struct`
  - 场景：`var p: Alias.Struct = ...`; `p.`
  - 断言：字段列表完整

### D. 悬停与诊断一致性（P1）

- B003-11: enum member hover token-boundary
  - 操作：同一行在 `Enum.Member` 的非 token 区域/enum 名/member 名分别 hover
  - 断言：仅对应 token 触发预期 hover

- B003-12: cross-file warnings policy parity
  - 场景：include 链中的 warning 发生在被 include 文件
  - 断言：warning 归属策略与 error 一致（按设计约定）

### E. 跨平台与协议稳健性（P1/P2）

- B003-13: dependency graph path normalization on Windows
  - 场景：didChange + didChangeWatchedFiles 混合触发
  - 断言：依赖级联稳定触发，不受斜杠风格影响

- B003-14: willRenameFiles case-insensitive mapping
  - 场景：include 文本大小写与实际文件名大小写不同
  - 断言：仍生成正确 edit

- B003-15: PathToFileUri escaping
  - 场景：路径包含空格、`#`、`%`
  - 断言：definition/rename/edit URI 可被客户端正确解析

- B003-16: JSON-RPC string id compatibility
  - 场景：请求 id 为字符串
  - 断言：响应 id 保持同类型且匹配

- B003-17: didClose dependency edge cleanup
  - 场景：open -> include -> close -> watched change
  - 断言：不再尝试对已关闭文档做依赖级联编译

---

## 修复优先级建议

- P0（立即）：G01, G02, G04, G05, G06, G07
- P1（本迭代）：G03, G08, G10, G11, G12, G13
- P2（稳定性收尾）：G09, G14, G15

---

## 结论

当前问题已从“单点 bug”进入“行为一致性”阶段：
- 编译能力多数可用，但编辑器体验在 alias/member call、位置精度、跨文件一致性上仍有断层。
- 建议按本报告测试点先补回归网，再按 P0→P1 顺序修复，避免修复过程中引入新回归。
