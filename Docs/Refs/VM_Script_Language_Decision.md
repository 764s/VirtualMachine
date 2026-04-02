# 脚本语言选型决策：为什么使用自定义 DSL

> 本文记录为什么最终选择自定义 DSL 而非嵌入现有语言子集，并包含 AI 友好性的专项分析。
>
> 相关文档：[VM_Summary.md](../VM_Summary.md)（总览）

---

## 1. 前置约束回顾

任何候选语言方案都必须满足以下 VM 硬约束，否则一票否决：

| 约束 | 含义 |
|------|------|
| 定长寄存器 + ECS 纯值 RAM | 不能有动态数组、闭包、GC 对象 |
| 零 GC | 运行期不能产生托管堆分配 |
| memcpy 快照/回滚 | 所有运行状态必须可整体拷贝恢复 |
| `wait` 一等语义 | 挂起必须编译为显式状态（IP + WaitCounter），不能依赖宿主协程 |
| Syscall 边界 | 与宿主交互只能走 Syscall Table，不能隐式调用 |
| `using`/`defer` Cleanup | 强制中断前必须可靠执行清理 |
| Fix64 确定性 | 全程定点数，帧同步安全 |
| ROM/RAM 分离 | 字节码只读，运行状态独立 |

---

## 2. 候选方案对比

### A. 自定义 DSL（当前选择）

语法风格借鉴 Go：`func`、`var x: type`、`defer`、`if/for`、`{}`，关键字约 12 个，完整 BNF ~80 行。

| 维度 | 评价 |
|------|------|
| VM 约束贴合度 | ⭐⭐⭐⭐⭐ 语法直接为 VM 物理法则量身定制 |
| 编译器复杂度 | ~800 行手写递归下降，无泛型/继承/闭包等复杂推导 |
| 编辑器投影 | AST 完全受控，流程图投影无歧义 |
| 运行期安全 | 编译器保证不可能生成违规字节码 |
| AI zero-shot 生成率 | 中等。没有公开训练数据，需提供 spec + 示例 |
| AI few-shot 生成率 | 高。给定完整 spec + 示例后，GPT-4+ 级模型可稳定生成 |
| AI 误用率 | 极低。没有"坏习惯"，只能用合法构造 |

### B. Lua 子集

只用 `local`、`function`、`if/while/for`、`return`，禁用 table/string/coroutine/metatable。

| 维度 | 评价 |
|------|------|
| VM 约束贴合度 | ⭐⭐ Lua 原生有 table（动态）、string（GC）、coroutine（宿主栈），全部需禁用 |
| AI zero-shot | 极高。所有主流 LLM 对 Lua 训练数据充足 |
| AI 误用风险 | **极高**。AI 会本能使用 `table.insert`、`string.format`、`coroutine.yield`，全部违规 |
| `wait` 语义 | Lua 的 `coroutine.yield` 依赖宿主 C 栈，不满足 memcpy 快照，需自创关键字 |
| `using`/`defer` | Lua 无此语法，需要魔改，此时已不再是 Lua |
| 类型系统 | 动态类型，无编译期寄存器分配/类型检查 |
| 编译器 | 需自写 Lua 子集→字节码编译器，且需处理大量合法 Lua 但对 VM 非法的语法并报清晰错误，比自定义 DSL 更复杂 |

### C. TypeScript 子集

只用 `let/const`、`function`、`if/while/for`、`return`、纯数值类型，禁用 object/array/class/async-await/string/模板。

| 维度 | 评价 |
|------|------|
| VM 约束贴合度 | ⭐⭐ TS 原生有 object/array/string/Promise/closure，全部违规 |
| AI zero-shot | 极高。TS/JS 是 LLM 训练最充分的语言 |
| AI 误用风险 | **极高**。AI 会使用 `[]`、`{}`、`async/await`、模板字符串、箭头函数 |
| `wait` 语义 | TS 的 `await` 是 Promise-based，与帧等待完全不同，用 `await wait(5)` 会误导 AI |
| `using`/`defer` | TS 5.2 有 `using`，但语义是 Disposable（GC 依赖），不匹配 |
| 编译器 | TS 子集→自定义字节码的编译器比自定义 DSL 更复杂 |

### D. Python 子集

只用 `def`、`if/while/for`、`return`，禁用 list/dict/class/import/string/f-string/comprehension。

| 维度 | 评价 |
|------|------|
| AI zero-shot | 极高 |
| AI 误用风险 | **极高**。AI 会用 list/dict/class/decorator |
| 缩进敏感 | Parser 更脆弱，编辑器流程图投影依赖 AST |
| 类型系统 | 动态类型（type hint 非强制），不适合编译期寄存器分配 |
| `wait`/`defer` | 无原生概念 |

### E. Go 超小子集

只用 `func`、`var`、`if/for`、`return`、`defer`、简单类型标注。

