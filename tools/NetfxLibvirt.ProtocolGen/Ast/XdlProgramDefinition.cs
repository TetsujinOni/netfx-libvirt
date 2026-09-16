namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>program NAME { version; ... } = Value;</c>. See
/// <see cref="XdlProcedureDefinition"/> — unused by libvirt's current
/// <c>.x</c> files, supported for grammar completeness.</summary>
public sealed record XdlProgramDefinition(string Name, IReadOnlyList<XdlVersionDefinition> Versions, string Value)
    : XdlDefinition;
