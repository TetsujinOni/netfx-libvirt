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
///
/// A call whose reply carries <see cref="VirNetMessageStatus.Continue"/>
/// (e.g. <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c>) opens a
/// <see cref="VirNetRpcStream"/> instead of returning a plain payload — see
/// <see cref="OpenStreamAsync"/>. Only one call or stream may be in flight
/// at a time on a given connection (same "one call at a time" simplification
/// as the rest of this class), so <see cref="OpenStreamAsync"/> and
/// <see cref="CallAsync"/> both throw if a previously opened stream hasn't
/// been disposed yet.
/// </summary>
public sealed class VirNetRpcClient
{
    private readonly Stream _stream;
    private uint _nextSerial = 1;
    private bool _streamActive;

    public VirNetRpcClient(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Raised from within <see cref="CallAsync"/> for any frame
    /// read that isn't the reply it's waiting for —
    /// <see cref="VirNetMessageType.Message"/> (event notifications) and
    /// <see cref="VirNetMessageType.Stream"/> (stream data) in practice. No
    /// decoding happens here; the handler gets the raw
    /// <see cref="VirNetMessage"/>.
    ///
    /// Carries no correlation to which logical operation "owns" it beyond
    /// happening to arrive while some <see cref="CallAsync"/> was in
    /// flight — a future subscriber (docs/plan.md's Events story) must
    /// validate a decoded event's own contents (e.g. its callback/stream
    /// ID) against what it actually registered for, not assume it's
    /// scoped to whichever call's read loop happened to observe it.
    /// Flagged during story 13's security review as a design note for
    /// story 14, not a live issue — nothing subscribes to this yet.</summary>
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
        if (_streamActive)
        {
            throw new InvalidOperationException(
                $"A {nameof(VirNetRpcStream)} opened by {nameof(OpenStreamAsync)} is still open on this connection; dispose it before making another call.");
        }

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
                    $"a VIR_NET_CONTINUE reply means this call opened a stream — use {nameof(OpenStreamAsync)} instead of {nameof(CallAsync)} for stream-opening procedures like REMOTE_PROC_DOMAIN_OPEN_GRAPHICS."),
                _ => throw new InvalidOperationException($"Unknown reply status {frame.Header.Status}."),
            };
        }
    }

    /// <summary>
    /// Like <see cref="CallAsync"/>, but for a procedure whose successful
    /// reply is a <see cref="VirNetMessageStatus.Continue"/> status rather
    /// than an ordinary payload — libvirt's signal that the call opened a
    /// <see cref="VirNetMessageType.Stream"/> instead of returning data
    /// directly (e.g. <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c>, which tunnels
    /// a domain's raw VNC/SPICE graphics bytes over this same RPC
    /// connection). Returns the opened <see cref="VirNetRpcStream"/>; the
    /// caller owns disposing it, which also releases this client for
    /// further calls (see the class doc).
    /// </summary>
    public async Task<VirNetRpcStream> OpenStreamAsync(int procedure, ReadOnlyMemory<byte> argsPayload, CancellationToken cancellationToken = default)
    {
        if (_streamActive)
        {
            throw new InvalidOperationException(
                $"A {nameof(VirNetRpcStream)} opened by a previous {nameof(OpenStreamAsync)} call is still open on this connection; dispose it before opening another.");
        }

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
                continue;
            }

            switch (frame.Header.Status)
            {
                case VirNetMessageStatus.Continue:
                    _streamActive = true;
                    return new VirNetRpcStream(this, _stream, callHeader.Prog, callHeader.Vers, procedure, serial);
                case VirNetMessageStatus.Error:
                    throw DecodeError(frame.Payload);
                default:
                    throw new InvalidOperationException(
                        $"Expected a {nameof(VirNetMessageStatus.Continue)} reply (this call should open a stream), but got {frame.Header.Status}.");
            }
        }
    }

    /// <summary>Used by <see cref="VirNetRpcStream"/> to forward a frame that arrived for a different serial while it was reading — same demultiplexing this client's own <see cref="CallAsync"/> does.</summary>
    internal void RaiseUnsolicitedMessage(VirNetMessage message) => UnsolicitedMessageReceived?.Invoke(message);

    /// <summary>Called by <see cref="VirNetRpcStream.DisposeAsync"/> to release this client for further <see cref="CallAsync"/>/<see cref="OpenStreamAsync"/> calls.</summary>
    internal void ReleaseStream() => _streamActive = false;

    internal static LibvirtRpcException DecodeError(byte[] payload) =>
        new(RemoteError.Decode(new XdrReader(payload)));
}
