using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt.Rpc;

/// <summary>
/// Sends one libvirt RPC call at a time over an already-connected transport
/// <see cref="Stream"/> and correlates the reply by serial number. Decodes
/// a <see cref="VirNetMessageStatus.Error"/> reply into a
/// <see cref="LibvirtRpcException"/> carrying the real <c>remote_error</c>
/// payload, rather than handing back a bare status code.
///
/// Half-duplex by design for now: a call is sent, then exactly one frame is
/// read back and assumed to be its reply. Event/stream messages
/// (<see cref="VirNetMessageType.Message"/>/<see cref="VirNetMessageType.Stream"/>,
/// which arrive unprompted between calls on a real connection) aren't
/// handled yet — see docs/plan.md's "After parity" section. This is enough
/// for the request/reply procedures the parity milestone needs.
/// </summary>
public sealed class VirNetRpcClient
{
    private readonly Stream _stream;
    private uint _nextSerial = 1;

    public VirNetRpcClient(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Sends <paramref name="procedure"/> with the given already-XDR-encoded
    /// argument payload (empty for a no-args procedure), and returns the
    /// reply's raw payload bytes for the caller to decode with the matching
    /// generated <c>*_ret</c> DTO (or discard, for a procedure with no
    /// return payload, e.g. <see cref="RemoteProcedure.RemoteProcConnectClose"/>).
    /// </summary>
    public async Task<byte[]> CallAsync(int procedure, ReadOnlyMemory<byte> argsPayload, CancellationToken cancellationToken = default)
    {
        var serial = _nextSerial++;
        var callHeader = new VirNetMessageHeader(
            Prog: (uint)RemoteProtocolConstants.RemoteProgram,
            Vers: (uint)RemoteProtocolConstants.RemoteProtocolVersion,
            Proc: procedure,
            Type: VirNetMessageType.Call,
            Serial: serial,
            Status: VirNetMessageStatus.Ok);

        await VirNetMessageFraming.WriteFrameAsync(_stream, callHeader, argsPayload, cancellationToken).ConfigureAwait(false);

        var reply = await VirNetMessageFraming.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);

        if (reply.Header.Serial != serial)
        {
            throw new InvalidOperationException(
                $"Reply serial {reply.Header.Serial} does not match call serial {serial} — out-of-order or interleaved messages aren't supported yet.");
        }

        return reply.Header.Status switch
        {
            VirNetMessageStatus.Ok => reply.Payload,
            VirNetMessageStatus.Error => throw DecodeError(reply.Payload),
            VirNetMessageStatus.Continue => throw new NotSupportedException(
                "a VIR_NET_CONTINUE reply means this call opened a stream — streaming isn't supported yet."),
            _ => throw new InvalidOperationException($"Unknown reply status {reply.Header.Status}."),
        };
    }

    private static LibvirtRpcException DecodeError(byte[] payload) =>
        new(RemoteError.Decode(new XdrReader(payload)));
}
