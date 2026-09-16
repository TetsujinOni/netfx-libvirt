namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary>The parsed contents of one <c>.x</c> file: an ordered list of
/// top-level definitions.</summary>
public sealed record XdlSpecification(IReadOnlyList<XdlDefinition> Definitions);
