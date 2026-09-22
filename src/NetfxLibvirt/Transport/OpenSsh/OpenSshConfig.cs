using System.Text;

namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>ssh_config's <c>StrictHostKeyChecking</c> values.</summary>
public enum OpenSshStrictHostKeyChecking
{
    /// <summary><c>yes</c>/<c>true</c>: never connect to a host whose key isn't already known.</summary>
    Yes,

    /// <summary><c>no</c>/<c>false</c>: silently accept and record any key — dangerous; exposed as data, this library never acts on it by itself.</summary>
    No,

    /// <summary><c>ask</c> (OpenSSH's default): prompt for an unknown key.</summary>
    Ask,

    /// <summary><c>accept-new</c>: silently accept and record a NEW host, but still refuse a CHANGED one.</summary>
    AcceptNew,
}

/// <summary>A line that couldn't be interpreted, or a construct this parser doesn't evaluate (see <see cref="OpenSshConfig"/>'s class doc). Never affects resolution — a skipped/diagnosed line is simply not applied.</summary>
public sealed record OpenSshConfigDiagnostic(string File, int Line, string Message);

/// <summary>The settings resolved for one host: the first value set by a matching block wins for
/// every field except <see cref="IdentityFiles"/>, which accumulates across every matching block,
/// in file order — both matching OpenSSH's own "first obtained value is used" rule (`ssh_config(5)`),
/// confirmed against real <c>ssh -G</c>. <see langword="null"/>/empty means nothing in the config set it.</summary>
/// <param name="HostName">Defaults to the host name passed to <see cref="OpenSshConfig.Resolve"/> itself when unset — this is what OpenSSH does (confirmed via <c>ssh -G</c>), so it is filled in here rather than left <see langword="null"/>.</param>
/// <param name="HostKeyAlias">**Use this (falling back to <see cref="HostName"/>) as the host for `known_hosts`/certificate lookups, not the alias the user typed** — this is exactly what `HostKeyAlias` is for (`ssh_config(5)`): letting host-key trust follow a stable identity when the actual network target varies (port forwards, load balancers, `HostName 127.0.0.1`, …).</param>
/// <param name="IdentityFiles">Tilde-expanded to an absolute path; any other <c>%</c>-token (`%d`, `%h`, `%r`, …) is left as literal text — confirmed <c>ssh -G</c> does the same (token substitution happens later, at connect time in real ssh, not at config-resolution time). When no <c>IdentityFile</c> directive matches, falls back to ssh's own built-in candidates under <c>~/.ssh</c> (<c>id_rsa</c>, <c>id_ecdsa</c>, <c>id_ecdsa_sk</c>, <c>id_ed25519</c>, <c>id_ed25519_sk</c> — confirmed against real <c>ssh -G</c>), filtered to ones that exist; an EXPLICITLY configured entry is never filtered this way, so a typo stays visible instead of silently disappearing. Never build an <c>IPrivateKeySource</c> per candidate one connection attempt at a time — see <see cref="NetfxLibvirt.Transport.SshTransportOptions.PrivateKeyPaths"/>, which offers every candidate within a single SSH session.</param>
/// <param name="UserKnownHostsFile">Space-separated in the file; tilde-expanded. <see langword="null"/> if unset (use <see cref="OpenSshKnownHosts.DefaultUserFilePath"/>).</param>
/// <param name="GlobalKnownHostsFiles">Tilde-expanded; empty if unset (use <see cref="OpenSshKnownHosts.DefaultGlobalFilePaths"/>).</param>
public sealed record OpenSshConfigHost(
    string HostName,
    string? HostKeyAlias,
    string? User,
    int? Port,
    IReadOnlyList<string> IdentityFiles,
    bool? IdentitiesOnly,
    IReadOnlyList<string>? UserKnownHostsFile,
    IReadOnlyList<string> GlobalKnownHostsFiles,
    OpenSshStrictHostKeyChecking? StrictHostKeyChecking,
    bool? HashKnownHosts,
    TimeSpan? ConnectTimeout)
{
    /// <summary>What a host-key verifier should treat as "the host" — see <see cref="HostKeyAlias"/>'s doc.</summary>
    public string HostKeyLookupName => HostKeyAlias ?? HostName;
}

