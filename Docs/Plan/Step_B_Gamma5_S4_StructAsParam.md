# B-γ5: S4 结构体作为函数参数

**状态**: ✅ 完成  
**依赖**: B-γ1 (FO6 自适应寄存器窗口) ✅  
**完成条件**: 结构体参数寄存器传递 + R5 安全限制 + 测试通过  
**结果**: 815 项 Assert × 2 模式全通过（+20 新测试 CS12-CS21）

---

## 设计概要

结构体参数在编译期拍平为连续寄存器。  
caller 将 struct 各字段展开到 scratch zone（r0..rN），callee 在 local 区还原。

### 寄存器传递协议

| 阶段 | 动作 |
|------|------|
| Caller 编译参数 | 将 struct 参数各字段 MOVE 到 scratch zone r0..rN（按参数顺序连续排列） |
| CALL 指令 | 建立新 register window |
| Callee 绑定参数 | 从 scratch zone r0..rN 拷贝到 local 区 r16+（struct 用 DeclareStructVar） |

### R5 安全限制

- scratch zone 最大 16 个寄存器（r0-r15）
- 所有参数（标量 1 + struct 按字段数）总计 ≤ 16
- 编译期校验，超出则报错

### 返回值

- 本步骤仅支持标量返回值（r0），struct 返回值暂不支持
- 妥协原因：返回值协议改动影响面大，且当前无编辑器展示需求
- 消除时间点：B-γ7 (SN1 嵌套 struct) 或后续需求时

---

## 子任务 Checklist

- [x] P1: AnalyzeVariableLifetimes — struct 参数的 FieldCount 从 0 改为实际字段数
- [x] P2: CompileFunction 参数绑定 — 支持 struct 参数（DeclareStructVar + multi-reg MOVE）
- [x] P3: EmitUserCall 参数准备 — struct 参数展开到 scratch zone（multi-reg MOVE）
- [x] P4: EmitUserCall 校验 — R5 安全限制（总寄存器 ≤ 16）
- [x] P5: 测试 — CS12-CS21 覆盖 10 个场景
- [x] P6: 运行全部测试验证（815 × 2 模式）
- [x] P7: 更新文档

---

## 测试计划

| ID | 场景 | 预期 |
|----|------|------|
| CS12 | 基本 struct 参数传递（Vec2 → 函数） | 正确读取字段 |
| CS13 | 多个 struct 参数 | 字段不冲突 |
| CS14 | struct + 标量混合参数 | 各参数正确绑定 |
| CS15 | struct 参数隔离（caller 不被修改） | caller 原始值不变 |
| CS16 | struct 参数 + 函数调用链 | 嵌套调用正确传递 |
| CS17 | struct 参数 + leaf 函数优化 | CALL_LEAF 正常工作 |
| CS18 | 错误：struct 参数超 16 寄存器 | 编译报错 |
| CS19 | 标量在前 struct 在后的混合参数 | scratch offset 正确 |
| CS20 | 3 字段 struct 参数（DamageInfo） | 字段正确传递 |
| CS21 | struct 参数 + while 循环 | 循环中字段正确 |

---

## 风险点（实施前评估 + 实施后状态）

| ID | 描述 | 状态 |
|----|------|------|
| R5 | struct 参数加剧窗口压力 — ≤16 scratch regs 总限制 | ✅ 编译期报错保护，FO6 后可解除 |
| — | CompileExpr(IdentifierExpr) 对 struct 变量返回 baseReg，需要在 EmitUserCall 识别为 struct | ✅ 通过 _structVarTypes 检查已解决 |

---

## 功能展望

| ID | 描述 | 触发时机 |
|----|------|----------|
| S4-F1 | struct 返回值（多寄存器 r0..rN 返回） | 编辑器需要展示 struct 返回值节点时 |
| S4-F2 | struct 字面量作为参数（不经过变量中转） | 语法糖需求出现时 |

## 优化展望

| ID | 描述 | 触发时机 |
|----|------|----------|
| S4-O1 | 消除 scratch→local 冗余 MOVE（当 struct 参数恰好对齐时） | 性能分析发现瓶颈时 |

## 妥协记录

| 项目 | 妥协内容 | 原因 | 消除时间点 |
|------|----------|------|------------|
| struct 返回值 | 仅支持标量返回（r0），不支持 struct 返回 | 返回值协议改动影响面大，当前无编辑器展示需求 | B-γ7 (SN1) 或后续需求时 |
| 单 struct 字段数限制 | 无编译期单 struct 字段数限制（仅总 scratch ≤16 限制） | FO6 已就位，窗口压力已缓解 | — |
