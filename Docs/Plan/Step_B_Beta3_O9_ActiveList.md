# B-β3: O9 活跃实例链表（Active Instance List）

> **目标**：用活跃实例索引列表替代 Tick() 的全量 128 slot 遍历，稀疏场景性能提升 ~85%。

## 一、背景

**现状**：`VMWorld.Tick()` 每帧遍历全部 `MaxInstances=128` 个 slot，
即使只有 3 个实例存活也循环 128 次。

**方案**：在 `InstancePool` 中维护预分配的 `int[] ActiveList + int ActiveListCount`。
- `Allocate()` 时追加（O(1)）
- `Free()` 时 swap-remove（O(1)）
- `Tick()` 只遍历 `ActiveListCount` 个

## 二、子任务清单

| # | 子任务 | 说明 | 状态 |
|---|--------|------|------|
| 1 | InstancePool 添加 ActiveList 字段 | `int[] ActiveList` + `int ActiveListCount`，Init() 中预分配 | ⏳ |
| 2 | VMInstanceState 添加 ActiveListIndex | 记录实例在 ActiveList 中的位置，swap-remove 需要 | ⏳ |
| 3 | InstancePool.Allocate() 追加到 ActiveList | 末尾追加 + 设置 ActiveListIndex | ⏳ |
| 4 | InstancePool.Free() swap-remove | 与末尾交换 + 更新被交换实例的 ActiveListIndex | ⏳ |
| 5 | VMWorld.Tick() 改为遍历 ActiveList | `for (int i = 0; i < Pool.ActiveListCount; i++)` | ⏳ |
| 6 | Snapshot Save/Load 包含 ActiveList | Array.Copy ActiveList + ActiveListCount + 各实例 ActiveListIndex | ⏳ |
| 7 | 性能测试：稀疏场景验证 | 3 active / 128 total，验证 Tick 只访问活跃实例 | ⏳ |
| 8 | 正确性测试：spawn/destroy/rollback | ActiveList 一致性 + rollback 后确定性 | ⏳ |
| 9 | 全量回归 | 700+ assertions × 2 模式全通过 | ⏳ |

## 三、设计细节

### 3.1 数据结构

```
InstancePool:
  int[] ActiveList       // 预分配 MaxInstances，存放活跃实例 ID
  int   ActiveListCount  // 当前活跃数量

VMInstanceState:
  int ActiveListIndex    // 该实例在 ActiveList 中的下标（-1 = 不活跃）
```

### 3.2 Allocate

```
ActiveList[ActiveListCount] = slot
inst.ActiveListIndex = ActiveListCount
ActiveListCount++
```

### 3.3 Free (swap-remove)

```
int idx = inst.ActiveListIndex
int last = ActiveListCount - 1
if (idx != last) {
    int movedId = ActiveList[last]
    ActiveList[idx] = movedId
    Instances[movedId].ActiveListIndex = idx
}
ActiveListCount--
inst.ActiveListIndex = -1
```

### 3.4 Tick

```
for (int i = 0; i < Pool.ActiveListCount; i++) {
    int id = Pool.ActiveList[i];
    ref VMInstanceState inst = ref Pool.Instances[id];
    // ... 原有逻辑
}
```

### 3.5 Snapshot

SaveState: `Array.Copy(ActiveList, snap.ActiveListData, MaxInstances)`
+ 存储 `ActiveListCount`
LoadState: 反向恢复 + 各实例的 `ActiveListIndex` 已包含在 Instances memcpy 中

### 3.6 妥协点

**无妥协**。方案完全向后兼容，零额外分配，复杂度低。

## 四、完成条件

1. ✅ Tick() 使用 ActiveList 遍历替代全量扫描
2. ✅ Snapshot Save/Load 包含 ActiveList 状态
3. ✅ 稀疏场景性能测试通过
4. ✅ 711 assertions × 2 模式全通过

## 五、功能展望

| ID | 内容 | 触发时机 |
|----|------|----------|
| **O10** | 快照只拷贝活跃实例（B-δ1） | ActiveList 已就绪，SaveState 可按 ActiveList 遍历拷贝，减少 80-90% 快照数据量 |

## 六、优化展望

| ID | 内容 | 说明 |
|----|------|------|
| O9+ | Tick 中 Completed 实例自动移出 ActiveList | 当前 Completed 实例仍在 ActiveList 中（由 Tick 跳过）。可在 Tick 中检测 Completed 后自动 swap-remove，进一步减少无效遍历。需注意与 DestroyInstance 的交互。 |

## 七、风险点

| 风险 | 说明 | 缓解措施 |
|------|------|----------|
| R-O9-1 | swap-remove 改变遍历顺序 | Tick 内不做 swap-remove，仅 spawn/destroy API 触发。Tick 遍历顺序 = 最近 spawn 顺序，语义上无保证。Snapshot 恢复确定性由 Array.Copy 保证。 |
| R-O9-2 | 并发 Spawn/Destroy 在 Tick 中 | 当前设计单线程，Tick 内不调用 Spawn/Destroy。若未来支持 Tick 内 spawn（如 Syscall 中），需在 Tick 循环后追加新实例。 |