/// <summary>
/// Enough of OpenSSH's <c>ssh_config(5)</c> to resolve a <c>Host</c> alias to real connection
/// parameters — the "consumer supplies a host name they'd type at an <c>ssh</c> prompt, gets back
/// what <c>ssh</c> itself would use" case, verified directly against real <c>ssh -G</c> output
/// (Windows OpenSSH 9.5+) rather than assumed from the man page.
///
/// **Supported:** <c>Host</c> blocks with the same pattern-list syntax as `known_hosts`
/// (<see cref="OpenSshPattern"/> — wildcards and <c>!</c>negation, reused rather than reimplemented);
/// directives before the first <c>Host</c> line apply unconditionally, exactly like a leading
/// <c>Host *</c> (confirmed); <c>HostName</c>, <c>User</c>, <c>Port</c>, <c>IdentityFile</c>
/// (cumulative), <c>IdentitiesOnly</c>, <c>HostKeyAlias</c>, <c>UserKnownHostsFile</c>,
/// <c>GlobalKnownHostsFile</c>, <c>StrictHostKeyChecking</c>, <c>HashKnownHosts</c>,
/// <c>ConnectTimeout</c>; top-level <c>Include</c> (glob-expanded, relative paths resolved against
/// <c>~/.ssh</c> like real ssh, cycle- and depth-guarded). Any other keyword is recognized as a line
/// (so it doesn't get mistaken for a stray value) and otherwise ignored — real config files are full
/// of directives (`Ciphers`, `ForwardAgent`, …) this library has no use for, and OpenSSH itself
/// tolerates unknown-to-a-given-version keywords the same way.
///
/// **Deliberately NOT supported, unlike real `ssh`:**
/// - **`Match`** (any form — `host`, `user`, `exec`, `canonical`, …). `Match exec` runs an arbitrary
///   shell command *during config resolution*; this library will not do that. Rather than implement
///   only the "safe-looking" forms and leave a config file that silently behaves differently under
///   this library than under real `ssh`, a `Match` line is recorded as a block boundary (so directives
///   after it are correctly NOT attributed to the preceding `Host` block) but never treated as
///   matching — with a <see cref="Diagnostics"/> entry saying so.
/// - **`Include` inside a `Host`/`Match` block.** Real `ssh_config` ANDs an included file's own `Host`
///   patterns with whatever block the `Include` line was nested in (`ssh_config(5)`) — subtle enough
///   that getting it wrong silently would be worse than not supporting it. A nested `Include` is
///   skipped with a diagnostic; a **top-level** one (the common case: config.d-style snippets) behaves
///   exactly like real `ssh`, confirmed.
/// - `%`-token expansion (`%d`, `%h`, `%n`, `%r`, …) beyond `~` — see
///   <see cref="OpenSshConfigHost.IdentityFiles"/>'s doc.
/// - `ProxyJump`/`ProxyCommand`, `CanonicalizeHostname`, algorithm-list directives
///   (`+`/`-`/`^`-prefixed `Ciphers`/`KexAlgorithms`/…), `Match`/`Include` combined with wildcards
///   spanning multiple config roots. Recorded in `docs/plan.md`'s backlog.
///
/// Unlike `OpenSshKnownHosts`, a genuinely malformed line here (bad `Port`, unrecognized
/// `StrictHostKeyChecking` value, an empty `Host`/`Include`) is a <see cref="Diagnostics"/> entry, not
/// a thrown exception — real `ssh` treats these as hard config errors (confirmed:
/// `ssh -G` exits non-zero), but this library's own `known_hosts` parser already established
/// "resilient parse, diagnostics for a human, never throw on garbage input" as this project's pattern
/// for files an external tool also writes, and `ssh_config` is no different.
/// </summary>
public sealed class OpenSshConfig
{
    private const int MaxIncludeDepth = 16;

