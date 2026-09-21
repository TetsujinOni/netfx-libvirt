using NetfxLibvirt.Tests.OpenSsh;
using NetfxLibvirt.Transport;
using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// OpenSSH host verification against a REAL lab host — the case that motivated
/// this feature (a host presenting an OpenSSH host certificate). Skips cleanly
/// unless the environment describes one; never touches the user's real
/// <c>known_hosts</c> (it uses a temporary file and answers the trust prompts
/// itself, so it verifies the mechanics, not your trust decision).
///
///   NETFX_LIBVIRT_LAB_SSH_HOST   host or host:port          (required)
///   NETFX_LIBVIRT_LAB_SSH_USER   login user                 (required)
///   NETFX_LIBVIRT_LAB_SSH_KEY    path to a private key      (required)
///   NETFX_LIBVIRT_LAB_SSH_CA     path to a CA .pub file, to pre-trust it as
///                                a @cert-authority (optional; without it the
///                                test answers "yes" to the CA / host key prompt)
/// </summary>
public class LabHostOpenSshVerifierTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task LabHost_ConnectsThroughTheOpenSshVerifier_AndSecondConnectIsSilent()
    {
        var target = Environment.GetEnvironmentVariable("NETFX_LIBVIRT_LAB_SSH_HOST");
        var user = Environment.GetEnvironmentVariable("NETFX_LIBVIRT_LAB_SSH_USER");
        var key = Environment.GetEnvironmentVariable("NETFX_LIBVIRT_LAB_SSH_KEY");
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(key))
        {
            Assert.Skip("No lab host configured (set NETFX_LIBVIRT_LAB_SSH_HOST / _USER / _KEY).");
        }

        var (host, port) = SplitHostPort(target);
        var knownHosts = _temp.File("known_hosts");
        if (Environment.GetEnvironmentVariable("NETFX_LIBVIRT_LAB_SSH_CA") is { Length: > 0 } caFile)
        {
            var ca = OpenSshPublicKey.FromBlob(Convert.FromBase64String(File.ReadAllText(caFile).Split(' ')[1]));
            File.WriteAllText(knownHosts, $"@cert-authority {host} {ca.KeyType} {ca.ToBase64()}\n");
        }

        var prompt = new RecordingPrompt();
        var options = new OpenSshHostKeyVerifierOptions
        {
            Host = host,
            Port = port,
            UserKnownHostsFile = knownHosts,
            GlobalKnownHostsFiles = [],
            Prompt = prompt,
        };
        SshHostKeyInfo? seen = null;
        var inner = OpenSshHostKeyVerifier.Create(options);

        SshTransportOptions Transport() => new()
        {
            Host = host,
            Port = port,
            Username = user,
            PrivateKeyPath = key,
            RemoteUri = "qemu:///system",
            ConnectTimeout = TimeSpan.FromSeconds(30),
            VerifyHostKeyAsync = async (info, ct) =>
            {
                seen = info;
                return await inner(info, ct);
            },
        };

        using (var first = await SshTransport.ConnectClientAsync(Transport(), TestContext.Current.CancellationToken))
        {
            Assert.True(first.IsConnected);
        }

        Assert.NotNull(seen);
        // Whatever the lab host presents, what we recorded is what it presented: a CA for a certificate, else the key.
        var recorded = OpenSshKnownHosts.Load([knownHosts]);
        Assert.NotEmpty(recorded.Entries);
        if (seen.IsCertificate)
        {
            Assert.Contains(recorded.Entries, e => e.Marker == KnownHostsMarker.CertificateAuthority && e.Key.Equals(seen.Certificate!.CertificateAuthorityKey));
        }
        else
        {
            Assert.Contains(recorded.Entries, e => e.Marker == KnownHostsMarker.None && e.Key.Blob.Span.SequenceEqual(seen.RawKey));
        }

        var promptsAfterFirst = prompt.HostKeyQuestions.Count + prompt.CaQuestions.Count;
        using (var second = await SshTransport.ConnectClientAsync(Transport(), TestContext.Current.CancellationToken))
        {
            Assert.True(second.IsConnected);
        }

        Assert.Equal(promptsAfterFirst, prompt.HostKeyQuestions.Count + prompt.CaQuestions.Count);
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value[(colon + 1)..], out var port) ? (value[..colon], port) : (value, 22);
    }
}
