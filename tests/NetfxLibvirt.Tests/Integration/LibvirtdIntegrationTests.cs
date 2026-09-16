using NetfxLibvirt.Rpc;
using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// End-to-end tests against a REAL running libvirtd over its Unix socket —
/// the "verify against real infra" half of this project's validation
/// discipline (see the <c>feedback_verify-against-real-infra</c> project
/// memory / <c>docs/status.md</c>), as opposed to every other test in this
/// project, which is deliberately hermetic.
///
/// Skipped by default — set <see cref="SocketPathEnvVar"/> to the daemon's
/// Unix socket path to run them — so `dotnet test` stays fully reproducible
/// on any contributor's machine with zero infrastructure dependency, per
/// this project's open-source-independence requirement (`docs/plan.md`
/// story 6's environment note). These must never be wired into anything
/// that assumes access to a specific maintainer's environment. The target
/// URI is deliberately <c>test:///default</c> — libvirt's built-in
/// null-hypervisor driver — not <c>qemu:///system</c>, so anyone with a
/// bare <c>libvirt-daemon</c> install (no KVM, no real VM capability) can
/// reproduce this suite.
///
/// A Windows-native process can't reach a Unix socket living inside WSL2's
/// separate kernel namespace, so run this from inside WSL (or any Linux
/// host with libvirtd):
/// <code>
/// NETFX_LIBVIRT_TEST_SOCKET=/var/run/libvirt/libvirt-sock \
///     dotnet test tests/NetfxLibvirt.Tests --filter FullyQualifiedName~Integration
/// </code>
/// </summary>
public class LibvirtdIntegrationTests
{
    private const string SocketPathEnvVar = "NETFX_LIBVIRT_TEST_SOCKET";
    private const string TestUri = "test:///default";

    private static async Task<LibvirtConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var socketPath = Environment.GetEnvironmentVariable(SocketPathEnvVar);
        if (string.IsNullOrEmpty(socketPath))
        {
            Assert.Skip($"Set {SocketPathEnvVar} to a real libvirtd's Unix socket path to run integration tests (see this class's doc).");
        }

        var stream = await UnixSocketTransport.ConnectAsync(socketPath, cancellationToken).ConfigureAwait(false);
        return await LibvirtConnection.OpenAsync(stream, TestUri, cancellationToken).ConfigureAwait(false);
    }

    [Fact]
    public async Task OpenAsync_AgainstRealLibvirtd_Succeeds()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ListDomainsAsync_AgainstRealLibvirtd_ReturnsTheBuiltInTestDomain()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
        var domains = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);

        // test:///default seeds exactly one domain named "test", running.
        var testDomain = Assert.Single(domains, d => d.Name == "test");
        Assert.Equal(DomainState.Running, testDomain.State);
    }

    [Fact]
    public async Task GetDomainXmlAsync_AgainstRealLibvirtd_ReturnsParsableXml()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
        var xml = await connection.GetDomainXmlAsync("test", TestContext.Current.CancellationToken);

        Assert.Contains("<domain", xml);
        Assert.Contains("test", xml);
    }

    [Fact]
    public async Task DestroyThenStartDomainAsync_AgainstRealLibvirtd_RoundTrips()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);

        // The seeded "test" domain starts running; destroy then recreate it
        // to prove both directions work against a real daemon, and leave it
        // running again afterward — don't leave the daemon's state worse
        // than this test found it.
        await connection.DestroyDomainAsync("test", TestContext.Current.CancellationToken);
        var afterDestroy = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(afterDestroy, d => d.Name == "test" && d.State == DomainState.Running);

        await connection.StartDomainAsync("test", TestContext.Current.CancellationToken);
        var afterStart = await connection.ListDomainsAsync(TestContext.Current.CancellationToken);
        Assert.Contains(afterStart, d => d.Name == "test" && d.State == DomainState.Running);
    }

    [Fact]
    public async Task DisconnectAsync_AgainstRealLibvirtd_Succeeds()
    {
        var connection = await OpenAsync(TestContext.Current.CancellationToken);
        await connection.DisconnectAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StartDomainAsync_UnknownDomainName_ThrowsLibvirtRpcExceptionFromRealDaemon()
    {
        await using var connection = await OpenAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<LibvirtRpcException>(
            () => connection.StartDomainAsync("this-domain-does-not-exist", TestContext.Current.CancellationToken));
    }
}
