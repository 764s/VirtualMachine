# Bug 修复区

> **定位**：语言服务器（LSP）及编译器已知 Bug 的修复跟踪。与讨论区平行，独立于串行计划。
>
> **状态标记**：
> - 🔧 **修复中** — 正在分析和修复
> - ✅ **已修复** — 修复已完成，测试已通过
>
> **文件命名**：`B{NNN}_{title}.md`

---

## 索引

| # | 文档 | 主题 | 状态 | 日期 | 备注 |
|---|------|------|------|------|------|
| B001 | [B001_ModuleLevelSymbolNavigation.md](B001_ModuleLevelSymbolNavigation.md) | 模块级符号导航缺陷 — 定义提供器/引用提供器/诊断 | ✅ 已修复 | 2026-04-13 | 影响 module-level const/var 声明中的枚举、结构体、函数、变量符号 |
| B002 | [B002_ParserInfiniteLoop.md](B002_ParserInfiniteLoop.md) | Parser 无限循环 — struct 声明中逗号分隔导致解析停滞 | ✅ 已修复 | 2026-04-14 | 静默无限循环，CI 超时；同时修复了三处 struct 解析循环的安全守卫 |
