using NetfxLibvirt.Transport;
using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>The composite verifier's decisions, over real ssh-keygen-issued keys and
/// certificates and real files on disk.</summary>
public class OpenSshHostKeyVerifierTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly RecordingPrompt _prompt = new();
    private const string Host = OpenSshFixtures.Host;

    public void Dispose() => _temp.Dispose();

    private string UserFile => _temp.File("known_hosts");

    private OpenSshHostKeyVerifierOptions Options(int port = 22, bool prompt = true) => new()
    {
        Host = Host,
        Port = port,
        UserKnownHostsFile = UserFile,
        GlobalKnownHostsFiles = [],
        Prompt = prompt ? _prompt : null,
    };

    private void WriteKnownHosts(params string[] lines) => File.WriteAllLines(UserFile, lines);

    private static string Line(string pubFile, string host = Host, string? marker = null)
    {
        var key = OpenSshFixtures.Key(pubFile);
        return $"{(marker is null ? "" : marker + " ")}{host} {key.KeyType} {key.ToBase64()}";
    }

    private static async Task<SshHostKeyRejectedException> RejectionOf(AsyncSshHostKeyVerifier verifier, SshHostKeyInfo key) =>
        await Assert.ThrowsAsync<SshHostKeyRejectedException>(async () => await verifier(key, TestContext.Current.CancellationToken));

    private static async Task Accepts(AsyncSshHostKeyVerifier verifier, SshHostKeyInfo key) =>
        Assert.True(await verifier(key, TestContext.Current.CancellationToken));

    // ================= plain keys =================

    [Fact]
    public async Task PlainKey_OnRecord_IsAcceptedWithoutAsking()
    {
        WriteKnownHosts(Line("host_ed25519.pub"));

        await Accepts(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Empty(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task PlainKey_Unknown_AsksOnce_ThenRecordsIt_AndNeverAsksAgain()
    {
        var info = OpenSshFixtures.PlainKeyInfo("host_ed25519.pub");
        var verifier = OpenSshHostKeyVerifier.Create(Options());

        await Accepts(verifier, info);
        await Accepts(verifier, info);
        await Accepts(OpenSshHostKeyVerifier.Create(Options()), info); // a fresh verifier reads the same file

        var question = Assert.Single(_prompt.HostKeyQuestions);
        Assert.Equal(Host, question.Host);
        Assert.Equal(22, question.Port);
        Assert.Equal("ssh-ed25519", question.KeyType);
        Assert.Equal(OpenSshFixtures.Key("host_ed25519.pub").Sha256Fingerprint, question.Fingerprint);
        Assert.StartsWith("SHA256:", question.Fingerprint);
        Assert.Equal([Line("host_ed25519.pub")], File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task PlainKey_Unknown_OnANonStandardPort_IsRecordedInBracketForm()
    {
        await Accepts(OpenSshHostKeyVerifier.Create(Options(port: 2222)), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal([Line("host_ed25519.pub", $"[{Host}]:2222")], File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task PlainKey_Unknown_WithNoPrompt_IsRejected_UnknownAndNoPrompt()
    {
        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options(prompt: false)), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal(SshHostKeyRejectionReason.UnknownAndNoPrompt, rejection.Reason);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task PlainKey_Unknown_UserDeclines_IsRejected_AndNothingIsWritten()
    {
        _prompt.Answer = false;

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal(SshHostKeyRejectionReason.UserDeclined, rejection.Reason);
        Assert.Single(_prompt.HostKeyQuestions);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task PlainKey_ChangedKey_IsRejected_WithOffendingFileLineAndExpectedFingerprint_WithoutAsking()
    {
        WriteKnownHosts("# header", Line("host_ed25519.pub"));
        var impostor = OpenSshFixtures.PlainKeyInfo("ca_ed25519.pub"); // an ed25519 key that isn't the recorded one

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), impostor);

        Assert.Equal(SshHostKeyRejectionReason.ChangedKey, rejection.Reason);
        Assert.Equal(Path.GetFullPath(UserFile), rejection.KnownHostsFile);
        Assert.Equal(2, rejection.KnownHostsLine);
        Assert.Equal(OpenSshFixtures.Key("host_ed25519.pub").Sha256Fingerprint, rejection.ExpectedFingerprint);
        Assert.Same(impostor, rejection.HostKey);
        Assert.Contains($"{Path.GetFullPath(UserFile)}:2", rejection.Message);
        Assert.Empty(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task PlainKey_HostKnownOnlyForOtherKeyTypes_IsUnknown_SoItAsks()
    {
        WriteKnownHosts(Line("host_rsa.pub"));

        await Accepts(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Single(_prompt.HostKeyQuestions);
        Assert.Equal(2, File.ReadAllLines(UserFile).Length);
    }

    [Fact]
    public async Task PlainKey_Revoked_IsRejected_EvenWhenAlsoOnRecord()
    {
        WriteKnownHosts(Line("host_ed25519.pub"), Line("host_ed25519.pub", "*", "@revoked"));

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
        Assert.Equal(2, rejection.KnownHostsLine);
        Assert.Empty(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task PlainKey_WhenACaCoversTheHost_IsRejected_NoFallbackToTrustOnFirstUse()
    {
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"));

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal(SshHostKeyRejectionReason.PlainKeyWhereCertificateRequired, rejection.Reason);
        Assert.Empty(_prompt.HostKeyQuestions);
        Assert.Single(File.ReadAllLines(UserFile)); // nothing appended
    }

    [Fact]
    public async Task PlainKey_WhenACaCovers_ButPolicyDisabled_FallsBackToOrdinaryKnownHostsSemantics()
    {
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"));
        var options = Options() with { RequireCertificateWhenCaCovers = false };

        await Accepts(OpenSshHostKeyVerifier.Create(options), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Single(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task PlainKey_CaOnlyCoversOtherHosts_DoesNotAffectThisHost()
    {
        WriteKnownHosts(Line("ca_ed25519.pub", "*.corp", "@cert-authority"));

        await Accepts(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));
    }

    // ================= certificates: CA already trusted =================

    private void TrustCa(string caPub = "ca_ed25519.pub") => WriteKnownHosts(Line(caPub, "*.example", "@cert-authority"));

    [Theory]
    [InlineData("cert-valid-ed25519ca.pub", "ca_ed25519.pub")]
    [InlineData("cert-valid-rsaca.pub", "ca_rsa.pub")]
    [InlineData("cert-valid-ecdsaca.pub", "ca_ecdsa.pub")]
    [InlineData("cert-valid-rsahostkey.pub", "ca_ed25519.pub")]
    [InlineData("cert-valid-ecdsahostkey.pub", "ca_ed25519.pub")]
    public async Task Certificate_FromATrustedCa_IsAcceptedSilently_AndNothingIsWritten(string cert, string ca)
    {
        TrustCa(ca);
        var before = File.ReadAllText(UserFile);

        await Accepts(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo(cert));

        Assert.Empty(_prompt.CaQuestions);
        Assert.Empty(_prompt.HostKeyQuestions);
        Assert.Equal(before, File.ReadAllText(UserFile));
    }

    [Fact]
    public async Task Certificate_FromADifferentCaThanTheOneTrustedForTheHost_IsRejected_UntrustedCa()
    {
        TrustCa("ca_ed25519.pub");

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-wrong-ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.CertificateInvalid, rejection.Reason);
        Assert.Equal(SshCertificateProblem.UntrustedCertificateAuthority, rejection.CertificateProblem);
        Assert.Contains(OpenSshFixtures.Key("ca_other.pub").Sha256Fingerprint, rejection.Message);
        Assert.Empty(_prompt.CaQuestions); // a CA already covers the host: no "trust this CA?" escape hatch
    }

    [Theory]
    [InlineData("cert-expired.pub", SshCertificateProblem.Expired)]
    [InlineData("cert-notyet.pub", SshCertificateProblem.NotYetValid)]
    [InlineData("cert-wrong-principal.pub", SshCertificateProblem.NoMatchingPrincipal)]
    [InlineData("cert-empty-principals.pub", SshCertificateProblem.NoMatchingPrincipal)]
    [InlineData("cert-user-cert.pub", SshCertificateProblem.WrongType)]
    [InlineData("cert-critical-option.pub", SshCertificateProblem.UnknownCriticalOption)]
    public async Task Certificate_FailingValidation_IsRejectedWithTheSpecificReason(string cert, SshCertificateProblem expected)
    {
        TrustCa("ca_ed25519.pub");

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo(cert));

        Assert.Equal(SshHostKeyRejectionReason.CertificateInvalid, rejection.Reason);
        Assert.Equal(expected, rejection.CertificateProblem);
    }

    [Fact]
    public async Task Certificate_ValidityUsesTheInjectedClock()
    {
        TrustCa();
        var info = OpenSshFixtures.CertificateInfo("cert-expired.pub");
        var cert = info.Certificate!;
        var inside = new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds((long)cert.ValidAfterUnixSeconds + 1));

        await Accepts(OpenSshHostKeyVerifier.Create(Options() with { TimeProvider = inside }), info);

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), info);
        Assert.Equal(SshCertificateProblem.Expired, rejection.CertificateProblem);
    }

    [Fact]
    public async Task Certificate_Sha1CaSignature_IsRejectedByDefault_AndAcceptedWhenOptedIn()
    {
        TrustCa("ca_rsa.pub");
        var info = OpenSshFixtures.CertificateInfo("cert-sha1-ca.pub");

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), info);
        Assert.Equal(SshCertificateProblem.DisallowedSignatureAlgorithm, rejection.CertificateProblem);

        await Accepts(OpenSshHostKeyVerifier.Create(Options() with { AllowSha1CaSignatures = true }), info);
    }

    [Fact]
    public async Task Certificate_WhenPolicyRequiresNoCertificate_StillFailsValidation()
    {
        TrustCa();
        var options = Options() with { RequireCertificateWhenCaCovers = false };

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(options), OpenSshFixtures.CertificateInfo("cert-expired.pub"));

        Assert.Equal(SshCertificateProblem.Expired, rejection.CertificateProblem);
    }

    [Fact]
    public async Task Certificate_MissingItsParsedFacts_FailsClosed()
    {
        TrustCa();
        var info = OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub") with { Certificate = null };

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), info);

        Assert.Equal(SshCertificateProblem.Malformed, rejection.CertificateProblem);
    }

    // ---- revocation of certificate-related keys ----

    [Fact]
    public async Task Certificate_WhoseCaIsRevoked_IsRejected_EvenIfAlsoTrusted()
    {
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"), Line("ca_ed25519.pub", "*", "@revoked"));

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
        Assert.Equal(2, rejection.KnownHostsLine);
    }

    [Fact]
    public async Task Certificate_WhoseCertifiedKeyIsRevoked_IsRejected()
    {
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"), Line("host_ed25519.pub", "*", "@revoked"));

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
    }

    [Fact]
    public async Task Certificate_WhoseCertifiedKeyIsRevoked_IsRejected_EvenWhenTheWireBlobWasNotCaptured()
    {
        // Fallback path: without the certificate blob only the key as the SSH library re-encoded it is available.
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"), Line("host_ed25519.pub", "*", "@revoked"));
        var info = OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub") with { CertificateBlob = null };

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), info);

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
    }

    [Fact]
    public async Task Certificate_ThatItselfIsRevoked_IsRejected()
    {
        var (type, blob) = OpenSshFixtures.ReadPublicKey("cert-valid-ed25519ca.pub");
        WriteKnownHosts(Line("ca_ed25519.pub", "*.example", "@cert-authority"), $"@revoked * {type} {Convert.ToBase64String(blob)}");

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.Revoked, rejection.Reason);
    }

    // ================= certificates: no CA covers the host yet =================

    [Fact]
    public async Task Certificate_NoCaCovers_AsksToTrustTheCa_ShowingItsFingerprintAndTheCertificate_ThenAppendsACertAuthorityLine()
    {
        var verifier = OpenSshHostKeyVerifier.Create(Options(port: 2222));
        var info = OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub");

        await Accepts(verifier, info);

        var question = Assert.Single(_prompt.CaQuestions);
        Assert.Equal(Host, question.Host);
        Assert.Equal(2222, question.Port);
        Assert.Equal("ssh-ed25519", question.KeyType);
        Assert.Equal(OpenSshFixtures.Key("ca_ed25519.pub").Sha256Fingerprint, question.Fingerprint); // the CA's, not the cert's or host key's
        Assert.Equal("id-valid-ed25519ca", question.CertificateKeyId);
        Assert.Equal([Host], question.CertificatePrincipals);
        Assert.Equal(1UL, question.CertificateSerial);
        Assert.Null(question.ValidBefore); // "forever"
        Assert.Empty(_prompt.HostKeyQuestions);

        // The CA is recorded — the certificate and the key it certifies are NOT pinned.
        Assert.Equal([Line("ca_ed25519.pub", $"[{Host}]:2222", "@cert-authority")], File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task Certificate_AfterTheCaIsRecorded_LaterConnectionsAreSilent_EvenAfterTheHostKeyRotates()
    {
        var verifier = OpenSshHostKeyVerifier.Create(Options());
        await Accepts(verifier, OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        // Renewal with a different host key (rsa), same CA: no new prompt.
        await Accepts(verifier, OpenSshFixtures.CertificateInfo("cert-valid-rsahostkey.pub"));
        await Accepts(verifier, OpenSshFixtures.CertificateInfo("cert-valid-ecdsahostkey.pub"));

        Assert.Single(_prompt.CaQuestions);
        Assert.Single(File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task Certificate_NoCaCovers_UserDeclines_IsRejected_AndNothingIsWritten()
    {
        _prompt.Answer = false;

        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.UserDeclined, rejection.Reason);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task Certificate_NoCaCovers_NoPrompt_IsRejected_UnknownAndNoPrompt()
    {
        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options(prompt: false)), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Equal(SshHostKeyRejectionReason.UnknownAndNoPrompt, rejection.Reason);
    }

    [Theory]
    [InlineData("cert-expired.pub", SshCertificateProblem.Expired)]
    [InlineData("cert-wrong-principal.pub", SshCertificateProblem.NoMatchingPrincipal)]
    [InlineData("cert-user-cert.pub", SshCertificateProblem.WrongType)]
    public async Task Certificate_NoCaCovers_ButItIsInvalid_IsRejectedWithoutBotheringTheUser(string cert, SshCertificateProblem expected)
    {
        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo(cert));

        Assert.Equal(expected, rejection.CertificateProblem);
        Assert.Empty(_prompt.CaQuestions);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task Certificate_ThatIsUnrelatedToPlainKeyRecords_DoesNotConsultThem()
    {
        // A plain entry for the certified key exists, but no CA: the certificate flow still asks about the CA.
        WriteKnownHosts(Line("host_ed25519.pub"));

        await Accepts(OpenSshHostKeyVerifier.Create(Options()), OpenSshFixtures.CertificateInfo("cert-valid-ed25519ca.pub"));

        Assert.Single(_prompt.CaQuestions);
    }

    // ================= global files, hashing, preflight =================

    [Fact]
    public async Task GlobalKnownHostsFile_IsConsulted_ButNeverWritten()
    {
        var global = _temp.File("ssh_known_hosts");
        File.WriteAllText(global, Line("host_ed25519.pub") + "\n");
        var options = Options() with { GlobalKnownHostsFiles = [global] };

        await Accepts(OpenSshHostKeyVerifier.Create(options), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));
        Assert.Empty(_prompt.HostKeyQuestions);

        // A change relative to the GLOBAL file is reported against that file.
        var rejection = await RejectionOf(OpenSshHostKeyVerifier.Create(options), OpenSshFixtures.PlainKeyInfo("ca_ed25519.pub"));
        Assert.Equal(Path.GetFullPath(global), rejection.KnownHostsFile);

        // A new host is recorded in the USER file only.
        var other = options with { Host = "new.example" };
        await Accepts(OpenSshHostKeyVerifier.Create(other), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));
        Assert.Single(File.ReadAllLines(global));
        Assert.Contains("new.example", File.ReadAllText(UserFile));
    }

    [Fact]
    public async Task Hashing_MatchFile_HashesNewEntriesOnlyWhenTheFileAlreadyUsesHashing()
    {
        File.Copy(OpenSshFixtures.Path("known_hosts_hashed"), UserFile);
        var options = Options() with { Host = "brandnew.example" };

        await Accepts(OpenSshHostKeyVerifier.Create(options), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        var lines = File.ReadAllLines(UserFile);
        Assert.StartsWith("|1|", lines[^1]);
        Assert.DoesNotContain("brandnew.example", File.ReadAllText(UserFile));
        Assert.Equal(TrustState.Known, OpenSshHostKeyVerifier.GetTrustState(options)); // and it still matches
    }

    [Theory]
    [InlineData(KnownHostsHashing.Never, false)]
    [InlineData(KnownHostsHashing.Always, true)]
    public async Task Hashing_ExplicitModesAreHonored(KnownHostsHashing mode, bool expectHashed)
    {
        await Accepts(OpenSshHostKeyVerifier.Create(Options() with { Hashing = mode }), OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"));

        Assert.Equal(expectHashed, File.ReadAllText(UserFile).StartsWith("|1|", StringComparison.Ordinal));
    }

    [Fact]
    public void GetTrustState_ReportsUnknownKnownAndCaCovered()
    {
        Assert.Equal(TrustState.Unknown, OpenSshHostKeyVerifier.GetTrustState(Options())); // file doesn't even exist

        WriteKnownHosts(Line("host_ed25519.pub"));
        Assert.Equal(TrustState.Known, OpenSshHostKeyVerifier.GetTrustState(Options()));
        Assert.Equal(TrustState.Unknown, OpenSshHostKeyVerifier.GetTrustState(Options(port: 2222))); // different name form

        WriteKnownHosts(Line("host_ed25519.pub"), Line("ca_ed25519.pub", "*.example", "@cert-authority"));
        Assert.Equal(TrustState.CaCovered, OpenSshHostKeyVerifier.GetTrustState(Options())); // CA wins

        WriteKnownHosts(Line("host_ed25519.pub", Host, "@revoked"));
        Assert.Equal(TrustState.Unknown, OpenSshHostKeyVerifier.GetTrustState(Options())); // a revocation isn't "known"
    }

    // ================= concurrency, cancellation, failure =================

    [Fact]
    public async Task ConcurrentHandshakesToOneUnknownHost_PromptOnce_AndEveryoneIsAccepted()
    {
        _prompt.Delay = TimeSpan.FromMilliseconds(200);
        var info = OpenSshFixtures.PlainKeyInfo("host_ed25519.pub");

        // Independent verifiers (as separate connections would create) sharing one file.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            Task.Run(async () => await OpenSshHostKeyVerifier.Create(Options())(info, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));

        Assert.All(results, Assert.True);
        Assert.Single(_prompt.HostKeyQuestions);
        Assert.Single(File.ReadAllLines(UserFile));
    }

    [Fact]
    public async Task ConcurrentHandshakes_WhenTheUserDeclines_AreAllRejected_AndNothingIsRecorded()
    {
        _prompt.Answer = false;
        var info = OpenSshFixtures.PlainKeyInfo("host_ed25519.pub");

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            try
            {
                return await OpenSshHostKeyVerifier.Create(Options())(info, TestContext.Current.CancellationToken);
            }
            catch (SshHostKeyRejectedException ex)
            {
                Assert.Equal(SshHostKeyRejectionReason.UserDeclined, ex.Reason);
                return false;
            }
        }, TestContext.Current.CancellationToken)));

        Assert.All(outcomes, Assert.False);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task CancellingWhileAPromptIsPending_DismissesIt_WritesNothing_AndSurfacesCancellation()
    {
        _prompt.Delay = TimeSpan.FromSeconds(30);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = OpenSshHostKeyVerifier.Create(Options())(OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"), cts.Token).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(File.Exists(UserFile));
    }

    [Fact]
    public async Task CancellingWhileWaitingBehindAnotherPrompt_ReturnsPromptly()
    {
        _prompt.Delay = TimeSpan.FromSeconds(30);
        using var firstCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var info = OpenSshFixtures.PlainKeyInfo("host_ed25519.pub");
        var first = OpenSshHostKeyVerifier.Create(Options())(info, firstCts.Token).AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        using var secondCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var second = OpenSshHostKeyVerifier.Create(Options())(info, secondCts.Token).AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        secondCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        firstCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
    }

    [Fact]
    public async Task AlreadyCancelled_ThrowsBeforeDoingAnything()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await OpenSshHostKeyVerifier.Create(Options())(OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"), new CancellationToken(canceled: true)));
        Assert.Empty(_prompt.HostKeyQuestions);
    }

    [Fact]
    public async Task IfTheTrustDecisionCannotBeRecorded_TheConnectionIsNotTrusted()
    {
        // A directory where the file should be: appending must fail, and the user's "yes" must not turn into silent, unrecorded trust.
        Directory.CreateDirectory(UserFile);
        var options = Options();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await OpenSshHostKeyVerifier.Create(options)(OpenSshFixtures.PlainKeyInfo("host_ed25519.pub"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("lab.example\n@cert-authority * ssh-ed25519 AAAA")]
    [InlineData("lab example")]
    [InlineData("a,b")]
    [InlineData("|1|salt|hash")]
    [InlineData("*.example")]
    [InlineData("[lab.example]:2222")]
    [InlineData("!lab.example")]
    [InlineData("lab.example	")]
    public void HostThatCouldAlterAKnownHostsLine_IsRefusedUpFront(string host)
    {
        Assert.Throws<ArgumentException>(() => OpenSshHostKeyVerifier.Create(new OpenSshHostKeyVerifierOptions { Host = host }));
        Assert.Throws<ArgumentException>(() => OpenSshHostKeyVerifier.GetTrustState(host));
    }

    [Theory]
    [InlineData("lab.example")]
    [InlineData("LAB-1.corp_net.example")]
    [InlineData("192.168.1.10")]
    [InlineData("fe80::1%eth0")]
    [InlineData("b00FCcher.example")]
    public void OrdinaryHostNamesAndAddresses_AreAccepted(string host) =>
        OpenSshHostKeyVerifier.Create(new OpenSshHostKeyVerifierOptions { Host = host });

    [Fact]
    public void Create_ValidatesItsOptions()
    {
        Assert.Throws<ArgumentException>(() => OpenSshHostKeyVerifier.Create(new OpenSshHostKeyVerifierOptions { Host = " " }));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenSshHostKeyVerifier.Create(new OpenSshHostKeyVerifierOptions { Host = "h", Port = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => OpenSshHostKeyVerifier.Create(new OpenSshHostKeyVerifierOptions { Host = "h", Port = 70000 }));
    }

    [Fact]
    public async Task ThroughTheTransportBridge_ARejectionReasonReachesTheCaller()
    {
        // The reason must survive the sync-over-async bridge the real handshake uses, not just direct verifier calls.
        WriteKnownHosts(Line("host_ed25519.pub"));
        var verification = new SshHostKeyVerification(
            new SshTransportOptions { Host = Host, Username = "u", RemoteUri = "x", VerifyHostKeyAsync = OpenSshHostKeyVerifier.Create(Options()) },
            TestContext.Current.CancellationToken);

        Assert.False(verification.Verify(OpenSshFixtures.PlainKeyInfo("ca_ed25519.pub")));

        var thrown = Assert.IsType<SshHostKeyRejectedException>(verification.MapConnectFailure(new Exception("generic"), Host, 22, "generic"));
        Assert.Equal(SshHostKeyRejectionReason.ChangedKey, thrown.Reason);
        Assert.Equal(1, thrown.KnownHostsLine);
        await Task.CompletedTask;
    }
}
