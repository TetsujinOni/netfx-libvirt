using System.Net.Sockets;

namespace NetfxLibvirt.Transport;

/// <summary>
/// Connects to a local libvirtd over its Unix domain socket (typically
/// <c>/var/run/libvirt/libvirt-sock</c>) — the simplest of libvirt's four
/// transports (local, TCP, TLS, SSH), and this project's first connection
/// to a real running libvirtd (see <c>docs/plan.md</c> story 6).
/// <see cref="UnixDomainSocketEndPoint"/> works on both Linux and Windows
/// 10+, so no platform-specific shim is needed here — only WSL/Linux
/// actually has a libvirtd to dial today, but the code itself isn't
/// Linux-only.
/// </summary>
public static class UnixSocketTransport
{
    public static async Task<NetworkStream> ConnectAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }
}
