using System.Collections.Concurrent;

namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>The host, port, and key a prompt is asking about.</summary>
/// <param name="KeyType">The key type of the key in question — the host key, or for a CA prompt the CA's key (e.g. <c>ssh-ed25519</c>).</param>
/// <param name="Fingerprint">Its fingerprint as <c>ssh-keygen -l</c> prints it: <c>SHA256:</c> + unpadded base64.</param>
public record OpenSshPromptContext(string Host, int Port, string KeyType, string Fingerprint);

/// <summary>"This host presented a key we have no record of. Trust it?" — the classic first-connection question.</summary>
public sealed record NewHostKeyContext(string Host, int Port, string KeyType, string Fingerprint)
    : OpenSshPromptContext(Host, Port, KeyType, Fingerprint);

/// <summary>"This host presented a certificate signed by a CA we don't know for this host. Trust the CA for this host?"
/// <see cref="OpenSshPromptContext.KeyType"/> and <see cref="OpenSshPromptContext.Fingerprint"/> are the CA's — what
/// gets recorded on yes. The rest describes the certificate that prompted the question (already checked: HOST type,
/// valid now, issued for this host).</summary>
/// <param name="ValidAfter">The certificate's start of validity, or <see langword="null"/> if unbounded.</param>
/// <param name="ValidBefore">Its end of validity, or <see langword="null"/> if unbounded.</param>
public sealed record NewCertificateAuthorityContext(
    string Host,
    int Port,
    string KeyType,
    string Fingerprint,
    string CertificateKeyId,
    ulong CertificateSerial,
    IReadOnlyList<string> CertificatePrincipals,
    DateTimeOffset? ValidAfter,
    DateTimeOffset? ValidBefore)
    : OpenSshPromptContext(Host, Port, KeyType, Fingerprint);

/// <summary>What <see cref="OpenSshHostKeyVerifier"/> asks a person. Return <see langword="true"/> to trust. Called
/// at most once at a time per <c>known_hosts</c> file; honor <paramref name="cancellationToken"/> (cancelling the
/// connect is how a pending prompt is dismissed).</summary>
public interface IOpenSshHostKeyPrompt
{
    ValueTask<bool> ConfirmNewHostKeyAsync(NewHostKeyContext context, CancellationToken cancellationToken);

    ValueTask<bool> ConfirmNewCertificateAuthorityAsync(NewCertificateAuthorityContext context, CancellationToken cancellationToken);
}

public enum KnownHostsHashing
{
    /// <summary>Hash new entries iff the file already contains hashed entries (keeps a <c>HashKnownHosts yes</c> file consistent).</summary>
    MatchFile,

    Never,

    Always,
}

public sealed record OpenSshHostKeyVerifierOptions
{
    /// <summary>The name being connected to, as the user typed it (matched against <c>known_hosts</c> and certificate principals; not resolved).
    /// **Must be the same <see cref="SshTransportOptions.Host"/> the connection uses** — this is a separate options record, and trust decisions
    /// apply to the name given here, not to whatever is actually dialed. Only host-name / address characters are accepted (it is written into
    /// <c>known_hosts</c>).</summary>
    public required string Host { get; init; }

    /// <summary>Must be the same as <see cref="SshTransportOptions.Port"/>.</summary>
    public int Port { get; init; } = 22;

    /// <summary>The file trust decisions are read from and appended to — same meaning as ssh_config's <c>UserKnownHostsFile</c>. Default <see cref="OpenSshKnownHosts.DefaultUserFilePath"/>.</summary>
    public string? UserKnownHostsFile { get; init; }

    /// <summary>Read-only extra files (ssh's <c>GlobalKnownHostsFile</c>). Default <see cref="OpenSshKnownHosts.DefaultGlobalFilePaths"/>; pass an empty list for none.</summary>
    public IReadOnlyList<string>? GlobalKnownHostsFiles { get; init; }

    /// <summary>When any <c>@cert-authority</c> entry covers the host, a plain key from that host is REJECTED
    /// (<see cref="SshHostKeyRejectionReason.PlainKeyWhereCertificateRequired"/>) instead of falling back to trust-on-first-use
    /// — otherwise anyone able to impersonate the host could simply decline to present a certificate. Default
    /// <see langword="true"/>. A presented certificate that fails validation is rejected regardless.</summary>
    public bool RequireCertificateWhenCaCovers { get; init; } = true;

    /// <summary>The clock for certificate validity. Default <see cref="TimeProvider.System"/>. (SSH.NET applies its own
    /// real-clock check first; this can only make acceptance stricter, not looser.)</summary>
    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Allow CA signatures made with SHA-1 (<c>ssh-rsa</c>). Default <see langword="false"/>, matching OpenSSH ≥ 8.2.</summary>
    public bool AllowSha1CaSignatures { get; init; }

