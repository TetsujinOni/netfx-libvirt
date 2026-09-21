namespace NetfxLibvirt.Transport;

/// <summary>Thrown by <see cref="SshTransport.ConnectAsync"/> and
/// <see cref="SshPortForward.OpenAsync"/> when the caller's host key
/// verifier (<see cref="SshTransportOptions.VerifyHostKey"/> or
/// <see cref="SshTransportOptions.VerifyHostKeyAsync"/>) rejected the key the
/// server presented — a distinct, catchable signal, so a consumer doesn't
/// have to sniff a generic connection failure or record the mismatch in its
/// own verifier closure. Derives from <see cref="SshTransportException"/>, so
/// existing <c>catch (SshTransportException)</c> handlers still see it.
/// <see cref="HostKey"/> is the exact key that was rejected (e.g. to show
/// in a "host key changed" warning).</summary>
public sealed class SshHostKeyRejectedException : SshTransportException
{
    public SshHostKeyRejectedException(SshHostKeyInfo hostKey, string host, int port, Exception? innerException = null)
        : base(
            $"The SSH host key presented by {host}:{port} ({hostKey.AlgorithmName}, SHA256:{hostKey.Sha256Fingerprint}) was rejected by the host key verifier.",
            innerException!)
    {
        HostKey = hostKey;
        Host = host;
        Port = port;
    }

    /// <summary>The host key the server presented, which the verifier rejected.</summary>
    public SshHostKeyInfo HostKey { get; }

    public string Host { get; }

    public int Port { get; }
}
