namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary>One <c>case VALUE: declaration;</c> arm, or the
/// <c>default: declaration;</c> arm when <see cref="IsDefault"/> is set (in
/// which case <see cref="Value"/> is null).</summary>
public sealed record XdlUnionCase(string? Value, bool IsDefault, XdlDeclaration Arm);

/// <summary><c>union NAME switch (type discriminant) { case ...; };</c> — a
/// discriminated union. <paramref name="Discriminant"/> is always a
/// <c>simple_declaration</c> per the XDR grammar (the switch variable can't
/// itself be an array or optional).</summary>
public sealed record XdlUnionDefinition(string Name, XdlSimpleDeclaration Discriminant, IReadOnlyList<XdlUnionCase> Cases)
    : XdlDefinition;
