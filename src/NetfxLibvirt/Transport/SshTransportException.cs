namespace NetfxLibvirt.Transport;

/// <summary>Thrown when <see cref="SshTransport.ConnectAsync"/> fails at the
/// SSH layer itself (authentication rejected, remote command refused) —
/// distinct from a <c>NetfxLibvirt.Rpc.LibvirtRpcException</c>, which means
/// the SSH tunnel worked and the libvirt RPC underneath it is what
/// failed.</summary>
public sealed class SshTransportException : Exception
{
    public SshTransportException(string message) : base(message)
    {
    }
}
