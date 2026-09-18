using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Transport;

/// <summary>Hermetic regression tests for a real bug found wiring
/// <see cref="SshPortForward"/> into a real caller (avalonia-virt-manager):
/// the generic <c>catch (Exception ex)</c> around <c>SshClient.ConnectAsync</c>
/// in both <see cref="SshTransport.ConnectAsync"/> and
/// <see cref="SshPortForward.OpenAsync"/> caught
/// <see cref="OperationCanceledException"/> too and rewrapped it as an
/// <see cref="SshTransportException"/> — so a caller cancelling mid-connect
/// (e.g. navigating away) saw a misleading "authentication failed" instead
/// of an ordinary cancellation. Both now rethrow cancellation as-is via a
/// dedicated <c>catch (OperationCanceledException) when
/// (cancellationToken.IsCancellationRequested)</c> clause before the
/// generic one. No Docker/network needed — SSH.NET's own
/// <c>BaseClient.ConnectAsync</c> calls
/// <c>cancellationToken.ThrowIfCancellationRequested()</c> before touching
/// the network, so an already-cancelled token exercises this without ever
/// dialing anywhere.</summary>
public class SshCancellationTests
{
    private static SshTransportOptions BuildOptions() => new()
    {
        Host = "203.0.113.1", // TEST-NET-3 (RFC 5737) -- never actually dialed, cancellation fires first
        Username = "irrelevant",
        VerifyHostKey = _ => true,
        RemoteUri = "test:///default",
    };

    [Fact]
    public async Task SshTransport_ConnectAsync_AlreadyCancelledToken_ThrowsOperationCanceledExceptionUnwrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SshTransport.ConnectAsync(BuildOptions(), cts.Token));
    }

    [Fact]
    public async Task SshPortForward_OpenAsync_AlreadyCancelledToken_ThrowsOperationCanceledExceptionUnwrapped()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => SshPortForward.OpenAsync(BuildOptions(), "127.0.0.1", 5900, cts.Token));
    }
}
