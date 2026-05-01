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
        TestHarness.BeginSuite("TreeWalkerTests");

        void Assert(bool condition, string testName)
        {
            if (condition) passed++; else failed++;
            TestHarness.Assert(condition, testName);
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

        // Tests 26-28 (0-GC, V1 GC, V2 Rollback) → PerformanceTests.cs

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

        // Tests 38-41 (V1b/V2b GC, V3 perf, V4 throughput) → PerformanceTests.cs

        // ===== Test: G4 — Step limit produces PanicStepLimitExceeded =====
        {
            // Infinite loop: R0=1, JUMP_IF_NOT_ZERO back to itself
            var instructions = new Instruction[]
            {
                new Instruction { Code = OpCode.LOAD_CONST, A = 0, B = 0 }, // R0 = const[0] = 1
                new Instruction { Code = OpCode.JUMP_IF_NOT_ZERO, A = 0, B = 0 }, // if R0 != 0 goto 0
            };
            var constants = new Number[] { Number.FromInt(1) };
            var prog = new VMProgram(instructions, constants, 1);

            var world = new VMWorld();
            world.MaxStepsPerTick = 8;
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].ErrorFlag == VMError.PanicStepLimitExceeded,
                "G4: step limit → PanicStepLimitExceeded (not PanicIllegalInstruction)");
        }

        // ===== Test T1: wait_for runtime — instance A waits for instance B =====
        {
            // Instance B: simple program — LOAD_CONST + RETURN (completes in 1 tick)
            var progB = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0), // R0 = 42
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(42) },
                1
            );

            // Instance A: LOAD_CONST + WAIT(1) + SYSCALL + RETURN
            // A will be suspended via WaitTargetInstanceId before it reaches SYSCALL
            int reportedA = -1;
            var progA = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0), // R0 = 99
                    new Instruction(OpCode.SYSCALL, 0, 0, 1), // Report(R0)
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(99) },
                1
            );

            var world = new VMWorld();
            world.Modules.Load(0, progA);
            world.Modules.Load(1, progB);

            int idA = world.SpawnInstance(0, 0);
            int idB = world.SpawnInstance(1, 0);

            world.Syscalls.Register(0, "Report", (ref VMInstanceState s) =>
            {
                reportedA = s.Registers.Get(0).ToInt();
            });

            // Set A to wait for B
            world.Pool.Instances[idA].WaitTargetInstanceId = idB;

            // Tick 1: B completes, A skipped (waiting for B)
            world.Tick();
            Assert(reportedA == -1, "T1 wait_for: A still waiting after tick 1");
            Assert((world.Pool.Instances[idB].StateFlags & VMStateFlags.Completed) != 0,
                "T1 wait_for: B completed in tick 1");

            // Tick 2: B finished → A resumes and executes SYSCALL
            world.Tick();
            Assert(reportedA == 99, "T1 wait_for: A resumed after B completed, reported 99");
            Assert((world.Pool.Instances[idA].StateFlags & VMStateFlags.Completed) != 0,
                "T1 wait_for: A completed after resuming");
        }

        // ===== Test T2a: Division by zero (DIV) → returns 0, no panic =====
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0), // R0 = 10
                    new Instruction(OpCode.LOAD_CONST, 1, 1), // R1 = 0
                    new Instruction(OpCode.DIV, 2, 0, 1),     // R2 = R0 / R1 = 10 / 0
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(10), Number.FromInt(0) },
                3
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(2) == Number.Zero,
                "T2a DIV/0: result = 0 (silent)");
            Assert(world.Pool.Instances[id].ErrorFlag == VMError.None,
                "T2a DIV/0: no panic");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "T2a DIV/0: instance completed normally");
        }

        // ===== Test T2b: Modulo by zero (MOD) → returns 0, no panic =====
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0), // R0 = 7
                    new Instruction(OpCode.LOAD_CONST, 1, 1), // R1 = 0
                    new Instruction(OpCode.MOD, 2, 0, 1),     // R2 = R0 % R1 = 7 % 0
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(7), Number.FromInt(0) },
                3
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(2) == Number.Zero,
                "T2b MOD/0: result = 0 (silent)");
            Assert(world.Pool.Instances[id].ErrorFlag == VMError.None,
                "T2b MOD/0: no panic");
        }

        // ===== Test T4: Instance pool exhaustion =====
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0), // R0 = 1
                    new Instruction(OpCode.RETURN),
                },
                new Number[] { Number.FromInt(1) },
                1
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);

            // Fill the pool to capacity (128 instances)
            bool allAllocated = true;
            for (int i = 0; i < VMConstants.MaxInstances; i++)
            {
                int id = world.SpawnInstance(0, 0);
                if (id < 0) { allAllocated = false; break; }
            }
            Assert(allAllocated, "T4 pool: all 128 instances allocated successfully");
            Assert(world.Pool.ActiveCount == VMConstants.MaxInstances,
                $"T4 pool: active count = {world.Pool.ActiveCount} (== {VMConstants.MaxInstances})");

            // 129th allocation should fail
            int overflow = world.SpawnInstance(0, 0);
            Assert(overflow == -1, "T4 pool: 129th allocation returns -1 (pool full)");
        }

        // ===== Test F01: CALL + RET_FUNC basic (hand-written bytecode) =====
        // Simulate: entry calls func at IP=4 which sets r16=42, then returns
        //   IP 0: LOAD_CONST r16, 100          (caller local)
        //   IP 1: CALL target=4, windowSize=16  (caller window = 16)
        //   IP 2: MOVE r17, r0                  (save return value)
        //   IP 3: RETURN
        //   IP 4: LOAD_CONST r16, 42            (callee local — physically r32 due to window)
        //   IP 5: MOVE r0, r16                  (return value in r0)
        //   IP 6: RET_FUNC
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 16, 0),         // IP 0: r16 = 100
                    new Instruction(OpCode.CALL, 4, 16),               // IP 1: CALL entry=4, winSize=16
                    new Instruction(OpCode.MOVE, 17, 0),               // IP 2: r17 = r0 (return value)
                    new Instruction(OpCode.RETURN),                    // IP 3: RETURN
                    new Instruction(OpCode.LOAD_CONST, 16, 1),         // IP 4: callee r16 = 42
                    new Instruction(OpCode.MOVE, 0, 16),               // IP 5: r0 = callee r16
                    new Instruction(OpCode.RET_FUNC),                  // IP 6: RET_FUNC
                },
                new Number[] { Number.FromInt(100), Number.FromInt(42) },
                48 // need physical registers up to r32
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // Caller's r16 (phys r16) should still be 100 (not clobbered by callee)
            Assert(world.Pool.Instances[id].Registers.Get(16) == Number.FromInt(100),
                "F01: caller r16 = 100 (not clobbered)");
            // r0 should hold return value 42
            Assert(world.Pool.Instances[id].Registers.Get(0) == Number.FromInt(42),
                "F01: r0 = 42 (return value from callee)");
            // r17 should hold the saved return value = 42
            Assert(world.Pool.Instances[id].Registers.Get(17) == Number.FromInt(42),
                "F01: r17 = 42 (saved return value)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "F01: Completed");
            Assert(world.Pool.Instances[id].CallStackDepth == 0,
                "F01: CallStackDepth back to 0");
        }

        // ===== Test F02: CALL chain — a() calls b() =====
        // entry → func_a → func_b → return 42, func_a returns it +1 = 43
        //   IP 0: CALL target=3, winSize=16    (call func_a)
        //   IP 1: MOVE r16, r0                 (save result)
        //   IP 2: RETURN
        //   IP 3: CALL target=7, winSize=16    (func_a calls func_b)
        //   IP 4: MOVE r16, r0                 (func_a saves b's return)
        //   IP 5: ADD r0, r16, r17             (return b() + 1) — r17 loaded with 1
        //   IP 6: RET_FUNC
        //   IP 7: LOAD_CONST r16, 42           (func_b returns 42)
        //   IP 8: MOVE r0, r16
        //   IP 9: RET_FUNC
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    // entry
                    new Instruction(OpCode.CALL, 3, 16),               // IP 0: call func_a
                    new Instruction(OpCode.MOVE, 16, 0),               // IP 1: r16 = return from a
                    new Instruction(OpCode.RETURN),                    // IP 2
                    // func_a (window base offset = 16 from entry)
                    new Instruction(OpCode.CALL, 8, 16),               // IP 3: call func_b
                    new Instruction(OpCode.MOVE, 16, 0),               // IP 4: r16 = return from b
                    new Instruction(OpCode.LOAD_CONST, 17, 0),         // IP 5: r17 = 1
                    new Instruction(OpCode.ADD, 0, 16, 17),            // IP 6: r0 = r16 + r17 = 43
                    new Instruction(OpCode.RET_FUNC),                  // IP 7
                    // func_b (window base offset = 32 from entry)
                    new Instruction(OpCode.LOAD_CONST, 16, 1),         // IP 8: r16 = 42
                    new Instruction(OpCode.MOVE, 0, 16),               // IP 9: r0 = r16
                    new Instruction(OpCode.RET_FUNC),                  // IP 10
                },
                new Number[] { Number.FromInt(1), Number.FromInt(42) },
                64 // need registers up to physical r48
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            // entry's r16 should hold result from func_a = 43
            Assert(world.Pool.Instances[id].Registers.Get(16) == Number.FromInt(43),
                "F02: entry r16 = 43 (a returned b()+1)");
            Assert(world.Pool.Instances[id].Registers.Get(0) == Number.FromInt(43),
                "F02: r0 = 43 (final return value)");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "F02: Completed");
            Assert(world.Pool.Instances[id].CallStackDepth == 0,
                "F02: CallStackDepth back to 0");
        }

        // ===== Test F03: StackOverflow protection =====
        // Self-recursive function: CALL self forever → should trigger StackOverflow at depth 16
        //   IP 0: CALL target=0, winSize=2      (infinite recursion with tiny window)
        //   IP 1: RETURN                         (never reached)
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.CALL, 0, 2),   // IP 0: call self
                    new Instruction(OpCode.RETURN),         // IP 1: never reached
                },
                new Number[0],
                64
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].ErrorFlag == VMError.PanicStackOverflow,
                "F03: StackOverflow triggered");
            Assert(world.Pool.Instances[id].CallStackDepth == VMConstants.MaxCallDepth,
                $"F03: depth = {world.Pool.Instances[id].CallStackDepth} (== MaxCallDepth={VMConstants.MaxCallDepth})");
        }

        // ===== Test F04: CALL with parameter passing via scratch zone =====
        // entry loads args into r0,r1, calls add(a,b) which returns a+b
        //   IP 0: LOAD_CONST r0, 10     (arg a)
        //   IP 1: LOAD_CONST r1, 20     (arg b)
        //   IP 2: CALL target=5, winSize=16
        //   IP 3: MOVE r16, r0          (save result 30)
        //   IP 4: RETURN
        //   IP 5: ADD r0, r0, r1        (callee: r0 = r0 + r1, scratch is shared)
        //   IP 6: RET_FUNC
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),    // IP 0: r0 = 10
                    new Instruction(OpCode.LOAD_CONST, 1, 1),    // IP 1: r1 = 20
                    new Instruction(OpCode.CALL, 5, 16),          // IP 2: call add
                    new Instruction(OpCode.MOVE, 16, 0),          // IP 3: r16 = r0 (result)
                    new Instruction(OpCode.RETURN),               // IP 4
                    new Instruction(OpCode.ADD, 0, 0, 1),         // IP 5: r0 = r0 + r1
                    new Instruction(OpCode.RET_FUNC),             // IP 6
                },
                new Number[] { Number.FromInt(10), Number.FromInt(20) },
                32
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);
            world.Tick();

            Assert(world.Pool.Instances[id].Registers.Get(16) == Number.FromInt(30),
                "F04: add(10,20) = 30 via scratch zone");
            Assert((world.Pool.Instances[id].StateFlags & VMStateFlags.Completed) != 0,
                "F04: Completed");
        }

        // ===== Test F05: GC zero-allocation for CALL/RET_FUNC =====
        {
            var prog = new VMProgram(
                new Instruction[]
                {
                    new Instruction(OpCode.LOAD_CONST, 0, 0),     // r0 = 5
                    new Instruction(OpCode.CALL, 4, 16),           // call func
                    new Instruction(OpCode.MOVE, 16, 0),
                    new Instruction(OpCode.RETURN),
                    new Instruction(OpCode.ADD, 0, 0, 0),          // r0 = r0 + r0 = 10
                    new Instruction(OpCode.RET_FUNC),
                },
                new Number[] { Number.FromInt(5) },
                32
            );

            var world = new VMWorld();
            world.Modules.Load(0, prog);
            int id = world.SpawnInstance(0, 0);

            // Warm up first
            world.Tick();
            world.DestroyInstance(id);

            // Measure GC
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            for (int trial = 0; trial < 100; trial++)
            {
                id = world.SpawnInstance(0, 0);
                world.Tick();
                world.DestroyInstance(id);
            }
            long after = System.GC.GetAllocatedBytesForCurrentThread();
            long gcBytes = after - before;

            Assert(gcBytes == 0, $"F05: CALL/RET_FUNC 0 GC ({gcBytes} bytes)");
        }

        // ===== Lang-14: Bitwise operations (TreeWalker) =====
        {
            // func test(): int { return (6 & 3) | (1 << 4) }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "int",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new BinaryExpr(NodeKind.BitOr,
                        new BinaryExpr(NodeKind.BitAnd,
                            new IntLiteralExpr(6), new IntLiteralExpr(3)),
                        new BinaryExpr(NodeKind.Shl,
                            new IntLiteralExpr(1), new IntLiteralExpr(4))
                    ))
                }),
                false
            );

            var module = new ModuleNode("test_bitwise");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsNumber().ToInt() == ((6 & 3) | (1 << 4)), $"TW-BW01: (6&3)|(1<<4)=18, got {result.AsNumber().ToInt()}");
        }

        // TW-BW02: BitNot, XOR, Shr
        {
            // func test(): int { return ~0 ^ (16 >> 2) }
            var func = new FuncDecl("test",
                new List<ParamDecl>(),
                "int",
                new BlockStmt(new List<Stmt>
                {
                    new ReturnStmt(new BinaryExpr(NodeKind.BitXor,
                        new UnaryExpr(NodeKind.BitNot, new IntLiteralExpr(0)),
                        new BinaryExpr(NodeKind.Shr,
                            new IntLiteralExpr(16), new IntLiteralExpr(2))
                    ))
                }),
                false
            );

            var module = new ModuleNode("test_bitwise2");
            module.Functions.Add(func);

            var walker = new TreeWalker();
            walker.LoadModule(module);

            var result = walker.CallFunction("test");
            Assert(result.AsNumber().ToInt() == (~0 ^ (16 >> 2)), $"TW-BW02: ~0^(16>>2)=-5, got {result.AsNumber().ToInt()}");
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"TreeWalker Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
        TestHarness.EndSuite();
    }
}
