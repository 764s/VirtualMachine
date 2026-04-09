using System.Collections.Generic;

namespace FFVM.AST
{
    // ===== Base Nodes =====

    public enum NodeKind
    {
        // Literals
        NumberLiteral,
        IntLiteral,
        BoolLiteral,
        StringIdLiteral,
        StringLiteral,

        // Identifiers
        Identifier,
        FieldAccess,

        // Expressions - Arithmetic
        Add, Sub, Mul, Div, Mod, Negate,

        // Expressions - Comparison
        Eq, Neq, Lt, Gt, Lte, Gte,

        // Expressions - Logical
        And, Or, Not,

        // Expressions - Assignment
        Assign,

        // Expressions - Function/Syscall
        Call,
        SyscallExpr,
        MemberCall,         // Lang-8: svc.func(args) cross-instance call

        // Expressions - Struct literal
        StructLiteral,

        // Statements
        Block,
        VarDecl,
        If,
        While,
        For,
        Return,
        Wait,
        WaitFor,
        Yield,
        Defer,
        Using,
        ExprStatement,

        // Top-level declarations
        FuncDecl,
        StructDecl,
        Import,
        Module,
    }

    /// <summary>
    /// Base AST node. All nodes carry source location for error reporting.
    /// </summary>
    public abstract class ASTNode
    {
        public NodeKind Kind { get; }
        public int Line { get; set; }
        public int Column { get; set; }

        protected ASTNode(NodeKind kind)
        {
            Kind = kind;
        }
    }

    // ===== Expressions =====

    public abstract class Expr : ASTNode
    {
        protected Expr(NodeKind kind) : base(kind) { }
    }

    public class NumberLiteralExpr : Expr
    {
        public float Value { get; }
        public NumberLiteralExpr(float value) : base(NodeKind.NumberLiteral) { Value = value; }
    }

    public class IntLiteralExpr : Expr
    {
        public int Value { get; }
        public IntLiteralExpr(int value) : base(NodeKind.IntLiteral) { Value = value; }
    }

    public class BoolLiteralExpr : Expr
    {
        public bool Value { get; }
        public BoolLiteralExpr(bool value) : base(NodeKind.BoolLiteral) { Value = value; }
    }

    public class StringIdLiteralExpr : Expr
    {
        public int HashId { get; }
        public string DebugText { get; }
        public StringIdLiteralExpr(int hashId, string debugText) : base(NodeKind.StringIdLiteral)
        {
            HashId = hashId;
            DebugText = debugText;
        }
    }

    public class StringLiteralExpr : Expr
    {
        public string Value { get; }
        public StringLiteralExpr(string value) : base(NodeKind.StringLiteral) { Value = value; }
    }

    public class IdentifierExpr : Expr
    {
        public string Name { get; }
        public IdentifierExpr(string name) : base(NodeKind.Identifier) { Name = name; }
    }

    /// <summary>
    /// Struct field access: expr.fieldName
    /// At compile time this resolves to register offset.
    /// </summary>
    public class FieldAccessExpr : Expr
    {
        public Expr Target { get; }
        public string FieldName { get; }
        public FieldAccessExpr(Expr target, string fieldName) : base(NodeKind.FieldAccess)
        {
            Target = target;
            FieldName = fieldName;
        }
    }

    // Binary ops
    public class BinaryExpr : Expr
    {
        public Expr Left { get; }
        public Expr Right { get; }
        public BinaryExpr(NodeKind op, Expr left, Expr right) : base(op)
        {
            Left = left;
            Right = right;
        }
    }

    // Unary ops
    public class UnaryExpr : Expr
    {
        public Expr Operand { get; }
        public UnaryExpr(NodeKind op, Expr operand) : base(op) { Operand = operand; }
    }

    public class AssignExpr : Expr
    {
        public Expr Target { get; }
        public Expr Value { get; }
        public AssignExpr(Expr target, Expr value) : base(NodeKind.Assign)
        {
            Target = target;
            Value = value;
        }
    }

    public class CallExpr : Expr
    {
        public string FunctionName { get; }
        public List<Expr> Arguments { get; }
        public CallExpr(string functionName, List<Expr> arguments) : base(NodeKind.Call)
        {
            FunctionName = functionName;
            Arguments = arguments;
        }
    }

