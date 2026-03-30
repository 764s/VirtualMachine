using System;
using System.Collections.Generic;

namespace FFVM.AST
{
    /// <summary>
    /// Result of an expression evaluation in the tree-walker.
    /// Tagged union: Number, Int, Bool, StringId, or Void.
    /// </summary>
    public struct Value
    {
        public enum Tag : byte { Void, Num, Int, Bool, StringId }

        public Tag Type;
        public Number NumValue;
        public int IntValue;
        public bool BoolValue;

        public static Value Void() => new Value { Type = Tag.Void };
        public static Value FromNumber(Number n) => new Value { Type = Tag.Num, NumValue = n };
        public static Value FromInt(int i) => new Value { Type = Tag.Int, IntValue = i };
        public static Value FromBool(bool b) => new Value { Type = Tag.Bool, BoolValue = b };
        public static Value FromStringId(int id) => new Value { Type = Tag.StringId, IntValue = id };

        /// <summary>Coerce to Number for arithmetic.</summary>
        public Number AsNumber()
        {
            switch (Type)
            {
                case Tag.Num: return NumValue;
                case Tag.Int: return Number.FromInt(IntValue);
                case Tag.Bool: return BoolValue ? Number.One : Number.Zero;
                default: return Number.Zero;
            }
        }

        /// <summary>Coerce to bool for conditions.</summary>
        public bool AsBool()
        {
            switch (Type)
            {
                case Tag.Bool: return BoolValue;
                case Tag.Num: return NumValue != Number.Zero;
                case Tag.Int: return IntValue != 0;
                default: return false;
            }
        }

        public int AsInt()
        {
            switch (Type)
            {
                case Tag.Int: return IntValue;
                case Tag.Num: return NumValue.ToInt();
                case Tag.Bool: return BoolValue ? 1 : 0;
                default: return 0;
            }
        }

        public override string ToString()
        {
            switch (Type)
            {
                case Tag.Num: return $"Num({NumValue})";
                case Tag.Int: return $"Int({IntValue})";
                case Tag.Bool: return $"Bool({BoolValue})";
                case Tag.StringId: return $"StringId({IntValue})";
                default: return "Void";
            }
        }
    }

    /// <summary>
    /// Variable scope with lexical scoping chain.
    /// </summary>
    public class Environment
    {
        private readonly Dictionary<string, Value> _vars = new Dictionary<string, Value>();
        private readonly Environment _parent;

        public Environment(Environment parent = null)
        {
            _parent = parent;
        }

        public void Define(string name, Value value)
        {
            _vars[name] = value;
        }

        public bool TryGet(string name, out Value value)
        {
            if (_vars.TryGetValue(name, out value))
                return true;
            if (_parent != null)
                return _parent.TryGet(name, out value);
            value = Value.Void();
            return false;
        }

        public bool TrySet(string name, Value value)
        {
            if (_vars.ContainsKey(name))
            {
                _vars[name] = value;
                return true;
            }
            if (_parent != null)
                return _parent.TrySet(name, value);
            return false;
        }
    }

    /// <summary>
    /// Signal exceptions for control flow in the tree-walker.
    /// </summary>
    public class ReturnSignal : Exception
    {
        public Value ReturnValue { get; }
        public ReturnSignal(Value value) : base("return") { ReturnValue = value; }
    }

    public class WaitSignal : Exception
    {
        public int FrameCount { get; }
        public WaitSignal(int frames) : base("wait") { FrameCount = frames; }
    }

    public class WaitForSignal : Exception
    {
        public int TargetInstanceId { get; }
        public WaitForSignal(int targetId) : base("wait_for") { TargetInstanceId = targetId; }
    }

    public class YieldSignal : Exception
    {
        public YieldSignal() : base("yield") { }
    }

    public class PanicException : Exception
    {
        public VMError Error { get; }
        public PanicException(VMError error, string message) : base(message) { Error = error; }
    }

    /// <summary>
    /// Syscall handler for the tree-walker interpreter.
    /// Takes argument values, returns a result value.
    /// </summary>
    public delegate Value TreeWalkerSyscallHandler(Value[] args);

