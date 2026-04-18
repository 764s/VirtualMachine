# DX16: 變量引用作用域隔離

## 目標

修復 LSP 審查（D17 D_LspUsabilityAudit）發現的已知限制 KL-05：
- **KL-05**：同名變量 `textDocument/references` 返回所有同名引用，未按作用域隔離

## 前置條件

| 前置 | 狀態 |
|------|------|
| DX15 Private 跨文件補全過濾 | ✅ 已完成 |
| DX12 LSP 可用性審查（KL-05 發現） | ✅ 已完成 |

## 變更摘要

### SymbolAtPosition 結構

- 新增 `declLine` / `declCol` 欄位 — 記錄當前符號所屬的 `VarDeclStmt` 聲明位置（1-based）。`0` 表示模組級變量或參數（無需作用域隔離）。

### FindSymbolWalker

1. 新增 `_activeDecls` 字典 — 追蹤每個變量名在當前作用域鏈中的最新 VarDeclStmt 位置。
2. `VisitStmt` 攔截 `BlockStmt` / `ForStmt` — 在進入子塊前保存 `_activeDecls`，退出後恢復（作用域邊界 save/restore）。
3. `VarDeclStmt` 處理 — 先走查 initializer（使用**舊**的活動聲明），再更新 `_activeDecls`。
4. `IdentifierExpr` → Variable — 從 `_activeDecls` 讀取 `declLine`/`declCol`。

### ScopedIdentRefsWalker（新增）

- `AstWalker` 子類，帶有與 `FindSymbolWalker` 相同的作用域追蹤邏輯。
- 只在 active declaration 與目標 `(targetDeclLine, targetDeclCol)` 匹配時收集引用。
- 覆蓋 `VisitStmt`（BlockStmt/ForStmt 作用域邊界 + VarDeclStmt 更新）、`VisitExpr`（IdentifierExpr 收集）。

### CollectReferencesWithOrigin

- 新增 `declLine`/`declCol` 參數（默認 0）。
- Variable 分支：若 `scopeFunc != null && declLine > 0`，使用 `ScopedIdentRefsWalker` 只在目標函數內按作用域收集；否則回退到原有的 `IdentRefsWalker` 全局收集。

### FindDefinitionLocation

- 新增 `declLine`/`declCol` 參數。
- Variable 分支：若 `declLine > 0`，直接返回聲明位置，跳過 `FindVarDeclLocation` 掃描。

### HandleReferences / HandleRename

- 傳遞 `target.Value.declLine` / `target.Value.declCol` 給 `CollectReferencesWithOrigin`。

### HandleDefinition / HandleHighlight

- 傳遞 `target.Value.declLine` / `target.Value.declCol` 給 `FindDefinitionLocation`。

## 測試

| 測試 ID | 場景 | 斷言數 |
|---------|------|--------|
| DX16-01 | inner/outer 同名變量 — outer 引用隔離 | 1 |
| DX16-02 | inner/outer 同名變量 — inner 引用隔離 | 1 |
| DX16-03 | 從使用位置（非聲明）查引用 — 作用域正確 | 2 |
| DX16-04 | for 循環變量作用域隔離 | 2 |
| DX16-05 | rename 尊重作用域隔離 | 2 |
| DX16-06 | go-to-definition 跳轉到正確的作用域聲明 | 2 |
| DX16-07 | 三層嵌套 — 每層獨立隔離 | 3 |
| DX16-08 | 無 shadowing 回歸 — 單一變量仍收集所有引用 | 1 |
| DX12-22 | 升級：`≥1` → `==3` / `==2`（作用域隔離已生效） | 2 |
| **合計** | | **16** |

## 總計

- 新增 DX16-01~08（16 asserts）
- DX12-22 升級（2 asserts 嚴格化）
- 2278 測試總計（652 LSP）
