namespace NetfxLibvirt.Transport;

/// <summary>Parameters for <see cref="SshTransport.ConnectAsync"/>. Deliberately
/// covers the same two auth paths <c>virt-desktop</c>'s own
/// <c>SSHConnectionParams</c> supports (password, private key file) — SSH-agent
/// forwarding isn't included yet (see <c>docs/plan.md</c> story 11).</summary>
public sealed record SshTransportOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    /// <summary>Password authentication. Mutually exclusive with
    /// <see cref="PrivateKeyPath"/> in practice — if both are set,
    /// <see cref="SshTransport.ConnectAsync"/> prefers the private key.</summary>
    public string? Password { get; init; }

    /// <summary>Path to a private key file (any format
    /// <c>Microsoft.DevTunnels.Ssh.Keys</c> supports: OpenSSH, PKCS#8,
    /// PKCS#1, SEC1, SSH2 — see that package's README).</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Passphrase for <see cref="PrivateKeyPath"/>, if it's encrypted.</summary>
    public string? PrivateKeyPassphrase { get; init; }

    public required SshHostKeyVerifier VerifyHostKey { get; init; }

    /// <summary>The libvirt connection URI to hand the remote
    /// <c>virt-ssh-helper</c> (e.g. <c>qemu:///system</c>) — identifies the
    /// driver/socket on the remote end. Resolved server-side by
    /// <c>virt-ssh-helper</c> itself, not sent as an RPC argument; see
    /// <see cref="SshTransport"/>'s class doc.</summary>
    public required string RemoteUri { get; init; }
}
