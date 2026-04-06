# 曳光弹：范围、目标与验证

> **状态**：✅ 已完成讨论 — 曳光弹已通过 Phase A + Phase B 全部验证，后续阶段建立在此基础之上。
>
> **来源**：从 [VM_Summary.md](../VM_Summary.md) §四抽取。原始详细设计讨论见 [VM_Tracer_Bullet.md](VM_Tracer_Bullet.md)。

---

## 1. 曳光弹的业务定义

```
skill TracerBullet
{
    defer {
        SetBlackboard(self, CastingState, 0)
    }

    SetBlackboard(self, CastingState, 1)
    wait 10
    PlayEffect(self, Fx_SimpleCast)
}
```

## 2. 为什么选这个最小业务

一颗曳光弹同时覆盖：
- `wait`（一等挂起语义）
- ROM/RAM 分离
- ECS 组件化状态
- Cleanup 机制（`defer`）
- Syscall 边界
- Save/Load
- 0 GC

## 3. 验证分两阶段

**Phase A（曳光弹成立门槛，必须全部通过）：**

1. `VMInstanceState` 是纯值类型，无托管字段
2. `wait 10` 正确挂起/恢复
3. 恢复后仅执行一次 `PlayEffect`
4. 正常结束走 Cleanup（`CastingState` → 0）
5. 强制 Kill 时 Cleanup 仍然执行（`CastingState` → 0、`PlayEffect` 不触发）
6. `Killed` 优先级高于 `WaitFrames > 0`

**Phase B（Phase A 通过后立即验证，不阻塞闭环）：**

7. Save/Load 后执行行为一致
8. 全流程零 GC

## 4. OpCode 集

### Phase 1：曳光弹核心（7 条）

| OpCode | 职责 |
|--------|------|
| `NOP` | 占位/调试对齐/回填辅助 |
| `LOAD_CONST` | 将 ROM 常量装入寄存器 |
| `SYSCALL` | 唯一宿主交互入口 |
| `WAIT` | 唯一挂起入口 |
| `PUSH_CLEANUP` | 注册 Cleanup 入口 IP |
| `POP_CLEANUP` | 正常离开作用域时注销 Cleanup |
| `RETURN` | 结束/Cleanup 驱动切换点 |

### Phase 2：Step 5 扩展（19 条）

| OpCode | 职责 |
|--------|------|
| `MOVE` | 寄存器间复制 Reg[A] = Reg[B] |
| `JUMP` | 无条件跳转 IP = A |
| `JUMP_IF_ZERO` | 条件跳转：Reg[B] == 0 → IP = A |
| `JUMP_IF_NOT_ZERO` | 条件跳转：Reg[B] != 0 → IP = A |
| `ADD/SUB/MUL/DIV/MOD` | 算术运算 Reg[A] = Reg[B] op Reg[C] |
| `CMP_EQ/NEQ/LT/LTE/GT/GTE` | 比较运算 → 0 或 1 |
| `AND/OR` | 布尔运算（非零为真） |
| `NOT` | 逻辑取反 |
| `NEG` | 算术取负 |

### Phase 3：Step 8 函数调用（2 条）

| OpCode | 职责 |
|--------|------|
| `CALL` | A=目标函数入口 IP, B=callerWindowSize → 压入 CallFrame + 寄存器窗口偏移 + jump |
| `RET_FUNC` | 弹出 CallFrame → 恢复 IP + RegisterBase → 返回 caller |

### Phase 4：结构体优化 + 叶函数（3 条）

| OpCode | 职责 |
|--------|------|
| `CALL_LEAF` | 叶函数优化调用（跳过 CallFrame push/pop） |
| `RET_LEAF` | 叶函数返回（从 inst 字段恢复） |
| `COPY_BLOCK` | A=dest, B=src, C=count → 批量寄存器拷贝（≥3 字段结构体赋值） |

## 5. 示意字节码

```
 0: PUSH_CLEANUP 8              // defer 块入口 → IP 8
 1: LOAD_CONST r3, Const_One    // 装入 1
 2: SYSCALL SetBlackboard       // SetBlackboard(self=r0, key=r1, value=r3)
 3: WAIT 10                     // 挂起 10 帧
 4: LOAD_CONST r2, Const_Fx     // 装入特效 ID
 5: SYSCALL PlayEffect          // PlayEffect(self=r0, effect=r2)
 6: RETURN                      // 正常结束 → 触发 Cleanup
 7: NOP                         // 对齐
 8: LOAD_CONST r3, Const_Zero   // Cleanup 块：装入 0
 9: SYSCALL SetBlackboard       // SetBlackboard(self=r0, key=r1, value=r3)
10: RETURN                      // Cleanup 结束
```

## 6. 验证门禁与通过条件

以下验证项为整个 VM 工程的必过门禁。每项均可模块化执行，不需要完整环境，可分阶段插入推进顺序中，只需最终全部通过即可。

### V1: GC 精确验证 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 确认字节码 Tick 循环内零托管堆分配 |
| 前置 | 曳光弹通过（字节码路径已可运行） |
| 方法 | 使用 `GC.GetAllocatedBytesForCurrentThread()` 精确测量当前线程分配。50 轮预热后，10 个活跃实例执行 SYSCALL + WAIT + Cleanup，连续 100 Tick，断言 0 bytes 分配 |
| 通过条件 | 预热后连续 100 Tick，`GC.Alloc` = 0 bytes（Syscall 注册、VMProgram 构造等预热期分配不计） |
| 结果 | ✅ Test 27 通过：100 ticks alloc = 0 bytes |

