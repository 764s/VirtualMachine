using System.Collections.Generic;
using FFVM.AST;

namespace FFVM.Compiler
{
    public class CompileResult
    {
        public VMProgram Program;
        public List<string> Errors;
        public bool Success => Errors == null || Errors.Count == 0;
    }

    /// <summary>
    /// Compiles a parsed AST (single function) into a VMProgram (bytecode + constants).
    ///
    /// Register layout:
    ///   r0..r15   — scratch zone: syscall arguments / return values
    ///   r16..r47  — local variables (32 slots)
    ///   r48..r63  — expression temporaries (16 slots)
    /// </summary>
    public class BytecodeCompiler
    {
        private const int VarRegBase = 16;
        private const int TempRegBase = 48;

        private List<Instruction> _instructions;
        private List<Number> _constants;
        private Dictionary<string, int> _variables;   // name → register
        private int _nextVarReg;
        private int _tempTop;
        private Dictionary<string, int> _syscalls;    // name → slot
        private List<string> _errors;

        // Deferred cleanup blocks (emitted after main body)
        private struct DeferredCleanup
        {
            public int PushCleanupIP;
            public BlockStmt Body;
        }
        private List<DeferredCleanup> _deferredCleanups;

        /// <summary>
        /// Compile source text into a VMProgram.
        /// </summary>
        /// <param name="source">Script source code</param>
        /// <param name="entryFunc">Entry function name (typically "main")</param>
        /// <param name="syscalls">Syscall name → slot mapping</param>
        public CompileResult Compile(string source, string entryFunc, Dictionary<string, int> syscalls)
        {
            var parser = new Parser();
            var module = parser.Parse(source, out var parseErrors);

            if (parseErrors != null && parseErrors.Count > 0)
                return new CompileResult { Errors = parseErrors };

            return CompileModule(module, entryFunc, syscalls);
        }

        /// <summary>
        /// Compile a pre-parsed module into a VMProgram.
        /// </summary>
        public CompileResult CompileModule(ModuleNode module, string entryFunc, Dictionary<string, int> syscalls)
        {
            FuncDecl func = null;
            for (int i = 0; i < module.Functions.Count; i++)
            {
                if (module.Functions[i].Name == entryFunc)
                {
                    func = module.Functions[i];
                    break;
                }
            }

            if (func == null)
                return new CompileResult { Errors = new List<string> { $"Entry function '{entryFunc}' not found" } };

            _instructions = new List<Instruction>();
            _constants = new List<Number>();
            _variables = new Dictionary<string, int>();
            _nextVarReg = VarRegBase;
            _tempTop = TempRegBase;
            _syscalls = syscalls ?? new Dictionary<string, int>();
            _errors = new List<string>();
            _deferredCleanups = new List<DeferredCleanup>();

            // Declare parameters as variables
            for (int i = 0; i < func.Parameters.Count; i++)
            {
                DeclareVar(func.Parameters[i].Name);
            }

            // Compile function body
            CompileBlock(func.Body);

            // Emit RETURN for main body
            Emit(OpCode.RETURN);

            // Emit deferred cleanup blocks
            // Order in bytecode doesn't matter — the cleanup stack (LIFO) determines execution order
            for (int i = 0; i < _deferredCleanups.Count; i++)
            {
                int cleanupIP = _instructions.Count;
                Backpatch(_deferredCleanups[i].PushCleanupIP, cleanupIP);
                CompileBlock(_deferredCleanups[i].Body);
                Emit(OpCode.RETURN);
            }

            if (_errors.Count > 0)
                return new CompileResult { Errors = _errors };

            return new CompileResult
            {
                Program = new VMProgram(
                    _instructions.ToArray(),
                    _constants.ToArray(),
                    _nextVarReg
                ),
                Errors = _errors
            };
        }

        // ===== Variable management =====

        private int DeclareVar(string name)
        {
            if (_nextVarReg >= TempRegBase)
            {
                _errors.Add($"Too many local variables (max {TempRegBase - VarRegBase})");
                return VarRegBase;
            }
            int reg = _nextVarReg++;
            _variables[name] = reg;
            return reg;
        }

        private int ResolveVar(string name)
        {
            if (_variables.TryGetValue(name, out int reg))
                return reg;
            _errors.Add($"Undefined variable '{name}'");
            return VarRegBase;
        }

        // ===== Temp management =====

        private int AllocTemp()
        {
            if (_tempTop >= VMConstants.MaxRegisters)
            {
                _errors.Add("Expression too complex (out of temp registers)");
                return TempRegBase;
            }
            return _tempTop++;
        }

        private void ResetTemps()
        {
            _tempTop = TempRegBase;
        }

        // ===== Constant pool =====

        private int AddConst(Number value)
        {
            for (int i = 0; i < _constants.Count; i++)
            {
                if (_constants[i] == value)
                    return i;
            }
            int idx = _constants.Count;
            _constants.Add(value);
            return idx;
        }

        // ===== Instruction emission =====

        private int CurrentIP() => _instructions.Count;