    /// <summary>
    /// Tree-walking AST interpreter for Phase 2 validation.
    /// Verifies: branches, loops, wait/suspend, function calls, syscalls.
    /// NOT for production use — prototype only.
    /// </summary>
    public class TreeWalker
    {
        private readonly Dictionary<string, FuncDecl> _functions = new Dictionary<string, FuncDecl>();
        private readonly Dictionary<int, TreeWalkerSyscallHandler> _syscalls = new Dictionary<int, TreeWalkerSyscallHandler>();
        private readonly Dictionary<string, StructDecl> _structs = new Dictionary<string, StructDecl>();

        private Environment _globalEnv;
        private int _callDepth;
        private readonly List<BlockStmt> _deferStack = new List<BlockStmt>();
        private bool _killed;

        public bool IsKilled => _killed;
        public Action<string> Log { get; set; }

        public TreeWalker()
        {
            _globalEnv = new Environment();
        }

        // ===== Setup =====

        public void RegisterSyscall(int slot, TreeWalkerSyscallHandler handler)
        {
            _syscalls[slot] = handler;
        }

        public void LoadModule(ModuleNode module)
        {
            foreach (var s in module.Structs)
                _structs[s.Name] = s;

            foreach (var f in module.Functions)
                _functions[f.Name] = f;
        }

        // ===== Entry Points =====

        /// <summary>
        /// Execute a named function with arguments. Returns the result value.
        /// </summary>
        public Value CallFunction(string name, params Value[] args)
        {
            if (!_functions.TryGetValue(name, out var func))
                throw new PanicException(VMError.PanicUnresolvedExtern, $"Function not found: {name}");

            var env = new Environment(_globalEnv);
            for (int i = 0; i < func.Parameters.Count && i < args.Length; i++)
            {
                env.Define(func.Parameters[i].Name, args[i]);
            }

            _callDepth = 0;
            _deferStack.Clear();
            _killed = false;

            Value result;
            try
            {
                ExecuteBlock(func.Body, env);
                result = Value.Void();
            }
            catch (ReturnSignal ret)
            {
                result = ret.ReturnValue;
            }

            ExecuteCleanups(env);
            return result;
        }

        /// <summary>
        /// Force-kill this execution: runs all registered Cleanup blocks in LIFO order,
        /// then prevents further main-flow execution.
        /// </summary>
        public void Kill()
        {
            _killed = true;
            ExecuteCleanups(_globalEnv);
        }

        // ===== Statement Execution =====

        private void ExecuteStmt(Stmt stmt, Environment env)
        {
            switch (stmt)
            {
                case BlockStmt block:
                    ExecuteBlock(block, new Environment(env));
                    break;

                case VarDeclStmt varDecl:
                    ExecuteVarDecl(varDecl, env);
                    break;

                case IfStmt ifStmt:
                    ExecuteIf(ifStmt, env);
                    break;

                case WhileStmt whileStmt:
                    ExecuteWhile(whileStmt, env);
                    break;

                case ForStmt forStmt:
                    ExecuteFor(forStmt, env);
                    break;

                case ReturnStmt returnStmt:
                {
                    var val = returnStmt.Value != null ? EvalExpr(returnStmt.Value, env) : Value.Void();
                    throw new ReturnSignal(val);
                }

                case WaitStmt waitStmt:
                {
                    int frames = EvalExpr(waitStmt.FrameCount, env).AsInt();
                    throw new WaitSignal(frames);
                }

                case WaitForStmt waitFor:
                {
                    int targetId = EvalExpr(waitFor.TargetInstanceId, env).AsInt();
                    throw new WaitForSignal(targetId);
                }

                case DeferStmt deferStmt:
                    _deferStack.Add(deferStmt.Body);
                    break;

                case YieldStmt _:
                    throw new YieldSignal();

                case ExprStmt exprStmt:
                    EvalExpr(exprStmt.Expression, env);
                    break;

                default:
                    throw new PanicException(VMError.PanicIllegalInstruction,
                        $"Unknown statement type: {stmt.Kind}");
            }
        }

        private void ExecuteBlock(BlockStmt block, Environment env)
        {
            foreach (var stmt in block.Statements)
            {
                ExecuteStmt(stmt, env);
            }
        }