### V2: 回滚正确性验证 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 确认 Save/Load 后执行行为与未中断运行 bit-exact 一致 |
| 前置 | 曳光弹通过 |
| 方法 | 跑 50 帧 → Save → 跑 50 帧（偶向）→ Load → 再跑 100 帧；对比两次从相同帧开始运行的完整 Syscall 调用序列 |
| 通过条件 | Syscall 调用序列完全一致，最终 StateFlags 完全一致 |
| 结果 | ✅ Test 28 通过：syscall sequence bit-exact，StateFlags match |

### V3: 单实例性能基准 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 摩清 VM 字节码解释与等价宿主 C# 逻辑的性能差距倍率 |
| 前置 | MOVE/JUMP 补完（否则指令序列不具代表性） |
| 方法 | 同一段逻辑（循环 + 分支 + 算术 + Syscall）分别用 VM 字节码和纯 C# 实现，`Stopwatch` 跑 N 轮取平均，输出倍率 |
| 通过条件 | 记录倍率即可（参考值：解释器通常 10-30x，超过 50x 需排查） |
| 结果 | ✅ Test 40 通过：VM = 5913 µs, C# = 1575 µs, ratio = 3.8x（远优于 50x 上限） |

### V4: N 实例吞吐上限 — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 找到帧预算内的最大并发 VM 实例数 |
| 前置 | 同 V3 |
| 方法 | 从 128 → 256 → 512 → 1024 逐级加实例数，每轮跑固定 Tick 数，记录耗时曲线 |
| 通过条件 | 128 实例 × 50 条指令/Tick 总耗时 < 1ms（在目标硬件上） |
| 结果 | ✅ Test 41 通过：128 实例 = 0.391ms, 256 = 0.762ms, 512 = 0.883ms, 1024 = 1.961ms（近线性扩展，128 实例远低于 1ms） |

### V3-B: 编译脚本性能基准（Compiled Script Benchmark） — ✅ 通过

| 项目 | 内容 |
|------|------|
| 目的 | 测量编译器生成的字节码（文本脚本 → 编译 → 执行）与等价 C# 逻辑的性能差距 |
| 前置 | Lexer + Parser + BytecodeCompiler 完成 |
| 区别于 V3 | V3 使用手写/手动构造的字节码（代表 VM 解释器开销下限）；V3-B 使用编译器从文本脚本生成的字节码（代表实际开发者编写脚本时的真实性能） |
| 方法 | 5 组相同逻辑的 FFVM 脚本和 C# 代码（均使用 `Number` 结构体），20 轮预热 + 200 轮测量取平均，`Stopwatch` 计时 |
| 通过条件 | 所有 5 组值匹配（VM 结果 == C# 结果），记录倍率 |
| 自动化 | `run-benchmarks.cmd` 一键执行，输出机器可解析格式，自动生成 `benchmarks/benchmark_results.md` 报告 |
| Unity 运行 | 菜单 `TestVM → RunBenchmarks`，结果输出到 Unity Console |

**5 组基准测试：**

| 编号 | 名称 | 逻辑 | 规模 | 指令数 |
|------|------|------|------|--------|
| B01 | ArithLoop | 算术 + 取模 + 分支（每 3 次调 Syscall） | 10,000 轮 | 32 |
| B02 | Fibonacci | 迭代斐波那契（swap 循环） | fib(25) | 20 |
| B03 | NestedLoop | O(n²) 嵌套循环 + 乘法累加 | 100×100 | 26 |
| B04 | Branching | 4 路 if/else-if 分支链 | 10,000 轮 | 41 |
| B05 | Accumulator | 纯 ADD 累加（最小开销基准线） | 50,000 轮 | 16 |

**最新结果（Release，.NET 6.0，20 核）：**

| Benchmark | VM (µs) | C# (µs) | Ratio |
|-----------|---------|---------|-------|
| B01_ArithLoop | 540.7 | 78.7 | **6.87x** |
| B02_Fibonacci | 0.6 | 0.1 | **6.05x** |
| B03_NestedLoop | 189.9 | 32.4 | **5.86x** |
| B04_Branching | 463.9 | 81.5 | **5.69x** |
| B05_Accumulator | 819.0 | 163.8 | **5.00x** |

**性能分析：**

- **编译脚本 5-7x** vs **手写字节码 1.7x（V3）**：差距来自编译器生成的额外指令——`LOAD_CONST`、`MOVE`、寄存器间搬运、表达式临时寄存器分配等。手写字节码可以最优化寄存器使用，而编译器为通用性牺牲了部分效率。
- **两个数字都有意义**：1.7x 代表 VM 解释器本身的开销下限（天花板不高）；5-7x 代表开发者写脚本时的真实感受。
- **对比其他嵌入式脚本引擎**：Lua 5.4 解释器通常 20-40x，MoonSharp 50-100x，xLua 10-30x。FFVM 编译脚本的 5-7x 已优于多数通用脚本方案。
- **绝对值视角**：50K 次纯累加（B05）约 820µs，单帧预算 16.6ms（60fps），脚本开销占比极低。

### V5: 帧内 Profiler 验证（真实 Syscall 接入后） — ⚪ 待前置

| 项目 | 内容 |
|------|------|
| 目的 | 确认含 ECS 交互开销的真实 Tick 耗时在帧预算内 |
| 前置 | 真实 Syscall 接入 ECS（技能释放、子弹生成等） |
| 方法 | Unity Profiler Timeline 观察 VM Tick marker，确认 GC.Alloc = 0 且帧耗时可接受 |
| 通过条件 | 帧预算内 GC.Alloc = 0，总耗时在可接受范围 |
| 最早可做 | 第一个真实技能脚本在场景中运行时 |
| 必须通过 | 进入步骤 10（编辑器 UI）前 |
