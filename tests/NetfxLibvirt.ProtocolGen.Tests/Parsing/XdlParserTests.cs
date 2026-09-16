using NetfxLibvirt.ProtocolGen.Ast;
using NetfxLibvirt.ProtocolGen.Parsing;

namespace NetfxLibvirt.ProtocolGen.Tests.Parsing;

public class XdlParserTests
{
    [Fact]
    public void Parse_ConstDefinition_WithDecimalValue()
    {
        var spec = XdlParser.Parse("const REMOTE_STRING_MAX = 4194304;");
        var def = Assert.IsType<XdlConstDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal("REMOTE_STRING_MAX", def.Name);
        Assert.Equal("4194304", def.Value);
    }

    [Fact]
    public void Parse_ConstDefinition_WithHexValue()
    {
        var spec = XdlParser.Parse("const REMOTE_PROGRAM = 0x20008086;");
        var def = Assert.IsType<XdlConstDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal("0x20008086", def.Value);
    }

    [Fact]
    public void Parse_ConstDefinition_WithIdentifierValue_IsDroppedNotThrown()
    {
        // libvirt's .x files carry a few consts defined in terms of #define'd C
        // header names we don't have (e.g. VIR_SECURITY_MODEL_BUFLEN). These
        // must parse without error, but contribute nothing to the AST.
        var spec = XdlParser.Parse("const REMOTE_SECURITY_MODEL_MAX = VIR_SECURITY_MODEL_BUFLEN;");
        Assert.Empty(spec.Definitions);
    }

    [Fact]
    public void Parse_TypedefSimple()
    {
        var spec = XdlParser.Parse("typedef opaque remote_uuid;");
        var def = Assert.IsType<XdlTypedefDefinition>(Assert.Single(spec.Definitions));
        var decl = Assert.IsType<XdlSimpleDeclaration>(def.Declaration);
        Assert.Equal("remote_uuid", decl.Name);
        Assert.Equal("opaque", decl.TypeName);
    }

    [Fact]
    public void Parse_TypedefFixedArray()
    {
        var spec = XdlParser.Parse("typedef opaque remote_uuid[VIR_UUID_BUFLEN];");
        var def = Assert.IsType<XdlTypedefDefinition>(Assert.Single(spec.Definitions));
        var decl = Assert.IsType<XdlFixedArrayDeclaration>(def.Declaration);
        Assert.Equal("remote_uuid", decl.Name);
        Assert.Equal("opaque", decl.TypeName);
        Assert.Equal("VIR_UUID_BUFLEN", decl.Length);
    }

    [Fact]
    public void Parse_TypedefVariableArray_WithBound()
    {
        var spec = XdlParser.Parse("typedef string remote_nonnull_string<REMOTE_STRING_MAX>;");
        var def = Assert.IsType<XdlTypedefDefinition>(Assert.Single(spec.Definitions));
        var decl = Assert.IsType<XdlVariableArrayDeclaration>(def.Declaration);
        Assert.Equal("string", decl.TypeName);
        Assert.Equal("REMOTE_STRING_MAX", decl.MaxLength);
    }

    [Fact]
    public void Parse_TypedefVariableArray_Unbounded()
    {
        var spec = XdlParser.Parse("typedef remote_nonnull_domain remote_domain_list<>;");
        var def = Assert.IsType<XdlTypedefDefinition>(Assert.Single(spec.Definitions));
        var decl = Assert.IsType<XdlVariableArrayDeclaration>(def.Declaration);
        Assert.Null(decl.MaxLength);
    }

    [Fact]
    public void Parse_TypedefPointer_IsOptionalDeclaration()
    {
        var spec = XdlParser.Parse("typedef remote_nonnull_string *remote_string;");
        var def = Assert.IsType<XdlTypedefDefinition>(Assert.Single(spec.Definitions));
        var decl = Assert.IsType<XdlOptionalDeclaration>(def.Declaration);
        Assert.Equal("remote_string", decl.Name);
        Assert.Equal("remote_nonnull_string", decl.TypeName);
    }

    [Fact]
    public void Parse_BareUnsigned_MeansUnsignedInt()
    {
        // Real usage: virnetprotocol.x's virNetMessageHeader declares "unsigned
        // prog;" with no trailing int/short/hyper/char — valid XDR shorthand.
        var spec = XdlParser.Parse("struct s { unsigned prog; };");
        var def = Assert.IsType<XdlStructDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal("unsigned int", def.Fields[0].TypeName);
    }

