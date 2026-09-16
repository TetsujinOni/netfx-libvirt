using NetfxLibvirt.ProtocolGen.Ast;

namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>
/// Resolves a declaration's raw <c>TypeName</c> string (a primitive
/// keyword, or the free-text name of another definition) plus its own
/// array/optional wrapper into a concrete <see cref="XdlTypeShape"/>,
/// following typedef chains as needed (e.g. <c>remote_string</c> — a
/// pointer typedef — to <c>remote_nonnull_string</c> — a bounded-string
/// typedef — down to the actual <see cref="XdlBoundedStringShape"/>).
/// </summary>
public static class XdlTypeResolver
{
    private static readonly IReadOnlyDictionary<string, XdlPrimitiveShape> Primitives = new Dictionary<string, XdlPrimitiveShape>
    {
        ["int"] = new("int", "Int"),
        ["unsigned int"] = new("uint", "UInt"),
        ["hyper"] = new("long", "Hyper"),
        ["unsigned hyper"] = new("ulong", "UHyper"),
        ["short"] = new("short", "Int"),
        ["unsigned short"] = new("ushort", "UInt"),
        ["char"] = new("sbyte", "Int"),
        ["unsigned char"] = new("byte", "UInt"),
        ["float"] = new("float", "Float"),
        ["double"] = new("double", "Double"),
        ["bool"] = new("bool", "Bool"),
    };

    /// <summary>Marks the bare <c>opaque</c>/<c>string</c> keyword while a
    /// declaration's own array/optional wrapper is still being applied —
    /// never returned once <see cref="ResolveDeclaration"/> finishes.</summary>
    private sealed record UnwrappedKeywordShape(bool IsString) : XdlTypeShape;

    public static XdlTypeShape ResolveDeclaration(XdlDeclaration declaration, XdlModule module) =>
        ResolveDeclaration(declaration, module, new HashSet<string>());

    private static XdlTypeShape ResolveDeclaration(XdlDeclaration declaration, XdlModule module, HashSet<string> visiting)
    {
        var baseShape = ResolveTypeName(declaration.TypeName, module, visiting);

        XdlTypeShape shape = declaration switch
        {
            XdlSimpleDeclaration => baseShape,
            XdlFixedArrayDeclaration fixedArray => baseShape is UnwrappedKeywordShape
                ? new XdlFixedOpaqueShape(module.Constants.Resolve(fixedArray.Length))
                : new XdlFixedListShape(module.Constants.Resolve(fixedArray.Length), baseShape),
            XdlVariableArrayDeclaration variableArray => baseShape switch
            {
                UnwrappedKeywordShape { IsString: true } => new XdlBoundedStringShape(ResolveOptionalBound(variableArray.MaxLength, module)),
                UnwrappedKeywordShape => new XdlBoundedOpaqueShape(ResolveOptionalBound(variableArray.MaxLength, module)),
                _ => new XdlBoundedListShape(ResolveOptionalBound(variableArray.MaxLength, module), baseShape),
            },
            XdlOptionalDeclaration => new XdlOptionalShape(RequireWrapped(baseShape, declaration)),
            _ => throw new NotSupportedException($"unknown declaration kind {declaration.GetType()}"),
        };

        if (shape is UnwrappedKeywordShape)
        {
            throw new InvalidOperationException(
                $"'{declaration.TypeName} {declaration.Name}' declares bare opaque/string with no array bound — not valid XDR (RFC 4506 requires opaque/string to be array-declared).");
        }

        return shape;
    }

    private static XdlTypeShape RequireWrapped(XdlTypeShape baseShape, XdlDeclaration declaration) =>
        baseShape is UnwrappedKeywordShape
            ? throw new InvalidOperationException($"'{declaration.TypeName} *{declaration.Name}' points directly at bare opaque/string — not seen in libvirt's .x files, unsupported here.")
            : baseShape;

    private static long? ResolveOptionalBound(string? bound, XdlModule module) =>
        bound is null ? null : module.Constants.Resolve(bound);

    private static XdlTypeShape ResolveTypeName(string typeName, XdlModule module, HashSet<string> visiting)
    {
        switch (typeName)
        {
            case "opaque":
                return new UnwrappedKeywordShape(IsString: false);
            case "string":
                return new UnwrappedKeywordShape(IsString: true);
        }

        if (Primitives.TryGetValue(typeName, out var primitive))
        {
            return primitive;
        }

        if (module.Structs.TryGetValue(typeName, out var structDef))
        {
            return new XdlStructShape(structDef);
        }

        if (module.Enums.TryGetValue(typeName, out var enumDef))
        {
            return new XdlEnumShape(enumDef);
        }

        if (module.Unions.TryGetValue(typeName, out var unionDef))
        {
            return new XdlUnionShape(unionDef);
        }

        if (module.Typedefs.TryGetValue(typeName, out var typedefDeclaration))
        {
            if (!visiting.Add(typeName))
            {
                throw new InvalidOperationException($"cyclic typedef chain involving '{typeName}'");
            }

            var resolved = ResolveDeclaration(typedefDeclaration, module, visiting);
            visiting.Remove(typeName);
            return resolved;
        }

        throw new InvalidOperationException($"unknown type '{typeName}' — not a primitive, struct, enum, union, or typedef in this module");
    }
}