| 维度 | 评价 |
|------|------|
| VM 约束贴合度 | ⭐⭐⭐⭐ `defer` 原生匹配 Cleanup 语义，静态类型适配寄存器分配 |
| AI zero-shot | 高。Go 训练数据充足 |
| AI 误用风险 | 中等。AI 可能用 slice/map/goroutine/channel，但这些关键字容易用编译器报错拦截 |
| `wait` 语义 | Go 无原生 `wait`，需自加 |
| `using` | Go 无 `using`，但已有 `defer`，`using` 可作为语法糖自加 |
| 编译器 | ~1200 行，比自定义 DSL 稍复杂 |

---

## 3. 综合对比矩阵

| 维度 | 自定义 DSL | Lua 子集 | TS 子集 | Python 子集 | Go 子集 |
|------|-----------|----------|---------|------------|---------|
| **VM 约束贴合** | ⭐⭐⭐⭐⭐ | ⭐⭐ | ⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ |
| **AI zero-shot** | ⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ |
| **AI few-shot**（给 spec + 示例后） | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| **AI 误用率** | ⭐⭐⭐⭐⭐(极低) | ⭐(极高) | ⭐(极高) | ⭐(极高) | ⭐⭐⭐(中) |
| **编译器复杂度** | 低(~800行) | 高(需拒绝大量合法Lua) | 高 | 高 | 中(~1200行) |
| **`wait` 天然支持** | ✅(自带) | ❌(需魔改) | ❌(await 语义不同) | ❌(需自造) | ❌(需自加) |
| **`defer` 天然支持** | ✅ | ❌ | ❌ | ❌ | ✅ |
| **`using` 可扩展** | ✅ | ❌ | 语义冲突 | ❌ | ✅(语法糖) |
| **编辑器投影** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐ | ⭐⭐⭐⭐ |

---

## 4. 核心洞察：AI 友好性的关键不是"AI 认识这门语言"

> **AI 误用率 >> AI 识别率**

对于受限 VM 场景：

- 使用一门 AI 熟悉的通用语言 → AI 会大量使用该语言中**在 VM 里违规**的特性 → 编译错误多、修正成本高
- 使用自定义 DSL + 完整 spec → AI 没有"坏习惯"，只能用提供的合法构造 → 错误率低

**类比**：给 AI 一个只有 12 个关键字的 DSL 和完整手册，比给它一个有 500 个特性但只允许用其中 15 个的通用语言，效果更好。

---

## 5. 最终决策

**选择自定义 DSL，语法风格借鉴 Go。**

理由：

1. **约束完美贴合**：语法直接为 VM 物理法则设计，`wait`/`defer`/`using` 均为一等关键字
2. **AI 误用率最低**：语法空间极小，AI 无法生成违规代码（不存在 table/array/class/closure 等关键字）
3. **编译器最简**：~800 行递归下降，无需处理"合法但被禁"的语法分支
4. **编辑器投影最稳**：AST 完全受控，每个节点都有明确的字节码映射

语法借鉴 Go 而非完全自造的理由：

- `func`、`var`、`defer`、`if/for`、`{}` 等关键字在 AI 训练数据中大量存在
- AI 看到类 Go 语法会自然跟随，不会产生 table/object/array 等噪声
- 不发明陌生关键字（不用 `proc` 替代 `func`、不用 `emit` 替代 `return`），最大化利用 AI 先验知识

---

## 6. AI 友好性最大化策略（落地要求）

1. **提供机器可读 spec**：
   - `grammar.ebnf`（~80 行完整 BNF）
   - `syscall_manifest.json`（所有 Syscall 签名 + 参数类型 + cleanup 标记）
   - `examples/`（20-30 个覆盖性示例脚本）

2. **AI prompt 模板**：固定 200-300 token 的系统 prompt 注入语法规则 + Syscall 表

3. **编译器错误信息**：必须对 AI 友好，包含修正建议（如 `"数组不受支持，请使用 Syscall 处理批量数据"）`

4. **不做的事**：
   - 不使用 Lua/TS/Python 子集（AI 的"坏习惯"会极大增加非法代码生成率）
   - 不发明完全陌生的关键字（白白降低 AI 先验知识利用率）

---

## 7. 示例脚本（目标语法风格预览）

```
module fireball

func main() {
    using SetBlackboard(BB_CASTING, 1)   // 编译器自动生成 cleanup
    wait 20                               // 前摇 20 帧

    var target: int = Syscall.FindNearestEnemy()
    if target > 0 {
        defer { Syscall.StopVFX(vfx) }   // 手动 cleanup 逃生舱
        var vfx: int = Syscall.PlayVFX(VFX_FIREBALL, target)
        Syscall.DealDamage(target, 50)
        wait 10                           // 后摇
    }
}
```

关键字清单（~12 个）：`module`、`import`、`func`、`var`、`if`、`for`、`while`、`return`、`wait`、`wait_for`、`yield`、`defer`、`using`
