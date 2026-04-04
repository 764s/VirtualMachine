using System.Collections.Generic;
using System.Globalization;

namespace FFVM.Compiler
{
    public enum TokenType
    {
        // Literals
        IntLiteral,
        FloatLiteral,
        StringLiteral,

        // Identifier
        Identifier,

        // Keywords
        Func, Var, If, Else, While, For, Return,
        Wait, WaitFor, Yield, Defer, Using,
        True, False, Struct,

        // Operators
        Plus, Minus, Star, Slash, Percent,
        Assign, Eq, Neq, Lt, Gt, Lte, Gte,
        AmpAmp, PipePipe, Bang,

        // Delimiters
        LParen, RParen, LBrace, RBrace,
        Comma, Colon, Semicolon, Dot,

        // Special
        EOF,
        Error,
    }

    public struct Token
    {
        public TokenType Type;
        public string Text;
        public int Line;
        public int Column;
        public int IntValue;
        public double FloatValue;

        public Token(TokenType type, string text, int line, int col)
        {
            Type = type;
            Text = text;
            Line = line;
            Column = col;
            IntValue = 0;
            FloatValue = 0;
        }

        public override string ToString() => $"{Type}({Text}) @{Line}:{Column}";
    }

    public class Lexer
    {
        private readonly string _source;
        private int _pos;
        private int _line;
        private int _col;

        internal static readonly Dictionary<string, TokenType> Keywords = new Dictionary<string, TokenType>
        {
            { "func",     TokenType.Func },
            { "var",      TokenType.Var },
            { "if",       TokenType.If },
            { "else",     TokenType.Else },
            { "while",    TokenType.While },
            { "for",      TokenType.For },
            { "return",   TokenType.Return },
            { "wait",     TokenType.Wait },
            { "wait_for", TokenType.WaitFor },
            { "yield",    TokenType.Yield },
            { "defer",    TokenType.Defer },
            { "using",    TokenType.Using },
            { "true",     TokenType.True },
            { "false",    TokenType.False },
            { "struct",   TokenType.Struct },
        };

        public Lexer(string source)
        {
            _source = source ?? "";
            _pos = 0;
            _line = 1;
            _col = 1;
        }

        public Token[] Tokenize()
        {
            var tokens = new List<Token>();
            while (true)
            {
                var tok = NextToken();
                tokens.Add(tok);
                if (tok.Type == TokenType.EOF || tok.Type == TokenType.Error)
                    break;
            }
            return tokens.ToArray();
        }

        private char Peek() => _pos < _source.Length ? _source[_pos] : '\0';
        private char PeekAt(int offset) => (_pos + offset) < _source.Length ? _source[_pos + offset] : '\0';

        private char Advance()
        {
            char c = _source[_pos];
            _pos++;
            if (c == '\n') { _line++; _col = 1; }
            else { _col++; }
            return c;
        }

        private void SkipWhitespaceAndComments()
        {
            while (_pos < _source.Length)
            {
                char c = Peek();
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    Advance();
                }
                else if (c == '/' && PeekAt(1) == '/')
                {
                    while (_pos < _source.Length && Peek() != '\n')
                        Advance();
                }
                else
                {
                    break;
                }
            }
        }

