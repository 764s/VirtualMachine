# Lang-11: 模块级 struct const/var 直接初始化

> **状态**：✅ 完成
> **来源**：Lang-11 — 模块级 struct const 直接初始化（方案 A）
> **日期**：2026-04-10

---

## 一、背景

Lang-1 引入模块变量（`var`/`const` 顶层声明），但仅支持标量 `Number` 初始化。KOF 碰撞框配置（`HitPhaseDef`、`Box4` 等 struct 类型）只能在函数内逐字段赋值，无法提升为模块级声明。

Lang-11 使编译器支持模块级 `var`/`const` 声明 struct 类型并以 struct literal 初始化。

---

## 二、设计方案（方案 A：编译期寄存器展开）

编译器在 `ProcessModuleVariables` 阶段识别 struct literal 初始化器：
1. 获取 struct 拍平字段数（`FlatFieldCount`，递归处理嵌套 struct）
2. 分配 N 个连续模块变量寄存器（固定区 r56+ 或溢出到扩展寄存器）
3. `TryFoldStructLiteral` 递归折叠所有标量字段到编译期常量
4. `EmitModuleVarInit` 展开为逐字段 `LOAD_CONST` + `STORE_MVAR`

**纯编译期特性，零运行时改动。**

### 语法支持

```ffs
struct Vec2 { x: float, y: float }
struct Rect { min: Vec2, max: Vec2 }

// var struct — 可读写
var pos: Vec2 = Vec2 { x: 10, y: 20 }

// const struct — 只读，阻止整体赋值和字段赋值
const origin: Vec2 = Vec2 { x: 0, y: 0 }

// 嵌套 struct
var bounds: Rect = Rect { min: Vec2 { x: 1, y: 2 }, max: Vec2 { x: 3, y: 4 } }

// 无初始化器 → 默认零值
var velocity: Vec2

// 常量表达式字段值
const OFFSET: int = 5
var adjusted: Vec2 = Vec2 { x: OFFSET * 2, y: OFFSET + 3 }
```

---

## 三、实现细节

### 3.1 编译器改动

| 组件 | 改动 | 文件 |
|------|------|------|
| `ProcessModuleVariables` | struct 类型检测 → 委托 `ProcessModuleStructVar` | BytecodeCompiler.cs |
| `ProcessModuleStructVar` | N 连续寄存器分配 + const 标记 + struct literal 折叠 | BytecodeCompiler.cs |
| `TryFoldStructLiteral` | 递归折叠 struct literal 字段 → `_moduleVarInitValues` | BytecodeCompiler.cs |
| `_moduleStructVarTypes` | 新字段：模块 struct 变量名→类型名映射 | BytecodeCompiler.cs |
| 函数初始化 | 预填充 `_structVarTypes` from `_moduleStructVarTypes` | BytecodeCompiler.cs |
| `CompileExpr` (IdentifierExpr) | 模块 struct 变量不做单字段物化，返回 base register | BytecodeCompiler.cs |
| `EmitUserCall` | 模块 struct 变量作函数参数：`EmitLoadModuleVar` per field | BytecodeCompiler.cs |
| const 赋值防护 | 整体赋值 + 字段赋值检查 `_moduleConstVarNames` | BytecodeCompiler.cs |
| `@export` 限制 | struct 模块变量不支持 @export（单槽位 ExportVarEntry 限制） | BytecodeCompiler.cs |

### 3.2 运行时改动

无。纯编译期特性。

---

## 四、测试覆盖

| 测试 | 场景 | 验证点 |
|------|------|--------|
| MSV01 | var struct + literal init + field read | 字段正确初始化 + 表达式计算 |
| MSV02 | const struct + literal init + field read | const struct 字段正确读取 |
| MSV03 | const struct 整体赋值 | 编译错误：Cannot assign to 'const' struct |
| MSV04 | const struct 字段赋值 | 编译错误：Cannot assign to field of 'const' struct |
| MSV05 | var struct 字段写入 | 写入后字段值更新，其他字段不变 |
| MSV06 | 嵌套 struct (Rect → Vec2) | 4 层嵌套字段正确读取 |
| MSV07 | 跨函数共享 | 函数修改模块 struct var，调用方可见 |
| MSV08 | 无初始化器 (var) | 默认零值 |
| MSV09 | 无初始化器 (const) | 编译错误：requires an initializer |
| MSV10 | 非 literal 初始化器 | 编译错误：must be a struct literal |
| MSV11 | 常量表达式字段值 | `OFFSET * 2` / `OFFSET + 3` 正确折叠 |
| MSV12 | var struct 整体赋值 | struct literal 重新赋值 |
| MSV13 | @export struct | 编译错误：@export not supported |
| MSV14 | 模块 struct var 作函数参数 | EmitLoadModuleVar per field → 正确传参 |
| MSV15 | 混合标量 + struct 模块变量 | 标量 var/const + struct var 共存 |

**55 个新 Assert。1365 测试总计。B01-B06 benchmark 无回归。**

---

## 五、妥协点

| 妥协 | 原因 | 消除时间点 |
|------|------|-----------|
| `@export` 不支持 struct 模块变量 | `ExportVarEntry` 为单槽位设计，struct 需多槽位元数据 | 当跨实例 struct 变量访问（XLOAD_MVAR multi-field）需求出现时 |
| const struct 不做常量折叠 | const struct 使用寄存器而非纯编译期常量 | Lang-9 深度内联可优化（const struct 字段 → 内联为 LOAD_CONST） |

---

## 六、功能展望

| ID | 内容 | 触发时机 | 暂缓理由 |
|----|------|----------|----------|
| MSV-F1 | const struct 字段编译期折叠 | Lang-9 深度内联基础设施就绪后，const struct 字段访问可内联为 LOAD_CONST | 暂缓 — 可行性高（数据已在 `_moduleVarInitValues` 中，~15 行改动），但收益低（LOAD_MVAR 与 LOAD_CONST 均为 1 条 O(1) 指令，无热路径瓶颈）。可做不急，保持展望 |
| MSV-F2 | @export struct 多槽位导出 | 跨实例 struct 变量访问需求出现时 | 暂缓 — 可行性中（需改 ExportVarEntry + XLOAD_MVAR 运行时，~80-120 行），但无实际使用场景。多个标量 @export 可完全替代。不推荐主动实施 |

---

## 七、风险点

| 风险 | 影响 | 缓解 |
|------|------|------|
| 模块变量寄存器耗尽 | struct 占多个槽位（如 Rect 占 4 个），8 个固定槽位可能不够 | Lang-1.1b 扩展寄存器自动溢出，已验证 |
| 嵌套 struct 深度 | 深度嵌套导致字段数爆炸 | 循环引用已检测（SN1），实际 KOF 场景嵌套≤2 层 |

---

## 八、优化展望

const struct 当前使用寄存器存储字段值（运行时从模块变量区加载）。理论上，const struct 的字段值全部已知（编译期常量），可以：
- 在 `CompileExpr` 的 `FieldAccessExpr` 路径，如果根变量是 const struct，直接折叠为 `LOAD_CONST`（绕过 `LOAD_MVAR`）
- 无需寄存器分配（与标量 const 一致）

此优化在 Lang-9 深度内联基础设施中可自然实现（内联展开时 const 字段替换为字面量）。
