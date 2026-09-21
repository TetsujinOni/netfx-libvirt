using NetfxLibvirt.Tests.OpenSsh;
using NetfxLibvirt.Transport;
using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// OpenSSH host verification over REAL SSH handshakes against real
/// <c>sshd</c> presenting REAL <c>ssh-keygen</c>-issued host certificates
/// (see <see cref="CertificateSshd"/>) — the end-to-end proof of everything
/// the hermetic tests assume about SSH.NET:
///
/// * A host certificate is presented as such, and the certificate blob we
///   capture from the algorithm factory is exactly what the server sent.
/// * SSH.NET checks the certificate validity window BEFORE our verifier runs, so
///   an expired/not-yet-valid certificate never reaches it and surfaces as
///   <see cref="SshHostKeyRejectionReason.CertificateInvalid"/> (mapped from public
///   facts). Tampered signatures can't be served by a real sshd (OpenSSH verifies
///   a certificate's CA signature when loading it); SSH.NET's signature checks are
///   pinned by <c>SshNetCertificateVerificationSentinelTests</c> instead.
/// * SSH.NET does NOT check type, principals, critical options or CA trust —
///   a user certificate and a certificate for another host both reach the
///   verifier, which rejects them.
/// Uses the shared Testcontainers fixture (skips cleanly without Docker).
/// </summary>
[Collection(nameof(LibvirtdContainerCollection))]
public class OpenSshHostCertificateIntegrationTests : IDisposable
{
    private readonly LibvirtdContainerFixture _fixture;
    private readonly TempDirectory _temp = new();
    private readonly RecordingPrompt _prompt = new();

    public OpenSshHostCertificateIntegrationTests(LibvirtdContainerFixture fixture)
    {
        _fixture = fixture;
        if (_fixture.StartupFailure is not null)
        {
            Assert.Skip($"Integration test container failed to start (is Docker running?): {_fixture.StartupFailure}");
        }
    }

    public void Dispose() => _temp.Dispose();

    private string UserFile => _temp.File("known_hosts");

    private static async Task<string> CaLineAsync(LibvirtdContainerFixture fixture, string ca, string hostPattern = "*")
    {
        var (type, blob) = await CertificateSshd.ReadCaAsync(fixture, ca);
        return $"@cert-authority {hostPattern} {type} {Convert.ToBase64String(blob)}";
    }

    private OpenSshHostKeyVerifierOptions VerifierOptions(string host, int port, bool prompt = true, bool allowSha1 = false) => new()
    {
        Host = host,
        Port = port,
        UserKnownHostsFile = UserFile,
        GlobalKnownHostsFiles = [],
        Prompt = prompt ? _prompt : null,
        AllowSha1CaSignatures = allowSha1,
    };

    private SshTransportOptions Transport(string host, int port, AsyncSshHostKeyVerifier verifier) => new()
    {
        Host = host,
        Port = port,
        Username = "root",
        PrivateKeyPath = _fixture.PrivateKeyPath,
        VerifyHostKeyAsync = verifier,
        RemoteUri = "test:///default",
    };

    /// <summary>Connects (handshake + auth) and returns the host key info the verifier was handed.</summary>
    private async Task<SshHostKeyInfo> ConnectAsync(string host, int port, OpenSshHostKeyVerifierOptions verifierOptions)
    {
        SshHostKeyInfo? seen = null;
        var inner = OpenSshHostKeyVerifier.Create(verifierOptions);
        AsyncSshHostKeyVerifier spy = async (key, ct) =>
        {
            seen = key;
            return await inner(key, ct);
        };

        using var client = await SshTransport.ConnectClientAsync(Transport(host, port, spy), TestContext.Current.CancellationToken);

        Assert.True(client.IsConnected);
        return seen!;
    }

    private async Task<SshHostKeyRejectedException> ConnectExpectingRejectionAsync(string host, int port, OpenSshHostKeyVerifierOptions verifierOptions) =>
        await Assert.ThrowsAsync<SshHostKeyRejectedException>(() => ConnectAsync(host, port, verifierOptions));

    // ================= valid certificates, every CA type and certified key type =================

