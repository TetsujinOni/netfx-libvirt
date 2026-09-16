using System.Text;

namespace NetfxLibvirt.ProtocolGen.Lexing;

/// <summary>
/// Hand-written scanner for libvirt's XDR protocol definition language.
/// Mirrors the token set of go-libvirt's <c>internal/lvgen</c> lexer
/// (<c>sunrpc.y</c> / <c>lvlexer.go</c>), which is itself the only other
/// from-scratch parser for this grammar we could find as a cross-check —
/// libvirt's own C build compiles <c>.x</c> files with the system
/// <c>rpcgen</c>, it doesn't ship its own parser.
/// </summary>
public sealed class XdlLexer
{
    private static readonly IReadOnlyDictionary<string, XdlTokenKind> Keywords = new Dictionary<string, XdlTokenKind>
    {
        ["hyper"] = XdlTokenKind.Hyper,
        ["int"] = XdlTokenKind.Int,
        ["short"] = XdlTokenKind.Short,
        ["char"] = XdlTokenKind.Char,
        ["bool"] = XdlTokenKind.Bool,
        ["case"] = XdlTokenKind.Case,
        ["const"] = XdlTokenKind.Const,
        ["default"] = XdlTokenKind.Default,
        ["double"] = XdlTokenKind.Double,
        ["enum"] = XdlTokenKind.Enum,
        ["float"] = XdlTokenKind.Float,
        ["opaque"] = XdlTokenKind.Opaque,
        ["string"] = XdlTokenKind.StringKeyword,
        ["struct"] = XdlTokenKind.Struct,
        ["switch"] = XdlTokenKind.Switch,
        ["typedef"] = XdlTokenKind.Typedef,
        ["union"] = XdlTokenKind.Union,
        ["unsigned"] = XdlTokenKind.Unsigned,
        ["void"] = XdlTokenKind.Void,
        ["program"] = XdlTokenKind.Program,
        ["version"] = XdlTokenKind.Version,
    };

    private static readonly IReadOnlyDictionary<char, XdlTokenKind> Punctuation = new Dictionary<char, XdlTokenKind>
    {
        ['{'] = XdlTokenKind.LBrace,
        ['}'] = XdlTokenKind.RBrace,
        ['['] = XdlTokenKind.LBracket,
        [']'] = XdlTokenKind.RBracket,
        ['<'] = XdlTokenKind.LAngle,
        ['>'] = XdlTokenKind.RAngle,
        ['('] = XdlTokenKind.LParen,
        [')'] = XdlTokenKind.RParen,
        [','] = XdlTokenKind.Comma,
        ['='] = XdlTokenKind.Equals,
        [';'] = XdlTokenKind.Semicolon,
        [':'] = XdlTokenKind.Colon,
        ['*'] = XdlTokenKind.Star,
    };

    private readonly string _input;
    private int _pos;
    private int _line = 1;
    private int _column = 1;

    public XdlLexer(string input)
    {
        _input = input;
    }

    /// <summary>Scans the entire input and returns every token, ending with an
    /// <see cref="XdlTokenKind.Eof"/> sentinel.</summary>
    public List<XdlToken> Tokenize()
    {
        var tokens = new List<XdlToken>();
        while (true)
        {
            var token = NextToken();
            tokens.Add(token);
            if (token.Kind == XdlTokenKind.Eof)
            {
                return tokens;
            }
        }
    }

    private XdlToken NextToken()
    {
        SkipTrivia();

        if (_pos >= _input.Length)
        {
            return new XdlToken(XdlTokenKind.Eof, string.Empty, _line, _column);
        }

        var startLine = _line;
        var startColumn = _column;
        var c = Current;

        if (c == '/' && Peek(1) == '*')
        {
            return LexComment(startLine, startColumn);
        }

        if (char.IsLetter(c))
        {
            return LexIdentifier(startLine, startColumn);
        }

        if (char.IsDigit(c) || (c == '-' && char.IsDigit(Peek(1))))
        {
            return LexNumber(startLine, startColumn);
        }

        if (Punctuation.TryGetValue(c, out var kind))
        {
            Advance();
            return new XdlToken(kind, c.ToString(), startLine, startColumn);
        }

        throw new XdlLexException($"unexpected character '{c}'", startLine, startColumn);
    }

