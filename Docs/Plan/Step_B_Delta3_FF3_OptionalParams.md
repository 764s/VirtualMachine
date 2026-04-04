# B-δ3 FF3 可选参数与默认值

## 概要
为 FFScript 函数声明增加可选参数 (`= expr`) 支持,编译器自动在调用端填充缺省值。

## 设计

### 语法
```
func name(required: int, opt: int = 10): int { ... }
```
- 可选参数必须在所有必选参数之后
- 默认表达式可以是：字面量(int/float/bool)、一元负号 `-5`

### AST 变更
- `ParamDecl` 增加 `DefaultValue` 字段（`Expr?`, null = 必选）

### Parser 变更
- `ParseFuncDecl`：参数类型后检测 `=`，解析 `ParseExpression()` 作为默认值
- 强制 required-before-optional 顺序，违反时报编译错误

### Compiler 变更 (BytecodeCompiler.EmitUserCall)
- 统计 `requiredCount`（无 DefaultValue 的参数数）和 `totalCount`
- 实参数量验证：`requiredCount <= argCount <= totalCount`
- 显式实参放入 scratch zone 后，遍历剩余参数，编译 `DefaultValue` expr 并 MOVE 到 scratch

### LSP 变更
- 新增 `FormatParamDecl(ParamDecl)` 和 `FormatDefaultValue(Expr)` 辅助方法
- `FormatFuncSignature`、hover、completion、signatureHelp 参数标签均显示 `= value`

## 测试

| ID | 场景 | 断言数 |
|---|---|---|
| FF3-01 | 基本可选参数(省略/提供) | 2 |
| FF3-02 | 多个可选参数(0/1/2省略) | 3 |
| FF3-03 | 所有参数可选 | 2 |
| FF3-04 | 实参不足 → 编译错误 | 1 |
| FF3-05 | 实参过多 → 编译错误 | 1 |
| FF3-06 | 必选在可选后 → 编译错误 | 1 |
| FF3-07 | 负数默认值 (-5) | 2 |
| FF3-LSP-01 | signatureHelp 显示默认值 | 5 |
| FF3-LSP-02 | hover 参数显示默认值 | 3 |

## 风险
- 默认表达式当前求值在调用端而非编译期常量折叠，未来可优化
- 仅支持字面量表达式；复杂表达式(函数调用等)也能编译但 LSP 显示为 `...`
