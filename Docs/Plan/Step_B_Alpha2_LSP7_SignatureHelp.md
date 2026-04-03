# B-α2: LSP7 参数提示 (signatureHelp)

## 目标

实现 `textDocument/signatureHelp`，在用户输入 `funcName(` 或 `,` 时显示参数列表与当前参数高亮。
覆盖用户函数和 Syscall（通过 LSP6 声明获取签名）。

## 依赖

- B-α1 (LSP6 Syscall 声明协议) ✅

## 子任务

- [x] 1. `HandleInitialize` 注册 `signatureHelpProvider` 能力（触发字符 `(`, `,`）
- [x] 2. `HandleRequest` dispatch 添加 `textDocument/signatureHelp` 分支
- [x] 3. 实现 `HandleSignatureHelp` 方法
  - 从光标位置反向扫描源码，找到函数名和当前参数索引
  - 用户函数：从 AST `FuncDecl` 获取签名
  - Syscall：从 `_syscallSignatures` 获取签名
  - 构建 LSP `SignatureHelp` 响应（signatures + activeParameter + activeSignature）
- [x] 4. 测试辅助 `LspBatchSession.AddSignatureHelp`
- [x] 5. 测试用例（≥8 项 Assert）：
  - 用户函数 `(` 后触发
  - 参数 `,` 后 activeParameter 递增
  - Syscall signatureHelp
  - 未知函数返回 null
  - 嵌套括号正确处理
  - 多参数 activeParameter 追踪
- [x] 6. 更新 VM_Summary.md 步骤状态
- [x] 7. 追加功能展望 / 优化展望 / 风险点

## 设计决策

### 参数索引计算

通过从光标位置反向扫描源码文本：
1. 找到未匹配的 `(`，记录函数名
2. 统计 `(` 到光标之间的 `,`（忽略嵌套括号和字符串内的逗号）
3. 逗号数即为 activeParameter 索引

### 响应格式

```json
{
  "signatures": [
    {
      "label": "func add(a: int, b: int): int",
      "parameters": [
        { "label": "a: int" },
        { "label": "b: int" }
      ]
    }
  ],
  "activeSignature": 0,
  "activeParameter": 0
}
```

### 妥协

- **无重载支持**：FFScript 不支持函数重载，`signatures` 数组始终只含一项。永久妥协（语言设计如此）。
- **字符串内逗号**：简单跳过引号对内的字符，足以覆盖常见场景。

## 完成结果

- 676 项 Assert 全通过（112 TW + 214 Compiler + 17 Perf + 18 FFScript + 51 Debug + 93 DAP + 171 LSP）
- LSP7 新增 32 项 Assert（8 个测试用例 × 多项检查）

## 功能展望

- **LSP8 参数标签偏移量**：当前 `parameters[].label` 使用字符串格式，未来可改为 `[start, end]` 偏移量格式，让编辑器精确高亮签名中的参数片段。
- **重试触发**：当用户删除 `)` 后重新输入时，signatureHelp 应自动重新触发。当前依赖编辑器的 retrigger 机制。

## 优化展望

- **缓存调用上下文**：`FindCallContext` 每次从光标位置反向扫描全文本。对于大文件，可缓存最近一次的函数名和括号位置，增量更新。

## 风险点

| 风险 | 等级 | 说明 |
|------|------|------|
| 反向扫描性能 | 低 | 当前逐字符反向扫描，对典型脚本文件（<1000 行）无性能问题 |
| 字符串转义 | 低 | 反向扫描跳过 `"..."` 字符串时不处理转义引号 `\"`，极端情况可能误判 |
