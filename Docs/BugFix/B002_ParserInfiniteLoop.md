# B002: Parser 无限循环 — struct 声明中使用逗号分隔字段

> **状态**：✅ 已修复
> **来源**：B001 测试中 `struct Box4 { ox: int, oy: int }` 导致 CI 超时
> **影响**：编译器 Parser 在 struct 声明体内遇到非预期 token 时静默无限循环

---

## 缺陷描述

### 触发条件

```ffs
struct Box4 { ox: int, oy: int }
```

使用逗号（`,`）而非换行或分号（`;`）分隔 struct 字段时，Parser 进入无限循环。

### 根因分析

`ParseStructDecl()` (Parser.cs:485–500) 的字段解析循环：

```
while (!Check(RBrace) && !IsAtEnd())
{
    Expect(Identifier, "for struct field name");
    Expect(Colon, "after field name");
    Expect(Identifier, "for field type");
    Match(Semicolon);   // ← 仅消费分号，逗号被忽略
}
```

**问题链**：
1. 解析完 `ox: int` 后，当前 token 为 `,`
2. `Match(Semicolon)` 失败，逗号不被消费
3. 循环回到顶部：`Expect(Identifier)` 对 `,` 失败 → 记录错误但 **不前进**
4. `Expect(Colon)` 对 `,` 同样失败 → 记录错误但 **不前进**
5. `Expect(Identifier)` 再次失败 → 记录错误但 **不前进**
6. `Match(Semicolon)` 仍然失败
7. **无限循环**：所有 token 均未被消费，`_pos` 永远不变

### 静默性问题

此类无限循环尤其危险：
- `Expect()` 失败时只向 `_errors` 列表添加错误消息，不抛出异常
- 调用方无法感知解析停滞
- CI 中表现为超时，无有意义的错误输出
- LSP 服务器中可能导致响应阻塞

---

## 修复方案

### Fix 1: ParseStructDecl 接受逗号分隔符

```csharp
// B002: accept comma or semicolon as optional field separator
if (!Match(TokenType.Semicolon))
    Match(TokenType.Comma);
```

与 `ParseStructLiteral` 行为对齐 — struct 字面量已支持逗号分隔。

### Fix 2: 安全守卫 — 防止无限循环

在所有 `while (!Check(RBrace) && !IsAtEnd())` 循环中添加位置检查：

```csharp
int savedPos = _pos;
// ... parse fields ...
if (_pos == savedPos)
{
    Error($"Unexpected token '{Current().Text}' in struct body at ...");
    Advance(); // 强制前进，打破无限循环
}
```

应用于三处：
1. `ParseStructDecl` — struct 声明
2. `ParseStructLiteral` — struct 字面量
3. `ParsePostfixExpression` 内的内联 struct 字面量（aliased struct literal）

### Fix 3: B001 枚举名引用遗漏

`CollectReferencesWithOrigin` 对 `SymbolKindTag.Enum` 类型缺少函数体内 `EnumName.MEMBER` 表达式中 `EnumName` 部分的引用收集。

添加 `EnumIdentRefsWalker` 遍历函数体（与模块级初始化器对称）。

---

## 测试覆盖

无需新增测试 — B001-07 测试 (`struct Box4 { ox: int, oy: int }`) 即为本缺陷的回归测试。修复前该测试导致无限循环/CI 超时，修复后正常通过。

B001-11 测试 (enum name refs ≥3) 从 FAIL 变为 PASS — Fix 3 修复。

全量测试：2166 total (114 TW + 1302 Compiler + 44 Perf + 18 FFS + 51 Debug + 97 DAP + 540 LSP)，0 失败。
