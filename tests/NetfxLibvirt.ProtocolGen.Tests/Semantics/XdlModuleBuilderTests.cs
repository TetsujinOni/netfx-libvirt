using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Tests.Semantics;

public class XdlModuleBuilderTests
{
    private static XdlModule BuildModule(string source) => XdlModuleBuilder.Build(XdlParser.Parse(source));

    [Fact]
    public void Build_IndexesStructsEnumsUnionsTypedefsByName()
    {
        const string source = """
            const FOO = 1;
            typedef int bar;
            struct baz { int x; };
            enum qux { A = 0 };
            union quux switch (int t) { case 0: int x; };
            """;
        var module = BuildModule(source);

        Assert.Equal(1, module.Constants.Resolve("FOO"));
        Assert.True(module.Typedefs.ContainsKey("bar"));
        Assert.True(module.Structs.ContainsKey("baz"));
        Assert.True(module.Enums.ContainsKey("qux"));
        Assert.True(module.Unions.ContainsKey("quux"));
    }

    [Fact]
    public void Build_ExtractsProcedure_WithBothArgsAndRetStructs()
    {
        const string source = """
            struct remote_thing_open_args { int flags; };
            struct remote_thing_open_ret { int handle; };
            enum remote_procedure {
                REMOTE_PROC_THING_OPEN = 1
            };
            """;
        var module = BuildModule(source);
        var proc = Assert.Single(module.Procedures);
        Assert.Equal("REMOTE_PROC_THING_OPEN", proc.Name);
        Assert.Equal(1, proc.Number);
        Assert.Equal("remote_thing_open_args", proc.ArgsStructName);
        Assert.Equal("remote_thing_open_ret", proc.RetStructName);
    }

    [Fact]
    public void Build_ExtractsProcedure_WithNeitherArgsNorRetStruct_LeavesBothNull()
    {
        const string source = """
            enum remote_procedure {
                REMOTE_PROC_THING_PING = 1
            };
            """;
        var module = BuildModule(source);
        var proc = Assert.Single(module.Procedures);
        Assert.Null(proc.ArgsStructName);
        Assert.Null(proc.RetStructName);
    }

    [Fact]
    public void Build_NonProcedureEnumValues_AreNotExtractedAsProcedures()
    {
        const string source = """
            enum remote_domain_state {
                REMOTE_DOMAIN_NOSTATE = 0,
                REMOTE_DOMAIN_RUNNING = 1
            };
            """;
        var module = BuildModule(source);
        Assert.Empty(module.Procedures);
    }
}
