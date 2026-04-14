# DX13: 参数 LSP 完整支持

## 目标

修复 LSP 审查（D17 D_LspUsabilityAudit）发现的两个参数相关已知限制：
- **KL-01**：`textDocument/references` 在参数上仅返回函数体内使用，不含参数声明位置
- **KL-02**：`textDocument/rename` 对参数返回 null（不支持参数重命名）

同时完善参数相关的定义跳转精确性和签名悬停支持。

## 前置条件

| 前置 | 状态 |
|------|------|
| DX11 VFS + Rename 状态更新 | ✅ 已完成 |
| DX9 ParamDecl.NameLine/NameColumn 精确位置 | ✅ 已有 |
| DX12 LSP 可用性审查（KL-01~05 发现） | ✅ 已完成 |

## 完成条件

| # | 条件 | 状态 |
|---|------|------|
| ① | 参数引用包含声明位置（KL-01 修复） | ✅ |
| ② | 参数重命名支持（KL-02 修复） | ✅ |
| ③ | Go-to-definition 返回精确参数声明位置（NameLine/NameColumn） | ✅ |
| ④ | 参数签名位置悬停显示类型信息 | ✅ |
| ⑤ | 参数引用和重命名按函数作用域隔离 | ✅ |
| ⑥ | DX12-08/09 测试升级为正向断言 | ✅ |
| ⑦ | DX13-01~09 测试全部通过（16 asserts） | ✅ |
| ⑧ | 全部测试通过无回归 | ✅ |

## 子任务清单

### Phase 1: FindSymbolAtPosition 参数检测

- [x] 1.1 在函数签名遍历中检查 ParamDecl.NameLine/NameColumn 匹配光标位置
- [x] 1.2 返回 SymbolAtPosition { kind = Parameter, scopeFunc = func.Name }，携带作用域函数名

### Phase 2: CollectReferencesWithOrigin 参数分支（KL-01 修复）

- [x] 2.1 新增 `kind == SymbolKindTag.Parameter && scopeFunc != null` 分支
- [x] 2.2 在 scopeFunc 函数的 Parameters 中定位声明位置，加入 locations
- [x] 2.3 在 scopeFunc 函数的 Body 中 CollectIdentRefsInBlock 收集使用位置
- [x] 2.4 HandleReferences 传递 scopeFunc 参数到 CollectReferencesWithOrigin

### Phase 3: HandleRename 参数支持（KL-02 修复）

- [x] 3.1 HandleRename 中 CollectReferencesWithOrigin 传递 scopeFunc 参数
- [x] 3.2 参数引用列表（含声明 + 使用）全部生成重命名编辑

### Phase 4: FindDefinitionLocation 精确参数位置

- [x] 4.1 参数匹配时优先使用 p.NameLine/p.NameColumn（DX9 提供的精确位置）
- [x] 4.2 保留 func.Line/func.Column 作为 fallback（NameLine 不可用时）

### Phase 5: FindHoverText 签名参数悬停

- [x] 5.1 在函数遍历中优先检查参数声明位置（在 FindHoverInBlock 之前）
- [x] 5.2 匹配时返回 `(parameter) {FormatParamDecl(param)}` 格式悬停文本
- [x] 5.3 附加 DocComment（如有）

### Phase 6: 测试覆盖

- [x] 6.1 DX13-01: 参数引用包含声明位置（≥3: decl + 2 usages）— KL-01 验证
- [x] 6.2 DX13-02: 从声明位置发起参数重命名（≥3 edits）— KL-02 验证
- [x] 6.3 DX13-03: 从使用位置发起参数重命名（≥3 edits）
- [x] 6.4 DX13-04: 多函数同名参数引用按作用域隔离（exactly 2 refs）
- [x] 6.5 DX13-05: 多函数同名参数重命名按作用域隔离（exactly 2 edits）
- [x] 6.6 DX13-06: 参数使用位置 Go-to-definition 跳转到声明行
- [x] 6.7 DX13-07: 参数声明位置 Hover 显示类型信息
- [x] 6.8 DX13-08: 从声明位置发起参数引用（≥2 refs）
- [x] 6.9 DX13-09: 跨文件函数中的参数引用（≥3 refs）
- [x] 6.10 DX12-08 升级：参数引用 ≥3（含声明位置）
- [x] 6.11 DX12-09 升级：参数重命名返回 non-null 且 ≥3 edits

