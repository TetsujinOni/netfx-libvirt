namespace NetfxLibvirt.Transport;

/// <summary>Parameters for <see cref="SshTransport.ConnectAsync"/> and
/// <see cref="SshPortForward.OpenAsync"/>. Deliberately covers the same two
/// auth paths <c>virt-desktop</c>'s own <c>SSHConnectionParams</c> supports
/// (password, private key file) — SSH-agent forwarding isn't included yet
/// (see <c>docs/plan.md</c> story 11).</summary>
public sealed record SshTransportOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 22;

    public required string Username { get; init; }

    /// <summary>Password authentication. Mutually exclusive with
    /// <see cref="PrivateKeyPath"/> in practice — if both are set,
    /// <see cref="SshTransport.ConnectAsync"/> prefers the private key.</summary>
    public string? Password { get; init; }

    /// <summary>Path to a private key file (any format <c>SSH.NET</c>'s
    /// <c>PrivateKeyFile</c> supports: OpenSSH, PKCS#1, PKCS#8, PuTTY,
    /// ssh.com — RSA, DSA, ECDSA, and Ed25519 all included).</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Passphrase for <see cref="PrivateKeyPath"/>, if it's encrypted.</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Synchronous host key verifier. **Exactly one** of this and
    /// <see cref="VerifyHostKeyAsync"/> must be set — this library never
    /// silently accepts an unverified host key, so setting neither (or both)
    /// makes <see cref="SshTransport.ConnectAsync"/> /
    /// <see cref="SshPortForward.OpenAsync"/> throw
    /// <see cref="ArgumentException"/> before any network I/O happens. (C#
    /// can't express "exactly one of these two properties is required" at
    /// compile time — a single <c>required</c> property would break every
    /// caller wanting the other one — so this is checked at the first
    /// opportunity instead: the start of the connect call.)</summary>
    public SshHostKeyVerifier? VerifyHostKey { get; init; }

    /// <summary>Asynchronous host key verifier — for asking a person
    /// "trust this host?" during the handshake. See
    /// <see cref="AsyncSshHostKeyVerifier"/> for the threading/cancellation
    /// contract, and set <see cref="ConnectTimeout"/> generously if the
    /// verifier waits on a user. Exactly one of this and
    /// <see cref="VerifyHostKey"/> must be set.</summary>
    public AsyncSshHostKeyVerifier? VerifyHostKeyAsync { get; init; }

    /// <summary>Overrides SSH.NET's connection timeout (30 seconds by
    /// default), which bounds the whole handshake — **including time spent
    /// inside <see cref="VerifyHostKeyAsync"/>**. An interactive verifier
    /// that lets a person take a minute to decide needs this raised above
    /// that; otherwise the connect fails with a timeout while the prompt is
    /// still on screen. Note the server has its own limit too (OpenSSH's
    /// <c>LoginGraceTime</c>, 120 seconds by default).</summary>
    public TimeSpan? ConnectTimeout { get; init; }

    /// <summary>The libvirt connection URI to hand the remote
    /// <c>virt-ssh-helper</c> (e.g. <c>qemu:///system</c>) — identifies the
    /// driver/socket on the remote end. Resolved server-side by
    /// <c>virt-ssh-helper</c> itself, not sent as an RPC argument; see
    /// <see cref="SshTransport"/>'s class doc. Unused by
    /// <see cref="SshPortForward"/>.</summary>
    public required string RemoteUri { get; init; }
}