    /// <summary>Asked about new keys and new CAs. If <see langword="null"/>, anything not already trusted is rejected
    /// with <see cref="SshHostKeyRejectionReason.UnknownAndNoPrompt"/>.</summary>
    public IOpenSshHostKeyPrompt? Prompt { get; init; }

    public KnownHostsHashing Hashing { get; init; } = KnownHostsHashing.MatchFile;
}

/// <summary>Whether connecting to a host is expected to need to ask anyone anything — see <see cref="OpenSshHostKeyVerifier.GetTrustState(OpenSshHostKeyVerifierOptions)"/>.</summary>
public enum TrustState
{
    /// <summary>No entry of any kind covers the host: a first connection, which will prompt (if a prompt is configured).</summary>
    Unknown,

    /// <summary>A plain key is on record for the host. Won't prompt if the server offers a key type with an entry; it
    /// still can if it offers a type with none (OpenSSH semantics: only same-type mismatches are "changed").</summary>
    Known,

    /// <summary>A <c>@cert-authority</c> entry covers the host: never prompts (a valid certificate is accepted silently, anything else rejected).</summary>
    CaCovered,
}

/// <summary>
/// OpenSSH-standard host verification as an <see cref="AsyncSshHostKeyVerifier"/>:
/// reads/appends the user's real <c>known_hosts</c> (no bespoke trust store),
/// with OpenSSH's semantics for plain keys, <c>@cert-authority</c> host
/// certificates and <c>@revoked</c> keys, and asks the supplied
/// <see cref="IOpenSshHostKeyPrompt"/> only where <c>ssh</c> would ask.
///
/// Decision table (a rejection throws <see cref="SshHostKeyRejectedException"/> with the <see cref="SshHostKeyRejectionReason"/>):
/// <list type="bullet">
/// <item>Any presented key/CA/certificate listed under <c>@revoked</c> for this host → <c>Revoked</c>.</item>
/// <item><b>Plain key</b>: if a CA covers the host and <see cref="OpenSshHostKeyVerifierOptions.RequireCertificateWhenCaCovers"/> →
/// <c>PlainKeyWhereCertificateRequired</c>. Else by entry: exact key on record → accept; a different key of the same type →
/// <c>ChangedKey</c> (file:line, expected fingerprint); only other key types or nothing → ask
/// <see cref="IOpenSshHostKeyPrompt.ConfirmNewHostKeyAsync"/>, and on yes append the key.</item>
/// <item><b>Certificate</b>: must be a HOST certificate, no critical options, acceptable CA signature algorithm, valid now,
/// with an explicit matching principal — else <c>CertificateInvalid</c>. If CAs cover the host, its CA must be one of them
/// (exact key match) → accept; else <c>CertificateInvalid/UntrustedCertificateAuthority</c>. If none covers the host, ask
/// <see cref="IOpenSshHostKeyPrompt.ConfirmNewCertificateAuthorityAsync"/> and on yes append
/// <c>@cert-authority &lt;host&gt; &lt;type&gt; &lt;base64&gt;</c>. The certificate itself, or the key it certifies, is
/// never pinned.</item>
/// </list>
///
/// Decisions that need a prompt are serialized per <c>known_hosts</c> file within the process, and re-evaluated after the
/// lock is acquired, so concurrent handshakes to one unknown host prompt once. If recording a new entry fails (read-only
/// file, permissions) the connect fails — trust is never granted without being recorded. Two separate processes appending
/// at the same instant each land an intact line but may both prompt.
/// </summary>
public static class OpenSshHostKeyVerifier
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static AsyncSshHostKeyVerifier Create(OpenSshHostKeyVerifierOptions options)
    {
        Validate(options);
        return (hostKey, cancellationToken) => VerifyAsync(options, hostKey, cancellationToken);
    }

    /// <summary>Cheap preflight over the same files <see cref="Create"/> reads, so a consumer can raise
    /// <see cref="SshTransportOptions.ConnectTimeout"/> only when a person may be asked (<see cref="TrustState.Unknown"/>
    /// or <see cref="TrustState.Known"/>) — see <see cref="TrustState"/> for the caveat.</summary>
    public static TrustState GetTrustState(OpenSshHostKeyVerifierOptions options)
    {
        Validate(options);
        var known = Load(options);
        if (known.FindCertificateAuthorities(options.Host, options.Port).Count > 0)
        {
            return TrustState.CaCovered;
        }

        return known.HasPlainEntries(options.Host, options.Port) ? TrustState.Known : TrustState.Unknown;
    }

    public static TrustState GetTrustState(string host, int port = 22) =>
        GetTrustState(new OpenSshHostKeyVerifierOptions { Host = host, Port = port });

    private static void Validate(OpenSshHostKeyVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);

        // The host is written into known_hosts on trust. Anything that isn't a plausible host name / address
        // literal (whitespace, newlines, ',' '|' '*' '?' '!' '[' ']' ...) could alter the meaning of the line it lands
        // in — e.g. smuggle in a second entry — so refuse it up front rather than escape it.
        foreach (var c in options.Host)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '-' or '_' or ':' or '%'))
            {
                throw new ArgumentException($"Host contains a character ('{(char.IsControl(c) ? $"U+{(int)c:X4}" : c.ToString())}') that isn't valid in a host name or address.", nameof(options));
            }
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, 65535);
    }

    private static string UserFile(OpenSshHostKeyVerifierOptions options) =>
        Path.GetFullPath(options.UserKnownHostsFile ?? OpenSshKnownHosts.DefaultUserFilePath);

    private static OpenSshKnownHosts Load(OpenSshHostKeyVerifierOptions options) =>
        OpenSshKnownHosts.Load([UserFile(options), .. options.GlobalKnownHostsFiles ?? OpenSshKnownHosts.DefaultGlobalFilePaths]);

    // ---- the decision ----

    private abstract record Decision;

    private sealed record Accept : Decision;

    private sealed record AskAboutKey(OpenSshPublicKey Key) : Decision;

    private sealed record AskAboutCa(SshHostCertificate Certificate) : Decision;

    private static async ValueTask<bool> VerifyAsync(OpenSshHostKeyVerifierOptions options, SshHostKeyInfo hostKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Evaluate(options, Load(options), hostKey) is Accept)
        {
            return true;
        }

        // Needs a person. One at a time per file; look again once we hold the lock — someone else's answer may have settled it.
        var gate = FileLocks.GetOrAdd(UserFile(options), static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = Load(options);
            var decision = Evaluate(options, known, hostKey);

            switch (decision)
            {
                case Accept:
                    return true;

                case AskAboutKey ask:
                {
                    if (options.Prompt is null)
                    {
                        throw Reject(options, hostKey, SshHostKeyRejectionReason.UnknownAndNoPrompt);
                    }

                    var context = new NewHostKeyContext(options.Host, options.Port, ask.Key.KeyType, ask.Key.Sha256Fingerprint);
                    if (!await options.Prompt.ConfirmNewHostKeyAsync(context, cancellationToken).ConfigureAwait(false))
                    {
                        throw Reject(options, hostKey, SshHostKeyRejectionReason.UserDeclined);
                    }

                    Record(options, known, ask.Key, KnownHostsMarker.None);
                    return true;
                }

                case AskAboutCa ask:
                {
                    if (options.Prompt is null)
                    {
                        throw Reject(options, hostKey, SshHostKeyRejectionReason.UnknownAndNoPrompt);
                    }

                    var certificate = ask.Certificate;
                    var context = new NewCertificateAuthorityContext(
                        options.Host,
                        options.Port,
                        certificate.CertificateAuthorityKey.KeyType,
                        certificate.CertificateAuthorityKey.Sha256Fingerprint,
                        certificate.KeyId,
                        certificate.Serial,
                        certificate.Principals,
                        SshHostCertificate.ToDateTimeOffset(certificate.ValidAfterUnixSeconds),
                        SshHostCertificate.ToDateTimeOffset(certificate.ValidBeforeUnixSeconds));
                    if (!await options.Prompt.ConfirmNewCertificateAuthorityAsync(context, cancellationToken).ConfigureAwait(false))
                    {
                        throw Reject(options, hostKey, SshHostKeyRejectionReason.UserDeclined);
                    }

                    Record(options, known, certificate.CertificateAuthorityKey, KnownHostsMarker.CertificateAuthority);
                    return true;
                }

                default:
                    throw new InvalidOperationException("Unreachable decision.");
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Record(OpenSshHostKeyVerifierOptions options, OpenSshKnownHosts known, OpenSshPublicKey key, KnownHostsMarker marker)
    {
        var file = UserFile(options);
        var hash = options.Hashing switch
        {
            KnownHostsHashing.Always => true,
            KnownHostsHashing.Never => false,
            _ => known.FileUsesHashing(file),
        };

        OpenSshKnownHosts.Append(file, options.Host, options.Port, key, marker, hash);
    }

    private static Decision Evaluate(OpenSshHostKeyVerifierOptions options, OpenSshKnownHosts known, SshHostKeyInfo hostKey) =>
        hostKey.IsCertificate ? EvaluateCertificate(options, known, hostKey) : EvaluatePlainKey(options, known, hostKey);

    private static Decision EvaluatePlainKey(OpenSshHostKeyVerifierOptions options, OpenSshKnownHosts known, SshHostKeyInfo hostKey)
    {
        OpenSshPublicKey key;
        try
        {
            key = OpenSshPublicKey.FromBlob(hostKey.RawKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The host key blob presented by the SSH library is not a valid SSH public key.", ex);
        }

        var check = known.CheckKey(options.Host, options.Port, key);
        if (check.Outcome == KnownHostKeyOutcome.Revoked)
        {
            throw Reject(options, hostKey, SshHostKeyRejectionReason.Revoked, entry: check.Entry);
        }

        if (options.RequireCertificateWhenCaCovers && known.FindCertificateAuthorities(options.Host, options.Port).Count > 0)
        {
            throw Reject(options, hostKey, SshHostKeyRejectionReason.PlainKeyWhereCertificateRequired);
        }

        return check.Outcome switch
        {
            KnownHostKeyOutcome.Known => new Accept(),
            KnownHostKeyOutcome.Changed => throw Reject(options, hostKey, SshHostKeyRejectionReason.ChangedKey, entry: check.Entry),
            _ => new AskAboutKey(key),
        };
    }

    private static Decision EvaluateCertificate(OpenSshHostKeyVerifierOptions options, OpenSshKnownHosts known, SshHostKeyInfo hostKey)
    {
        var certificate = hostKey.Certificate
            ?? throw Reject(options, hostKey, SshHostKeyRejectionReason.CertificateInvalid, SshCertificateProblem.Malformed, "The certificate could not be read.");

        // Revocation is deny-only, so cast the net wide: the CA, the certificate as sent, and the key it certifies
        // (both as sliced from the wire certificate and as re-encoded by the SSH library).
        var revoked = known.FindRevocation(options.Host, options.Port, certificate.CertificateAuthorityKey.Blob.Span);
        if (revoked is null && hostKey.CertificateBlob is { } blob)
        {
            revoked = known.FindRevocation(options.Host, options.Port, blob);
            if (revoked is null && SshCertificateWire.TryGetEmbeddedKeyBlob(blob, out var embedded))
            {
                revoked = known.FindRevocation(options.Host, options.Port, embedded);
            }
        }

        revoked ??= known.FindRevocation(options.Host, options.Port, hostKey.RawKey);
        if (revoked is not null)
        {
            throw Reject(options, hostKey, SshHostKeyRejectionReason.Revoked, entry: revoked);
        }

        if (certificate.Check(options.Host, options.TimeProvider ?? TimeProvider.System, options.AllowSha1CaSignatures) is var (problem, detail))
        {
            throw Reject(options, hostKey, SshHostKeyRejectionReason.CertificateInvalid, problem, detail);
        }

        var covering = known.FindCertificateAuthorities(options.Host, options.Port);
        if (covering.Count == 0)
        {
            return new AskAboutCa(certificate);
        }

        if (covering.Any(e => e.Key.Equals(certificate.CertificateAuthorityKey)))
        {
            return new Accept();
        }

        throw Reject(
            options,
            hostKey,
            SshHostKeyRejectionReason.CertificateInvalid,
            SshCertificateProblem.UntrustedCertificateAuthority,
            $"The certificate was signed by CA {certificate.CertificateAuthorityKey.Sha256Fingerprint}, which is not among the CAs trusted for this host.");
    }

    private static SshHostKeyRejectedException Reject(
        OpenSshHostKeyVerifierOptions options,
        SshHostKeyInfo hostKey,
        SshHostKeyRejectionReason reason,
        SshCertificateProblem? problem = null,
        string? detail = null,
        OpenSshKnownHostsEntry? entry = null)
    {
        if (entry is not null)
        {
            detail = reason == SshHostKeyRejectionReason.ChangedKey
                ? $"Offending entry: {entry.Location} (expected {entry.Key.Sha256Fingerprint})."
                : $"Listed at {entry.Location}.";
        }

        return new SshHostKeyRejectedException(hostKey, options.Host, options.Port, reason, detail)
        {
            CertificateProblem = problem,
            KnownHostsFile = entry?.SourceFile,
            KnownHostsLine = entry?.LineNumber,
            ExpectedFingerprint = reason == SshHostKeyRejectionReason.ChangedKey ? entry?.Key.Sha256Fingerprint : null,
        };
    }
}
