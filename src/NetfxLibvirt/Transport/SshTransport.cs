using System.Diagnostics;
using System.Security.Claims;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Keys;
using Microsoft.DevTunnels.Ssh.Messages;
using Microsoft.DevTunnels.Ssh.Tcp;

namespace NetfxLibvirt.Transport;

/// <summary>
/// Connects to a remote libvirtd over SSH, matching <c>virt-desktop</c>'s
/// actual deployment pattern (<c>docs/plan.md</c> story 11) — but not its
/// exact mechanism. <c>virt-desktop</c> opens a <c>direct-streamlocal</c> SSH
/// channel straight to the remote Unix socket; this instead runs libvirt's
/// own official SSH helper, <c>virt-ssh-helper &lt;uri&gt;</c>, over a plain
/// SSH <c>exec</c> channel — libvirt's own client uses this exact mechanism
/// (see <c>src/remote/remote_ssh_helper.c</c> in the vendored reference:
/// it connects to the local libvirt socket and relays raw bytes over its own
/// stdin/stdout, no framing or handshake of its own). Preferred over
/// <c>direct-streamlocal</c> because it needs no special SSH channel-type
/// support from the SSH library — a plain <c>exec</c> channel is the most
/// universally-supported SSH client capability there is, so this works with
/// <c>Microsoft.DevTunnels.Ssh</c> (chosen for its Microsoft-maintained,
/// pure-managed, no-P/Invoke pedigree — see the project's own decision
/// record) without needing to hand-implement a missing channel type.
///
/// Falls back to nothing else yet: if <c>virt-ssh-helper</c> isn't on the
/// remote host's <c>PATH</c>, the exec request is rejected and this throws
/// — matching libvirt's own client, which falls back to a netcat-family
/// command in that case, is a possible future addition, not implemented
/// here (see <c>docs/plan.md</c>).
/// </summary>
public static class SshTransport
{
    public static async Task<Stream> ConnectAsync(SshTransportOptions options, CancellationToken cancellationToken = default)
    {
        var client = new SshClient(SshSessionConfiguration.Default, new TraceSource(nameof(SshTransport)));
        var session = await client.OpenSessionAsync(options.Host, options.Port, cancellationToken).ConfigureAwait(false);

        session.Authenticating += (_, e) =>
        {
            e.AuthenticationTask = Task.FromResult(VerifyServerHostKey(options, e));
        };

        var credentials = BuildCredentials(options);
        if (!await session.AuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false))
        {
            throw new SshTransportException($"SSH authentication to {options.Host}:{options.Port} as '{options.Username}' failed.");
        }

        var channel = await session.OpenChannelAsync(cancellationToken).ConfigureAwait(false);
        var command = $"virt-ssh-helper {options.RemoteUri}";
        var accepted = await channel.RequestAsync(new CommandRequestMessage(command), cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            throw new SshTransportException(
                $"Remote host refused to run '{command}' — is libvirt-daemon (with virt-ssh-helper) installed and on PATH there?");
        }

        return new SshStream(channel);
    }

    private static ClaimsPrincipal? VerifyServerHostKey(SshTransportOptions options, SshAuthenticatingEventArgs e)
    {
        if (e.AuthenticationType != SshAuthenticationType.ServerPublicKey || e.PublicKey is null)
        {
            return null;
        }

        return options.VerifyHostKey(e.PublicKey) ? new ClaimsPrincipal(new ClaimsIdentity()) : null;
    }

    private static SshClientCredentials BuildCredentials(SshTransportOptions options)
    {
        if (options.PrivateKeyPath is not null)
        {
            var keyPair = KeyPair.ImportKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            return new SshClientCredentials(options.Username, keyPair);
        }

        return new SshClientCredentials(options.Username, options.Password);
    }
}
