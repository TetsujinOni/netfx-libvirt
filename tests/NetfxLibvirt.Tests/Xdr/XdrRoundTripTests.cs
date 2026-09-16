using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Xdr;

/// <summary>Round-trips every primitive writer/reader pair to prove they agree on wire format independent of the byte-literal tests.</summary>
public class XdrRoundTripTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Int_RoundTrips(int value)
    {
        var writer = new XdrWriter();
        writer.WriteInt(value);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(value, reader.ReadInt());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void UInt_RoundTrips(uint value)
    {
        var writer = new XdrWriter();
        writer.WriteUInt(value);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(value, reader.ReadUInt());
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    [InlineData(0L)]
    public void Hyper_RoundTrips(long value)
    {
        var writer = new XdrWriter();
        writer.WriteHyper(value);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(value, reader.ReadHyper());
    }

    [Fact]
    public void UHyper_RoundTrips()
    {
        var writer = new XdrWriter();
        writer.WriteUHyper(ulong.MaxValue);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(ulong.MaxValue, reader.ReadUHyper());
    }

    [Theory]
    [InlineData(1.5f)]
    [InlineData(-0.0f)]
    [InlineData(float.MaxValue)]
    public void Float_RoundTrips(float value)
    {
        var writer = new XdrWriter();
        writer.WriteFloat(value);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(value, reader.ReadFloat());
    }

    [Fact]
    public void Double_RoundTrips()
    {
        var writer = new XdrWriter();
        writer.WriteDouble(Math.PI);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(Math.PI, reader.ReadDouble());
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("libvirt rpc")]
    [InlineData("unicode: 日本語")]
    public void String_RoundTrips(string value)
    {
        var writer = new XdrWriter();
        writer.WriteString(value);
        var reader = new XdrReader(writer.ToArray());
        Assert.Equal(value, reader.ReadString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void VarOpaque_RoundTripsAcrossAllPaddingRemainders(int length)
    {
        var data = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();

        var writer = new XdrWriter();
        writer.WriteVarOpaque(data);
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(data, reader.ReadVarOpaque());
        Assert.Equal(0, reader.Remaining);
        Assert.Equal(0, writer.Length % 4);
    }

    [Fact]
    public void Array_RoundTripsNestedStrings()
    {
        var items = new List<string> { "one", "two", "three" };

        var writer = new XdrWriter();
        writer.WriteArray(items, (w, v) => w.WriteString(v));
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(items, reader.ReadArray(r => r.ReadString()));
    }

    [Fact]
    public void Optional_RoundTripsPresentAndAbsent()
    {
        var writer = new XdrWriter();
        writer.WriteOptional<string>("present", (w, v) => w.WriteString(v));
        writer.WriteOptional<string>(null, (w, v) => w.WriteString(v));

        var reader = new XdrReader(writer.ToArray());
        Assert.Equal("present", reader.ReadOptional(r => r.ReadString()));
        Assert.Null(reader.ReadOptional(r => r.ReadString()));
    }

    [Fact]
    public void EverythingIsFourByteAligned()
    {
        var writer = new XdrWriter();
        writer.WriteBool(true);
        writer.WriteString("x");
        writer.WriteVarOpaque([1, 2, 3, 4, 5]);
        writer.WriteFixedOpaque([1, 2]);

        Assert.Equal(0, writer.Length % 4);
    }
}
