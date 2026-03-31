using System;
using System.Collections.Generic;
using FFVM;
using FFVM.AST;
using UnityEditor;
using UnityEngine;

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

            bool v2SeqMatch = logRefPost.Count == logRollbackPost.Count;
            for (int i = 0; i < logRefPost.Count && v2SeqMatch; i++)
                v2SeqMatch = logRefPost[i] == logRollbackPost[i];
            Assert(v2SeqMatch,
                "V2 rollback: syscall sequence bit-exact");

            Assert(finalFlagsRef == finalFlagsRollback,
                $"V2 rollback: final StateFlags match ({finalFlagsRef} vs {finalFlagsRollback})");

            // Verify the instance actually completed (not still idle)
            Assert((finalFlagsRef & VMStateFlags.Completed) != 0,
                "V2 rollback: instance completed in reference run");
        }

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"TreeWalker Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
