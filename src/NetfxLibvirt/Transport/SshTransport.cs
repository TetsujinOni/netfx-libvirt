using Renci.SshNet;
using Renci.SshNet.Common;

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
    public static async Task<Stream> ConnectAsync(SshTransportOptions options, CancellationToken cancellationToken = default)
    {
        var connectionInfo = new ConnectionInfo(options.Host, options.Port, options.Username, BuildAuthenticationMethod(options));
        var client = new SshClient(connectionInfo);
        client.HostKeyReceived += (_, e) => e.CanTrust = VerifyServerHostKey(options, e);

        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshTransportException($"SSH authentication to {options.Host}:{options.Port} as '{options.Username}' failed.", ex);
        }

        var command = client.CreateCommand($"virt-ssh-helper {options.RemoteUri}");
        _ = command.ExecuteAsync(cancellationToken);

        return new SshCommandDuplexStream(client, command);
    }

    private static bool VerifyServerHostKey(SshTransportOptions options, HostKeyEventArgs e) =>
        options.VerifyHostKey(new SshHostKeyInfo(e.HostKeyName, e.KeyLength, e.FingerPrintSHA256, e.HostKey));

    private static AuthenticationMethod BuildAuthenticationMethod(SshTransportOptions options)
    {
        if (options.PrivateKeyPath is not null)
        {
            var keyFile = new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            return new PrivateKeyAuthenticationMethod(options.Username, keyFile);
        }

        return new PasswordAuthenticationMethod(options.Username, options.Password ?? string.Empty);
    }
}
