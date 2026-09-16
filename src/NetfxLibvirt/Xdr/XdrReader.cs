using System.Buffers.Binary;
using System.Text;

namespace NetfxLibvirt.Xdr;

/// <summary>
/// Decodes values from RFC 4506 External Data Representation (XDR).
/// Mirrors <see cref="XdrWriter"/>: 4-byte-aligned big-endian scalars,
/// length-prefixed variable data padded to a 4-byte boundary.
/// </summary>
public sealed class XdrReader
{
    private readonly ReadOnlyMemory<byte> _buffer;
    private int _position;

    public XdrReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
    }

    public int Position => _position;

    public int Remaining => _buffer.Length - _position;

    public int ReadInt() => unchecked((int)ReadRawUInt32());

    public uint ReadUInt() => ReadRawUInt32();

    public long ReadHyper()
    {
        RequireRemaining(8);
        var value = BinaryPrimitives.ReadInt64BigEndian(_buffer.Span.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public ulong ReadUHyper()
    {
        RequireRemaining(8);
        var value = BinaryPrimitives.ReadUInt64BigEndian(_buffer.Span.Slice(_position, 8));
        _position += 8;
        return value;
    }

    public bool ReadBool()
    {
        var value = ReadInt();
        return value switch
        {
            0 => false,
            1 => true,
            _ => throw new XdrException($"Invalid XDR boolean value {value}; expected 0 or 1."),
        };
    }

    public TEnum ReadEnum<TEnum>() where TEnum : struct, Enum => (TEnum)(object)ReadInt();

    public float ReadFloat()
    {
        RequireRemaining(4);
        var value = BinaryPrimitives.ReadSingleBigEndian(_buffer.Span.Slice(_position, 4));
        _position += 4;
        return value;
    }

    public double ReadDouble()
    {
        RequireRemaining(8);
        var value = BinaryPrimitives.ReadDoubleBigEndian(_buffer.Span.Slice(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>Reads fixed-length opaque data: raw bytes, then skips padding to a 4-byte boundary.</summary>
    public byte[] ReadFixedOpaque(int length)
    {
        RequireRemaining(length);
        var data = _buffer.Span.Slice(_position, length).ToArray();
        _position += length;
        SkipPadding(length);
        return data;
    }

    /// <summary>Reads variable-length opaque data: a 4-byte length prefix, the bytes, then padding.</summary>
    public byte[] ReadVarOpaque(int maxLength = int.MaxValue)
    {
        var length = ReadUInt();
        if (length > maxLength)
        {
            throw new XdrException($"XDR opaque length {length} exceeds maximum of {maxLength}.");
        }

        if (length > (uint)Remaining)
        {
            throw new XdrException($"XDR opaque length {length} exceeds {Remaining} remaining bytes.");
        }

        return ReadFixedOpaque((int)length);
    }

    /// <summary>Reads a string as variable-length opaque UTF-8 bytes.</summary>
    public string ReadString(int maxLength = int.MaxValue) => Encoding.UTF8.GetString(ReadVarOpaque(maxLength));

    /// <summary>Reads a variable-length array: a 4-byte count prefix followed by each element.</summary>
    public List<T> ReadArray<T>(Func<XdrReader, T> readElement, int maxCount = int.MaxValue)
    {
        var count = ReadUInt();
        if (count > maxCount)
        {
            throw new XdrException($"XDR array count {count} exceeds maximum of {maxCount}.");
        }

        return ReadFixedArray((int)count, readElement);
    }

    /// <summary>Reads a fixed-length array: <paramref name="count"/> elements in sequence, no count prefix.</summary>
    public List<T> ReadFixedArray<T>(int count, Func<XdrReader, T> readElement)
    {
        var items = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(readElement(this));
        }

        return items;
    }

    /// <summary>Reads XDR "optional-data": a bool discriminant, followed by the value only when present.</summary>
    public T? ReadOptional<T>(Func<XdrReader, T> readValue) where T : notnull
        => ReadBool() ? readValue(this) : default;

    private uint ReadRawUInt32()
    {
        RequireRemaining(4);
        var value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Span.Slice(_position, 4));
        _position += 4;
        return value;
    }

    private void SkipPadding(int dataLength)
    {
        var pad = (4 - (dataLength % 4)) % 4;
        if (pad == 0)
        {
            return;
        }

        RequireRemaining(pad);
        _position += pad;
    }

    private void RequireRemaining(int count)
    {
        if (count > Remaining)
        {
            throw new XdrException($"Unexpected end of XDR data: need {count} bytes, {Remaining} remaining.");
        }
    }
}