    /// <summary>
    /// Lang-8: Cross-instance member function call: svc.func(args)
    /// Compiler routes to XCALL / XLOAD_MVAR / XSTORE_MVAR based on export table.
    /// </summary>
    public class MemberCallExpr : Expr
    {
        public string TargetName { get; }
        public string MemberName { get; }
        public List<Expr> Arguments { get; }
        public MemberCallExpr(string targetName, string memberName, List<Expr> arguments)
            : base(NodeKind.MemberCall)
        {
            TargetName = targetName;
            MemberName = memberName;
            Arguments = arguments;
        }
    }

    public class SyscallExpr : Expr
    {
        public int SyscallSlot { get; }
        public string SyscallName { get; }
        public List<Expr> Arguments { get; }
        public SyscallExpr(int slot, string name, List<Expr> arguments) : base(NodeKind.SyscallExpr)
        {
            SyscallSlot = slot;
            SyscallName = name;
            Arguments = arguments;
        }
    }

    public class StructLiteralExpr : Expr
    {
        public string TypeName { get; }
        public List<(string FieldName, Expr Value)> Fields { get; }
        public StructLiteralExpr(string typeName, List<(string FieldName, Expr Value)> fields)
            : base(NodeKind.StructLiteral)
        {
            TypeName = typeName;
            Fields = fields;
        }
    }

    // ===== Statements =====

    public abstract class Stmt : ASTNode
    {
        protected Stmt(NodeKind kind) : base(kind) { }
    }

    public class BlockStmt : Stmt
    {
        public List<Stmt> Statements { get; }
        public BlockStmt(List<Stmt> statements) : base(NodeKind.Block) { Statements = statements; }
    }

    /// <summary>
    /// Variable declaration: var/const name: type = initializer;
    /// TypeName is used for compile-time struct flattening.
    /// IsConst enables compile-time constant propagation (B-ε3).
    /// </summary>
    public class VarDeclStmt : Stmt
    {
        public string Name { get; }
        public string TypeName { get; }
        public Expr Initializer { get; }
        public bool IsConst { get; }
        public bool IsExported { get; }
        public VarDeclStmt(string name, string typeName, Expr initializer, bool isConst = false, bool isExported = false) : base(NodeKind.VarDecl)
        {
            Name = name;
            TypeName = typeName;
            Initializer = initializer;
            IsConst = isConst;
            IsExported = isExported;
        }
    }

    public class IfStmt : Stmt
    {
        public Expr Condition { get; }
        public Stmt ThenBranch { get; }
        public Stmt ElseBranch { get; }
        public IfStmt(Expr condition, Stmt thenBranch, Stmt elseBranch) : base(NodeKind.If)
        {
            Condition = condition;
            ThenBranch = thenBranch;
            ElseBranch = elseBranch;
        }
    }

    public class WhileStmt : Stmt
    {
        public Expr Condition { get; }
        public Stmt Body { get; }
        public WhileStmt(Expr condition, Stmt body) : base(NodeKind.While)
        {
            Condition = condition;
            Body = body;
        }
    }

    public class ForStmt : Stmt
    {
        public Stmt Initializer { get; }
        public Expr Condition { get; }
        public Expr Increment { get; }
        public Stmt Body { get; }
        public ForStmt(Stmt init, Expr cond, Expr incr, Stmt body) : base(NodeKind.For)
        {
            Initializer = init;
            Condition = cond;
            Increment = incr;
            Body = body;
        }
    }

    public class ReturnStmt : Stmt
    {
        public Expr Value { get; }
        public ReturnStmt(Expr value) : base(NodeKind.Return) { Value = value; }
    }

    /// <summary>
    /// wait(N) — suspend this instance for N frames.
    /// </summary>
    public class WaitStmt : Stmt
    {
        public Expr FrameCount { get; }
        public WaitStmt(Expr frameCount) : base(NodeKind.Wait) { FrameCount = frameCount; }
    }

