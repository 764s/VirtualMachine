using System.Collections.Generic;
using FFVM;
using FFVM.AST;
using UnityEngine;

/// <summary>
/// Phase 2 validation: hand-built AST → tree-walker interpreter.
/// Covers: arithmetic, variables, branches, loops, functions, syscalls, wait/yield.
/// Run from Unity Editor (attach to a GameObject or call from menu).
/// </summary>
public static class TreeWalkerTests
{
    [UnityEngine.RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
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

        // ===== Summary =====
        Debug.Log($"========================================");
        Debug.Log($"TreeWalker Tests: {passed} passed, {failed} failed");
        Debug.Log($"========================================");
    }
}
