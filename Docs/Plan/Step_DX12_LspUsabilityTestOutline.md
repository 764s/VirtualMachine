# DX12: FFS LSP 易用性测试用例大纲

> **目标**: 结构性覆盖日常脚本编写中所有跨文件转到定义/查找引用场景，确保语言服务在实际使用的各个角落都能正常工作。

---

## 一、测试矩阵设计原则

**维度1 — 符号类型 (Symbol Kind)**
| ID | 符号类型 | 说明 |
|----|---------|------|
| SK1 | 函数 (Function) | func 声明与调用 |
| SK2 | 结构体 (Struct) | struct 声明与使用 |
| SK3 | 枚举 (Enum) | enum 声明与使用 |
| SK4 | 枚举成员 (EnumMember) | Color.RED 等 |
| SK5 | 结构体字段 (StructField) | pos.x 等字段访问 |
| SK6 | 局部变量 (LocalVariable) | 函数内 var/const |
| SK7 | 模块变量 (ModuleVariable) | 顶层 var/const |
| SK8 | 参数 (Parameter) | 函数参数 |
| SK9 | Include文件 (IncludeFile) | include "path" |
| SK10 | 外部函数 (ExternalFunc) | external func |

**维度2 — LSP 功能 (LSP Feature)**
| ID | 功能 | 说明 |
|----|------|------|
| LF1 | 转到定义 (Go-to-Definition) | textDocument/definition |
| LF2 | 查找引用 (Find References) | textDocument/references |
| LF3 | 悬停提示 (Hover) | textDocument/hover |
| LF4 | 重命名 (Rename) | textDocument/rename |
| LF5 | 自动补全 (Completion) | textDocument/completion |
| LF6 | 签名帮助 (SignatureHelp) | textDocument/signatureHelp |
| LF7 | 文档符号 (DocumentSymbol) | textDocument/documentSymbol |
| LF8 | 语义着色 (SemanticTokens) | textDocument/semanticTokens/full |

**维度3 — 文件范围 (Scope)**
| ID | 范围 | 说明 |
|----|------|------|
| FS1 | 同文件 (Same File) | 定义和使用在同一文件 |
| FS2 | 直接包含 (Direct Include) | A include B，符号在B中定义 |
| FS3 | 传递包含 (Transitive Include) | A→B→C，符号在C中定义 |
| FS4 | 菱形包含 (Diamond Include) | A→B,A→C,B→D,C→D |
| FS5 | 别名包含 (Aliased Include) | include "x" as Alias |
| FS6 | ffproj 路径 (.ffproj includePaths) | 非根目录 include |

---

## 二、符号类型 × LSP功能 × 文件范围 覆盖矩阵

### 2.1 函数 (SK1 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 同文件函数定义跳转 | LF1 | FS1 | LSP4-T07, US-02 | ✅ |
| 同文件函数引用查找 | LF2 | FS1 | LSP4-T10 | ✅ |
| 同文件函数悬停 | LF3 | FS1 | LSP4-T04 | ✅ |
| 同文件函数重命名 | LF4 | FS1 | DX5-07 | ✅ |
| 同文件函数补全 | LF5 | FS1 | LSP5-T02 | ✅ |
| 同文件函数签名帮助 | LF6 | FS1 | LSP7-T01 | ✅ |
| 跨文件函数定义跳转 | LF1 | FS2 | DX4-P3-05, US-03 | ✅ |
| 跨文件函数引用查找 | LF2 | FS2 | US-09 | ✅ |
| 跨文件函数悬停 | LF3 | FS2 | DX4-P3-07, US-12 | ✅ |
| 跨文件函数重命名 | LF4 | FS2 | US-16 | ✅ |
| 跨文件函数补全 | LF5 | FS2 | DX4-P3-08 | ✅ |
| 跨文件函数签名帮助 | LF6 | FS2 | DX4-P3-09, US-13 | ✅ |
| 传递包含函数定义跳转 | LF1 | FS3 | DX4-P3-11 | ✅ |
| **传递包含函数引用查找** | LF2 | FS3 | — | ❌ **GAP** |
| **传递包含函数补全** | LF5 | FS3 | — | ❌ **GAP** |
| **别名包含函数定义跳转** | LF1 | FS5 | — | ❌ **GAP** |
| **别名包含函数引用查找** | LF2 | FS5 | — | ❌ **GAP** |
| ffproj 路径函数定义跳转 | LF1 | FS6 | DX4-P3-14, US-17 | ✅ |
| ffproj 路径函数引用查找 | LF2 | FS6 | DX4-P3-16 | ✅ |

