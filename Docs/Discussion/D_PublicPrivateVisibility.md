# Include 可见性：public / private 修饰符

> **状态**：✅ 已完成讨论 → [Step_Lang15_PublicPrivateVisibility.md](../Plan/Step_Lang15_PublicPrivateVisibility.md)
> **来源**：Lang-15 — include mixin 名称隔离
> **日期**：2026-04-11

---

## 一、背景

Lang-2 引入了 `include` 预处理器，实现了文件间的声明合并（mixin 语义）。当前所有被 include 的声明（func/var/const/struct/enum）都进入同一个平坦命名空间，命名冲突通过"跨文件覆盖允许、同文件重定义报错"的规则处理。

这种平坦合并在脚本规模增大后带来两个问题：

1. **无意冲突**：被 include 的文件中的辅助函数/变量与主文件命名碰撞，导致意外覆盖。
2. **缺少封装**：include 文件的所有内部实现细节暴露给使用方，没有"只导出 API"的能力。

---

## 二、设计目标

引入 `public` / `private` 可见性修饰符，目的是：

- **减少 include 合并时的命名冲突**（主要目的）
- **顺带保留一点私有功能**（次要收益）
- **不影响 mixin 的实际运行语义**（关键约束）

---

## 三、张力讨论与决策

### 张力 1：public/private 与 @export 的关系

**问题**：public/private 是 include（编译期名称合并）的可见性系统。@export 是 VM 服务脚本的运行时外部接口系统。两者应如何关联？

**选项**：
- A. 隐含关联：@export 自动意味着 public
- B. 完全隔离：两套系统完全正交，互不关联

**决策**：**选择 B — 完全隔离**。

**理由**：不希望用户认知上让这两个系统关联，以免产生不必要的区分精力消耗。两轴完全正交，所有 4 种组合均合法：

| 组合 | include 可见 | VM 外部可见 | 场景 |
|------|------------|-----------|------|
| `private`（默认，无 @export） | ❌ | ❌ | 纯内部辅助 |
| `public`（无 @export） | ✅ | ❌ | include 可混入，VM 外不可见 |
| `private` + `@export` | ❌ | ✅ | 服务 API，但 include 方不可见 |
| `public` + `@export` | ✅ | ✅ | 既可混入又对 VM 外暴露 |

### 张力 2：服务脚本的标识方式

**问题**：是否需要额外的 `@service` 标记来标识服务脚本？

**决策**：**赞同隐式识别**。含 `@export` 声明的模块即为服务脚本，无需额外标识。

### 张力 3：private 对 mixin 运行语义的影响

**问题**：private 符号在 include 合并后应如何处理？

**决策**：**private 不影响 mixin 的实际运行语义**。

**核心原则**：
- public/private 的**主要目的是处理混入冲突，减少冲突情况**
- **不影响混入的实际语义**：private 声明实际在当前脚本运行（编译执行），但不会因命名相同产生冲突，也无法被 includer 正常获取
- 这是**名称隔离**，不是访问控制

**具体行为**：
- ✅ private 函数/变量被编译到最终模块中（运行时存在）
- ✅ 同文件内的 public 函数可以调用同文件的 private 函数
- ❌ includer 的代码无法按名称引用被 include 文件的 private 符号
- ❌ 不同文件的同名 private 符号互不冲突（各自独立存在）

---

## 四、语法设计

```ffs
// 默认 = public（向后兼容，Phase A 期间）
func api() { ... }              // public（当前默认行为不变）
var shared = 0                  // public

// 显式 private
private func helper() { ... }  // 仅本文件可见
private var counter = 0         // 仅本文件可见
private const LIMIT = 100       // 仅本文件可见

// 显式 public（Phase A 可选标注，Phase B 后建议标注）
public func api() { ... }      // include 方可见
public var shared_state = 0     // include 方可见

// @export 与 public/private 正交
@export func service_call() { ... }           // 对 VM 外可见 + public（默认）
@export private func isolated_api() { ... }   // 对 VM 外可见，include 方不可见

// struct / enum 同理
private struct InternalData { a: int }
public enum Direction { Left, Right }
```

**默认可见性**：Phase A 默认 public（向后兼容），未来 Phase B 可考虑切换默认为 private（breaking change，需迁移窗口）。

---

## 五、实现方案：origin-aware lookup

### 方案选择

| 方案 | 描述 | 优势 | 劣势 |
|------|------|------|------|
| A: name mangling | 对 private 符号做 `__file__name` 改写 | 概念简单 | AST 改写复杂、报错显示乱码 |
| **B: origin-aware lookup** | 每个声明记录 OriginFile，编译器按 origin 过滤 | **无改名、错误消息干净、调试友好** | 编译器需 origin-aware 查找 |
| C: per-file scope | private 不进入 merged ModuleNode | 最干净隔离 | 架构改动最大 |

**选择方案 B**：origin-aware lookup。

### 核心思路

**Preprocessor 改动**：
1. `FuncDecl` / `VarDeclStmt` / `StructDecl` / `EnumDecl` 利用已有 AST Line/Column 信息 + 新增 `OriginFile` 属性
2. private 符号正常参与合并（进入 target 列表）
3. 冲突规则调整：
   - private vs private（不同文件）：均保留，不冲突
   - private vs public（不同文件）：均保留，不冲突
   - public vs public（不同文件）：跨文件覆盖（现有规则不变）

**编译器改动**：
1. 符号查找时：如果符号是 private 且 OriginFile ≠ 当前编译上下文的源文件 → 跳过
2. 同名 private 函数来自不同文件时，使用内部 qualified name `(OriginFile, Name)` 区分

### 关键挑战：同名 private 函数并存

当多个 include 文件都有 `private func helper()` 时，合并后需保留多个同名 FuncDecl。突破当前"函数名唯一"假设。

解决方式：编译器内部用 `(OriginFile, Name)` 作为函数表 key，对用户展示仍用原始 Name。

---

## 六、向后兼容与迁移

默认从 public 切换到 private 是 breaking change。采用两阶段迁移：

| 阶段 | 行为 | 目的 |
|------|------|------|
| **Phase A**（Lang-15 本期） | 新增 `private` 关键字 + `public` 关键字。未标注的符号仍按 public 处理（向后兼容零 breaking） | 引入语法，给用户迁移窗口 |
| Phase B（未来可选） | 默认改为 private。未标注 = private | 正式生效新默认（需 breaking change 评估） |

**Lang-15 Phase A 的核心价值**：用户可以立即使用 `private` 标记内部实现，减少 include 冲突，同时不影响任何现有代码。

---

## 七、与 Lang-16（include-as alias）的关系

未来若实现 `include "path" as alias`（命名空间别名），`public/private` 仍然有用：
- `alias.func()` 只能访问 `public` 符号
- `private` 符号在 alias 命名空间内也不可见
- 两者互补，不冲突

---

## 八、结论

| 决策点 | 结论 |
|--------|------|
| public/private 与 @export 关系 | 完全隔离，两轴正交 |
| 默认可见性 | Phase A = public（向后兼容） |
| private 对 mixin 语义的影响 | 不影响运行，仅名称隔离 |
| 实现方案 | origin-aware lookup（方案 B） |
| 同名 private 处理 | 内部 qualified name `(OriginFile, Name)`，对用户透明 |
| 迁移策略 | Phase A 零 breaking → Phase B 可选切换默认 |
