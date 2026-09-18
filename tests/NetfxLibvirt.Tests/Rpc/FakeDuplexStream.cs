using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;

namespace NetfxLibvirt.Tests.Rpc;

/// <summary>A minimal duplex <see cref="Stream"/> for testing request/reply
/// RPC code: writes go to one buffer (inspectable via
/// <see cref="ReadAllSentFrames"/>), reads are served from a separately
/// pre-queued buffer (via <see cref="QueueReply"/>/<see cref="QueueMessage"/>)
/// — exactly the shape <see cref="VirNetRpcClient"/> needs (write a call,
/// then read frames until the matching reply turns up), without needing a
/// real socket pair to simulate it.</summary>
internal sealed class FakeDuplexStream : Stream
{
    private readonly MemoryStream _outgoing = new();
    private readonly MemoryStream _incoming = new();
    private long _incomingReadPosition;

    public void QueueReply(uint serial, VirNetMessageStatus status, byte[] payload)
    {
        var header = new VirNetMessageHeader(
            Prog: (uint)RemoteProtocolConstants.RemoteProgram,
            Vers: (uint)RemoteProtocolConstants.RemoteProtocolVersion,
            Proc: 0,
            Type: VirNetMessageType.Reply,
            Serial: serial,
            Status: status);
        QueueFrame(header, payload);
    }

    /// <summary>Queues a <see cref="VirNetMessageType.Stream"/> frame — the
    /// shape a call that opened a stream (<see cref="VirNetMessageStatus.Continue"/>
    /// reply) gets on subsequent reads, keyed to that same call's serial.</summary>
    public void QueueStream(uint serial, VirNetMessageStatus status, byte[] payload)
    {
        var header = new VirNetMessageHeader(
            Prog: (uint)RemoteProtocolConstants.RemoteProgram,
            Vers: (uint)RemoteProtocolConstants.RemoteProtocolVersion,
            Proc: 0,
            Type: VirNetMessageType.Stream,
            Serial: serial,
            Status: status);
        QueueFrame(header, payload);
    }

    /// <summary>Queues an unsolicited <see cref="VirNetMessageType.Message"/>
    /// frame — real events arrive this way, unprompted and not tied to any
    /// call's serial (hence <c>serial: 0</c>, matching a real server's own
    /// framing for these).</summary>
    public void QueueMessage(int procedure, byte[] payload)
    {
        var header = new VirNetMessageHeader(
            Prog: (uint)RemoteProtocolConstants.RemoteProgram,
            Vers: (uint)RemoteProtocolConstants.RemoteProtocolVersion,
            Proc: procedure,
            Type: VirNetMessageType.Message,
            Serial: 0,
            Status: VirNetMessageStatus.Ok);
        QueueFrame(header, payload);
    }

    private void QueueFrame(VirNetMessageHeader header, byte[] payload)
    {
        // Always append at the end; ReadAsync below restores its own read
        // cursor (_incomingReadPosition) before every read regardless of
        // wherever this leaves _incoming.Position.
        _incoming.Position = _incoming.Length;
        VirNetMessageFraming.WriteFrameAsync(_incoming, header, payload).GetAwaiter().GetResult();
    }

    public List<(VirNetMessageHeader Header, byte[] Payload)> ReadAllSentFrames()
    {
        _outgoing.Position = 0;
        var frames = new List<(VirNetMessageHeader, byte[])>();
        while (_outgoing.Position < _outgoing.Length)
        {
            var message = VirNetMessageFraming.ReadFrameAsync(_outgoing).GetAwaiter().GetResult();
            frames.Add((message.Header, message.Payload));
        }

        return frames;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _incoming.Position = _incomingReadPosition;
        var read = await _incoming.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _incomingReadPosition = _incoming.Position;
        return read;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _outgoing.Position = _outgoing.Length;
        await _outgoing.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("use ReadAsync");
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("use WriteAsync");
}