### 2.2 结构体 (SK2 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 同文件struct定义跳转 | LF1 | FS1 | US-02 | ✅ |
| 同文件struct引用查找 | LF2 | FS1 | DX5-08~09 | ✅ |
| 同文件struct悬停 | LF3 | FS1 | US-06, US-07 | ✅ |
| 同文件struct重命名 | LF4 | FS1 | DX5-10 | ✅ |
| 跨文件struct定义跳转 | LF1 | FS2 | US-03, DX4-P3-15 | ✅ |
| 跨文件struct引用查找 | LF2 | FS2 | US-10 | ✅ |
| 跨文件struct悬停 | LF3 | FS2 | US-08 | ✅ |
| **跨文件struct重命名** | LF4 | FS2 | — | ❌ **GAP** |
| 跨文件struct字段补全 | LF5 | FS2 | DX4-P3-12 | ✅ |
| **传递包含struct定义跳转** | LF1 | FS3 | — | ❌ **GAP** |

### 2.3 枚举 (SK3 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 同文件enum定义跳转 | LF1 | FS1 | US-02 | ✅ |
| 同文件enum引用查找 | LF2 | FS1 | E003-01 | ✅ |
| 同文件enum悬停 | LF3 | FS1 | LSP-EN03, US-04/05 | ✅ |
| 跨文件enum定义跳转 | LF1 | FS2 | US-03 | ✅ |
| 跨文件enum引用查找 | LF2 | FS2 | US-11, E003-03 | ✅ |
| **跨文件enum重命名** | LF4 | FS2 | — | ❌ **GAP** |
| 跨文件enum成员补全 | LF5 | FS2 | LSP-EN02 | ✅ |

### 2.4 枚举成员 (SK4 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 同文件enumMember定义跳转 | LF1 | FS1 | LSP-EN04 | ✅ |
| 同文件enumMember引用查找 | LF2 | FS1 | E003-02 | ✅ |
| 跨文件enumMember定义跳转 | LF1 | FS2 | US-15 | ✅ |
| 跨文件enumMember引用查找 | LF2 | FS2 | E003-04 | ✅ |
| **跨文件enumMember重命名** | LF4 | FS2 | — | ❌ **GAP** |

### 2.5 结构体字段 (SK5 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 同文件字段定义跳转 | LF1 | FS1 | DX7-01 | ✅ |
| 同文件字段引用查找 | LF2 | FS1 | DX7-03 | ✅ |
| 跨文件字段定义跳转 | LF1 | FS2 | US-14 | ✅ |
| **跨文件字段引用查找** | LF2 | FS2 | — | ❌ **GAP** |
| **跨文件字段重命名** | LF4 | FS2 | — | ❌ **GAP** |
| **嵌套字段定义跳转(a.b.c)** | LF1 | FS1 | — | ❌ **GAP** |

### 2.6 变量与参数 (SK6~SK8 × LF* × FS*)

| 测试场景 | LF | FS | 现有测试 | 状态 |
|---------|----|----|---------|------|
| 局部变量定义跳转 | LF1 | FS1 | LSP4-T08 | ✅ |
| 局部变量引用查找 | LF2 | FS1 | LSP4-T11 | ✅ |
| 模块变量定义跳转 | LF1 | FS1 | B001-04 | ✅ |
| 模块变量引用查找 | LF2 | FS1 | B001-09/10 | ✅ |
| **模块变量跨文件定义跳转** | LF1 | FS2 | — | ❌ **GAP** |
| **模块变量跨文件引用查找** | LF2 | FS2 | — | ❌ **GAP** |
| 参数定义跳转 | LF1 | FS1 | (implicit in func) | ⚠️ 无专项 |
| **参数引用查找** | LF2 | FS1 | — | ❌ **GAP** |
| **参数重命名** | LF4 | FS1 | — | ❌ **GAP** |

---

## 三、使用上下文覆盖 (Symbol Contexts)

### 3.1 符号出现在不同语句上下文中

| 上下文 | 说明 | 现有覆盖 | 状态 |
|-------|------|---------|------|
| 变量声明初始化器 | `var x: int = helper()` | LSP4-T07 | ✅ |
| 赋值右侧 | `x = helper()` | — | ❌ **GAP** |
| If条件 | `if x > threshold {` | — | ❌ **GAP** |
| While条件 | `while count > 0 {` | — | ❌ **GAP** |
| For初始化/条件/增量 | `for var i=0; i<max; i=i+1` | — | ❌ **GAP** |
| Return表达式 | `return calculate()` | — | ❌ **GAP** |
| Wait表达式 | `wait frameCount` | (implicit) | ⚠️ |
| 函数调用参数 | `foo(bar(), baz)` | — | ❌ **GAP** |
| 二元表达式操作数 | `a + getVal()` | — | ❌ **GAP** |
| 结构体字面量字段值 | `Vec2 { x: getX(), y: 0 }` | — | ❌ **GAP** |
| 模块变量初始化器 | `const v = TagBit.WALK` | B001-01~12 | ✅ |
| 类型注解 | `var p: Point` | B001-03, E003 | ✅ |
| 嵌套函数调用 | `foo(bar(baz()))` | — | ❌ **GAP** |
| 字段访问链 | `obj.field.sub` | — | ❌ **GAP** |

