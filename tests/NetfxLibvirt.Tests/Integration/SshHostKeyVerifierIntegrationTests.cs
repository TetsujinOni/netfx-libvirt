using System.Diagnostics;
using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// The async host key verifier over a REAL SSH handshake (the Testcontainers
/// fixture's <c>sshd</c>) — proves the bridge in
/// <see cref="SshHostKeyVerification"/> works with SSH.NET's actual
/// <c>HostKeyReceived</c> event, not just in isolation: an async accept
/// completes the connect, an async reject surfaces as
/// <see cref="SshHostKeyRejectedException"/> carrying the real key the
/// server presented, cancelling the connect dismisses a pending prompt, and
/// the connect timeout really does bound the time a verifier may take.
/// Uses <see cref="LibvirtdContainerCollection"/> like the other SSH
/// integration tests (skips cleanly if Docker isn't available).
/// </summary>
[Collection(nameof(LibvirtdContainerCollection))]
public class SshHostKeyVerifierIntegrationTests
{
    private readonly LibvirtdContainerFixture _fixture;

    public SshHostKeyVerifierIntegrationTests(LibvirtdContainerFixture fixture)
    {
        _fixture = fixture;
    }

    private SshTransportOptions Options(AsyncSshHostKeyVerifier verifier, TimeSpan? connectTimeout = null)
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
            VerifyHostKeyAsync = verifier,
            ConnectTimeout = connectTimeout,
            RemoteUri = "test:///default",
        };
    }

    [Fact]
    public async Task AsyncVerifier_Accept_CompletesTheConnectForBothTransports()
    {
        SshHostKeyInfo? seen = null;
        AsyncSshHostKeyVerifier verifier = async (key, ct) =>
        {
            seen = key;
            await Task.Delay(200, ct); // a real asynchronous decision, like a user answering a dialog
            return true;
        };
        var options = Options(verifier);

        await using (await SshTransport.ConnectAsync(options, TestContext.Current.CancellationToken))
        {
        }

        Assert.NotNull(seen);
        Assert.False(string.IsNullOrEmpty(seen.AlgorithmName));
        Assert.False(string.IsNullOrEmpty(seen.Sha256Fingerprint));

        await using var forward = await SshPortForward.OpenAsync(options, "127.0.0.1", 22, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AsyncVerifier_Reject_SurfacesSshHostKeyRejectedExceptionWithTheRealKey_ForBothTransports()
    {
        SshHostKeyInfo? offered = null;
        var options = Options((key, _) =>
        {
            offered = key;
            return ValueTask.FromResult(false);
        });

        var fromTransport = await Assert.ThrowsAsync<SshHostKeyRejectedException>(
            () => SshTransport.ConnectAsync(options, TestContext.Current.CancellationToken));
        Assert.Equal(offered!.Sha256Fingerprint, fromTransport.HostKey.Sha256Fingerprint);
        Assert.Equal(offered.AlgorithmName, fromTransport.HostKey.AlgorithmName);
        Assert.Equal(_fixture.SshHostPort, fromTransport.Port);

        var fromForward = await Assert.ThrowsAsync<SshHostKeyRejectedException>(
            () => SshPortForward.OpenAsync(options, "127.0.0.1", 22, TestContext.Current.CancellationToken));
        Assert.Equal(offered.Sha256Fingerprint, fromForward.HostKey.Sha256Fingerprint);
    }

    [Fact]
    public async Task SyncVerifier_Reject_AlsoSurfacesSshHostKeyRejectedException()
    {
        var options = Options((_, _) => ValueTask.FromResult(true)) with
        {
            VerifyHostKeyAsync = null,
            VerifyHostKey = _ => false,
        };

        await Assert.ThrowsAsync<SshHostKeyRejectedException>(() => SshTransport.ConnectAsync(options, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AsyncVerifier_CancellingTheConnectWhilePrompting_DismissesThePromptAndSurfacesOperationCanceledException()
    {
        var promptDismissed = new TaskCompletionSource();
        var promptShown = new TaskCompletionSource();
        var options = Options(async (_, ct) =>
        {
            promptShown.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct); // a dialog nobody answers
                return true;
            }
            finally
            {
                promptDismissed.SetResult(); // the verifier saw the connect's token fire
            }
        });

        using var cts = new CancellationTokenSource();
        var connect = SshTransport.ConnectAsync(options, cts.Token);
        await promptShown.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        await promptDismissed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"cancelling took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ConnectTimeout_BoundsHowLongAVerifierMayTake_AndAGenerousOneCoversASlowPrompt()
    {
        AsyncSshHostKeyVerifier slowButAccepting = async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return true;
        };

        // Too short for the "user": the handshake times out while the prompt is still pending.
        var tooShort = Options(slowButAccepting, connectTimeout: TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<SshTransportException>(() => SshTransport.ConnectAsync(tooShort, TestContext.Current.CancellationToken));

        // Generous enough: the same slow verifier now succeeds.
        var generous = Options(slowButAccepting, connectTimeout: TimeSpan.FromSeconds(60));
        await using (await SshTransport.ConnectAsync(generous, TestContext.Current.CancellationToken))
        {
        }
    }
}
