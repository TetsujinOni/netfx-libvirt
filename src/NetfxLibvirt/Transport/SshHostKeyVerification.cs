using NetfxLibvirt.Transport.OpenSsh;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

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

    /// <summary>The <see cref="SshHostKeyRejectedException"/> a verifier threw to reject with a specific reason, if any.</summary>
    public SshHostKeyRejectedException? Rejection { get; private set; }

    /// <summary>The exception a verifier threw (other than cancellation by the connect's own token), if any.</summary>
    public Exception? VerifierFault { get; private set; }

    /// <summary>Whether the connect's token was cancelled while a verifier was pending.</summary>
    public bool VerifierCancelled { get; private set; }

    /// <summary>What SSH.NET was handed for the host key: the exact wire
    /// bytes and the algorithm object it built from them.</summary>
    private sealed record HostKeyCapture(byte[] Wire, KeyHostAlgorithm Algorithm);

    // Every algorithm the factories built, not just the last: for a certificate SSH.NET reuses this same
    // dictionary to build the CA key's algorithm while verifying the certificate, so "the last capture"
    // is the CA, not the host key. Matched to the event by object identity / exact bytes instead.
    private readonly List<HostKeyCapture> _captures = [];

    private HostKeyCapture? FindCapture(Func<HostKeyCapture, bool> predicate)
    {
        lock (_captures)
        {
            return _captures.LastOrDefault(predicate);
        }
    }

    /// <summary>Whether SSH.NET got as far as raising <c>HostKeyReceived</c>
    /// (i.e. the key exchange signature — and for a certificate, its own
    /// verification — passed).</summary>
    public bool HandlerInvoked { get; private set; }

    /// <summary>
    /// Hooks <c>HostKeyReceived</c>, and wraps the connection's public
    /// <see cref="ConnectionInfo.HostKeyAlgorithms"/> factories so we see the
    /// host key exactly as the server sent it. SSH.NET's event args expose
    /// only a re-encoding (its <c>HostKey</c> round-trips key fields through
    /// <c>BigInteger</c>, which is not guaranteed to reproduce the wire
    /// bytes) and, for a certificate, a parsed object without the original
    /// bytes; the factory is handed the wire blob and returns the algorithm
    /// object the event is later built from, so wrapping it is a supported
    /// way to get both. The wrapper changes nothing about what the original
    /// factory builds or when.
    /// </summary>
    public void Attach(SshClient client)
    {
        var algorithms = client.ConnectionInfo.HostKeyAlgorithms;
        foreach (var name in algorithms.Keys.ToList())
        {
            var original = algorithms[name];
            algorithms[name] = data =>
            {
                var algorithm = original(data);
                lock (_captures)
                {
                    _captures.Add(new HostKeyCapture(data, algorithm));
                    if (_captures.Count > 16)
                    {
                        _captures.RemoveAt(0);
                    }
                }

                return algorithm;
            };
        }

        client.HostKeyReceived += (_, e) =>
        {
            HandlerInvoked = true;
            SshHostKeyInfo info;
            try
            {
                info = BuildHostKeyInfo(e);
            }
            catch (Exception ex)
            {
                // Can't even describe the key (e.g. an unreadable certificate): refuse it, and say why.
                VerifierFault = ex;
                e.CanTrust = false;
                return;
            }

            e.CanTrust = Verify(info);
        };
    }

    private SshHostKeyInfo BuildHostKeyInfo(HostKeyEventArgs e)
    {
        if (e.Certificate is { } certificate)
        {
            // The wire bytes of THIS certificate: the capture whose algorithm holds this very Certificate object.
            var capture = FindCapture(c => c.Algorithm is CertificateHostAlgorithm a && ReferenceEquals(a.Certificate, certificate));

            return new SshHostKeyInfo(e.HostKeyName, e.KeyLength, e.FingerPrintSHA256, e.HostKey)
            {
                Certificate = SshHostCertificate.FromSshNet(certificate),
                CertificateBlob = capture?.Wire,
            };
        }

        // A plain key: the capture whose algorithm is this key (same name, and it re-encodes to the event's bytes).
        var plain = FindCapture(c => c.Algorithm is not CertificateHostAlgorithm && c.Algorithm.Name == e.HostKeyName && c.Algorithm.Data.AsSpan().SequenceEqual(e.HostKey));

        return plain is not null
            ? new SshHostKeyInfo(e.HostKeyName, e.KeyLength, OpenSshPublicKey.FingerprintOf(plain.Wire)["SHA256:".Length..], plain.Wire)
            : new SshHostKeyInfo(e.HostKeyName, e.KeyLength, e.FingerPrintSHA256, e.HostKey);
    }

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
        catch (SshHostKeyRejectedException rejection)
        {
            // A verifier rejecting with a specific reason (e.g. OpenSshHostKeyVerifier) — surface it as thrown.
            RejectedKey = hostKey;
            Rejection = rejection;
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

        if (Rejection is not null)
        {
            return Rejection;
        }

        if (RejectedKey is not null)
        {
            return new SshHostKeyRejectedException(RejectedKey, host, port, failure);
        }

        if (!HandlerInvoked && DescribeCertificateRejectedDuringKeyExchange(host, port, failure) is { } certificateFailure)
        {
            return certificateFailure;
        }

        return new SshTransportException(genericMessage, failure);
    }

    /// <summary>
    /// SSH.NET verifies a host certificate (key-exchange signature, CA
    /// signature, validity window) BEFORE raising <c>HostKeyReceived</c>, and
    /// fails the connect with a generic "host key could not be verified" if
    /// any of it fails — so the verifier never sees such a certificate. But
    /// the certificate reached the algorithm factory (see <see cref="Attach"/>),
    /// so this names the cause where it can be told from public facts: a
    /// validity window that excludes now is Expired / NotYetValid; anything
    /// else (a bad CA signature, a key-exchange signature that doesn't match
    /// the certified key) is the honest umbrella
    /// <see cref="SshCertificateProblem.VerificationFailed"/> — we don't
    /// re-run SSH.NET's cryptography to pick which. This only ever explains
    /// a failure; it can't cause a connection to be accepted.
    /// </summary>
    private SshHostKeyRejectedException? DescribeCertificateRejectedDuringKeyExchange(string host, int port, Exception failure)
    {
        var capture = FindCapture(c => c.Algorithm is CertificateHostAlgorithm);
        if (capture is null)
        {
            return null;
        }

        var algorithm = (CertificateHostAlgorithm)capture.Algorithm;
        var certificate = algorithm.Certificate;
        var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        var nowSeconds = now < 0 ? 0UL : (ulong)now;

        SshCertificateProblem problem;
        string detail;
        if (nowSeconds < certificate.ValidAfterUnixSeconds)
        {
            problem = SshCertificateProblem.NotYetValid;
            detail = $"Certificate is not valid until {SshHostCertificate.ToDateTimeOffset(certificate.ValidAfterUnixSeconds):u}.";
        }
        else if (nowSeconds > certificate.ValidBeforeUnixSeconds)
        {
            problem = SshCertificateProblem.Expired;
            detail = $"Certificate expired at {SshHostCertificate.ToDateTimeOffset(certificate.ValidBeforeUnixSeconds):u}.";
        }
        else
        {
            problem = SshCertificateProblem.VerificationFailed;
            detail = "The SSH library rejected the certificate during key exchange: invalid CA signature, or the key-exchange signature doesn't match the certified key.";
        }

        var rawKey = SshCertificateWire.TryGetEmbeddedKeyBlob(capture.Wire, out var embedded) ? embedded : capture.Wire;
        var info = new SshHostKeyInfo(algorithm.Name, certificate.Key.KeyLength, OpenSshPublicKey.FingerprintOf(rawKey)["SHA256:".Length..], rawKey)
        {
            CertificateBlob = capture.Wire,
        };

        return new SshHostKeyRejectedException(info, host, port, SshHostKeyRejectionReason.CertificateInvalid, detail, failure)
        {
            CertificateProblem = problem,
        };
    }
}
