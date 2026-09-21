namespace NetfxLibvirt.Transport;

/// <summary>Thrown by <see cref="SshTransport.ConnectAsync"/> and
/// <see cref="SshPortForward.OpenAsync"/> when the server's host key (or
/// host certificate) was not accepted — by the caller's verifier
/// (<see cref="SshTransportOptions.VerifyHostKey"/> /
/// <see cref="SshTransportOptions.VerifyHostKeyAsync"/>), or, for a
/// certificate, by the SSH library's own verification during key exchange.
/// A distinct, catchable signal, so a consumer doesn't have to sniff a
/// generic connection failure or record the reason in its own verifier
/// closure. Derives from <see cref="SshTransportException"/>, so existing
/// <c>catch (SshTransportException)</c> handlers still see it.
/// <see cref="Reason"/> says why; <see cref="HostKey"/> is the exact key that
/// was rejected (e.g. to show in a "host key changed" warning).
///
/// A custom <see cref="AsyncSshHostKeyVerifier"/> may throw this itself to
/// reject with a specific reason — it's surfaced to the caller as thrown.</summary>
public sealed class SshHostKeyRejectedException : SshTransportException
{
    public SshHostKeyRejectedException(SshHostKeyInfo hostKey, string host, int port, Exception? innerException = null)
        : this(hostKey, host, port, SshHostKeyRejectionReason.RejectedByVerifier, detail: null, innerException)
    {
    }

    public SshHostKeyRejectedException(
        SshHostKeyInfo hostKey, string host, int port, SshHostKeyRejectionReason reason, string? detail = null, Exception? innerException = null)
        : base(BuildMessage(hostKey, host, port, reason, detail), innerException!)
    {
        HostKey = hostKey;
        Host = host;
        Port = port;
        Reason = reason;
        Detail = detail;
    }

    /// <summary>The host key the server presented, which was rejected.</summary>
    public SshHostKeyInfo HostKey { get; }

    public string Host { get; }

    public int Port { get; }

    public SshHostKeyRejectionReason Reason { get; }

    /// <summary>Human-readable specifics (never needed to act on <see cref="Reason"/>).</summary>
    public string? Detail { get; }

    /// <summary>For <see cref="SshHostKeyRejectionReason.CertificateInvalid"/>: what was wrong.</summary>
    public SshCertificateProblem? CertificateProblem { get; init; }

    /// <summary>For <see cref="SshHostKeyRejectionReason.ChangedKey"/> and <see cref="SshHostKeyRejectionReason.Revoked"/>: the <c>known_hosts</c> file holding the offending entry.</summary>
    public string? KnownHostsFile { get; init; }

    /// <summary>For <see cref="SshHostKeyRejectionReason.ChangedKey"/> and <see cref="SshHostKeyRejectionReason.Revoked"/>: its 1-based line number, as <c>ssh</c> reports it.</summary>
    public int? KnownHostsLine { get; init; }

    /// <summary>For <see cref="SshHostKeyRejectionReason.ChangedKey"/>: the <c>SHA256:</c> fingerprint of the key that was expected (the one in <c>known_hosts</c>).</summary>
    public string? ExpectedFingerprint { get; init; }

    private static string BuildMessage(SshHostKeyInfo hostKey, string host, int port, SshHostKeyRejectionReason reason, string? detail)
    {
        var what = hostKey.IsCertificate ? "host certificate" : "host key";
        var prefix = $"The SSH {what} presented by {host}:{port} ({hostKey.AlgorithmName}, SHA256:{hostKey.Sha256Fingerprint})";
        var why = reason switch
        {
            SshHostKeyRejectionReason.RejectedByVerifier => "was rejected by the host key verifier.",
            SshHostKeyRejectionReason.ChangedKey => "does not match the key on record for this host — possible machine-in-the-middle, or the host was reinstalled.",
            SshHostKeyRejectionReason.Revoked => "is revoked.",
            SshHostKeyRejectionReason.CertificateInvalid => "was rejected: the certificate is not valid for this host.",
            SshHostKeyRejectionReason.PlainKeyWhereCertificateRequired => "was rejected: a certificate authority is trusted for this host, so a certificate is required, but a plain key was presented.",
            SshHostKeyRejectionReason.UserDeclined => "was not trusted by the user.",
            SshHostKeyRejectionReason.UnknownAndNoPrompt => "is not known, and no prompt is configured to ask whether to trust it.",
            _ => "was rejected.",
        };

        return detail is null ? $"{prefix} {why}" : $"{prefix} {why} {detail}";
    }
}
