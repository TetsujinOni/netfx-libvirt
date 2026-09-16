using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Tests.Xdr;

public class XdrReaderTests
{
    [Fact]
    public void ReadInt_DecodesBigEndian()
    {
        var reader = new XdrReader(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(0x01020304, reader.ReadInt());
    }

    [Fact]
    public void ReadInt_NegativeValue_DecodesTwosComplement()
    {
        var reader = new XdrReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(-1, reader.ReadInt());
    }

    [Fact]
    public void ReadUInt_DecodesBigEndian()
    {
        var reader = new XdrReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(0xFFFFFFFFu, reader.ReadUInt());
    }

    [Fact]
    public void ReadHyper_Decodes8BytesBigEndian()
    {
        var reader = new XdrReader(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 });
        Assert.Equal(0x0102030405060708, reader.ReadHyper());
    }

    [Fact]
    public void ReadUHyper_Decodes8BytesBigEndian()
    {
        var reader = new XdrReader(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(0xFFFFFFFFFFFFFFFFu, reader.ReadUHyper());
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0, 0, 0, 1 }, true)]
    public void ReadBool_DecodesZeroOrOne(byte[] data, bool expected)
    {
        var reader = new XdrReader(data);
        Assert.Equal(expected, reader.ReadBool());
    }

    [Fact]
    public void ReadBool_InvalidValue_Throws()
    {
        var reader = new XdrReader(new byte[] { 0, 0, 0, 2 });
        Assert.Throws<XdrException>(() => reader.ReadBool());
    }

    private enum SampleEnum
    {
        Zero = 0,
        Five = 5,
    }

    [Fact]
    public void ReadEnum_DecodesUnderlyingInt()
    {
        var reader = new XdrReader(new byte[] { 0, 0, 0, 5 });
        Assert.Equal(SampleEnum.Five, reader.ReadEnum<SampleEnum>());
    }

    [Fact]
    public void ReadFixedOpaque_SkipsPadding()
    {
        // 3 data bytes + 1 pad byte, followed by a marker int to prove padding was skipped.
        var reader = new XdrReader(new byte[] { 0x01, 0x02, 0x03, 0x00, 0x00, 0x00, 0x00, 0x2A });
        var data = reader.ReadFixedOpaque(3);

        Assert.Equal([0x01, 0x02, 0x03], data);
        Assert.Equal(42, reader.ReadInt());
    }

    [Fact]
    public void ReadVarOpaque_ReadsLengthPrefixedPaddedData()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x03, 0xAA, 0xBB, 0xCC, 0x00 });
        Assert.Equal([0xAA, 0xBB, 0xCC], reader.ReadVarOpaque());
    }

    [Fact]
    public void ReadVarOpaque_ExceedingMaxLength_Throws()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x05, 1, 2, 3, 4, 5, 0, 0, 0 });
        Assert.Throws<XdrException>(() => reader.ReadVarOpaque(maxLength: 4));
    }

    [Fact]
    public void ReadVarOpaque_LengthExceedsRemainingBytes_Throws()
    {
        // Declares a length of 100 but supplies far fewer bytes - must not allocate wildly or read out of bounds.
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x64, 1, 2, 3, 4 });
        Assert.Throws<XdrException>(() => reader.ReadVarOpaque());
    }

    [Fact]
    public void ReadString_DecodesUtf8()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x02, (byte)'h', (byte)'i', 0x00, 0x00 });
        Assert.Equal("hi", reader.ReadString());
    }

    [Fact]
    public void ReadArray_ReadsCountThenElements()
    {
        var reader = new XdrReader(new byte[]
        {
            0x00, 0x00, 0x00, 0x03,
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x03,
        });

        var items = reader.ReadArray(r => r.ReadInt());
        Assert.Equal([1, 2, 3], items);
    }

    [Fact]
    public void ReadArray_ExceedingMaxCount_Throws()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x0A });
        Assert.Throws<XdrException>(() => reader.ReadArray(r => r.ReadInt(), maxCount: 2));
    }

    [Fact]
    public void ReadFixedArray_ReadsExactCountWithoutPrefix()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0x08 });
        var items = reader.ReadFixedArray(2, r => r.ReadInt());
        Assert.Equal([7, 8], items);
    }

    [Fact]
    public void ReadOptional_Present_ReadsTrueThenValue()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, (byte)'x', 0x00, 0x00, 0x00 });
        var value = reader.ReadOptional(r => r.ReadString());
        Assert.Equal("x", value);
    }

    [Fact]
    public void ReadOptional_Absent_ReturnsDefault()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x00, 0x00, 0x00 });
        var value = reader.ReadOptional(r => r.ReadString());
        Assert.Null(value);
    }

    [Fact]
    public void ReadInt_TruncatedInput_Throws()
    {
        var reader = new XdrReader(new byte[] { 0x00, 0x01 });
        Assert.Throws<XdrException>(() => reader.ReadInt());
    }

    [Fact]
    public void Remaining_TracksPositionAcrossReads()
    {
        var reader = new XdrReader(new byte[8]);
        Assert.Equal(8, reader.Remaining);
        reader.ReadInt();
        Assert.Equal(4, reader.Remaining);
        reader.ReadInt();
        Assert.Equal(0, reader.Remaining);
    }
}
