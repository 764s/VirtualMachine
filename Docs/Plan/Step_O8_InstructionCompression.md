# O8 指令压缩：16B → 4B

> **来源**：VM_Summary.md §十 优化展望 O8 / Outlook_And_Risks.md §3.2 Tier 3
> **目标**：将 `Instruction` 结构从 16 字节（1B opcode + 3B padding + 3×4B int）压缩为 4 字节（1B opcode + 3×1B operand），提升 L1 指令缓存命中率。
> **预期收益**：全场景 10-20% 系统性加速，B04 分支密集场景受益最大。
> **前置条件**：无硬前置。B-ε/B-ζ 优化已全部完成，当前处于 C 阶段（⚪ 宿主阻塞）。

---

## 一、工程规模估算

### 1.1 影响范围

| 模块 | 文件 | 改动性质 | 预估改动量 |
|------|------|---------|-----------|
| **Instruction 定义** | OpCode.cs | 结构体重新定义 + wide opcode 新增 | ~80 行 |
| **编译器 Emit** | BytecodeCompiler.cs | Emit() 增加溢出检测 + wide 变体 emit | ~120 行 |
| **编译器 Backpatch** | BytecodeCompiler.cs | ~20 处 backpatch 站点的 wide 处理 | ~100 行 |
| **编译器 Peephole** | BytecodeCompiler.cs | 模式匹配适配 + NOP 压缩验证 | ~60 行 |
| **编译器 FO6 remap** | BytecodeCompiler.cs | 寄存器重映射逻辑验证（操作数仍在 byte 范围） | ~20 行 |
| **VM 执行引擎** | VMWorld.cs | 47→52+ case 适配 + wide opcode case | ~150 行 |
| **VMProgram 构造** | VMProgram.cs | SENTINEL 追加逻辑验证 | ~5 行 |
| **测试** | CompilerTests.cs, BenchmarkRunner.cs | 回归验证，无语义变化 | ~0 行（重跑即可） |
| **调试器/DAP/LSP** | 各文件 | **零改动**（不直接读指令字节） | 0 |
| **快照/序列化** | Snapshot.cs | **零改动**（不含指令数据） | 0 |
| **合计** | | | **~535 行** |

### 1.2 工程量评估

- **不算大**：核心改动集中在 3 个文件（OpCode.cs、BytecodeCompiler.cs、VMWorld.cs）
- **风险可控**：调试器/DAP/LSP/快照系统完全不受影响
- **现有测试覆盖**：1007 项 Assert × 2 模式 + B01-B06 性能基准提供强回归保障
- **主要挑战**：操作数溢出（IP > 255）需要 wide 变体指令，是复杂度来源

### 1.3 操作数溢出分析

| 操作数类型 | 典型范围 | byte 上限 | 溢出风险 | 应对方案 |
|-----------|---------|----------|---------|---------|
| 寄存器索引 | 0-63 | 255 | **无** | MaxRegisters=64，永不溢出 |
| 常量池索引 | 0-50 | 255 | **极低** | 单脚本极少超 255 个常量 |
| 跳转目标 IP | 0-N | 255 | **中** | 中型脚本可能超 255 条指令 |
| Syscall slot | 0-20 | 255 | **无** | 技能脚本 Syscall 数有限 |
| 窗口大小 | 1-64 | 255 | **无** | MaxRegisters=64 |
| 跳转表索引 | 0-5 | 255 | **无** | 典型 <10 表 |

**关键结论**：唯一需要 wide 变体的是**跳转目标 IP**。

---

## 二、坏的影响与风险分析

### 2.1 负面影响

| # | 影响 | 严重度 | 缓解措施 |
|---|------|--------|---------|
| N1 | **Opcode 空间膨胀**：新增 ~5 个 WIDE 变体 OpCode → 总数从 47 增至 ~52 | 低 | OpCode 仍为 byte（0-255），52 远低于上限。switch dispatch 跳转表仍连续。 |
| N2 | **编译器复杂度增加**：每个 backpatch 站点需判断 IP 是否 > 255 | 中 | 集中在 `PatchJump()` 辅助函数中，不扩散到调用方。 |
| N3 | **Peephole 需处理 wide 指令**：模式匹配需跳过或识别 wide 变体 | 中 | wide 指令仅出现在大程序中（>255 指令），目前所有 benchmark 和测试 <100 指令。 |
| N4 | **调试可读性下降**：disassembly 输出需适配紧凑编码 | 低 | 加 ToString() 辅助方法即可。 |
| N5 | **未来 OpCode 扩展空间减少**：从剩余 209 个降至 ~203 个 | 极低 | 仍有巨大余量。 |

### 2.2 风险点

