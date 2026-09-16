namespace NetfxLibvirt.ProtocolGen.Lexing;

/// <summary>
/// Token kinds for libvirt's XDR protocol definition language (the RFC 4506
/// XDR type grammar plus the RPCL <c>program</c>/<c>version</c> extension),
/// as used by <c>virnetprotocol.x</c> and <c>remote_protocol.x</c>.
/// </summary>
public enum XdlTokenKind
{
    Eof,

    Identifier,

    /// <summary>An identifier matching the "&lt;PROGRAM&gt;_PROC_&lt;NAME&gt;" shape,
    /// e.g. <c>REMOTE_PROC_CONNECT_OPEN</c> — libvirt's convention for naming
    /// procedure enum values, mirrored from go-libvirt's lexer.</summary>
    ProcIdentifier,

    /// <summary>A decimal or <c>0x</c>-prefixed hexadecimal integer literal.</summary>
    Constant,

    /// <summary>The text inside a <c>/** ... */</c> comment, which libvirt uses to
    /// annotate procedure enum values (<c>@generate</c>, <c>@readstream</c>, etc.).</summary>
    MetadataComment,

    // Keywords
    Bool,
    Case,
    Const,
    Default,
    Double,
    Enum,
    Float,
    Opaque,
    StringKeyword,
    Struct,
    Switch,
    Typedef,
    Union,
    Unsigned,
    Void,
    Hyper,
    Int,
    Short,
    Char,
    Program,
    Version,

    // Punctuation
    LBrace,
    RBrace,
    LBracket,
    RBracket,
    LAngle,
    RAngle,
    LParen,
    RParen,
    Comma,
    Equals,
    Semicolon,
    Colon,
    Star,
}
