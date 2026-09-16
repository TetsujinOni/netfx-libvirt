using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Tests.Semantics;

public class XdlTypeResolverTests
{
    private static XdlModule BuildModule(string source) => XdlModuleBuilder.Build(XdlParser.Parse(source));

    [Fact]
    public void Resolve_FixedOpaqueViaTypedefChain_ResolvesToFixedOpaqueShape()
    {
        const string source = """
            const VIR_UUID_BUFLEN = 16;
            typedef opaque remote_uuid[VIR_UUID_BUFLEN];
            struct s { remote_uuid uuid; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var fixedOpaque = Assert.IsType<XdlFixedOpaqueShape>(shape);
        Assert.Equal(16, fixedOpaque.Length);
    }

    [Fact]
    public void Resolve_BoundedStringViaTypedefChain_ResolvesToBoundedStringShape()
    {
        const string source = """
            const REMOTE_STRING_MAX = 4194304;
            typedef string remote_nonnull_string<REMOTE_STRING_MAX>;
            struct s { remote_nonnull_string name; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var bounded = Assert.IsType<XdlBoundedStringShape>(shape);
        Assert.Equal(4194304, bounded.MaxLength);
    }

    [Fact]
    public void Resolve_OptionalOverBoundedStringViaDoubleTypedefChain_Nests()
    {
        // Mirrors the real remote_string -> remote_nonnull_string -> string<N> chain.
        const string source = """
            const REMOTE_STRING_MAX = 4194304;
            typedef string remote_nonnull_string<REMOTE_STRING_MAX>;
            typedef remote_nonnull_string *remote_string;
            struct s { remote_string name; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var optional = Assert.IsType<XdlOptionalShape>(shape);
        var bounded = Assert.IsType<XdlBoundedStringShape>(optional.Inner);
        Assert.Equal(4194304, bounded.MaxLength);
    }

    [Fact]
    public void Resolve_DirectStructReference_ResolvesToStructShape()
    {
        const string source = """
            struct inner { int x; };
            struct outer { inner value; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["outer"].Fields[0], module);
        var structShape = Assert.IsType<XdlStructShape>(shape);
        Assert.Equal("inner", structShape.Definition.Name);
    }

    [Fact]
    public void Resolve_BoundedListOfEnum_ResolvesToBoundedListShapeWrappingEnum()
    {
        const string source = """
            const MAX = 20;
            enum kind { A = 0, B = 1 };
            struct s { kind values<MAX>; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var list = Assert.IsType<XdlBoundedListShape>(shape);
        Assert.Equal(20, list.MaxLength);
        Assert.IsType<XdlEnumShape>(list.Element);
    }

    [Fact]
    public void Resolve_UnboundedVariableList_HasNullMaxLength()
    {
        const string source = """
            struct inner { int x; };
            struct s { inner values<>; };
            """;
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var list = Assert.IsType<XdlBoundedListShape>(shape);
        Assert.Null(list.MaxLength);
    }

    [Fact]
    public void Resolve_Primitive_MapsToExpectedCSharpTypeAndWireMethod()
    {
        const string source = "struct s { unsigned hyper big; };";
        var module = BuildModule(source);
        var shape = XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module);
        var primitive = Assert.IsType<XdlPrimitiveShape>(shape);
        Assert.Equal("ulong", primitive.CSharpType);
        Assert.Equal("UHyper", primitive.WireMethodSuffix);
    }

    [Fact]
    public void Resolve_BareOpaqueWithNoBound_Throws()
    {
        // Not valid XDR (RFC 4506 requires opaque/string to be array-declared)
        // but syntactically parseable, so the resolver — not the parser — is
        // what has to reject it.
        const string source = "struct s { opaque bad; };";
        var module = BuildModule(source);
        Assert.Throws<InvalidOperationException>(() => XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module));
    }

    [Fact]
    public void Resolve_UnknownTypeName_Throws()
    {
        const string source = "struct s { totally_unknown_type x; };";
        var module = BuildModule(source);
        Assert.Throws<InvalidOperationException>(() => XdlTypeResolver.ResolveDeclaration(module.Structs["s"].Fields[0], module));
    }
}
