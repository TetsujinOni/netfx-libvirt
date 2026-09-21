using System.Text;
using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>Parsing, matching and appending against real <c>ssh-keygen</c>-produced
/// <c>known_hosts</c> files (including its own hashing), with the real
/// <c>ssh-keygen -F</c> as an oracle for "does ssh consider this host to be in this file".</summary>
public class OpenSshKnownHostsTests
{
    private static OpenSshKnownHosts LoadFixture(string name) => OpenSshKnownHosts.Load([OpenSshFixtures.Path(name)]);

    // ---- parsing ----

    [Fact]
    public void Parse_PlainFixture_KeepsSourceFileAndLineNumbersAndSkipsGarbageWithDiagnostics()
    {
        var path = OpenSshFixtures.Path("known_hosts_plain");
        var known = LoadFixture("known_hosts_plain");

        Assert.Equal(4, known.Entries.Count);
        Assert.All(known.Entries, e => Assert.Equal(path, e.SourceFile));
        Assert.Equal([3, 4, 5, 6], known.Entries.Select(e => e.LineNumber));
        Assert.Equal($"{path}:3", known.Entries[0].Location);
        Assert.Equal("comment for lab", known.Entries[0].Comment);
        Assert.Equal("ssh-ed25519", known.Entries[0].Key.KeyType);

        // The invalid line and the unknown marker are skipped — never trusted — and reported with where they are.
        Assert.Equal([7, 8], known.Diagnostics.Select(d => d.Line));
        Assert.Contains("bogus-marker", known.Diagnostics[1].Message);
    }

    [Fact]
    public void Parse_MarkersCommentsAndBlankLines()
    {
        var pub = OpenSshFixtures.Key("host_ed25519.pub");
        var text = $"""
            # a comment

            @cert-authority *.corp {pub.KeyType} {pub.ToBase64()} the corp CA
            @revoked bad.corp {pub.KeyType} {pub.ToBase64()}
              indented.corp   {pub.KeyType}   {pub.ToBase64()}
            """;
        var known = OpenSshKnownHosts.Parse(text, "mem");

        Assert.Equal(
            [KnownHostsMarker.CertificateAuthority, KnownHostsMarker.Revoked, KnownHostsMarker.None],
            known.Entries.Select(e => e.Marker));
        Assert.Equal([3, 4, 5], known.Entries.Select(e => e.LineNumber));
        Assert.Equal("the corp CA", known.Entries[0].Comment);
        Assert.Empty(known.Diagnostics);
    }

    [Fact]
    public void Parse_KeyTypeThatDisagreesWithTheBlob_IsRejected()
    {
        var pub = OpenSshFixtures.Key("host_ed25519.pub");
        var known = OpenSshKnownHosts.Parse($"h.example ssh-rsa {pub.ToBase64()}", "mem");

        Assert.Empty(known.Entries);
        Assert.Single(known.Diagnostics);
    }

    [Fact]
    public void Load_MissingFile_IsEmpty_ButUnreadableFileThrows()
    {
        using var temp = new TempDirectory();
        Assert.Empty(OpenSshKnownHosts.Load([temp.File("nope")]).Entries);

        // A directory where a file is expected: exists but can't be read → must not silently mean "no entries".
        Assert.ThrowsAny<Exception>(() => OpenSshKnownHosts.Load([temp.Path]));
    }

