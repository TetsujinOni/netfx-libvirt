using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Tests.Semantics;

public class XdlConstantTableTests
{
    [Fact]
    public void Resolve_DecimalLiteral_ParsesDirectly()
    {
        var table = new XdlConstantTable();
        Assert.Equal(4194304, table.Resolve("4194304"));
    }

    [Fact]
    public void Resolve_HexLiteral_ParsesDirectly()
    {
        var table = new XdlConstantTable();
        Assert.Equal(0x20008086, table.Resolve("0x20008086"));
    }

    [Fact]
    public void Resolve_KnownConst_ReturnsAddedValue()
    {
        var table = new XdlConstantTable();
        table.Add("REMOTE_STRING_MAX", "4194304");
        Assert.Equal(4194304, table.Resolve("REMOTE_STRING_MAX"));
    }

    [Fact]
    public void Resolve_WellKnownExternalConstant_ReturnsDocumentedValue()
    {
        // VIR_UUID_BUFLEN is never defined in either vendored .x file — it
        // comes from libvirt's public C header. See XdlConstantTable's doc.
        var table = new XdlConstantTable();
        Assert.Equal(16, table.Resolve("VIR_UUID_BUFLEN"));
    }

    [Fact]
    public void Resolve_UnknownIdentifier_Throws()
    {
        var table = new XdlConstantTable();
        Assert.Throws<InvalidOperationException>(() => table.Resolve("NOT_A_REAL_CONST"));
    }
}
