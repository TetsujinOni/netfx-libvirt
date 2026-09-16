using NetfxLibvirt.Generated.Remote;

namespace NetfxLibvirt.Rpc;

/// <summary>
/// Thrown when a libvirt RPC call replies with
/// <see cref="VirNetMessageStatus.Error"/>. Carries the decoded
/// <c>remote_error</c> payload so callers can inspect the real libvirt
/// error code/domain instead of just a message string.
/// </summary>
public sealed class LibvirtRpcException : Exception
{
    public LibvirtRpcException(RemoteError error)
        : base(error.Message ?? $"libvirt RPC error {error.Code} (domain {error.Domain})")
    {
        Error = error;
    }

    public RemoteError Error { get; }
}
