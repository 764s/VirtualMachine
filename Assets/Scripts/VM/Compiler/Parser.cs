using System.Collections.Generic;
using FFVM.AST;

namespace FFVM.Compiler
{
    public class Parser
    {
        private Token[] _tokens;
        private int _pos;
        private List<string> _errors;
        private string[] _sourceLines;
        private HashSet<string> _structNames = new HashSet<string>();

        public ModuleNode Parse(string source, out List<string> errors)
        {
            var lexer = new Lexer(source);
            _tokens = lexer.Tokenize();
            _pos = 0;
            _errors = new List<string>();
            _sourceLines = (source ?? "").Split('\n');

            var module = new ModuleNode("script");

            // Check for lexer errors
            if (_tokens.Length > 0 && _tokens[_tokens.Length - 1].Type == TokenType.Error)
            {
                _errors.Add(_tokens[_tokens.Length - 1].Text);
                errors = _errors;
                return module;
            }

            while (!IsAtEnd())
            {
                if (Check(TokenType.Include))
                {
                    module.Imports.Add(ParseIncludeDecl());
                }
                else if (Check(TokenType.Export))
                {
                    // @export prefix — next must be var, const, or func (possibly preceded by @inline)
                    Advance(); // consume @export
                    bool inlineHint = false;
                    if (Check(TokenType.Inline))
                    {
                        inlineHint = true;
                        Advance(); // consume @inline
                    }
                    if (Check(TokenType.Func))
                        module.Functions.Add(ParseFuncDecl(isExported: true, isInline: inlineHint));
                    else if (inlineHint)
                    {
                        Error($"@inline can only be applied to functions, got '{Current().Text}'");
                        Advance();
                    }
                    else if (Check(TokenType.Var))
                        module.ModuleVariables.Add(ParseVarDecl(false, isExported: true));
                    else if (Check(TokenType.Const))
                        module.ModuleVariables.Add(ParseVarDecl(true, isExported: true));
                    else
                    {
                        Error($"Expected 'func', 'var' or 'const' after '@export', got '{Current().Text}'");
                        Advance();
                    }
                }
                else if (Check(TokenType.Inline))
                {
                    // @inline prefix — must be followed by @export func or just func (warning: non-export ignored)
                    Advance(); // consume @inline
                    if (Check(TokenType.Export))
                    {
                        Advance(); // consume @export
                        if (Check(TokenType.Func))
                            module.Functions.Add(ParseFuncDecl(isExported: true, isInline: true));
                        else
                        {
                            Error($"Expected 'func' after '@inline @export', got '{Current().Text}'");
                            Advance();
                        }
                    }
                    else if (Check(TokenType.Func))
                    {
                        module.Functions.Add(ParseFuncDecl(isExported: false, isInline: true));
                    }
                    else
                    {
                        Error($"Expected '@export' or 'func' after '@inline', got '{Current().Text}'");
                        Advance();
                    }
                }
                else if (Check(TokenType.Func))
                {
                    module.Functions.Add(ParseFuncDecl());
                }
                else if (Check(TokenType.Struct))
                {
                    module.Structs.Add(ParseStructDecl());
                }
                else if (Check(TokenType.Var))
                {
                    module.ModuleVariables.Add(ParseVarDecl(false));
                }
                else if (Check(TokenType.Const))
                {
                    module.ModuleVariables.Add(ParseVarDecl(true));
                }
                else
                {
                    Error($"Expected 'func', 'struct', 'var', 'const', '@export', '@inline' or 'include', got '{Current().Text}'");
                    Advance();
                }
            }

            errors = _errors;
            return module;
        }

        // ===== Token helpers =====

        private Token Current() => _pos < _tokens.Length ? _tokens[_pos] : _tokens[_tokens.Length - 1];
        private bool IsAtEnd() => _pos >= _tokens.Length || _tokens[_pos].Type == TokenType.EOF || _tokens[_pos].Type == TokenType.Error;
        private bool Check(TokenType type) => _pos < _tokens.Length && _tokens[_pos].Type == type;

        private bool Check(params TokenType[] types)
        {
            if (_pos >= _tokens.Length) return false;
            var cur = _tokens[_pos].Type;
            for (int i = 0; i < types.Length; i++)
                if (cur == types[i]) return true;
            return false;
        }

        private Token Advance()
        {
            var tok = _tokens[_pos];
            if (!IsAtEnd()) _pos++;
            return tok;
        }

