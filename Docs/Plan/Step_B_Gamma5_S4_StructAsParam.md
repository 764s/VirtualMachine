# B-γ5: S4 结构体作为函数参数

**状态**: ⏳ → 执行中  
**依赖**: B-γ1 (FO6 自适应寄存器窗口) ✅  
**完成条件**: 结构体参数寄存器传递 + R5 安全限制 + 测试通过

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
- 单个 struct 参数字段数 ≤ 4（保守限制，与 FO6 联合评估后可解除）

### 返回值

- 本步骤仅支持标量返回值（r0），struct 返回值暂不支持
- 妥协原因：返回值协议改动影响面大，且当前无编辑器展示需求
- 消除时间点：B-γ7 (SN1 嵌套 struct) 或后续需求时

---

## 子任务 Checklist

- [ ] P1: AnalyzeVariableLifetimes — struct 参数的 FieldCount 从 0 改为实际字段数
- [ ] P2: CompileFunction 参数绑定 — 支持 struct 参数（DeclareStructVar + multi-reg MOVE）
- [ ] P3: EmitUserCall 参数准备 — struct 参数展开到 scratch zone（multi-reg MOVE）
- [ ] P4: EmitUserCall 校验 — R5 安全限制（总寄存器 ≤ 16，单 struct ≤ 4 字段）
- [ ] P5: 测试 — CS12-CS20+ 覆盖各场景
- [ ] P6: 运行全部测试验证
- [ ] P7: 更新文档

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
| CS20 | struct 参数 + while 循环 | 循环中字段正确 |

---

## 风险点

| ID | 描述 | 缓解 |
|----|------|------|
| R5 | struct 参数加剧窗口压力 | ≤4 字段限制 + 编译报错 |
| NEW | CompileExpr(IdentifierExpr) 对 struct 变量返回 baseReg，需要在 EmitUserCall 识别为 struct | 通过 _structVarTypes 检查 |
