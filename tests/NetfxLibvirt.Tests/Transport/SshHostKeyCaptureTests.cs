using NetfxLibvirt.Tests.OpenSsh;
using NetfxLibvirt.Transport;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace NetfxLibvirt.Tests.Transport;

/// <summary>The host key algorithm factory wrapper (<see cref="SshHostKeyVerification.Attach"/>)
/// must be invisible to SSH.NET: same algorithms, same preference order, same objects built.
/// The end-to-end proof that it captures the right bytes is
/// <c>Integration/OpenSshHostCertificateIntegrationTests</c>.</summary>
public class SshHostKeyCaptureTests
{
    private static (SshClient Client, SshHostKeyVerification Verification) Attached()
    {
        var verification = new SshHostKeyVerification(
            new SshTransportOptions { Host = "h", Username = "u", RemoteUri = "x", VerifyHostKey = _ => true },
            CancellationToken.None);
        var client = new SshClient(new ConnectionInfo("h", "u", new NoneAuthenticationMethod("u")));
        return (client, verification);
    }

    [Fact]
    public void Attach_PreservesTheAlgorithmSetAndPreferenceOrder()
    {
        var (client, verification) = Attached();
        using var _ = client;
        var before = client.ConnectionInfo.HostKeyAlgorithms.Keys.ToList();

        verification.Attach(client);

        Assert.Equal(before, client.ConnectionInfo.HostKeyAlgorithms.Keys.ToList());
        Assert.Contains("ssh-ed25519-cert-v01@openssh.com", before); // the cert algorithms are among those wrapped
    }

    [Fact]
    public void Attach_WrappedFactories_StillBuildTheSameKindOfAlgorithm()
    {
        var blob = OpenSshFixtures.ReadPublicKey("cert-valid-ed25519ca.pub").Blob;
        var (client, verification) = Attached();
        using var _ = client;
        var plainFactory = client.ConnectionInfo.HostKeyAlgorithms["ssh-ed25519-cert-v01@openssh.com"];
        var expected = plainFactory(blob);

        verification.Attach(client);
        var actual = client.ConnectionInfo.HostKeyAlgorithms["ssh-ed25519-cert-v01@openssh.com"](blob);

        Assert.IsType<CertificateHostAlgorithm>(actual);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Data, actual.Data);
    }
}

public class SshHostKeyRejectedExceptionTests
{
    private static readonly SshHostKeyInfo Key = new("ssh-ed25519", 256, "fp", [1]);

    [Fact]
    public void OriginalConstructor_StillMeansRejectedByVerifier()
    {
        var ex = new SshHostKeyRejectedException(Key, "h", 22);

        Assert.Equal(SshHostKeyRejectionReason.RejectedByVerifier, ex.Reason);
        Assert.Contains("rejected by the host key verifier", ex.Message);
        Assert.Contains("SHA256:fp", ex.Message);
        Assert.IsAssignableFrom<SshTransportException>(ex);
    }

    [Fact]
    public void Message_NamesTheCertificateWhenOneWasPresented()
    {
        var withCert = Key with { CertificateBlob = [1, 2, 3] };
        Assert.True(withCert.IsCertificate);

        var ex = new SshHostKeyRejectedException(withCert, "h", 22, SshHostKeyRejectionReason.CertificateInvalid, "expired") { CertificateProblem = SshCertificateProblem.Expired };

        Assert.Contains("host certificate", ex.Message);
        Assert.Contains("expired", ex.Message);
        Assert.Equal(SshCertificateProblem.Expired, ex.CertificateProblem);
        Assert.Equal("expired", ex.Detail);
    }
}
