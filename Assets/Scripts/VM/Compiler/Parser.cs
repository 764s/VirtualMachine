using System;
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
                    var importDecl = ParseIncludeDecl();
                    if (!string.IsNullOrEmpty(importDecl.Alias))
                    {
                        for (int k = 0; k < module.Imports.Count; k++)
                        {
                            if (string.Equals(module.Imports[k].Alias, importDecl.Alias, StringComparison.Ordinal))
                            {
                                _errors.Add($"Duplicate include alias '{importDecl.Alias}' (line {importDecl.Line})");
                                break;
                            }
                        }
                    }
                    module.Imports.Add(importDecl);
                }
                else if (Check(TokenType.Override))
                {
                    // Lang-16: override prefix
                    Advance(); // consume 'override'

                    // override + private → error
                    if (Check(TokenType.Private))
                    {
                        Error("'override' cannot be combined with 'private' — override replaces a cross-file declaration, private avoids conflict by name isolation");
                        Advance();
                        // Skip to next declaration keyword for recovery
                        SkipTo(TokenType.Func, TokenType.Var, TokenType.Const, TokenType.Struct, TokenType.Enum);
                        if (IsAtEnd()) break;
                    }

                    // optional 'public' after override (redundant but allowed)
                    if (Check(TokenType.Public))
                        Advance();

                    ParseDeclWithModifiers(module, isPrivate: false, isOverride: true);
                }
                else if (Check(TokenType.Private) || Check(TokenType.Public))
                {
                    // Lang-15: visibility modifier prefix
                    bool isPrivate = Check(TokenType.Private);
                    Advance(); // consume 'private' or 'public'

                    // Lang-16: check for override after visibility
                    if (Check(TokenType.Override))
                    {
                        if (isPrivate)
                        {
                            Error("'override' cannot be combined with 'private' — override replaces a cross-file declaration, private avoids conflict by name isolation");
                            Advance(); // consume override
                            SkipTo(TokenType.Func, TokenType.Var, TokenType.Const, TokenType.Struct, TokenType.Enum);
                            if (IsAtEnd()) break;
                        }
                        else
                        {
                            Advance(); // consume override (public override ...)
                        }
                        ParseDeclWithModifiers(module, isPrivate: false, isOverride: true);
                    }
                    else
                    {
                        ParseDeclWithModifiers(module, isPrivate: isPrivate, isOverride: false);
                    }
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
                    // Lang-15: @export [private|public] ...
                    bool privAfterExport = false;
                    if (Check(TokenType.Private) || Check(TokenType.Public))
                    {
                        privAfterExport = Check(TokenType.Private);
                        Advance(); // consume visibility modifier
                    }
                    if (Check(TokenType.Func))
                        module.Functions.Add(ParseFuncDecl(isExported: true, isInline: inlineHint, isPrivate: privAfterExport));
                    else if (inlineHint)
                    {
                        Error($"@inline can only be applied to functions, got '{Current().Text}'");
                        Advance();
                    }
                    else if (Check(TokenType.Var))
                        module.ModuleVariables.Add(ParseVarDecl(false, isExported: true, isPrivate: privAfterExport));
                    else if (Check(TokenType.Const))
                        module.ModuleVariables.Add(ParseVarDecl(true, isExported: true, isPrivate: privAfterExport));
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
                else if (Check(TokenType.External))
                {
                    // DX8: external func declaration — host-provided function with parameter metadata
                    var extTok = Advance(); // consume 'external'
                    if (Check(TokenType.Func))
                    {
                        var fd = ParseExternalFuncDecl();
                        fd.ExternalLine = extTok.Line;
                        fd.ExternalColumn = extTok.Column;
                        module.Functions.Add(fd);
                    }
                    else
                    {
                        Error($"Expected 'func' after 'external', got '{Current().Text}'");
                        Advance();
                    }
                }
                else if (Check(TokenType.Struct))
                {
                    module.Structs.Add(ParseStructDecl());
                }
                else if (Check(TokenType.Enum))
                {
                    module.Enums.Add(ParseEnumDecl());
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
                    Error($"Expected 'func', 'struct', 'enum', 'var', 'const', '@export', '@inline', 'private', 'public', 'override', 'external' or 'include', got '{Current().Text}'");
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

        /// <summary>
        /// Lang-16: Shared helper to parse a declaration after visibility/override modifiers have been consumed.
        /// Handles @export, @inline, func, struct, enum, var, const.
        /// </summary>
        private void ParseDeclWithModifiers(ModuleNode module, bool isPrivate, bool isOverride)
        {
            if (Check(TokenType.Export))
            {
                Advance(); // consume @export
                bool inlineHint = false;
                if (Check(TokenType.Inline))
                {
                    inlineHint = true;
                    Advance(); // consume @inline
                }
                if (Check(TokenType.Func))
                    module.Functions.Add(ParseFuncDecl(isExported: true, isInline: inlineHint, isPrivate: isPrivate, isOverride: isOverride));
                else if (inlineHint)
                {
                    Error($"@inline can only be applied to functions, got '{Current().Text}'");
                    Advance();
                }
                else if (Check(TokenType.Var))
                    module.ModuleVariables.Add(ParseVarDecl(false, isExported: true, isPrivate: isPrivate, isOverride: isOverride));
                else if (Check(TokenType.Const))
                    module.ModuleVariables.Add(ParseVarDecl(true, isExported: true, isPrivate: isPrivate, isOverride: isOverride));
                else
                {
                    Error($"Expected 'func', 'var' or 'const' after '@export', got '{Current().Text}'");
                    Advance();
                }
            }
            else if (Check(TokenType.Inline))
            {
                Advance(); // consume @inline
                if (Check(TokenType.Export))
                {
                    Advance(); // consume @export
                    if (Check(TokenType.Func))
                        module.Functions.Add(ParseFuncDecl(isExported: true, isInline: true, isPrivate: isPrivate, isOverride: isOverride));
                    else
                    {
                        Error($"Expected 'func' after '@inline @export', got '{Current().Text}'");
                        Advance();
                    }
                }
                else if (Check(TokenType.Func))
                {
                    module.Functions.Add(ParseFuncDecl(isExported: false, isInline: true, isPrivate: isPrivate, isOverride: isOverride));
                }
                else
                {
                    Error($"Expected '@export' or 'func' after '@inline', got '{Current().Text}'");
                    Advance();
                }
            }
            else if (Check(TokenType.Func))
            {
                module.Functions.Add(ParseFuncDecl(isPrivate: isPrivate, isOverride: isOverride));
            }
            else if (Check(TokenType.Struct))
            {
                module.Structs.Add(ParseStructDecl(isPrivate: isPrivate, isOverride: isOverride));
            }
            else if (Check(TokenType.Enum))
            {
                module.Enums.Add(ParseEnumDecl(isPrivate: isPrivate, isOverride: isOverride));
            }
            else if (Check(TokenType.Var))
            {
                module.ModuleVariables.Add(ParseVarDecl(false, isPrivate: isPrivate, isOverride: isOverride));
            }
            else if (Check(TokenType.Const))
            {
                module.ModuleVariables.Add(ParseVarDecl(true, isPrivate: isPrivate, isOverride: isOverride));
            }
            else
            {
                string prefix = isOverride ? "override" : (isPrivate ? "private" : "public");
                Error($"Expected 'func', 'struct', 'enum', 'var', 'const', '@export' or '@inline' after '{prefix}', got '{Current().Text}'");
                Advance();
            }
        }

        private ImportDecl ParseIncludeDecl()
        {
            int line = Current().Line, col = Current().Column;
            Expect(TokenType.Include, "");
            var pathToken = Expect(TokenType.StringLiteral, "after 'include'");
            string path = pathToken.Text ?? "";
            // Lang-17: optional `as Alias` suffix
            string alias = null;
            if (Current().Type == TokenType.Identifier && Current().Text == "as")
            {
                Advance(); // consume 'as'
                var aliasToken = Expect(TokenType.Identifier, "after 'as'");
                alias = aliasToken.Text;
            }
            var decl = new ImportDecl(path, alias);
            decl.Line = line;
            decl.Column = col;
            decl.PathLine = pathToken.Line;
            decl.PathColumn = pathToken.Column;
            return decl;
        }

        private FuncDecl ParseFuncDecl(bool isExported = false, bool isInline = false, bool isPrivate = false, bool isOverride = false)
        {
            int line = Current().Line, col = Current().Column;
            Expect(TokenType.Func, "");
            string name = Expect(TokenType.Identifier, "after 'func'").Text ?? "?";
            // Lang-18: support dotted name for aliased override (override func Alias.Name)
            string aliasTarget = null;
            if (Check(TokenType.Dot))
            {
                if (!isOverride)
                {
                    Error($"Dotted function name '{name}.<name>' is only valid with 'override' keyword");
                }
                Advance(); // consume '.'
                string memberName = Expect(TokenType.Identifier, "after '.' in function name").Text ?? "?";
                aliasTarget = name;
                name = memberName;
            }
            Expect(TokenType.LParen, "after function name");

            var parameters = new List<ParamDecl>();
            bool seenOptional = false;
            if (!Check(TokenType.RParen))
            {
                do
                {
                    var pNameToken = Expect(TokenType.Identifier, "in parameter list");
                    string pName = pNameToken.Text ?? "?";
                    Expect(TokenType.Colon, "after parameter name");
                    var pTypeToken = Expect(TokenType.Identifier, "for parameter type");
                    string pType = pTypeToken.Text ?? "int";
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
                    var param = new ParamDecl(pName, pType, defaultValue);
                    // DX7: track precise position of parameter type name token
                    param.TypeNameLine = pTypeToken.Line;
                    param.TypeNameColumn = pTypeToken.Column;
                    // DX9: track precise position of parameter name token
                    param.NameLine = pNameToken.Line;
                    param.NameColumn = pNameToken.Column;
                    parameters.Add(param);
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "after parameters");

            string returnType = null;
            if (Match(TokenType.Colon))
            {
                returnType = Expect(TokenType.Identifier, "for return type").Text;
            }

            var body = ParseBlock();
            var decl = new FuncDecl(name, parameters, returnType, body, isPrivate, isExported, isInline, isOverride);
            decl.AliasTarget = aliasTarget;
            decl.Line = line;
            decl.Column = col;
            AttachDocComment(decl, line);
            return decl;
        }

        /// <summary>
        /// DX8: Parse an external function declaration (no body).
        /// Syntax: external func Name(param1: type1, param2: type2): returnType
        /// </summary>
        private FuncDecl ParseExternalFuncDecl()
        {
            int line = Current().Line, col = Current().Column;
            Expect(TokenType.Func, "");
            string name = Expect(TokenType.Identifier, "after 'external func'").Text ?? "?";
            Expect(TokenType.LParen, "after function name");

            var parameters = new List<ParamDecl>();
            if (!Check(TokenType.RParen))
            {
                do
                {
                    var pNameToken = Expect(TokenType.Identifier, "in parameter list");
                    string pName = pNameToken.Text ?? "?";
                    Expect(TokenType.Colon, "after parameter name");
                    var pTypeToken = Expect(TokenType.Identifier, "for parameter type");
                    string pType = pTypeToken.Text ?? "int";
                    var param = new ParamDecl(pName, pType);
                    param.TypeNameLine = pTypeToken.Line;
                    param.TypeNameColumn = pTypeToken.Column;
                    // DX9: track precise position of parameter name token
                    param.NameLine = pNameToken.Line;
                    param.NameColumn = pNameToken.Column;
                    parameters.Add(param);
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "after parameters");

            string returnType = null;
            if (Match(TokenType.Colon))
            {
                returnType = Expect(TokenType.Identifier, "for return type").Text;
            }

            // No body — external functions are host-provided
            var decl = new FuncDecl(name, parameters, returnType, null, false, false, false, false, isExternal: true);
            decl.Line = line;
            decl.Column = col;
            AttachDocComment(decl, line);
            return decl;
        }

        private StructDecl ParseStructDecl(bool isPrivate = false, bool isOverride = false)
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'struct'
            var nameToken = Expect(TokenType.Identifier, "after 'struct'");
            string name = nameToken.Text ?? "?";
            int nameLine = nameToken.Line, nameCol = nameToken.Column;
            // Lang-18: support dotted name for aliased override (override struct Alias.Name)
            string aliasTarget = null;
            if (Check(TokenType.Dot))
            {
                if (!isOverride)
                {
                    Error($"Dotted struct name '{name}.<name>' is only valid with 'override' keyword");
                }
                Advance(); // consume '.'
                var memberToken = Expect(TokenType.Identifier, "after '.' in struct name");
                string memberName = memberToken.Text ?? "?";
                aliasTarget = name;
                name = memberName;
                nameLine = memberToken.Line;
                nameCol = memberToken.Column;
            }
            Expect(TokenType.LBrace, "after struct name");

            var fields = new List<StructField>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                int savedPos = _pos;
                int fLine = Current().Line, fCol = Current().Column;
                string fieldName = Expect(TokenType.Identifier, "for struct field name").Text ?? "?";
                Expect(TokenType.Colon, "after field name");
                var fieldTypeToken = Expect(TokenType.Identifier, "for field type");
                string fieldType = fieldTypeToken.Text ?? "int";
                var sf = new StructField(fieldName, fieldType);
                sf.Line = fLine;
                sf.Column = fCol;
                // DX7: track precise position of field type name token
                sf.TypeNameLine = fieldTypeToken.Line;
                sf.TypeNameColumn = fieldTypeToken.Column;
                fields.Add(sf);
                // B002: accept comma or semicolon as optional field separator
                if (!Match(TokenType.Semicolon))
                    Match(TokenType.Comma);
                // B002: safety guard — if no progress was made, skip token to avoid infinite loop
                if (_pos == savedPos)
                {
                    Error($"Unexpected token '{Current().Text}' in struct body at {Current().Line}:{Current().Column}");
                    Advance();
                }
            }
            Expect(TokenType.RBrace, "to close struct");

            var decl = new StructDecl(name, fields, isPrivate, isOverride);
            decl.AliasTarget = aliasTarget;
            decl.Line = line;
            decl.Column = col;
            decl.NameLine = nameLine;
            decl.NameColumn = nameCol;
            var docLines = CollectDocLines(line);
            if (docLines != null) decl.DocComment = string.Join("\n", docLines);
            _structNames.Add(aliasTarget != null ? aliasTarget + "." + name : name);
            return decl;
        }

        /// <summary>
        /// Lang-13: Parse enum declaration.
        /// <code>enum Name { A, B = expr, C }</code>
        /// </summary>
        private EnumDecl ParseEnumDecl(bool isPrivate = false, bool isOverride = false)
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'enum'
            var enumNameToken = Expect(TokenType.Identifier, "after 'enum'");
            string name = enumNameToken.Text ?? "?";
            int nameLine = enumNameToken.Line, nameCol = enumNameToken.Column;
            // Lang-18: support dotted name for aliased override (override enum Alias.Name)
            string aliasTarget = null;
            if (Check(TokenType.Dot))
            {
                if (!isOverride)
                {
                    Error($"Dotted enum name '{name}.<name>' is only valid with 'override' keyword");
                }
                Advance(); // consume '.'
                var enumMemberToken = Expect(TokenType.Identifier, "after '.' in enum name");
                string memberName = enumMemberToken.Text ?? "?";
                aliasTarget = name;
                name = memberName;
                nameLine = enumMemberToken.Line;
                nameCol = enumMemberToken.Column;
            }
            Expect(TokenType.LBrace, "after enum name");

            var members = new List<EnumMember>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                int mLine = Current().Line, mCol = Current().Column;
                string memberName = Expect(TokenType.Identifier, "for enum member name").Text ?? "?";
                Expr valueExpr = null;
                if (Match(TokenType.Assign))
                {
                    valueExpr = ParseExpression();
                }
                var member = new EnumMember(memberName, valueExpr);
                member.Line = mLine;
                member.Column = mCol;
                members.Add(member);
                if (!Match(TokenType.Comma))
                    break; // no comma means end of member list (or trailing comma handled by loop condition)
            }
            Expect(TokenType.RBrace, "to close enum");

            var decl = new EnumDecl(name, members, isPrivate, isOverride);
            decl.AliasTarget = aliasTarget;
            decl.Line = line;
            decl.Column = col;
            decl.NameLine = nameLine;
            decl.NameColumn = nameCol;
            var enumDocLines = CollectDocLines(line);
            if (enumDocLines != null) decl.DocComment = string.Join("\n", enumDocLines);
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

        private VarDeclStmt ParseVarDecl(bool isConst, bool isExported = false, bool isPrivate = false, bool isOverride = false)
        {
            int line = Current().Line, col = Current().Column;
            Advance(); // consume 'var' or 'const'
            var nameToken = Expect(TokenType.Identifier, isConst ? "after 'const'" : "after 'var'");
            string name = nameToken.Text ?? "?";
            int nameLine = nameToken.Line, nameCol = nameToken.Column;
            // Lang-18: support dotted name for aliased override (override const Alias.Name: type = value)
            string aliasTarget = null;
            if (Check(TokenType.Dot) && isOverride)
            {
                Advance(); // consume '.'
                string memberName = Expect(TokenType.Identifier, "after '.' in variable name").Text ?? "?";
                aliasTarget = name;
                name = memberName;
            }
            Expect(TokenType.Colon, "after variable name");
            var typeToken = Expect(TokenType.Identifier, "for variable type");
            string typeName = typeToken.Text ?? "int";
            int typeNameLine = typeToken.Line, typeNameCol = typeToken.Column;
            // Lang-17: dotted type name for aliased struct types (Alias.StructName)
            if (Check(TokenType.Dot))
            {
                Advance(); // consume '.'
                string memberType = Expect(TokenType.Identifier, "for aliased type name").Text ?? "?";
                typeName = typeName + "." + memberType;
            }

            Expr initializer = null;
            if (Match(TokenType.Assign))
            {
                initializer = ParseExpression();
            }
            else if (isConst)
            {
                Error("'const' declaration requires an initializer");
            }

            var stmt = new VarDeclStmt(name, typeName, initializer, isConst, isExported, isPrivate, isOverride);
            stmt.AliasTarget = aliasTarget;
            stmt.Line = line;
            stmt.Column = col;
            var varDocLines = CollectDocLines(line);
            if (varDocLines != null)
                stmt.DocComment = string.Join("\n", varDocLines);
            // DX7: track precise position of type name token
            stmt.TypeNameLine = typeNameLine;
            stmt.TypeNameColumn = typeNameCol;
            // DX9: track precise position of variable name token
            stmt.NameLine = nameLine;
            stmt.NameColumn = nameCol;
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
            Expr left = ParseBitOr();
            while (Match(TokenType.AmpAmp))
            {
                Expr right = ParseBitOr();
                left = new BinaryExpr(NodeKind.And, left, right);
                left.Line = right.Line;
            }
            return left;
        }

        // Lang-14: bitwise OR (lowest bitwise precedence)
        private Expr ParseBitOr()
        {
            Expr left = ParseBitXor();
            while (Match(TokenType.Pipe))
            {
                Expr right = ParseBitXor();
                left = new BinaryExpr(NodeKind.BitOr, left, right);
            }
            return left;
        }

        // Lang-14: bitwise XOR
        private Expr ParseBitXor()
        {
            Expr left = ParseBitAnd();
            while (Match(TokenType.Caret))
            {
                Expr right = ParseBitAnd();
                left = new BinaryExpr(NodeKind.BitXor, left, right);
            }
            return left;
        }

        // Lang-14: bitwise AND (highest bitwise precedence)
        private Expr ParseBitAnd()
        {
            Expr left = ParseEquality();
            while (Match(TokenType.Amp))
            {
                Expr right = ParseEquality();
                left = new BinaryExpr(NodeKind.BitAnd, left, right);
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
            Expr left = ParseShift();
            while (Check(TokenType.Lt, TokenType.Gt, TokenType.Lte, TokenType.Gte))
            {
                var op = Advance();
                Expr right = ParseShift();
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

        // Lang-14: shift operators (<< >>)
        private Expr ParseShift()
        {
            Expr left = ParseAddition();
            while (Check(TokenType.LtLt, TokenType.GtGt))
            {
                var op = Advance();
                Expr right = ParseAddition();
                NodeKind kind = op.Type == TokenType.LtLt ? NodeKind.Shl : NodeKind.Shr;
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
            if (Match(TokenType.Tilde))
            {
                Expr operand = ParseUnary();
                return new UnaryExpr(NodeKind.BitNot, operand);
            }
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            Expr expr = ParsePrimary();
            while (Check(TokenType.Dot))
            {
                Advance(); // consume '.'
                var fieldToken = Expect(TokenType.Identifier, "after '.'");
                string fieldName = fieldToken.Text ?? "?";
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
                    // DX7: track precise position of the field name token (after '.')
                    fa.FieldNameLine = fieldToken.Line;
                    fa.FieldNameColumn = fieldToken.Column;
                    expr = fa;
                    // Lang-17: check if followed by '{' and 'identifier:' → aliased struct literal
                    if (expr is FieldAccessExpr faExpr && faExpr.Target is IdentifierExpr aliasExpr &&
                        Check(TokenType.LBrace) && IsStructLiteralLookahead())
                    {
                        string qualType = aliasExpr.Name + "." + faExpr.FieldName;
                        Advance(); // consume '{'
                        var fields = new List<(string FieldName, Expr Value, int FieldNameLine, int FieldNameColumn)>();
                        while (!Check(TokenType.RBrace) && !IsAtEnd())
                        {
                            int savedPos = _pos;
                            Token fnTok = Expect(TokenType.Identifier, "for field name in struct literal");
                            string fn = fnTok.Text ?? "?";
                            int fnLine = fnTok.Line;
                            int fnCol = fnTok.Column;
                            Expect(TokenType.Colon, "after field name in struct literal");
                            Expr val = ParseExpression();
                            fields.Add((fn, val, fnLine, fnCol));
                            Match(TokenType.Comma);
                            // B002: safety guard — if no progress was made, skip token to avoid infinite loop
                            if (_pos == savedPos)
                            {
                                Error($"Unexpected token '{Current().Text}' in struct literal at {Current().Line}:{Current().Column}");
                                Advance();
                            }
                        }
                        Expect(TokenType.RBrace, "to close struct literal");
                        var sl = new StructLiteralExpr(qualType, fields);
                        sl.Line = expr.Line;
                        sl.Column = expr.Column;
                        expr = sl;
                    }
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
                // _structNames covers same-file structs; IsStructLiteralLookahead()
                // covers cross-file structs from includes (same heuristic as Lang-17 aliased literals).
                if (Check(TokenType.LBrace) && (_structNames.Contains(tok.Text) || IsStructLiteralLookahead()))
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
            var fields = new List<(string FieldName, Expr Value, int FieldNameLine, int FieldNameColumn)>();
            while (!Check(TokenType.RBrace) && !IsAtEnd())
            {
                int savedPos = _pos;
                Token fieldToken = Expect(TokenType.Identifier, "for field name in struct literal");
                string fieldName = fieldToken.Text ?? "?";
                int fieldLine = fieldToken.Line;
                int fieldCol = fieldToken.Column;
                Expect(TokenType.Colon, "after field name in struct literal");
                Expr value = ParseExpression();
                fields.Add((fieldName, value, fieldLine, fieldCol));
                // allow optional comma between fields
                Match(TokenType.Comma);
                // B002: safety guard — if no progress was made, skip token to avoid infinite loop
                if (_pos == savedPos)
                {
                    Error($"Unexpected token '{Current().Text}' in struct literal at {Current().Line}:{Current().Column}");
                    Advance();
                }
            }
            Expect(TokenType.RBrace, "to close struct literal");
            var expr = new StructLiteralExpr(typeToken.Text, fields);
            expr.Line = typeToken.Line;
            expr.Column = typeToken.Column;
            return expr;
        }

        /// <summary>
        /// Lang-17: Lookahead check: is the current position at '{' followed by 'identifier :'?
        /// Used to disambiguate struct literal from block after dotted type name.
        /// </summary>
        private bool IsStructLiteralLookahead()
        {
            // current = '{', peek +1 = identifier?, peek +2 = ':'?
            if (_pos + 2 >= _tokens.Length) return false;
            return _tokens[_pos + 1].Type == TokenType.Identifier &&
                   _tokens[_pos + 2].Type == TokenType.Colon;
        }
    }
}
