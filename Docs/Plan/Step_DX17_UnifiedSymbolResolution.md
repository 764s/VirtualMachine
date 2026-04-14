# DX17: 統一符號解析

## 目標

消除 LSP 結構審計（D19 D_LspStructuralAudit）識別的符號解析重複問題：
- 合併 `SymbolAtPosition` + `FindDefinitionLocation` 返回值為 `ResolvedSymbol`
- `HandleDefinition`/`HandleReferences`/`HandleRename`/`HandleHover` 共享 `ResolveSymbol` 調用
- 消除 `ResolveSymbolDualAst` 中的二次查找
- 修復 `AstWalker.WalkStmt` 遺漏 `WaitForStmt.TargetInstanceId` 子表達式

## 前置條件

| 前置 | 狀態 |
|------|------|
| DX16 變量引用作用域隔離 | ✅ 已完成 |
| D19 LSP 結構審計（根因分析） | ✅ 已完成 |

## 子任務

### Phase A: 修復 WaitForStmt 子表達式遍歷

- [ ] A1: `AstWalker.WalkStmt` 中新增 `WaitForStmt` 分支，走查 `TargetInstanceId` 表達式
- [ ] A2: 新增測試 DX17-01 — `wait_for(var)` 中的變量出現在 references 結果中

### Phase B: 定義 ResolvedSymbol + ResolveSymbol

- [ ] B1: 定義 `struct ResolvedSymbol`，合併身份欄位（Kind/Name/ParentName/ScopeFunc）+ 定義位置（DefLine/DefCol/NameLen/OriginFile）+ 作用域隔離（ScopeDeclLine/ScopeDeclCol）
- [ ] B2: 實現 `ResolveSymbol(uri, astLine, astCol)` → `(ResolvedSymbol?, ModuleNode mergedAst)`
  - 吸收 `ResolveSymbolDualAst` 雙 AST 解析邏輯
  - 內聯 `FindDefinitionLocation` 調用
  - IncludeFile 作為 Kind 返回（不需要額外返回值）
- [ ] B3: 全部 652 LSP 測試通過（回歸驗證）

### Phase C: 重構 Handle* 方法使用 ResolveSymbol

- [ ] C1: `HandleDefinition` — 使用 `ResolveSymbol`，從 `DefLine/DefCol/OriginFile` 構建跳轉位置
- [ ] C2: `HandleReferences` — 使用 `ResolveSymbol`，傳遞 `ScopeDeclLine/ScopeDeclCol` 給 `CollectReferencesWithOrigin`
- [ ] C3: `HandleRename` — 使用 `ResolveSymbol`，同 C2
- [ ] C4: `HandleHover` — 使用 `ResolveSymbol` + 新 `FormatHoverForSymbol(ResolvedSymbol, ModuleNode)` 替代 `FindHoverText`
  - 妥協：聲明位置關鍵字懸停（"func"/"struct"/"enum"/"var" 關鍵字上的 hover）保留為 fallback，不改變 `FindSymbolAtPosition` 行為。原因：修改 `FindSymbolAtPosition` 匹配關鍵字可能影響 go-to-definition / rename 行為。DX18 統一引用收集完成後可重新評估。

### Phase D: 清理 + 驗證

- [ ] D1: 移除廢棄代碼（`SymbolAtPosition` 結構體、`ResolveSymbolDualAst` 方法，若已無引用）
- [ ] D2: 全部 2278 測試通過，0 回歸

## ResolvedSymbol 結構設計

```
struct ResolvedSymbol {
    SymbolKindTag Kind;
    string Name;
    string ParentName;       // struct/enum name for fields/members
    string ScopeFunc;        // containing function (null for top-level)

    // Definition location (from FindDefinitionLocation)
    int DefLine, DefCol;     // 1-based, actual definition position
    int NameLen;             // name token length
    string OriginFile;       // cross-file origin (null = same file)

    // DX16: Scope isolation (from FindSymbolAtPosition)
    int ScopeDeclLine;       // governing VarDeclStmt line (0 = no scope isolation)
    int ScopeDeclCol;        // governing VarDeclStmt column
}
```

## 測試

| 測試 ID | 場景 | 斷言數 |
|---------|------|--------|
| DX17-01 | `wait_for(myVar)` — myVar 出現在 references 中 | 2 |
| DX17-02 | `wait_for(myVar)` — rename myVar 同步修改 wait_for 內的引用 | 2 |
| **合計** | | **4** |

加上 652 現有 LSP 測試作為回歸保障。

## 功能展望

- **SymbolAtPosition 消除**：目前 `SymbolAtPosition` 仍作為 `FindSymbolAtPosition` 的內部返回類型。DX18（統一引用收集）完成後，可考慮讓 `FindSymbolAtPosition` 直接返回 `ResolvedSymbol`，徹底消除 `SymbolAtPosition`。
- **關鍵字懸停遷移**：`HandleHover` 使用 `FindHoverText` 作為 fallback 處理 "func"/"struct"/"enum"/"var" 關鍵字上的 hover。可通過擴展 `FindSymbolAtPosition` 匹配關鍵字區域來遷移，但需評估對 go-to-definition/rename 的影響。

## 優化展望

- **單次查找**：`ResolveSymbol` 合併了雙 AST 解析 + 定義位置查找，消除了 `ResolveSymbolDualAst` 的二次查找。`HandleDefinition` 不再獨立調用 `FindDefinitionLocation`。
- **Hover 快速路徑**：大多數 hover 請求現在走 `ResolveSymbol` + `FormatHoverForSymbol` 快速路徑，只有關鍵字 hover 走 `FindHoverText` fallback。

## 風險點

| 風險 | 等級 | 說明 |
|------|------|------|
| FindHoverText fallback 遺留 | 低 | FindHoverText 作為 HandleHover 的 fallback 保留。功能完整但增加代碼量。DX18 或後續可消除。永久妥協原因：修改 FindSymbolAtPosition 匹配關鍵字可能影響其他 LSP 功能的行為。 |
| SymbolAtPosition 內部殘留 | 低 | SymbolAtPosition 仍作為 FindSymbolAtPosition 的返回類型。不影響外部介面，DX18 可一併消除。 |
