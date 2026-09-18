using System.Net.Sockets;
using System.Text;
using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// End-to-end proof that <see cref="SshPortForward"/> actually relays real
/// bytes from a real remote-network service — <c>docs/plan.md</c> story 15's
/// follow-up: the real fix for reaching a domain's graphics server when it's
/// bound to the hypervisor's loopback interface, once
/// <c>REMOTE_PROC_DOMAIN_OPEN_GRAPHICS</c> was confirmed unusable remotely
/// (FD-passing only).
///
/// Forwards to the fixture container's *own* <c>sshd</c>
/// (<c>127.0.0.1:22</c>, resolved from the SSH server's — i.e. the
/// container's — perspective) rather than adding a second service to the
/// Docker image just for this: a real TCP service already exists there, and
/// reading its genuine <c>SSH-2.0-...</c> banner back through the forwarded
/// local port is exactly as strong a proof that real bytes crossed the
/// tunnel as forwarding to anything else would be.
/// </summary>
[Collection(nameof(LibvirtdContainerCollection))]
public class SshPortForwardIntegrationTests
{
    private readonly LibvirtdContainerFixture _fixture;

    public SshPortForwardIntegrationTests(LibvirtdContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private SshTransportOptions BuildOptions()
    {
        if (_fixture.StartupFailure is not null)
        {
            Assert.Skip($"Integration test container failed to start (is Docker running?): {_fixture.StartupFailure}");
        }

        return new SshTransportOptions
        {
            Host = _fixture.Host,
            Port = _fixture.SshHostPort,
            Username = "root",
            PrivateKeyPath = _fixture.PrivateKeyPath,
            VerifyHostKey = SshHostKeyVerifiers.DangerousAcceptAny, // throwaway container this test run itself just built — see SshLibvirtdIntegrationTests' own doc
            RemoteUri = "test:///default", // unused by SshPortForward; required by SshTransportOptions
        };
    }

    [Fact]
    public async Task OpenAsync_ForwardsToTheContainersOwnSshd_RelaysARealSshBanner()
    {
        await using var forward = await SshPortForward.OpenAsync(
            BuildOptions(), remoteHost: "127.0.0.1", remotePort: 22, TestContext.Current.CancellationToken);

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(forward.LocalEndPoint, TestContext.Current.CancellationToken);

        var buffer = new byte[64];
        await using var networkStream = tcpClient.GetStream();
        var read = await networkStream.ReadAsync(buffer, TestContext.Current.CancellationToken);

        var banner = Encoding.ASCII.GetString(buffer, 0, read);
        Assert.StartsWith("SSH-2.0-", banner);
    }
}
