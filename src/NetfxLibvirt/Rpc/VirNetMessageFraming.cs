using System.Buffers.Binary;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Rpc;

/// <summary>A decoded on-the-wire libvirt RPC message: header plus its still-encoded XDR payload.</summary>
public readonly record struct VirNetMessage(VirNetMessageHeader Header, byte[] Payload);

/// <summary>
/// Reads and writes libvirt's RPC framing over any transport
/// <see cref="Stream"/> (Unix socket, TCP, TLS, or an SSH channel stream —
/// all four transports share this exact framing).
///
/// Wire format per <c>src/rpc/virnetprotocol.x</c>: a 4-byte big-endian
/// length prefix — the total byte count of the length field itself, the
/// header, and the payload combined — followed by the 24-byte
/// <see cref="VirNetMessageHeader"/>, followed by the payload.
/// </summary>
public static class VirNetMessageFraming
{
    /// <summary>Size of the length-prefix field itself (<c>VIR_NET_MESSAGE_LEN_MAX</c>).</summary>
    public const int LengthPrefixSize = 4;

    /// <summary>Maximum combined size of header + payload, excluding the length prefix (<c>VIR_NET_MESSAGE_MAX</c>).</summary>
    public const int MaxHeaderAndPayloadSize = 33_554_432;

    /// <summary>Maximum payload size (<c>VIR_NET_MESSAGE_PAYLOAD_MAX</c>): <see cref="MaxHeaderAndPayloadSize"/> minus the header.</summary>
    public const int MaxPayloadSize = MaxHeaderAndPayloadSize - VirNetMessageHeader.WireSize;

    /// <summary>Encodes a full frame (length prefix + header + payload) into a single buffer.</summary>
    public static byte[] EncodeFrame(VirNetMessageHeader header, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadSize)
        {
            throw new ArgumentOutOfRangeException(nameof(payload), payload.Length,
                $"Payload exceeds the maximum of {MaxPayloadSize} bytes.");
        }

        var totalLength = LengthPrefixSize + VirNetMessageHeader.WireSize + payload.Length;
        var buffer = new byte[totalLength];

        BinaryPrimitives.WriteUInt32BigEndian(buffer, (uint)totalLength);

        var headerWriter = new XdrWriter();
        header.Encode(headerWriter);
        headerWriter.WrittenSpan.CopyTo(buffer.AsSpan(LengthPrefixSize));

        payload.CopyTo(buffer.AsSpan(LengthPrefixSize + VirNetMessageHeader.WireSize));

        return buffer;
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        VirNetMessageHeader header,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        var frame = EncodeFrame(header, payload.Span);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one full frame from <paramref name="stream"/>: the length
    /// prefix, then exactly that many further bytes (header + payload).
    /// </summary>
    public static async Task<VirNetMessage> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var lengthPrefix = new byte[LengthPrefixSize];
        await stream.ReadExactlyAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
        var totalLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefix);

        if (totalLength < LengthPrefixSize + VirNetMessageHeader.WireSize)
        {
            throw new InvalidOperationException(
                $"Frame length {totalLength} is smaller than the length prefix plus header ({LengthPrefixSize + VirNetMessageHeader.WireSize} bytes).");
        }

        if (totalLength > (uint)(LengthPrefixSize + MaxHeaderAndPayloadSize))
        {
            throw new InvalidOperationException(
                $"Frame length {totalLength} exceeds the maximum of {LengthPrefixSize + MaxHeaderAndPayloadSize} bytes.");
        }

        var rest = new byte[totalLength - LengthPrefixSize];
        await stream.ReadExactlyAsync(rest, cancellationToken).ConfigureAwait(false);

        var reader = new XdrReader(rest.AsMemory(0, VirNetMessageHeader.WireSize));
        var header = VirNetMessageHeader.Decode(reader);
        var payload = rest.AsMemory(VirNetMessageHeader.WireSize).ToArray();

        return new VirNetMessage(header, payload);
    }
}