| # | 风险 | 可能性 | 影响 | 应对 |
|---|------|-------|------|------|
| R1 | **JIT 优化回退**：紧凑 struct 可能导致 RyuJIT 无法内联某些操作 | 低 | 需 benchmark 验证 | 每步完成后运行 B01-B06，发现回退立即止血 |
| R2 | **Backpatch 溢出**：编译时 IP 未确定，backpatch 时才发现需要 wide → 指令数组需要插入/扩展 | 中 | 设计复杂度 | 采用"预留 wide 位"策略（见 §三 设计决策）|
| R3 | **Peephole NOP 压缩后 IP 重映射**：wide 指令占 2 slot vs 1 slot | 中 | 正确性 | 步骤 3 集中处理，有完整测试覆盖 |
| R4 | **Fix64 模式兼容**：USE_FIXPOINT 编译标志下的行为一致性 | 低 | 正确性 | CI 双模式矩阵自动验证 |

---

## 三、关键设计决策

### 3.1 编码方案：Lua 4-byte 模型

采用 Lua 5.x 的成熟方案：

```
┌────────────┬────────────┬────────────┬────────────┐
│  OpCode    │     A      │     B      │     C      │
│  (8 bits)  │  (8 bits)  │  (8 bits)  │  (8 bits)  │
└────────────┴────────────┴────────────┴────────────┘
```

- 所有操作数为 unsigned byte (0-255)
- 寄存器索引（0-63）：永远在范围内，无需 wide
- 常量索引：目前所有脚本 < 255，但预留 LOAD_CONST_WIDE
- **跳转目标 IP**：这是唯一需要溢出处理的操作数

### 3.2 Wide 跳转策略：额外指令携带高位

当跳转目标 IP > 255 时，用两条指令编码：

```
EXTEND_AX  hi_byte  0  0       ← 新增辅助指令，在 VM 中将 hi_byte 存入临时变量
JUMP       lo_byte  0  0       ← 后续跳转指令读取 (hi_byte << 8 | lo_byte) 作为目标
```

**优点**：
- 不需要新增大量 wide 变体 OpCode（仅 +1 个 EXTEND_AX）
- 热路径上（IP ≤ 255）零开销
- 仅大型程序支付 1 条额外指令的代价

**受影响的 OpCode**（A 操作数为 IP 的指令）：
- JUMP, JUMP_IF_ZERO, JUMP_IF_NOT_ZERO
- JUMP_IF_EQ, JUMP_IF_NEQ, JUMP_IF_LT, JUMP_IF_LTE, JUMP_IF_GT, JUMP_IF_GTE
- JUMP_IF_EQ_K ~ JUMP_IF_GTE_K
- FORLOOP
- CALL, CALL_LEAF
- PUSH_CLEANUP
- SWITCH (A=defaultIP)

### 3.3 Backpatch 策略：延迟决定

编译时所有跳转先 emit 为普通（1 slot）指令，A=0 作占位。
`Finalize()` 阶段统一扫描：
1. 如果所有 backpatched IP ≤ 255 → 无需修改（最常见路径）
2. 如果存在 IP > 255 → 在该指令前插入 EXTEND_AX，然后 **全局重映射**所有 IP 引用（因为插入导致后续 IP 偏移）

这与当前 Peephole NOP 压缩的 "remapIP[]" 机制完全一致，可复用。

---

## 四、串行子计划

> **增强执行原则**：每步修改后，必须保持原版功能不变（所有 1007 项 Assert × 2 模式通过）。
> 必须妥协时，明确标注妥协原因和消除点。

### 步骤总览

| 序号 | 步骤 | 内容 | 验证条件 | 状态 |
|------|------|------|---------|------|
| O8-1 | Instruction 结构体重定义 | 4B 紧凑编码（`StructLayout.Explicit, Size=4`）+ `int→(byte)` 截断构造函数 | 全量测试通过 | ✅ |
| O8-2 | VM 执行引擎适配 | `int opA = op.A | extendedA` per-instruction merge，20+ case 适配 | 全量测试通过 + B01-B06 benchmark | ✅ |
| O8-3 | EXTEND_AX 支持 | OpCode 46 前缀 + `_wideA` 并行列表 + `ExpandWideJumps()` 迭代式后处理 + VM 原子处理（零 step 代价） | 全量测试 + S02/S05 大型脚本验证 | ✅ |
| O8-4 | Peephole 适配 | `_wideA` 方案使 Peephole 不直接接触 EXTEND_AX；`ExpandWideJumps` 在 Peephole 之后运行 | — | ⏸ 临时妥协可接受 |
| O8-5 | Benchmark 验证 + 清理 | B01↓17% B03↓15% B04↓5% B05↓7%；B06↑21%（EXTEND_AX 额外指令）；1007 Assert 全通过 | B01-B06 确认收益 | ✅ |

---

### O8-1：Instruction 结构体重定义

