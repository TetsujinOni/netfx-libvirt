using Renci.SshNet;

namespace NetfxLibvirt.Transport;

/// <summary>
/// Connects to a remote libvirtd over SSH, matching <c>virt-desktop</c>'s
/// actual deployment pattern (<c>docs/plan.md</c> story 11) — but not its
/// exact mechanism. <c>virt-desktop</c> opens a <c>direct-streamlocal</c> SSH
/// channel straight to the remote Unix socket; this instead runs libvirt's
/// own official SSH helper, <c>virt-ssh-helper &lt;uri&gt;</c>, over a plain
/// SSH <c>exec</c> channel (a <see cref="SshCommand"/> backed by a
/// <c>ChannelSession</c>, not a PTY-allocating <c>ShellStream</c>) — libvirt's
/// own client uses this exact mechanism (see
/// <c>src/remote/remote_ssh_helper.c</c> in the vendored reference: it
/// connects to the local libvirt socket and relays raw bytes over its own
/// stdin/stdout, no framing or handshake of its own). Preferred over
/// <c>direct-streamlocal</c> because it needs no special SSH channel-type
/// support from the SSH library — a plain <c>exec</c> channel is the most
/// universally-supported SSH client capability there is.
///
/// **Library: <c>SSH.NET</c>, not <c>Microsoft.DevTunnels.Ssh</c>.** The
/// original decision 7 (see <c>docs/plan.md</c>) picked DevTunnels.Ssh for
/// its Microsoft-maintained pedigree once its one missing feature
/// (<c>direct-streamlocal</c>) turned out irrelevant. That decision was
/// reopened once this project's own account needed to authenticate with an
/// Ed25519 key: DevTunnels.Ssh's key importer only supports RSA/ECDSA (the
/// exact reason the Testcontainers fixture's *test* key is ECDSA, not
/// Ed25519 — see <see cref="LibvirtdContainerFixture"/>'s history), and it
/// has neither Ed25519 nor OpenSSH-certificate support anywhere in its
/// source tree, confirmed directly rather than assumed. A community PR
/// adding Ed25519 to DevTunnels.Ssh's TypeScript side has sat fully
/// mergeable, CLA-signed, with zero maintainer engagement for 6+ months —
/// measured evidence this gap wasn't closing on any useful timeline.
/// <c>Tmds.Ssh</c> and <c>SSH.NET</c> both support Ed25519 and OpenSSH
/// certificates; forking DevTunnels.Ssh to add that coverage was considered
/// and rejected as disproportionate — this project's actual need is one
/// plain exec channel, not the multi-channel/interactive strength
/// DevTunnels.Ssh brings to its native Dev Tunnels use case, so paying to
/// maintain a patched fork of it here would be sprawl for capability never
/// used. <c>SSH.NET</c> was picked over <c>Tmds.Ssh</c> on the same
/// supply-chain-concentration reasoning that ruled Tmds.Ssh out the first
/// time (one primary maintainer, ~250 stars) — SSH.NET is the larger,
/// longer-established community project of the two, and its
/// <see cref="SshCommand"/> already exposes exactly the raw,
/// non-interactive duplex exec-channel primitive this transport needs
/// (<see cref="SshCommand.OutputStream"/> / <see cref="SshCommand.CreateInputStream"/>),
/// not just the PTY-biased <c>ShellStream</c> a shallower read of its API
/// might suggest.
///
/// Falls back to nothing else yet: if <c>virt-ssh-helper</c> isn't on the
/// remote host's <c>PATH</c>, the remote shell reports the error on its own
/// (SSH's <c>exec</c> request type doesn't distinguish "command not found"
/// from "command ran and failed" — the server-side shell handles that, not
/// the SSH library), which surfaces as a downstream failure once
/// <c>LibvirtConnection.OpenAsync</c> tries to handshake over what's really
/// an already-closed stream. A future addition (see <c>docs/plan.md</c>)
/// could probe for this more precisely; not implemented here, matching the
/// previous DevTunnels.Ssh-based implementation's own fidelity on this
/// point.
/// </summary>
public static class SshTransport
{
    /// <exception cref="ArgumentException">Neither or both of <see cref="SshTransportOptions.VerifyHostKey"/> / <see cref="SshTransportOptions.VerifyHostKeyAsync"/> are set.</exception>
    /// <exception cref="SshHostKeyRejectedException">The host key verifier rejected the server's key.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (including while a host key verifier was pending) — never re-wrapped as an authentication failure.</exception>
    public static async Task<Stream> ConnectAsync(SshTransportOptions options, CancellationToken cancellationToken = default)
    {
        var client = await ConnectClientAsync(options, cancellationToken).ConfigureAwait(false);

        var command = client.CreateCommand($"virt-ssh-helper {ShellQuote(options.RemoteUri)}");
        _ = command.ExecuteAsync(cancellationToken);

        return new SshCommandDuplexStream(client, command);
    }

