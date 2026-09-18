using System.Net;
using Renci.SshNet;

namespace NetfxLibvirt.Transport;

/// <summary>
/// Opens an SSH local port forward (<c>ssh -L</c>'s equivalent, a
/// <c>direct-tcpip</c> channel) to a TCP port on the SSH server's own
/// network — the real, working way to reach a domain's graphics server
/// (VNC/SPICE) when it's bound to the hypervisor's loopback interface
/// (<c>&lt;graphics ... listen='127.0.0.1'&gt;</c>, libvirt's actual
/// default): dialing the hypervisor's routable IP at that port directly
/// gets connection-refused, because nothing outside the hypervisor can
/// reach it that way.
///
/// **There is no libvirt RPC-level tunnel for this**, despite how it might
/// look at first — <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c>/<c>_FD</c> are
/// both FD-passing (<c>SCM_RIGHTS</c> ancillary data on the socket itself),
/// which only works over a real local <c>AF_UNIX</c> socket, never over a
/// network transport like SSH or TCP — confirmed against libvirt's actual C
/// client (<c>remoteDomainOpenGraphics</c>/<c>remoteDomainOpenGraphicsFD</c>
/// in <c>src/remote/remote_driver.c</c>, both using <c>callFull</c>'s
/// <c>fdin</c>/<c>fdout</c> parameters) and against
/// <c>digitalocean/go-libvirt</c>'s own generated wrappers (neither wires a
/// stream reader/writer for either procedure). Real <c>virt-viewer</c>/
/// <c>virt-manager</c> reach a loopback-bound graphics server by opening
/// their own SSH port forward directly to the hypervisor, independent of
/// the libvirt RPC connection — this class is that same mechanism.
///
/// Uses SSH.NET's <see cref="ForwardedPortLocal"/> bound to an ephemeral
/// local port (<c>127.0.0.1:0</c>, the OS picks an actually-free port)
/// rather than exposing a raw channel <see cref="Stream"/> directly —
/// SSH.NET has no public API for the latter, and this shape (connect to a
/// local <c>host:port</c>, exactly like <c>ssh -L</c> itself produces) is
/// also what a downstream VNC/SPICE client library actually expects.
///
/// Opens its own independent SSH connection — deliberately not the same
/// connection a <see cref="SshTransport"/>-backed <see cref="LibvirtConnection"/>
/// might already have open, matching how a real port forward is an
/// independent concern from the RPC session's own transport.
/// </summary>
public sealed class SshPortForward : IAsyncDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;
    private bool _disposed;

    private SshPortForward(SshClient client, ForwardedPortLocal port)
    {
        _client = client;
        _port = port;
    }

    /// <summary>The local loopback endpoint to connect to — transparently forwarded to the remote host/port given to <see cref="OpenAsync"/>.</summary>
    public IPEndPoint LocalEndPoint => new(IPAddress.Loopback, (int)_port.BoundPort);

    /// <summary>
    /// Connects over SSH (using <paramref name="sshOptions"/>'s host/auth/
    /// host-key-verification fields — its <see cref="SshTransportOptions.RemoteUri"/>
    /// is unused here, that's a <see cref="SshTransport"/>-only concern) and
    /// starts forwarding an ephemeral local port to
    /// <paramref name="remoteHost"/>:<paramref name="remotePort"/> as
    /// resolved from the SSH server's own network (e.g. <c>127.0.0.1</c> to
    /// reach a loopback-bound service on the hypervisor itself).
    /// </summary>
    public static async Task<SshPortForward> OpenAsync(
        SshTransportOptions sshOptions, string remoteHost, int remotePort, CancellationToken cancellationToken = default)
    {
        var connectionInfo = new ConnectionInfo(sshOptions.Host, sshOptions.Port, sshOptions.Username, SshTransport.BuildAuthenticationMethod(sshOptions));
        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, e) => e.CanTrust = sshOptions.VerifyHostKey(new SshHostKeyInfo(e.HostKeyName, e.KeyLength, e.FingerPrintSHA256, e.HostKey));

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshTransportException($"SSH authentication to {sshOptions.Host}:{sshOptions.Port} as '{sshOptions.Username}' failed.", ex);
        }

        var port = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, (uint)remotePort);
        client.AddForwardedPort(port);

        try
        {
            port.Start();
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshTransportException($"Failed to start the local port forward to {remoteHost}:{remotePort} via {sshOptions.Host}:{sshOptions.Port}.", ex);
        }

        return new SshPortForward(client, port);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _port.Stop();
        _port.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
