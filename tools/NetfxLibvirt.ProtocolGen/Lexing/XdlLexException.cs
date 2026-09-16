namespace NetfxLibvirt.ProtocolGen.Lexing;

/// <summary>Thrown when the lexer encounters input that cannot form a valid token
/// (an unterminated comment/directive, or a malformed number literal).</summary>
public sealed class XdlLexException : Exception
{
    public XdlLexException(string message, int line, int column)
        : base($"{message} at {line}:{column}")
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }
}