    private readonly List<Block> _blocks = [];
    private readonly List<OpenSshConfigDiagnostic> _diagnostics = [];

    private OpenSshConfig()
    {
    }

    public IReadOnlyList<OpenSshConfigDiagnostic> Diagnostics => _diagnostics;

    /// <summary><c>%USERPROFILE%\.ssh\config</c> (<c>~/.ssh/config</c> off Windows) — ssh's own default per-user config path.</summary>
    public static string DefaultUserFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");

    /// <summary>ssh's own built-in <c>IdentityFile</c> candidate file NAMES, in the order it tries them,
    /// confirmed against real <c>ssh -G</c>. The <c>_sk</c> entries are FIDO2/security-key resident keys; this
    /// library doesn't otherwise support those, but real ssh lists them too, so this stays a faithful mirror.</summary>
    private static readonly string[] DefaultIdentityFileNames = ["id_rsa", "id_ecdsa", "id_ecdsa_sk", "id_ed25519", "id_ed25519_sk"];

    /// <summary>The default candidates under a given <c>.ssh</c> directory, filtered to ones that actually
    /// exist — separated from <see cref="Resolve"/>'s real <c>%USERPROFILE%\.ssh</c> for testability (a unit
    /// test can't durably assert which keys exist in the real profile directory on every machine this runs on).</summary>
    internal static IEnumerable<string> DefaultIdentityFilesUnder(string sshDirectory) =>
        DefaultIdentityFileNames.Select(name => Path.Combine(sshDirectory, name)).Where(File.Exists);

    /// <summary>Loads <paramref name="path"/> (default <see cref="DefaultUserFilePath"/>), following top-level <c>Include</c> directives. A missing file resolves to an empty config (matching real `ssh`, which treats no config file as normal); an existing-but-unreadable file throws.</summary>
    public static OpenSshConfig Load(string? path = null)
    {
        var result = new OpenSshConfig();
        var resolvedPath = Path.GetFullPath(path ?? DefaultUserFilePath);
        result.LoadFile(resolvedPath, currentlyIncluding: []);
        return result;
    }

    /// <summary>Parses <paramref name="content"/> as a file named <paramref name="sourceName"/> (for tests and in-memory use). <c>Include</c> lines are recorded as unsupported (no filesystem to resolve them against) unless <paramref name="baseDirectory"/> is given, in which case they're followed exactly as <see cref="Load"/> would.</summary>
    public static OpenSshConfig Parse(string content, string sourceName = "<memory>", string? baseDirectory = null)
    {
        var result = new OpenSshConfig();
        result.ParseInto(content, sourceName, baseDirectory, currentlyIncluding: []);
        return result;
    }