        private Token NextToken()
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length)
                return new Token(TokenType.EOF, "", _line, _col);

            int startLine = _line, startCol = _col;
            char c = Peek();

            // Numbers
            if (char.IsDigit(c))
                return ScanNumber(startLine, startCol);

            // String literals
            if (c == '"')
                return ScanString(startLine, startCol);

            // Identifiers and keywords
            if (char.IsLetter(c) || c == '_')
                return ScanIdentifier(startLine, startCol);

            // Operators and delimiters
            Advance();
            switch (c)
            {
                case '+': return new Token(TokenType.Plus, "+", startLine, startCol);
                case '-': return new Token(TokenType.Minus, "-", startLine, startCol);
                case '*': return new Token(TokenType.Star, "*", startLine, startCol);
                case '/': return new Token(TokenType.Slash, "/", startLine, startCol);
                case '%': return new Token(TokenType.Percent, "%", startLine, startCol);
                case '(': return new Token(TokenType.LParen, "(", startLine, startCol);
                case ')': return new Token(TokenType.RParen, ")", startLine, startCol);
                case '{': return new Token(TokenType.LBrace, "{", startLine, startCol);
                case '}': return new Token(TokenType.RBrace, "}", startLine, startCol);
                case ',': return new Token(TokenType.Comma, ",", startLine, startCol);
                case ':': return new Token(TokenType.Colon, ":", startLine, startCol);
                case ';': return new Token(TokenType.Semicolon, ";", startLine, startCol);
                case '.': return new Token(TokenType.Dot, ".", startLine, startCol);
                case '=':
                    if (Peek() == '=') { Advance(); return new Token(TokenType.Eq, "==", startLine, startCol); }
                    return new Token(TokenType.Assign, "=", startLine, startCol);
                case '!':
                    if (Peek() == '=') { Advance(); return new Token(TokenType.Neq, "!=", startLine, startCol); }
                    return new Token(TokenType.Bang, "!", startLine, startCol);
                case '<':
                    if (Peek() == '=') { Advance(); return new Token(TokenType.Lte, "<=", startLine, startCol); }
                    return new Token(TokenType.Lt, "<", startLine, startCol);
                case '>':
                    if (Peek() == '=') { Advance(); return new Token(TokenType.Gte, ">=", startLine, startCol); }
                    return new Token(TokenType.Gt, ">", startLine, startCol);
                case '&':
                    if (Peek() == '&') { Advance(); return new Token(TokenType.AmpAmp, "&&", startLine, startCol); }
                    return new Token(TokenType.Error, $"Unexpected character '&' at {startLine}:{startCol}", startLine, startCol);
                case '|':
                    if (Peek() == '|') { Advance(); return new Token(TokenType.PipePipe, "||", startLine, startCol); }
                    return new Token(TokenType.Error, $"Unexpected character '|' at {startLine}:{startCol}", startLine, startCol);
                default:
                    return new Token(TokenType.Error, $"Unexpected character '{c}' at {startLine}:{startCol}", startLine, startCol);
            }
        }

        private Token ScanNumber(int line, int col)
        {
            int start = _pos;
            while (_pos < _source.Length && char.IsDigit(Peek()))
                Advance();

            if (Peek() == '.' && char.IsDigit(PeekAt(1)))
            {
                Advance(); // skip '.'
                while (_pos < _source.Length && char.IsDigit(Peek()))
                    Advance();
                string text = _source.Substring(start, _pos - start);
                var tok = new Token(TokenType.FloatLiteral, text, line, col);
                tok.FloatValue = double.Parse(text, CultureInfo.InvariantCulture);
                return tok;
            }

            string intText = _source.Substring(start, _pos - start);
            var intTok = new Token(TokenType.IntLiteral, intText, line, col);
            intTok.IntValue = int.Parse(intText, CultureInfo.InvariantCulture);
            return intTok;
        }

        private Token ScanIdentifier(int line, int col)
        {
            int start = _pos;
            while (_pos < _source.Length && (char.IsLetterOrDigit(Peek()) || Peek() == '_'))
                Advance();

            string text = _source.Substring(start, _pos - start);

            if (Keywords.TryGetValue(text, out var kwType))
                return new Token(kwType, text, line, col);

            return new Token(TokenType.Identifier, text, line, col);
        }

        private Token ScanString(int line, int col)
        {
            Advance(); // skip opening '"'
            var sb = new System.Text.StringBuilder();
            while (_pos < _source.Length && Peek() != '"')
            {
                char ch = Peek();
                if (ch == '\n')
                    return new Token(TokenType.Error, $"Unterminated string literal at {line}:{col}", line, col);
                if (ch == '\\')
                {
                    Advance();
                    if (_pos >= _source.Length)
                        return new Token(TokenType.Error, $"Unterminated string literal at {line}:{col}", line, col);
                    char esc = Peek();
                    switch (esc)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case '\\': sb.Append('\\'); break;
                        case '"': sb.Append('"'); break;
                        default:
                            return new Token(TokenType.Error, $"Unknown escape sequence '\\{esc}' at {_line}:{_col}", _line, _col);
                    }
                    Advance();
                }
                else
                {
                    sb.Append(ch);
                    Advance();
                }
            }
            if (_pos >= _source.Length)
                return new Token(TokenType.Error, $"Unterminated string literal at {line}:{col}", line, col);
            Advance(); // skip closing '"'
            return new Token(TokenType.StringLiteral, sb.ToString(), line, col);
        }
    }
}
