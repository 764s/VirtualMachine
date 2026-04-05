# B-ζ3: SWITCH Jump Table Instruction

## Goal

Replace linear if-else-if chains with O(1) jump table dispatch when all conditions
compare the same variable against consecutive integer constants starting from 0.

## Design

### OpCode

```
SWITCH = 45   // A=defaultIP, B=testReg, C=jumpTableIdx
SENTINEL = 46 // moved from 45
```

### Instruction encoding

| Field | Meaning |
|-------|---------|
| A | Default IP (jumped when value out of range) |
| B | Test register (dispatched value) |
| C | Jump table index into VMProgram.JumpTables |

### VMProgram change

New field `int[][] JumpTables` — each entry is an IP array indexed by the test value.

### Detection (TryCompileSwitch)

Walk the if-else-if chain from the root IfStmt:
1. All conditions must be `BinaryExpr(Eq, IdentifierExpr(var), IntLiteral(k))` or reversed
2. All branches must reference the **same** variable
3. Constants must form a **consecutive range starting from 0**
4. Minimum **3** cases required (otherwise linear chain is fine)

### Code layout

```
SWITCH defaultIP, testReg, jumpTableIdx
  [case 0 body]
  JUMP endSwitch
  [case 1 body]
  JUMP endSwitch
  ...
  [case N-1 body]
  JUMP endSwitch
default:
  [else body]
endSwitch:
```

JumpTable[idx] = { IP_case0, IP_case1, ..., IP_caseN-1 }

### VMWorld execution

```csharp
case OpCode.SWITCH:
{
    int val = regs[Reg(op.B, rb)].ToInt();
    int[] table = program.JumpTables[op.C];
    if (val >= 0 && val < table.Length)
        inst.IP = table[val];
    else
        inst.IP = op.A; // default
    break;
}
```

### Peephole integration

1. **HasJumpTargetInA**: includes SWITCH (A = defaultIP)
2. **GetRegisterMask**: returns 2 (B = testReg)
3. **Phase 1 jump targets**: SWITCH jump table entries added to jumpTargets set
4. **Phase 3 compaction**: jump table IPs remapped through remap[] array

## Files changed

| File | Change |
|------|--------|
| OpCode.cs | +SWITCH=45, SENTINEL→46 |
| VMProgram.cs | +JumpTables field, constructor parameter |
| VMWorld.cs | +SWITCH case handler |
| BytecodeCompiler.cs | +_jumpTables field, +TryCompileSwitch, CompileIf integration, GetRegisterMask, HasJumpTargetInA, peephole jump table tracking + remapping, CompileModule wiring |

## Test results

- **1007 tests × 2 modes**: all passed, 0 failures
- No new test cases required (existing if-else chains exercise SWITCH automatically)

## Benchmark (B04_Branching: 4-way if-else-if chain, 10K iterations)

| Metric | Before (B-ζ2) | After (B-ζ3) | Δ |
|--------|---------------|--------------|---|
| B04 VM (μs) | ~2590–3004 | ~1475–1586 | ↓40–47% |

> Note: High variance on 8-core desktop; B04 consistent improvement across multiple runs.
> Other benchmarks also improved but primarily attributed to system load variance.

## Interaction with LICM

LICM (B-ζ1) hoists constants from the loop body, including the 0/1/2 constants used in
if-else conditions. With SWITCH, these hoisted constants become unused (SWITCH doesn't
reference them — it reads the value from the test register and dispatches via table).
The unused LOAD_CONST instructions are O(1) per loop entry and don't impact per-iteration
performance. Future work could teach LICM to skip constants used only in SWITCH patterns.

## Limitations

- Only triggers for **0-based consecutive integer** constants
- Minimum 3 cases required
- Non-zero-based ranges or sparse constants fall back to linear if-else chain
- Constants compared via `==` only (no range checks)
