namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>version NAME { procedure; ... } = Value;</c> inside a
/// <c>program</c> block.</summary>
public sealed record XdlVersionDefinition(string Name, IReadOnlyList<XdlProcedureDefinition> Procedures, string Value);
