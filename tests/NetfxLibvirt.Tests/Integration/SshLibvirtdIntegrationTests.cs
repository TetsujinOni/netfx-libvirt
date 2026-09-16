using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// End-to-end tests over a REAL SSH connection to a REAL libvirtd —
/// <c>docs/plan.md</c> story 11, matching <c>virt-desktop</c>'s actual
/// deployment pattern (SSH to the remote host, then reach libvirt) rather
/// than the local Unix socket <see cref="LibvirtdIntegrationTests"/> covers.
///
/// Runs against the throwaway container <see cref="LibvirtdContainerFixture"/>
/// builds and starts — needs only Docker, no manual host setup, works the
/// same for every contributor and in CI. If Docker itself isn't reachable,
/// these tests skip cleanly rather than fail (see the fixture's doc).
///
/// Uses <see cref="SshHostKeyVerifiers.DangerousAcceptAny"/> deliberately —
/// appropriate here specifically because the target is a throwaway
/// container this test run itself just built, not a real remote
/// deployment. Never do that outside a test like this one.
/// </summary>
[Collection(nameof(LibvirtdContainerCollection))]
public class SshLibvirtdIntegrationTests
{
    private const string TestUri = "test:///default";

    private readonly LibvirtdContainerFixture _fixture;

    public SshLibvirtdIntegrationTests(LibvirtdContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<LibvirtConnection> OpenAsync(CancellationToken cancellationToken)
    {
        if (_fixture.StartupFailure is not null)
        {
            Assert.Skip($"Integration test container failed to start (is Docker running?): {_fixture.StartupFailure}");
        }

        var options = new SshTransportOptions
        {
            Host = _fixture.Host,
            Port = _fixture.SshHostPort,
            Username = "root",
            PrivateKeyPath = _fixture.PrivateKeyPath,
            VerifyHostKey = SshHostKeyVerifiers.DangerousAcceptAny,
            RemoteUri = TestUri,
        };

        var stream = await SshTransport.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        return await LibvirtConnection.OpenAsync(stream, TestUri, cancellationToken).ConfigureAwait(false);
    }

    [Fact]
    public async Task OpenAsync_OverRealSsh_Succeeds()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListDomainsAsync_OverRealSsh_ReturnsTheBuiltInTestDomain()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
        var domains = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);

        var testDomain = Assert.Single(domains, d => d.Name == "test");
        Assert.Equal(DomainState.Running, testDomain.State);
    }

    [Fact]
    public async Task GetDomainXmlAsync_OverRealSsh_ReturnsParsableXml()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
        var xml = await connection.GetDomainXmlAsync("test", TestContext.Current.CancellationToken);

        Assert.Contains("<domain", xml);
    }

    [Fact]
    public async Task DisconnectAsync_OverRealSsh_Succeeds()
    {
        var connection = await OpenAsync(TestContext.Current.CancellationToken);
        await connection.DisconnectAsync(TestContext.Current.CancellationToken);
    }
}
