using System.Buffers.Binary;
using System.Security.Cryptography;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>
/// SENTINELS for the security floor this library stands on.
///
/// This library deliberately does not verify certificate cryptography
/// itself; it relies on SSH.NET doing so BEFORE a host key verifier is ever
/// invoked (<c>KeyExchange.ValidateExchangeHash</c> →
/// <c>CertificateHostAlgorithm.VerifySignatureBlob</c>, SSH.NET 2026.0.0,
/// <c>src/Renci.SshNet/Security/CertificateHostAlgorithm.cs</c>). These tests
/// pin that assumption against SSH.NET's REAL verification code — obtained
/// through the public <c>ConnectionInfo.HostKeyAlgorithms</c> factories and
/// driven through the public <c>KeyHostAlgorithm.VerifySignature</c> — using
/// REAL <c>ssh-keygen</c> certificates and real private keys, with the
/// "server" side producing key-exchange-style signatures exactly as SSH.NET's
/// own <c>CertificateHostAlgorithm.Sign</c> does. (A real <c>sshd</c> can't
/// serve most of these: OpenSSH verifies a certificate's CA signature when it
/// loads it, so a tampered one is silently replaced by a plain host key.)
///
/// If an SSH.NET upgrade ever makes any of these fail, a forged or altered
/// certificate could reach — and this library's policy layer would then trust
/// the facts of — a certificate nobody vouched for. Do not "fix" the test;
/// hold the upgrade.
///
/// Also documents (requirement: "confirm SSH.NET checks the server's KEX
/// signature against the certificate's embedded key"): the peer must hold the
/// certified key — a signature by any other key is refused.
/// </summary>
public class SshNetCertificateVerificationSentinelTests
{
    private static readonly byte[] ExchangeHash = RandomNumberGenerator.GetBytes(32); // stands in for the KEX exchange hash

    /// <summary>Does SSH.NET's own certificate host-key algorithm accept a signature over the exchange hash made by
    /// <paramref name="signingKeyFile"/>, for the presented certificate <paramref name="certificateBlob"/>?</summary>
    private static bool SshNetAccepts(byte[] certificateBlob, string signingKeyFile = "host_ed25519")
    {
        var name = SshNameOf(certificateBlob);
        var connectionInfo = new ConnectionInfo("h", "u", new NoneAuthenticationMethod("u"));

        // What the client does when the server sends this as its host key: build the algorithm from the wire blob.
        var clientSide = connectionInfo.HostKeyAlgorithms[name](certificateBlob);

        // What a server holding <signingKeyFile> does: sign the exchange hash (SignatureKeyData-encoded).
        using var privateKeyFile = new PrivateKeyFile(OpenSshFixtures.Path(signingKeyFile));
        var serverSide = new CertificateHostAlgorithm(name, privateKeyFile.Key, new Certificate(certificateBlob));
        var signature = serverSide.Sign(ExchangeHash);

        return clientSide.VerifySignature(ExchangeHash, signature);
    }

    private static string SshNameOf(byte[] blob) =>
        System.Text.Encoding.ASCII.GetString(blob, 4, (int)BinaryPrimitives.ReadUInt32BigEndian(blob));

    private static byte[] Blob(string certFile) => OpenSshFixtures.ReadPublicKey(certFile).Blob;

    private static byte[] Mutated(byte[] blob, Action<byte[]> mutate)
    {
        var copy = (byte[])blob.Clone();
        mutate(copy);
        return copy;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        var index = haystack.AsSpan().IndexOf(needle);
        Assert.True(index >= 0, "expected field not found in the certificate");
        return index;
    }

    // ---- the baseline: real certificates verify, so the negative cases below mean something ----

    [Theory]
    [InlineData("cert-valid-ed25519ca.pub")]
    [InlineData("cert-valid-rsaca.pub")]
    [InlineData("cert-valid-ecdsaca.pub")]
    public void ValidCertificate_SignedByTheCertifiedKey_IsAccepted(string cert) => Assert.True(SshNetAccepts(Blob(cert)));

