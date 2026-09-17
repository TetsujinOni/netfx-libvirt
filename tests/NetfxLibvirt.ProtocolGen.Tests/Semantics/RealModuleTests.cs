using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Tests.Semantics;

/// <summary>Builds the resolved semantic module from the real, vendored
/// <c>remote_protocol.x</c> — this project's validation standard applied one
/// layer up from <c>RealProtocolFileTests</c> (parsing): the MVP procedure
/// set's args/ret struct names and field shapes, resolved against the actual
/// file, not a hand-picked snippet.</summary>
public class RealModuleTests
{
    // Copied here at build time (see this project's .csproj), not located by
    // walking up from AppContext.BaseDirectory looking for the repo root —
    // that breaks whenever the source tree isn't checked out alongside the
    // test binaries.
    //
    // Fetched (reference/fetch-upstream-x.sh), not vendored — see
    // reference/README.md — so it may genuinely not be there yet on a fresh
    // clone; skip cleanly rather than fail, matching this project's usual
    // "missing prerequisite -> skip" pattern (Docker, WSL socket, etc.).
    private static XdlModule BuildRemoteProtocolModule()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "reference", "upstream-x", "remote_protocol.x");
        if (!File.Exists(path))
        {
            Assert.Skip("remote_protocol.x hasn't been fetched — run reference/fetch-upstream-x.sh first.");
        }

        return XdlModuleBuilder.Build(XdlParser.Parse(File.ReadAllText(path)));
    }

    [Theory]
    [InlineData("REMOTE_PROC_CONNECT_OPEN", "remote_connect_open_args", null)]
    [InlineData("REMOTE_PROC_CONNECT_CLOSE", null, null)]
    [InlineData("REMOTE_PROC_CONNECT_GET_CAPABILITIES", null, "remote_connect_get_capabilities_ret")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_XML_DESC", "remote_domain_get_xml_desc_args", "remote_domain_get_xml_desc_ret")]
    [InlineData("REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME", "remote_domain_lookup_by_name_args", "remote_domain_lookup_by_name_ret")]
    [InlineData("REMOTE_PROC_DOMAIN_DESTROY", "remote_domain_destroy_args", null)]
    [InlineData("REMOTE_PROC_DOMAIN_SHUTDOWN", "remote_domain_shutdown_args", null)]
    [InlineData("REMOTE_PROC_DOMAIN_CREATE", "remote_domain_create_args", null)]
    [InlineData("REMOTE_PROC_DOMAIN_GET_INFO", "remote_domain_get_info_args", "remote_domain_get_info_ret")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_STATE", "remote_domain_get_state_args", "remote_domain_get_state_ret")]
    [InlineData("REMOTE_PROC_AUTH_LIST", null, "remote_auth_list_ret")]
    [InlineData("REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS", "remote_connect_list_all_domains_args", "remote_connect_list_all_domains_ret")]
    public void MvpProcedure_ResolvesExpectedArgsAndRetStructNames(string procName, string? expectedArgs, string? expectedRet)
    {
        var module = BuildRemoteProtocolModule();
        var proc = module.Procedures.Single(p => p.Name == procName);
        Assert.Equal(expectedArgs, proc.ArgsStructName);
        Assert.Equal(expectedRet, proc.RetStructName);
    }

    [Fact]
    public void RemoteNonnullDomain_ResolvesToNameUuidId()
    {
        var module = BuildRemoteProtocolModule();
        var domain = module.Structs["remote_nonnull_domain"];

        var nameShape = XdlTypeResolver.ResolveDeclaration(domain.Fields[0], module);
        Assert.IsType<XdlBoundedStringShape>(nameShape);

        var uuidShape = XdlTypeResolver.ResolveDeclaration(domain.Fields[1], module);
        var uuid = Assert.IsType<XdlFixedOpaqueShape>(uuidShape);
        Assert.Equal(16, uuid.Length);

        var idShape = XdlTypeResolver.ResolveDeclaration(domain.Fields[2], module);
        Assert.Equal("int", Assert.IsType<XdlPrimitiveShape>(idShape).CSharpType);
    }

    [Fact]
    public void RemoteDomainGetInfoRet_FieldsResolveToExpectedPrimitives()
    {
        var module = BuildRemoteProtocolModule();
        var ret = module.Structs["remote_domain_get_info_ret"];

        var expected = new[] { "byte", "ulong", "ulong", "ushort", "ulong" };
        var actual = ret.Fields.Select(f => Assert.IsType<XdlPrimitiveShape>(XdlTypeResolver.ResolveDeclaration(f, module)).CSharpType);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemoteAuthListRet_TypesField_IsBoundedListOfRemoteAuthTypeEnum()
    {
        var module = BuildRemoteProtocolModule();
        var ret = module.Structs["remote_auth_list_ret"];
        var shape = XdlTypeResolver.ResolveDeclaration(ret.Fields[0], module);
        var list = Assert.IsType<XdlBoundedListShape>(shape);
        Assert.Equal(20, list.MaxLength);
        var enumShape = Assert.IsType<XdlEnumShape>(list.Element);
        Assert.Equal("remote_auth_type", enumShape.Definition.Name);
    }

    [Fact]
    public void RemoteConnectOpenArgs_NameField_IsOptionalBoundedString()
    {
        var module = BuildRemoteProtocolModule();
        var args = module.Structs["remote_connect_open_args"];
        var shape = XdlTypeResolver.ResolveDeclaration(args.Fields[0], module);
        var optional = Assert.IsType<XdlOptionalShape>(shape);
        Assert.IsType<XdlBoundedStringShape>(optional.Inner);
    }

    [Fact]
    public void EveryMvpArgsAndRetStruct_ResolvesEveryFieldWithoutThrowing()
    {
        // Broad sweep: every field of every MVP procedure's args/ret struct
        // must resolve to some shape. A throw here means the emitter (which
        // depends on exactly this) can't handle a real field yet.
        var module = BuildRemoteProtocolModule();
        string[] mvpProcedures =
        [
            "REMOTE_PROC_CONNECT_OPEN", "REMOTE_PROC_CONNECT_CLOSE",
            "REMOTE_PROC_CONNECT_GET_CAPABILITIES",
            "REMOTE_PROC_DOMAIN_GET_XML_DESC", "REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME",
            "REMOTE_PROC_DOMAIN_DESTROY", "REMOTE_PROC_DOMAIN_SHUTDOWN",
            "REMOTE_PROC_DOMAIN_CREATE", "REMOTE_PROC_DOMAIN_GET_INFO",
            "REMOTE_PROC_DOMAIN_GET_STATE", "REMOTE_PROC_AUTH_LIST",
            "REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS",
        ];

        foreach (var procName in mvpProcedures)
        {
            var proc = module.Procedures.Single(p => p.Name == procName);
            foreach (var structName in new[] { proc.ArgsStructName, proc.RetStructName })
            {
                if (structName is null)
                {
                    continue;
                }

                foreach (var field in module.Structs[structName].Fields)
                {
                    XdlTypeResolver.ResolveDeclaration(field, module);
                }
            }
        }
    }

    [Fact]
    public void RemoteError_EveryFieldResolves_IncludingOptionalNestedStructs()
    {
        // remote_error is every VIR_NET_ERROR reply's payload (plan.md story
        // 2) — not any procedure's own args/ret struct, so it isn't covered
        // by the sweep above. Its `dom`/`net` fields are optional structs
        // (pointer typedefs to remote_nonnull_domain/remote_nonnull_network)
        // reached this way for the first time in this project.
        var module = BuildRemoteProtocolModule();
        var error = module.Structs["remote_error"];

        var domShape = XdlTypeResolver.ResolveDeclaration(error.Fields.Single(f => f.Name == "dom"), module);
        var domOptional = Assert.IsType<XdlOptionalShape>(domShape);
        Assert.Equal("remote_nonnull_domain", Assert.IsType<XdlStructShape>(domOptional.Inner).Definition.Name);

        var netShape = XdlTypeResolver.ResolveDeclaration(error.Fields.Single(f => f.Name == "net"), module);
        var netOptional = Assert.IsType<XdlOptionalShape>(netShape);
        Assert.Equal("remote_nonnull_network", Assert.IsType<XdlStructShape>(netOptional.Inner).Definition.Name);

        foreach (var field in error.Fields)
        {
            XdlTypeResolver.ResolveDeclaration(field, module);
        }
    }
}