    [Fact]
    public void Parse_StructDefinition_WithMultipleFields()
    {
        const string source = """
            struct remote_nonnull_domain {
                remote_nonnull_string name;
                remote_uuid uuid;
                int id;
            };
            """;
        var spec = XdlParser.Parse(source);
        var def = Assert.IsType<XdlStructDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal("remote_nonnull_domain", def.Name);
        Assert.Equal(3, def.Fields.Count);
        Assert.Equal(["name", "uuid", "id"], def.Fields.Select(f => f.Name));
        Assert.Equal(["remote_nonnull_string", "remote_uuid", "int"], def.Fields.Select(f => f.TypeName));
    }

    [Fact]
    public void Parse_UnionDefinition_WithMultipleCases()
    {
        const string source = """
            union remote_typed_param_value switch (int type) {
             case 1:
                 int i;
             case 2:
                 hyper l;
             case 3:
                 remote_nonnull_string s;
            };
            """;
        var spec = XdlParser.Parse(source);
        var def = Assert.IsType<XdlUnionDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal("remote_typed_param_value", def.Name);
        Assert.Equal("type", def.Discriminant.Name);
        Assert.Equal("int", def.Discriminant.TypeName);
        Assert.Equal(3, def.Cases.Count);
        Assert.Equal("1", def.Cases[0].Value);
        Assert.False(def.Cases[0].IsDefault);
        Assert.Equal("i", def.Cases[0].Arm.Name);
        Assert.Equal("s", def.Cases[2].Arm.Name);
    }

    [Fact]
    public void Parse_UnionDefinition_WithDefaultCase()
    {
        const string source = """
            union remote_thing switch (int type) {
             case 1:
                 int i;
             default:
                 void_placeholder v;
            };
            """;
        var spec = XdlParser.Parse(source);
        var def = Assert.IsType<XdlUnionDefinition>(Assert.Single(spec.Definitions));
        Assert.True(def.Cases[1].IsDefault);
        Assert.Null(def.Cases[1].Value);
    }

    [Fact]
    public void Parse_EnumDefinition_WithAutoAndExplicitValues()
    {
        const string source = """
            enum remote_domain_state {
                REMOTE_DOMAIN_NOSTATE = 0,
                REMOTE_DOMAIN_RUNNING,
                REMOTE_DOMAIN_BLOCKED
            };
            """;
        var spec = XdlParser.Parse(source);
        var def = Assert.IsType<XdlEnumDefinition>(Assert.Single(spec.Definitions));
        Assert.Equal(3, def.Values.Count);
        Assert.Equal("0", def.Values[0].Value);
        Assert.Null(def.Values[1].Value);
        Assert.False(def.Values[0].IsProcedure);
    }

    [Fact]
    public void Parse_EnumDefinition_ProcedureValues_WithAndWithoutMetadata()
    {
        const string source = """
            enum remote_procedure {
                REMOTE_PROC_CONNECT_OPEN = 1,

                /**
                 * @generate: both
                 */
                REMOTE_PROC_AUTH_LIST = 66
            };
            """;
        var spec = XdlParser.Parse(source);
        var def = Assert.IsType<XdlEnumDefinition>(Assert.Single(spec.Definitions));

        var open = def.Values[0];
        Assert.True(open.IsProcedure);
        Assert.Equal("REMOTE_PROC_CONNECT_OPEN", open.Name);
        Assert.Equal("1", open.Value);
        Assert.Null(open.MetadataComment);

        var authList = def.Values[1];
        Assert.True(authList.IsProcedure);
        Assert.Equal("66", authList.Value);
        Assert.Equal("@generate: both", authList.MetadataComment);
    }

    [Fact]
    public void Parse_MultipleDefinitions_InOneFile()
    {
        const string source = """
            const FOO = 1;
            typedef int bar;
            struct baz {
                int qux;
            };
            """;
        var spec = XdlParser.Parse(source);
        Assert.Equal(3, spec.Definitions.Count);
        Assert.IsType<XdlConstDefinition>(spec.Definitions[0]);
        Assert.IsType<XdlTypedefDefinition>(spec.Definitions[1]);
        Assert.IsType<XdlStructDefinition>(spec.Definitions[2]);
    }

    [Fact]
    public void Parse_MalformedInput_ThrowsParseException()
    {
        Assert.Throws<XdlParseException>(() => XdlParser.Parse("struct {"));
    }

    [Fact]
    public void Parse_NestedEnumAsTypeSpecifier_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() => XdlParser.Parse("typedef enum { A, B } thing;"));
    }
}
