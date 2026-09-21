using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Transport;

/// <summary>A remote SSH server's host key, in a form independent of
/// whichever SSH library <see cref="SshTransport"/> happens to use
/// underneath — so a future library swap (already happened once; see
/// <c>docs/plan.md</c> story 11) never breaks a caller's
/// <see cref="SshHostKeyVerifier"/> implementation.</summary>
/// <param name="AlgorithmName">The negotiated host key algorithm, e.g.
/// <c>ssh-ed25519</c>, <c>rsa-sha2-512</c>, or
/// <c>ssh-ed25519-cert-v01@openssh.com</c> when the server presented a
/// certificate.</param>
/// <param name="KeyLengthBits">The key length in bits.</param>
/// <param name="Sha256Fingerprint">The SHA-256 fingerprint in the same
/// format <c>ssh-keygen -l -E sha256</c> prints (non-padded base64, no
/// <c>SHA256:</c> prefix) — the form most pinning/TOFU stores compare
/// against. For a certificate this is the fingerprint of the key the
/// certificate certifies, exactly as <c>ssh-keygen -l</c> prints it for a
/// certificate file.</param>
/// <param name="RawKey">The host key blob exactly as the server sent it
/// (SSH wire format), for callers that need more than the fingerprint (e.g.
/// writing a <c>known_hosts</c>-style entry). For a certificate this is the
/// certified key, not the certificate — see <see cref="CertificateBlob"/>.</param>
public sealed record SshHostKeyInfo(string AlgorithmName, int KeyLengthBits, string Sha256Fingerprint, byte[] RawKey)
{
    /// <summary>Non-<see langword="null"/> when the server presented an OpenSSH
    /// host certificate: the facts of the certificate that SSH.NET already
    /// verified cryptographically (see <see cref="SshHostCertificate"/>). A
    /// verifier should treat a certificate as authenticating the host only
    /// after checking its CA is trusted and it's issued for this host.</summary>
    public SshHostCertificate? Certificate { get; init; }

    /// <summary>The certificate exactly as the server sent it (wire format),
    /// for audit/logging (e.g. saving it for <c>ssh-keygen -L -f</c>). Never
    /// used to make trust decisions here.</summary>
    public byte[]? CertificateBlob { get; init; }

    /// <summary>Whether the server presented a certificate rather than a bare key.</summary>
    public bool IsCertificate => Certificate is not null || CertificateBlob is not null || SshCertificateWire.IsCertificateAlgorithm(AlgorithmName);
}