    /// <summary>Skips whitespace and <c>%</c>-directive lines (preprocessor output
    /// libvirt leaves in the <c>.x</c> file, e.g. <c>%#include "internal.h"</c>) —
    /// neither carries protocol information we need.</summary>
    private void SkipTrivia()
    {
        while (_pos < _input.Length)
        {
            var c = Current;
            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            if (c == '%' && _column == 1)
            {
                while (_pos < _input.Length && Current != '\n')
                {
                    Advance();
                }
                continue;
            }

            break;
        }
    }

    private XdlToken LexComment(int startLine, int startColumn)
    {
        var isMetadata = Peek(2) == '*';
        Advance(); // '/'
        Advance(); // '*'
        if (isMetadata)
        {
            Advance(); // second '*'
        }

        var body = new StringBuilder();
        while (true)
        {
            if (_pos >= _input.Length)
            {
                throw new XdlLexException("unterminated block comment", startLine, startColumn);
            }

            if (Current == '*' && Peek(1) == '/')
            {
                Advance();
                Advance();
                break;
            }

            body.Append(Current);
            Advance();
        }

        return isMetadata
            ? new XdlToken(XdlTokenKind.MetadataComment, NormalizeMetadataComment(body.ToString()), startLine, startColumn)
            : NextToken();
    }

    /// <summary>Strips Javadoc-style continuation-line <c>*</c> prefixes (libvirt's
    /// real <c>/** ... */</c> annotations span multiple lines, e.g.
    /// <c>/**\n * @generate: both\n * @priority: high\n */</c>) so callers see
    /// clean <c>@key: value</c> lines rather than the raw comment body.</summary>
    private static string NormalizeMetadataComment(string raw)
    {
        var normalized = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('*'))
            {
                line = line[1..].TrimStart(' ');
            }

            if (line.Length > 0)
            {
                normalized.Add(line);
            }
        }

        return string.Join('\n', normalized);
    }

    private XdlToken LexIdentifier(int startLine, int startColumn)
    {
        var start = _pos;
        while (_pos < _input.Length && (char.IsLetterOrDigit(Current) || Current == '_'))
        {
            Advance();
        }

        var text = _input[start.._pos];
        if (Keywords.TryGetValue(text, out var keywordKind))
        {
            return new XdlToken(keywordKind, text, startLine, startColumn);
        }

        return new XdlToken(IsProcIdentifier(text) ? XdlTokenKind.ProcIdentifier : XdlTokenKind.Identifier, text, startLine, startColumn);
    }

    /// <summary>Matches libvirt's "&lt;PROGRAM&gt;_PROC_&lt;NAME&gt;" naming
    /// convention (e.g. <c>REMOTE_PROC_CONNECT_OPEN</c>): a single leading
    /// underscore-free word, then <c>_PROC_</c>. Ported from go-libvirt's
    /// <c>procIdent</c> in <c>lvlexer.go</c>.</summary>
    private static bool IsProcIdentifier(string ident)
    {
        var ix = ident.IndexOf("_PROC_", StringComparison.Ordinal);
        return ix != -1 && ident.IndexOf('_') == ix;
    }

    private XdlToken LexNumber(int startLine, int startColumn)
    {
        var start = _pos;
        var negative = Current == '-';
        if (negative)
        {
            Advance();
        }

        var isHex = false;
        if (!negative && Current == '0' && Peek(1) is 'x' or 'X')
        {
            isHex = true;
            Advance();
            Advance();
        }

        var digitStart = _pos;
        while (_pos < _input.Length && (isHex ? Uri.IsHexDigit(Current) : char.IsDigit(Current)))
        {
            Advance();
        }

        if (_pos == digitStart)
        {
            throw new XdlLexException("invalid number literal", startLine, startColumn);
        }

        if (_pos < _input.Length && char.IsLetter(Current))
        {
            throw new XdlLexException($"invalid number: {_input[start..(_pos + 1)]}", startLine, startColumn);
        }

        return new XdlToken(XdlTokenKind.Constant, _input[start.._pos], startLine, startColumn);
    }

    private char Current => _input[_pos];

    private char Peek(int ahead) => _pos + ahead < _input.Length ? _input[_pos + ahead] : '\0';

    private void Advance()
    {
        if (_input[_pos] == '\n')
        {
            _line++;
            _column = 1;
        }
        else
        {
            _column++;
        }

        _pos++;
    }
}
