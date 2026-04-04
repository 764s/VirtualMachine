# E002 Syscall 寄存器约定隐患（影响深远缺陷）

> **来源**：[P002_Sandbox_Build.md — P3, P4](../Practice/P002_Sandbox_Build.md)
> **等级**：🟠 影响深远缺陷 — 必须立即修复
> **状态**：⏳ 待修复
> **创建日期**：2026-04-04

---

## 一、缺陷描述

### P3 — Syscall 参数寄存器约定未文档化且手动管理

Syscall handler 从**绝对寄存器** r0/r1/r2 读写参数，而非 `RegisterBase` 相对寻址。此约定在代码中通过示例隐含传递，缺少集中说明文档。

首次实现 Sandbox syscall 时，开发者曾错误使用 `s.RegisterBase + 0`（相对地址），导致读取到错误的寄存器值。**此错误不产生编译错误或运行时异常，只产生静默语义错误，极难调试。**

### P4 — DapServer 不注册 Syscall + 新旧 Syscall 冲突风险

- `DapServer.HandleLaunch()` 创建空 syscall 映射，DAP 调试模式下含 syscall 的脚本编译失败
- 引入新 syscall 时，**手动指定寄存器槽位**存在与老 syscall 冲突的可能
- 引入过程应尽可能简单，不应要求开发者手动关注底层寄存器

### 现状总结

| 维度 | 当前状况 | 理想状态 |
|------|---------|---------|
| 参数传递 | 手动写绝对 r0/r1/r2 | 自动化，开发者不感知寄存器 |
| 冲突检测 | 无 | 编译期或注册期自动检测 |
| 文档 | 仅代码示例隐含 | 集中说明 + 机制保障 |
| DAP 支持 | 空映射，syscall 脚本无法调试 | 自动加载声明文件 |

---

## 二、用户决策

> **p3**：描述 syscall 的寄存器参数现状（最终不希望是手动指定）。
> **p4**：要求能解决问题，且**不手动关注底层寄存器**。担心引入新 syscall 与老 syscall 冲突。希望引入过程尽可能简单。

---

## 三、修复计划

### 阶段 1：Syscall 注册机制安全化

1. **Syscall 签名元数据自动化**：
   - 利用已有的 `SyscallSignature`（B-α1 已实现），确保参数数量、类型在注册时已声明
   - 注册时**自动分配**参数寄存器映射，开发者只需声明参数名和类型
2. **冲突检测**：
   - 在 `SyscallTable.Register()` 中增加槽位冲突检测
   - 若两个 syscall 的寄存器使用范围重叠，抛出明确错误
3. **Handler 参数抽象**：
   - 评估：为 syscall handler 提供类型安全的参数访问 API（如 `args.GetInt(0)` 而非 `s.Registers.Get(0)`）
   - 内部自动映射到正确寄存器号，开发者无需关心绝对/相对地址

### 阶段 2：DapServer 声明文件加载

4. **DapServer 加载 `.ffvm.d.json`**：
   - 允许 `launch.json` 通过 `syscallDecl` 参数指定声明文件路径
   - DapServer 解析声明文件，自动注册 no-op 占位 handler
   - 确保含 syscall 的脚本在 DAP 模式下可编译

### 阶段 3：验证

5. **回归测试**：全量 Assert 通过 + Sandbox syscall 功能正常
6. **DAP 调试验证**：含 syscall 的脚本在 VS Code 中可断点调试

---

## 四、风险点

| ID | 风险 | 缓解措施 |
|----|------|----------|
| ER4 | 参数抽象层可能引入性能开销 | 评估内联可能性，热路径上确保零额外开销 |
| ER5 | 自动寄存器映射可能与现有手动指定的 syscall 不兼容 | 过渡期支持两种模式，提供迁移路径 |
| ER6 | DapServer no-op handler 可能掩盖运行时错误 | no-op handler 输出警告日志，明确标记为占位 |
| ER7 | 改变 syscall 注册 API 影响现有所有 syscall 实现 | 阶段 1 先增量添加安全检查，不破坏现有 API |

---

## 五、与现有计划的关系

- B-α1（LSP6 Syscall 声明协议）已实现 `SyscallSignature` 元数据，可作为本修复的基础
- 阶段 1 的冲突检测和参数抽象属于**新增安全层**，不替换现有机制
- 阶段 2 的 DapServer 改进是 P4 的直接解决方案

---

## 六、完成标准

- [ ] Syscall 注册时自动验证无冲突
- [ ] Handler 参数访问 API 抽象底层寄存器
- [ ] 开发者引入新 syscall 无需手动指定寄存器号
- [ ] DapServer 支持加载 `.ffvm.d.json` 声明文件
- [ ] 全量 Assert 通过
- [ ] 更新本文件状态为 ✅
