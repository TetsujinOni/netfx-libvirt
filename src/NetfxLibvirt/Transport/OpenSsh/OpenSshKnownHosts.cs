using System.Security.Cryptography;
using System.Text;

namespace NetfxLibvirt.Transport.OpenSsh;

public enum KnownHostsMarker
{
    /// <summary>A plain host key line.</summary>
    None,

    /// <summary><c>@cert-authority</c>: the key is a CA trusted to sign host certificates for the matching hosts.</summary>
    CertificateAuthority,

    /// <summary><c>@revoked</c>: the key must never be accepted.</summary>
    Revoked,
}

/// <summary>A line that couldn't be interpreted and was skipped (never used for trust).</summary>
public sealed record KnownHostsDiagnostic(string File, int Line, string Message);

/// <summary>One parsed <c>known_hosts</c> line, with where it came from.</summary>
public sealed class OpenSshKnownHostsEntry
{
    private readonly byte[]? _salt;
    private readonly byte[]? _hash;

    internal OpenSshKnownHostsEntry(
        KnownHostsMarker marker, string hostField, OpenSshPublicKey key, string? comment, string sourceFile, int lineNumber, byte[]? salt, byte[]? hash)
    {
        Marker = marker;
        HostField = hostField;
        Key = key;
        Comment = comment;
        SourceFile = sourceFile;
        LineNumber = lineNumber;
        _salt = salt;
        _hash = hash;
    }

    public KnownHostsMarker Marker { get; }

    /// <summary>The raw host field: a comma-separated pattern list, or a single <c>|1|salt|hash</c> hashed name.</summary>
    public string HostField { get; }

    public bool IsHashed => _hash is not null;

    public OpenSshPublicKey Key { get; }

    public string? Comment { get; }

    public string SourceFile { get; }

    /// <summary>1-based.</summary>
    public int LineNumber { get; }

    /// <summary><c>file:line</c>, the way <c>ssh</c> reports an offending entry.</summary>
    public string Location => $"{SourceFile}:{LineNumber}";

    /// <summary>Whether this entry applies to <paramref name="hostForm"/> —
    /// <c>host</c> for port 22, <c>[host]:port</c> otherwise (see
    /// <see cref="OpenSshKnownHosts.HostForm"/>).</summary>
    public bool MatchesHost(string hostForm)
    {
        var lowered = hostForm.ToLowerInvariant();
        if (_hash is not null)
        {
            return CryptographicOperations.FixedTimeEquals(HMACSHA1.HashData(_salt!, Encoding.UTF8.GetBytes(lowered)), _hash);
        }

        return OpenSshPattern.MatchesList(HostField, lowered);
    }

    public override string ToString() => $"{Location}: {Marker} {HostField} {Key}";
}

public enum KnownHostKeyOutcome
{
    /// <summary>An entry for this host has exactly this key.</summary>
    Known,

    /// <summary>The host has an entry of this key's type with a DIFFERENT key (and none matching): the key changed.</summary>
    Changed,

    /// <summary>No entry for this host and key type — new to us (entries for other key types don't count).</summary>
    Unknown,

    /// <summary>A matching <c>@revoked</c> entry lists this key.</summary>
    Revoked,
}

public readonly record struct KnownHostKeyCheck(KnownHostKeyOutcome Outcome, OpenSshKnownHostsEntry? Entry);

/// <summary>
/// OpenSSH <c>known_hosts</c> parsing and matching, following <c>sshd(8)</c>
/// "SSH_KNOWN_HOSTS FILE FORMAT": <c>host,host2 keytype base64 [comment]</c>,
/// <c>[host]:port</c> for non-22 ports, <c>*</c>/<c>?</c> wildcards and
/// <c>!</c> negation, hashed names (<c>|1|salt|hash</c>, HMAC-SHA1), the
/// <c>@cert-authority</c> and <c>@revoked</c> markers, comments and blank
/// lines. Every entry keeps its file and line number.
///
/// Not implemented (see <c>docs/plan.md</c> backlog): matching by IP address
/// (<c>CheckHostIP</c>), <c>ssh_config</c> beyond the file path,
/// <c>VerifyHostKeyDNS</c>, <c>KnownHostsCommand</c>. Only the name the caller
/// connects with is matched.
///
/// A file that doesn't exist is treated as empty; one that exists but
/// can't be read throws (never silently "no entries" — that would fail open
/// on a revocation list). Lines that can't be parsed are skipped and
/// reported in <see cref="Diagnostics"/>; a skipped line never grants trust.
/// </summary>
public sealed class OpenSshKnownHosts
{
    private readonly List<OpenSshKnownHostsEntry> _entries = [];
    private readonly List<KnownHostsDiagnostic> _diagnostics = [];

    private OpenSshKnownHosts()
    {
    }

    public IReadOnlyList<OpenSshKnownHostsEntry> Entries => _entries;

