using NetfxLibvirt.Transport;
using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>The certificate POLICY (<see cref="SshHostCertificate.Check"/>) over real
/// <c>ssh-keygen</c>-issued certificates, read through SSH.NET's own parser — the same
/// object the transport builds policy from. The cryptography (KEX signature, CA
/// signature) is SSH.NET's and is covered by the real-handshake tests in
/// <c>Integration/OpenSshHostCertificateIntegrationTests</c>.</summary>
public class SshHostCertificateTests
{
    private static readonly TimeProvider Now = TimeProvider.System;

    private static SshCertificateProblem? Problem(string certFile, string host = OpenSshFixtures.Host, TimeProvider? time = null, bool allowSha1 = false) =>
        OpenSshFixtures.HostCertificate(certFile).Check(host, time ?? Now, allowSha1)?.Problem;

    [Theory]
    [InlineData("cert-valid-ed25519ca.pub")]
    [InlineData("cert-valid-rsaca.pub")]
    [InlineData("cert-valid-ecdsaca.pub")]
    [InlineData("cert-valid-rsahostkey.pub")]
    [InlineData("cert-valid-ecdsahostkey.pub")]
    public void ValidCertificates_ForEveryCaAndCertifiedKeyType_Pass(string cert) => Assert.Null(Problem(cert));

    [Fact]
    public void FieldsMatchWhatRealSshKeygenReports()
    {
        var oracle = OpenSshFixtures.RunSshKeygen("-L", "-f", OpenSshFixtures.Path("cert-valid-rsaca.pub"));
        if (oracle is null)
        {
            Assert.Skip("ssh-keygen isn't on PATH.");
        }

        var cert = OpenSshFixtures.HostCertificate("cert-valid-rsaca.pub");

        Assert.Contains($"Signing CA: RSA {OpenSshFixtures.Key("ca_rsa.pub").Sha256Fingerprint}", oracle.Value.Output);
        Assert.Equal(OpenSshFixtures.Key("ca_rsa.pub").Sha256Fingerprint, cert.CertificateAuthorityKey.Sha256Fingerprint);
        Assert.Contains("Key ID: \"id-valid-rsaca\"", oracle.Value.Output);
        Assert.Equal("id-valid-rsaca", cert.KeyId);
        Assert.Contains("Serial: 2", oracle.Value.Output);
        Assert.Equal(2UL, cert.Serial);
        Assert.Contains("host certificate", oracle.Value.Output);
        Assert.Equal(SshHostCertificate.HostCertificateType, cert.CertificateType);
        Assert.Equal([OpenSshFixtures.Host], cert.Principals);
        Assert.Equal("rsa-sha2-512", cert.SignatureAlgorithm); // modern ssh-keygen's RSA default
    }

    [Fact]
    public void CaKeyBlob_FromSshNet_IsByteForByteTheCaPublicKeyFile() =>
        Assert.Equal(
            OpenSshFixtures.ReadPublicKey("ca_ed25519.pub").Blob,
            OpenSshFixtures.HostCertificate("cert-valid-ed25519ca.pub").CertificateAuthorityKey.Blob.ToArray());

    // ---- validity window, with an injectable clock ----

    [Fact]
    public void Expired_RealClock() => Assert.Equal(SshCertificateProblem.Expired, Problem("cert-expired.pub"));

    [Fact]
    public void NotYetValid_RealClock() => Assert.Equal(SshCertificateProblem.NotYetValid, Problem("cert-notyet.pub"));

    [Fact]
    public void ValidityWindow_IsStartInclusive_EndExclusive()
    {
        var cert = OpenSshFixtures.HostCertificate("cert-expired.pub");
        var after = cert.ValidAfterUnixSeconds;
        var before = cert.ValidBeforeUnixSeconds;

        TimeProvider At(ulong unix) => new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds((long)unix));