    [Fact]
    public void Load_FileHeldOpenForWritingByAnotherProcess_StillReads()
    {
        using var temp = new TempDirectory();
        var pub = OpenSshFixtures.Key("host_ed25519.pub");
        File.WriteAllText(temp.File("kh"), $"h.example {pub.KeyType} {pub.ToBase64()}\n");

        using var writer = new FileStream(temp.File("kh"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        Assert.Single(OpenSshKnownHosts.Load([temp.File("kh")]).Entries);
    }

    // ---- matching ----

    [Theory]
    [InlineData("lab.example", 22, true)]
    [InlineData("LAB.EXAMPLE", 22, true)]      // case-insensitive
    [InlineData("alias.example", 22, true)]    // second name in the list
    [InlineData("lab.example", 2222, true)]    // [lab.example]:2222 has its own entry
    [InlineData("alias.example", 2222, false)] // plain names don't match a non-22 port
    [InlineData("lab.example", 2223, false)]
    [InlineData("x.wild.example", 22, true)]   // wildcard
    [InlineData("bad.wild.example", 22, false)] // negation
    [InlineData("onlyrsa.example", 22, true)]
    [InlineData("nothere.example", 22, false)]
    public void FindEntries_MatchesHostPortWildcardAndNegation(string host, int port, bool expectFound) =>
        Assert.Equal(expectFound, LoadFixture("known_hosts_plain").FindEntries(host, port).Count > 0);

    [Theory]
    [InlineData("lab.example", 22, true)]
    [InlineData("LAB.example", 22, true)]
    [InlineData("lab.example", 2222, true)]
    [InlineData("lab.example", 2223, false)]
    [InlineData("other.example", 22, false)]
    public void FindEntries_HashedFixtureWrittenBySshKeygen(string host, int port, bool expectFound)
    {
        var known = LoadFixture("known_hosts_hashed");

        Assert.All(known.Entries, e => Assert.True(e.IsHashed));
        Assert.Equal(expectFound, known.FindEntries(host, port).Count > 0);
    }

    [Theory]
    [InlineData("known_hosts_plain", "lab.example", 22)]
    [InlineData("known_hosts_plain", "alias.example", 22)]
    [InlineData("known_hosts_plain", "lab.example", 2222)]
    [InlineData("known_hosts_plain", "alias.example", 2222)]
    [InlineData("known_hosts_plain", "x.wild.example", 22)]
    [InlineData("known_hosts_plain", "bad.wild.example", 22)]
    [InlineData("known_hosts_plain", "nothere.example", 22)]
    [InlineData("known_hosts_hashed", "lab.example", 22)]
    [InlineData("known_hosts_hashed", "lab.example", 2222)]
    [InlineData("known_hosts_hashed", "lab.example", 2223)]
    [InlineData("known_hosts_hashed", "other.example", 22)]
    public void FindEntries_AgreesWithRealSshKeygenFind(string file, string host, int port)
    {
        var name = port == 22 ? host : $"[{host}]:{port}";
        var oracle = OpenSshFixtures.RunSshKeygen("-F", name, "-f", OpenSshFixtures.Path(file));
        if (oracle is null)
        {
            Assert.Skip("ssh-keygen isn't on PATH.");
        }

        var sshSaysFound = oracle.Value.ExitCode == 0 && oracle.Value.Output.Contains("found: line");
        Assert.Equal(sshSaysFound, LoadFixture(file).FindEntries(host, port).Count > 0);
    }

    // ---- plain-key semantics ----

    [Fact]
    public void CheckKey_ExactKey_IsKnown()
    {
        var check = LoadFixture("known_hosts_plain").CheckKey("lab.example", 22, OpenSshFixtures.Key("host_ed25519.pub"));

        Assert.Equal(KnownHostKeyOutcome.Known, check.Outcome);
        Assert.Equal(3, check.Entry!.LineNumber);
    }

    [Fact]
    public void CheckKey_SameHostSameTypeDifferentKey_IsChanged_ReportingTheOffendingEntry()
    {
        // ca_ed25519 is an ed25519 key that is NOT lab.example's recorded ed25519 key.
        var impostor = OpenSshFixtures.Key("ca_ed25519.pub");

        var check = LoadFixture("known_hosts_plain").CheckKey("lab.example", 22, impostor);

        Assert.Equal(KnownHostKeyOutcome.Changed, check.Outcome);
        Assert.Equal(3, check.Entry!.LineNumber);
        Assert.Equal(OpenSshFixtures.Key("host_ed25519.pub").Sha256Fingerprint, check.Entry.Key.Sha256Fingerprint);
    }

    [Fact]
    public void CheckKey_HostKnownOnlyForOtherKeyTypes_IsUnknown_NotChanged()
    {
        // onlyrsa.example has an RSA entry; presenting an ed25519 key is "new", exactly as in ssh.
        var check = LoadFixture("known_hosts_plain").CheckKey("onlyrsa.example", 22, OpenSshFixtures.Key("host_ed25519.pub"));

        Assert.Equal(KnownHostKeyOutcome.Unknown, check.Outcome);
    }

    [Fact]
    public void CheckKey_AbsentHost_IsUnknown() =>
        Assert.Equal(KnownHostKeyOutcome.Unknown, LoadFixture("known_hosts_plain").CheckKey("nothere.example", 22, OpenSshFixtures.Key("host_ed25519.pub")).Outcome);

    [Fact]
    public void CheckKey_ExactMatchWinsEvenWhenAnotherEntryOfTheSameTypeDiffers()
    {
        var a = OpenSshFixtures.Key("host_ed25519.pub");
        var b = OpenSshFixtures.Key("ca_ed25519.pub");
        var known = OpenSshKnownHosts.Parse($"h.example {b.KeyType} {b.ToBase64()}\nh.example {a.KeyType} {a.ToBase64()}\n");

        Assert.Equal(KnownHostKeyOutcome.Known, known.CheckKey("h.example", 22, a).Outcome);
    }

    [Fact]
    public void CheckKey_ChangedOnANonStandardPort_UsesTheBracketedEntryOnly()
    {
        // [lab.example]:2222 has an RSA entry; lab.example:22's ed25519 entry must not be consulted for port 2222.
        var known = LoadFixture("known_hosts_plain");

        Assert.Equal(KnownHostKeyOutcome.Known, known.CheckKey("lab.example", 2222, OpenSshFixtures.Key("host_rsa.pub")).Outcome);
        Assert.Equal(KnownHostKeyOutcome.Unknown, known.CheckKey("lab.example", 2222, OpenSshFixtures.Key("host_ed25519.pub")).Outcome);
    }

    [Fact]
    public void CheckKey_RevokedKey_IsRevokedRegardlessOfOtherEntries()
    {
        var revoked = OpenSshKnownHosts.Load([OpenSshFixtures.Path("known_hosts_revoked")]);

        var check = revoked.CheckKey("anything.example", 22, OpenSshFixtures.Key("host_ed25519.pub")); // '@revoked *'
        Assert.Equal(KnownHostKeyOutcome.Revoked, check.Outcome);
        Assert.Equal(1, check.Entry!.LineNumber);

        // The second entry is scoped to other.example only.
        Assert.Equal(KnownHostKeyOutcome.Unknown, revoked.CheckKey("lab.example", 22, OpenSshFixtures.Key("host_rsa.pub")).Outcome);
        Assert.Equal(KnownHostKeyOutcome.Revoked, revoked.CheckKey("other.example", 22, OpenSshFixtures.Key("host_rsa.pub")).Outcome);
    }

    [Fact]
    public void CertificateAuthorities_AreFoundByHostPattern_AndNeverActAsPlainKeys()
    {
        var known = LoadFixture("known_hosts_ca");

        Assert.Single(known.FindCertificateAuthorities("lab.example", 22));       // *.example
        Assert.Empty(known.FindCertificateAuthorities("lab.other", 22));
        Assert.Single(known.FindCertificateAuthorities("lab.example", 2222));     // only the explicit [lab.example]:2222 line; *.example doesn't match "[lab.example]:2222"
        Assert.False(known.HasPlainEntries("lab.example", 22));
        Assert.Equal(KnownHostKeyOutcome.Unknown, known.CheckKey("lab.example", 22, OpenSshFixtures.Key("ca_ed25519.pub")).Outcome);
    }

    // ---- appending ----

    [Fact]
    public void Append_CreatesFile_WritesPlainLine_AndUsesBracketFormOffPort22()
    {
        using var temp = new TempDirectory();
        var file = temp.File("sub/known_hosts");
        var key = OpenSshFixtures.Key("host_ed25519.pub");

        OpenSshKnownHosts.Append(file, "Lab.Example", 22, key, KnownHostsMarker.None, hashHost: false);
        OpenSshKnownHosts.Append(file, "lab.example", 2222, key, KnownHostsMarker.CertificateAuthority, hashHost: false);

        var lines = File.ReadAllLines(file);
        Assert.Equal($"lab.example {key.KeyType} {key.ToBase64()}", lines[0]);
        Assert.Equal($"@cert-authority [lab.example]:2222 {key.KeyType} {key.ToBase64()}", lines[1]);
        Assert.Equal(2, lines.Length);
    }

    [Fact]
    public void Append_NeverRewrites_ExistingBytesStayAPrefix_AndAMissingTrailingNewlineIsHandled()
    {
        using var temp = new TempDirectory();
        var file = temp.File("known_hosts");
        var key = OpenSshFixtures.Key("host_ed25519.pub");
        var original = "# my hand-edited file\r\nold.example ssh-ed25519 AAAA  keep me"; // CRLF, odd spacing, NO trailing newline
        File.WriteAllBytes(file, new UTF8Encoding(false).GetBytes(original));

        OpenSshKnownHosts.Append(file, "new.example", 22, key, KnownHostsMarker.None, hashHost: false);

        var after = File.ReadAllBytes(file);
        var originalBytes = new UTF8Encoding(false).GetBytes(original);
        Assert.True(after.AsSpan().StartsWith(originalBytes), "existing content must be untouched");
        var appended = Encoding.UTF8.GetString(after.AsSpan(originalBytes.Length));
        Assert.Equal($"\nnew.example {key.KeyType} {key.ToBase64()}\n", appended);
    }

    [Fact]
    public void Append_WhileAnotherHandleHasTheFileOpenForReadWrite_Succeeds()
    {
        using var temp = new TempDirectory();
        var file = temp.File("known_hosts");
        File.WriteAllText(file, "");
        using var other = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        OpenSshKnownHosts.Append(file, "h.example", 22, OpenSshFixtures.Key("host_ed25519.pub"), KnownHostsMarker.None, hashHost: false);

        Assert.Single(OpenSshKnownHosts.Load([file]).Entries);
    }

    [Fact]
    public void Append_Hashed_ProducesALineOurMatcherAndRealSshKeygenBothFind()
    {
        using var temp = new TempDirectory();
        var file = temp.File("known_hosts");
        var key = OpenSshFixtures.Key("host_ed25519.pub");

        OpenSshKnownHosts.Append(file, "secret.example", 2222, key, KnownHostsMarker.None, hashHost: true);

        Assert.DoesNotContain("secret.example", File.ReadAllText(file));
        var known = OpenSshKnownHosts.Load([file]);
        Assert.True(known.Entries[0].IsHashed);
        Assert.True(known.FileUsesHashing(file));
        Assert.Equal(KnownHostKeyOutcome.Known, known.CheckKey("secret.example", 2222, key).Outcome);
        Assert.Equal(KnownHostKeyOutcome.Unknown, known.CheckKey("secret.example", 22, key).Outcome);

        var oracle = OpenSshFixtures.RunSshKeygen("-F", "[secret.example]:2222", "-f", file);
        if (oracle is not null)
        {
            Assert.Contains("found: line 1", oracle.Value.Output);
        }
    }

    [Fact]
    public void HashedLinesFromSshKeygenAndOurOwnAgreeOnWhatHashingMeans()
    {
        // ssh-keygen's hashed fixture matched via HMAC-SHA1(salt, "[host]:port" | "host"); ours must round-trip through the same rule.
        var fromSshKeygen = LoadFixture("known_hosts_hashed");
        var ours = OpenSshKnownHosts.Parse(OpenSshKnownHosts.FormatLine("lab.example", 22, OpenSshFixtures.Key("host_ed25519.pub"), KnownHostsMarker.None, hashHost: true));

        Assert.True(fromSshKeygen.Entries[0].MatchesHost("lab.example"));
        Assert.True(ours.Entries[0].MatchesHost("lab.example"));
        Assert.False(ours.Entries[0].MatchesHost("lab.example2"));
    }
}
