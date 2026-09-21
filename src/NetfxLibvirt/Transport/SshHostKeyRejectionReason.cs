namespace NetfxLibvirt.Transport;

/// <summary>Why a host key was rejected — see <see cref="SshHostKeyRejectedException.Reason"/>.</summary>
public enum SshHostKeyRejectionReason
{
    /// <summary>A custom verifier returned <see langword="false"/> without giving a reason.</summary>
    RejectedByVerifier,

    /// <summary>The host has a known key of the same type in <c>known_hosts</c>, and the server presented a different one — the classic "REMOTE HOST IDENTIFICATION HAS CHANGED" case. See <see cref="SshHostKeyRejectedException.KnownHostsFile"/>/<see cref="SshHostKeyRejectedException.KnownHostsLine"/> and <see cref="SshHostKeyRejectedException.ExpectedFingerprint"/>.</summary>
    ChangedKey,

    /// <summary>The key (or the CA, or the certificate) is listed under <c>@revoked</c>.</summary>
    Revoked,

    /// <summary>The server presented a certificate that failed validation; see <see cref="SshHostKeyRejectedException.CertificateProblem"/>.</summary>
    CertificateInvalid,

    /// <summary>A <c>@cert-authority</c> entry covers this host, so a certificate is required, but the server presented a plain key (no fallback to trust-on-first-use).</summary>
    PlainKeyWhereCertificateRequired,

    /// <summary>The user was asked and declined.</summary>
    UserDeclined,

    /// <summary>The key or CA is new, and no prompt was configured to ask about it.</summary>
    UnknownAndNoPrompt,
}

/// <summary>What was wrong with a rejected host certificate.</summary>
public enum SshCertificateProblem
{
    /// <summary>The certificate couldn't be interpreted (or none was available to check).</summary>
    Malformed,

    /// <summary>It isn't a HOST certificate (e.g. a user certificate).</summary>
    WrongType,

    /// <summary>It carries critical options; none are defined for host certificates, so any is unknown.</summary>
    UnknownCriticalOption,

    /// <summary>Signed with an algorithm this library doesn't accept for CA signatures (SHA-1 <c>ssh-rsa</c> unless opted in, <c>ssh-dss</c>, anything unlisted).</summary>
    DisallowedSignatureAlgorithm,

    /// <summary>The current time is past the certificate's <c>valid_before</c>.</summary>
    Expired,

    /// <summary>The current time is before the certificate's <c>valid_after</c>.</summary>
    NotYetValid,

    /// <summary>No principal matches the host (an empty principals list matches nothing — stricter than OpenSSH).</summary>
    NoMatchingPrincipal,

    /// <summary>The signing CA isn't one of the <c>@cert-authority</c> keys trusted for this host.</summary>
    UntrustedCertificateAuthority,

    /// <summary>The SSH library rejected the certificate during key exchange for a reason other than its validity window — an invalid CA signature, a key-exchange signature that doesn't match the certified key, or a key it couldn't use. The certificate never reached the verifier.</summary>
    VerificationFailed,
}