### 3.2 控制流内部符号可达性

| 场景 | 说明 | 现有覆盖 | 状态 |
|-----|------|---------|------|
| if 分支内变量 | 定义在if块中的变量 | — | ❌ **GAP** |
| while 循环体内变量 | 循环体内定义的变量 | — | ❌ **GAP** |
| for 循环变量 | for的init变量在循环体中引用 | — | ❌ **GAP** |
| 嵌套块变量 | 内层块访问外层变量 | — | ❌ **GAP** |
| defer 块内符号 | defer { cleanup() } | — | ❌ **GAP** |

---

## 四、跨文件高级场景

### 4.1 多文件拓扑

| 场景 | 说明 | 现有覆盖 | 状态 |
|-----|------|---------|------|
| A→B 直接包含 | 基本场景 | DX4-P3 全系列 | ✅ |
| A→B→C 传递包含 | 二级传递 | DX4-P3-11 | ✅ |
| A→B, A→C (扇出) | 一个文件包含多个 | DX6-04 | ✅ |
| A→C, B→C (扇入) | 多个文件包含同一个 | DX10-07 | ✅ |
| A→B→D, A→C→D (菱形) | 菱形依赖 | DX10-03 | ✅ |
| **A→B→C→D 深链** | 三级以上传递 | — | ❌ **GAP** |
| **A→B as Lib (别名导航)** | 别名模块内符号导航 | — | ❌ **GAP** |
| 隔离测试 (无include) | 无include时不看到其他文件符号 | DX4-P3-10 | ✅ |

### 4.2 编辑与增量更新

| 场景 | 说明 | 现有覆盖 | 状态 |
|-----|------|---------|------|
| 编辑被包含文件→诊断级联 | 改B后A获得新诊断 | DX10-01/02 | ✅ |
| 磁盘文件变化级联 | didChangeWatchedFiles | DX10-05 | ✅ |
| **添加新include后符号可见** | 编辑A加入include B，B的符号出现在A的补全中 | — | ❌ **GAP** |
| **移除include后符号不可见** | 编辑A移除include B，B的符号不再出现 | — | ❌ **GAP** |
| 依赖图随import变化更新 | DX10-04 | DX10-04 | ✅ |
| 连续文件重命名 | DX11 全系列 | DX11-01~05 | ✅ |

### 4.3 private 与 override

| 场景 | 说明 | 现有覆盖 | 状态 |
|-----|------|---------|------|
| **private func 不跨文件可见** | A include B, B 有 private func, A 补全不显示 | — | ❌ **GAP** |
| **private struct 不跨文件可见** | 同上 struct | — | ❌ **GAP** |
| **override func 定义跳转** | 跳转到override而非原始 | — | ❌ **GAP** |
| **override func 引用查找** | 包含override和被覆盖的声明 | — | ❌ **GAP** |

---

## 五、边界条件与异常场景

| 场景 | 说明 | 现有覆盖 | 状态 |
|-----|------|---------|------|
| 空文件 | 补全/符号不崩溃 | LSP4-T03, LSP5-T04 | ✅ |
| 语法错误文件 | 仍能提供部分导航 | LSP-T05 | ✅ |
| 未知符号 | 定义返回null | LSP4-T09 | ✅ |
| 空位置 | 悬停返回null | LSP4-T06 | ✅ |
| **光标在关键字上** | if/while/for/return 上定义/引用 | — | ❌ **GAP** |
| **光标在字面量上** | 数字/字符串上 | — | ❌ **GAP** |
| **光标在操作符上** | +, -, == 上 | — | ❌ **GAP** |
| **同名不同作用域变量** | 内外层同名变量，引用查找应隔离 | — | ❌ **GAP** |
| **同名函数与变量** | 局部变量与函数同名时的消歧 | — | ❌ **GAP** |
| 无workspace根目录 | 回退到单文件AST | DX4-P3-13 | ✅ |

---

## 六、现有测试审查结果

