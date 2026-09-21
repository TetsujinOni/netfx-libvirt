using System.Diagnostics;
using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Transport;

/// <summary>Hermetic tests for <see cref="SshHostKeyVerification"/> — the
/// bridge from a caller's sync/async host key verifier onto SSH.NET's
/// synchronous <c>HostKeyReceived</c> event, and the mapping of "why the
/// handshake failed" onto the exception a caller should actually see. The
/// end-to-end version over a real SSH handshake lives in
/// <c>Integration/SshHostKeyVerifierIntegrationTests</c>.</summary>
public class SshHostKeyVerificationTests
{
    private static readonly SshHostKeyInfo SampleKey = new("ssh-ed25519", 256, "AAAAfingerprintAAAA", [1, 2, 3]);

    private static SshTransportOptions Options(SshHostKeyVerifier? sync = null, AsyncSshHostKeyVerifier? async = null) => new()
    {
        Host = "h",
        Username = "u",
        RemoteUri = "test:///default",
        VerifyHostKey = sync,
        VerifyHostKeyAsync = async,
    };

    private static SshHostKeyVerification Create(AsyncSshHostKeyVerifier async, CancellationToken ct = default) =>
        new(Options(async: async), ct);

    // ---- validation: "never silently accept an unverified host key" ----

    [Fact]
    public void Constructor_NeitherVerifierSet_Throws() =>
        Assert.Throws<ArgumentException>(() => new SshHostKeyVerification(Options(), CancellationToken.None));

    [Fact]
    public void Constructor_BothVerifiersSet_Throws() =>
        Assert.Throws<ArgumentException>(
            () => new SshHostKeyVerification(Options(sync: _ => true, async: (_, _) => ValueTask.FromResult(true)), CancellationToken.None));

    [Fact]
    public async Task ConnectAsync_NeitherVerifierSet_FailsBeforeAnyNetworkIo()
    {
        // TEST-NET-3: if validation happened after dialing, this would hang/time out rather than throw ArgumentException.
        var options = Options() with { Host = "203.0.113.1" };

        await Assert.ThrowsAsync<ArgumentException>(() => SshTransport.ConnectAsync(options, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => SshPortForward.OpenAsync(options, "127.0.0.1", 5900, TestContext.Current.CancellationToken));
    }

    // ---- sync verifier keeps working ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Verify_SyncVerifier_ReturnsItsDecision(bool decision)
    {
        var verification = new SshHostKeyVerification(Options(sync: _ => decision), CancellationToken.None);

        Assert.Equal(decision, verification.Verify(SampleKey));
        Assert.Equal(decision ? null : SampleKey, verification.RejectedKey);
    }

    // ---- async accept / reject ----

    [Fact]
    public void Verify_AsyncAccept_ReturnsTrueAndRecordsNoRejection()
    {
        var verification = Create(async (_, _) =>
        {
            await Task.Delay(20);
            return true;
        });

        Assert.True(verification.Verify(SampleKey));
        Assert.Null(verification.RejectedKey);
    }

    [Fact]
    public void Verify_AsyncReject_ReturnsFalseAndMapsToSshHostKeyRejectedException()
    {
        var verification = Create(async (_, _) =>
        {
            await Task.Delay(20);
            return false;
        });

        Assert.False(verification.Verify(SampleKey));

        var mapped = verification.MapConnectFailure(new InvalidOperationException("kex failed"), "host", 22, "generic");
        var rejected = Assert.IsType<SshHostKeyRejectedException>(mapped);
        Assert.Same(SampleKey, rejected.HostKey);
        Assert.Equal("host", rejected.Host);
        Assert.Equal(22, rejected.Port);
        Assert.IsAssignableFrom<SshTransportException>(rejected); // existing catch (SshTransportException) handlers still see it
        Assert.Contains("SHA256:AAAAfingerprintAAAA", rejected.Message);
    }

    [Fact]
    public void Verify_VerifierReceivesTheKeyAndTheConnectsToken()
    {
        using var cts = new CancellationTokenSource();
        SshHostKeyInfo? seenKey = null;
        CancellationToken seenToken = default;
        var verification = Create((key, ct) =>
        {
            seenKey = key;
            seenToken = ct;
            return ValueTask.FromResult(true);
        }, cts.Token);

        verification.Verify(SampleKey);

        Assert.Same(SampleKey, seenKey);
        Assert.Equal(cts.Token, seenToken);
    }

    // ---- cancellation during the prompt ----

    [Fact]
    public void Verify_CancelledWhilePrompting_RejectsKeyAndMapsToOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        var verification = Create(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct); // a prompt that only ends when the connect is cancelled
            return true;
        }, cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var trusted = verification.Verify(SampleKey);

