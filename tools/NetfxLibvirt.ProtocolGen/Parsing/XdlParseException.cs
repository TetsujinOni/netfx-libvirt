using NetfxLibvirt.ProtocolGen.Lexing;

namespace NetfxLibvirt.ProtocolGen.Parsing;

/// <summary>Thrown when the token stream doesn't match the XDR/RPCL grammar
/// (<c>sunrpc.y</c>'s grammar, cross-checked against go-libvirt's parser of
/// the same name).</summary>
public sealed class XdlParseException : Exception
{
    public XdlParseException(string message, XdlToken found)
        : base($"{message}, found {found}")
    {
        Found = found;
    }

    public XdlToken Found { get; }
}