### Phase 7: 文档更新

- [x] 7.1 VM_Summary.md 更新 DX13 状态 ✅ + 测试总计
- [x] 7.2 Outlook_And_Risks.md 更新 DX13 状态 ✅
- [x] 7.3 D_LspUsabilityAudit.md 更新 KL-01/KL-02 状态为已修复

## 实现细节

### FindSymbolAtPosition 参数检测

在函数签名遍历中新增参数声明位置检查，利用 DX9 提供的 `ParamDecl.NameLine/NameColumn` 精确匹配：

```
foreach func in ast.Functions:
    foreach p in func.Parameters:
        if p.NameLine > 0 && p.NameLine == line && ColMatches(p.NameColumn, p.Name.Length, col):
            return { name=p.Name, kind=Parameter, scopeFunc=func.Name }
```

### CollectReferencesWithOrigin Parameter 分支

新增独立的 Parameter 分支，按 scopeFunc 限定搜索范围：

```
if kind == Parameter && scopeFunc != null:
    foreach func in ast.Functions:
        if func.Name == scopeFunc:
            funcUri = ResolveOriginUri(requestingUri, func.OriginFile)
            // 1. 声明位置
            foreach p in func.Parameters:
                if p.Name == name && p.NameLine > 0:
                    locations.Add(MakeLocation(funcUri, p.NameLine, p.NameColumn, name.Length))
            // 2. 函数体内使用
            if func.Body != null:
                CollectIdentRefsInBlock(func.Body, name, funcUri, locations)
            break
```

### FindDefinitionLocation 精确化

```
if p.Name == name:
    if p.NameLine > 0:
        return (p.NameLine, p.NameColumn, p.Name.Length, func.OriginFile)
    return (func.Line, func.Column, func.Name.Length, func.OriginFile)  // fallback
```

### FindHoverText 签名参数

函数遍历中优先检查参数声明位置（在遍历函数体之前），匹配时返回格式化参数信息。

## 实现统计

| 指标 | 值 |
|------|-----|
| LspServer.cs 变更 | ~63 行（Parameter 分支 + FindSymbolAtPosition 参数检测 + FindDefinitionLocation 精确化 + FindHoverText 签名参数） |
| LspTests.cs 新增 | ~287 行（DX13-01~09 新增 + DX12-08/09 升级断言） |
| 测试总计 | 2234（114 TW + 1302 Compiler + 44 Perf + 18 FFS + 51 Debug + 97 DAP + 608 LSP） |
| LSP 测试总计 | 608（590 existing + 18 DX13） |
| 无回归 | ✅ 全部 2234 测试通过 |

## 功能展望

| 展望 | 描述 | 触发条件 |
|------|------|----------|
| 参数类型推导悬停 | 当前 hover 依赖 FormatParamDecl 静态文本；未来可添加运行时类型推导信息 | 类型系统增强后 |
| 参数 CodeLens 引用计数 | 参数声明上方显示引用计数 lens | CodeLens 基础设施就绪后 |

## 优化展望

| 展望 | 描述 | 触发条件 |
|------|------|----------|
| 参数引用缓存 | CollectReferencesWithOrigin Parameter 分支每次全遍历函数体；可缓存解析结果 | 大函数场景性能问题出现时 |

## 风险点

| 风险 | 等级 | 说明 |
|------|------|------|
| 同名参数跨函数引用泄漏 | 低 | scopeFunc 隔离确保参数引用限定在声明函数内；DX13-04/05 测试覆盖该场景 |
| NameLine == 0 回退路径 | 低 | 旧 AST 或特殊场景下 NameLine 可能为 0，FindDefinitionLocation 有 fallback 到 func.Line |

---

> **补充说明**：本文件为补救性创建。DX13 实现已在 commit `ae8c97b` 完成，本文件回溯记录子计划结构以保持流程完整性。