        private void Emit(OpCode code, int a = 0, int b = 0, int c = 0)
        {
            _instructions.Add(new Instruction(code, a, b, c));
        }

        private int EmitJump(OpCode code, int testReg = 0)
        {
            int ip = _instructions.Count;
            Emit(code, 0, testReg); // A=target placeholder, B=testReg
            return ip;
        }

        private void Backpatch(int instrIP, int targetIP)
        {
            var instr = _instructions[instrIP];
            _instructions[instrIP] = new Instruction(instr.Code, targetIP, instr.B, instr.C);
        }

        // ===== Statement compilation =====

        private void CompileBlock(BlockStmt block)
        {
            for (int i = 0; i < block.Statements.Count; i++)
            {
                CompileStmt(block.Statements[i]);
                ResetTemps();
            }
        }

        private void CompileStmt(Stmt stmt)
        {
            if (stmt is VarDeclStmt varDecl) { CompileVarDecl(varDecl); return; }
            if (stmt is IfStmt ifStmt)       { CompileIf(ifStmt); return; }
            if (stmt is WhileStmt whileStmt) { CompileWhile(whileStmt); return; }
            if (stmt is ForStmt forStmt)     { CompileFor(forStmt); return; }
            if (stmt is ReturnStmt retStmt)  { CompileReturn(retStmt); return; }
            if (stmt is WaitStmt waitStmt)   { CompileWait(waitStmt); return; }
            if (stmt is YieldStmt)           { Emit(OpCode.WAIT, 1); return; }
            if (stmt is DeferStmt deferStmt) { CompileDefer(deferStmt); return; }
            if (stmt is BlockStmt block)     { CompileBlock(block); return; }
            if (stmt is ExprStmt exprStmt)   { CompileExprStmt(exprStmt); return; }
            _errors.Add($"Unknown statement type: {stmt.GetType().Name}");
        }

        private void CompileVarDecl(VarDeclStmt stmt)
        {
            int reg = DeclareVar(stmt.Name);
            if (stmt.Initializer != null)
            {
                int valueReg = CompileExpr(stmt.Initializer);
                if (valueReg != reg)
                    Emit(OpCode.MOVE, reg, valueReg);
            }
            else
            {
                // Default initialize to 0
                int ci = AddConst(Number.Zero);
                Emit(OpCode.LOAD_CONST, reg, ci);
            }
        }

        private void CompileIf(IfStmt stmt)
        {
            int condReg = CompileExpr(stmt.Condition);
            int jumpElseIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            ResetTemps();

            CompileStmt(stmt.ThenBranch);

            if (stmt.ElseBranch != null)
            {
                int jumpEndIP = EmitJump(OpCode.JUMP);
                Backpatch(jumpElseIP, CurrentIP());
                CompileStmt(stmt.ElseBranch);
                Backpatch(jumpEndIP, CurrentIP());
            }
            else
            {
                Backpatch(jumpElseIP, CurrentIP());
            }
        }

        private void CompileWhile(WhileStmt stmt)
        {
            int loopStart = CurrentIP();
            int condReg = CompileExpr(stmt.Condition);
            int jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
            ResetTemps();

            CompileStmt(stmt.Body);
            Emit(OpCode.JUMP, loopStart);
            Backpatch(jumpEndIP, CurrentIP());
        }

        private void CompileFor(ForStmt stmt)
        {
            if (stmt.Initializer != null)
            {
                CompileStmt(stmt.Initializer);
                ResetTemps();
            }

            int loopStart = CurrentIP();

            // Condition (if null, treat as always true → infinite loop)
            int jumpEndIP = -1;
            if (stmt.Condition != null)
            {
                int condReg = CompileExpr(stmt.Condition);
                jumpEndIP = EmitJump(OpCode.JUMP_IF_ZERO, condReg);
                ResetTemps();
            }

            CompileStmt(stmt.Body);

            if (stmt.Increment != null)
            {
                CompileExpr(stmt.Increment);
                ResetTemps();
            }

            Emit(OpCode.JUMP, loopStart);

            if (jumpEndIP >= 0)
                Backpatch(jumpEndIP, CurrentIP());
        }

        private void CompileReturn(ReturnStmt stmt)
        {
            if (stmt.Value != null)
            {
                int valueReg = CompileExpr(stmt.Value);
                // Return value convention: r0
                if (valueReg != 0)
                    Emit(OpCode.MOVE, 0, valueReg);
            }
            Emit(OpCode.RETURN);
        }

        private void CompileWait(WaitStmt stmt)
        {
            if (stmt.FrameCount is IntLiteralExpr intLit)
            {
                Emit(OpCode.WAIT, intLit.Value);
            }
            else if (stmt.FrameCount is NumberLiteralExpr numLit)
            {
                Emit(OpCode.WAIT, (int)numLit.Value);
            }
            else
            {
                _errors.Add($"wait argument must be a constant integer (line {stmt.Line})");
            }
        }

