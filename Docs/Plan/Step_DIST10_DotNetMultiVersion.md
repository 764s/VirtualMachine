# DIST-10：.NET 多版本兼容策略

> **状态**：✅ 已完成（2026-04-06）
> **前置**：DIST-1（✅ 已完成）
> **详细设计**：[Discussion/Step_DIST_Distribution.md §七](../Discussion/Step_DIST_Distribution.md)
> **KOF98 覆盖验证**：同上 §7.6

---

## 完成条件

1. ✅ `dotnet build src/FFVM/FFVM.csproj` 双目标（`netstandard2.1` + `net8.0`）成功
2. ✅ `dotnet pack src/FFVM/FFVM.csproj` 产生的 nupkg 包含两个 TFM 程序集（`lib/net8.0/FFVM.dll` + `lib/netstandard2.1/FFVM.dll`）
3. ✅ `FFVM.Cli.csproj` 配置 `RollForward=LatestMajor`
4. ✅ StandaloneRunner 现有测试全通过（1007 Assert）
5. ✅ KOF98 构建通过（ProjectReference 到双目标 FFVM）

---

## 实施记录

### T1：FFVM.csproj 双目标 + 条件编译 ✅

- `<TargetFramework>` → `<TargetFrameworks>netstandard2.1;net8.0</TargetFrameworks>`
- 追加 `<PropertyGroup Condition="netstandard2.1">` 定义 `FFVM_LEGACY_CSHARP`
- 两个目标均编译成功

### T2：VMWorld.cs AggressiveOptimization 条件编译 ✅

- `#if !FFVM_LEGACY_CSHARP` 包裹 `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`
- netstandard2.1 目标编译通过（DIST-10 讨论 §7.2 审计表中此项原为"✅ 无需处理"，实测为 ❌ 需条件编译，已修正）

### T3：FFVM.Cli.csproj RollForward ✅

- 追加 `<RollForward>LatestMajor</RollForward>`

### T4：构建验证 ✅

- `dotnet build src/FFVM/FFVM.csproj -c Release` — 双目标 0 Error
- `dotnet pack` — nupkg 含 `lib/net8.0/FFVM.dll`（150KB）+ `lib/netstandard2.1/FFVM.dll`（148KB）
- StandaloneRunner — 1007 Assert 全通过（112 TW + 506 Compiler + 44 Perf + 18 FFScript + 51 Debug + 97 DAP + 179 LSP）
- KOF98 — 构建成功（ProjectReference 自动选择 net8.0 目标）
- FFVM.Cli — 构建成功

---

## 妥协

无永久妥协。`netstandard2.1` 目标不含 `AggressiveOptimization` JIT 提示，但此属性在 net8.0 目标中保留，NuGet 消费者使用 net8.0+ 运行时时自动获得优化版本。