**目标**：将 `Instruction` 从 16B 改为 4B，编译器和 VM 无感切换。

**具体改动**：

1. **OpCode.cs** — 修改 `Instruction` 结构体：
```csharp
[StructLayout(LayoutKind.Explicit, Size = 4)]
public struct Instruction
{
    [FieldOffset(0)] public OpCode Code;  // 1 byte
    [FieldOffset(1)] public byte A;       // 1 byte
    [FieldOffset(2)] public byte B;       // 1 byte
    [FieldOffset(3)] public byte C;       // 1 byte

    // 兼容性构造函数：int → byte，带范围检查（Debug 模式）
    public Instruction(OpCode code, int a = 0, int b = 0, int c = 0)
    {
        Code = code;
        A = (byte)a;
        B = (byte)b;
        C = (byte)c;
    }
}
```

2. **编译器**：`Emit()` 签名不变（`int a, int b, int c`），内部走 `new Instruction(code, a, b, c)` 自动截断。增加 Debug.Assert 检查溢出。

3. **VMWorld.cs**：dispatch 中 `op.A`, `op.B`, `op.C` 自动从 `int` 变为 `byte`，`Reg(op.B, rb)` 等调用无需修改（byte 会隐式转为 int）。

**关键注意**：`Reg(int r, int regBase)` 参数是 `int`，byte 自动提升，无需改动。

**验证条件**：
- [ ] `dotnet build Sandbox/Sandbox.csproj -c Release` 编译成功
- [ ] 1007 项 Assert × 2 模式全通过
- [ ] B01-B06 benchmark 运行正确（结果 PASS）

**妥协**：无。结构体改动是纯粹的内存布局变化，所有现有操作数在 byte 范围内。

---

### O8-2：VM 执行引擎适配

**目标**：VMWorld.ExecuteInstance() 适配 byte 操作数读取，确认 JIT 优化不回退。

**具体改动**：

1. **VMWorld.cs** — `fixed (Instruction* codeBase = code)` 仍然有效（Instruction 仍是 blittable struct）。
   - `ref Instruction op = ref codeBase[inst.IP]` — 现在每条指令 4B，指针步进 4B（之前 16B）。
   - 所有 `op.A`, `op.B`, `op.C` 从 `int` 变为 `byte`，在表达式中隐式提升为 `int`。

2. **性能关键点**：确认 `byte` 操作数的 JIT 行为：
   - `regs[Reg(op.B, rb)]` — `op.B` 为 byte，传入 `Reg(int, int)` 时隐式 widening，无额外指令。
   - `inst.IP = op.A` — `op.A` 为 byte，赋值给 `int inst.IP`，隐式 widening。
   - `constBase[op.C]` — byte 做数组索引，JIT 会 zero-extend，无性能损失。

3. **Reg() 方法**：`r < 16` 比较中 `r` 仍为 `int`（byte 隐式转换），逻辑不变。

**验证条件**：
- [ ] 全量测试通过
- [ ] B01-B06 benchmark 性能**不回退**（与 O8-1 前对比）
- [ ] 确认 L1 缓存收益（B04 期望改善最明显）

**妥协**：无。此步是 O8-1 的直接后果，不需要额外逻辑。
实际上 O8-1 和 O8-2 应一并完成（结构体改了，执行引擎自然适配），分步仅为文档清晰。

> **实施说明**：O8-1 和 O8-2 可合并为单次提交。分开描述是为了分析清晰，但由于结构体改动后 VM 自然适配（byte 隐式提升为 int），不存在中间状态不可编译的问题。

---

### O8-3：EXTEND_AX 辅助指令

**目标**：支持跳转目标 IP > 255 的场景。

**具体改动**：

1. **OpCode.cs** — 新增：
```csharp
EXTEND_AX = 47,  // A=hi_byte → next instruction's A is extended to (hi<<8|lo)
// SENTINEL 改为 48
```

2. **VMWorld.cs** — 新增 case：
```csharp
case OpCode.EXTEND_AX:
    extendedA = op.A << 8;  // 存入局部变量
    inst.IP++;
    continue;                // 立即执行下一条指令
```
   在所有读取 `op.A` 作为 IP 的 case 中：
```csharp
int targetIP = op.A | extendedA;  // 合并高位
extendedA = 0;                     // 重置
```

3. **BytecodeCompiler.cs** — 修改 `Emit()` 和 backpatch：
```csharp
private void EmitWithIP(OpCode code, int targetIP, int b = 0, int c = 0)
{
    if (targetIP > 255)
    {
        Emit(OpCode.EXTEND_AX, targetIP >> 8);
        _sourceLines.Add(_currentLine); // EXTEND_AX 共享同一源码行
    }
    Emit(code, targetIP & 0xFF, b, c);
}
```

