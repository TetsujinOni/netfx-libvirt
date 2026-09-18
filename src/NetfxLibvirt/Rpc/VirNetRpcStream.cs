namespace NetfxLibvirt.Rpc;

/// <summary>
/// A libvirt RPC stream (<c>src/rpc/virnetprotocol.x</c>'s
/// <see cref="VirNetMessageType.Stream"/> message type), opened by
/// <see cref="VirNetRpcClient.OpenStreamAsync"/> when a call's reply carries
/// <see cref="VirNetMessageStatus.Continue"/> instead of an ordinary payload
/// — e.g. <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c>, which tunnels a domain's
/// raw VNC/SPICE graphics protocol bytes over the *existing* RPC connection
/// rather than a second socket.
///
/// Frame shape confirmed against <c>digitalocean/go-libvirt</c>'s own
/// <c>Socket.SendStream</c>/<c>processIncomingStream</c> (see
/// <c>reference/go-libvirt-src/socket.go</c> — <see cref="VirNetRpcClient"/>
/// only special-cases the *first* reply's <see cref="VirNetMessageStatus.Continue"/>;
/// everything after that, including a real, documented libvirtd quirk, lives
/// here):
///
/// - Outbound (<see cref="WriteAsync(ReadOnlyMemory{byte},CancellationToken)"/>):
///   each chunk is a <see cref="VirNetMessageType.Stream"/>/
///   <see cref="VirNetMessageStatus.Continue"/> frame carrying data, chunked
///   to <see cref="VirNetMessageFraming.MaxPayloadSize"/>. Finished by
///   <see cref="CompleteWritingAsync"/> (a Status=Ok frame with an empty
///   payload) or aborted by <see cref="AbortAsync"/> (Status=Error) — never
///   both; whichever runs first wins, matching go-libvirt's own
///   io.EOF-vs-abort-channel split in <c>SendStream</c>.
/// - Inbound (<see cref="ReadAsync(Memory{byte},CancellationToken)"/>): ends
///   on either a clean Status=Ok frame, **or** — a real, documented libvirtd
///   quirk go-libvirt's own comment calls out explicitly ("libvirtd breaks
///   protocol and returns StatusContinue with an empty response Payload when
///   the stream finishes") — a Status=Continue frame whose payload happens
///   to be empty. A Status=Error frame decodes the same <c>remote_error</c>
///   shape as an ordinary call error.
///
/// Disposing releases the owning <see cref="VirNetRpcClient"/> for further
/// calls (only one call or stream may be in flight on a connection at a
/// time — see that class's own doc). If neither <see cref="CompleteWritingAsync"/>
/// nor <see cref="AbortAsync"/> was called first, disposal completes
/// gracefully (Status=Ok) rather than aborting — ordinary teardown isn't
/// itself an error condition.
/// </summary>
public sealed class VirNetRpcStream : Stream
{
    private readonly VirNetRpcClient _owner;
    private readonly Stream _transport;
    private readonly uint _prog;
    private readonly uint _vers;
    private readonly int _proc;
    private readonly uint _serial;

    private byte[] _pending = [];
    private int _pendingOffset;
    private bool _readEnded;
    private bool _writeFinished;
    private bool _disposed;

    internal VirNetRpcStream(VirNetRpcClient owner, Stream transport, uint prog, uint vers, int proc, uint serial)
    {
        _owner = owner;
        _transport = transport;
        _prog = prog;
        _vers = vers;
        _proc = proc;
        _serial = serial;
    }

    public override bool CanRead => true;

    public override bool CanWrite => true;

    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (_pendingOffset >= _pending.Length && !_readEnded)
        {
            var frame = await VirNetMessageFraming.ReadFrameAsync(_transport, cancellationToken).ConfigureAwait(false);
            if (frame.Header.Serial != _serial)
            {
                _owner.RaiseUnsolicitedMessage(frame);
                continue;
            }

            switch (frame.Header.Status)
            {
                case VirNetMessageStatus.Ok:
                    _readEnded = true;
                    break;
                case VirNetMessageStatus.Error:
                    _readEnded = true;
                    throw VirNetRpcClient.DecodeError(frame.Payload);
                case VirNetMessageStatus.Continue when frame.Payload.Length == 0:
                    // The documented libvirtd quirk (see the class doc):
                    // end-of-stream signaled with Continue + an empty
                    // payload instead of a clean Ok.
                    _readEnded = true;
                    break;
                case VirNetMessageStatus.Continue:
                    _pending = frame.Payload;
                    _pendingOffset = 0;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected stream frame status {frame.Header.Status}.");
            }
        }

        if (_pendingOffset >= _pending.Length)
        {
            return 0;
        }

        var n = Math.Min(buffer.Length, _pending.Length - _pendingOffset);
        _pending.AsSpan(_pendingOffset, n).CopyTo(buffer.Span);
        _pendingOffset += n;
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeFinished)
        {
            throw new InvalidOperationException(
                $"This stream's write side was already finished by {nameof(CompleteWritingAsync)} or {nameof(AbortAsync)}.");
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var chunkLength = Math.Min(buffer.Length - offset, VirNetMessageFraming.MaxPayloadSize);
            var header = new VirNetMessageHeader(_prog, _vers, _proc, VirNetMessageType.Stream, _serial, VirNetMessageStatus.Continue);
            await VirNetMessageFraming.WriteFrameAsync(_transport, header, buffer.Slice(offset, chunkLength), cancellationToken).ConfigureAwait(false);
            offset += chunkLength;
        }
    }

    /// <summary>Cleanly ends the write direction: a Status=Ok frame with an empty payload, mirroring go-libvirt's own end-of-stream signal on <c>io.EOF</c>. Idempotent; a no-op if <see cref="AbortAsync"/> already ran.</summary>
    public async Task CompleteWritingAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeFinished)
        {
            return;
        }

        _writeFinished = true;
        var header = new VirNetMessageHeader(_prog, _vers, _proc, VirNetMessageType.Stream, _serial, VirNetMessageStatus.Ok);
        await VirNetMessageFraming.WriteFrameAsync(_transport, header, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Aborts the write direction: a Status=Error frame with an empty payload, for a real failure (not ordinary teardown — see the class doc for why plain disposal doesn't do this). Idempotent; a no-op if <see cref="CompleteWritingAsync"/> already ran.</summary>
    public async Task AbortAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeFinished)
        {
            return;
        }

        _writeFinished = true;
        var header = new VirNetMessageHeader(_prog, _vers, _proc, VirNetMessageType.Stream, _serial, VirNetMessageStatus.Error);
        await VirNetMessageFraming.WriteFrameAsync(_transport, header, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (!_writeFinished)
        {
            try
            {
                _writeFinished = true;
                var header = new VirNetMessageHeader(_prog, _vers, _proc, VirNetMessageType.Stream, _serial, VirNetMessageStatus.Ok);
                await VirNetMessageFraming.WriteFrameAsync(_transport, header, ReadOnlyMemory<byte>.Empty, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort: disposal must not throw even if this final frame fails to send.
            }
        }

        _owner.ReleaseStream();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }
}
