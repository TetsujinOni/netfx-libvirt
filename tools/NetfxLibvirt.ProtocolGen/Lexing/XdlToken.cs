namespace NetfxLibvirt.ProtocolGen.Lexing;

/// <summary>A single lexeme, with 1-based source position for error reporting.</summary>
public readonly record struct XdlToken(XdlTokenKind Kind, string Text, int Line, int Column)
{
    public override string ToString() => $"{Kind} {Text.Length switch { > 40 => Text[..40] + "...", _ => Text }} @{Line}:{Column}";
}
