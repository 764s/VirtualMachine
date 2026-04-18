# B-α1: LSP6 Syscall 声明协议 (Syscall Declaration Protocol)

## 目标

允许宿主通过声明文件 (`.ffvm.d.json`) 或注册 API 声明 Syscall 签名（参数名、参数类型、返回类型、说明文本），为 LSP5/LSP7 提供宿主方法元数据。

## 完成条件

| # | 条件 | 状态 |
|---|------|------|
| ① | SyscallTable 扩展签名元数据 | ✅ |
| ② | .ffvm.d.json 声明文件加载 | ✅ |
| ③ | 补全增强测试通过 | ✅ |

## 子任务清单

### Phase 1: SyscallTable 签名元数据扩展

- [x] 1.1 添加 `SyscallParamInfo` 记录类型 (name, typeName)
- [x] 1.2 添加 `SyscallSignature` 记录类型 (params[], returnType, description) + `Format(name)` 方法
- [x] 1.3 在 SyscallTable 中添加 `_signatures[]` 数组
- [x] 1.4 添加 `RegisterSignature(int slot, SyscallSignature sig)` 方法
- [x] 1.5 添加 `GetSignature(int slot)` 方法

### Phase 2: LspServer 声明文件支持

- [x] 2.1 定义 `.ffvm.d.json` JSON 格式规范
- [x] 2.2 添加 `LoadDeclarationJson(string json)` 方法：解析 JSON → 内部签名元数据
- [x] 2.3 LspServer 构造函数新增可选 `Dictionary<string, SyscallSignature>` 参数
- [x] 2.4 补全时使用签名元数据生成 detail 文本

### Phase 3: 测试覆盖

- [x] 3.1 LspBatchSession 支持传入 syscall 声明 (`SetSyscalls` + `SetDeclarationJson`)
- [x] 3.2 LSP6-T01: 声明后 syscall 补全项包含参数签名 (7 assertions)
- [x] 3.3 LSP6-T02: .ffvm.d.json 加载并生效 (7 assertions)
- [x] 3.4 LSP6-T03: 多参数 syscall detail 格式正确 (3 assertions)
- [x] 3.5 LSP6-T04: 无声明 syscall 保持向后兼容 (3 assertions)

### Phase 4: 文档更新

- [x] 4.1 VM_Summary.md 更新 B-α1 状态 ✅ + 推进当前位置 → B-α2
- [x] 4.2 追加功能/优化展望 + 风险点

## .ffvm.d.json 格式规范

```json
{
  "syscalls": [
    {
      "name": "PlayAnim",
      "slot": 0,
      "parameters": [
        { "name": "animId", "type": "int" },
        { "name": "speed", "type": "float" }
      ],
      "returnType": "void",
      "description": "Play animation by ID at given speed"
    },
    {
      "name": "GetHP",
      "slot": 1,
      "parameters": [],
      "returnType": "int",
      "description": "Get current hit points"
    }
  ]
}
```

字段说明：
- `name`: Syscall 名称（必须）
- `slot`: 槽位编号（必须，0-based）
- `parameters`: 参数列表（可选，默认空）
  - `name`: 参数名（必须）
  - `type`: 类型名（必须，如 `int` / `float` / `string`）
- `returnType`: 返回类型（可选，默认 `void`）
- `description`: 说明文本（可选）

## 实现统计

| 项目 | 数值 |
|------|------|
| 新增 Assert | +20（119 → 139 LspTests） |
| 总 Assert | 644（112 TW + 214 Compiler + 17 Perf + 18 FFScript + 51 Debug + 93 DAP + 139 LSP） |
| 修改文件 | SyscallTable.cs, LspServer.cs, LspTests.cs |
| 新增类型 | SyscallParamInfo, SyscallSignature |

## 功能展望

- **LSP7 signatureHelp**: B-α2 直接复用 `SyscallSignature` 提供参数提示，无需额外数据源
- **Hover 增强**: `HandleHover` 可使用 `description` 字段展示 syscall 文档
- **声明文件发现**: 未来可支持 `initializationOptions.declarationFiles` 在 LSP initialize 时自动加载

## 风险点

- **R-LSP6-1**: `.ffvm.d.json` 当前仅通过 `LoadDeclarationJson()` API 加载，无文件系统发现机制。LSP Server 启动时无自动扫描。这是有意设计——声明来源由宿主环境决定（编辑器扩展 / 编译管线 / CLI 参数），不属于 LSP Server 职责。
- **R-LSP6-2**: 声明文件 slot 与运行时 SyscallTable slot 的一致性由宿主保证，LSP Server 不做交叉验证。

## 妥协说明

无永久妥协。设计完整覆盖三个完成条件。
