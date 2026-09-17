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
/// A real connection can deliver a <see cref="VirNetMessageType.Message"/>
/// (event notification) or <see cref="VirNetMessageType.Stream"/> frame
/// *unprompted*, interleaved with an in-flight call's reply — an earlier
/// version assumed the very next frame read after sending a call was always
/// that call's reply, which silently broke the moment anything else talked
/// on the connection. <see cref="CallAsync"/> now keeps reading frames
/// until one actually matches its own serial, raising anything else via
/// <see cref="UnsolicitedMessageReceived"/> instead of misinterpreting it.
///
/// Still cooperative, not a true independent background reader: nothing
/// reads the stream while no call is in flight, so an event that arrives
/// with no call currently awaiting a reply is invisible until the next
/// call happens to be made. That's an intentional, scoped-down version of
/// this fix — the real prerequisite for the two event procedures
/// docs/plan.md's Events story validates the architecture with — not a
/// full push-based event subscription model, which stays out of scope
/// until something beyond that validation slice actually needs it.
/// </summary>
public sealed class VirNetRpcClient
{
    private readonly Stream _stream;
    private uint _nextSerial = 1;

    public VirNetRpcClient(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Raised from within <see cref="CallAsync"/> for any frame
    /// read that isn't the reply it's waiting for —
    /// <see cref="VirNetMessageType.Message"/> (event notifications) and
    /// <see cref="VirNetMessageType.Stream"/> (stream data) in practice. No
    /// decoding happens here; the handler gets the raw
    /// <see cref="VirNetMessage"/>.</summary>
    public event Action<VirNetMessage>? UnsolicitedMessageReceived;

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

        while (true)
        {
            var frame = await VirNetMessageFraming.ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);

            if (frame.Header.Type is not (VirNetMessageType.Reply or VirNetMessageType.ReplyWithFds))
            {
                UnsolicitedMessageReceived?.Invoke(frame);
                continue;
            }

            if (frame.Header.Serial != serial)
            {
                // A reply for some other serial while we're waiting for ours
                // shouldn't happen given one call in flight at a time, but
                // it's not an event either — drop it, not raise it as
                // unsolicited, and keep waiting for our own reply.
                continue;
            }

            return frame.Header.Status switch
            {
                VirNetMessageStatus.Ok => frame.Payload,
                VirNetMessageStatus.Error => throw DecodeError(frame.Payload),
                VirNetMessageStatus.Continue => throw new NotSupportedException(
                    "a VIR_NET_CONTINUE reply means this call opened a stream — streaming isn't supported yet."),
                _ => throw new InvalidOperationException($"Unknown reply status {frame.Header.Status}."),
            };
        }
    }

    private static LibvirtRpcException DecodeError(byte[] payload) =>
        new(RemoteError.Decode(new XdrReader(payload)));
}
