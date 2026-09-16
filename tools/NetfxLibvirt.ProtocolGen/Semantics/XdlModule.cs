using NetfxLibvirt.ProtocolGen.Ast;

namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>
/// A resolved <c>.x</c> file: definitions indexed by name for lookup during
/// type-shape resolution, plus folded constants and the extracted RPC
/// procedure list. Built by <see cref="XdlModuleBuilder"/> from the raw,
/// unresolved <see cref="XdlSpecification"/> the parser produces.
/// </summary>
public sealed class XdlModule
{
    public required XdlConstantTable Constants { get; init; }
    public required IReadOnlyDictionary<string, XdlStructDefinition> Structs { get; init; }
    public required IReadOnlyDictionary<string, XdlEnumDefinition> Enums { get; init; }
    public required IReadOnlyDictionary<string, XdlUnionDefinition> Unions { get; init; }
    public required IReadOnlyDictionary<string, XdlDeclaration> Typedefs { get; init; }
    public required IReadOnlyList<XdlProcedure> Procedures { get; init; }
}
