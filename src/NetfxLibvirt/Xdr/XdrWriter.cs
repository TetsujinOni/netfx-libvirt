using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace NetfxLibvirt.Xdr;

/// <summary>
/// Encodes values into RFC 4506 External Data Representation (XDR) — the wire
/// encoding libvirt's RPC protocol uses for every message payload. All scalar
/// types are 4-byte-aligned, big-endian, and variable-length data (opaque
/// bytes, strings, arrays) is prefixed with a 4-byte unsigned length and
/// padded with zero bytes up to the next 4-byte boundary.
/// </summary>
public sealed class XdrWriter
{
    private readonly ArrayBufferWriter<byte> _buffer = new();

    public int Length => _buffer.WrittenCount;

    public void WriteInt(int value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteInt32BigEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteUInt(uint value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteUInt32BigEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteHyper(long value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteInt64BigEndian(span, value);
        _buffer.Advance(8);
    }

    public void WriteUHyper(ulong value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteUInt64BigEndian(span, value);
        _buffer.Advance(8);
    }

    public void WriteBool(bool value) => WriteInt(value ? 1 : 0);

    public void WriteEnum<TEnum>(TEnum value) where TEnum : struct, Enum
        => WriteInt(Convert.ToInt32(value));

    public void WriteFloat(float value)
    {
        var span = _buffer.GetSpan(4);
        BinaryPrimitives.WriteSingleBigEndian(span, value);
        _buffer.Advance(4);
    }

    public void WriteDouble(double value)
    {
        var span = _buffer.GetSpan(8);
        BinaryPrimitives.WriteDoubleBigEndian(span, value);
        _buffer.Advance(8);
    }

    /// <summary>Writes fixed-length opaque data: raw bytes padded to a 4-byte boundary, no length prefix.</summary>
    public void WriteFixedOpaque(ReadOnlySpan<byte> data)
    {
        _buffer.Write(data);
        WritePadding(data.Length);
    }

    /// <summary>Writes variable-length opaque data: a 4-byte length prefix, the bytes, then padding.</summary>
    public void WriteVarOpaque(ReadOnlySpan<byte> data)
    {
        WriteUInt(checked((uint)data.Length));
        WriteFixedOpaque(data);
    }

    /// <summary>Writes a string as variable-length opaque UTF-8 bytes (XDR has no native string type distinct from opaque data).</summary>
    public void WriteString(string value) => WriteVarOpaque(Encoding.UTF8.GetBytes(value));

    /// <summary>Writes a variable-length array: a 4-byte count prefix followed by each element.</summary>
    public void WriteArray<T>(IReadOnlyList<T> items, Action<XdrWriter, T> writeElement)
    {
        WriteUInt(checked((uint)items.Count));
        WriteFixedArray(items, writeElement);
    }

    /// <summary>Writes a fixed-length array: each element in sequence, no count prefix.</summary>
    public void WriteFixedArray<T>(IReadOnlyList<T> items, Action<XdrWriter, T> writeElement)
    {
        foreach (var item in items)
        {
            writeElement(this, item);
        }
    }

    /// <summary>Writes XDR "optional-data": a bool discriminant, followed by the value only when present.</summary>
    public void WriteOptional<T>(T? value, Action<XdrWriter, T> writeValue) where T : notnull
    {
        WriteBool(value is not null);
        if (value is not null)
        {
            writeValue(this, value);
        }
    }

    private void WritePadding(int dataLength)
    {
        var pad = (4 - (dataLength % 4)) % 4;
        if (pad == 0)
        {
            return;
        }

        var span = _buffer.GetSpan(pad);
        span[..pad].Clear();
        _buffer.Advance(pad);
    }

    public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

    public ReadOnlySpan<byte> WrittenSpan => _buffer.WrittenSpan;
}
