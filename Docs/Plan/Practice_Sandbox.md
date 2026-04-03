# 实践建议：Sandbox 构建过程中发现的问题与改进方向

> **来源**：Sandbox（FFScript 沙盒区）实际构建过程中遇到的阻碍和观察。
> **目的**：提出可纳入串行计划、展望计划或风险点的建议项。
> **日期**：2026-04-03

---

## 一、发现的 Bug

### P1. 编译器寄存器生命周期 Bug（严重）

**现象**：当函数中存在多个局部变量（其中部分在 while 循环前已"死亡"），编译器复用其寄存器给 `sum`/`i` 等循环变量时，在特定对齐条件下产生错误结果。

**最小复现**：
```ffs
func add(a: int, b: int): int { return a + b }
func main() {
    var a: int = 1
    var b: int = 2     // ← 两个死变量
    var sum: int = 0
    var i: int = 1
    while i <= 100 { sum = sum + i; i = i + 1 }
    Report(sum)         // 预期 5050，实际 127
    var r: int = add(1, 2)
}
```

**特征**：
- 2 个死变量 → 结果 127（❌），1 个或 3 个死变量 → 正确 5050（✅）
- 与是否存在函数调用无关（纯 syscall 也触发）
- `locals` 计数正确（编译器知道只需 2 个活跃局部），但分配的具体寄存器号导致运行时冲突
- 127 = 2⁷ - 1，疑似某处存在 7-bit 截断或 signed-byte 溢出

**影响范围**：任何在单函数中大量使用临时变量 + 循环的脚本均可能触发。

**建议**：
- **优先级**：高（阻塞实际使用）
- **归属**：寄存器生命周期分析（F4）或窥孔优化 pass（B-β1）
- **行动**：添加寄存器分配后的 bytecode 验证测试；逐条审计 `FreeTemp` / `AllocTemp` 的调用时机与 while 循环的 back-edge 交互

---

## 二、Sandbox 构建过程中的技术阻碍

### P2. .csproj 不在版本控制中

**现象**：Unity 项目惯例将 `.csproj` 加入 `.gitignore`，导致 Sandbox 和 StandaloneRunner 的 `.csproj` 文件无法直接 `git clone` 后使用。

**当前方案**：提供 `.csproj.template` 文件，用户需手动复制。CI 工作流使用 heredoc 动态生成。

**建议**：
- 在仓库根目录提供 `setup.sh` / `setup.bat` 脚本，一键生成所有 `.csproj`
- 或者将独立于 Unity 的 `.csproj`（StandaloneRunner、Sandbox）移出 `.gitignore`
- **优先级**：低（但影响新用户体验）

### P3. Syscall 参数寄存器约定需要文档化

**现象**：Syscall handler 从绝对寄存器 r0/r1/r2 读写参数，而非 `RegisterBase` 相对寻址。此约定在代码中通过示例隐含传递（CompilerTests 中 `s.Registers.Get(0)`），但缺少集中说明文档。

首次实现 Sandbox syscall 时，开发者曾错误使用 `s.RegisterBase + 0`（相对地址），导致读取到错误的寄存器值。此错误不会产生编译错误或运行时异常，只会导致静默的语义错误，极难调试。

**建议**：
- 在 `SyscallTable.cs` 的 XML Doc 中明确说明寄存器约定
- 在 `sandbox.ffvm.d.json` 或 `SyscallSignature` 中增加参数寄存器映射的元数据
- **优先级**：中（影响所有 syscall 实现者）

### P4. DapServer 不注册 Syscall

**现象**：`DapServer.HandleLaunch()` 创建空的 `Dictionary<string, int>()` 传给编译器，不注册任何 syscall。这意味着 DAP 调试模式下，包含 syscall 调用的脚本会编译失败。

**当前方案**：README 中标注"DAP 调试不含 Sandbox SysCall"限制。

**建议**：
- 允许 `DapServer` 接收外部传入的 syscall 映射（通过 launch.json 的 `syscallDecl` 参数指定 `.ffvm.d.json` 文件路径）
- 或实现 no-op 占位 syscall handler 自动注册
- **优先级**：中（影响 Sandbox 脚本的断点调试）

---

## 三、架构设计建议

### P5. Number 显示精度

**现象**：`Number.ToString()` 默认输出 4 位小数（如 `42.0000`、`5050.0000`），对整数值显示不友好。Sandbox 的控制台输出因此略显冗长。

**建议**：
- 为 `Number` 添加智能格式化方法：整数值显示为 `42`，非整数显示为 `3.14`
- **优先级**：低（美化输出，不影响功能）

### P6. 深度递归函数导致步数限制

**现象**：前一 session 发现 `fibonacci(10)` 使用递归实现时，即使 `MaxStepsPerTick = 10_000_000` 也报 `PanicStepLimitExceeded`。经调查，这不一定是步数不够，而可能与 P1 寄存器 bug 导致的无限循环有关。

**建议**：
- 修复 P1 后重新测试递归场景
- 考虑为 Sandbox 增加 `--max-steps` 命令行参数
- **优先级**：待 P1 修复后再评估

---

## 四、流程与工具建议

### P7. Sandbox 可作为回归测试宿主

**现象**：Sandbox 本质上是一个独立于 StandaloneRunner 的第二执行环境。可以用实际 `.ffs` 脚本文件验证编译器→运行时的端到端行为，补充单元测试的覆盖。

**建议**：
- 为 CI 添加 `Sandbox --run` 步骤，确保示例脚本始终可编译运行
- 添加断言检查脚本输出（如 `sum = 5050`）
- **优先级**：中

### P8. .vscode 目录被全局 gitignore 排除

**现象**：仓库 `.gitignore` 中有 `.vscode/` 规则，导致 `Sandbox/.vscode/launch.json` 和 `tasks.json` 无法被 git 追踪。这些文件对于开箱即用的调试体验是必要的。

**建议**：
- 在 `.gitignore` 中使用 `!Sandbox/.vscode/` 例外规则
- 或将调试配置文件以不同名称（如 `launch.json.template`）提供
- **优先级**：中（影响调试体验的开箱即用性）

---

## 五、总结与优先级排序

| ID | 建议 | 类型 | 优先级 | 建议归属 |
|----|------|------|--------|---------|
| P1 | 寄存器生命周期 Bug | Bug 修复 | **高** | 串行计划（新步骤 or B-γ1 前置） |
| P2 | .csproj 管理脚本 | 工程改进 | 低 | 展望计划 |
| P3 | Syscall 寄存器约定文档 | 文档 | 中 | 当前可执行 |
| P4 | DapServer + Syscall 支持 | 功能增强 | 中 | 串行计划候选 |
| P5 | Number 显示精度 | 美化 | 低 | 展望计划 |
| P6 | 深度递归验证 | 验证 | 待定 | 依赖 P1 |
| P7 | Sandbox 回归测试 | CI 增强 | 中 | 展望计划 |
| P8 | .vscode gitignore 例外 | 工程改进 | 中 | 当前可执行 |