    /// <summary>Resolves every setting for <paramref name="host"/>, in file/include order, first-match-wins per field (accumulating for <see cref="OpenSshConfigHost.IdentityFiles"/>) — see the class doc for exactly what's evaluated.</summary>
    public OpenSshConfigHost Resolve(string host)
    {
        string? hostName = null, hostKeyAlias = null, user = null, userKnownHostsFileRaw = null;
        int? port = null, connectTimeout = null;
        bool? identitiesOnly = null, hashKnownHosts = null;
        OpenSshStrictHostKeyChecking? strict = null;
        List<string> identityFiles = [];
        List<string>? globalKnownHostsFiles = null;

        foreach (var block in _blocks)
        {
            if (!block.Applies(host))
            {
                continue;
            }

            foreach (var entry in block.Entries)
            {
                switch (entry.Keyword)
                {
                    case "hostname": hostName ??= ExpandTokens(StripQuotes(entry.Value), host); break;
                    case "hostkeyalias": hostKeyAlias ??= ExpandTokens(StripQuotes(entry.Value), host); break;
                    case "user": user ??= StripQuotes(entry.Value); break;
                    case "port": port ??= ParseInt(entry, 1, 65535); break;
                    case "identitiesonly": identitiesOnly ??= ParseBool(entry); break;
                    case "hashknownhosts": hashKnownHosts ??= ParseBool(entry); break;
                    case "connecttimeout": connectTimeout ??= ParseInt(entry, 0, int.MaxValue); break;
                    case "stricthostkeychecking": strict ??= ParseStrictHostKeyChecking(entry); break;
                    case "identityfile": identityFiles.Add(ExpandPath(StripQuotes(entry.Value))); break;
                    case "userknownhostsfile":
                        userKnownHostsFileRaw ??= entry.Value;
                        break;
                    case "globalknownhostsfile":
                        globalKnownHostsFiles ??= SplitTokens(entry.Value).Select(ExpandPath).ToList();
                        break;
                }
            }
        }

        // No explicit IdentityFile anywhere: real ssh still tries its own built-in candidate list (confirmed:
        // `ssh -G` lists all five even with no IdentityFile directive at all), filtered to ones that actually
        // exist locally (confirmed separately: an EXPLICITLY configured IdentityFile is NOT filtered this way —
        // it's still offered even if missing, so a typo stays visible instead of silently vanishing; only the
        // implicit defaults are filtered, exactly like `ssh` itself only offers default keys it can find).
        if (identityFiles.Count == 0)
        {
            identityFiles.AddRange(DefaultIdentityFilesUnder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh")));
        }

        return new OpenSshConfigHost(
            HostName: hostName ?? host,
            HostKeyAlias: hostKeyAlias,
            User: user,
            Port: port,
            IdentityFiles: identityFiles,
            IdentitiesOnly: identitiesOnly,
            UserKnownHostsFile: userKnownHostsFileRaw is null ? null : SplitTokens(userKnownHostsFileRaw).Select(ExpandPath).ToList(),
            GlobalKnownHostsFiles: globalKnownHostsFiles ?? [],
            StrictHostKeyChecking: strict,
            HashKnownHosts: hashKnownHosts,
            ConnectTimeout: connectTimeout is null ? null : TimeSpan.FromSeconds(connectTimeout.Value));
    }

    // ---- loading / parsing ----

    private void LoadFile(string path, HashSet<string> currentlyIncluding)
    {
        string content;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            content = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        ParseInto(content, path, Path.GetDirectoryName(path), currentlyIncluding);
    }

    private void ParseInto(string content, string source, string? baseDirectory, HashSet<string> currentlyIncluding)
    {
        Block? current = null; // null = the implicit unconditional block before any Host/Match line
        var lineNumber = 0;

        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (!TrySplitKeywordValue(line, out var keyword, out var value))
            {
                _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "Couldn't find a keyword on this line."));
                continue;
            }

