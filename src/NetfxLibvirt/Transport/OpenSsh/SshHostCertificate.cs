using Renci.SshNet.Security;

namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>
/// The trust-relevant facts of an OpenSSH host certificate
/// (PROTOCOL.certkeys) and this library's policy for accepting one.
///
/// **What checks the cryptography.** Not this class. By the time a
/// certificate reaches a host key verifier, SSH.NET has already (a) verified
/// the key-exchange signature against the key embedded in the certificate —
/// so the peer provably holds that key — (b) verified the certificate's
/// signature against the CA key embedded in it, and (c) checked the
/// current time against the validity window. Those are integrity checks:
/// they show the certificate is self-consistent, not that anyone trusts it.
/// (SSH.NET source: <c>CertificateHostAlgorithm.VerifySignatureBlob</c>,
/// 2026.0.0; <c>KeyExchange.ValidateExchangeHash</c> runs it, then raises
/// <c>HostKeyReceived</c> only if it passed.) Deliberately, this library
/// does not re-implement or second-guess that verification: the facts here
/// are read from the very object SSH.NET verified, so what is signature-
/// checked and what is policy-checked cannot differ (two parsers of the
/// same bytes could).
///
/// **What this class adds** — the trust policy SSH.NET leaves to the
/// consumer, in <see cref="Check"/>: HOST type, no critical options, an
/// acceptable CA signature algorithm, the validity window against an
/// injectable clock, and an explicit principal match (an empty principals
/// list matches nothing, unlike OpenSSH where it means "any host"). Which CA
/// to trust is decided by the caller, from <c>@cert-authority</c> entries,
/// by exact key match against <see cref="CertificateAuthorityKey"/>.
/// </summary>
public sealed class SshHostCertificate
{
    /// <summary>SSH_CERT_TYPE_HOST.</summary>
    public const uint HostCertificateType = 2;

    private static readonly string[] AcceptedSignatureAlgorithms =
    [
        "ssh-ed25519",
        "rsa-sha2-256",
        "rsa-sha2-512",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
    ];

    public SshHostCertificate(
        string keyId,
        ulong serial,
        uint certificateType,
        IReadOnlyList<string> principals,
        ulong validAfterUnixSeconds,
        ulong validBeforeUnixSeconds,
        IReadOnlyList<string> criticalOptionNames,
        OpenSshPublicKey certificateAuthorityKey,
        string signatureAlgorithm)
    {
        KeyId = keyId;
        Serial = serial;
        CertificateType = certificateType;
        Principals = principals;
        ValidAfterUnixSeconds = validAfterUnixSeconds;
        ValidBeforeUnixSeconds = validBeforeUnixSeconds;
        CriticalOptionNames = criticalOptionNames;
        CertificateAuthorityKey = certificateAuthorityKey;
        SignatureAlgorithm = signatureAlgorithm;
    }

    public string KeyId { get; }

    public ulong Serial { get; }

    /// <summary>1 = user, 2 = host.</summary>
    public uint CertificateType { get; }

    public IReadOnlyList<string> Principals { get; }

    /// <summary>Seconds since the Unix epoch; the certificate is valid from here (inclusive).</summary>
    public ulong ValidAfterUnixSeconds { get; }

    /// <summary>Seconds since the Unix epoch; valid until here (exclusive). <see cref="ulong.MaxValue"/> means "forever".</summary>
    public ulong ValidBeforeUnixSeconds { get; }

    public IReadOnlyList<string> CriticalOptionNames { get; }

    /// <summary>The CA key that signed the certificate, as embedded in it (exact wire blob). Trust it only if it equals a <c>@cert-authority</c> key.</summary>
    public OpenSshPublicKey CertificateAuthorityKey { get; }

    /// <summary>The signature algorithm name the CA used, e.g. <c>ssh-ed25519</c>, <c>rsa-sha2-512</c>, <c>ssh-rsa</c> (SHA-1).</summary>
    public string SignatureAlgorithm { get; }

    /// <exception cref="FormatException">The certificate's CA key or signature can't be read.</exception>
    internal static SshHostCertificate FromSshNet(Certificate certificate)
    {
        var caKey = OpenSshPublicKey.FromBlob(certificate.CertificateAuthorityKey);
        var signatureAlgorithm = SshWire.TryReadLeadingName(certificate.Signature)
            ?? throw new FormatException("The certificate's signature has no valid algorithm name.");

        return new SshHostCertificate(
            certificate.KeyId,
            certificate.Serial,
            (uint)certificate.Type,
            certificate.ValidPrincipals.ToArray(),
            certificate.ValidAfterUnixSeconds,
            certificate.ValidBeforeUnixSeconds,
            certificate.CriticalOptions.Keys.ToArray(),
            caKey,
            signatureAlgorithm);
    }