        private void ExecuteVarDecl(VarDeclStmt decl, Environment env)
        {
            Value init = decl.Initializer != null
                ? EvalExpr(decl.Initializer, env)
                : Value.FromNumber(Number.Zero);

            env.Define(decl.Name, init);
        }

        private void ExecuteIf(IfStmt ifStmt, Environment env)
        {
            bool cond = EvalExpr(ifStmt.Condition, env).AsBool();
            if (cond)
                ExecuteStmt(ifStmt.ThenBranch, env);
            else if (ifStmt.ElseBranch != null)
                ExecuteStmt(ifStmt.ElseBranch, env);
        }

        private void ExecuteWhile(WhileStmt whileStmt, Environment env)
        {
            while (EvalExpr(whileStmt.Condition, env).AsBool())
            {
                ExecuteStmt(whileStmt.Body, env);
            }
        }

        private void ExecuteFor(ForStmt forStmt, Environment env)
        {
            var loopEnv = new Environment(env);
            if (forStmt.Initializer != null)
                ExecuteStmt(forStmt.Initializer, loopEnv);

            while (forStmt.Condition == null || EvalExpr(forStmt.Condition, loopEnv).AsBool())
            {
                ExecuteStmt(forStmt.Body, loopEnv);
                if (forStmt.Increment != null)
                    EvalExpr(forStmt.Increment, loopEnv);
            }
        }

        // ===== Expression Evaluation =====

        private Value EvalExpr(Expr expr, Environment env)
        {
            switch (expr)
            {
                case NumberLiteralExpr num:
                    return Value.FromNumber(Number.FromFloat(num.Value));

                case IntLiteralExpr intLit:
                    return Value.FromInt(intLit.Value);

                case BoolLiteralExpr boolLit:
                    return Value.FromBool(boolLit.Value);

                case StringIdLiteralExpr strId:
                    return Value.FromStringId(strId.HashId);

                case IdentifierExpr ident:
                    if (env.TryGet(ident.Name, out var val))
                        return val;
                    throw new PanicException(VMError.PanicOutOfBounds,
                        $"Undefined variable: {ident.Name}");

                case AssignExpr assign:
                    return EvalAssign(assign, env);

                case BinaryExpr binary:
                    return EvalBinary(binary, env);

                case UnaryExpr unary:
                    return EvalUnary(unary, env);

                case CallExpr call:
                    return EvalCall(call, env);

                case SyscallExpr syscall:
                    return EvalSyscall(syscall, env);

                case FieldAccessExpr fieldAccess:
                    return EvalFieldAccess(fieldAccess, env);

                default:
                    throw new PanicException(VMError.PanicIllegalInstruction,
                        $"Unknown expression type: {expr.Kind}");
            }
        }

        private Value EvalAssign(AssignExpr assign, Environment env)
        {
            var value = EvalExpr(assign.Value, env);

            if (assign.Target is IdentifierExpr ident)
            {
                if (!env.TrySet(ident.Name, value))
                    throw new PanicException(VMError.PanicOutOfBounds,
                        $"Undefined variable for assignment: {ident.Name}");
                return value;
            }

            throw new PanicException(VMError.PanicIllegalInstruction,
                "Invalid assignment target");
        }

