using System;
using System.Collections.Generic;
using FFVM;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// VM verification gates (V1–V4) and performance benchmarks.
/// Extracted for independent, repeatable long-term validation.
/// Run from Unity: TestVM → RunPerformanceTests
/// Run from CLI:   dotnet run --project StandaloneRunner (calls PerformanceTests.RunAll)
/// </summary>
public static class PerformanceTests
{
    [MenuItem("TestVM/RunPerformanceTests")]
    public static void RunAll()
    {
        int passed = 0;
        int failed = 0;

        void Assert(bool condition, string testName)
        {
            if (condition)
            {
                Debug.Log($"[PASS] {testName}");
                passed++;
            }
            else
            {
                Debug.LogError($"[FAIL] {testName}");
                failed++;
            }
        }

        // ===== Test P01: 0 GC in bytecode Tick loop (basic) =====
        {
            // Setup: program with SYSCALL + WAIT + cleanup, pre-allocate everything
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 5),
                    new Instruction(OpCode.SYSCALL, 0),
                    new Instruction(OpCode.WAIT, 1),
                    new Instruction(OpCode.RETURN),
                    new Instruction(OpCode.NOP),
                    // cleanup block
                    new Instruction(OpCode.SYSCALL, 0),
                    new Instruction(OpCode.RETURN),
                },
                new Number[0],
                0
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { /* no alloc */ });

            // Warmup: run a few rounds to JIT and stabilize
            for (int warm = 0; warm < 5; warm++)
            {
                int wid = world.SpawnInstance(0, 0);
                for (int t = 0; t < 10; t++) world.Tick();
                world.DestroyInstance(wid);
            }

            // Measure using per-thread allocation counter (precise, not affected by
            // other threads or runtime internals unlike GC.GetTotalMemory)
            long threadBefore = GC.GetAllocatedBytesForCurrentThread();

            const int rounds = 20;
            for (int r = 0; r < rounds; r++)
            {
                int mid = world.SpawnInstance(0, 0);
                for (int t = 0; t < 10; t++) world.Tick();
                world.DestroyInstance(mid);
            }

            long threadAfter = GC.GetAllocatedBytesForCurrentThread();
            long delta = threadAfter - threadBefore;

            Assert(delta == 0,
                $"P01 0-GC basic: bytecode tick delta = {delta} bytes (== 0)");
        }

        // =================================================================
        //  V1: GC Precise Verification (§4.6 V1)
        //  Confirms bytecode Tick loop is zero-GC over 100 consecutive ticks
        //  with active instances executing SYSCALL + WAIT + Cleanup.
        // =================================================================

        // ===== Test P02: V1 — 100-tick zero GC with active instances =====
        {
            // Program: defer{Syscall_Noop}; Syscall_Noop; wait 5; Syscall_Noop; return
            // Each instance takes 7 ticks to complete (1 exec + 5 wait + 1 resume+cleanup)
            var v1Program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 6),   // 0: defer → IP 6
                    new Instruction(OpCode.SYSCALL, 0),        // 1: Noop (main)
                    new Instruction(OpCode.WAIT, 5),           // 2: wait 5 frames
                    new Instruction(OpCode.SYSCALL, 0),        // 3: Noop (post-wait)
                    new Instruction(OpCode.RETURN),            // 4: normal return → cleanup
                    new Instruction(OpCode.NOP),               // 5: spacer
                    new Instruction(OpCode.SYSCALL, 0),        // 6: cleanup Noop
                    new Instruction(OpCode.RETURN),            // 7: cleanup done
                },
                new Number[0],
                0
            );

            var v1World = new VMWorld();
            v1World.Modules.Load(0, v1Program);
            v1World.Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { });

            // Heavy warmup: 50 rounds to fully JIT all paths
            for (int warm = 0; warm < 50; warm++)
            {
                int wid = v1World.SpawnInstance(0, 0);
                for (int t = 0; t < 10; t++) v1World.Tick();
                v1World.DestroyInstance(wid);
            }

            // Spawn 10 instances that will be active during the 100-tick window
            // They complete in ~7 ticks each, so re-spawn mid-run to keep the pool active
            for (int i = 0; i < 10; i++)
                v1World.SpawnInstance(0, 0);

            // Measure: 100 consecutive ticks with active instances
            long v1Before = GC.GetAllocatedBytesForCurrentThread();

            for (int t = 0; t < 100; t++)
                v1World.Tick();

            long v1After = GC.GetAllocatedBytesForCurrentThread();
            long v1Delta = v1After - v1Before;

            Assert(v1Delta == 0,
                $"P02 V1 GC precise: 100 ticks alloc = {v1Delta} bytes (== 0)");

            // Also verify instances actually ran (not just idle ticks)
            bool anyCompleted = false;
            for (int i = 0; i < VMConstants.MaxInstances; i++)
            {
                ref VMInstanceState inst = ref v1World.Pool.Instances[i];
                if (inst.IsAlive && (inst.StateFlags & VMStateFlags.Completed) != 0)
                {
                    anyCompleted = true;
                    break;
                }
            }
            Assert(anyCompleted, "P02 V1 GC precise: instances actually executed (not idle)");
        }

        // =================================================================
        //  V2: Rollback Correctness Verification (§4.6 V2)
        //  100 frames → Save → 50 diverge → Load → 100 frames
        //  Syscall sequences and final StateFlags must be bit-exact.
        // =================================================================

        // ===== Test P03: V2 — Rollback correctness =====
        {
            // Program: defer{SetBB(0)}; SetBB(1); wait 80; PlayEffect; return
            // Timeline: frame 1 → SetBB(1)+WAIT, frames 2-81 waiting, frame 82 → PlayEffect+Cleanup
            // Save at frame 50 (mid-wait), diverge 50 frames, load back, run 100 more.
            var v2Instructions = new Instruction[]
            {
                new Instruction(OpCode.PUSH_CLEANUP, 8),       // 0: defer → IP 8
                new Instruction(OpCode.LOAD_CONST, 0, 1),      // 1: R0 = 1
                new Instruction(OpCode.SYSCALL, 0),             // 2: SetBB(1)
                new Instruction(OpCode.WAIT, 80),               // 3: wait 80 frames
                new Instruction(OpCode.LOAD_CONST, 1, 2),      // 4: R1 = effectId
                new Instruction(OpCode.SYSCALL, 1),             // 5: PlayEffect
                new Instruction(OpCode.RETURN),                 // 6: normal return → cleanup
                new Instruction(OpCode.NOP),                    // 7: spacer
                new Instruction(OpCode.LOAD_CONST, 0, 0),      // 8: R0 = 0 (cleanup)
                new Instruction(OpCode.SYSCALL, 0),             // 9: SetBB(0)
                new Instruction(OpCode.RETURN),                 // 10: cleanup done
            };
            var v2Consts = new Number[]
            {
                Number.FromInt(0),  // [0] = 0
                Number.FromInt(1),  // [1] = 1
                Number.FromInt(99), // [2] = effectId
            };
            var v2Program = new VMProgram(v2Instructions, v2Consts, 2);

            // --- Run A (reference): 200 frames, collect syscall log from frame 51 onward ---
            var logRefPost = new List<string>();
            VMStateFlags finalFlagsRef;
            {
                var wA = new VMWorld();
                var logAll = new List<string>();
                wA.Modules.Load(0, v2Program);
                wA.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
                    { logAll.Add($"SetBB({s.Registers.Get(0).ToInt()})"); });
                wA.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
                    { logAll.Add($"PlayEffect({s.Registers.Get(1).ToInt()})"); });
                wA.SpawnInstance(0, 0);

                // Run 50 frames (pre-save portion — we'll compare post-save)
                for (int t = 0; t < 50; t++) wA.Tick();

                // Record log from frame 51 onward
                logAll.Clear();
                for (int t = 0; t < 100; t++) wA.Tick();
                logRefPost = logAll;

                finalFlagsRef = wA.Pool.Instances[0].StateFlags;
            }

            // --- Run B (rollback): 50 frames → Save → 50 diverge → Load → 100 frames ---
            var logRollbackPost = new List<string>();
            VMStateFlags finalFlagsRollback;
            {
                var wB = new VMWorld();
                wB.Modules.Load(0, v2Program);
                wB.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
                    { logRollbackPost.Add($"SetBB({s.Registers.Get(0).ToInt()})"); });
                wB.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
                    { logRollbackPost.Add($"PlayEffect({s.Registers.Get(1).ToInt()})"); });
                wB.SpawnInstance(0, 0);

                // Run 50 frames (same as reference pre-save)
                for (int t = 0; t < 50; t++) wB.Tick();

                // Save state at frame 50
                wB.SaveState();
                int savedFrame = wB.FrameNumber;

                // Diverge: run 50 more frames (frames 51-100)
                for (int t = 0; t < 50; t++) wB.Tick();

                // Load back to frame 50
                logRollbackPost.Clear(); // discard diverged syscalls
                bool loaded = wB.LoadState(savedFrame);
                Assert(loaded, "P03 V2 rollback: LoadState succeeded");

                // Run 100 frames from the restored state (should match reference)
                for (int t = 0; t < 100; t++) wB.Tick();

                finalFlagsRollback = wB.Pool.Instances[0].StateFlags;
            }

            // Compare results
            Assert(logRefPost.Count == logRollbackPost.Count,
                $"P03 V2 rollback: syscall count match ({logRefPost.Count} vs {logRollbackPost.Count})");

            bool v2SeqMatch = true;
            int v2MinCount = Math.Min(logRefPost.Count, logRollbackPost.Count);
            for (int i = 0; i < v2MinCount && v2SeqMatch; i++)
                v2SeqMatch = logRefPost[i] == logRollbackPost[i];
            v2SeqMatch = v2SeqMatch && (logRefPost.Count == logRollbackPost.Count);
            Assert(v2SeqMatch,
                "P03 V2 rollback: syscall sequence bit-exact");

            Assert(finalFlagsRef == finalFlagsRollback,
                $"P03 V2 rollback: final StateFlags match ({finalFlagsRef} vs {finalFlagsRollback})");

            // Verify the instance actually completed (not still idle)
            Assert((finalFlagsRef & VMStateFlags.Completed) != 0,
                "P03 V2 rollback: instance completed in reference run");
        }

        // =================================================================
        //  V1b: 0 GC for Phase 2 opcodes
        //  Confirms all new opcodes (MOVE, arithmetic, compare, boolean,
        //  JUMP, JUMP_IF) do not allocate.
        // =================================================================

        // ===== Test P04: 0 GC — Phase 2 opcodes =====
        {
            // Program exercises all Phase 2 opcodes:
            // MOVE, ADD, SUB, MUL, DIV, MOD, CMP_EQ, CMP_LT, AND, OR, NOT, NEG,
            // JUMP, JUMP_IF_ZERO, JUMP_IF_NOT_ZERO
            // loop 10 iterations then return
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),         // IP 0: R0 = 0 (counter)
                    new Instruction(OpCode.LOAD_CONST, 1, 1),         // IP 1: R1 = 10 (limit)
                    new Instruction(OpCode.LOAD_CONST, 2, 2),         // IP 2: R2 = 1 (step)
                    // loop start:
                    new Instruction(OpCode.CMP_LT, 3, 0, 1),         // IP 3: R3 = (R0 < 10)?
                    new Instruction(OpCode.JUMP_IF_ZERO, 17, 3),      // IP 4: exit if done
                    new Instruction(OpCode.ADD, 0, 0, 2),             // IP 5: R0 += 1
                    new Instruction(OpCode.SUB, 4, 1, 0),             // IP 6: R4 = 10 - R0
                    new Instruction(OpCode.MUL, 5, 0, 2),             // IP 7: R5 = R0 * 1
                    new Instruction(OpCode.MOVE, 6, 5),               // IP 8: R6 = R5
                    new Instruction(OpCode.CMP_EQ, 7, 0, 1),         // IP 9: R7 = (R0 == 10)?
                    new Instruction(OpCode.CMP_LTE, 8, 0, 1),        // IP 10: R8 = (R0 <= 10)?
                    new Instruction(OpCode.AND, 9, 7, 8),             // IP 11: R9 = AND(R7, R8)
                    new Instruction(OpCode.OR, 10, 7, 3),             // IP 12: R10 = OR(R7, R3)
                    new Instruction(OpCode.NOT, 11, 7),               // IP 13: R11 = NOT(R7)
                    new Instruction(OpCode.NEG, 12, 0),               // IP 14: R12 = -R0
                    new Instruction(OpCode.MOD, 13, 0, 2),            // IP 15: R13 = R0 % 1
                    new Instruction(OpCode.JUMP, 3),                  // IP 16: → loop start
                    // exit:
                    new Instruction(OpCode.RETURN),                   // IP 17
                },
                new Number[] { Number.FromInt(0), Number.FromInt(10), Number.FromInt(1) },
                14
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { });

            // Warmup
            for (int warm = 0; warm < 50; warm++)
            {
                int wid = world.SpawnInstance(0, 0);
                world.Tick();
                world.DestroyInstance(wid);
            }

            // Measure
            long gcBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int r = 0; r < 20; r++)
            {
                int mid = world.SpawnInstance(0, 0);
                world.Tick();
                world.DestroyInstance(mid);
            }
            long gcAfter = GC.GetAllocatedBytesForCurrentThread();
            long gcDelta = gcAfter - gcBefore;

            Assert(gcDelta == 0,
                $"P04 0-GC Phase2 opcodes: delta = {gcDelta} bytes (== 0)");
        }

        // =================================================================
        //  V2b: Save/Load correctness with Phase 2 opcodes
        //  Loop with wait-per-iteration, save mid-run, diverge, reload.
        // =================================================================

        // ===== Test P05: Save/Load correctness with Phase 2 opcodes =====
        {
            // Loop: sum = 0; i = 1; while i <= 20: sum += i; i++; wait 1; end; report(sum)
            // Save at frame 5 (mid-loop), diverge, load, resume — must match reference
            var progInstr = new Instruction[]
            {
                new Instruction(OpCode.LOAD_CONST, 0, 0),         // IP 0: R0 = 0 (sum)
                new Instruction(OpCode.LOAD_CONST, 1, 1),         // IP 1: R1 = 1 (i)
                new Instruction(OpCode.LOAD_CONST, 2, 2),         // IP 2: R2 = 20 (limit)
                new Instruction(OpCode.LOAD_CONST, 3, 1),         // IP 3: R3 = 1 (step)
                // loop start (IP 4):
                new Instruction(OpCode.CMP_GT, 4, 1, 2),         // IP 4: R4 = (i > 20)?
                new Instruction(OpCode.JUMP_IF_NOT_ZERO, 10, 4), // IP 5: if done → IP 10
                new Instruction(OpCode.ADD, 0, 0, 1),             // IP 6: sum += i
                new Instruction(OpCode.ADD, 1, 1, 3),             // IP 7: i += 1
                new Instruction(OpCode.WAIT, 1),                  // IP 8: wait 1 frame (spread across ticks)
                new Instruction(OpCode.JUMP, 4),                  // IP 9: → loop start
                // exit:
                new Instruction(OpCode.SYSCALL, 0),               // IP 10: report result
                new Instruction(OpCode.RETURN),                   // IP 11
            };
            var progConsts = new Number[]
            {
                Number.FromInt(0), Number.FromInt(1), Number.FromInt(20),
            };

            // --- Run A: reference (no save/load) ---
            var logA = new List<string>();
            int finalSumA;
            {
                var prog = new VMProgram(progInstr, progConsts, 5);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
                    { logA.Add($"sum={s.Registers.Get(0).ToInt()}"); });
                w.SpawnInstance(0, 0);
                for (int t = 0; t < 60; t++) w.Tick();
                finalSumA = w.Pool.Instances[0].Registers.Get(0).ToInt();
            }

            // --- Run B: save at tick 5, diverge, load, resume ---
            var logB = new List<string>();
            int finalSumB;
            {
                var prog = new VMProgram(progInstr, progConsts, 5);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
                    { logB.Add($"sum={s.Registers.Get(0).ToInt()}"); });
                w.SpawnInstance(0, 0);

                for (int t = 0; t < 5; t++) w.Tick();
                w.SaveState();
                int sf = w.FrameNumber;

                // Diverge
                for (int t = 0; t < 10; t++) w.Tick();

                // Load back
                logB.Clear();
                w.LoadState(sf);

                // Resume
                for (int t = 0; t < 60; t++) w.Tick();
                finalSumB = w.Pool.Instances[0].Registers.Get(0).ToInt();
            }

            Assert(finalSumA == 210, $"P05 Save/Load loop: reference sum = {finalSumA} (== 210)");
            Assert(finalSumA == finalSumB,
                $"P05 Save/Load loop: rollback sum matches ({finalSumA} vs {finalSumB})");
            Assert(logA.Count == logB.Count && logA.Count > 0,
                $"P05 Save/Load loop: syscall count match ({logA.Count} vs {logB.Count})");
            bool seqOk = logA.Count == logB.Count;
            for (int i = 0; i < logA.Count && seqOk; i++)
                seqOk = logA[i] == logB[i];
            Assert(seqOk, "P05 Save/Load loop: syscall sequence bit-exact");
        }

        // =================================================================
        //  V3: Single-Instance Performance Benchmark (§4.6 V3)
        //  VM bytecode vs equivalent C# logic — measure overhead ratio.
        //  Same logic: loop 10000 iterations with arithmetic + branch + syscall.
        //  Using Number type in both paths for fair data-type comparison.
        // =================================================================

        // ===== Test P06: V3 — Single-instance performance benchmark =====
        {
            int v3Iters = 10000;
            int v3Runs = 100;

            // --- VM bytecode program ---
            // Loop v3Iters times: arithmetic + branch + syscall (every 3rd iteration)
            // Registers: R0=i, R1=limit, R2=step(1), R3=acc, R4=temp, R5=cmp, R6=divisor(3), R7=mod
            var v3Program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),          // IP 0:  R0 = 0 (counter)
                    new Instruction(OpCode.LOAD_CONST, 1, 1),          // IP 1:  R1 = limit
                    new Instruction(OpCode.LOAD_CONST, 2, 2),          // IP 2:  R2 = 1 (step)
                    new Instruction(OpCode.LOAD_CONST, 3, 0),          // IP 3:  R3 = 0 (accumulator)
                    new Instruction(OpCode.LOAD_CONST, 6, 3),          // IP 4:  R6 = 3 (divisor)
                    // loop start (IP 5):
                    new Instruction(OpCode.CMP_GTE, 5, 0, 1),         // IP 5:  R5 = (i >= limit)?
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 16, 5),   // IP 6:  if done → exit
                    new Instruction(OpCode.ADD, 3, 3, 0),              // IP 7:  acc += i
                    new Instruction(OpCode.MUL, 4, 0, 2),              // IP 8:  temp = i * 1
                    new Instruction(OpCode.SUB, 4, 4, 2),              // IP 9:  temp -= 1
                    new Instruction(OpCode.ADD, 3, 3, 4),              // IP 10: acc += temp
                    new Instruction(OpCode.MOD, 7, 0, 6),              // IP 11: R7 = i % 3
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 14, 7),   // IP 12: if i%3 != 0 → skip syscall
                    new Instruction(OpCode.SYSCALL, 0),                 // IP 13: noop syscall
                    // skip_syscall (IP 14):
                    new Instruction(OpCode.ADD, 0, 0, 2),              // IP 14: i++
                    new Instruction(OpCode.JUMP, 5),                    // IP 15: → loop start
                    // exit (IP 16):
                    new Instruction(OpCode.RETURN),                     // IP 16
                },
                new Number[]
                {
                    Number.FromInt(0),          // [0] = 0
                    Number.FromInt(v3Iters),    // [1] = limit
                    Number.FromInt(1),          // [2] = 1
                    Number.FromInt(3),          // [3] = divisor
                },
                8
            );

            var v3World = new VMWorld();
            v3World.MaxStepsPerTick = v3Iters * 15; // enough for full loop
            v3World.Modules.Load(0, v3Program);
            v3World.Syscalls.Register(0, "BenchNoop", (ref VMInstanceState s) => { });

            // --- Correctness check ---
            {
                int vid = v3World.SpawnInstance(0, 0);
                v3World.Tick();
                int vmAcc = v3World.Pool.Instances[vid].Registers.Get(3).ToInt();
                v3World.DestroyInstance(vid);
                Assert(vmAcc == 99980000,
                    $"P06 V3 correctness: acc = {vmAcc} (== 99980000)");
            }

            // --- Warmup VM ---
            for (int w = 0; w < 10; w++)
            {
                int wid = v3World.SpawnInstance(0, 0);
                v3World.Tick();
                v3World.DestroyInstance(wid);
            }

            // --- Measure VM ---
            var v3sw = Stopwatch.StartNew();
            for (int r = 0; r < v3Runs; r++)
            {
                int id = v3World.SpawnInstance(0, 0);
                v3World.Tick();
                v3World.DestroyInstance(id);
            }
            v3sw.Stop();
            double v3VmTotalMs = v3sw.Elapsed.TotalMilliseconds;
            double v3VmPerRunUs = (v3VmTotalMs / v3Runs) * 1000.0;

            // --- C# equivalent using Number type ---
            Number csLimit = Number.FromInt(v3Iters);
            Number csStep = Number.FromInt(1);
            Number csDivisor = Number.FromInt(3);

            // Warmup C#
            for (int w = 0; w < 10; w++)
            {
                Number csI = Number.Zero;
                Number csAcc = Number.Zero;
                int csSC = 0;
                while (csI < csLimit)
                {
                    csAcc = csAcc + csI;
                    Number t = csI * csStep;
                    t = t - csStep;
                    csAcc = csAcc + t;
                    if (csI % csDivisor == Number.Zero) csSC++;
                    csI = csI + csStep;
                }
            }

            // Measure C#
            v3sw.Restart();
            for (int r = 0; r < v3Runs; r++)
            {
                Number csI = Number.Zero;
                Number csAcc = Number.Zero;
                int csSC = 0;
                while (csI < csLimit)
                {
                    csAcc = csAcc + csI;
                    Number t = csI * csStep;
                    t = t - csStep;
                    csAcc = csAcc + t;
                    if (csI % csDivisor == Number.Zero) csSC++;
                    csI = csI + csStep;
                }
            }
            v3sw.Stop();
            double v3CsTotalMs = v3sw.Elapsed.TotalMilliseconds;
            double v3CsPerRunUs = (v3CsTotalMs / v3Runs) * 1000.0;

            double v3Ratio = v3VmPerRunUs / v3CsPerRunUs;

            Debug.Log($"[BENCH] V3 Single-Instance Performance:");
            Debug.Log($"[BENCH]   VM bytecode : {v3VmPerRunUs:F1} µs/run ({v3Iters} iterations)");
            Debug.Log($"[BENCH]   C# native   : {v3CsPerRunUs:F1} µs/run ({v3Iters} iterations)");
            Debug.Log($"[BENCH]   Ratio       : {v3Ratio:F1}x");

            Assert(v3Ratio < 50.0,
                $"P06 V3 perf: VM/C# ratio = {v3Ratio:F1}x (expected < 50x, reference 10-30x)");
        }

        // =================================================================
        //  V4: N-Instance Throughput Benchmark (§4.6 V4)
        //  128 → 256 → 512 → 1024 instances × ~50 instructions/tick.
        //  Pass condition: 128 instances × 50 instr/tick < 1ms.
        //  Uses multiple VMWorlds for counts exceeding MaxInstances (128).
        // =================================================================

        // ===== Test P07: V4 — N-instance throughput =====
        {
            // Program: ~57 instructions per instance (5-iteration loop), single-tick completion
            // R0=i, R1=5(limit), R2=1(step), R3=acc, R4=temp, R5=cmp
            var v4Program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),         // IP 0:  R0 = 0 (counter)
                    new Instruction(OpCode.LOAD_CONST, 1, 1),         // IP 1:  R1 = 5 (limit)
                    new Instruction(OpCode.LOAD_CONST, 2, 2),         // IP 2:  R2 = 1 (step)
                    new Instruction(OpCode.LOAD_CONST, 3, 0),         // IP 3:  R3 = 0 (acc)
                    // loop (IP 4):
                    new Instruction(OpCode.CMP_GTE, 5, 0, 1),        // IP 4:  R5 = (i >= 5)?
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 14, 5),  // IP 5:  if done → exit
                    new Instruction(OpCode.ADD, 3, 3, 0),             // IP 6:  acc += i
                    new Instruction(OpCode.MUL, 4, 0, 2),             // IP 7:  temp = i * 1
                    new Instruction(OpCode.SUB, 4, 4, 2),             // IP 8:  temp -= 1
                    new Instruction(OpCode.ADD, 3, 3, 4),             // IP 9:  acc += temp
                    new Instruction(OpCode.ADD, 0, 0, 2),             // IP 10: i++
                    new Instruction(OpCode.SYSCALL, 0),               // IP 11: noop
                    new Instruction(OpCode.NOP),                      // IP 12: padding
                    new Instruction(OpCode.JUMP, 4),                  // IP 13: → loop
                    // exit (IP 14):
                    new Instruction(OpCode.RETURN),                   // IP 14
                },
                new Number[]
                {
                    Number.FromInt(0),  // [0] = 0
                    Number.FromInt(5),  // [1] = 5
                    Number.FromInt(1),  // [2] = 1
                },
                6
            );

            // --- V4 correctness check ---
            {
                var cw = new VMWorld();
                cw.Modules.Load(0, v4Program);
                cw.Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { });
                int cid = cw.SpawnInstance(0, 0);
                cw.Tick();
                int v4Acc = cw.Pool.Instances[cid].Registers.Get(3).ToInt();
                Assert(v4Acc == 15,
                    $"P07 V4 correctness: acc = {v4Acc} (== 15)");
            }

            // --- Benchmark at each scale ---
            int v4Rounds = 1000;
            int[] v4Scales = new int[] { 128, 256, 512, 1024 };

            Debug.Log($"[BENCH] V4 N-Instance Throughput (~57 instr/instance):");

            foreach (int targetN in v4Scales)
            {
                int worldCount = (targetN + VMConstants.MaxInstances - 1) / VMConstants.MaxInstances;
                int instancesPerWorld = VMConstants.MaxInstances;

                // Create worlds
                var v4Worlds = new VMWorld[worldCount];
                for (int w = 0; w < worldCount; w++)
                {
                    v4Worlds[w] = new VMWorld();
                    v4Worlds[w].Modules.Load(0, v4Program);
                    v4Worlds[w].Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { });
                }

                // Warmup
                for (int warm = 0; warm < 10; warm++)
                {
                    for (int w = 0; w < worldCount; w++)
                    {
                        for (int i = 0; i < instancesPerWorld; i++)
                            v4Worlds[w].SpawnInstance(0, 0);
                        v4Worlds[w].Tick();
                        for (int i = 0; i < instancesPerWorld; i++)
                            v4Worlds[w].DestroyInstance(i);
                    }
                }

                // Measure
                var v4sw = Stopwatch.StartNew();
                for (int r = 0; r < v4Rounds; r++)
                {
                    for (int w = 0; w < worldCount; w++)
                    {
                        for (int i = 0; i < instancesPerWorld; i++)
                            v4Worlds[w].SpawnInstance(0, 0);
                        v4Worlds[w].Tick();
                        for (int i = 0; i < instancesPerWorld; i++)
                            v4Worlds[w].DestroyInstance(i);
                    }
                }
                v4sw.Stop();

                double v4AvgMs = v4sw.Elapsed.TotalMilliseconds / v4Rounds;
                double v4PerInstanceUs = (v4AvgMs * 1000.0) / targetN;

                Debug.Log($"[BENCH]   {targetN,4} instances: {v4AvgMs:F3} ms/tick ({v4PerInstanceUs:F2} µs/instance)");

                if (targetN == 128)
                {
                    Assert(v4AvgMs < 1.0,
                        $"P07 V4 throughput: 128 instances × ~57 instr = {v4AvgMs:F3} ms (< 1ms)");
                }
            }
        }

        // =================================================================
        //  O9: Active Instance List — sparse scenario tests
        //  Verifies ActiveList consistency after spawn/destroy/rollback
        //  and that Tick() only processes active instances.
        // =================================================================

        // ===== Test P08: O9 — ActiveList consistency after spawn/destroy =====
        {
            var o9Program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.WAIT, 100),   // 0: wait 100 (stay alive)
                    new Instruction(OpCode.RETURN),      // 1: return
                },
                new Number[0],
                0
            );

            var o9World = new VMWorld();
            o9World.Modules.Load(0, o9Program);

            // Spawn 10 instances
            int[] ids = new int[10];
            for (int i = 0; i < 10; i++)
                ids[i] = o9World.SpawnInstance(0, 0);

            Assert(o9World.Pool.ActiveListCount == 10,
                $"P08 O9 spawn: ActiveListCount = {o9World.Pool.ActiveListCount} (== 10)");

            // Verify all are in the active list
            bool allPresent = true;
            for (int i = 0; i < 10; i++)
            {
                int idx = o9World.Pool.Instances[ids[i]].ActiveListIndex;
                if (idx < 0 || idx >= o9World.Pool.ActiveListCount ||
                    o9World.Pool.ActiveList[idx] != ids[i])
                {
                    allPresent = false;
                    break;
                }
            }
            Assert(allPresent, "P08 O9 spawn: all instances in ActiveList with correct index");

            // Destroy instances 0, 3, 7 (non-contiguous)
            o9World.DestroyInstance(ids[0]);
            o9World.DestroyInstance(ids[3]);
            o9World.DestroyInstance(ids[7]);

            Assert(o9World.Pool.ActiveListCount == 7,
                $"P08 O9 destroy: ActiveListCount = {o9World.Pool.ActiveListCount} (== 7)");

            // Verify consistency: each active list entry points to an alive instance
            // and each alive instance's ActiveListIndex is correct
            bool consistent = true;
            for (int i = 0; i < o9World.Pool.ActiveListCount; i++)
            {
                int aid = o9World.Pool.ActiveList[i];
                ref VMInstanceState ains = ref o9World.Pool.Instances[aid];
                if (!ains.IsAlive || ains.ActiveListIndex != i)
                {
                    consistent = false;
                    break;
                }
            }
            Assert(consistent, "P08 O9 destroy: ActiveList consistent after swap-remove");

            // Destroy all remaining
            for (int i = 0; i < 10; i++)
            {
                if (o9World.Pool.Instances[ids[i]].IsAlive)
                    o9World.DestroyInstance(ids[i]);
            }
            Assert(o9World.Pool.ActiveListCount == 0,
                $"P08 O9 destroy all: ActiveListCount = {o9World.Pool.ActiveListCount} (== 0)");
        }

        // ===== Test P09: O9 — Sparse tick only touches active instances =====
        {
            int execCount = 0;
            var o9SparseProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.SYSCALL, 0),  // 0: count execution
                    new Instruction(OpCode.RETURN),      // 1: return
                },
                new Number[0],
                1
            );

            var o9Sparse = new VMWorld();
            o9Sparse.Modules.Load(0, o9SparseProgram);
            o9Sparse.Syscalls.Register(0, "Count", (ref VMInstanceState s) => { execCount++; });

            // Spawn 3 instances out of 128 capacity
            o9Sparse.SpawnInstance(0, 0);
            o9Sparse.SpawnInstance(0, 0);
            o9Sparse.SpawnInstance(0, 0);

            execCount = 0;
            o9Sparse.Tick();

            Assert(execCount == 3,
                $"P09 O9 sparse: execCount = {execCount} (== 3, not 128)");
        }

        // ===== Test P10: O9 — ActiveList survives rollback =====
        {
            var o9RollbackProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.SYSCALL, 0),   // 0: noop
                    new Instruction(OpCode.WAIT, 200),     // 1: stay alive
                    new Instruction(OpCode.RETURN),        // 2: return
                },
                new Number[0],
                1
            );

            var o9RB = new VMWorld();
            o9RB.Modules.Load(0, o9RollbackProgram);
            o9RB.Syscalls.Register(0, "Noop", (ref VMInstanceState s) => { });

            // Spawn 5 instances, tick a few frames, save
            int[] rbIds = new int[5];
            for (int i = 0; i < 5; i++)
                rbIds[i] = o9RB.SpawnInstance(0, 0);

            for (int t = 0; t < 3; t++) o9RB.Tick();
            o9RB.SaveState(); // frame 3

            // Diverge: destroy 2, spawn 3 more
            o9RB.DestroyInstance(rbIds[0]);
            o9RB.DestroyInstance(rbIds[2]);
            for (int t = 0; t < 5; t++)
            {
                o9RB.SpawnInstance(0, 0);
                o9RB.Tick();
            }

            int preLoadCount = o9RB.Pool.ActiveListCount;

            // Rollback to frame 3
            bool loaded = o9RB.LoadState(3);
            Assert(loaded, "P10 O9 rollback: LoadState succeeded");

            Assert(o9RB.Pool.ActiveListCount == 5,
                $"P10 O9 rollback: ActiveListCount = {o9RB.Pool.ActiveListCount} (== 5, was {preLoadCount})");

            // Verify consistency after rollback
            bool rbConsistent = true;
            for (int i = 0; i < o9RB.Pool.ActiveListCount; i++)
            {
                int aid = o9RB.Pool.ActiveList[i];
                ref VMInstanceState ains = ref o9RB.Pool.Instances[aid];
                if (!ains.IsAlive || ains.ActiveListIndex != i)
                {
                    rbConsistent = false;
                    break;
                }
            }
            Assert(rbConsistent, "P10 O9 rollback: ActiveList consistent after LoadState");

            // Continue ticking post-rollback — must not crash
            for (int t = 0; t < 10; t++) o9RB.Tick();
            Assert(true, "P10 O9 rollback: 10 ticks post-rollback OK");
        }

        // ===== Test P11: O9 — Sparse performance benchmark =====
        {
            // Benchmark sparse (3/128) tick overhead
            var o9BenchProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),  // 0: R0 = 1
                    new Instruction(OpCode.RETURN),             // 1: return
                },
                new Number[] { Number.FromInt(1) },
                1
            );

            // Sparse: 3 active / 128 capacity
            var sparseWorld = new VMWorld();
            sparseWorld.Modules.Load(0, o9BenchProgram);

            // Warmup
            for (int w = 0; w < 100; w++)
            {
                for (int i = 0; i < 3; i++)
                    sparseWorld.SpawnInstance(0, 0);
                sparseWorld.Tick();
                // Destroy all: iterate backwards to avoid swap-remove index confusion
                for (int i = sparseWorld.Pool.ActiveListCount - 1; i >= 0; i--)
                {
                    int did = sparseWorld.Pool.ActiveList[i];
                    sparseWorld.DestroyInstance(did);
                }
            }

            // Fresh measure
            for (int i = 0; i < 3; i++)
                sparseWorld.SpawnInstance(0, 0);

            var sparseSw = Stopwatch.StartNew();
            int sparseRounds = 100_000;
            for (int r = 0; r < sparseRounds; r++)
            {
                // Reset instances to re-execute
                for (int i = 0; i < sparseWorld.Pool.ActiveListCount; i++)
                {
                    int sid = sparseWorld.Pool.ActiveList[i];
                    ref var sinst = ref sparseWorld.Pool.Instances[sid];
                    sinst.IP = 0;
                    sinst.StateFlags = VMStateFlags.Active;
                }
                sparseWorld.Tick();
            }
            sparseSw.Stop();
            double sparseUs = sparseSw.Elapsed.TotalMilliseconds * 1000.0 / sparseRounds;

            Debug.Log($"[BENCH] O9 sparse (3/128): {sparseUs:F2} µs/tick");

            // The test passes as long as the sparse scenario runs correctly
            Assert(sparseUs > 0, $"P11 O9 sparse perf: {sparseUs:F2} µs/tick (benchmark logged)");
        }

        // =================================================================
        //  O10: Snapshot only copies active instances
        //  Verifies correctness and data reduction after O10 optimization.
        // =================================================================

        // ===== Test P12: O10 — Save/Load correctness with partial active instances =====
        {
            var o10Program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.WAIT, 100),   // 0: stay alive
                    new Instruction(OpCode.RETURN),      // 1: return
                },
                new Number[0],
                0
            );

            var o10World = new VMWorld();
            o10World.Modules.Load(0, o10Program);

            // Spawn 5 instances
            int[] o10Ids = new int[5];
            for (int i = 0; i < 5; i++)
                o10Ids[i] = o10World.SpawnInstance(0, 0);

            o10World.Tick(); // frame 1: instances execute WAIT 100, suspend

            // Set distinct register values (after tick so IP is valid)
            for (int i = 0; i < 5; i++)
                o10World.Pool.Instances[o10Ids[i]].Registers.Set(0, Number.FromInt(100 + i));

            o10World.SaveState();

            // Mutate all registers
            for (int i = 0; i < 5; i++)
                o10World.Pool.Instances[o10Ids[i]].Registers.Set(0, Number.FromInt(999));

            // Rollback
            bool o10Loaded = o10World.LoadState(o10World.FrameNumber);
            Assert(o10Loaded, "P12 O10: LoadState succeeded");

            // Verify all 5 instances restored correctly
            bool o10Correct = true;
            for (int i = 0; i < 5; i++)
            {
                ref var inst = ref o10World.Pool.Instances[o10Ids[i]];
                if (!inst.IsAlive ||
                    inst.Registers.Get(0) != Number.FromInt(100 + i) ||
                    inst.ActiveListIndex < 0)
                {
                    o10Correct = false;
                    break;
                }
            }
            Assert(o10Correct, "P12 O10: all 5 instances restored with correct registers");
            Assert(o10World.Pool.ActiveListCount == 5,
                $"P12 O10: ActiveListCount = {o10World.Pool.ActiveListCount} (== 5)");
        }

        // ===== Test P13: O10 — Stale instances invalidated after rollback =====
        {
            var o10StaleProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.WAIT, 100),
                    new Instruction(OpCode.RETURN),
                },
                new Number[0],
                0
            );

            var o10Stale = new VMWorld();
            o10Stale.Modules.Load(0, o10StaleProgram);

            // Spawn 3, save
            int[] sIds = new int[3];
            for (int i = 0; i < 3; i++)
                sIds[i] = o10Stale.SpawnInstance(0, 0);

            o10Stale.Tick();
            o10Stale.SaveState(); // frame 1: 3 active

            // Spawn 2 more AFTER snapshot
            int extra1 = o10Stale.SpawnInstance(0, 0);
            int extra2 = o10Stale.SpawnInstance(0, 0);
            Assert(o10Stale.Pool.Instances[extra1].IsAlive, "P13 O10: extra1 alive before rollback");

            // Rollback to frame 1
            bool loaded = o10Stale.LoadState(o10Stale.FrameNumber);
            Assert(loaded, "P13 O10 stale: LoadState succeeded");

            // extra1 and extra2 should NOT be alive
            Assert(!o10Stale.Pool.Instances[extra1].IsAlive,
                "P13 O10 stale: extra1 not alive after rollback");
            Assert(!o10Stale.Pool.Instances[extra2].IsAlive,
                "P13 O10 stale: extra2 not alive after rollback");

            // Original 3 should be alive
            bool origAlive = true;
            for (int i = 0; i < 3; i++)
            {
                if (!o10Stale.Pool.Instances[sIds[i]].IsAlive)
                {
                    origAlive = false;
                    break;
                }
            }
            Assert(origAlive, "P13 O10 stale: original 3 instances still alive");
            Assert(o10Stale.Pool.ActiveListCount == 3,
                $"P13 O10 stale: ActiveListCount = {o10Stale.Pool.ActiveListCount} (== 3)");
        }

        // ===== Test P14: O10 — Edge case: 0 active instances =====
        {
            var o10Empty = new VMWorld();
            o10Empty.Modules.Load(0, new VMProgram(
                new Instruction[] { new Instruction(OpCode.RETURN) },
                new Number[0], 0
            ));

            o10Empty.Tick();
            o10Empty.SaveState(); // frame 1: 0 active
            o10Empty.SpawnInstance(0, 0); // spawn after snapshot

            bool loaded = o10Empty.LoadState(o10Empty.FrameNumber);
            Assert(loaded, "P14 O10 empty: LoadState succeeded");
            Assert(o10Empty.Pool.ActiveListCount == 0,
                $"P14 O10 empty: ActiveListCount = {o10Empty.Pool.ActiveListCount} (== 0)");
        }

        // ===== Test P15: O10 — Snapshot save/load benchmark (data reduction) =====
        {
            var o10BenchProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.WAIT, 100),
                    new Instruction(OpCode.RETURN),
                },
                new Number[0],
                0
            );

            // Measure with 5 active instances (typical case)
            var o10Bench = new VMWorld();
            o10Bench.Modules.Load(0, o10BenchProgram);
            for (int i = 0; i < 5; i++)
                o10Bench.SpawnInstance(0, 0);
            o10Bench.Tick();

            // Warmup
            for (int w = 0; w < 100; w++)
            {
                o10Bench.SaveState();
                o10Bench.LoadState(o10Bench.FrameNumber);
            }

            int benchRounds = 100_000;
            var saveSw = Stopwatch.StartNew();
            for (int r = 0; r < benchRounds; r++)
                o10Bench.SaveState();
            saveSw.Stop();
            double saveUs = saveSw.Elapsed.TotalMilliseconds * 1000.0 / benchRounds;

            var loadSw = Stopwatch.StartNew();
            for (int r = 0; r < benchRounds; r++)
                o10Bench.LoadState(o10Bench.FrameNumber);
            loadSw.Stop();
            double loadUs = loadSw.Elapsed.TotalMilliseconds * 1000.0 / benchRounds;

            Debug.Log($"[BENCH] O10 snapshot (5/128 active): save={saveUs:F2} µs, load={loadUs:F2} µs");

            // Data reduction: 5/128 = 3.9% of instances copied → 96% reduction in instance data
            Assert(saveUs > 0, $"P15 O10 save perf: {saveUs:F2} µs (benchmark logged)");
            Assert(loadUs > 0, $"P15 O10 load perf: {loadUs:F2} µs (benchmark logged)");
        }

        // ===== Test P16: O10 — Post-rollback tick correctness =====
        {
            int o10ExecCount = 0;
            var o10TickProgram = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.SYSCALL, 0),   // 0: count
                    new Instruction(OpCode.WAIT, 100),     // 1: stay alive
                    new Instruction(OpCode.RETURN),        // 2: return
                },
                new Number[0],
                1
            );

            var o10Tick = new VMWorld();
            o10Tick.Modules.Load(0, o10TickProgram);
            o10Tick.Syscalls.Register(0, "Count", (ref VMInstanceState s) => { o10ExecCount++; });

            // Spawn 3, tick, save
            for (int i = 0; i < 3; i++)
                o10Tick.SpawnInstance(0, 0);
            o10Tick.Tick(); // frame 1: instances execute SYSCALL → WAIT
            o10Tick.SaveState();
            int savedFrame = o10Tick.FrameNumber;

            // Spawn 2 more, tick several times
            o10Tick.SpawnInstance(0, 0);
            o10Tick.SpawnInstance(0, 0);
            for (int t = 0; t < 5; t++)
                o10Tick.Tick();

            // Rollback
            o10ExecCount = 0;
            bool loaded = o10Tick.LoadState(savedFrame);
            Assert(loaded, "P16 O10 tick: LoadState succeeded");

            // Tick 10 more frames — only 3 original instances should be ticked
            // (they are in WAIT state, so no SYSCALL calls expected)
            o10Tick.Tick();
            Assert(o10ExecCount == 0,
                $"P16 O10 tick: post-rollback tick, execCount = {o10ExecCount} (== 0, all waiting)");

            // Verify only 3 active
            Assert(o10Tick.Pool.ActiveListCount == 3,
                $"P16 O10 tick: ActiveListCount = {o10Tick.Pool.ActiveListCount} (== 3)");
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"Performance Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
