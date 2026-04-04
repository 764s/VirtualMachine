# B-δ1 O10 快照只拷贝活跃实例

> **状态**: ✅ 完成（2026-04-05）
> **依赖**: B-β3 (O9 活跃实例链表) ✅
> **测试**: 929 项 Assert × 2 模式全通过（float + Fix64）

## 一、目标

将 `SnapshotRingBuffer.SaveState/LoadState` 从全量拷贝 128 个 `VMInstanceState`（~92 KB）改为仅拷贝 `ActiveList` 中的活跃实例（典型 3-10 个，~2-7 KB），实现 **80-96% 快照实例数据量减少**。

## 二、设计

### 2.1 SaveState 变更

**Before (O9)**:
```csharp
Array.Copy(pool.Instances, snap.InstanceSnapshots, 128); // 全量 ~92KB
```

**After (O10)**:
```csharp
for (int i = 0; i < pool.ActiveListCount; i++)
{
    int id = pool.ActiveList[i];
    snap.InstanceSnapshots[id] = pool.Instances[id];
}
```

仅按 ActiveList 索引逐实例拷贝。快照数组仍保持 128 槽预分配（零运行时分配），未使用槽位含陈旧数据但永不被读取。

### 2.2 LoadState 变更

核心新增：**先清 IsAlive 防止幽灵实例**。

```csharp
// 1. 清除所有实例的 IsAlive（128 byte writes，忽略不计的开销）
for (int j = 0; j < 128; j++)
    pool.Instances[j].IsAlive = false;

// 2. 恢复 FreeStack + ActiveList（int[] 全量拷贝，共 1KB）
// 3. 仅恢复快照中活跃的实例
for (int j = 0; j < snap.ActiveListCount; j++)
{
    int id = snap.ActiveListData[j];
    pool.Instances[id] = snap.InstanceSnapshots[id];
}
```

### 2.3 幽灵实例问题

**场景**：快照保存时 3 个活跃实例 → 后续 spawn 新实例 → rollback。

O10 之前的全量拷贝自然覆盖了所有 128 槽，不存在此问题。O10 仅拷贝活跃实例，因此 rollback 后新 spawn 的实例仍保持 `IsAlive = true`（陈旧状态）。

**解决方案**：LoadState 开头先对所有 128 槽设置 `IsAlive = false`（128 byte writes），再仅恢复快照活跃实例。这保证了：
- 活跃实例正确恢复
- 幽灵实例被标记为死亡
- Tick 仅遍历 ActiveList，不会触碰幽灵槽位
- Allocate 对死亡槽位做 `inst = default`，完全清除陈旧数据

## 三、妥协

| 妥协 | 原因 | 类型 |
|------|------|------|
| FreeStack/ActiveList 仍全量拷贝（int[128] × 2 = 1KB） | 它们仅 512B 各自，优化收益不及复杂度成本 | 永久 |
| Snapshot 预分配数组仍 128 槽 | 零运行时分配原则，避免动态大小数组 | 永久 |

## 四、测试清单

| ID | 测试 | 验证点 |
|----|------|--------|
| P12 | Save/Load 正确性 | 5 个活跃实例寄存器值 + ActiveListIndex 正确还原 |
| P13 | 幽灵实例失效 | rollback 后 post-snapshot spawn 的实例 IsAlive = false |
| P14 | 0 活跃边界 | 空池 save/load 不崩溃 |
| P15 | 性能基准 | 5/128 活跃时 save ~0.12µs, load ~0.37µs |
| P16 | rollback 后 Tick | 仅原始 3 实例被 tick，extra 实例不执行 |

## 五、性能数据

| 指标 | Before (全量) | After (O10, 5/128) | 减少 |
|------|--------------|---------------------|------|
| SaveState 实例拷贝量 | 128 × ~740B = ~92KB | 5 × ~740B = ~3.7KB | **96%** |
| LoadState 实例拷贝量 | ~92KB | ~3.7KB + 128B clear | **96%** |
| SaveState 延迟 | — | ~0.12 µs | — |
| LoadState 延迟 | — | ~0.37 µs | — |

## 六、功能展望

无。O10 为终态优化，无进一步功能扩展。

## 七、优化展望

| ID | 内容 | 收益 | 复杂度 |
|----|------|------|--------|
| O10b | FreeStack/ActiveList 部分拷贝 | 再减 ~1KB/snapshot（FreeStack 仅 FreeTop 条目，ActiveList 仅 ActiveListCount 条目） | 低，但绝对收益极小 |

## 八、风险点

| 风险 | 影响 | 当前状态 |
|------|------|----------|
| 幽灵实例通过直接 `pool.Instances[id]` 访问（绕过 ActiveList / IsAlive 检查） | IsAlive 为 false 但其他字段含陈旧数据，可能导致逻辑错误 | ✅ 安全 — Tick 仅遍历 ActiveList，Allocate 做 `inst = default`，无直接裸访问路径 |