        private Token Expect(TokenType type, string context)
        {
            if (Check(type)) return Advance();
            Error($"Expected {type} {context}, got '{Current().Text}' at {Current().Line}:{Current().Column}");
            return Current();
        }

        private bool Match(TokenType type)
        {
            if (Check(type)) { Advance(); return true; }
            return false;
        }

        private void Error(string msg)
        {
            _errors.Add(msg);
        }

        private void SkipTo(params TokenType[] syncTokens)
        {
            while (!IsAtEnd())
            {
                for (int i = 0; i < syncTokens.Length; i++)
                    if (Check(syncTokens[i])) return;
                Advance();
            }
        }

        // ===== Top-level =====

        private ImportDecl ParseIncludeDecl()
        {
            Expect(TokenType.Include, "");
            var pathToken = Expect(TokenType.StringLiteral, "after 'include'");
            string path = pathToken.Text ?? "";
            return new ImportDecl(path);
        }

        private FuncDecl ParseFuncDecl(bool isExported = false, bool isInline = false)
        {
            int line = Current().Line, col = Current().Column;
            Expect(TokenType.Func, "");
            string name = Expect(TokenType.Identifier, "after 'func'").Text ?? "?";
            Expect(TokenType.LParen, "after function name");

            var parameters = new List<ParamDecl>();
            bool seenOptional = false;
            if (!Check(TokenType.RParen))
            {
                do
                {
                    string pName = Expect(TokenType.Identifier, "in parameter list").Text ?? "?";
                    Expect(TokenType.Colon, "after parameter name");
                    string pType = Expect(TokenType.Identifier, "for parameter type").Text ?? "int";
                    // FF3: optional default value
                    Expr defaultValue = null;
                    if (Match(TokenType.Assign))
                    {
                        defaultValue = ParseExpression();
                        seenOptional = true;
                    }
                    else if (seenOptional)
                    {
                        _errors.Add($"Required parameter '{pName}' cannot follow optional parameter (line {Current().Line})");
                    }
                    parameters.Add(new ParamDecl(pName, pType, defaultValue));
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "after parameters");

            string returnType = null;
            if (Match(TokenType.Colon))
            {
                returnType = Expect(TokenType.Identifier, "for return type").Text;
            }

            var body = ParseBlock();
            var decl = new FuncDecl(name, parameters, returnType, body, false, isExported, isInline);
            decl.Line = line;
            decl.Column = col;
            AttachDocComment(decl, line);
            return decl;
        }

        private StructDecl ParseStructDecl()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'struct'
            string name = Expect(TokenType.Identifier, "after 'struct'").Text ?? "?";
            Expect(TokenType.LBrace, "after struct name");

            var fields = new List<StructField>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                string fieldName = Expect(TokenType.Identifier, "for struct field name").Text ?? "?";
                Expect(TokenType.Colon, "after field name");
                string fieldType = Expect(TokenType.Identifier, "for field type").Text ?? "int";
                fields.Add(new StructField(fieldName, fieldType));
                Match(TokenType.Semicolon); // optional semicolons
            }
            Expect(TokenType.RBrace, "to close struct");

            var decl = new StructDecl(name, fields);
            decl.Line = line;
            decl.Column = col;
            var docLines = CollectDocLines(line);
            if (docLines != null) decl.DocComment = string.Join("\n", docLines);
            _structNames.Add(name);
            return decl;
        }

        private void AttachDocComment(FuncDecl decl, int declLine)
        {
            var rawLines = CollectDocLines(declLine);
            if (rawLines == null) return;

            var summary = new List<string>();
            string returnDoc = null;
            // paramName → description
            var paramDocs = new Dictionary<string, string>();

            foreach (string line in rawLines)
            {
                if (line.StartsWith("@param "))
                {
                    string rest = line.Substring(7).TrimStart();
                    int spaceIdx = rest.IndexOf(' ');
                    if (spaceIdx > 0)
                        paramDocs[rest.Substring(0, spaceIdx)] = rest.Substring(spaceIdx + 1).TrimStart();
                    else
                        paramDocs[rest] = "";
                }
                else if (line.StartsWith("@return ") || line.StartsWith("@returns "))
                {
                    int idx = line.IndexOf(' ');
                    returnDoc = line.Substring(idx + 1).TrimStart();
                }
                else
                {
                    summary.Add(line);
                }
            }

            decl.DocComment = summary.Count > 0 ? string.Join("\n", summary) : null;
            decl.ReturnDoc = returnDoc;
            foreach (var p in decl.Parameters)
            {
                string doc;
                if (paramDocs.TryGetValue(p.Name, out doc))
                    p.DocComment = doc;
            }
        }

