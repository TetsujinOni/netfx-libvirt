namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>struct NAME { ... };</c> — fields are encoded on the wire in
/// declaration order, per RFC 4506.</summary>
public sealed record XdlStructDefinition(string Name, IReadOnlyList<XdlDeclaration> Fields) : XdlDefinition;
