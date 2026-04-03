# B-R1: FFScript 正式命名 + `.ffs` 后缀统一

> **状态**：✅ 完成。624 项 Assert（112 TW + 214 Compiler + 17 Perf + 18 FFScript + 51 Debug + 93 DAP + 119 LSP），float + Fix64 双模式通过。

---

## 背景

脚本语言正式定名为 **FFScript**，源文件后缀统一为 **`.ffs`**。
此前代码中混用 `.vm`（源文件）和 `.ffvm`（编译后字节码 / 测试源文件），测试类名为 `SkillScriptTests`。
本步骤将全部引用统一为新命名约定。

## 命名约定

| 概念 | 旧名 | 新名 |
|------|------|------|
| 脚本语言名称 | （未正式命名） | **FFScript** |
| 源文件后缀 | `.vm` | **`.ffs`** |
| 编译后字节码格式 | `.ffvm` | `.ffvm`（不变） |
| VSCode 语言 ID | `ffvm` | `ffvm`（不变，保持一致性） |
| TextMate 作用域 | `source.ffvm` | `source.ffvm`（不变） |

## 变更清单

- [x] **R1-01** `SkillScriptTests` → `FFScriptTests`：类名、文件名、MenuItem、日志输出
- [x] **R1-02** `StandaloneRunner/Program.cs`：`SkillScriptTests.RunAll()` → `FFScriptTests.RunAll()`
- [x] **R1-03** `LspTests.cs`：全部 `file:///xxx.vm` URI → `file:///xxx.ffs`（~40 处）
- [x] **R1-04** `DapTests.cs`：全部 `dap_test_xxx.ffvm` → `dap_test_xxx.ffs`（11 处）
- [x] **R1-05** `vscode-ffvm-debug/package.json`：`extensions` 从 `[".ffvm", ".vm"]` → `[".ffs", ".ffvm"]`，`aliases` 从 `"FFVM Script"` → `"FFScript"`
- [x] **R1-06** `vscode-ffvm-debug/syntaxes/ffvm.tmLanguage.json`：`fileTypes` 从 `["ffvm", "vm"]` → `["ffs", "ffvm"]`
- [x] **R1-07** 技能示例文件重命名：`skill_*.vm` → `skill_*.ffs`
- [x] **R1-08** 文档更新：VM_Summary.md、Outlook_And_Risks.md、Skills/README.md、7 个步骤计划文档

## 妥协点

无。此为纯重命名/统一操作，无功能妥协。

## 功能展望

- `.ffvm.d.json` 声明文件格式名称可保留（属 VM 声明协议层，非脚本源文件）

## 风险点

无新增风险。