        private Value EvalBinary(BinaryExpr binary, Environment env)
        {
            // Short-circuit for logical operators
            if (binary.Kind == NodeKind.And)
            {
                var left = EvalExpr(binary.Left, env);
                if (!left.AsBool()) return Value.FromBool(false);
                return Value.FromBool(EvalExpr(binary.Right, env).AsBool());
            }

            if (binary.Kind == NodeKind.Or)
            {
                var left = EvalExpr(binary.Left, env);
                if (left.AsBool()) return Value.FromBool(true);
                return Value.FromBool(EvalExpr(binary.Right, env).AsBool());
            }

            var lhs = EvalExpr(binary.Left, env);
            var rhs = EvalExpr(binary.Right, env);

            switch (binary.Kind)
            {
                // Arithmetic
                case NodeKind.Add: return Value.FromNumber(lhs.AsNumber() + rhs.AsNumber());
                case NodeKind.Sub: return Value.FromNumber(lhs.AsNumber() - rhs.AsNumber());
                case NodeKind.Mul: return Value.FromNumber(lhs.AsNumber() * rhs.AsNumber());
                case NodeKind.Div:
                {
                    var r = rhs.AsNumber();
                    if (r == Number.Zero)
                        throw new PanicException(VMError.PanicDivideByZero, "Division by zero");
                    return Value.FromNumber(lhs.AsNumber() / r);
                }
                case NodeKind.Mod:
                {
                    var r = rhs.AsNumber();
                    if (r == Number.Zero)
                        throw new PanicException(VMError.PanicDivideByZero, "Modulo by zero");
                    return Value.FromNumber(lhs.AsNumber() % r);
                }

                // Comparison
                case NodeKind.Eq: return Value.FromBool(lhs.AsNumber() == rhs.AsNumber());
                case NodeKind.Neq: return Value.FromBool(lhs.AsNumber() != rhs.AsNumber());
                case NodeKind.Lt: return Value.FromBool(lhs.AsNumber() < rhs.AsNumber());
                case NodeKind.Gt: return Value.FromBool(lhs.AsNumber() > rhs.AsNumber());
                case NodeKind.Lte: return Value.FromBool(lhs.AsNumber() <= rhs.AsNumber());
                case NodeKind.Gte: return Value.FromBool(lhs.AsNumber() >= rhs.AsNumber());

                default:
                    throw new PanicException(VMError.PanicIllegalInstruction,
                        $"Unknown binary operator: {binary.Kind}");
            }
        }

        private Value EvalUnary(UnaryExpr unary, Environment env)
        {
            var operand = EvalExpr(unary.Operand, env);
            switch (unary.Kind)
            {
                case NodeKind.Negate: return Value.FromNumber(-operand.AsNumber());
                case NodeKind.Not: return Value.FromBool(!operand.AsBool());
                default:
                    throw new PanicException(VMError.PanicIllegalInstruction,
                        $"Unknown unary operator: {unary.Kind}");
            }
        }

        private Value EvalCall(CallExpr call, Environment env)
        {
            if (!_functions.TryGetValue(call.FunctionName, out var func))
                throw new PanicException(VMError.PanicUnresolvedExtern,
                    $"Function not found: {call.FunctionName}");

            _callDepth++;
            if (_callDepth > VMConstants.MaxCallDepth)
                throw new PanicException(VMError.PanicStackOverflow,
                    $"Call stack overflow (depth {_callDepth})");

            // Evaluate arguments
            var callEnv = new Environment(_globalEnv);
            for (int i = 0; i < func.Parameters.Count && i < call.Arguments.Count; i++)
            {
                var argVal = EvalExpr(call.Arguments[i], env);
                callEnv.Define(func.Parameters[i].Name, argVal);
            }

            try
            {
                ExecuteBlock(func.Body, callEnv);
                _callDepth--;
                return Value.Void();
            }
            catch (ReturnSignal ret)
            {
                _callDepth--;
                return ret.ReturnValue;
            }
        }

        private Value EvalSyscall(SyscallExpr syscall, Environment env)
        {
            if (!_syscalls.TryGetValue(syscall.SyscallSlot, out var handler))
                throw new PanicException(VMError.PanicIllegalInstruction,
                    $"Syscall not registered: slot {syscall.SyscallSlot} ({syscall.SyscallName})");

            var args = new Value[syscall.Arguments.Count];
            for (int i = 0; i < syscall.Arguments.Count; i++)
                args[i] = EvalExpr(syscall.Arguments[i], env);

            return handler(args);
        }

        // ===== Cleanup =====

        private void ExecuteCleanups(Environment env)
        {
            for (int i = _deferStack.Count - 1; i >= 0; i--)
            {
                ExecuteBlock(_deferStack[i], env);
            }
            _deferStack.Clear();
        }

        private Value EvalFieldAccess(FieldAccessExpr fieldAccess, Environment env)
        {
            // In the tree-walker prototype, struct fields are stored as "structVar.fieldName"
            // This is a simplified version — the real compiler will flatten to register offsets
            if (fieldAccess.Target is IdentifierExpr ident)
            {
                string key = $"{ident.Name}.{fieldAccess.FieldName}";
                if (env.TryGet(key, out var val))
                    return val;
            }
            throw new PanicException(VMError.PanicOutOfBounds,
                $"Field not found: {fieldAccess.FieldName}");
        }
    }
}
