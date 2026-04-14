# DX15: Private 跨文件補全過濾

## 目標

修復 LSP 審查（D17 D_LspUsabilityAudit）發現的已知限制 KL-04：
- **KL-04**：`private func/struct/enum/var` 仍出現在 include 文件的 `textDocument/completion` 列表中

## 前置條件

| 前置 | 狀態 |
|------|------|
| DX14 Rename 完整性補全 | ✅ 已完成 |
| DX12 LSP 可用性審查（KL-04 發現） | ✅ 已完成 |

## 變更摘要

### LspServer.cs

1. **`HandleCompletion`** — 在通用補全循環（Functions / Structs / Enums / ModuleVariables）每項迭代前加入 `IsPrivate && IsFromOtherFile(origin, current)` 守衛，跳過跨文件 private 符號。
2. **dot-context 補全** — struct 字段補全和 enum 成員補全同樣加入 private 守衛，避免 `EnumName.` 或 `structVar.` 觸發跨文件 private 項。
3. **`IsFromOtherFile(originFile, currentFilePath)`** — 新增 `internal static` 輔助方法，正規化路徑後大小寫不敏感比較，返回 `true` 表示符號來自不同文件。

### 測試

| 測試 ID | 場景 | 斷言數 |
|---------|------|--------|
| DX15-01 | private func 跨文件過濾 | 2 |
| DX15-02 | private struct 跨文件過濾 | 2 |
| DX15-03 | private enum 跨文件過濾 | 2 |
| DX15-04 | private var 跨文件過濾 | 2 |
| DX15-05 | private func/struct/enum 同文件可見 | 3 |
| DX15-06 | 混合可見性（include private 隱藏 + 本地 private 可見） | 3 |
| DX15-07 | private const 跨文件過濾 | 2 |

DX12-23 測試升級：移除 known-limitation 分支，改為嚴格 `!hasSecret` 斷言。

## 完成條件

| # | 條件 | 狀態 |
|---|------|------|
| ① | private func/struct/enum/var 不出現在跨文件 completion 中（KL-04 修復） | ✅ |
| ② | 同文件 private 符號仍正常出現 | ✅ |
| ③ | DX12-23 測試升級為嚴格斷言 | ✅ |
| ④ | DX15-01~07 全通過（16 asserts） | ✅ |
| ⑤ | 全量 2264 測試通過（638 LSP） | ✅ |
