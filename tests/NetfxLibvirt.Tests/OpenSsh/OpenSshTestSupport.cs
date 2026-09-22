using System.Diagnostics;
using NetfxLibvirt.Transport;
using NetfxLibvirt.Transport.OpenSsh;
using Renci.SshNet.Security;

namespace NetfxLibvirt.Tests.OpenSsh;

/// <summary>Loads the real <c>ssh-keygen</c>-generated fixtures
/// (<c>Fixtures/OpenSsh/</c>, copied next to the test DLL).</summary>
internal static class OpenSshFixtures
{
    public const string Host = "lab.example";

    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenSsh", name);

    /// <summary>The "type base64" of a <c>.pub</c> file's first line.</summary>
    public static (string Type, byte[] Blob) ReadPublicKey(string name)
    {
        var parts = File.ReadAllText(Path(name)).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], Convert.FromBase64String(parts[1]));
    }

    public static OpenSshPublicKey Key(string name) => OpenSshPublicKey.FromBlob(ReadPublicKey(name).Blob);

    /// <summary>What the transport would hand a verifier for this plain host key.</summary>
    public static SshHostKeyInfo PlainKeyInfo(string pubFile)
    {
        var (type, blob) = ReadPublicKey(pubFile);
        return new SshHostKeyInfo(type, 256, OpenSshPublicKey.FingerprintOf(blob)["SHA256:".Length..], blob);
    }

    /// <summary>SSH.NET's own parse of a fixture certificate — the object the real code path builds policy from.</summary>
    public static Certificate SshNetCertificate(string certFile) => new(ReadPublicKey(certFile).Blob);

    public static SshHostCertificate HostCertificate(string certFile) => SshHostCertificate.FromSshNet(SshNetCertificate(certFile));

    /// <summary>What the transport would hand a verifier for this presented certificate.</summary>
    public static SshHostKeyInfo CertificateInfo(string certFile)
    {
        var (type, blob) = ReadPublicKey(certFile);
        SshCertificateWire.TryGetEmbeddedKeyBlob(blob, out var embedded);
        return new SshHostKeyInfo(type, 256, OpenSshPublicKey.FingerprintOf(embedded)["SHA256:".Length..], embedded)
        {
            Certificate = HostCertificate(certFile),
            CertificateBlob = blob,
        };
    }

    /// <summary>Runs the real OpenSSH binary as an oracle; <see langword="null"/> if it isn't available (callers skip).</summary>
    public static (int ExitCode, string Output)? RunSshKeygen(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Runs real <c>ssh -F &lt;file&gt; -G &lt;host&gt;</c> (print the fully-resolved configuration) as an
    /// oracle for <see cref="OpenSshConfig"/>; <see langword="null"/> if <c>ssh</c> isn't available (callers skip).
    /// Deliberately the plain <c>ssh</c> client, not <c>ssh-keygen</c> — <c>-G</c> is an `ssh`-only flag.</summary>
    public static (int ExitCode, string Output)? RunSshConfigDump(string configFile, string host)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "-F", configFile, "-G", host })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Parses one directive's value out of <c>ssh -G</c>'s dump (one <c>keyword value</c> per line, lowercase keyword).</summary>
    public static string? SshConfigDumpValue(string dump, string keyword) =>
        dump.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .FirstOrDefault(l => l.StartsWith(keyword + " ", StringComparison.Ordinal))
            ?[(keyword.Length + 1)..];
}

/// <summary>A clock frozen at a chosen instant.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netfx-libvirt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Records what it was asked and answers as scripted.</summary>
internal sealed class RecordingPrompt : IOpenSshHostKeyPrompt
{
    public bool Answer { get; set; } = true;

    public TimeSpan Delay { get; set; }

    public List<NewHostKeyContext> HostKeyQuestions { get; } = [];

    public List<NewCertificateAuthorityContext> CaQuestions { get; } = [];

    public async ValueTask<bool> ConfirmNewHostKeyAsync(NewHostKeyContext context, CancellationToken cancellationToken)
    {
        lock (HostKeyQuestions)
        {
            HostKeyQuestions.Add(context);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Answer;
    }

    public async ValueTask<bool> ConfirmNewCertificateAuthorityAsync(NewCertificateAuthorityContext context, CancellationToken cancellationToken)
    {
        lock (CaQuestions)
        {
            CaQuestions.Add(context);
        }

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken);
        }

        return Answer;
    }
}