    /// <summary>
    /// Quotes <paramref name="value"/> as a single, literal POSIX shell word (standard technique: wrap in
    /// single quotes, and replace every embedded single quote with <c>'\''</c> — close the quote, an
    /// escaped literal quote, reopen the quote). <c>CreateCommand</c> hands its whole string to SSH.NET,
    /// which sends it as the payload of an SSH <c>exec</c> request; the server's <c>sshd</c> runs that
    /// through the login shell (<c>sh -c "&lt;string&gt;"</c>), so <see cref="SshTransportOptions.RemoteUri"/>
    /// landing there unescaped would let any shell metacharacter it contains (<c>;</c>, <c>|</c>, a
    /// backtick, <c>$()</c>, …) run as an arbitrary command on the remote host, under whatever account the
    /// SSH session authenticated as — remote code execution, and a real one: <c>RemoteUri</c> is exactly
    /// the kind of value a consuming app is liable to let a user type or edit (e.g. a saved host profile),
    /// not something this library can assume is already trustworthy. Quoting, not a character allow-list,
    /// because a legitimate libvirt URI's query string can contain characters (<c>&amp;</c>, <c>=</c>, …)
    /// an allow-list would either have to special-case or wrongly reject.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> contains a NUL character (not
    /// representable on a real shell command line, and undefined once it reaches the wire).</exception>
    internal static string ShellQuote(string value)
    {
        if (value.Contains('\0'))
        {
            throw new ArgumentException("Value contains a NUL character, which cannot appear in a shell command.", nameof(value));
        }

        return "'" + value.Replace("'", "'\\''") + "'";
    }

    /// <summary>The connect-and-verify path shared with <see cref="SshPortForward"/>:
    /// validates the options, connects, runs the host key verifier (see
    /// <see cref="SshHostKeyVerification"/>), and maps any failure to the
    /// exception the caller should see.</summary>
    internal static async Task<SshClient> ConnectClientAsync(SshTransportOptions options, CancellationToken cancellationToken)
    {
        var verification = new SshHostKeyVerification(options, cancellationToken);

        var connectionInfo = new ConnectionInfo(options.Host, options.Port, options.Username, BuildAuthenticationMethod(options));
        if (options.ConnectTimeout is { } timeout)
        {
            connectionInfo.Timeout = timeout;
        }

        var client = new SshClient(connectionInfo);
        verification.Attach(client);

        try
        {
            // Off the caller's context: nothing SSH.NET does synchronously
            // during connect (including invoking the host key event) may
            // run on, and so block, a UI thread the verifier might need.
            await Task.Run(() => client.ConnectAsync(cancellationToken), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw verification.MapConnectFailure(
                ex, options.Host, options.Port, $"SSH authentication to {options.Host}:{options.Port} as '{options.Username}' failed.");
        }

        return client;
    }

    /// <summary>Shared with <see cref="SshPortForward"/> so both connect using the exact same auth logic.</summary>
    /// <exception cref="ArgumentException"><see cref="SshTransportOptions.PrivateKeyPath"/> and <see cref="SshTransportOptions.PrivateKeyPaths"/> are both set.</exception>
    internal static AuthenticationMethod BuildAuthenticationMethod(SshTransportOptions options)
    {
        if (options.PrivateKeyPath is not null && options.PrivateKeyPaths is not null)
        {
            throw new ArgumentException(
                $"Set only one of {nameof(SshTransportOptions.PrivateKeyPath)} or {nameof(SshTransportOptions.PrivateKeyPaths)}, not both — which one should decide?",
                nameof(options));
        }

        if (options.PrivateKeyPaths is { Count: > 0 } paths)
        {
            // One PrivateKeyAuthenticationMethod offering every key within a single session, exactly like real
            // ssh — not one connection attempt per candidate (see PrivateKeyPaths' doc for why that matters).
            var keyFiles = paths.Select(path => (IPrivateKeySource)new PrivateKeyFile(path, options.PrivateKeyPassphrase)).ToArray();
            return new PrivateKeyAuthenticationMethod(options.Username, keyFiles);
        }

        if (options.PrivateKeyPath is not null)
        {
            var keyFile = new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(options.Username, keyFile);
        }

        return new PasswordAuthenticationMethod(options.Username, options.Password ?? string.Empty);
    }
}