        private void CompileDefer(DeferStmt stmt)
        {
            int pushIP = CurrentIP();
            Emit(OpCode.PUSH_CLEANUP, 0); // placeholder for cleanup entry IP
            _deferredCleanups.Add(new DeferredCleanup { PushCleanupIP = pushIP, Body = stmt.Body });
        }

        private void CompileExprStmt(ExprStmt stmt)
        {
            // Optimized path: void syscall call (skip result save)
            if (stmt.Expression is CallExpr call && _syscalls.ContainsKey(call.FunctionName))
            {
                CompileSyscallVoid(call);
                return;
            }
            // Generic path: compile expression, discard result
            CompileExpr(stmt.Expression);
        }

        // ===== Expression compilation =====
        // Returns the register holding the result

        private int CompileExpr(Expr expr)
        {
            if (expr is IntLiteralExpr intLit)
            {
                int reg = AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromInt(intLit.Value)));
                return reg;
            }

            if (expr is NumberLiteralExpr numLit)
            {
                int reg = AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(Number.FromFloat(numLit.Value)));
                return reg;
            }

            if (expr is BoolLiteralExpr boolLit)
            {
                int reg = AllocTemp();
                Emit(OpCode.LOAD_CONST, reg, AddConst(boolLit.Value ? Number.One : Number.Zero));
                return reg;
            }

            if (expr is IdentifierExpr ident)
            {
                return ResolveVar(ident.Name);
            }

            if (expr is BinaryExpr bin)
            {
                int left = CompileExpr(bin.Left);
                int right = CompileExpr(bin.Right);
                int dest = AllocTemp();
                Emit(BinOpCode(bin.Kind), dest, left, right);
                return dest;
            }

            if (expr is UnaryExpr un)
            {
                int operand = CompileExpr(un.Operand);
                int dest = AllocTemp();
                Emit(UnOpCode(un.Kind), dest, operand);
                return dest;
            }

            if (expr is AssignExpr assign)
            {
                int valueReg = CompileExpr(assign.Value);
                if (assign.Target is IdentifierExpr target)
                {
                    int targetReg = ResolveVar(target.Name);
                    if (valueReg != targetReg)
                        Emit(OpCode.MOVE, targetReg, valueReg);
                    return targetReg;
                }
                _errors.Add($"Invalid assignment target (line {assign.Line})");
                return valueReg;
            }

            if (expr is CallExpr call)
            {
                return CompileSyscallExpr(call);
            }

            _errors.Add($"Unknown expression type: {expr.GetType().Name}");
            return TempRegBase;
        }

        // ===== Syscall compilation =====

        /// <summary>
        /// Compile a syscall call as an expression (saves result to temp).
        /// Two-phase arg compilation to avoid register conflicts with nested calls.
        /// </summary>
        private int CompileSyscallExpr(CallExpr call)
        {
            if (!_syscalls.TryGetValue(call.FunctionName, out int slot))
            {
                _errors.Add($"Unknown function '{call.FunctionName}' (line {call.Line})");
                return TempRegBase;
            }

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to r0, r1, ... (safe: all sources are r16+/r48+)
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            Emit(OpCode.SYSCALL, slot, 0, call.Arguments.Count);

            // Save result from r0 to temp (protect from future overwrites)
            int resultTemp = AllocTemp();
            Emit(OpCode.MOVE, resultTemp, 0);
            return resultTemp;
        }

        /// <summary>
        /// Compile a void syscall call (no result save, used for expression statements).
        /// </summary>
        private void CompileSyscallVoid(CallExpr call)
        {
            int slot = _syscalls[call.FunctionName];

            // Phase 1: compile all args into temp registers
            int[] argRegs = new int[call.Arguments.Count];
            for (int i = 0; i < call.Arguments.Count; i++)
                argRegs[i] = CompileExpr(call.Arguments[i]);

            // Phase 2: move args to r0, r1, ...
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (argRegs[i] != i)
                    Emit(OpCode.MOVE, i, argRegs[i]);
            }

            Emit(OpCode.SYSCALL, slot, 0, call.Arguments.Count);
        }

        // ===== OpCode mapping =====

        private static OpCode BinOpCode(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Add: return OpCode.ADD;
                case NodeKind.Sub: return OpCode.SUB;
                case NodeKind.Mul: return OpCode.MUL;
                case NodeKind.Div: return OpCode.DIV;
                case NodeKind.Mod: return OpCode.MOD;
                case NodeKind.Eq:  return OpCode.CMP_EQ;
                case NodeKind.Neq: return OpCode.CMP_NEQ;
                case NodeKind.Lt:  return OpCode.CMP_LT;
                case NodeKind.Lte: return OpCode.CMP_LTE;
                case NodeKind.Gt:  return OpCode.CMP_GT;
                case NodeKind.Gte: return OpCode.CMP_GTE;
                case NodeKind.And: return OpCode.AND;
                case NodeKind.Or:  return OpCode.OR;
                default: return OpCode.NOP;
            }
        }

        private static OpCode UnOpCode(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Negate: return OpCode.NEG;
                case NodeKind.Not:    return OpCode.NOT;
                default: return OpCode.NOP;
            }
        }
    }
}
