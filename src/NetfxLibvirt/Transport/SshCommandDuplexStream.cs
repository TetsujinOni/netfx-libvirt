using Renci.SshNet;

namespace NetfxLibvirt.Transport;

/// <summary>Adapts a running <see cref="SshCommand"/>'s
/// <see cref="SshCommand.OutputStream"/> (read side) and
/// <see cref="SshCommand.CreateInputStream"/> (write side) into a single
/// duplex <see cref="Stream"/> — what <see cref="SshTransport"/> needs to
/// hand to <c>LibvirtConnection.OpenAsync</c>, which is transport-agnostic
/// and just wants one bidirectional stream. Owns (and disposes) the
/// underlying <see cref="SshCommand"/> and <see cref="SshClient"/>, since
/// nothing else holds a reference to either once this is returned.
///
/// A <see cref="ChannelSession"/>-backed command (not a
/// <c>ShellStream</c>) is deliberately what backs this — no pseudo-terminal
/// allocation, so <c>virt-ssh-helper</c>'s raw RPC byte stream passes
/// through unmodified instead of risking terminal-driver interference
/// (echo, line editing, signal characters) a PTY would introduce.</summary>
internal sealed class SshCommandDuplexStream : Stream
{
    private readonly SshClient _client;
    private readonly SshCommand _command;
    private readonly Stream _input;
    private readonly Stream _output;

    public SshCommandDuplexStream(SshClient client, SshCommand command)
    {
        _client = client;
        _command = command;
        _input = command.CreateInputStream();
        _output = command.OutputStream;
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

    public override int Read(byte[] buffer, int offset, int count) => _output.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _output.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => _input.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _input.WriteAsync(buffer, cancellationToken);

    public override void Flush() => _input.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _input.Dispose();
            _command.Dispose();
            _client.Dispose();
        }

        base.Dispose(disposing);
    }
}
