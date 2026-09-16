using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// End-to-end tests over a REAL SSH connection to a REAL libvirtd —
/// <c>docs/plan.md</c> story 11, matching <c>virt-desktop</c>'s actual
/// deployment pattern (SSH to the remote host, then reach libvirt) rather
/// than the local Unix socket <see cref="LibvirtdIntegrationTests"/> covers.
///
/// Skipped by default — set <see cref="HostEnvVar"/> (and the other env
/// vars below) to run — for the same open-source-independence reason as
/// <see cref="LibvirtdIntegrationTests"/>: `dotnet test` must stay
/// reproducible with zero infrastructure on any contributor's machine.
///
/// To run against WSL's own libvirtd over SSH to localhost (after
/// `sudo apt-get install -y openssh-server && sudo systemctl enable --now ssh`
/// inside WSL, and adding your public key to
/// `~/.ssh/authorized_keys` there, or setting a password):
/// <code>
/// NETFX_LIBVIRT_SSH_HOST=localhost \
/// NETFX_LIBVIRT_SSH_USER=&lt;your WSL username&gt; \
/// NETFX_LIBVIRT_SSH_PRIVATE_KEY_PATH=/home/&lt;user&gt;/.ssh/id_ed25519 \
///     dotnet test tests/NetfxLibvirt.Tests --filter FullyQualifiedName~SshLibvirtdIntegrationTests
/// </code>
/// This uses <see cref="SshHostKeyVerifiers.DangerousAcceptAny"/> deliberately —
/// appropriate only because the target here is a loopback connection to a
/// host this test suite (and whoever set the env vars) already controls,
/// not a real remote deployment. Never do that outside a test like this one.
/// </summary>
public class SshLibvirtdIntegrationTests
{
    private const string HostEnvVar = "NETFX_LIBVIRT_SSH_HOST";
    private const string PortEnvVar = "NETFX_LIBVIRT_SSH_PORT";
    private const string UserEnvVar = "NETFX_LIBVIRT_SSH_USER";
    private const string PasswordEnvVar = "NETFX_LIBVIRT_SSH_PASSWORD";
    private const string PrivateKeyPathEnvVar = "NETFX_LIBVIRT_SSH_PRIVATE_KEY_PATH";
    private const string TestUri = "test:///default";

    private static async Task<LibvirtConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var host = Environment.GetEnvironmentVariable(HostEnvVar);
        var user = Environment.GetEnvironmentVariable(UserEnvVar);
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user))
        {
            Assert.Skip($"Set {HostEnvVar} and {UserEnvVar} (and {PasswordEnvVar} or {PrivateKeyPathEnvVar}) to run SSH integration tests (see this class's doc).");
        }

        var portText = Environment.GetEnvironmentVariable(PortEnvVar);
        var options = new SshTransportOptions
        {
            Host = host,
            Port = string.IsNullOrEmpty(portText) ? 22 : int.Parse(portText),
            Username = user,
            Password = Environment.GetEnvironmentVariable(PasswordEnvVar),
            PrivateKeyPath = Environment.GetEnvironmentVariable(PrivateKeyPathEnvVar),
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