        Assert.Equal(SshCertificateProblem.NotYetValid, cert.Check(OpenSshFixtures.Host, At(after - 1), false)?.Problem);
        Assert.Null(cert.Check(OpenSshFixtures.Host, At(after), false));
        Assert.Null(cert.Check(OpenSshFixtures.Host, At(before - 1), false));
        Assert.Equal(SshCertificateProblem.Expired, cert.Check(OpenSshFixtures.Host, At(before), false)?.Problem);
    }

    [Fact]
    public void ForeverCertificate_IsValidAtAnyFutureTime()
    {
        var far = new FixedTimeProvider(new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(ulong.MaxValue, OpenSshFixtures.HostCertificate("cert-valid-ed25519ca.pub").ValidBeforeUnixSeconds);
        Assert.Null(Problem("cert-valid-ed25519ca.pub", time: far));
    }

    // ---- principals ----

    [Fact]
    public void WrongPrincipal_IsRejected() =>
        Assert.Equal(SshCertificateProblem.NoMatchingPrincipal, Problem("cert-wrong-principal.pub"));

    [Fact]
    public void EmptyPrincipals_MatchNothing_UnlikeOpenSsh()
    {
        var cert = OpenSshFixtures.HostCertificate("cert-empty-principals.pub");

        Assert.Empty(cert.Principals);
        Assert.Equal(SshCertificateProblem.NoMatchingPrincipal, cert.Check(OpenSshFixtures.Host, Now, false)?.Problem);
        Assert.Equal(SshCertificateProblem.NoMatchingPrincipal, cert.Check("anything", Now, false)?.Problem);
    }

    [Theory]
    [InlineData("lab.example", true)]
    [InlineData("other.example", true)]
    [InlineData("example", false)]
    [InlineData("lab.example.com", false)]
    public void WildcardPrincipal_UsesPatternSemantics(string host, bool matches) =>
        Assert.Equal(matches ? null : SshCertificateProblem.NoMatchingPrincipal, Problem("cert-wildcard-principal.pub", host));

    [Fact]
    public void PrincipalMatching_IsCaseInsensitive() => Assert.Null(Problem("cert-valid-ed25519ca.pub", "LAB.Example"));

    // ---- type, options, CA signature algorithm ----

    [Fact]
    public void UserCertificateOfferedAsAHostCertificate_IsRejected() =>
        Assert.Equal(SshCertificateProblem.WrongType, Problem("cert-user-cert.pub"));

    [Fact]
    public void CriticalOption_IsRejectedAsUnknown()
    {
        var cert = OpenSshFixtures.HostCertificate("cert-critical-option.pub");

        Assert.Equal(["force-command"], cert.CriticalOptionNames);
        Assert.Equal(SshCertificateProblem.UnknownCriticalOption, cert.Check(OpenSshFixtures.Host, Now, false)?.Problem);
    }

    [Fact]
    public void Sha1CaSignature_IsRejectedByDefault_AndAllowedOnlyByOptIn()
    {
        Assert.Equal("ssh-rsa", OpenSshFixtures.HostCertificate("cert-sha1-ca.pub").SignatureAlgorithm);

        Assert.Equal(SshCertificateProblem.DisallowedSignatureAlgorithm, Problem("cert-sha1-ca.pub"));
        Assert.Null(Problem("cert-sha1-ca.pub", allowSha1: true));
    }

    [Theory]
    [InlineData("cert-valid-ed25519ca.pub", "ssh-ed25519")]
    [InlineData("cert-valid-rsaca.pub", "rsa-sha2-512")]
    [InlineData("cert-valid-ecdsaca.pub", "ecdsa-sha2-nistp384")]
    public void SignatureAlgorithm_IsReadFromTheCertificatesSignature(string cert, string expected) =>
        Assert.Equal(expected, OpenSshFixtures.HostCertificate(cert).SignatureAlgorithm);

    [Theory]
    [InlineData("ssh-ed25519", "ssh-rsa")]
    [InlineData("rsa-sha2-512", "ssh-ed25519")]
    [InlineData("ecdsa-sha2-nistp256", "ecdsa-sha2-nistp384")]
    [InlineData("ssh-ed25519", "ecdsa-sha2-nistp256")]
    public void CaSignatureAlgorithm_ThatDoesNotBelongToTheCaKeyType_IsRejected(string signatureAlgorithm, string caKeyType)
    {
        var caKey = OpenSshFixtures.Key("ca_ed25519.pub");
        var real = OpenSshFixtures.HostCertificate("cert-valid-ed25519ca.pub");
        var mismatched = new SshHostCertificate(
            real.KeyId, real.Serial, real.CertificateType, real.Principals, real.ValidAfterUnixSeconds, real.ValidBeforeUnixSeconds,
            real.CriticalOptionNames, KeyOfType(caKeyType, caKey), signatureAlgorithm);

        Assert.Equal(SshCertificateProblem.DisallowedSignatureAlgorithm, mismatched.Check(OpenSshFixtures.Host, Now, false)?.Problem);
    }

    private static OpenSshPublicKey KeyOfType(string type, OpenSshPublicKey fallback) => type switch
    {
        "ssh-rsa" => OpenSshFixtures.Key("ca_rsa.pub"),
        "ecdsa-sha2-nistp384" => OpenSshFixtures.Key("ca_ecdsa.pub"),
        "ecdsa-sha2-nistp256" => OpenSshFixtures.Key("host_ecdsa.pub"),
        _ => fallback,
    };

    // ---- division of responsibility: what this layer does NOT check ----

    [Fact]
    public void WrongCa_PassesPolicy_BecauseCaTrustIsTheCallersDecision() =>
        Assert.Null(Problem("cert-wrong-ca.pub")); // rejected by the verifier's @cert-authority match, see OpenSshHostKeyVerifierTests

    [Fact]
    public void TamperedSignature_ParsesAndPassesPolicy_BecauseTheSignatureIsCheckedBySshNetDuringKeyExchange()
    {
        // This documents the boundary on purpose: our layer never looks at signature bytes. A cert
        // with a corrupted signature is caught by SSH.NET before the verifier runs — proven end-to-end
        // by Integration/OpenSshHostCertificateIntegrationTests (TamperedSignature_...).
        Assert.Null(Problem("cert-tampered.pub"));

        var oracle = OpenSshFixtures.RunSshKeygen("-L", "-f", OpenSshFixtures.Path("cert-tampered.pub"));
        if (oracle is not null)
        {
            Assert.Contains("incorrect signature", oracle.Value.Output); // real OpenSSH rejects it
        }
    }

    // ---- wire slice used for revocation matching ----

    [Theory]
    [InlineData("cert-valid-ed25519ca.pub", "host_ed25519.pub")]
    [InlineData("cert-valid-rsahostkey.pub", "host_rsa.pub")]
    [InlineData("cert-valid-ecdsahostkey.pub", "host_ecdsa.pub")]
    public void EmbeddedKeySlice_IsByteForByteTheHostPublicKey(string cert, string hostPub)
    {
        var blob = OpenSshFixtures.ReadPublicKey(cert).Blob;

        Assert.True(SshCertificateWire.TryGetEmbeddedKeyBlob(blob, out var embedded));
        Assert.Equal(OpenSshFixtures.ReadPublicKey(hostPub).Blob, embedded);
    }

    [Fact]
    public void EmbeddedKeySlice_OnTruncatedOrGarbageInput_FailsClosedWithoutThrowing()
    {
        var blob = OpenSshFixtures.ReadPublicKey("cert-valid-ed25519ca.pub").Blob;

        for (var cut = 0; cut < 60; cut++)
        {
            Assert.False(SshCertificateWire.TryGetEmbeddedKeyBlob(blob.AsSpan(0, cut), out _));
        }

        Assert.False(SshCertificateWire.TryGetEmbeddedKeyBlob([0xff, 0xff, 0xff, 0xff, 1, 2, 3], out _));
        Assert.False(SshCertificateWire.TryGetEmbeddedKeyBlob([], out _));
    }
}
