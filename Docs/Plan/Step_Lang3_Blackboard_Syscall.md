# Lang-3: 黑板 Syscall 正式化（GetBlackboard / SetBlackboard 标准化测试）

> **来源**：VM_Summary.md §七 Lang 表、KOF98/Docs/Discussion/D_SkillScripting.md SK10/SK14
>
> **前置**：Lang-1 ✅（模块变量）、Lang-1.1a ✅（MaxRegisters 常量配置化）、Lang-1.1b ✅（扩展寄存器）、Lang-2 ✅（include）
>
> **状态**：✅ 完成
>
> **目标**：验证黑板 Syscall 在 VM 中的端到端正确性。通过标准化测试套件（BB01–BB10）确认 Get/SetBlackboard 与现有语言特性（模块变量、include、defer/using、字符串常量）的集成无误。

---

## 一、核心设计

### 1.1 黑板概念

黑板（Blackboard）是跨脚本运行时数据共享的标准通道：
- **写入**：`SetBlackboard(key, value)` — 将 value 写入 key 对应的槽位
- **读取**：`GetBlackboard(key)` — 从 key 对应的槽位读取 value，不存在返回 0

### 1.2 Key 约定

依据 VM_Summary §3.7 决策：
- 黑板 Key 使用编译期整数 ID（`const KEY_X: int = 1`）
- 禁止运行时 Hash
- 曳光弹阶段：手工映射常量 ID

### 1.3 Syscall 签名

| Syscall | 参数 | 返回值 | 说明 |
|---------|------|--------|------|
| `SetBlackboard(key: int, value: int)` | r0=key, r1=value | 无 | 写入黑板 |
| `GetBlackboard(key: int): int` | r0=key | r0=value | 读取黑板，不存在返回 0 |

> 注：测试中使用简化签名（不含 entityId），与 KOF98 生产实现（含 entityId）签名不同。
> 生产实现在 `KOF98/VM/GameSyscalls.cs` 中，slot 180/181。

---

## 二、测试矩阵（BB01–BB10）

| ID | 场景 | 验证点 |
|----|------|--------|
| BB01 | 基础 Set/Get 往返 | SetBlackboard(key, value) 后 GetBlackboard(key) 返回 value |
| BB02 | 多 Key 独立性 | 不同 key 的值互不干扰 |
| BB03 | 默认值 | GetBlackboard 未设置的 key 返回 0 |
| BB04 | 覆盖写入 | 同一 key 多次 Set，Get 返回最后一次的值 |
| BB05 | 跨函数持久 | main 中 Set，调用另一函数中 Get，值可见 |
| BB06 | 与模块变量集成 | 模块变量缓存 Get 结果，跨函数共享 |
| BB07 | 与 include 集成 | include 文件定义 key 常量 + 辅助函数，主文件调用 |
| BB08 | 与 defer 集成 | defer 中 SetBlackboard 重置，确保 cleanup 正确执行 |
| BB09 | 与字符串 Key 集成 | 使用字符串常量作为 key 名称传给 syscall（字符串索引 = 整数 ID） |
| BB10 | 循环批量读写 | while 循环中批量 Set/Get，验证多次读写正确性 |

---

## 三、实现方案

### 3.1 测试基础设施

测试使用本地 `Dictionary<int, int>` 模拟黑板存储，注册 `SetBlackboard`（slot 0）和 `GetBlackboard`（slot 1）两个 Syscall。

```csharp
var board = new Dictionary<int, int>();
world.Syscalls.Register(0, "SetBlackboard", (ref VMInstanceState s) =>
{
    var args = new SyscallArgs(ref s);
    board[args.GetInt(0)] = args.GetInt(1);
});
world.Syscalls.Register(1, "GetBlackboard", (ref VMInstanceState s) =>
{
    var args = new SyscallArgs(ref s);
    int key = args.GetInt(0);
    args.SetReturnInt(board.TryGetValue(key, out var v) ? v : 0);
});
```

### 3.2 无 VM/编译器改动

Lang-3 是纯宿主 Syscall 验证，不需要任何 VM 运行时或编译器改动。所有功能已由现有基础设施支持：
- SyscallTable.Register（slot 注册）
- SyscallArgs（参数读取 + 返回值设置）
- 字符串常量（STR1，已完成）
- 模块变量（Lang-1，已完成）
- include（Lang-2，已完成）

---

## 四、验收标准

- [ ] BB01–BB10 全部通过
- [ ] 现有 1087 测试无回归
- [ ] VM_Summary.md 更新 Lang-3 状态为 ✅
