# B-γ7 SN1 嵌套结构体（递归拍平为连续寄存器）

> **前置**：Step 9 (S1-S3) 单层结构体拍平 ✅ + B-γ5 (S4) 结构体函数参数 ✅
> **目标**：struct 字段类型允许为另一个 struct，编译期递归拍平为连续寄存器

---

## 一、设计决策

### 1.1 拍平策略

嵌套结构体在编译期**完全递归拍平**为连续标量寄存器，运行时无任何嵌套结构概念。

```
struct Vec2 { x: int, y: int }          // 2 registers
struct Rect { min: Vec2, max: Vec2 }    // 4 registers: min.x, min.y, max.x, max.y
struct Scene { r: Rect, id: int }       // 5 registers: r.min.x, r.min.y, r.max.x, r.max.y, id
```

### 1.2 核心不变量

- **拍平后全为标量寄存器**：与架构规则 §6（寄存器禁持动态/托管/不定长结构）一致
- **编译期完成**：运行时零开销，无间接寻址
- **循环引用检测**：struct A { f: B } + struct B { f: A } → 编译错误

### 1.3 妥协点

| 妥协 | 原因 | 消除时间点 |
|------|------|-----------|
| 不支持嵌套 struct 作为函数返回值 | 当前 r0 单寄存器返回约定 | FF4 多返回值排期时 |
| 不支持 `var r: Rect = otherRect` 右侧为函数调用 | 函数返回值只有 r0 | FF4 |
| LSP dot-completion 暂不支持 `a.inner.` 两级补全 | LSP 补全解析基于文本前缀 | 后续 LSP 增强（永久妥协可能性低） |

---

## 二、实施清单

### SN1-1: 编译期递归拍平 struct type table ✅

- [x] `BuildFlatStructInfo()` + `FlattenStruct()` 方法：遍历 `_structTypes`，递归展开嵌套 struct
  - `flatFieldCount`：递归展开后的总标量字段数
  - `flatFields[]`：`(dotPath, offset)` 数组，dotPath 用 `"inner.x"` 格式
- [x] 循环引用检测：visiting set，检测到环报 `"Circular struct reference detected"` 编译错误
- [x] 存储为 `_flatStructInfo: Dictionary<string, FlatStructInfo>`
- [x] 辅助方法：`GetFlatFieldCount()`, `ResolveFlatFieldOffset()`, `GetSubFieldFlatCount()`, `GetFieldStructType()`, `ResolveFieldChainType()`

### SN1-2: DeclareStructVar 使用 flatFieldCount ✅

- [x] `DeclareStructVar(name, flatFieldCount)` 替代 `structDecl.Fields.Count`·
- [x] `_structVarTypes` 不变（仍映射 varName → typeName）

### SN1-3: ResolveFieldAccess 递归解析 ✅

- [x] `CollectFieldChain()` 收集 `FieldAccessExpr` 链为 `(varName, dotPath)`
- [x] 通过 `ResolveFlatFieldOffset()` 查找 `dotPath` 在 flat struct 中的偏移
- [x] 支持任意深度嵌套：`a.inner.x`、`s.bounds.min.x` 等

### SN1-4: CompileVarDecl 适配 ✅

- [x] struct 变量初始化使用 flatFieldCount
- [x] struct-to-struct 赋值使用 flatFieldCount 做 N×MOVE
- [x] 默认初始化使用 flatFieldCount 做 N×LOAD_CONST
- [x] DBG2 SymbolEntry.FieldNames 使用拍平后的 dotPath 名称

### SN1-5: 整体赋值（AssignExpr）适配 ✅

- [x] `a = b`（两个嵌套 struct 变量）使用 flatFieldCount 做 N×MOVE
- [x] 子 struct 字段赋值：`a.min = b.min` → 检测两侧 ResolveFieldChainType 匹配同类型 → flatFieldCount × MOVE
- [x] 子 struct 赋值也支持 `a.min = localVec2Var` 形式

### SN1-6: S4 函数参数传递适配 ✅

- [x] 参数绑定（CompileFunction）使用 flatFieldCount
- [x] EmitUserCall scratch zone 展开使用 flatFieldCount
- [x] R5 scratch 限制检查使用 flatFieldCount
- [x] 生命周期分析 FieldCount 使用 flatFieldCount

### SN1-7: DBG2 符号表适配 ✅

- [x] SymbolEntry.FieldNames 使用拍平后的 `["min.x", "min.y", ...]` dotPath 名称
- [x] FieldCount 使用 flatFieldCount

### SN1-8: LSP dot-completion 嵌套支持 ✅

- [x] 补全前缀提取支持 dot-chain（`a.inner.` → dotPrefix="a.inner"）
- [x] 按 dot-chain 逐层解析 struct 类型，最终获取叶层 struct 的字段列表
- [x] 支持任意深度嵌套补全

### SN1-9: 测试 CS22-CS30 ✅（新增 34 项 Assert）

- [x] CS22: 基础嵌套 struct 声明 + 字段访问（Rect.min.x/y）
- [x] CS23: 嵌套 struct 初始化赋值（var b: Rect = a）
- [x] CS24: 嵌套 struct 作为函数参数（area(r: Rect)）
- [x] CS25: 三层嵌套 struct（Scene.bounds.min.x）
- [x] CS26: 循环引用检测（A→B→A → 编译错误）
- [x] CS27: 嵌套 struct 字段在表达式中使用（r.min.x + r.max.y）
- [x] CS28: 嵌套 struct + wait + 快照回滚正确性
- [x] CS29: 嵌套 struct 整体赋值（b = a）
- [x] CS30: 子 struct 字段赋值（b.min = a.min）

---

## 三、功能展望

| ID | 内容 | 触发条件 |
|----|------|---------|
| SN1-F1 | 嵌套 struct 作为函数返回值 | FF4 多返回值排期时 |
| SN1-F2 | struct 字面量构造语法 `var r: Rect = { min: { x: 1, y: 2 }, max: { x: 3, y: 4 } }` | SN2 排期时 |
| SN1-F3 | 嵌套 struct 的 COPY_BLOCK 优化（单条指令替代 N×MOVE） | SO1 排期时 |

## 四、优化展望

| ID | 内容 | 预期收益 |
|----|------|---------|
| SN1-O1 | `FlatFieldEntry` 查找从线性扫描改为 Dictionary 缓存 | struct 字段数极多时有意义，当前规模不需要 |
| SN1-O2 | 子 struct 赋值自动升级为 COPY_BLOCK（依赖 SO1） | 大 struct 赋值指令数从 N 降为 1 |

## 五、风险点

| 等级 | 风险 | 缓解 |
|------|------|------|
| 低 | 深度嵌套（>5 层）导致寄存器膨胀 | FO7 静态调用深度分析已有累计寄存器窗口溢出检测 |
| 低 | LSP 补全 dot-chain 解析在极端嵌套下性能退化 | 当前 struct 数量 <20，O(N²) 可忽略 |
| 已消除 | 循环引用导致编译器死循环 | CS26 测试验证 visiting set 检测正确 |
