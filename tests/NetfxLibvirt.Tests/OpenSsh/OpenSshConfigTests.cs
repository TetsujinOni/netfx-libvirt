using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>Parsing and resolution against real files on disk, cross-checked against real <c>ssh -F ... -G</c>
/// wherever this parser's documented behavior matches real ssh (skipped if <c>ssh</c> isn't on PATH); the
/// deliberate divergences (<c>Match</c>, nested <c>Include</c>, tilde-expanding <c>IdentityFile</c>/
/// <c>GlobalKnownHostsFile</c>) are asserted directly instead, since there's no oracle for "what we
/// intentionally do differently and why" — see <see cref="OpenSshConfig"/>'s class doc.
///
/// Two things the oracle can't be compared against byte-for-byte, found while writing these tests:
/// <c>User</c> (and any other defaultable field) is <see langword="null"/> in our model when unset, but
/// <c>ssh -G</c> always prints a resolved value (falling back to the local account for <c>User</c>) — so
/// only compare when the config actually sets it; and the specific <c>ssh</c> resolved from `PATH` in this
/// environment prints tilde-expanded paths in POSIX form (<c>/c/Users/...</c>), not the Windows form (<c>C:\Users\...</c>)
/// this library needs to actually open files — so tilde-involving paths are checked for shape, not byte-equality.</summary>
public class OpenSshConfigTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string ConfigFile => _temp.File("config");

    private OpenSshConfig LoadConfig(string content)
    {
        File.WriteAllText(ConfigFile, content);
        return OpenSshConfig.Load(ConfigFile);
    }

    /// <summary>Resolves both through our parser and through real <c>ssh -G</c>, asserting our value matches the oracle's for every keyword in <paramref name="keywords"/> (skips the assertion, not the whole test, if <c>ssh</c> is unavailable). Only for keywords whose unset/defaulted representation agrees between the two — see the class doc.</summary>
    private void AssertMatchesOracle(string content, string host, params (string Keyword, Func<OpenSshConfigHost, string?> Ours)[] keywords)
    {
        var resolved = LoadConfig(content).Resolve(host);
        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, host);
        if (oracle is null)
        {
            Assert.Skip("ssh isn't on PATH.");
        }

        Assert.Equal(0, oracle.Value.ExitCode);
        foreach (var (keyword, ours) in keywords)
        {
            var expected = OpenSshFixtures.SshConfigDumpValue(oracle.Value.Output, keyword);
            Assert.Equal(expected, ours(resolved));
        }
    }

    // ---- basic directives, each cross-checked against real ssh -G ----

    [Fact]
    public void HostName_User_Port_MatchRealSsh()
    {
        var content = """
            Host lab
                HostName lab.example.com
                User admin
                Port 2222
            """;

        AssertMatchesOracle(
            content, "lab",
            ("hostname", r => r.HostName),
            ("user", r => r.User),
            ("port", r => r.Port?.ToString()));
    }

    [Fact]
    public void IdentityFile_IsTildeExpanded_UnlikeRawSshDump_ButShapeMatches()
    {
        // ssh -G leaves IdentityFile un-expanded (confirmed); we expand it, since a consumer needs to open the file.
        var resolved = LoadConfig("Host lab\n  IdentityFile ~/.ssh/id_ed25519\n").Resolve("lab");

        Assert.Single(resolved.IdentityFiles);
        Assert.False(resolved.IdentityFiles[0].StartsWith('~'));
        Assert.EndsWith(Path.Combine(".ssh", "id_ed25519"), resolved.IdentityFiles[0]);
    }

    [Fact]
    public void NoIdentityFileConfigured_FallsBackToSshsBuiltInCandidates_FilteredToOnesThatExist()
    {
        var sshDir = _temp.File("sshdir");
        Directory.CreateDirectory(sshDir);
        File.WriteAllText(Path.Combine(sshDir, "id_ed25519"), "fake key");
        File.WriteAllText(Path.Combine(sshDir, "id_ecdsa_sk"), "fake key"); // out of the real candidate order, to prove order isn't just "as created"

        var candidates = OpenSshConfig.DefaultIdentityFilesUnder(sshDir).ToList();

        // ssh's own order: id_rsa, id_ecdsa, id_ecdsa_sk, id_ed25519, id_ed25519_sk — missing ones simply absent.
        Assert.Equal(
            [Path.Combine(sshDir, "id_ecdsa_sk"), Path.Combine(sshDir, "id_ed25519")],
            candidates);
    }

    [Fact]
    public void NoIdentityFileConfigured_Resolve_IsWiredToTheRealProfileSshDirectory()
    {
        // What's actually in %USERPROFILE%\.ssh varies by machine (covered hermetically by
        // DefaultIdentityFilesUnder's own test above) — here we only confirm Resolve()'s fallback routes to
        // exactly that directory, by comparing against DefaultIdentityFilesUnder's own answer for it, whatever
        // that happens to be. A test asserting Resolve() returns a fixed literal list would be non-portable.
        var resolved = LoadConfig("Host lab\n  HostName lab.example\n").Resolve("lab");
        var expected = OpenSshConfig.DefaultIdentityFilesUnder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));

        Assert.Equal(expected, resolved.IdentityFiles);
    }

    [Fact]
    public void ExplicitIdentityFile_IsNeverFilteredByExistence_UnlikeTheDefaultFallback()
    {
        // Confirmed against real ssh -G: an explicitly configured (even nonexistent) IdentityFile is still
        // offered — only the IMPLICIT defaults get filtered. A typo in a real config should stay visible.
        var resolved = LoadConfig("Host lab\n  IdentityFile ~/.ssh/definitely_does_not_exist_anywhere\n").Resolve("lab");

        Assert.Single(resolved.IdentityFiles);
        Assert.EndsWith("definitely_does_not_exist_anywhere", resolved.IdentityFiles[0]);
    }

    [Fact]
    public void UnsetHostName_DefaultsToTheHostArgumentItself_MatchingRealSsh()
    {
        AssertMatchesOracle("Host lab\n  User admin\n", "lab", ("hostname", r => r.HostName));
        Assert.Equal("lab", LoadConfig("Host lab\n  User admin\n").Resolve("lab").HostName);
    }

    [Fact]
    public void IdentityFile_AccumulatesAcrossMatchingBlocks_InFileOrder_MatchingRealSsh()
    {
        var content = """
            Host lab
                IdentityFile ~/.ssh/id_ed25519

            Host *
                IdentityFile ~/.ssh/id_rsa
            """;
        var resolved = LoadConfig(content).Resolve("lab");

        Assert.Equal(2, resolved.IdentityFiles.Count);
        Assert.EndsWith("id_ed25519", resolved.IdentityFiles[0]);
        Assert.EndsWith("id_rsa", resolved.IdentityFiles[1]);

        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, "lab");
        if (oracle is not null)
        {
            var lines = oracle.Value.Output.Split('\n').Count(l => l.TrimStart().StartsWith("identityfile ", StringComparison.Ordinal));
            Assert.Equal(2, lines);
        }
    }

    [Fact]
    public void SingleValuedDirectives_UseFirstMatchInFileOrder_AcrossBlocks_MatchingRealSsh()
    {
        var content = """
            Host lab
                Port 2222

            Host *
                Port 22
            """;

        AssertMatchesOracle(content, "lab", ("port", r => r.Port?.ToString()));
        Assert.Equal(2222, LoadConfig(content).Resolve("lab").Port);
    }

    [Fact]
    public void HostName_UsesFirstMatchInFileOrder_AcrossBlocks_NotLastMatch()
    {
        var content = "Host lab\n  HostName first.example\n\nHost *\n  HostName second.example\n";

        Assert.Equal("first.example", LoadConfig(content).Resolve("lab").HostName);
    }

    [Fact]
    public void DirectivesBeforeAnyHostLine_ApplyUnconditionally_LikeALeadingHostStar_MatchingRealSsh()
    {
        var content = "Port 2200\n\nHost lab\n  HostName lab.example\n";

        AssertMatchesOracle(content, "lab", ("port", r => r.Port?.ToString()));
        Assert.Equal(2200, LoadConfig(content).Resolve("lab").Port);
        // Applies to any host, not just ones with their own Host block.
        Assert.Equal(2200, LoadConfig(content).Resolve("unrelated.example").Port);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("ask")]
    [InlineData("accept-new")]
    public void IdentitiesOnly_And_StrictHostKeyChecking_MatchRealSsh(string strict)
    {
        var content = $"Host lab\n  IdentitiesOnly yes\n  StrictHostKeyChecking {strict}\n  HashKnownHosts yes\n  ConnectTimeout 15\n";

        AssertMatchesOracle(
            content, "lab",
            ("identitiesonly", r => r.IdentitiesOnly is true ? "yes" : "no"),
            ("hashknownhosts", r => r.HashKnownHosts is true ? "yes" : "no"),
            ("connecttimeout", r => r.ConnectTimeout?.TotalSeconds.ToString("F0")));

        var expected = strict switch
        {
            "yes" => OpenSshStrictHostKeyChecking.Yes,
            "no" => OpenSshStrictHostKeyChecking.No,
            "ask" => OpenSshStrictHostKeyChecking.Ask,
            _ => OpenSshStrictHostKeyChecking.AcceptNew,
        };
        Assert.Equal(expected, LoadConfig(content).Resolve("lab").StrictHostKeyChecking);
    }

    [Fact]
    public void UserKnownHostsFile_IsTildeExpanded_ToAnAbsolutePath()
    {
        // Both real ssh and we expand this one's tilde (confirmed) — but the specific `ssh` resolved from PATH
        // here prints it POSIX-style (/c/Users/...), not the Windows form this library needs, so shape not bytes.
        var resolved = LoadConfig("Host lab\n  UserKnownHostsFile ~/.ssh/known_hosts_lab\n").Resolve("lab");

        Assert.Single(resolved.UserKnownHostsFile!);
        Assert.False(resolved.UserKnownHostsFile![0].StartsWith('~'));
        Assert.True(Path.IsPathFullyQualified(resolved.UserKnownHostsFile[0]));
        Assert.EndsWith(Path.Combine(".ssh", "known_hosts_lab"), resolved.UserKnownHostsFile[0]);
    }

    [Fact]
    public void UserKnownHostsFile_SupportsMultiplePaths()
    {
        var resolved = LoadConfig("Host lab\n  UserKnownHostsFile ~/.ssh/a ~/.ssh/b\n").Resolve("lab");

        Assert.Equal(2, resolved.UserKnownHostsFile!.Count);
        Assert.EndsWith("a", resolved.UserKnownHostsFile[0]);
        Assert.EndsWith("b", resolved.UserKnownHostsFile[1]);
    }

    [Fact]
    public void GlobalKnownHostsFile_TildeExpanded_OurOwnBehavior_DivergesFromRawSshDump()
    {
        // Documented divergence: real `ssh -G` prints GlobalKnownHostsFile's value un-expanded (unlike
        // UserKnownHostsFile, confirmed) — a display quirk of `-G`, not something a real client benefits from.
        // We expand it because a consumer needs to actually open the file.
        var resolved = LoadConfig("Host lab\n  GlobalKnownHostsFile ~/.ssh/gkh\n").Resolve("lab");

        Assert.Single(resolved.GlobalKnownHostsFiles);
        Assert.False(resolved.GlobalKnownHostsFiles[0].StartsWith('~'));

        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, "lab");
        if (oracle is not null)
        {
            Assert.Equal("~/.ssh/gkh", OpenSshFixtures.SshConfigDumpValue(oracle.Value.Output, "globalknownhostsfile"));
        }
    }

    [Fact]
    public void GlobalKnownHostsFile_MultiplePaths_OneDirective_IsFirstMatchWinsAsAWhole()
    {
        var content = "Host lab\n  GlobalKnownHostsFile /a/one /a/two\n\nHost *\n  GlobalKnownHostsFile /b/three\n";
        var resolved = LoadConfig(content).Resolve("lab");

        Assert.Equal(["/a/one", "/a/two"], resolved.GlobalKnownHostsFiles);
    }

    [Fact]
    public void HostKeyAlias_IsResolved_AndDrivesHostKeyLookupName_MatchingRealSsh()
    {
        var content = "Host lab\n  HostName 127.0.0.1\n  HostKeyAlias lab-real\n";

        AssertMatchesOracle(content, "lab", ("hostname", r => r.HostName), ("hostkeyalias", r => r.HostKeyAlias));

        var resolved = LoadConfig(content).Resolve("lab");
        Assert.Equal("lab-real", resolved.HostKeyLookupName);
    }

    [Fact]
    public void NoHostKeyAlias_HostKeyLookupNameFallsBackToHostName()
    {
        var resolved = LoadConfig("Host lab\n  HostName lab.example\n").Resolve("lab");

        Assert.Null(resolved.HostKeyAlias);
        Assert.Equal("lab.example", resolved.HostKeyLookupName);
    }

    // ---- Host pattern matching: reuses OpenSshPattern's glob, cross-checked against real ssh -G ----

    [Theory]
    [InlineData("a.example", "user1")]
    [InlineData("b.example", "user1")]
    [InlineData("other.com", null)]
    public void WildcardHostPatterns_MatchRealSsh(string host, string? expectedUser)
    {
        var content = "Host *.example\n  User user1\n";

        var resolved = LoadConfig(content).Resolve(host);
        Assert.Equal(expectedUser, resolved.User);

        // The oracle's User is never null (falls back to the local account when unset) — compare "did the
        // block's value win", not raw equality; see the class doc.
        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, host);
        if (oracle is not null)
        {
            Assert.Equal(expectedUser == "user1", OpenSshFixtures.SshConfigDumpValue(oracle.Value.Output, "user") == "user1");
        }
    }

    [Theory]
    [InlineData("good.example", "user1")]
    [InlineData("bad.example", null)]
    public void NegatedHostPatterns_MatchRealSsh(string host, string? expectedUser)
    {
        var content = "Host *.example !bad.example\n  User user1\n";

        var resolved = LoadConfig(content).Resolve(host);
        Assert.Equal(expectedUser, resolved.User);

        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, host);
        if (oracle is not null)
        {
            Assert.Equal(expectedUser == "user1", OpenSshFixtures.SshConfigDumpValue(oracle.Value.Output, "user") == "user1");
        }
    }

    [Fact]
    public void HostPatternMatching_IsCaseSensitive_UnlikeKnownHostsMatching_ConfirmedAgainstRealSsh()
    {
        // Surprising, easy to assume otherwise (known_hosts/principal matching IS case-insensitive) — see
        // OpenSshPattern's class doc. Confirmed directly: `ssh -F ... -G` against `Host LAB` matches a query
        // of "LAB" but not "lab".
        var content = "Host LAB\n  User admin\n";

        Assert.Equal("admin", LoadConfig(content).Resolve("LAB").User);
        Assert.Null(LoadConfig(content).Resolve("lab").User);

        var oracleExact = OpenSshFixtures.RunSshConfigDump(ConfigFile, "LAB");
        var oracleLower = OpenSshFixtures.RunSshConfigDump(ConfigFile, "lab");
        if (oracleExact is not null && oracleLower is not null)
        {
            Assert.Equal("admin", OpenSshFixtures.SshConfigDumpValue(oracleExact.Value.Output, "user"));
            Assert.NotEqual("admin", OpenSshFixtures.SshConfigDumpValue(oracleLower.Value.Output, "user"));
        }
    }

    // ---- Include: top-level, matches real ssh ----

    [Fact]
    public void TopLevelInclude_AbsolutePath_AppliesTheIncludedHostBlock()
    {
        // Not cross-checked against the oracle: the `ssh` resolved from PATH here has its own bug/quirk
        // resolving a drive-letter-absolute Include path (confirmed manually: it silently treats "C:\..." as
        // relative and prepends "~/.ssh/", so the Include always "matches no files" for it) — this library's
        // own path handling (Path.IsPathFullyQualified) doesn't have that problem.
        var includeDir = _temp.File("confd");
        Directory.CreateDirectory(includeDir);
        File.WriteAllText(Path.Combine(includeDir, "extra.conf"), "Host extra\n  HostName extra.example.com\n");
        File.WriteAllText(ConfigFile, $"Include {includeDir}\\*.conf\n\nHost lab\n  HostName lab.example.com\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Empty(config.Diagnostics);
        Assert.Equal("extra.example.com", config.Resolve("extra").HostName);
        Assert.Equal("lab.example.com", config.Resolve("lab").HostName); // the rest of the file still parses
    }

    [Fact]
    public void TopLevelInclude_RelativePath_ResolvesAgainstUserSshDirectory_NotTheIncludingFilesDirectory()
    {
        // Documented, confirmed-against-real-ssh behavior: a bare relative Include path is ~/.ssh-relative,
        // never relative to the including file — so a relative Include from THIS temp file resolves against the
        // real machine's ~/.ssh, not _temp. We only assert it does NOT silently resolve against _temp (which
        // would be the "looks right, is wrong" bug this design note exists to avoid).
        Directory.CreateDirectory(_temp.File("confd"));
        File.WriteAllText(_temp.File("confd/relative.conf"), "Host relhost\n  HostName should-not-apply\n");
        File.WriteAllText(ConfigFile, "Include confd/relative.conf\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Equal("relhost", config.Resolve("relhost").HostName); // unresolved: defaults to the alias itself
    }

    [Fact]
    public void Include_MissingDirectory_IsSilentlyEmpty_NotAnError()
    {
        File.WriteAllText(ConfigFile, $"Include {_temp.File("nonexistent")}\\*.conf\n\nHost lab\n  User admin\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Empty(config.Diagnostics);
        Assert.Equal("admin", config.Resolve("lab").User);
    }

    [Fact]
    public void Include_SelfCycle_IsDetected_AndDoesNotHangOrThrow()
    {
        File.WriteAllText(ConfigFile, $"Include {ConfigFile}\n\nHost lab\n  User admin\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Equal("admin", config.Resolve("lab").User); // still resolves the rest of the file once
        Assert.Contains(config.Diagnostics, d => d.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Include_MutualCycle_IsDetected_AndDoesNotHangOrThrow()
    {
        var fileB = _temp.File("b.conf");
        File.WriteAllText(ConfigFile, $"Include {fileB}\n\nHost lab\n  User fromA\n");
        File.WriteAllText(fileB, $"Include {ConfigFile}\n\nHost other\n  User fromB\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Equal("fromA", config.Resolve("lab").User);
        Assert.Equal("fromB", config.Resolve("other").User);
        Assert.Contains(config.Diagnostics, d => d.Message.Contains("cycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Include_WithNoBaseDirectory_IsDiagnosedNotThrown_WhenParsingInMemoryContentDirectly()
    {
        var config = OpenSshConfig.Parse("Include foo.conf\n\nHost lab\n  User admin\n");

        Assert.Contains(config.Diagnostics, d => d.Message.Contains("base directory", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("admin", config.Resolve("lab").User);
    }

    // ---- deliberate divergences from real ssh: asserted directly, not against the oracle ----

    [Fact]
    public void MatchLines_AreNotEvaluated_UnlikeRealSsh_AndProduceADiagnostic()
    {
        // Real ssh DOES apply "Match host lab" here (confirmed: port comes out 9999). This library
        // deliberately does not implement Match at all (see class doc) — so our Port stays unset,
        // and the directives under Match are correctly NOT attributed to the following Host block either.
        var content = "Match host lab\n  Port 9999\n\nHost lab\n  HostName lab.example\n";

        var config = LoadConfig(content);
        var resolved = config.Resolve("lab");

        Assert.Null(resolved.Port);
        Assert.Equal("lab.example", resolved.HostName); // the real Host block below Match is unaffected
        Assert.Contains(config.Diagnostics, d => d.Message.Contains("Match", StringComparison.Ordinal));

        var oracle = OpenSshFixtures.RunSshConfigDump(ConfigFile, "lab");
        if (oracle is not null)
        {
            Assert.Equal("9999", OpenSshFixtures.SshConfigDumpValue(oracle.Value.Output, "port")); // real ssh: different from us, by design
        }
    }

    [Fact]
    public void IncludeInsideAHostBlock_IsSkippedWithADiagnostic_UnlikeRealSsh()
    {
        // Real ssh ANDs the included file's Host patterns with the enclosing block's — subtle enough that
        // approximating it would risk silently misapplying trust-relevant settings. We just don't do it.
        var includeDir = _temp.File("confd2");
        Directory.CreateDirectory(includeDir);
        File.WriteAllText(Path.Combine(includeDir, "nested.conf"), "Host nested\n  User fromNested\n");
        File.WriteAllText(ConfigFile, $"Host *.corp\n  User corpuser\n  Include {includeDir}\\*.conf\n");

        var config = OpenSshConfig.Load(ConfigFile);

        Assert.Null(config.Resolve("nested").User);
        Assert.Contains(config.Diagnostics, d => d.Message.Contains("Include", StringComparison.Ordinal) && d.Message.Contains("Host/Match", StringComparison.Ordinal));
    }

    // ---- resilience: malformed input never throws, always produces a diagnostic at parse time ----

    [Theory]
    [InlineData("Host lab\n  Port notanumber\n")]
    [InlineData("Host lab\n  Port 0\n")]
    [InlineData("Host lab\n  Port 70000\n")]
    [InlineData("Host lab\n  IdentitiesOnly maybe\n")]
    [InlineData("Host lab\n  StrictHostKeyChecking sometimes\n")]
    [InlineData("Host\n  User admin\n")]
    [InlineData("Include\n")]
    [InlineData("=noKeywordBeforeEquals\n")]
    public void MalformedLines_NeverThrow_AndAreDiagnosedAtParseTime(string content)
    {
        // At PARSE time, not only when a matching host happens to be resolved — matches OpenSshKnownHosts'
        // precedent: Diagnostics is complete right after Load()/Parse().
        var config = LoadConfig(content);

        Assert.NotEmpty(config.Diagnostics);
    }

    [Fact]
    public void MalformedValue_IsExcludedFromResolution_NotJustDiagnosed()
    {
        var config = LoadConfig("Host lab\n  Port notanumber\n  User admin\n");

        var resolved = config.Resolve("lab");
        Assert.Null(resolved.Port); // the bad Port line contributes nothing — never half-applied
        Assert.Equal("admin", resolved.User); // the rest of the block is unaffected
    }

    [Fact]
    public void UnrecognizedKeywords_AreSilentlyIgnored_NotDiagnosed_MatchingRealSshsTolerance()
    {
        var content = "Host lab\n  Ciphers aes256-gcm@openssh.com\n  ForwardAgent yes\n  HostName lab.example\n";

        var config = LoadConfig(content);

        Assert.Empty(config.Diagnostics);
        Assert.Equal("lab.example", config.Resolve("lab").HostName);
    }

    [Fact]
    public void MissingFile_ResolvesEmpty_NotAnError()
    {
        var config = OpenSshConfig.Load(_temp.File("does-not-exist"));

        Assert.Empty(config.Diagnostics);
        var resolved = config.Resolve("anything");
        Assert.Equal("anything", resolved.HostName);
        Assert.Null(resolved.User);
        // Not asserting IdentityFiles here: an empty config still falls back to ssh's built-in default
        // candidates (see NoIdentityFileConfigured_FallsBackToSshsBuiltInCandidates_...), which legitimately
        // depend on what's in the REAL %USERPROFILE%\.ssh on whatever machine runs this test.
    }

    [Fact]
    public void UnreadableFile_Throws_NotSilentlyEmpty()
    {
        Directory.CreateDirectory(ConfigFile); // a directory where a file is expected: exists, can't be read as one

        Assert.ThrowsAny<Exception>(() => OpenSshConfig.Load(ConfigFile));
    }

    // ---- syntax details ----

    [Theory]
    [InlineData("HostName lab.example")]
    [InlineData("HostName=lab.example")]
    [InlineData("HostName = lab.example")]
    [InlineData("  HostName\tlab.example  ")]
    [InlineData("hostname lab.example")]
    [InlineData("HOSTNAME lab.example")]
    public void KeywordValueSyntax_Variants_AllParse(string directiveLine)
    {
        var resolved = LoadConfig($"Host lab\n  {directiveLine}\n").Resolve("lab");

        Assert.Equal("lab.example", resolved.HostName);
    }

    [Fact]
    public void CommentsAndBlankLines_AreSkipped()
    {
        var content = "# a comment\n\n  # indented comment\nHost lab\n  # another\n  User admin\n\n";

        var config = LoadConfig(content);

        Assert.Empty(config.Diagnostics);
        Assert.Equal("admin", config.Resolve("lab").User);
    }

    [Fact]
    public void QuotedValue_WithASpace_IsSupportedForListDirectives()
    {
        var resolved = LoadConfig("""
            Host lab
              IdentityFile "a path/with a space/id_ed25519"
            """).Resolve("lab");

        Assert.Equal(["a path/with a space/id_ed25519"], resolved.IdentityFiles);
    }

    [Fact]
    public void Parse_InMemory_WithoutAFile_StillResolves()
    {
        var config = OpenSshConfig.Parse("Host lab\n  User admin\n");

        Assert.Equal("admin", config.Resolve("lab").User);
    }
}