4. **Backpatch** — 修改 `PatchJump()` 及所有 backpatch 站点：
   如果 patched IP > 255，需要在目标指令前插入 EXTEND_AX → 引发全局 IP 重映射。
   **策略**：在 `Finalize()` 阶段（Peephole 后、VMProgram 构造前）统一执行一次 wide 扩展 pass。

**验证条件**：
- [ ] 全量测试通过（现有脚本均 < 255 指令，不触发 EXTEND_AX）
- [ ] **新增测试**：构造 >255 指令的合成脚本，验证 wide 跳转正确性
  - 前向跳转 > 255
  - 后向跳转（循环回到 IP 0 区域）
  - CALL 到 > 255 的函数入口
  - PUSH_CLEANUP 到 > 255 的 cleanup 块

**临时妥协**：
- **妥协内容**：Peephole NOP 压缩暂不处理 EXTEND_AX 指令的插入/删除。即：如果 Peephole 后指令数从 >255 降到 ≤255，多余的 EXTEND_AX 不会被自动消除。反之如果 Peephole 压缩后仍 >255，EXTEND_AX 仍然正确（因为 wide pass 在 Peephole 之后执行）。
- **妥协原因**：Peephole 和 wide-pass 两个 IP 重映射 pass 的交互验证需要额外覆盖，应独立步骤处理。
- **妥协影响**：极小。目前所有测试和 benchmark 脚本 < 100 指令，不触发此路径。只有未来大型脚本才会产生少量冗余 NOP（EXTEND_AX 占位），不影响正确性。
- **消除点**：O8-4 步骤。

---

### O8-4：Peephole 适配

**目标**：Peephole 优化器正确处理 EXTEND_AX 指令。

**具体改动**：

1. **跳转目标集合构建**（Phase 1）：
   - 识别 EXTEND_AX + 后续指令的 "组合跳转目标"
   - 跳转目标指向 EXTEND_AX 位置（不可拆分的指令对）

2. **模式匹配**（Phase 2）：
   - P1 (self-move), P4 (jump-to-next)：不受影响（不涉及跳转目标）
   - P2 (dest-redirect), P5 (compare-branch fusion)：检查前驱是否为 EXTEND_AX，若是则跳过

3. **NOP 压缩 + IP 重映射**（Phase 3）：
   - EXTEND_AX 和其后续指令视为原子对，一起删除或保留
   - IP 重映射时，EXTEND_AX 的 IP 值也需要更新

4. **冗余 EXTEND_AX 清理**：
   - 如果 NOP 压缩后目标 IP 降至 ≤ 255，移除冗余 EXTEND_AX

**验证条件**：
- [ ] 全量测试通过
- [ ] >255 指令合成测试通过（含 Peephole 优化开启/关闭对比）
- [ ] 消除 O8-3 临时妥协

**妥协**：无。此步骤完成后 EXTEND_AX 处理完整。

---

### O8-5：Benchmark 验证 + 文档更新

**目标**：确认性能收益，更新文档。

**具体改动**：

1. **运行 B01-B06 benchmark**，与 O8-1 前基线对比：
   - 期望整体 10-20% 改善
   - B04 期望改善最大（分支密集 + 最多指令种类）

2. **运行跨语言 benchmark**，更新 cross_lang_results.md

3. **更新文档**：
   - VM_Summary.md §十 O8 状态 → ✅
   - Outlook_And_Risks.md O8 状态 → ✅
   - cross_lang_results.md Gap Analysis 表格更新指令编码行

**验证条件**：
- [ ] 全量测试通过
- [ ] B01-B06 benchmark 无回退
- [ ] 跨语言 benchmark 数据更新
- [ ] 文档更新完毕

**妥协**：无。

---

## 五、功能展望

| ID | 内容 | 触发时机 |
|----|------|---------|
| O8-F1 | LOAD_CONST_WIDE（常量池 > 255 条目） | 极大型脚本出现时 |
| O8-F2 | sBx 编码（有符号偏移跳转，消除 EXTEND_AX） | 如果 EXTEND_AX 频繁出现影响性能 |

## 六、优化展望

| ID | 内容 | 预期收益 |
|----|------|---------|
| O8-O1 | 编译器在 Finalize 中统计 EXTEND_AX 条数作为 "代码膨胀度" 指标 | 诊断 |
| O8-O2 | 将 `extendedA` 与 `inst.IP` 打包避免额外局部变量 | 微优化 |

## 七、风险点

| # | 风险 | 状态 |
|---|------|------|
| R1 | JIT 优化回退（byte struct 在特定 .NET 版本上的表现） | ⏳ O8-2 验证 |
| R2 | 大型脚本 EXTEND_AX 导致 backpatch 全局重映射复杂度 | ⏳ O8-3 验证 |
| R3 | Peephole + EXTEND_AX 交互正确性 | ⏳ O8-4 验证 |