    // ---- KEX signature must come from the certified key ----

    [Fact]
    public void KeyExchangeSignatureByAnyOtherKey_IsRefused_SoThePeerMustHoldTheCertifiedKey() =>
        Assert.False(SshNetAccepts(Blob("cert-valid-ed25519ca.pub"), signingKeyFile: "attacker_ed25519"));

    // ---- CA signature must cover the certificate exactly as presented ----

    [Fact]
    public void CorruptedCaSignature_IsRefused() => Assert.False(SshNetAccepts(Blob("cert-tampered.pub")));

    [Fact]
    public void AlteredPrincipal_KeepingTheOriginalSignature_IsRefused()
    {
        // "Widen" a certificate: the principal is covered by the CA signature, so a changed one must not verify.
        var altered = Mutated(Blob("cert-valid-ed25519ca.pub"), b => b[IndexOf(b, "lab.example"u8.ToArray()) + 4] ^= 0x01);

        Assert.False(SshNetAccepts(altered));
    }

    [Fact]
    public void AlteredKeyId_KeepingTheOriginalSignature_IsRefused()
    {
        var altered = Mutated(Blob("cert-valid-ed25519ca.pub"), b => b[IndexOf(b, "id-valid-ed25519ca"u8.ToArray())] ^= 0x01);

        Assert.False(SshNetAccepts(altered));
    }

    [Fact]
    public void ReviveAnExpiredCertificateByRewritingItsValidBefore_IsRefused()
    {
        var expired = Blob("cert-expired.pub");
        var validBefore = OpenSshFixtures.HostCertificate("cert-expired.pub").ValidBeforeUnixSeconds;
        var needle = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(needle, validBefore);
        var revived = Mutated(expired, b => b.AsSpan(IndexOf(b, needle), 8).Fill(0xff)); // "forever"

        Assert.False(SshNetAccepts(revived));
    }

    [Fact]
    public void FlipUserCertificateToHostType_KeepingTheOriginalSignature_IsRefused()
    {
        // A user certificate rewritten to claim type=host: the type field is covered by the signature.
        var user = Blob("cert-user-cert.pub");
        var serial = OpenSshFixtures.HostCertificate("cert-user-cert.pub").Serial; // 0 for this fixture
        var typeField = new byte[12]; // serial(8) + type(4) — locate serial=0,type=1 by structure instead of guessing:
        BinaryPrimitives.WriteUInt64BigEndian(typeField, serial);
        BinaryPrimitives.WriteUInt32BigEndian(typeField.AsSpan(8), 1);
        var flipped = Mutated(user, b => b[IndexOf(b, typeField) + 11] = 2);

        Assert.False(SshNetAccepts(flipped));
    }

    // ---- SSH.NET's own validity-window check (so an expired certificate never reaches the verifier) ----

    [Fact]
    public void ExpiredCertificate_IsRefusedBySshNetItself() => Assert.False(SshNetAccepts(Blob("cert-expired.pub")));

    [Fact]
    public void NotYetValidCertificate_IsRefusedBySshNetItself() => Assert.False(SshNetAccepts(Blob("cert-notyet.pub")));

    // ---- what SSH.NET does NOT check: the reason this library's policy layer exists ----

    [Theory]
    [InlineData("cert-user-cert.pub")]          // a USER certificate
    [InlineData("cert-wrong-principal.pub")]    // issued for a different host
    [InlineData("cert-empty-principals.pub")]   // no principals at all
    [InlineData("cert-critical-option.pub")]    // carries a critical option
    [InlineData("cert-wrong-ca.pub")]           // signed by a CA nobody here trusts (SSH.NET trusts the CA embedded in the cert)
    public void SshNetAcceptsThese_BecauseItDoesStructureValidationNotTrust_SoOurPolicyMustReject(string cert) =>
        Assert.True(SshNetAccepts(Blob(cert)));
}
