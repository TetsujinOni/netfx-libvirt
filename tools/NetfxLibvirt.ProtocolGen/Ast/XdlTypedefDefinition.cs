namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>typedef &lt;declaration&gt;;</c> — introduces
/// <see cref="XdlDeclaration.Name"/> as an alias, with
/// <see cref="XdlDeclaration.TypeName"/> (plus the declaration's own
/// array/optional shape) as its definition.</summary>
public sealed record XdlTypedefDefinition(XdlDeclaration Declaration) : XdlDefinition;
