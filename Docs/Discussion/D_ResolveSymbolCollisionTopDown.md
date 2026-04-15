# ResolveSymbol 同位置信息冲突：长期测试保留与顶层修复路径

> **状态**：✅ 已完成讨论  
> **日期**：2026-04-15  
> **来源**：DX17/DX18 收敛后新增验证用例 `VERIFY-01`（include/main 同 line+col 变量碰撞）

---

## 一、问题陈述

当前 `ResolveSymbol` 采用 per-file AST + merged AST 的双路径定位。  
在变量符号上，merged fallback 可能覆盖 per-file 命中，导致同 line/col 碰撞时跳到 include 文件定义。

这类问题不是单点逻辑错误，而是“候选选择策略”未在架构层统一定义。

---

## 二、长期测试要求（先锁行为）

- 保留 `VERIFY-01` 作为长期回归测试，不降级为临时测试。
- 该测试语义：当当前文件已命中局部变量时，definition 必须优先返回当前文件局部声明。
- 后续修复必须在不移除该用例的前提下推进。

---

## 三、顶层修复原则（自上而下）

1. **符号归属优先级统一**  
   同文件语义命中 > 跨文件位置碰撞命中。

2. **ResolveSymbol 成为唯一候选仲裁入口**  
   Definition/References/Rename/Hover 仅消费其结果，不再分散追加各自补丁逻辑。

3. **作用域身份参与候选决策**  
   对 Variable/Parameter 等高冲突符号，必须以作用域声明身份（函数+声明位置）约束候选替换。

4. **跨文件 fallback 仅在“可证明必要”时触发**  
   per-file 未命中或语义信息缺失时，merged 候选才可接管。

---

## 四、目标结构

- 将“硬编码分支覆盖”升级为“候选收集 + 统一评分/仲裁策略”。
- 评分维度至少包含：
  - 文件一致性（当前文件优先）
  - 作用域一致性（decl identity 匹配）
  - 符号类型完整性（如 StructField parentName 完整度）
  - 位置匹配置信度
- `ResolveSymbol` 输出保持统一结构，供全部 Handle* 复用。

---

## 五、行动项（提交串行需求）

| 需求 | 名称 | 内容 | 优先级 | 前置 |
|------|------|------|--------|------|
| DX19 | ResolveSymbol 候选仲裁修复 | 保留长期测试 VERIFY-01；将 ResolveSymbol 从“变量强制 merged fallback”改为“统一候选仲裁”；确保 Definition/References/Rename/Hover 一致语义 | ⭐⭐⭐ | DX18 ✅ |