    /// <summary>
    /// wait_for(instanceId) — suspend until target instance completes.
    /// </summary>
    public class WaitForStmt : Stmt
    {
        public Expr TargetInstanceId { get; }
        public WaitForStmt(Expr targetId) : base(NodeKind.WaitFor) { TargetInstanceId = targetId; }
    }

    /// <summary>
    /// yield — suspend for 1 frame (syntactic sugar for wait(1)).
    /// </summary>
    public class YieldStmt : Stmt
    {
        public YieldStmt() : base(NodeKind.Yield) { }
    }

    /// <summary>
    /// defer { ... } — register a cleanup block to execute on scope exit or forced kill.
    /// </summary>
    public class DeferStmt : Stmt
    {
        public BlockStmt Body { get; }
        public DeferStmt(BlockStmt body) : base(NodeKind.Defer) { Body = body; }
    }

    /// <summary>
    /// using SyscallName(args) { body } — acquire resource via paired syscall, auto-release on exit/kill.
    /// Compiles to: SYSCALL(acquire) + PUSH_CLEANUP(release_block) + body + POP_CLEANUP.
    /// </summary>
    public class UsingStmt : Stmt
    {
        public string SyscallName { get; }
        public List<Expr> Arguments { get; }
        public BlockStmt Body { get; }
        public UsingStmt(string syscallName, List<Expr> arguments, BlockStmt body) : base(NodeKind.Using)
        {
            SyscallName = syscallName;
            Arguments = arguments;
            Body = body;
        }
    }

    public class ExprStmt : Stmt
    {
        public Expr Expression { get; }
        public ExprStmt(Expr expression) : base(NodeKind.ExprStatement) { Expression = expression; }
    }

    // ===== Top-Level Declarations =====

    public class ParamDecl
    {
        public string Name { get; }
        public string TypeName { get; }
        public string DocComment { get; set; }
        /// <summary>FF3: Optional default value expression (null = required parameter).</summary>
        public Expr DefaultValue { get; }
        public ParamDecl(string name, string typeName, Expr defaultValue = null)
        {
            Name = name;
            TypeName = typeName;
            DefaultValue = defaultValue;
        }
    }

    public class FuncDecl : ASTNode
    {
        public string Name { get; }
        public List<ParamDecl> Parameters { get; }
        public string ReturnType { get; }
        public BlockStmt Body { get; }
        public bool IsPrivate { get; }
        public bool IsExported { get; }
        public bool IsInline { get; }
        public string DocComment { get; set; }
        public string ReturnDoc { get; set; }

        public FuncDecl(string name, List<ParamDecl> parameters, string returnType, BlockStmt body, bool isPrivate, bool isExported = false, bool isInline = false)
            : base(NodeKind.FuncDecl)
        {
            Name = name;
            Parameters = parameters;
            ReturnType = returnType;
            Body = body;
            IsPrivate = isPrivate;
            IsExported = isExported;
            IsInline = isInline;
        }
    }

    public class StructField
    {
        public string Name { get; }
        public string TypeName { get; }
        public StructField(string name, string typeName)
        {
            Name = name;
            TypeName = typeName;
        }
    }

    public class StructDecl : ASTNode
    {
        public string Name { get; }
        public List<StructField> Fields { get; }
        public string DocComment { get; set; }
        public StructDecl(string name, List<StructField> fields) : base(NodeKind.StructDecl)
        {
            Name = name;
            Fields = fields;
        }
    }

    public class ImportDecl : ASTNode
    {
        public string ModulePath { get; }
        public ImportDecl(string modulePath) : base(NodeKind.Import) { ModulePath = modulePath; }
    }

    /// <summary>
    /// Root node of a .ffs file.
    /// </summary>
    public class ModuleNode : ASTNode
    {
        public string FilePath { get; }
        public List<ImportDecl> Imports { get; }
        public List<StructDecl> Structs { get; }
        public List<FuncDecl> Functions { get; }
        public List<VarDeclStmt> ModuleVariables { get; }

        public ModuleNode(string filePath) : base(NodeKind.Module)
        {
            FilePath = filePath;
            Imports = new List<ImportDecl>();
            Structs = new List<StructDecl>();
            Functions = new List<FuncDecl>();
            ModuleVariables = new List<VarDeclStmt>();
        }
    }
}