            var lower = keyword.ToLowerInvariant();
            switch (lower)
            {
                case "host":
                    if (value.Length == 0)
                    {
                        _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "'Host' with no pattern."));
                        continue;
                    }

                    current = new Block(value);
                    _blocks.Add(current);
                    continue;

                case "match":
                    _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "'Match' is not evaluated — directives after it are ignored until the next 'Host'/'Match' line. See OpenSshConfig's class doc."));
                    current = new Block(patternList: null); // never applies to anything
                    _blocks.Add(current);
                    continue;

                case "include":
                    if (current is not null)
                    {
                        _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "'Include' inside a Host/Match block is not supported (its scoping would need to be ANDed with the enclosing block) — ignored."));
                        continue;
                    }

                    if (value.Length == 0)
                    {
                        _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "'Include' with no path."));
                        continue;
                    }

                    if (baseDirectory is null)
                    {
                        _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, "'Include' can't be resolved without a base directory (this content was parsed directly, not loaded from a file)."));
                        continue;
                    }

                    ResolveIncludes(value, baseDirectory, source, lineNumber, currentlyIncluding);
                    continue;

                default:
                    var entry = new Entry(lower, value, source, lineNumber);
                    if (IsWellFormed(entry)) // a malformed value is diagnosed right here, at parse time — same as OpenSshKnownHosts, so Diagnostics is complete without having to Resolve every possible host first
                    {
                        (current ??= UnconditionalLeadingBlock()).Entries.Add(entry);
                    }

                    continue;
            }
        }
    }

    /// <summary>Directives before any <c>Host</c>/<c>Match</c> line apply unconditionally — confirmed against real <c>ssh -G</c>. Modeled as an always-matching block, added once and reused for every such line in a file.</summary>
    private Block UnconditionalLeadingBlock()
    {
        if (_blocks.Count > 0 && _blocks[0].IsUnconditionalLeading)
        {
            return _blocks[0];
        }

        var block = Block.UnconditionalLeading();
        _blocks.Insert(0, block);
        return block;
    }

    private void ResolveIncludes(string patterns, string baseDirectory, string source, int lineNumber, HashSet<string> currentlyIncluding)
    {
        if (currentlyIncluding.Count >= MaxIncludeDepth)
        {
            _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, $"Include nesting exceeded {MaxIncludeDepth} levels — stopped (possible cycle)."));
            return;
        }

        foreach (var pattern in SplitTokens(patterns))
        {
            var expanded = ExpandPath(pattern);
            // A bare relative path (no ~, no drive/leading separator) resolves against ~/.ssh, matching real ssh — an
            // Include *inside* a config already under ~/.ssh resolving relative to itself would be a plausible-but-wrong
            // guess; ssh's actual, confirmed rule is simpler: always ~/.ssh, never "next to the including file".
            var fullPattern = Path.IsPathFullyQualified(expanded)
                ? expanded
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", expanded);

            var directory = Path.GetDirectoryName(fullPattern);
            var filePattern = Path.GetFileName(fullPattern);
            if (string.IsNullOrEmpty(directory))
            {
                _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, $"Couldn't resolve Include path '{pattern}'."));
                continue;
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, filePattern).OrderBy(f => f, StringComparer.Ordinal)
                    : [];
            }
            catch (IOException)
            {
                matches = [];
            }

            foreach (var file in matches)
            {
                var canonical = Path.GetFullPath(file);
                if (!currentlyIncluding.Add(canonical))
                {
                    _diagnostics.Add(new OpenSshConfigDiagnostic(source, lineNumber, $"Include cycle detected at '{canonical}' — skipped."));
                    continue;
                }

                LoadFile(canonical, currentlyIncluding);
                currentlyIncluding.Remove(canonical);
            }
        }
    }

    /// <summary>Validates (and diagnoses) the handful of directives whose value has a specific
    /// format, at parse time — everything else (an unrecognized keyword, or one whose value is
    /// free-form text/a path) is always well-formed as far as this parser is concerned.</summary>
    private bool IsWellFormed(Entry entry) => entry.Keyword switch
    {
        "port" => ParseInt(entry, 1, 65535) is not null,
        "connecttimeout" => ParseInt(entry, 0, int.MaxValue) is not null,
        "identitiesonly" or "hashknownhosts" => ParseBool(entry) is not null,
        "stricthostkeychecking" => ParseStrictHostKeyChecking(entry) is not null,
        _ => true,
    };

    // ---- line/value parsing ----

    /// <summary>ssh_config's own tokenizer: keyword then value, separated by whitespace OR (whitespace-surrounded) <c>=</c>; case-insensitive keyword. Trailing content on the value side is kept as-is (further split by <see cref="SplitTokens"/> where the directive is list-valued).</summary>
    private static bool TrySplitKeywordValue(string line, out string keyword, out string value)
    {
        var i = 0;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != '=')
        {
            i++;
        }

        if (i == 0)
        {
            keyword = value = "";
            return false;
        }

        keyword = line[..i];
        var rest = line[i..].TrimStart();
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart();
        }

        value = rest.TrimEnd();
        return true;
    }

    /// <summary>Whitespace-separated tokens, honoring double-quoted tokens (so a path with a space survives) — same shape as ssh_config's own list values (<c>IdentityFile</c>, <c>GlobalKnownHostsFile</c>, <c>Include</c>, …).</summary>
    internal static IEnumerable<string> SplitTokens(string value)
    {
        var i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && char.IsWhiteSpace(value[i]))
            {
                i++;
            }

            if (i >= value.Length)
            {
                yield break;
            }

            if (value[i] == '"')
            {
                var end = value.IndexOf('"', i + 1);
                if (end < 0)
                {
                    yield return value[(i + 1)..];
                    yield break;
                }

                yield return value[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var start = i;
                while (i < value.Length && !char.IsWhiteSpace(value[i]))
                {
                    i++;
                }

                yield return value[start..i];
            }
        }
    }

    /// <summary>Strips one matched pair of surrounding double quotes, for a directive whose value is a single item (not a whitespace-separated list, which handles its own per-token quoting via <see cref="SplitTokens"/>).</summary>
    private static string StripQuotes(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string ExpandPath(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        if (path[0] != '~')
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.Length == 1)
        {
            return home;
        }

        if (path[1] is not ('/' or '\\'))
        {
            return path; // "~user/..." (foreign home dir): left alone, not supported
        }

        var tail = path[2..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(home, tail);
    }

    /// <summary>Only the <c>%h</c> (the host being resolved) token, for <c>HostName</c>/<c>HostKeyAlias</c> — real ssh supports more, but this is the one that's actually common and safe to get right without knowing the resolved username yet.</summary>
    private static string ExpandTokens(string value, string host) => value.Replace("%h", host, StringComparison.Ordinal);

    private int? ParseInt(Entry entry, int min, int max)
    {
        if (int.TryParse(entry.Value, out var value) && value >= min && value <= max)
        {
            return value;
        }

        _diagnostics.Add(new OpenSshConfigDiagnostic(entry.File, entry.Line, $"'{entry.Keyword}' value '{entry.Value}' isn't a valid integer in [{min}, {max}]."));
        return null;
    }

    private bool? ParseBool(Entry entry)
    {
        switch (entry.Value.ToLowerInvariant())
        {
            case "yes" or "true": return true;
            case "no" or "false": return false;
            default:
                _diagnostics.Add(new OpenSshConfigDiagnostic(entry.File, entry.Line, $"'{entry.Keyword}' value '{entry.Value}' isn't 'yes' or 'no'."));
                return null;
        }
    }

    private OpenSshStrictHostKeyChecking? ParseStrictHostKeyChecking(Entry entry)
    {
        switch (entry.Value.ToLowerInvariant())
        {
            case "yes" or "true": return OpenSshStrictHostKeyChecking.Yes;
            case "no" or "false" or "off": return OpenSshStrictHostKeyChecking.No;
            case "ask": return OpenSshStrictHostKeyChecking.Ask;
            case "accept-new": return OpenSshStrictHostKeyChecking.AcceptNew;
            default:
                _diagnostics.Add(new OpenSshConfigDiagnostic(entry.File, entry.Line, $"'StrictHostKeyChecking' value '{entry.Value}' isn't recognized (expected yes/no/ask/accept-new)."));
                return null;
        }
    }

    private sealed record Entry(string Keyword, string Value, string File, int Line);

    private sealed class Block
    {
        private readonly string? _patternList; // null = never matches ('Match', unevaluated)
        public bool IsUnconditionalLeading { get; }

        public Block(string? patternList)
        {
            // ssh_config's Host pattern list is WHITESPACE-separated ("Host *.example !bad.example") — unlike
            // known_hosts' comma-separated host field, which is what OpenSshPattern.MatchesList expects (confirmed:
            // a literal comma in a Host line is just part of one pattern, not a separator). Re-joined with commas here.
            _patternList = patternList is null ? null : string.Join(',', SplitTokens(patternList));
        }

        private Block(bool unconditionalLeading)
        {
            IsUnconditionalLeading = unconditionalLeading;
        }

        public static Block UnconditionalLeading() => new(unconditionalLeading: true);

        public List<Entry> Entries { get; } = [];

        // Case-sensitive: confirmed against real `ssh -G` (Host LAB does NOT match a query of "lab") — see OpenSshPattern's class doc.
        public bool Applies(string host) => IsUnconditionalLeading || (_patternList is not null && OpenSshPattern.MatchesList(_patternList, host, ignoreCase: false));
    }
}