        private List<string> CollectDocLines(int declLine)
        {
            var lines = new List<string>();
            for (int i = declLine - 2; i >= 0; i--)
            {
                string trimmed = _sourceLines[i].TrimStart();
                if (trimmed.StartsWith("///"))
                {
                    string text = trimmed.Substring(3);
                    if (text.Length > 0 && text[0] == ' ') text = text.Substring(1);
                    lines.Add(text);
                }
                else
                {
                    break;
                }
            }
            if (lines.Count == 0) return null;
            lines.Reverse();
            return lines;
        }

        // ===== Statements =====

        private BlockStmt ParseBlock()
        {
            int line = Current().Line, col = Current().Column;
            Expect(TokenType.LBrace, "for block");
            var stmts = new List<Stmt>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                stmts.Add(ParseStatement());
                // Optional semicolons
                Match(TokenType.Semicolon);
            }
            Expect(TokenType.RBrace, "to close block");
            var block = new BlockStmt(stmts);
            block.Line = line;
            block.Column = col;
            return block;
        }

        private Stmt ParseStatement()
        {
            switch (Current().Type)
            {
                case TokenType.Var:    return ParseVarDecl(false);
                case TokenType.Const:  return ParseVarDecl(true);
                case TokenType.If:     return ParseIf();
                case TokenType.While:  return ParseWhile();
                case TokenType.For:    return ParseFor();
                case TokenType.Return: return ParseReturn();
                case TokenType.Wait:   return ParseWait();
                case TokenType.WaitFor: return ParseWaitFor();
                case TokenType.Yield:  return ParseYield();
                case TokenType.Defer:  return ParseDefer();
                case TokenType.Using:  return ParseUsing();
                case TokenType.LBrace: return ParseBlock();
                default:               return ParseExprStatement();
            }
        }

        private VarDeclStmt ParseVarDecl(bool isConst, bool isExported = false)
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'var' or 'const'
            string name = Expect(TokenType.Identifier, isConst ? "after 'const'" : "after 'var'").Text ?? "?";
            Expect(TokenType.Colon, "after variable name");
            string typeName = Expect(TokenType.Identifier, "for variable type").Text ?? "int";

            Expr initializer = null;
            if (Match(TokenType.Assign))
            {
                initializer = ParseExpression();
            }
            else if (isConst)
            {
                Error("'const' declaration requires an initializer");
            }

            var stmt = new VarDeclStmt(name, typeName, initializer, isConst, isExported);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private IfStmt ParseIf()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'if'
            Expr condition = ParseExpression();
            Stmt thenBranch = ParseBlock();
            Stmt elseBranch = null;

            if (Match(TokenType.Else))
            {
                if (Check(TokenType.If))
                    elseBranch = ParseIf();
                else
                    elseBranch = ParseBlock();
            }

            var stmt = new IfStmt(condition, thenBranch, elseBranch);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private WhileStmt ParseWhile()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'while'
            Expr condition = ParseExpression();
            Stmt body = ParseBlock();
            var stmt = new WhileStmt(condition, body);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private ForStmt ParseFor()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'for'

            // Init clause
            Stmt init = null;
            if (!Check(TokenType.Semicolon))
            {
                if (Check(TokenType.Var))
                    init = ParseVarDecl(false);
                else if (Check(TokenType.Const))
                    init = ParseVarDecl(true);
                else
                    init = ParseExprStatement();
            }
            Expect(TokenType.Semicolon, "after for-init");

            // Condition
            Expr cond = null;
            if (!Check(TokenType.Semicolon))
                cond = ParseExpression();
            Expect(TokenType.Semicolon, "after for-condition");

            // Increment
            Expr incr = null;
            if (!Check(TokenType.LBrace))
                incr = ParseExpression();

            Stmt body = ParseBlock();
            var stmt = new ForStmt(init, cond, incr, body);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private ReturnStmt ParseReturn()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'return'

            Expr value = null;
            // Return has a value unless followed by },  a statement keyword, or EOF
            if (!Check(TokenType.RBrace, TokenType.Func, TokenType.Var, TokenType.If,
                       TokenType.While, TokenType.For, TokenType.Return, TokenType.Wait,
                       TokenType.WaitFor, TokenType.Yield, TokenType.Defer, TokenType.Using,
                       TokenType.Struct, TokenType.EOF))
            {
                value = ParseExpression();
            }

            var stmt = new ReturnStmt(value);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private WaitStmt ParseWait()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'wait'
            Expr frameCount = ParseExpression();
            var stmt = new WaitStmt(frameCount);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private YieldStmt ParseYield()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'yield'
            var stmt = new YieldStmt();
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private WaitForStmt ParseWaitFor()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'wait_for'
            Expect(TokenType.LParen, "after 'wait_for'");
            Expr targetId = ParseExpression();
            Expect(TokenType.RParen, "after wait_for expression");
            var stmt = new WaitForStmt(targetId);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private DeferStmt ParseDefer()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'defer'
            var body = ParseBlock();
            var stmt = new DeferStmt(body);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private UsingStmt ParseUsing()
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'using'
            string syscallName = Expect(TokenType.Identifier, "after 'using'").Text ?? "?";
            Expect(TokenType.LParen, "after syscall name in 'using'");
            var args = new List<Expr>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    args.Add(ParseExpression());
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "after arguments in 'using'");
            var body = ParseBlock();
            var stmt = new UsingStmt(syscallName, args, body);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        private Stmt ParseExprStatement()
        {
            int line = Current().Line, col = Current().Column;
            Expr expr = ParseExpression();
            var stmt = new ExprStmt(expr);
            stmt.Line = line;
            stmt.Column = col;
            return stmt;
        }

        // ===== Expressions (precedence climbing) =====

        private Expr ParseExpression()
        {
            return ParseAssignment();
        }

        private Expr ParseAssignment()
        {
            Expr left = ParseLogicalOr();

            if (Match(TokenType.Assign))
            {
                Expr right = ParseAssignment(); // right-associative
                var expr = new AssignExpr(left, right);
                expr.Line = left.Line;
                expr.Column = left.Column;
                return expr;
            }

            return left;
        }

        private Expr ParseLogicalOr()
        {
            Expr left = ParseLogicalAnd();
            while (Match(TokenType.PipePipe))
            {
                Expr right = ParseLogicalAnd();
                left = new BinaryExpr(NodeKind.Or, left, right);
                left.Line = right.Line;
            }
            return left;
        }

        private Expr ParseLogicalAnd()
        {
            Expr left = ParseEquality();
            while (Match(TokenType.AmpAmp))
            {
                Expr right = ParseEquality();
                left = new BinaryExpr(NodeKind.And, left, right);
                left.Line = right.Line;
            }
            return left;
        }

        private Expr ParseEquality()
        {
            Expr left = ParseComparison();
            while (Check(TokenType.Eq, TokenType.Neq))
            {
                var op = Advance();
                Expr right = ParseComparison();
                NodeKind kind = op.Type == TokenType.Eq ? NodeKind.Eq : NodeKind.Neq;
                left = new BinaryExpr(kind, left, right);
            }
            return left;
        }

        private Expr ParseComparison()
        {
            Expr left = ParseAddition();
            while (Check(TokenType.Lt, TokenType.Gt, TokenType.Lte, TokenType.Gte))
            {
                var op = Advance();
                Expr right = ParseAddition();
                NodeKind kind;
                switch (op.Type)
                {
                    case TokenType.Lt:  kind = NodeKind.Lt;  break;
                    case TokenType.Gt:  kind = NodeKind.Gt;  break;
                    case TokenType.Lte: kind = NodeKind.Lte; break;
                    default:            kind = NodeKind.Gte; break;
                }
                left = new BinaryExpr(kind, left, right);
            }
            return left;
        }

        private Expr ParseAddition()
        {
            Expr left = ParseMultiplication();
            while (Check(TokenType.Plus, TokenType.Minus))
            {
                var op = Advance();
                Expr right = ParseMultiplication();
                NodeKind kind = op.Type == TokenType.Plus ? NodeKind.Add : NodeKind.Sub;
                left = new BinaryExpr(kind, left, right);
            }
            return left;
        }

        private Expr ParseMultiplication()
        {
            Expr left = ParseUnary();
            while (Check(TokenType.Star, TokenType.Slash, TokenType.Percent))
            {
                var op = Advance();
                Expr right = ParseUnary();
                NodeKind kind;
                switch (op.Type)
                {
                    case TokenType.Star:    kind = NodeKind.Mul; break;
                    case TokenType.Slash:   kind = NodeKind.Div; break;
                    default:                kind = NodeKind.Mod; break;
                }
                left = new BinaryExpr(kind, left, right);
            }
            return left;
        }

        private Expr ParseUnary()
        {
            if (Match(TokenType.Minus))
            {
                Expr operand = ParseUnary();
                return new UnaryExpr(NodeKind.Negate, operand);
            }
            if (Match(TokenType.Bang))
            {
                Expr operand = ParseUnary();
                return new UnaryExpr(NodeKind.Not, operand);
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            Expr expr = ParsePrimary();
            while (Check(TokenType.Dot))
            {
                Advance(); // consume '.'
                string fieldName = Expect(TokenType.Identifier, "after '.'").Text ?? "?";
                // Lang-8: check if followed by '(' → MemberCallExpr (svc.func(args))
                if (Check(TokenType.LParen) && expr is IdentifierExpr targetIdent)
                {
                    Advance(); // consume '('
                    var args = new List<Expr>();
                    if (!Check(TokenType.RParen))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }
                    Expect(TokenType.RParen, "after arguments");
                    var mc = new MemberCallExpr(targetIdent.Name, fieldName, args);
                    mc.Line = expr.Line;
                    mc.Column = expr.Column;
                    expr = mc;
                }
                else
                {
                    var fa = new FieldAccessExpr(expr, fieldName);
                    fa.Line = expr.Line;
                    fa.Column = expr.Column;
                    expr = fa;
                }
            }
            return expr;
        }

        private Expr ParsePrimary()
        {
            Token tok = Current();

            // Integer literal
            if (tok.Type == TokenType.IntLiteral)
            {
                Advance();
                var expr = new IntLiteralExpr(tok.IntValue);
                expr.Line = tok.Line;
                expr.Column = tok.Column;
                return expr;
            }

            // Float literal
            if (tok.Type == TokenType.FloatLiteral)
            {
                Advance();
                var expr = new NumberLiteralExpr((float)tok.FloatValue);
                expr.Line = tok.Line;
                expr.Column = tok.Column;
                return expr;
            }

            // String literal
            if (tok.Type == TokenType.StringLiteral)
            {
                Advance();
                var expr = new StringLiteralExpr(tok.Text);
                expr.Line = tok.Line;
                expr.Column = tok.Column;
                return expr;
            }

            // Boolean literal
            if (tok.Type == TokenType.True || tok.Type == TokenType.False)
            {
                Advance();
                var expr = new BoolLiteralExpr(tok.Type == TokenType.True);
                expr.Line = tok.Line;
                expr.Column = tok.Column;
                return expr;
            }

            // Identifier or function call or struct literal
            if (tok.Type == TokenType.Identifier)
            {
                Advance();

                // Struct literal: TypeName { field: expr, ... }
                if (Check(TokenType.LBrace) && _structNames.Contains(tok.Text))
                {
                    return ParseStructLiteral(tok);
                }

                // Function call?
                if (Check(TokenType.LParen))
                {
                    Advance(); // consume '('
                    var args = new List<Expr>();
                    if (!Check(TokenType.RParen))
                    {
                        do
                        {
                            args.Add(ParseExpression());
                        } while (Match(TokenType.Comma));
                    }
                    Expect(TokenType.RParen, "after arguments");
                    var call = new CallExpr(tok.Text, args);
                    call.Line = tok.Line;
                    call.Column = tok.Column;
                    return call;
                }

                var ident = new IdentifierExpr(tok.Text);
                ident.Line = tok.Line;
                ident.Column = tok.Column;
                return ident;
            }

            // Parenthesized expression
            if (tok.Type == TokenType.LParen)
            {
                Advance(); // consume '('
                Expr inner = ParseExpression();
                Expect(TokenType.RParen, "after expression");
                return inner;
            }

            Error($"Unexpected token '{tok.Text}' at {tok.Line}:{tok.Column}");
            Advance();
            var err = new IntLiteralExpr(0);
            err.Line = tok.Line;
            err.Column = tok.Column;
            return err;
        }

        private StructLiteralExpr ParseStructLiteral(Token typeToken)
        {
            Advance(); // consume '{'
            var fields = new List<(string FieldName, Expr Value)>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                string fieldName = Expect(TokenType.Identifier, "for field name in struct literal").Text ?? "?";
                Expect(TokenType.Colon, "after field name in struct literal");
                Expr value = ParseExpression();
                fields.Add((fieldName, value));
                // allow optional comma between fields
                Match(TokenType.Comma);
            }
            Expect(TokenType.RBrace, "to close struct literal");
            var expr = new StructLiteralExpr(typeToken.Text, fields);
            expr.Line = typeToken.Line;
            expr.Column = typeToken.Column;
            return expr;
        }
    }
}
