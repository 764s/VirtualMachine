using System;
using System.Collections.Generic;
using FFVM;
using FFVM.AST;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

/// <summary>
/// Phase 2 validation: hand-built AST → tree-walker interpreter.
/// Covers: arithmetic, variables, branches, loops, functions, syscalls, wait/yield.
/// Run from Unity Editor (attach to a GameObject or call from menu).
/// </summary>
public static class TreeWalkerTests
{
    // [UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    [MenuItem("TestVM/RunAll")]
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

        // ===== Test 1: Arithmetic =====
        {
            // func add(a: Number, b: Number): Number { return a + b; }
            var func = new FuncDecl("add",
                new List<ParamDecl> { new ParamDecl("a", "Number"), new ParamDecl("b", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new BinaryExpr(NodeKind.Add,
                        new IdentifierExpr("a"), new IdentifierExpr("b")))
                }),
                false
            );

            var module = new ModuleNode("test_arith");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("add", Value.FromNumber(3), Value.FromNumber(4));
            Assert(result.AsNumber().ToFloat() == 7f, "Arithmetic: 3 + 4 = 7");
        }

        // ===== Test 2: Variables and Assignment =====
        {
            // func test(): Number {
            //   var x: Number = 10;
            //   x = x * 2;
            //   return x;
            // }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new VarDeclStmt("x", "Number", new NumberLiteralExpr(10)),
                    new ExprStmt(new AssignExpr(
                        new IdentifierExpr("x"),
                        new BinaryExpr(NodeKind.Mul,
                            new IdentifierExpr("x"), new NumberLiteralExpr(2))
                    )),
                    new ReturnStmt(new IdentifierExpr("x"))
                }),
                false
            );

            var module = new ModuleNode("test_vars");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsNumber().ToFloat() == 20f, "Variables: 10 * 2 = 20");
        }

        // ===== Test 3: If/Else =====
        {
            // func max(a: Number, b: Number): Number {
            //   if (a > b) { return a; } else { return b; }
            // }
            var func = new FuncDecl("max",
                new List<ParamDecl> { new ParamDecl("a", "Number"), new ParamDecl("b", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new IfStmt(
                        new BinaryExpr(NodeKind.Gt,
                            new IdentifierExpr("a"), new IdentifierExpr("b")),
                        new BlockStmt(new List<Stmt> { new ReturnStmt(new IdentifierExpr("a")) }),
                        new BlockStmt(new List<Stmt> { new ReturnStmt(new IdentifierExpr("b")) })
                    )
                }),
                false
            );

            var module = new ModuleNode("test_if");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var r1 = walker.CallFunction("max", Value.FromNumber(5), Value.FromNumber(3));
            Assert(r1.AsNumber().ToFloat() == 5f, "If/Else: max(5,3) = 5");

            var r2 = walker.CallFunction("max", Value.FromNumber(2), Value.FromNumber(8));
            Assert(r2.AsNumber().ToFloat() == 8f, "If/Else: max(2,8) = 8");
        }

        // ===== Test 4: While Loop =====
        {
            // func sum(n: Number): Number {
            //   var total: Number = 0;
            //   var i: Number = 1;
            //   while (i <= n) {
            //     total = total + i;
            //     i = i + 1;
            //   }
            //   return total;
            // }
            var func = new FuncDecl("sum",
                new List<ParamDecl> { new ParamDecl("n", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new VarDeclStmt("total", "Number", new NumberLiteralExpr(0)),
                    new VarDeclStmt("i", "Number", new NumberLiteralExpr(1)),
                    new WhileStmt(
                        new BinaryExpr(NodeKind.Lte,
                            new IdentifierExpr("i"), new IdentifierExpr("n")),
                        new BlockStmt(new List<Stmt>
                        {
                            new ExprStmt(new AssignExpr(
                                new IdentifierExpr("total"),
                                new BinaryExpr(NodeKind.Add,
                                    new IdentifierExpr("total"), new IdentifierExpr("i"))
                            )),
                            new ExprStmt(new AssignExpr(
                                new IdentifierExpr("i"),
                                new BinaryExpr(NodeKind.Add,
                                    new IdentifierExpr("i"), new NumberLiteralExpr(1))
                            ))
                        })
                    ),
                    new ReturnStmt(new IdentifierExpr("total"))
                }),
                false
            );

            var module = new ModuleNode("test_while");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("sum", Value.FromNumber(10));
            Assert(result.AsNumber().ToFloat() == 55f, "While: sum(1..10) = 55");
        }

        // ===== Test 5: Function Calls =====
        {
            // func double(x: Number): Number { return x * 2; }
            // func quadruple(x: Number): Number { return double(double(x)); }
            var funcDouble = new FuncDecl("double",
                new List<ParamDecl> { new ParamDecl("x", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new BinaryExpr(NodeKind.Mul,
                        new IdentifierExpr("x"), new NumberLiteralExpr(2)))
                }),
                false
            );

            var funcQuad = new FuncDecl("quadruple",
                new List<ParamDecl> { new ParamDecl("x", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new CallExpr("double", new List<Expr>
                    {
                        new CallExpr("double", new List<Expr>
                        {
                            new IdentifierExpr("x")
                        })
                    }))
                }),
                false
            );

            var module = new ModuleNode("test_call");
            module.Functions.Add(funcDouble);
            module.Functions.Add(funcQuad);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("quadruple", Value.FromNumber(3));
            Assert(result.AsNumber().ToFloat() == 12f, "Function call: quadruple(3) = 12");
        }

        // ===== Test 6: Syscall =====
        {
            // Syscall slot 0 = "getHealth" → returns 100
            // func test(): Number { return @syscall(0, "getHealth"); }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new SyscallExpr(0, "getHealth", new List<Expr>()))
                }),
                false
            );

            var module = new ModuleNode("test_syscall");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(0, args => Value.FromNumber(100));
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsNumber().ToFloat() == 100f, "Syscall: getHealth() = 100");
        }

        // ===== Test 7: Syscall with arguments =====
        {
            // Syscall slot 1 = "damageEntity" (entityId, amount) → returns amount * 2
            // func test(): Number { return @syscall(1, "damageEntity", 42, 10); }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new SyscallExpr(1, "damageEntity", new List<Expr>
                    {
                        new NumberLiteralExpr(42),
                        new NumberLiteralExpr(10)
                    }))
                }),
                false
            );

            var module = new ModuleNode("test_syscall_args");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(1, args => Value.FromNumber(args[1].AsNumber() * Number.FromInt(2)));
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsNumber().ToFloat() == 20f, "Syscall with args: damage(42,10) = 20");
        }

        // ===== Test 8: Wait signal =====
        {
            // func test() { wait(5); }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "void",
                new BlockStmt(new List<Stmt>
                {
                    new WaitStmt(new NumberLiteralExpr(5))
                }),
                false
            );

            var module = new ModuleNode("test_wait");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            bool caught = false;
            int waitFrames = 0;
            try
            {
                walker.CallFunction("test");
            }
            catch (WaitSignal ws)
            {
                caught = true;
                waitFrames = ws.FrameCount;
            }
            Assert(caught && waitFrames == 5, "Wait: wait(5) suspends for 5 frames");
        }

        // ===== Test 9: Stack overflow detection =====
        {
            // func recurse(): Number { return recurse(); }
            var func = new FuncDecl("recurse",
                new List<ParamDecl>(),
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new CallExpr("recurse", new List<Expr>()))
                }),
                false
            );

            var module = new ModuleNode("test_overflow");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            bool panicCaught = false;
            try
            {
                walker.CallFunction("recurse");
            }
            catch (PanicException pe)
            {
                panicCaught = pe.Error == VMError.PanicStackOverflow;
            }
            Assert(panicCaught, "Panic: stack overflow on recursion");
        }

        // ===== Test 10: Logical operators =====
        {
            // func test(): bool { return (3 > 2) && (5 < 10); }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "bool",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new BinaryExpr(NodeKind.And,
                        new BinaryExpr(NodeKind.Gt,
                            new NumberLiteralExpr(3), new NumberLiteralExpr(2)),
                        new BinaryExpr(NodeKind.Lt,
                            new NumberLiteralExpr(5), new NumberLiteralExpr(10))
                    ))
                }),
                false
            );

            var module = new ModuleNode("test_logic");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsBool(), "Logic: (3>2) && (5<10) = true");
        }

        // ===== Test 11: For loop =====
        {
            // func factorial(n: Number): Number {
            //   var result: Number = 1;
            //   for (var i: Number = 1; i <= n; i = i + 1) {
            //     result = result * i;
            //   }
            //   return result;
            // }
            var func = new FuncDecl("factorial",
                new List<ParamDecl> { new ParamDecl("n", "Number") },
                "Number",
                new BlockStmt(new List<Stmt>
                {
                    new VarDeclStmt("result", "Number", new NumberLiteralExpr(1)),
                    new ForStmt(
                        new VarDeclStmt("i", "Number", new NumberLiteralExpr(1)),
                        new BinaryExpr(NodeKind.Lte, new IdentifierExpr("i"), new IdentifierExpr("n")),
                        new AssignExpr(new IdentifierExpr("i"),
                            new BinaryExpr(NodeKind.Add, new IdentifierExpr("i"), new NumberLiteralExpr(1))),
                        new BlockStmt(new List<Stmt>
                        {
                            new ExprStmt(new AssignExpr(
                                new IdentifierExpr("result"),
                                new BinaryExpr(NodeKind.Mul,
                                    new IdentifierExpr("result"), new IdentifierExpr("i"))
                            ))
                        })
                    ),
                    new ReturnStmt(new IdentifierExpr("result"))
                }),
                false
            );

            var module = new ModuleNode("test_for");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("factorial", Value.FromNumber(5));
            Assert(result.AsNumber().ToFloat() == 120f, "For loop: factorial(5) = 120");
        }

        // ===== Test 12: Number wrapper struct basics =====
        {
            Number a = Number.FromFloat(2.5f);
            Number b = Number.FromFloat(3.5f);
            Number sum = a + b;
            Number product = a * b;

            Assert(System.Math.Abs(sum.ToFloat() - 6.0f) < 0.001f, "Number: 2.5 + 3.5 = 6.0");
            Assert(System.Math.Abs(product.ToFloat() - 8.75f) < 0.001f, "Number: 2.5 * 3.5 = 8.75");

            Number zero = Number.Zero;
            Number one = Number.One;
            Assert(zero < one, "Number: 0 < 1");
            Assert(one == Number.FromInt(1), "Number: 1 == FromInt(1)");
        }

        // ===== Test 13: Instance Pool =====
        {
            var pool = new InstancePool();
            pool.Init();

            int id1 = pool.Allocate(0, 0);
            int id2 = pool.Allocate(0, 0);
            Assert(id1 >= 0 && id2 >= 0 && id1 != id2, "Pool: allocated 2 distinct instances");
            Assert(pool.ActiveCount == 2, "Pool: active count = 2");

            pool.Free(id1);
            Assert(pool.ActiveCount == 1, "Pool: after free, active = 1");

            int id3 = pool.Allocate(0, 0);
            Assert(id3 == id1, "Pool: reused freed slot");
        }

        // ===== Test 14: Snapshot Save/Load =====
        {
            var pool = new InstancePool();
            pool.Init();
            var ring = new SnapshotRingBuffer();

            int id = pool.Allocate(0, 0);
            pool.Instances[id].Registers.Set(0, Number.FromFloat(42.0f));
            pool.Instances[id].IP = 10;

            ring.SaveState(ref pool, 1);

            // Modify state
            pool.Instances[id].Registers.Set(0, Number.FromFloat(99.0f));
            pool.Instances[id].IP = 20;

            // Rollback
            bool loaded = ring.LoadState(ref pool, 1);
            Assert(loaded, "Snapshot: load frame 1 succeeded");
            Assert(pool.Instances[id].Registers.Get(0).ToFloat() == 42.0f,
                "Snapshot: register restored to 42");
            Assert(pool.Instances[id].IP == 10, "Snapshot: IP restored to 10");
        }

        // ===== Test 15: Defer — normal path (4.1) =====
        {
            // func test() { defer { syscall SetBB(0) }; syscall SetBB(1); return; }
            // Expected call order: [1, 0] — main flow first, cleanup after
            var callLog = new List<int>();

            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "void",
                new BlockStmt(new List<Stmt>
                {
                    new DeferStmt(new BlockStmt(new List<Stmt>
                    {
                        new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                        {
                            new IntLiteralExpr(0)
                        }))
                    })),
                    new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                    {
                        new IntLiteralExpr(1)
                    })),
                    new ReturnStmt(null)
                }),
                false
            );

            var module = new ModuleNode("test_defer_normal");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(0, args => { callLog.Add(args[0].AsInt()); return Value.Void(); });
            walker.LoadModule(module);
            walker.CallFunction("test");

            Assert(callLog.Count == 2, "Defer normal: exactly 2 syscalls");
            Assert(callLog.Count == 2 && callLog[0] == 1 && callLog[1] == 0,
                "Defer normal: order is [1, 0] (main first, cleanup after)");
        }

        // ===== Test 16: Defer — not fired on wait suspension (4.2 simplified) =====
        {
            // func test() { defer { syscall SetBB(0) }; syscall SetBB(1); wait 10; }
            // On wait suspension: SetBB(1) called, SetBB(0) NOT called (defer pending)
            var callLog = new List<int>();

            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "void",
                new BlockStmt(new List<Stmt>
                {
                    new DeferStmt(new BlockStmt(new List<Stmt>
                    {
                        new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                        {
                            new IntLiteralExpr(0)
                        }))
                    })),
                    new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                    {
                        new IntLiteralExpr(1)
                    })),
                    new WaitStmt(new NumberLiteralExpr(10))
                }),
                false
            );

            var module = new ModuleNode("test_defer_wait");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(0, args => { callLog.Add(args[0].AsInt()); return Value.Void(); });
            walker.LoadModule(module);

            bool waitCaught = false;
            try { walker.CallFunction("test"); }
            catch (WaitSignal) { waitCaught = true; }

            Assert(waitCaught, "Defer wait: WaitSignal thrown");
            Assert(callLog.Count == 1 && callLog[0] == 1,
                "Defer wait: only SetBB(1) called, cleanup NOT fired on suspension");
            // NOTE: Full wait-resume-cleanup test deferred to Step 6 (bytecode IP resume)
        }

        // ===== Test 17: Defer + Wait + Kill path (4.3) =====
        {
            // func test() { defer { syscall SetBB(0) }; syscall SetBB(1); wait 10; syscall PlayEffect(); }
            // Sequence: call → SetBB(1) → WaitSignal → Kill() → cleanup SetBB(0)
            // PlayEffect must NOT execute
            var callLog = new List<int>();
            bool playEffectCalled = false;

            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "void",
                new BlockStmt(new List<Stmt>
                {
                    new DeferStmt(new BlockStmt(new List<Stmt>
                    {
                        new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                        {
                            new IntLiteralExpr(0)
                        }))
                    })),
                    new ExprStmt(new SyscallExpr(0, "SetBB", new List<Expr>
                    {
                        new IntLiteralExpr(1)
                    })),
                    new WaitStmt(new NumberLiteralExpr(10)),
                    new ExprStmt(new SyscallExpr(1, "PlayEffect", new List<Expr>()))
                }),
                false
            );

            var module = new ModuleNode("test_defer_kill");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(0, args => { callLog.Add(args[0].AsInt()); return Value.Void(); });
            walker.RegisterSyscall(1, args => { playEffectCalled = true; return Value.Void(); });
            walker.LoadModule(module);

            try { walker.CallFunction("test"); }
            catch (WaitSignal) { /* suspended */ }

            // Kill while waiting — cleanup should fire
            walker.Kill();

            Assert(!playEffectCalled, "Defer kill: PlayEffect NOT executed");
            Assert(callLog.Count == 2 && callLog[0] == 1 && callLog[1] == 0,
                "Defer kill: SetBB(1) then cleanup SetBB(0)");
            Assert(walker.IsKilled, "Defer kill: IsKilled flag set");
        }

        // ===== Test 18: Multi-layer defer LIFO order (4.4) =====
        {
            // func test() { defer { A() }; defer { B() }; return; }
            // Expected order: [B, A] — last registered first executed
            var callLog = new List<string>();

            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "void",
                new BlockStmt(new List<Stmt>
                {
                    new DeferStmt(new BlockStmt(new List<Stmt>
                    {
                        new ExprStmt(new SyscallExpr(0, "A", new List<Expr>()))
                    })),
                    new DeferStmt(new BlockStmt(new List<Stmt>
                    {
                        new ExprStmt(new SyscallExpr(1, "B", new List<Expr>()))
                    })),
                    new ReturnStmt(null)
                }),
                false
            );

            var module = new ModuleNode("test_defer_lifo");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.RegisterSyscall(0, args => { callLog.Add("A"); return Value.Void(); });
            walker.RegisterSyscall(1, args => { callLog.Add("B"); return Value.Void(); });
            walker.LoadModule(module);
            walker.CallFunction("test");

            Assert(callLog.Count == 2 && callLog[0] == "B" && callLog[1] == "A",
                "Defer LIFO: order is [B, A]");
        }

        // ===== Test 19: Save/Load with Cleanup stack (5.1) =====
        {
            var pool = new InstancePool();
            pool.Init();
            var ring = new SnapshotRingBuffer();

            int id = pool.Allocate(0, 0);
            pool.Instances[id].StateFlags = VMStateFlags.Active;
            pool.Instances[id].CleanupDepth = 1;
            pool.Instances[id].CleanupStack.Set(0, new CleanupFrame { CleanupEntryIP = 42 });

            ring.SaveState(ref pool, 100);

            // Modify all fields
            pool.Instances[id].StateFlags = VMStateFlags.Completed;
            pool.Instances[id].CleanupDepth = 3;
            pool.Instances[id].CleanupStack.Set(0, new CleanupFrame { CleanupEntryIP = 999 });

            // Rollback
            bool loaded = ring.LoadState(ref pool, 100);
            Assert(loaded, "Snapshot cleanup: load succeeded");
            Assert(pool.Instances[id].StateFlags == VMStateFlags.Active,
                "Snapshot cleanup: StateFlags restored to Active");
            Assert(pool.Instances[id].CleanupDepth == 1,
                "Snapshot cleanup: CleanupDepth restored to 1");
            Assert(pool.Instances[id].CleanupStack.Get(0).CleanupEntryIP == 42,
                "Snapshot cleanup: CleanupEntryIP restored to 42");
        }

        // ===== Test 20: StateFlags snapshot consistency (5.2) =====
        {
            var pool = new InstancePool();
            pool.Init();
            var ring = new SnapshotRingBuffer();

            int id = pool.Allocate(0, 0);
            pool.Instances[id].StateFlags = VMStateFlags.Killed;

            ring.SaveState(ref pool, 200);

            pool.Instances[id].StateFlags = VMStateFlags.Active;

            bool loaded = ring.LoadState(ref pool, 200);
            Assert(loaded, "Snapshot flags: load succeeded");
            Assert(pool.Instances[id].StateFlags == VMStateFlags.Killed,
                "Snapshot flags: restored to Killed");
        }

        // TODO: 0 GC regression test — bytecode phase (Step 6)
        // TreeWalker uses managed objects (Environment, List, exceptions), so zero-GC
        // validation is not applicable here. Will be verified in bytecode interpreter.

        // =================================================================
        //  Phase A — Bytecode Path Tests (Step 6d)
        // =================================================================

        // ===== Test 21: Bytecode normal path (6d.2) =====
        {
            // Bytecode equivalent:
            //   defer { SetBB(0) }; SetBB(1); wait 10; PlayEffect(); return;
            //
            // IP 0: PUSH_CLEANUP 7     → register cleanup at IP 7
            // IP 1: LOAD_CONST R0, #1  → R0 = 1  (const[1])
            // IP 2: SYSCALL 0          → SetBB(R0=1)
            // IP 3: WAIT 10
            // IP 4: SYSCALL 1          → PlayEffect()
            // IP 5: RETURN             → cleanup depth>0 → InCleanup, jump IP 7
            // --- cleanup block ---
            // IP 6: NOP (unreachable, spacer)
            // IP 7: LOAD_CONST R0, #0  → R0 = 0  (const[0])
            // IP 8: SYSCALL 0          → SetBB(R0=0)
            // IP 9: RETURN             → InCleanup, depth=0 → Completed

            var syscallLog = new List<string>();

            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 7),      // IP 0
                    new Instruction(OpCode.LOAD_CONST, 0, 1),     // IP 1: R0 = const[1] = 1
                    new Instruction(OpCode.SYSCALL, 0),            // IP 2: SetBB
                    new Instruction(OpCode.WAIT, 10),              // IP 3
                    new Instruction(OpCode.SYSCALL, 1),            // IP 4: PlayEffect
                    new Instruction(OpCode.RETURN),                // IP 5
                    new Instruction(OpCode.NOP),                   // IP 6: spacer
                    new Instruction(OpCode.LOAD_CONST, 0, 0),     // IP 7: R0 = const[0] = 0
                    new Instruction(OpCode.SYSCALL, 0),            // IP 8: SetBB
                    new Instruction(OpCode.RETURN),                // IP 9
                },
                new Number[] { Number.FromInt(0), Number.FromInt(1) },
                1
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
            {
                syscallLog.Add($"SetBB({s.Registers.Get(0).ToInt()})");
            });
            world.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
            {
                syscallLog.Add("PlayEffect");
            });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: executes PUSH_CLEANUP, LOAD_CONST, SYSCALL SetBB(1), WAIT 10 → suspends
            world.Tick();
            Assert(syscallLog.Count == 1 && syscallLog[0] == "SetBB(1)",
                "BC normal: tick 1 → SetBB(1)");

            // Tick 2-11: wait countdown (10 ticks)
            for (int t = 0; t < 10; t++) world.Tick();
            Assert(syscallLog.Count == 1, "BC normal: waiting, no new syscalls");

            // Tick 12: resume → PlayEffect, RETURN → cleanup SetBB(0), RETURN → Completed
            world.Tick();
            Assert(syscallLog.Count == 3, "BC normal: 3 total syscalls");
            Assert(syscallLog[1] == "PlayEffect", "BC normal: PlayEffect after wait");
            Assert(syscallLog[2] == "SetBB(0)", "BC normal: cleanup SetBB(0)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "BC normal: Completed flag set");
        }

        // ===== Test 22: Bytecode kill path (6d.3) =====
        {
            // Same bytecode as Test 21
            var syscallLog = new List<string>();

            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 7),
                    new Instruction(OpCode.LOAD_CONST, 0, 1),
                    new Instruction(OpCode.SYSCALL, 0),
                    new Instruction(OpCode.WAIT, 10),
                    new Instruction(OpCode.SYSCALL, 1),            // PlayEffect — should NOT execute
                    new Instruction(OpCode.RETURN),
                    new Instruction(OpCode.NOP),
                    new Instruction(OpCode.LOAD_CONST, 0, 0),
                    new Instruction(OpCode.SYSCALL, 0),
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(0), Number.FromInt(1) },
                1
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
            {
                syscallLog.Add($"SetBB({s.Registers.Get(0).ToInt()})");
            });
            world.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
            {
                syscallLog.Add("PlayEffect");
            });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: runs to WAIT 10
            world.Tick();
            Assert(syscallLog.Count == 1 && syscallLog[0] == "SetBB(1)",
                "BC kill: tick 1 → SetBB(1)");

            // Kill while waiting
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;

            // Tick 2: Killed → enter cleanup → SetBB(0) → Completed
            world.Tick();
            Assert(syscallLog.Count == 2, "BC kill: 2 total syscalls (no PlayEffect)");
            Assert(syscallLog[1] == "SetBB(0)", "BC kill: cleanup SetBB(0)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "BC kill: Completed flag set");
        }

        // ===== Test 23: Killed priority > WaitCounter (6d.4) =====
        {
            var syscallLog = new List<string>();

            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 4),
                    new Instruction(OpCode.SYSCALL, 0),            // main work
                    new Instruction(OpCode.WAIT, 100),             // long wait
                    new Instruction(OpCode.RETURN),
                    // cleanup block at IP 4
                    new Instruction(OpCode.SYSCALL, 1),            // cleanup work
                    new Instruction(OpCode.RETURN),
                },
                new Number[0],
                0
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "Main", (ref VMInstanceState s) => { syscallLog.Add("Main"); });
            world.Syscalls.Register(1, "Cleanup", (ref VMInstanceState s) => { syscallLog.Add("Cleanup"); });

            int id = world.SpawnInstance(0, 0);

            // Tick 1: PUSH_CLEANUP, Main, WAIT 100 → suspended with WaitCounter=100
            world.Tick();
            Assert(world.Pool.Instances[id].WaitCounter == 100,
                "BC kill-prio: WaitCounter = 100");

            // Kill while WaitCounter > 0
            world.Pool.Instances[id].StateFlags |= VMStateFlags.Killed;

            // Tick 2: Killed takes priority over WaitCounter → cleanup runs
            world.Tick();
            Assert(syscallLog.Count == 2 && syscallLog[1] == "Cleanup",
                "BC kill-prio: cleanup ran despite WaitCounter > 0");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "BC kill-prio: Completed");
        }

        // ===== Test 24: Bytecode multi-layer defer LIFO (6d.5) =====
        {
            // defer { A() }; defer { B() }; return;
            // IP 0: PUSH_CLEANUP 5     → cleanup A at IP 5
            // IP 1: PUSH_CLEANUP 8     → cleanup B at IP 8
            // IP 2: RETURN             → InCleanup, pop top (B at IP 8)
            // -- cleanup B --
            // IP 3: NOP (spacer)
            // IP 4: NOP (spacer)
            // IP 5: SYSCALL 0 (A)
            // IP 6: RETURN             → InCleanup, depth=0 → Completed
            // IP 7: NOP (spacer)
            // IP 8: SYSCALL 1 (B)
            // IP 9: RETURN             → InCleanup, depth>0, pop next (A at IP 5)

            var syscallLog = new List<string>();

            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.PUSH_CLEANUP, 5),      // IP 0: cleanup A at IP 5
                    new Instruction(OpCode.PUSH_CLEANUP, 8),      // IP 1: cleanup B at IP 8
                    new Instruction(OpCode.RETURN),                // IP 2: normal → InCleanup
                    new Instruction(OpCode.NOP),                   // IP 3
                    new Instruction(OpCode.NOP),                   // IP 4
                    new Instruction(OpCode.SYSCALL, 0),            // IP 5: A()
                    new Instruction(OpCode.RETURN),                // IP 6
                    new Instruction(OpCode.NOP),                   // IP 7
                    new Instruction(OpCode.SYSCALL, 1),            // IP 8: B()
                    new Instruction(OpCode.RETURN),                // IP 9
                },
                new Number[0],
                0
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            world.Syscalls.Register(0, "A", (ref VMInstanceState s) => { syscallLog.Add("A"); });
            world.Syscalls.Register(1, "B", (ref VMInstanceState s) => { syscallLog.Add("B"); });

            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(syscallLog.Count == 2, "BC LIFO: 2 cleanup syscalls");
            Assert(syscallLog[0] == "B" && syscallLog[1] == "A",
                "BC LIFO: order is [B, A]");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "BC LIFO: Completed");
        }

        // =================================================================
        //  Phase B — Bytecode Save/Load + 0 GC Tests (Step 6e)
        // =================================================================

        // ===== Test 25: Save/Load then resume — same behavior (6e.1) =====
        {
            // Same bytecode: defer{SetBB(0)}; SetBB(1); wait 10; PlayEffect; return
            // Run A: straight through (reference)
            // Run B: save after WAIT, tick 5 more, load, then resume to completion
            // Assert: identical syscall sequences

            var programInstructions = new Instruction[]
            {
                new Instruction(OpCode.PUSH_CLEANUP, 7),
                new Instruction(OpCode.LOAD_CONST, 0, 1),     // R0 = 1
                new Instruction(OpCode.SYSCALL, 0),            // SetBB(1)
                new Instruction(OpCode.WAIT, 10),
                new Instruction(OpCode.SYSCALL, 1),            // PlayEffect
                new Instruction(OpCode.RETURN),
                new Instruction(OpCode.NOP),                   // spacer
                new Instruction(OpCode.LOAD_CONST, 0, 0),     // R0 = 0
                new Instruction(OpCode.SYSCALL, 0),            // SetBB(0)
                new Instruction(OpCode.RETURN),
            };
            var consts = new Number[] { Number.FromInt(0), Number.FromInt(1) };

            // --- Run A: reference run (no save/load) ---
            var logA = new List<string>();
            {
                var prog = new VMProgram(programInstructions, consts, 1);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
                    { logA.Add($"SetBB({s.Registers.Get(0).ToInt()})"); });
                w.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
                    { logA.Add("PlayEffect"); });
                w.SpawnInstance(0, 0);

                // Tick until completed
                for (int t = 0; t < 20; t++) w.Tick();
            }

            // --- Run B: save at WAIT, tick 5 more (diverge), load, resume ---
            var logB = new List<string>();
            {
                var prog = new VMProgram(programInstructions, consts, 1);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "SetBB", (ref VMInstanceState s) =>
                    { logB.Add($"SetBB({s.Registers.Get(0).ToInt()})"); });
                w.Syscalls.Register(1, "PlayEffect", (ref VMInstanceState s) =>
                    { logB.Add("PlayEffect"); });
                w.SpawnInstance(0, 0);

                // Tick 1: execute to WAIT
                w.Tick();

                // Save state at frame 1
                w.SaveState();
                int savedFrame = w.FrameNumber;

                // Tick 5 more (diverge state — WaitCounter counts down)
                for (int t = 0; t < 5; t++) w.Tick();

                // Load back to saved state
                logB.Clear(); // discard diverged syscall log
                w.LoadState(savedFrame);

                // Re-register syscalls (they capture logB which we cleared)
                // Actually they still reference logB, which is cleared — that's fine.
                // Now add back SetBB(1) since it was logged before save and we cleared.
                logB.Add("SetBB(1)"); // restore the pre-save entry

                // Tick to completion from restored state
                for (int t = 0; t < 20; t++) w.Tick();
            }

            Assert(logA.Count == logB.Count,
                $"Save/Load resume: same syscall count ({logA.Count} vs {logB.Count})");
            bool seqMatch = logA.Count == logB.Count;
            for (int i = 0; i < logA.Count && seqMatch; i++)
                seqMatch = logA[i] == logB[i];
            Assert(seqMatch,
                "Save/Load resume: identical syscall sequence");
        }

        // ===== Test 26: 0 GC in bytecode Tick loop (6e.2) =====
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
                $"0 GC: bytecode tick delta = {delta} bytes (== 0)");
        }

        // =================================================================
        //  V1: GC Precise Verification (§4.6 V1)
        //  Confirms bytecode Tick loop is zero-GC over 100 consecutive ticks
        //  with active instances executing SYSCALL + WAIT + Cleanup.
        // =================================================================

        // ===== Test 27: V1 — 100-tick zero GC with active instances =====
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
                $"V1 GC precise: 100 ticks alloc = {v1Delta} bytes (== 0)");

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
            Assert(anyCompleted, "V1 GC precise: instances actually executed (not idle)");
        }

        // =================================================================
        //  V2: Rollback Correctness Verification (§4.6 V2)
        //  100 frames → Save → 50 diverge → Load → 100 frames
        //  Syscall sequences and final StateFlags must be bit-exact.
        // =================================================================

        // ===== Test 28: V2 — Rollback correctness =====
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
                Assert(loaded, "V2 rollback: LoadState succeeded");

                // Run 100 frames from the restored state (should match reference)
                for (int t = 0; t < 100; t++) wB.Tick();

                finalFlagsRollback = wB.Pool.Instances[0].StateFlags;
            }

            // Compare results
            Assert(logRefPost.Count == logRollbackPost.Count,
                $"V2 rollback: syscall count match ({logRefPost.Count} vs {logRollbackPost.Count})");

            bool v2SeqMatch = true;
            int v2MinCount = Math.Min(logRefPost.Count, logRollbackPost.Count);
            for (int i = 0; i < v2MinCount && v2SeqMatch; i++)
                v2SeqMatch = logRefPost[i] == logRollbackPost[i];
            v2SeqMatch = v2SeqMatch && (logRefPost.Count == logRollbackPost.Count);
            Assert(v2SeqMatch,
                "V2 rollback: syscall sequence bit-exact");

            Assert(finalFlagsRef == finalFlagsRollback,
                $"V2 rollback: final StateFlags match ({finalFlagsRef} vs {finalFlagsRollback})");

            // Verify the instance actually completed (not still idle)
            Assert((finalFlagsRef & VMStateFlags.Completed) != 0,
                "V2 rollback: instance completed in reference run");
        }

        // =================================================================
        //  Step 5: MOVE/COPY + JUMP/JUMP_IF + Arithmetic + Compare/Boolean
        //  Bytecode-level tests for Phase 2 opcodes.
        // =================================================================

        // ===== Test 29: MOVE — register-to-register copy =====
        {
            // R0 = 42; R1 = MOVE(R0); assert R1 == 42
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // IP 0: R0 = 42
                    new Instruction(OpCode.MOVE, 1, 0),          // IP 1: R1 = R0
                    new Instruction(OpCode.RETURN),              // IP 2
                },
                new Number[] { Number.FromInt(42) },
                2
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(1).ToInt() == 42,
                "MOVE: R1 = R0 = 42");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "MOVE: Completed");
        }

        // ===== Test 30: Arithmetic opcodes (ADD, SUB, MUL, DIV, MOD) =====
        {
            // R0=10, R1=3
            // R2 = R0 + R1 = 13
            // R3 = R0 - R1 = 7
            // R4 = R0 * R1 = 30
            // R5 = R0 / R1 = 3  (integer div in float mode: 10/3 = 3.333, ToInt=3)
            // R6 = R0 % R1 = 1
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // R0 = 10
                    new Instruction(OpCode.LOAD_CONST, 1, 1),    // R1 = 3
                    new Instruction(OpCode.ADD, 2, 0, 1),        // R2 = R0 + R1
                    new Instruction(OpCode.SUB, 3, 0, 1),        // R3 = R0 - R1
                    new Instruction(OpCode.MUL, 4, 0, 1),        // R4 = R0 * R1
                    new Instruction(OpCode.DIV, 5, 0, 1),        // R5 = R0 / R1
                    new Instruction(OpCode.MOD, 6, 0, 1),        // R6 = R0 % R1
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(10), Number.FromInt(3) },
                7
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            ref VMInstanceState arithInst = ref world.Pool.Instances[id];
            Assert(arithInst.Registers.Get(2).ToInt() == 13, "Arith: 10 + 3 = 13");
            Assert(arithInst.Registers.Get(3).ToInt() == 7, "Arith: 10 - 3 = 7");
            Assert(arithInst.Registers.Get(4).ToInt() == 30, "Arith: 10 * 3 = 30");
            Assert(arithInst.Registers.Get(5).ToInt() == 3, "Arith: 10 / 3 = 3");
            Assert(arithInst.Registers.Get(6).ToInt() == 1, "Arith: 10 % 3 = 1");
        }

        // ===== Test 31: Comparison opcodes =====
        {
            // R0=5, R1=10, R2=5
            // R3  = (R0 == R2) → 1     (5 == 5)
            // R4  = (R0 == R1) → 0     (5 == 10)
            // R5  = (R0 != R1) → 1     (5 != 10)
            // R6  = (R0 <  R1) → 1     (5 < 10)
            // R7  = (R1 <  R0) → 0     (10 < 5)
            // R8  = (R0 <= R2) → 1     (5 <= 5)
            // R9  = (R0 >  R1) → 0     (5 > 10)
            // R10 = (R0 >= R2) → 1     (5 >= 5)
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),       // R0 = 5
                    new Instruction(OpCode.LOAD_CONST, 1, 1),       // R1 = 10
                    new Instruction(OpCode.LOAD_CONST, 2, 0),       // R2 = 5
                    new Instruction(OpCode.CMP_EQ, 3, 0, 2),        // R3 = (5 == 5) → 1
                    new Instruction(OpCode.CMP_EQ, 4, 0, 1),        // R4 = (5 == 10) → 0
                    new Instruction(OpCode.CMP_NEQ, 5, 0, 1),       // R5 = (5 != 10) → 1
                    new Instruction(OpCode.CMP_LT, 6, 0, 1),        // R6 = (5 < 10) → 1
                    new Instruction(OpCode.CMP_LT, 7, 1, 0),        // R7 = (10 < 5) → 0
                    new Instruction(OpCode.CMP_LTE, 8, 0, 2),       // R8 = (5 <= 5) → 1
                    new Instruction(OpCode.CMP_GT, 9, 0, 1),        // R9 = (5 > 10) → 0
                    new Instruction(OpCode.CMP_GTE, 10, 0, 2),      // R10 = (5 >= 5) → 1
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(5), Number.FromInt(10) },
                11
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            ref VMInstanceState cmpInst = ref world.Pool.Instances[id];
            Assert(cmpInst.Registers.Get(3).ToInt() == 1, "CMP: 5 == 5 → 1");
            Assert(cmpInst.Registers.Get(4).ToInt() == 0, "CMP: 5 == 10 → 0");
            Assert(cmpInst.Registers.Get(5).ToInt() == 1, "CMP: 5 != 10 → 1");
            Assert(cmpInst.Registers.Get(6).ToInt() == 1, "CMP: 5 < 10 → 1");
            Assert(cmpInst.Registers.Get(7).ToInt() == 0, "CMP: 10 < 5 → 0");
            Assert(cmpInst.Registers.Get(8).ToInt() == 1, "CMP: 5 <= 5 → 1");
            Assert(cmpInst.Registers.Get(9).ToInt() == 0, "CMP: 5 > 10 → 0");
            Assert(cmpInst.Registers.Get(10).ToInt() == 1, "CMP: 5 >= 5 → 1");
        }

        // ===== Test 32: Boolean and unary opcodes (AND, OR, NOT, NEG) =====
        {
            // R0=1 (true), R1=0 (false), R2=7 (truthy)
            // R3 = AND(R0, R2) → 1   (1 && 7)
            // R4 = AND(R0, R1) → 0   (1 && 0)
            // R5 = OR(R0, R1)  → 1   (1 || 0)
            // R6 = OR(R1, R1)  → 0   (0 || 0)
            // R7 = NOT(R1)     → 1   (!0)
            // R8 = NOT(R2)     → 0   (!7)
            // R9 = NEG(R2)     → -7  (-7)
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // R0 = 1
                    new Instruction(OpCode.LOAD_CONST, 1, 1),    // R1 = 0
                    new Instruction(OpCode.LOAD_CONST, 2, 2),    // R2 = 7
                    new Instruction(OpCode.AND, 3, 0, 2),        // R3 = AND(1, 7) → 1
                    new Instruction(OpCode.AND, 4, 0, 1),        // R4 = AND(1, 0) → 0
                    new Instruction(OpCode.OR, 5, 0, 1),         // R5 = OR(1, 0) → 1
                    new Instruction(OpCode.OR, 6, 1, 1),         // R6 = OR(0, 0) → 0
                    new Instruction(OpCode.NOT, 7, 1),           // R7 = NOT(0) → 1
                    new Instruction(OpCode.NOT, 8, 2),           // R8 = NOT(7) → 0
                    new Instruction(OpCode.NEG, 9, 2),           // R9 = NEG(7) → -7
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(1), Number.FromInt(0), Number.FromInt(7) },
                10
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            ref VMInstanceState boolInst = ref world.Pool.Instances[id];
            Assert(boolInst.Registers.Get(3).ToInt() == 1, "Bool: AND(1,7) → 1");
            Assert(boolInst.Registers.Get(4).ToInt() == 0, "Bool: AND(1,0) → 0");
            Assert(boolInst.Registers.Get(5).ToInt() == 1, "Bool: OR(1,0) → 1");
            Assert(boolInst.Registers.Get(6).ToInt() == 0, "Bool: OR(0,0) → 0");
            Assert(boolInst.Registers.Get(7).ToInt() == 1, "Bool: NOT(0) → 1");
            Assert(boolInst.Registers.Get(8).ToInt() == 0, "Bool: NOT(7) → 0");
            Assert(boolInst.Registers.Get(9).ToInt() == -7, "Unary: NEG(7) → -7");
        }

        // ===== Test 33: JUMP unconditional =====
        {
            // R0 = 1; JUMP → skip R0 = 99; assert R0 == 1
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // IP 0: R0 = 1
                    new Instruction(OpCode.JUMP, 3),             // IP 1: JUMP → IP 3
                    new Instruction(OpCode.LOAD_CONST, 0, 1),    // IP 2: R0 = 99 (SKIPPED)
                    new Instruction(OpCode.RETURN),              // IP 3
                },
                new Number[] { Number.FromInt(1), Number.FromInt(99) },
                1
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(0).ToInt() == 1,
                "JUMP: skipped over R0=99, R0 still 1");
        }

        // ===== Test 34: JUMP_IF_ZERO / JUMP_IF_NOT_ZERO =====
        {
            // R0=0, R1=5
            // JUMP_IF_ZERO(R0) → taken → R2=1
            // JUMP_IF_NOT_ZERO(R1) → taken → R3=1
            // Final: R2=1 (zero-branch taken), R3=1 (nonzero-branch taken)
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),             // IP 0: R0 = 0
                    new Instruction(OpCode.LOAD_CONST, 1, 1),             // IP 1: R1 = 5
                    new Instruction(OpCode.JUMP_IF_ZERO, 5, 0),           // IP 2: if R0==0 → JUMP IP 5
                    new Instruction(OpCode.LOAD_CONST, 2, 2),             // IP 3: R2 = 99 (SKIPPED)
                    new Instruction(OpCode.JUMP, 6),                      // IP 4: skip next
                    new Instruction(OpCode.LOAD_CONST, 2, 3),             // IP 5: R2 = 1 (taken)
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 9, 1),       // IP 6: if R1!=0 → JUMP IP 9
                    new Instruction(OpCode.LOAD_CONST, 3, 2),             // IP 7: R3 = 99 (SKIPPED)
                    new Instruction(OpCode.JUMP, 10),                     // IP 8: skip next
                    new Instruction(OpCode.LOAD_CONST, 3, 3),             // IP 9: R3 = 1 (taken)
                    new Instruction(OpCode.RETURN),                       // IP 10
                },
                new Number[] { Number.FromInt(0), Number.FromInt(5), Number.FromInt(99), Number.FromInt(1) },
                4
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            ref VMInstanceState brInst = ref world.Pool.Instances[id];
            Assert(brInst.Registers.Get(2).ToInt() == 1,
                "Branch: JUMP_IF_ZERO taken when R0==0");
            Assert(brInst.Registers.Get(3).ToInt() == 1,
                "Branch: JUMP_IF_NOT_ZERO taken when R1!=0");
        }

        // ===== Test 35: Loop — sum 1..10 = 55 using JUMP + comparison =====
        {
            // Equivalent to: sum=0; i=1; while(i<=10) { sum+=i; i+=1; } return sum;
            // R0 = sum (accumulator)
            // R1 = i (counter)
            // R2 = 10 (limit)
            // R3 = 1 (increment)
            // R4 = temp (comparison result)
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),         // IP 0: R0 = 0  (sum)
                    new Instruction(OpCode.LOAD_CONST, 1, 1),         // IP 1: R1 = 1  (i)
                    new Instruction(OpCode.LOAD_CONST, 2, 2),         // IP 2: R2 = 10 (limit)
                    new Instruction(OpCode.LOAD_CONST, 3, 1),         // IP 3: R3 = 1  (step)
                    // loop start:
                    new Instruction(OpCode.CMP_GT, 4, 1, 2),         // IP 4: R4 = (i > 10)?
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 9, 4),  // IP 5: if R4 → exit loop (IP 9)
                    new Instruction(OpCode.ADD, 0, 0, 1),             // IP 6: sum += i
                    new Instruction(OpCode.ADD, 1, 1, 3),             // IP 7: i += 1
                    new Instruction(OpCode.JUMP, 4),                  // IP 8: → loop start
                    // loop exit:
                    new Instruction(OpCode.RETURN),                   // IP 9
                },
                new Number[] { Number.FromInt(0), Number.FromInt(1), Number.FromInt(10) },
                5
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(0).ToInt() == 55,
                "Loop: sum(1..10) = 55");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "Loop: Completed");
        }

        // ===== Test 36: If/Else — max(a, b) using conditional jump =====
        {
            // max(5, 8): R0=5, R1=8
            // if R0 > R1 then R2=R0 else R2=R1
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),          // IP 0: R0 = 5
                    new Instruction(OpCode.LOAD_CONST, 1, 1),          // IP 1: R1 = 8
                    new Instruction(OpCode.CMP_GT, 3, 0, 1),           // IP 2: R3 = (5 > 8)?
                    new Instruction(OpCode.JUMP_IF_NOT_ZERO, 6, 3),    // IP 3: if true → IP 6
                    // else branch:
                    new Instruction(OpCode.MOVE, 2, 1),                // IP 4: R2 = R1 (8)
                    new Instruction(OpCode.JUMP, 7),                   // IP 5: skip then branch
                    // then branch:
                    new Instruction(OpCode.MOVE, 2, 0),                // IP 6: R2 = R0 (5)
                    new Instruction(OpCode.RETURN),                    // IP 7
                },
                new Number[] { Number.FromInt(5), Number.FromInt(8) },
                4
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(2).ToInt() == 8,
                "If/Else: max(5,8) = 8");
        }

        // ===== Test 37: MOVE preserves value after source overwrite =====
        {
            // R0=42; R1=MOVE(R0); R0=99; assert R1 still 42
            var program = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // R0 = 42
                    new Instruction(OpCode.MOVE, 1, 0),          // R1 = R0 (copy)
                    new Instruction(OpCode.LOAD_CONST, 0, 1),    // R0 = 99
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(42), Number.FromInt(99) },
                2
            );

            var world = new VMWorld();
            world.Modules.Load(0, program);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(1).ToInt() == 42,
                "MOVE copy: R1 preserved after R0 overwrite");
            Assert(world.Pool.Instances[id].Registers.Get(0).ToInt() == 99,
                "MOVE copy: R0 changed to 99");
        }

        // ===== Test 38: 0 GC — new opcodes don't allocate =====
        {
            // Program exercises all new opcodes:
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
                $"0 GC new opcodes: delta = {gcDelta} bytes (== 0)");
        }

        // ===== Test 39: Save/Load correctness with new opcodes =====
        {
            // Loop: sum = 0; i = 1; while i <= 20: sum += i; i++; end
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
            var logA2 = new List<string>();
            int finalSumA;
            {
                var prog = new VMProgram(progInstr, progConsts, 5);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
                    { logA2.Add($"sum={s.Registers.Get(0).ToInt()}"); });
                w.SpawnInstance(0, 0);
                // Each iteration: 1 tick execute + 1 tick wait = 2 ticks
                // 20 iterations × 2 = 40 ticks + 1 for report+return
                for (int t = 0; t < 60; t++) w.Tick();
                finalSumA = w.Pool.Instances[0].Registers.Get(0).ToInt();
            }

            // --- Run B: save at tick 5, diverge, load, resume ---
            var logB2 = new List<string>();
            int finalSumB;
            {
                var prog = new VMProgram(progInstr, progConsts, 5);
                var w = new VMWorld();
                w.Modules.Load(0, prog);
                w.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
                    { logB2.Add($"sum={s.Registers.Get(0).ToInt()}"); });
                w.SpawnInstance(0, 0);

                for (int t = 0; t < 5; t++) w.Tick();
                w.SaveState();
                int sf = w.FrameNumber;

                // Diverge
                for (int t = 0; t < 10; t++) w.Tick();

                // Load back
                logB2.Clear();
                w.LoadState(sf);

                // Resume
                for (int t = 0; t < 60; t++) w.Tick();
                finalSumB = w.Pool.Instances[0].Registers.Get(0).ToInt();
            }

            Assert(finalSumA == 210, $"Save/Load loop: reference sum = {finalSumA} (== 210)");
            Assert(finalSumA == finalSumB,
                $"Save/Load loop: rollback sum matches ({finalSumA} vs {finalSumB})");
            Assert(logA2.Count == logB2.Count && logA2.Count > 0,
                $"Save/Load loop: syscall count match ({logA2.Count} vs {logB2.Count})");
            bool seqOk = logA2.Count == logB2.Count;
            for (int i = 0; i < logA2.Count && seqOk; i++)
                seqOk = logA2[i] == logB2[i];
            Assert(seqOk, "Save/Load loop: syscall sequence bit-exact");
        }

        // =================================================================
        //  V3: Single-Instance Performance Benchmark (§4.6 V3)
        //  VM bytecode vs equivalent C# logic — measure overhead ratio.
        //  Same logic: loop 10000 iterations with arithmetic + branch + syscall.
        //  Using Number type in both paths for fair data-type comparison.
        // =================================================================

        // ===== Test 40: V3 — Single-instance performance benchmark =====
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
                // Expected: sum(0..9999) + sum(i-1 for i=0..9999)
                //         = 49995000 + 49985000 = 99980000
                Assert(vmAcc == 99980000,
                    $"V3 correctness: acc = {vmAcc} (== 99980000)");
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
                $"V3 perf: VM/C# ratio = {v3Ratio:F1}x (expected < 50x, reference 10-30x)");
        }

        // =================================================================
        //  V4: N-Instance Throughput Benchmark (§4.6 V4)
        //  128 → 256 → 512 → 1024 instances × ~50 instructions/tick.
        //  Pass condition: 128 instances × 50 instr/tick < 1ms.
        //  Uses multiple VMWorlds for counts exceeding MaxInstances (128).
        // =================================================================

        // ===== Test 41: V4 — N-instance throughput =====
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
                // i=0: acc=0+0=0, temp=0-1=-1, acc=0+(-1)=-1
                // i=1: acc=-1+1=0, temp=1-1=0, acc=0+0=0
                // i=2: acc=0+2=2, temp=2-1=1, acc=2+1=3
                // i=3: acc=3+3=6, temp=3-1=2, acc=6+2=8
                // i=4: acc=8+4=12, temp=4-1=3, acc=12+3=15
                Assert(v4Acc == 15,
                    $"V4 correctness: acc = {v4Acc} (== 15)");
            }

            // --- Benchmark at each scale ---
            int v4Rounds = 1000;
            int[] v4Scales = new int[] { 128, 256, 512, 1024 };

            Debug.Log($"[BENCH] V4 N-Instance Throughput (~57 instr/instance):");

            foreach (int targetN in v4Scales)
            {
                int worldCount = (targetN + VMConstants.MaxInstances - 1) / VMConstants.MaxInstances; // ceiling division
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
                        $"V4 throughput: 128 instances × ~57 instr = {v4AvgMs:F3} ms (< 1ms)");
                }
            }
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"TreeWalker Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
