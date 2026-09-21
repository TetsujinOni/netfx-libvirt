using Renci.SshNet;

namespace NetfxLibvirt.Transport;

/// <summary>
/// Bridges the caller's <see cref="SshHostKeyVerifier"/> /
/// <see cref="AsyncSshHostKeyVerifier"/> onto SSH.NET's synchronous
/// <c>HostKeyReceived</c> event, and remembers *why* a handshake failed so
/// the connect call can surface the real reason
/// (<see cref="SshHostKeyRejectedException"/>, <see cref="OperationCanceledException"/>,
/// a verifier fault) instead of SSH.NET's generic "key exchange failed".
///
/// The event handler runs on SSH.NET's message-listener thread and must
/// return the trust decision synchronously, so an async verifier is blocked
/// on — safely: it's started with <see cref="Task.Run(Func{Task})"/> so it
/// runs on the thread pool with no <see cref="SynchronizationContext"/>,
/// meaning nothing it awaits can ever need the thread this is blocking (the
/// classic sync-over-async deadlock against a UI context). And the wait
/// itself is cancellable: cancelling the connect's token stops waiting
/// immediately, even for a verifier that ignores the token.
/// </summary>
internal sealed class SshHostKeyVerification
{
    private readonly SshHostKeyVerifier? _sync;
    private readonly AsyncSshHostKeyVerifier? _async;
    private readonly CancellationToken _cancellationToken;

    /// <exception cref="ArgumentException">Neither or both verifiers are set.</exception>
    public SshHostKeyVerification(SshTransportOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.VerifyHostKey is null && options.VerifyHostKeyAsync is null)
        {
            throw new ArgumentException(
                $"Exactly one of {nameof(SshTransportOptions.VerifyHostKey)} or {nameof(SshTransportOptions.VerifyHostKeyAsync)} must be set — this library never silently accepts an unverified host key.",
                nameof(options));
        }

        if (options.VerifyHostKey is not null && options.VerifyHostKeyAsync is not null)
        {
            throw new ArgumentException(
                $"Set only one of {nameof(SshTransportOptions.VerifyHostKey)} or {nameof(SshTransportOptions.VerifyHostKeyAsync)}, not both — which one should decide?",
                nameof(options));
        }

        _sync = options.VerifyHostKey;
        _async = options.VerifyHostKeyAsync;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The key a verifier explicitly rejected (returned <see langword="false"/> for), if any.</summary>
    public SshHostKeyInfo? RejectedKey { get; private set; }

    /// <summary>The exception a verifier threw (other than cancellation by the connect's own token), if any.</summary>
    public Exception? VerifierFault { get; private set; }

    /// <summary>Whether the connect's token was cancelled while a verifier was pending.</summary>
    public bool VerifierCancelled { get; private set; }

    public void Attach(SshClient client) =>
        client.HostKeyReceived += (_, e) =>
            e.CanTrust = Verify(new SshHostKeyInfo(e.HostKeyName, e.KeyLength, e.FingerPrintSHA256, e.HostKey));

    /// <summary>Runs the configured verifier and returns whether to trust
    /// <paramref name="hostKey"/>. Never throws — any failure means "don't
    /// trust", recorded in <see cref="VerifierFault"/> /
    /// <see cref="VerifierCancelled"/> for <see cref="MapConnectFailure"/>.</summary>
    public bool Verify(SshHostKeyInfo hostKey)
    {
        try
        {
            var trusted = _async is not null ? RunAsync(_async, hostKey) : _sync!(hostKey);
            if (!trusted)
            {
                RejectedKey = hostKey;
            }

            return trusted;
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            VerifierCancelled = true;
            return false;
        }
        catch (Exception ex)
        {
            VerifierFault = ex;
            return false;
        }
    }

    private bool RunAsync(AsyncSshHostKeyVerifier verifier, SshHostKeyInfo hostKey)
    {
        var task = Task.Run(() => verifier(hostKey, _cancellationToken).AsTask());

        // If we stop waiting because the connect was cancelled, a verifier
        // that later faults shouldn't surface as an unobserved task exception.
        _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        Task.WaitAny([task], _cancellationToken);
        return task.GetAwaiter().GetResult();
    }

    /// <summary>Turns a failed connect into the exception the caller should
    /// actually see — real cancellation stays an
    /// <see cref="OperationCanceledException"/> (not re-wrapped as an auth
    /// failure), a rejected key becomes <see cref="SshHostKeyRejectedException"/>,
    /// a faulted verifier becomes an <see cref="SshTransportException"/>
    /// naming the verifier, and anything else is an SSH-layer failure.</summary>
    public Exception MapConnectFailure(Exception failure, string host, int port, string genericMessage)
    {
        if (VerifierCancelled || (failure is OperationCanceledException && _cancellationToken.IsCancellationRequested))
        {
            return failure as OperationCanceledException ?? new OperationCanceledException(_cancellationToken);
        }

        if (VerifierFault is not null)
        {
            return new SshTransportException(
                $"The host key verifier for {host}:{port} threw {VerifierFault.GetType().Name}: {VerifierFault.Message}", VerifierFault);
        }

        if (RejectedKey is not null)
        {
            return new SshHostKeyRejectedException(RejectedKey, host, port, failure);
        }

        return new SshTransportException(genericMessage, failure);
    }
}
