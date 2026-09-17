using NetfxLibvirt.ProtocolGen.Ast;
using NetfxLibvirt.ProtocolGen.Parsing;

namespace NetfxLibvirt.ProtocolGen.Tests.Parsing;

/// <summary>
/// Parses the real, vendored upstream <c>.x</c> files
/// (<c>reference/upstream-x</c>, see that folder's README for exact source
/// commits) rather than synthetic snippets — this project's own validation
/// standard applied to grammar work: a hand-built fixture can't tell us the
/// parser actually handles what libvirt ships, only a real file can.
/// </summary>
public class RealProtocolFileTests
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
    private static string ReadUpstreamFile(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "reference", "upstream-x", name);
        if (!File.Exists(path))
        {
            Assert.Skip($"{name} hasn't been fetched — run reference/fetch-upstream-x.sh first.");
        }

        return File.ReadAllText(path);
    }

    [Fact]
    public void Parse_VirNetProtocolX_Succeeds()
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("virnetprotocol.x"));
        Assert.NotEmpty(spec.Definitions);
    }

    [Fact]
    public void Parse_VirNetProtocolX_ContainsVirNetMessageHeader()
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("virnetprotocol.x"));
        var header = spec.Definitions.OfType<XdlStructDefinition>().Single(s => s.Name == "virNetMessageHeader");
        Assert.Equal(["prog", "vers", "proc", "type", "serial", "status"], header.Fields.Select(f => f.Name));
    }

    [Fact]
    public void Parse_RemoteProtocolX_Succeeds()
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("remote_protocol.x"));
        Assert.NotEmpty(spec.Definitions);
    }

    [Fact]
    public void Parse_RemoteProtocolX_HasExpectedTopLevelDefinitionCounts()
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("remote_protocol.x"));

        // Most remote_* wire types are declared as structs directly; typedef is
        // only used for the handful of pointer ("optional-data") and bounded
        // string/array aliases. Counts confirmed against the real file, not
        // guessed — see reference/README.md for the exact source commit.
        Assert.True(spec.Definitions.OfType<XdlStructDefinition>().Count() > 100);
        Assert.True(spec.Definitions.OfType<XdlTypedefDefinition>().Count() >= 10);
        Assert.Contains(spec.Definitions, d => d is XdlEnumDefinition { Name: "remote_procedure" });
    }

    [Fact]
    public void Parse_RemoteProtocolX_RemoteProgramConst_MatchesKnownValue()
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("remote_protocol.x"));
        var remoteProgram = spec.Definitions.OfType<XdlConstDefinition>().Single(c => c.Name == "REMOTE_PROGRAM");
        Assert.Equal("0x20008086", remoteProgram.Value);

        var protocolVersion = spec.Definitions.OfType<XdlConstDefinition>().Single(c => c.Name == "REMOTE_PROTOCOL_VERSION");
        Assert.Equal("1", protocolVersion.Value);
    }

    // The virt-desktop parity procedure set from docs/plan.md, confirmed by
    // hand against the real remote_protocol.x when that doc was written —
    // re-verified here so a future upstream refresh (see
    // reference/README.md) can't silently drift one of these without a test
    // failure.
    [Theory]
    [InlineData("REMOTE_PROC_CONNECT_OPEN", "1")]
    [InlineData("REMOTE_PROC_CONNECT_CLOSE", "2")]
    [InlineData("REMOTE_PROC_CONNECT_GET_CAPABILITIES", "7")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_XML_DESC", "14")]
    [InlineData("REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME", "23")]
    [InlineData("REMOTE_PROC_DOMAIN_DESTROY", "12")]
    [InlineData("REMOTE_PROC_DOMAIN_SHUTDOWN", "33")]
    [InlineData("REMOTE_PROC_DOMAIN_CREATE", "9")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_INFO", "16")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_STATE", "212")]
    [InlineData("REMOTE_PROC_AUTH_LIST", "66")]
    [InlineData("REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS", "273")]
    public void Parse_RemoteProtocolX_MvpProcedure_HasExpectedNumber(string procName, string expectedValue)
    {
        var spec = XdlParser.Parse(ReadUpstreamFile("remote_protocol.x"));
        var procEnum = spec.Definitions.OfType<XdlEnumDefinition>().Single(e => e.Name == "remote_procedure");
        var value = procEnum.Values.Single(v => v.Name == procName);

        Assert.True(value.IsProcedure);
        Assert.Equal(expectedValue, value.Value);
    }
}
