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
    /// <see cref="PrivateKeyPath"/>/<see cref="PrivateKeyPaths"/> in
    /// practice — if either is set, <see cref="SshTransport.ConnectAsync"/>
    /// prefers the private key(s).</summary>
    public string? Password { get; init; }

    /// <summary>Path to a single private key file (any format <c>SSH.NET</c>'s
    /// <c>PrivateKeyFile</c> supports: OpenSSH, PKCS#1, PKCS#8, PuTTY,
    /// ssh.com — RSA, DSA, ECDSA, and Ed25519 all included). Mutually
    /// exclusive with <see cref="PrivateKeyPaths"/> — set at most one of the
    /// two (checked at the start of <see cref="SshTransport.ConnectAsync"/>,
    /// before any network I/O). Prefer <see cref="PrivateKeyPaths"/> when a
    /// caller has more than one candidate key (e.g. the several
    /// <c>IdentityFile</c> entries <c>ssh_config</c>'s
    /// <c>NetfxLibvirt.Transport.OpenSsh.OpenSshConfig</c> resolves) —
    /// trying candidates one connection at a time, as this project's own
    /// consumer originally had to, costs a full extra TCP+KEX+auth round
    /// trip per miss and defeats server-side rate limiting on failed
    /// attempts; SSH.NET natively offers every key within a single session,
    /// exactly like real <c>ssh</c> does.</summary>
    public string? PrivateKeyPath { get; init; }

    /// <summary>Several candidate private key files, all offered within one
    /// SSH session (one <c>PrivateKeyAuthenticationMethod</c>, in order) —
    /// see <see cref="PrivateKeyPath"/>'s doc for why this exists. Every
    /// path must exist and be loadable; unlike
    /// <c>OpenSshConfig</c>'s own default-candidate fallback (which silently
    /// drops candidates that don't exist locally, matching real ssh), this
    /// list is taken as given — filter it yourself first if some entries
    /// might be missing. Uses the same <see cref="PrivateKeyPassphrase"/>
    /// for every entry; if your candidates need different passphrases (or
    /// some are unencrypted and others aren't), load them upstream (e.g.
    /// into an SSH agent) or connect once per differently-passphrased key
    /// instead. Mutually exclusive with <see cref="PrivateKeyPath"/>.</summary>
    public IReadOnlyList<string>? PrivateKeyPaths { get; init; }

    /// <summary>Passphrase for <see cref="PrivateKeyPath"/>, or shared
    /// across every <see cref="PrivateKeyPaths"/> entry, if encrypted.</summary>
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
    /// <see cref="SshTransport"/>'s class doc. <see cref="SshTransport.ConnectAsync"/>
    /// shell-quotes this before it ever reaches the remote exec command
    /// (see <see cref="SshTransport.ShellQuote"/>'s doc — this is a value an
    /// app is liable to let a user type or store, and unescaped shell
    /// metacharacters in it would otherwise be remote code execution on the
    /// libvirt host), so any string is safe to pass here regardless of
    /// where it came from; it still must be a URI <c>virt-ssh-helper</c> can
    /// actually resolve. Unused by <see cref="SshPortForward"/>.</summary>
    public required string RemoteUri { get; init; }
}
