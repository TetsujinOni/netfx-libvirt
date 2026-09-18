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
/// Some procedures (e.g. <c>REMOTE_PROC_DOMAIN_OPEN_CONSOLE</c>,
/// <c>_SCREENSHOT</c>, <c>_MIGRATE_PREPARE_TUNNEL</c>) open a
/// <see cref="VirNetRpcStream"/> of subsequent <see cref="VirNetMessageType.Stream"/>
/// frames on the same serial, in addition to their ordinary reply — see
/// <see cref="OpenStreamAsync"/>. Contrary to an earlier version of this
/// class, the *reply itself* is always an ordinary <see cref="VirNetMessageStatus.Ok"/>
/// or <see cref="VirNetMessageStatus.Error"/>, never <c>Continue</c> —
/// confirmed against libvirt's real client dispatcher
/// (<c>virNetClientCallDispatchReply</c> vs. the structurally separate
/// <c>virNetClientCallDispatchStream</c> in <c>src/rpc/virnetclient.c</c>):
/// <c>Continue</c> is a status value that only ever appears on
/// <see cref="VirNetMessageType.Stream"/>-typed frames, matching
/// <c>virnetprotocol.x</c>'s own doc comment, which this class's own
/// earlier version misread. <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c>
/// specifically is *not* one of these — it's FD-passing (<c>SCM_RIGHTS</c>),
/// confirmed against the real <c>remoteDomainOpenGraphics</c> in
/// <c>remote_driver.c</c> and go-libvirt's own generated wrapper (neither
/// wires a stream reader/writer for it) — see <c>docs/plan.md</c> story 15's
/// follow-up for the full correction.
///
/// Only one call or stream may be in flight at a time on a given connection
/// (same "one call at a time" simplification as the rest of this class), so
/// <see cref="OpenStreamAsync"/> and <see cref="CallAsync"/> both throw if a
/// previously opened stream hasn't been disposed yet.
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
                _ => throw new InvalidOperationException(
                    $"Unexpected reply status {frame.Header.Status} — a {nameof(VirNetMessageStatus.Continue)} reply is not valid protocol (see the class doc); {nameof(VirNetMessageStatus.Continue)} only ever appears on {nameof(VirNetMessageType.Stream)}-typed frames."),
            };
        }
    }

    /// <summary>
    /// Like <see cref="CallAsync"/>, but for a procedure that opens a
    /// <see cref="VirNetRpcStream"/> of <see cref="VirNetMessageType.Stream"/>
    /// frames on this call's serial, in addition to its ordinary reply
    /// (e.g. <c>REMOTE_PROC_DOMAIN_OPEN_CONSOLE</c>, <c>_SCREENSHOT</c>,
    /// <c>_MIGRATE_PREPARE_TUNNEL</c> — see the class doc for why
    /// <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c> is *not* one of these).
    /// Returns both the reply's own payload (some of these procedures still
    /// carry real data there — e.g. <c>DOMAIN_SCREENSHOT</c>'s MIME type —
    /// decode it with the matching generated <c>*_ret</c> DTO, or discard it
    /// if the procedure has none) and the opened stream; the caller owns
    /// disposing the stream, which also releases this client for further
    /// calls (see the class doc).
    /// </summary>
    public async Task<VirNetRpcStreamResult> OpenStreamAsync(int procedure, ReadOnlyMemory<byte> argsPayload, CancellationToken cancellationToken = default)
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
                case VirNetMessageStatus.Ok:
                    _streamActive = true;
                    var stream = new VirNetRpcStream(this, _stream, callHeader.Prog, callHeader.Vers, procedure, serial);
                    return new VirNetRpcStreamResult(frame.Payload, stream);
                case VirNetMessageStatus.Error:
                    throw DecodeError(frame.Payload);
                default:
                    throw new InvalidOperationException(
                        $"Unexpected reply status {frame.Header.Status} for a stream-opening call.");
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