    public IReadOnlyList<KnownHostsDiagnostic> Diagnostics => _diagnostics;

    /// <summary><c>%USERPROFILE%\.ssh\known_hosts</c> (<c>~/.ssh/known_hosts</c> off Windows) — ssh's <c>UserKnownHostsFile</c> default.</summary>
    public static string DefaultUserFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    /// <summary><c>%ProgramData%\ssh\ssh_known_hosts</c> on Windows, <c>/etc/ssh/ssh_known_hosts</c> elsewhere — ssh's <c>GlobalKnownHostsFile</c> default.</summary>
    public static IReadOnlyList<string> DefaultGlobalFilePaths { get; } =
        OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ssh", "ssh_known_hosts")]
            : ["/etc/ssh/ssh_known_hosts"];

    /// <summary>The name form ssh matches against: the bare host for port 22, <c>[host]:port</c> otherwise. Lowercased.</summary>
    public static string HostForm(string host, int port)
    {
        var lowered = host.ToLowerInvariant();
        return port == 22 ? lowered : $"[{lowered}]:{port}";
    }

    /// <summary>Loads and concatenates <paramref name="files"/> in order (user file first, then global files).</summary>
    public static OpenSshKnownHosts Load(IEnumerable<string> files)
    {
        var result = new OpenSshKnownHosts();
        foreach (var file in files)
        {
            string content;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                content = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            result.ParseInto(content, file);
        }

        return result;
    }

    /// <summary>Parses <paramref name="content"/> as a file named <paramref name="sourceName"/> (for tests and in-memory use).</summary>
    public static OpenSshKnownHosts Parse(string content, string sourceName = "<memory>")
    {
        var result = new OpenSshKnownHosts();
        result.ParseInto(content, sourceName);
        return result;
    }

    /// <summary>Entries whose host field matches, of any marker, in file order.</summary>
    public IReadOnlyList<OpenSshKnownHostsEntry> FindEntries(string host, int port)
    {
        var form = HostForm(host, port);
        return _entries.Where(e => e.MatchesHost(form)).ToList();
    }

    /// <summary>The <c>@cert-authority</c> entries covering this host.</summary>
    public IReadOnlyList<OpenSshKnownHostsEntry> FindCertificateAuthorities(string host, int port) =>
        FindEntries(host, port).Where(e => e.Marker == KnownHostsMarker.CertificateAuthority).ToList();

    /// <summary>Whether any plain (unmarked) entry exists for this host, of any key type.</summary>
    public bool HasPlainEntries(string host, int port) =>
        FindEntries(host, port).Any(e => e.Marker == KnownHostsMarker.None);