### ✅ 覆盖良好的区域
1. **函数跨文件导航** — 完整覆盖 FS1~FS3, FS6 的 LF1~LF6
2. **枚举支持** — 从 LSP-EN 到 E003 到 B001，完整覆盖
3. **依赖级联** — DX10 8个测试覆盖了各种拓扑
4. **文件重命名** — DX6 + DX11 共15个测试
5. **语义着色** — DX9 8个测试覆盖了主要token类型
6. **用户场景** — US-01~18 覆盖了最常见的跨文件操作

### ❌ 关键缺失 (按优先级排序)

**P0 — 日常使用频繁遇到**
1. **参数导航** — 无参数的定义跳转、引用查找、重命名测试
2. **控制流内符号** — 无 if/while/for 内部的符号导航测试
3. **赋值/Return/调用参数中的符号** — 无在表达式深处的符号导航测试
4. **嵌套字段访问** — 无 `a.b.c` 的导航测试

**P1 — 跨文件高级场景**
5. **跨文件struct/enum重命名** — 仅函数有跨文件重命名测试
6. **跨文件struct字段引用** — 仅有同文件字段引用测试
7. **传递包含引用查找** — 仅有传递定义跳转
8. **模块变量跨文件导航** — 仅有同文件测试
9. **别名包含符号导航** — 无 Alias.func() 的导航测试

**P2 — 边界条件**
10. **private 可见性** — 无private符号跨文件不可见的测试
11. **override 导航** — 无override声明的导航测试
12. **同名变量隔离** — 无同名不同作用域的消歧测试
13. **深链传递包含** — 仅测试到2级传递

---

## 七、审查发现的已知限制

通过 DX12 测试运行，发现并记录了以下已知限制：

| ID | 问题 | 严重度 | 说明 |
|----|------|-------|------|
| KL-01 | 参数引用不包含声明位置 | 低 | `textDocument/references` 在参数上仅返回函数体内的使用，不包括参数声明本身 |
| KL-02 | 参数重命名不支持 | 中 | `textDocument/rename` 对参数返回 null，无法重命名参数名 |
| KL-03 | struct字面量名不计入struct重命名 | 低 | `Vec2 { x: 1 }` 中的 `Vec2` 可能不被 rename 编辑覆盖 |
| KL-04 | private 不过滤跨文件补全 | 中 | `private func` 仍出现在 include 文件的 completion 列表中 |
| KL-05 | 同名变量引用不隔离作用域 | 低 | 内外层同名 `var x` 的 references 返回所有同名引用（5个），未按作用域隔离 |

这些发现为后续 LSP 改进提供了明确方向。

---

## 八、改进实施计划（已完成）


### DX12-Phase1: 表达式上下文覆盖 (DX12-01 ~ DX12-06)
- DX12-01: 赋值右侧函数调用的定义跳转
- DX12-02: if条件中变量/函数的定义跳转
- DX12-03: for循环变量引用查找
- DX12-04: return表达式中函数调用的定义跳转
- DX12-05: 嵌套函数调用中内层函数的定义跳转
- DX12-06: 结构体字面量字段值中函数调用的定义跳转

### DX12-Phase2: 参数导航 (DX12-07 ~ DX12-09)
- DX12-07: 参数定义跳转（函数内引用参数→跳转到参数声明）
- DX12-08: 参数引用查找（在参数名上查找所有引用）
- DX12-09: 参数重命名（重命名参数名更新所有引用）

### DX12-Phase3: 跨文件高级导航 (DX12-10 ~ DX12-16)
- DX12-10: 传递包含引用查找（A→B→C, 在A中查找C中函数的引用）
- DX12-11: 传递包含补全（A→B→C, A中能补全C的函数）
- DX12-12: 跨文件struct重命名
- DX12-13: 跨文件enum重命名
- DX12-14: 跨文件struct字段引用查找
- DX12-15: 跨文件enum成员重命名
- DX12-16: 模块变量跨文件定义跳转与引用查找

### DX12-Phase4: 控制流与高级上下文 (DX12-17 ~ DX12-22)
- DX12-17: if/else分支内变量的引用查找
- DX12-18: while循环条件中符号的定义跳转
- DX12-19: for循环初始化变量的引用查找
- DX12-20: 嵌套字段访问链定义跳转 (a.b.c)
- DX12-21: 函数调用参数位置的符号定义跳转
- DX12-22: 同名不同作用域变量的引用隔离

### DX12-Phase5: 可见性与Override (DX12-23 ~ DX12-25)
- DX12-23: private函数不出现在跨文件补全中
- DX12-24: override函数定义跳转到override声明
- DX12-25: 深链传递包含(3级以上)的定义跳转
