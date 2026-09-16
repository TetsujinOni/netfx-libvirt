using NetfxLibvirt.Generated.Remote;
using NetfxLibvirt.Rpc;
using NetfxLibvirt.Xdr;

namespace NetfxLibvirt;

/// <summary>
/// An open libvirt RPC session over an already-connected transport
/// <see cref="Stream"/> (Unix socket, TCP, TLS, or an SSH tunnel — this type
/// doesn't care which; see <c>docs/plan.md</c> for the transports
/// themselves). Construct with <see cref="OpenAsync"/>, which performs the
/// handshake every libvirt client must do before anything else: an
/// <c>AUTH_LIST</c> call (libvirt requires this even when no authentication
/// is actually used), then <c>CONNECT_OPEN</c>.
/// </summary>
public sealed class LibvirtConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly VirNetRpcClient _rpc;

    private LibvirtConnection(Stream stream, VirNetRpcClient rpc)
    {
        _stream = stream;
        _rpc = rpc;
    }

    /// <summary>
    /// Performs the AUTH_LIST + CONNECT_OPEN handshake over
    /// <paramref name="stream"/> and returns the open session.
    /// <paramref name="uri"/> is the libvirt connection URI (e.g.
    /// <c>qemu:///system</c>) — it identifies the driver on the other end,
    /// independent of how <paramref name="stream"/> itself got there.
    ///
    /// Only <see cref="RemoteAuthType.RemoteAuthNone"/> is supported so far
    /// — matches every transport in <c>docs/plan.md</c>'s parity milestone
    /// (a local Unix socket, or an SSH tunnel that already authenticated at
    /// the SSH layer). SASL/Polkit are real gaps, not silently accepted:
    /// this throws <see cref="NotSupportedException"/> naming what
    /// libvirtd actually offered instead of guessing.
    /// </summary>
    public static async Task<LibvirtConnection> OpenAsync(Stream stream, string uri, CancellationToken cancellationToken = default)
    {
        var rpc = new VirNetRpcClient(stream);

        var authListPayload = await rpc.CallAsync((int)RemoteProcedure.RemoteProcAuthList, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        var authList = RemoteAuthListRet.Decode(new XdrReader(authListPayload));
        if (!authList.Types.Contains(RemoteAuthType.RemoteAuthNone))
        {
            throw new NotSupportedException(
                $"libvirtd only offered [{string.Join(", ", authList.Types)}] — only {nameof(RemoteAuthType.RemoteAuthNone)} is supported so far (see docs/plan.md).");
        }

        var openArgs = new RemoteConnectOpenArgs { Name = uri, Flags = 0 };
        var argsWriter = new XdrWriter();
        openArgs.Encode(argsWriter);
        await rpc.CallAsync((int)RemoteProcedure.RemoteProcConnectOpen, argsWriter.ToArray(), cancellationToken).ConfigureAwait(false);

        return new LibvirtConnection(stream, rpc);
    }

    /// <summary>Closes the underlying transport stream without a graceful
    /// CONNECT_CLOSE RPC round-trip — a placeholder until
    /// <c>DisconnectAsync</c> (docs/plan.md story 10) adds that. Safe to
    /// call more than once or after a failed <see cref="OpenAsync"/>.</summary>
    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}