    /// <summary>The first matching <c>@revoked</c> entry that lists exactly <paramref name="keyBlob"/>, if any.</summary>
    public OpenSshKnownHostsEntry? FindRevocation(string host, int port, ReadOnlySpan<byte> keyBlob)
    {
        var form = HostForm(host, port);
        foreach (var entry in _entries)
        {
            if (entry.Marker == KnownHostsMarker.Revoked && entry.Key.Blob.Span.SequenceEqual(keyBlob) && entry.MatchesHost(form))
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// ssh's plain-key semantics. <c>@revoked</c> listing the key → <see cref="KnownHostKeyOutcome.Revoked"/>.
    /// Otherwise, among plain entries for this host: an exact match anywhere →
    /// <see cref="KnownHostKeyOutcome.Known"/> (even if another entry of the
    /// same type differs); none exact but one of the SAME KEY TYPE →
    /// <see cref="KnownHostKeyOutcome.Changed"/> (reporting that entry); only
    /// other key types, or nothing → <see cref="KnownHostKeyOutcome.Unknown"/>.
    /// </summary>
    public KnownHostKeyCheck CheckKey(string host, int port, OpenSshPublicKey key)
    {
        if (FindRevocation(host, port, key.Blob.Span) is { } revoked)
        {
            return new(KnownHostKeyOutcome.Revoked, revoked);
        }

        var form = HostForm(host, port);
        OpenSshKnownHostsEntry? changed = null;

        foreach (var entry in _entries)
        {
            if (entry.Marker != KnownHostsMarker.None || !string.Equals(entry.Key.KeyType, key.KeyType, StringComparison.Ordinal) || !entry.MatchesHost(form))
            {
                continue;
            }

            if (entry.Key.Equals(key))
            {
                return new(KnownHostKeyOutcome.Known, entry);
            }

            changed ??= entry;
        }

        return changed is null ? new(KnownHostKeyOutcome.Unknown, null) : new(KnownHostKeyOutcome.Changed, changed);
    }

    /// <summary>Whether any hashed entry was read from <paramref name="file"/> — used to keep new entries in the file's existing style.</summary>
    public bool FileUsesHashing(string file) =>
        _entries.Any(e => e.IsHashed && string.Equals(e.SourceFile, file, StringComparison.OrdinalIgnoreCase));

    // ---- append ----

    /// <summary>The line <see cref="Append"/> writes (no trailing newline).</summary>
    public static string FormatLine(string host, int port, OpenSshPublicKey key, KnownHostsMarker marker, bool hashHost)
    {
        var form = HostForm(host, port);
        string hostField;
        if (hashHost)
        {
            var salt = RandomNumberGenerator.GetBytes(20);
            var hash = HMACSHA1.HashData(salt, Encoding.UTF8.GetBytes(form));
            hostField = $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
        }
        else
        {
            hostField = form;
        }

        var prefix = marker switch
        {
            KnownHostsMarker.CertificateAuthority => "@cert-authority ",
            KnownHostsMarker.Revoked => "@revoked ",
            _ => string.Empty,
        };

        return $"{prefix}{hostField} {key.KeyType} {key.ToBase64()}";
    }

    /// <summary>
    /// Appends one entry. Never rewrites the file: the existing content is
    /// only read (its last byte, to start on a fresh line if the file lacks a
    /// trailing newline) and the new line goes out as a single append-mode
    /// write with <see cref="FileShare.ReadWrite"/>, so a concurrently
    /// running <c>ssh</c> or another instance neither blocks nor sees a
    /// half-written entry. Creates the directory and file if missing (owner-only
    /// permissions off Windows). Not atomic across processes that append at
    /// the same instant, but each line lands intact.
    /// </summary>
    public static void Append(string file, string host, int port, OpenSshPublicKey key, KnownHostsMarker marker, bool hashHost)
    {
        var fullPath = Path.GetFullPath(file);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var needsLeadingNewline = false;
        if (File.Exists(fullPath))
        {
            using var probe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (probe.Length > 0)
            {
                probe.Seek(-1, SeekOrigin.End);
                needsLeadingNewline = probe.ReadByte() != '\n';
            }
        }

        var text = (needsLeadingNewline ? "\n" : string.Empty) + FormatLine(host, port, key, marker, hashHost) + "\n";
        var bytes = new UTF8Encoding(false).GetBytes(text);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Append,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 0, // unbuffered: one write, one line
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(fullPath, options);
        stream.Write(bytes);
    }

    // ---- parsing ----

    private void ParseInto(string content, string source)
    {
        var lineNumber = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            lineNumber++;
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            try
            {
                _entries.Add(ParseLine(line, source, lineNumber));
            }
            catch (FormatException ex)
            {
                _diagnostics.Add(new KnownHostsDiagnostic(source, lineNumber, ex.Message));
            }
        }
    }

    private static OpenSshKnownHostsEntry ParseLine(string line, string source, int lineNumber)
    {
        var rest = line;

        var marker = KnownHostsMarker.None;
        if (rest[0] == '@')
        {
            var markerToken = NextToken(ref rest);
            marker = markerToken switch
            {
                "@cert-authority" => KnownHostsMarker.CertificateAuthority,
                "@revoked" => KnownHostsMarker.Revoked,
                _ => throw new FormatException($"Unknown marker '{markerToken}'."),
            };
        }

        var hostField = NextToken(ref rest);
        var keyType = NextToken(ref rest);
        var base64 = NextToken(ref rest);
        if (hostField.Length == 0 || keyType.Length == 0 || base64.Length == 0)
        {
            throw new FormatException("Expected '<hosts> <keytype> <base64-key>'.");
        }

        if (!OpenSshPublicKey.TryFromBase64(base64, keyType, out var key))
        {
            throw new FormatException($"The key is not valid base64 of a '{keyType}' key blob.");
        }

        byte[]? salt = null, hash = null;
        if (hostField[0] == '|')
        {
            var parts = hostField.Split('|');
            if (parts.Length != 4 || parts[1] != "1")
            {
                throw new FormatException("Unsupported hashed host field (expected |1|salt|hash).");
            }

            try
            {
                salt = Convert.FromBase64String(parts[2]);
                hash = Convert.FromBase64String(parts[3]);
            }
            catch (FormatException)
            {
                throw new FormatException("Hashed host field isn't valid base64.");
            }

            if (hash.Length != 20 || salt.Length == 0)
            {
                throw new FormatException("Hashed host field has the wrong salt/hash length.");
            }
        }

        var comment = rest.Length == 0 ? null : rest;
        return new OpenSshKnownHostsEntry(marker, hostField, key, comment, source, lineNumber, salt, hash);
    }

    private static string NextToken(ref string rest)
    {
        rest = rest.TrimStart(' ', '\t');
        var end = rest.IndexOfAny([' ', '\t']);
        string token;
        if (end < 0)
        {
            token = rest;
            rest = string.Empty;
        }
        else
        {
            token = rest[..end];
            rest = rest[end..].TrimStart(' ', '\t');
        }

        return token;
    }
}