    /// <summary>Applies the policy above, given the time and host. Returns
    /// <see langword="null"/> when acceptable (CA trust is a separate
    /// decision), otherwise the first problem found, in a fixed order: type,
    /// critical options, signature algorithm, time, principals.</summary>
    /// <param name="host">The bare host name being connected to (no port — principals never carry one).</param>
    public (SshCertificateProblem Problem, string Detail)? Check(string host, TimeProvider timeProvider, bool allowSha1CaSignatures)
    {
        if (CertificateType != HostCertificateType)
        {
            return (SshCertificateProblem.WrongType, $"Certificate type is {CertificateType} ({(CertificateType == 1 ? "user" : "unknown")}), not a host certificate.");
        }

        if (CriticalOptionNames.Count > 0)
        {
            return (SshCertificateProblem.UnknownCriticalOption, $"Host certificate has critical option(s) {string.Join(", ", CriticalOptionNames)}; none are defined for host certificates.");
        }

        var acceptable = AcceptedSignatureAlgorithms.Contains(SignatureAlgorithm, StringComparer.Ordinal)
            || (allowSha1CaSignatures && SignatureAlgorithm == "ssh-rsa");
        if (!acceptable)
        {
            return (SshCertificateProblem.DisallowedSignatureAlgorithm, SignatureAlgorithm == "ssh-rsa"
                ? "The CA signed this certificate with SHA-1 (ssh-rsa), which is not accepted by default."
                : $"CA signature algorithm '{SignatureAlgorithm}' is not accepted.");
        }

        // Defense in depth: the signature algorithm must belong to the CA key's own type. SSH.NET already refuses
        // a mismatched pair (its key constructors check the blob's type name — verified against 2026.0.0), so this
        // only matters if that ever changes; it costs one comparison and can only reject.
        if (!SignatureAlgorithmMatchesKeyType(SignatureAlgorithm, CertificateAuthorityKey.KeyType))
        {
            return (SshCertificateProblem.DisallowedSignatureAlgorithm, $"CA signature algorithm '{SignatureAlgorithm}' does not belong to the CA's key type '{CertificateAuthorityKey.KeyType}'.");
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var nowSeconds = now < 0 ? 0UL : (ulong)now;
        if (nowSeconds < ValidAfterUnixSeconds)
        {
            return (SshCertificateProblem.NotYetValid, $"Certificate is not valid until {FormatTime(ValidAfterUnixSeconds)}.");
        }

        if (nowSeconds >= ValidBeforeUnixSeconds)
        {
            return (SshCertificateProblem.Expired, $"Certificate expired at {FormatTime(ValidBeforeUnixSeconds)}.");
        }

        if (!Principals.Any(p => OpenSshPattern.MatchesList(p, host)))
        {
            return (SshCertificateProblem.NoMatchingPrincipal, Principals.Count == 0
                ? $"Certificate has no principals, so it matches no host (OpenSSH would accept it for any host; this library requires an explicit match for '{host}')."
                : $"None of the certificate's principals ({string.Join(", ", Principals)}) match '{host}'.");
        }

        return null;
    }

    private static bool SignatureAlgorithmMatchesKeyType(string signatureAlgorithm, string keyType) => signatureAlgorithm switch
    {
        "rsa-sha2-256" or "rsa-sha2-512" or "ssh-rsa" => keyType == "ssh-rsa",
        _ => signatureAlgorithm == keyType, // ssh-ed25519 and ecdsa-sha2-nistpNNN name their key type exactly
    };

    /// <summary>Unix seconds → time, or <see langword="null"/> for the "no bound" values (0 / beyond year 9999).</summary>
    public static DateTimeOffset? ToDateTimeOffset(ulong unixSeconds) =>
        unixSeconds == 0 || unixSeconds > 253402300799UL ? null : DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds);

    private static string FormatTime(ulong unixSeconds) =>
        ToDateTimeOffset(unixSeconds) is { } t ? t.ToString("u") : unixSeconds.ToString();
}
