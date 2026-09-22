using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// End-to-end proof, over a REAL SSH connection to a REAL <c>sshd</c>, that
/// <see cref="SshTransportOptions.RemoteUri"/> can no longer smuggle a second shell command into the
/// remote exec — the actual vulnerability <see cref="SshTransport.ShellQuote"/> closes (see its own doc
/// and <c>Transport/SshTransportShellQuoteTests</c> for the hermetic, shell-oracle-verified unit tests).
/// A hermetic test can prove the quoting function itself is correct; only a real handshake proves nothing
/// upstream of it (SSH.NET's <c>CreateCommand</c>, the actual SSH <c>exec</c> request, the container's real
/// <c>sh</c>) re-introduces the hole.
/// </summary>
[Collection(nameof(LibvirtdContainerCollection))]
public class SshTransportShellQuoteIntegrationTests
{
    private readonly LibvirtdContainerFixture _fixture;

    public SshTransportShellQuoteIntegrationTests(LibvirtdContainerFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.StartupFailure is not null)
        {
            Assert.Skip($"Integration test container failed to start (is Docker running?): {_fixture.StartupFailure}");
        }
    }

    private SshTransportOptions Options(string remoteUri) => new()
    {
        Host = _fixture.Host,
        Port = _fixture.SshHostPort,
        Username = "root",
        PrivateKeyPath = _fixture.PrivateKeyPath,
        VerifyHostKey = SshHostKeyVerifiers.DangerousAcceptAny, // the throwaway container this run itself built — see SshLibvirtdIntegrationTests
        RemoteUri = remoteUri,
    };

    private async Task<bool> MarkerExistsAsync(string marker)
    {
        var result = await _fixture.Container.ExecAsync(["sh", "-c", $"test -e /tmp/{marker}"]);
        return result.ExitCode == 0;
    }

    [Theory]
    [InlineData("; touch /tmp/{0}")]
    [InlineData("$(touch /tmp/{0})")]
    [InlineData("`touch /tmp/{0}`")]
    [InlineData("&& touch /tmp/{0}")]
    [InlineData("| touch /tmp/{0}")]
    [InlineData("\ntouch /tmp/{0}")]
    public async Task MaliciousRemoteUri_NeverExecutesASecondCommand_OnARealRemoteHost(string payloadTemplate)
    {
        var marker = $"pwned-{Guid.NewGuid():N}";
        var maliciousUri = "test:///default" + string.Format(payloadTemplate, marker);

        try
        {
            await using var stream = await SshTransport.ConnectAsync(Options(maliciousUri), TestContext.Current.CancellationToken);

            // Whatever happens to the connection/protocol layer on top of a bogus URI (virt-ssh-helper
            // rejecting it, or the handshake simply never producing valid RPC bytes) isn't what's under
            // test here — only whether a second shell command ran on the remote host is. Give the remote
            // exec time to have run before checking.
            try
            {
                var buffer = new byte[256];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _ = await stream.ReadAsync(buffer, timeout.Token);
            }
            catch (Exception)
            {
                // Expected in most cases: virt-ssh-helper can't make sense of the payload as a URI, or the
                // channel closes. Irrelevant to this test either way.
            }
        }
        catch (Exception)
        {
            // A rejected/failed connect is a fine, safe outcome for a garbage RemoteUri — this test only
            // cares that the injected command never ran.
        }

        Assert.False(await MarkerExistsAsync(marker), $"'{maliciousUri}' executed a second command on the remote host — RemoteUri is not being shell-quoted.");
    }

    [Fact]
    public async Task LegitimateRemoteUri_StillConnectsSuccessfully_ThroughTheSameQuotingPath()
    {
        // The fix must not break the ordinary case — same shape as SshLibvirtdIntegrationTests, kept here so
        // this file stands alone as evidence the quoting doesn't regress a real, valid libvirt URI.
        await using var stream = await SshTransport.ConnectAsync(Options("test:///default"), TestContext.Current.CancellationToken);
        await using var connection = await LibvirtConnection.OpenAsync(stream, "test:///default", TestContext.Current.CancellationToken);

        var domains = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);

        Assert.Contains(domains, d => d.Name == "test");
    }
}
