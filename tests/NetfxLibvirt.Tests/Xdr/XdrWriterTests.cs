using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Xdr;

public class XdrWriterTests
{
    [Fact]
    public void WriteInt_EncodesBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteInt(0x01020304);

        Assert.Equal([0x01, 0x02, 0x03, 0x04], writer.ToArray());
    }

    [Fact]
    public void WriteInt_NegativeValue_EncodesTwosComplement()
    {
        var writer = new XdrWriter();
        writer.WriteInt(-1);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], writer.ToArray());
    }

    [Fact]
    public void WriteUInt_EncodesBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteUInt(0xFFFFFFFF);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], writer.ToArray());
    }

    [Fact]
    public void WriteHyper_Encodes8BytesBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteHyper(0x0102030405060708);

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08], writer.ToArray());
    }

    [Fact]
    public void WriteUHyper_Encodes8BytesBigEndian()
    {
        var writer = new XdrWriter();
        writer.WriteUHyper(0xFFFFFFFFFFFFFFFF);

        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF], writer.ToArray());
    }

    [Theory]
    [InlineData(true, 0x00000001u)]
    [InlineData(false, 0x00000000u)]
    public void WriteBool_EncodesAsInt(bool value, uint expected)
    {
        var writer = new XdrWriter();
        writer.WriteBool(value);

        var bytes = writer.ToArray();
        var decoded = (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        Assert.Equal(expected, decoded);
    }

    private enum SampleEnum
    {
        Zero = 0,
        Five = 5,
    }

    [Fact]
    public void WriteEnum_EncodesUnderlyingValueAsInt()
    {
        var writer = new XdrWriter();
        writer.WriteEnum(SampleEnum.Five);

        Assert.Equal([0x00, 0x00, 0x00, 0x05], writer.ToArray());
    }

    [Fact]
    public void WriteFixedOpaque_PadsToFourByteBoundary()
    {
        var writer = new XdrWriter();
        writer.WriteFixedOpaque([0x01, 0x02, 0x03]);

        Assert.Equal([0x01, 0x02, 0x03, 0x00], writer.ToArray());
    }

    [Fact]
    public void WriteFixedOpaque_ExactMultipleOfFour_NoPadding()
    {
        var writer = new XdrWriter();
        writer.WriteFixedOpaque([0x01, 0x02, 0x03, 0x04]);

        Assert.Equal([0x01, 0x02, 0x03, 0x04], writer.ToArray());
    }

    [Fact]
    public void WriteVarOpaque_WritesLengthPrefixThenPaddedData()
    {
        var writer = new XdrWriter();
        writer.WriteVarOpaque([0xAA, 0xBB, 0xCC]);

        Assert.Equal([0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC, 0x00], writer.ToArray());
    }

    [Fact]
    public void WriteString_EncodesUtf8AsVarOpaque()
    {
        var writer = new XdrWriter();
        writer.WriteString("hi");

        // length=2, "hi" (0x68 0x69), padded with 2 zero bytes.
        Assert.Equal([0x00, 0x00, 0x00, 0x02, 0x68, 0x69, 0x00, 0x00], writer.ToArray());
    }

    [Fact]
    public void WriteArray_WritesCountThenElements()
    {
        var writer = new XdrWriter();
        writer.WriteArray(new List<int> { 1, 2, 3 }, (w, v) => w.WriteInt(v));

        Assert.Equal(
        [
            0x00, 0x00, 0x00, 0x03,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x03,
        ], writer.ToArray());
    }

    [Fact]
    public void WriteFixedArray_WritesElementsWithoutCount()
    {
        var writer = new XdrWriter();
        writer.WriteFixedArray(new List<int> { 7, 8 }, (w, v) => w.WriteInt(v));

        Assert.Equal([0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x08], writer.ToArray());
    }

    [Fact]
    public void WriteOptional_Present_WritesTrueThenValue()
    {
        var writer = new XdrWriter();
        writer.WriteOptional<string>("x", (w, v) => w.WriteString(v));

        Assert.Equal([0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, (byte)'x', 0x00, 0x00, 0x00], writer.ToArray());
    }

    [Fact]
    public void WriteOptional_Absent_WritesFalseOnly()
    {
        var writer = new XdrWriter();
        writer.WriteOptional<string>(null, (w, v) => w.WriteString(v));

        Assert.Equal([0x00, 0x00, 0x00, 0x00], writer.ToArray());
    }
}