        Assert.False(trusted);
        Assert.True(verification.VerifierCancelled);
        Assert.Null(verification.RejectedKey); // cancelled, not rejected
        Assert.IsAssignableFrom<OperationCanceledException>(
            verification.MapConnectFailure(new InvalidOperationException("kex failed"), "host", 22, "generic"));
    }

    [Fact]
    public void Verify_CancelledWhileVerifierIgnoresItsToken_StillReturnsPromptly()
    {
        // A verifier that never observes its token must not be able to hold
        // the handshake (and the caller's cancellation) hostage.
        using var cts = new CancellationTokenSource();
        var never = new TaskCompletionSource<bool>();
        var verification = Create((_, _) => new ValueTask<bool>(never.Task), cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var stopwatch = Stopwatch.StartNew();
        var trusted = verification.Verify(SampleKey);

        Assert.False(trusted);
        Assert.True(verification.VerifierCancelled);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Verify took {stopwatch.Elapsed} despite cancellation");
        never.SetException(new InvalidOperationException("late fault, must not blow up as an unobserved task exception"));
    }

    // ---- a throwing verifier rejects the key ----

    [Fact]
    public void Verify_ThrowingVerifier_RejectsKeyAndMapsToSshTransportExceptionWrappingIt()
    {
        var boom = new InvalidOperationException("dialog crashed");
        var verification = Create((_, _) => throw boom);

        Assert.False(verification.Verify(SampleKey));

        var mapped = verification.MapConnectFailure(new InvalidOperationException("kex failed"), "host", 22, "generic");
        var wrapped = Assert.IsType<SshTransportException>(mapped);
        Assert.Same(boom, wrapped.InnerException);
    }

    [Fact]
    public void Verify_VerifierCancelledByItsOwnTokenNotTheConnects_IsAVerifierFaultNotACallerCancellation()
    {
        using var ownCts = new CancellationTokenSource();
        ownCts.Cancel();
        var verification = Create((_, _) => throw new OperationCanceledException(ownCts.Token)); // connect's token is NOT cancelled

        Assert.False(verification.Verify(SampleKey));

        Assert.False(verification.VerifierCancelled);
        Assert.IsType<SshTransportException>(
            verification.MapConnectFailure(new InvalidOperationException("kex failed"), "host", 22, "generic"));
    }

    // ---- MapConnectFailure without any verifier involvement ----

    [Fact]
    public void MapConnectFailure_CallerCancelledConnect_StaysAnOperationCanceledExceptionNotWrapped()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var verification = new SshHostKeyVerification(Options(sync: _ => true), cts.Token);
        var original = new OperationCanceledException(cts.Token);

        Assert.Same(original, verification.MapConnectFailure(original, "host", 22, "generic"));
    }

    [Fact]
    public void MapConnectFailure_OtherFailure_IsAGenericSshTransportException()
    {
        var verification = new SshHostKeyVerification(Options(sync: _ => true), CancellationToken.None);
        var failure = new InvalidOperationException("bad password");

        var mapped = Assert.IsType<SshTransportException>(verification.MapConnectFailure(failure, "host", 22, "generic message"));
        Assert.Equal("generic message", mapped.Message);
        Assert.Same(failure, mapped.InnerException);
    }

    // ---- deadlock regression ----

    [Fact]
    public void Verify_AsyncVerifierAwaitingARealDelay_DoesNotDeadlockAgainstABlockedSynchronizationContext()
    {
        // Simulates SSH.NET's handler thread (or a UI thread) being blocked
        // synchronously inside Verify while carrying a SynchronizationContext
        // that would never pump again until Verify returns — the classic
        // sync-over-async deadlock. If Verify ever let the verifier capture
        // and resume on that context, the verifier's post-delay continuation
        // could never run and this would hang (bounded by Join, so it fails
        // instead).
        SynchronizationContext? contextSeenByVerifier = new BlockedSynchronizationContext();
        var verification = Create(async (_, _) =>
        {
            contextSeenByVerifier = SynchronizationContext.Current;
            await Task.Delay(100); // deliberately NOT ConfigureAwait(false) -- the way a UI consumer would write it
            return true;
        });

        var trusted = false;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new BlockedSynchronizationContext());
            trusted = verification.Verify(SampleKey);
        });
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Verify deadlocked against the caller's SynchronizationContext");
        Assert.True(trusted);
        Assert.Null(contextSeenByVerifier); // the verifier ran with no context, not the blocked caller's
    }

    /// <summary>A context whose <see cref="Post"/> queues work that never runs — a stand-in for a UI thread that's currently blocked.</summary>
    private sealed class BlockedSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state) => throw new InvalidOperationException("would block forever");
    }
}