    [Theory]
    [InlineData("ed25519", "ed25519")]
    [InlineData("ed25519", "rsa")]
    [InlineData("ed25519", "ecdsa")]
    [InlineData("rsa", "ed25519")]
    [InlineData("ecdsa", "ed25519")]
    public async Task ValidCertificate_FromATrustedCa_ConnectsSilently_AndTheVerifierSeesTheRealBlobs(string hostKeyType, string ca)
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, hostKeyType, ca);
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, ca) + "\n");

        var seen = await ConnectAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Empty(_prompt.CaQuestions);
        Assert.True(seen.IsCertificate);
        Assert.EndsWith("-cert-v01@openssh.com", seen.AlgorithmName);

        // What we captured is byte-for-byte what the server holds — not SSH.NET's re-encoding.
        var (_, certBlob) = await sshd.ReadCertificateAsync();
        Assert.Equal(certBlob, seen.CertificateBlob);
        var (_, hostBlob) = await sshd.ReadHostPublicKeyAsync();
        Assert.Equal(hostBlob, seen.RawKey);
        Assert.Equal(OpenSshPublicKey.FingerprintOf(hostBlob)["SHA256:".Length..], seen.Sha256Fingerprint);

        var (_, caBlob) = await CertificateSshd.ReadCaAsync(_fixture, ca);
        Assert.Equal(caBlob, seen.Certificate!.CertificateAuthorityKey.Blob.ToArray());
        Assert.Equal(SshHostCertificate.HostCertificateType, seen.Certificate.CertificateType);
    }

    // ================= trust-this-CA flow =================

    [Fact]
    public async Task NoCaKnown_AsksToTrustTheCa_RecordsIt_AndTheNextConnectionIsSilent()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture);
        var options = VerifierOptions(sshd.Host, sshd.Port);

        Assert.Equal(TrustState.Unknown, OpenSshHostKeyVerifier.GetTrustState(options));
        await ConnectAsync(sshd.Host, sshd.Port, options);

        var question = Assert.Single(_prompt.CaQuestions);
        var (type, caBlob) = await CertificateSshd.ReadCaAsync(_fixture, "ed25519");
        Assert.Equal(OpenSshPublicKey.FromBlob(caBlob).Sha256Fingerprint, question.Fingerprint);
        Assert.Equal($"it-{sshd.ContainerPort}", question.CertificateKeyId);
        Assert.Equal([sshd.Host], question.CertificatePrincipals);

        Assert.Equal(
            [$"@cert-authority [{sshd.Host}]:{sshd.Port} {type} {Convert.ToBase64String(caBlob)}"],
            File.ReadAllLines(UserFile));
        Assert.Equal(TrustState.CaCovered, OpenSshHostKeyVerifier.GetTrustState(options));

        await ConnectAsync(sshd.Host, sshd.Port, options);
        Assert.Single(_prompt.CaQuestions); // no second prompt
    }

    [Fact]
    public async Task NoCaKnown_UserDeclines_IsRejected_UserDeclined()
    {
        _prompt.Answer = false;
        var sshd = await CertificateSshd.StartAsync(_fixture);

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshHostKeyRejectionReason.UserDeclined, rejection.Reason);
        Assert.False(File.Exists(UserFile));
    }

    // ================= certificates SSH.NET verifies but policy must reject =================

    [Fact]
    public async Task UserCertificateOfferedAsAHostCertificate_ReachesTheVerifier_AndIsRejectedAsWrongType()
    {
        // The upstream gap this library closes: SSH.NET accepts (signature-valid, in-date) any certificate, of any TYPE.
        var sshd = await CertificateSshd.StartAsync(_fixture, userCertificate: true);
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshHostKeyRejectionReason.CertificateInvalid, rejection.Reason);
        Assert.Equal(SshCertificateProblem.WrongType, rejection.CertificateProblem);
    }

    [Fact]
    public async Task CertificateForAnotherHost_ReachesTheVerifier_AndIsRejectedAsNoMatchingPrincipal()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, principals: "some-other-host.example");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshCertificateProblem.NoMatchingPrincipal, rejection.CertificateProblem);
    }

    [Fact]
    public async Task CertificateWithNoPrincipals_IsRejected_UnlikeOpenSsh()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, principals: "");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshCertificateProblem.NoMatchingPrincipal, rejection.CertificateProblem);
    }

    [Fact]
    public async Task CertificateWithACriticalOption_IsRejected()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, signArgs: "-O force-command=/bin/true");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshCertificateProblem.UnknownCriticalOption, rejection.CertificateProblem);
    }

    [Fact]
    public async Task CertificateFromADifferentCaThanTheOneTrusted_IsRejected_UntrustedCa()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, ca: "other");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshCertificateProblem.UntrustedCertificateAuthority, rejection.CertificateProblem);
        Assert.Empty(_prompt.CaQuestions);
    }

    [Fact]
    public async Task Sha1CaSignature_IsRejectedByDefault_AndConnectsWhenOptedIn()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, ca: "rsa", signArgs: "-t ssh-rsa");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "rsa") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));
        Assert.Equal(SshCertificateProblem.DisallowedSignatureAlgorithm, rejection.CertificateProblem);

        await ConnectAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port, allowSha1: true));
    }

    // ================= certificates SSH.NET itself rejects during key exchange (regression sentinels) =================

    [Fact]
    public async Task ExpiredCertificate_NeverReachesTheVerifier_ButIsReportedAsExpired()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, signArgs: "-V 20200101:20200102");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshHostKeyRejectionReason.CertificateInvalid, rejection.Reason);
        Assert.Equal(SshCertificateProblem.Expired, rejection.CertificateProblem);
        Assert.True(rejection.HostKey.IsCertificate);
    }

    [Fact]
    public async Task NotYetValidCertificate_NeverReachesTheVerifier_ButIsReportedAsNotYetValid()
    {
        var sshd = await CertificateSshd.StartAsync(_fixture, signArgs: "-V 20980101:20990101");
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(sshd.Host, sshd.Port, VerifierOptions(sshd.Host, sshd.Port));

        Assert.Equal(SshCertificateProblem.NotYetValid, rejection.CertificateProblem);
    }

    // ================= plain-key hosts and CA policy, over the real fixture sshd (plain host keys, port 22) =================

    [Fact]
    public async Task PlainKeyHost_TrustOnFirstUse_ThenSilent_AndTheFingerprintMatchesWhatTheServerHolds()
    {
        var host = _fixture.Host;
        var port = _fixture.SshHostPort;
        var options = VerifierOptions(host, port);

        var seen = await ConnectAsync(host, port, options);
        await ConnectAsync(host, port, options);

        Assert.False(seen.IsCertificate);
        var question = Assert.Single(_prompt.HostKeyQuestions);
        Assert.Equal(TrustState.Known, OpenSshHostKeyVerifier.GetTrustState(options));

        // The blob and fingerprint are the server's real host key, byte for byte.
        var serverKeys = new List<byte[]>();
        foreach (var type in new[] { "ed25519", "ecdsa", "rsa" })
        {
            var bytes = await _fixture.Container.ReadFileAsync($"/etc/ssh/ssh_host_{type}_key.pub");
            serverKeys.Add(Convert.FromBase64String(System.Text.Encoding.UTF8.GetString(bytes).Split(' ')[1]));
        }

        Assert.Contains(serverKeys, k => k.SequenceEqual(seen.RawKey));
        Assert.Equal(OpenSshPublicKey.FingerprintOf(seen.RawKey), question.Fingerprint);
        Assert.Equal(OpenSshPublicKey.FingerprintOf(seen.RawKey)["SHA256:".Length..], seen.Sha256Fingerprint);
    }

    [Fact]
    public async Task PlainKeyHost_WhoseKeyChanged_IsRejected_WithFileAndLine()
    {
        var host = _fixture.Host;
        var port = _fixture.SshHostPort;
        // Record a DIFFERENT key of every type the server might present, so whichever it offers is "changed".
        var lines = new List<string> { "# pinned earlier" };
        foreach (var keyPub in new[] { "ca_ed25519.pub", "host_rsa.pub", "host_ecdsa.pub" })
        {
            var key = OpenSshFixtures.Key(keyPub);
            lines.Add($"[{host}]:{port} {key.KeyType} {key.ToBase64()}");
        }

        File.WriteAllLines(UserFile, lines);

        var rejection = await ConnectExpectingRejectionAsync(host, port, VerifierOptions(host, port));

        Assert.Equal(SshHostKeyRejectionReason.ChangedKey, rejection.Reason);
        Assert.Equal(Path.GetFullPath(UserFile), rejection.KnownHostsFile);
        Assert.InRange(rejection.KnownHostsLine!.Value, 2, 4);
        Assert.NotNull(rejection.ExpectedFingerprint);
    }

    [Fact]
    public async Task PlainKeyHost_WhenACaCoversIt_IsRejected_PlainKeyWhereCertificateRequired()
    {
        var host = _fixture.Host;
        var port = _fixture.SshHostPort;
        File.WriteAllText(UserFile, await CaLineAsync(_fixture, "ed25519") + "\n");

        var rejection = await ConnectExpectingRejectionAsync(host, port, VerifierOptions(host, port));

        Assert.Equal(SshHostKeyRejectionReason.PlainKeyWhereCertificateRequired, rejection.Reason);
        Assert.Empty(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task RevokedHostKey_IsRejected_OverARealHandshake()
    {
        var host = _fixture.Host;
        var port = _fixture.SshHostPort;
        var lines = new List<string>();
        foreach (var type in new[] { "ed25519", "ecdsa", "rsa" })
        {
            var text = System.Text.Encoding.UTF8.GetString(await _fixture.Container.ReadFileAsync($"/etc/ssh/ssh_host_{type}_key.pub")).Split(' ');
            lines.Add($"@revoked * {text[0]} {text[1]}");
        }

        File.WriteAllLines(UserFile, lines);

        var rejection = await ConnectExpectingRejectionAsync(host, port, VerifierOptions(host, port));

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
    }

    // ================= concurrency over real connections =================

    [Fact]
    public async Task ConcurrentConnectionsToOneUnknownHost_PromptOnce()
    {
        _prompt.Delay = TimeSpan.FromMilliseconds(300);
        var sshd = await CertificateSshd.StartAsync(_fixture);
        var options = VerifierOptions(sshd.Host, sshd.Port);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ConnectAsync(sshd.Host, sshd.Port, options)));

        Assert.Single(_prompt.CaQuestions);
        Assert.Single(File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task CancellingTheConnectWhileThePromptIsShowing_DismissesItAndWritesNothing()
    {
        _prompt.Delay = TimeSpan.FromSeconds(30);
        var sshd = await CertificateSshd.StartAsync(_fixture);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var connect = SshTransport.ConnectClientAsync(
            Transport(sshd.Host, sshd.Port, OpenSshHostKeyVerifier.Create(VerifierOptions(sshd.Host, sshd.Port))), cts.Token);
        await Task.Delay(1500, TestContext.Current.CancellationToken); // handshake reaches the prompt
        Assert.Single(_prompt.CaQuestions);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
        Assert.False(File.Exists(UserFile));
    }
}
