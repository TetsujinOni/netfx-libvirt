using System.Diagnostics;
using System.Text;
using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Transport;

/// <summary><see cref="SshTransport.ShellQuote"/> — the fix for a real command-injection
/// vulnerability: <see cref="SshTransport.ConnectAsync"/> used to interpolate
/// <see cref="SshTransportOptions.RemoteUri"/> straight into the string handed to the
/// remote <c>sh -c</c> (via SSH.NET's <c>CreateCommand</c>/the SSH <c>exec</c> request), so
/// any shell metacharacter in it was remote code execution on the libvirt host under
/// whatever account the SSH session authenticated as — a real exposure once a consuming
/// app lets a user type or store that URI (a saved host profile, not just a developer-edited
/// config file). Verified against a REAL POSIX shell as the oracle, not just "looks right".
///
/// The oracle runs a SCRIPT FILE (never a command STRING passed through .NET's
/// <c>ArgumentList</c>/Windows argv encoding), and compares via a shell variable/<c>[ = ]</c>
/// test, never <c>printf %s</c> or <c>echo</c> — both real pitfalls hit while writing this,
/// unrelated to <see cref="SshTransport.ShellQuote"/> itself: (1) Windows has no real argv;
/// .NET's <c>ProcessStartInfo.ArgumentList</c> re-encodes it into one command-line string
/// using the MSVCRT backslash/quote convention, which can silently alter a string containing
/// both quotes and backslashes before the POSIX shell ever sees it — a script *file*'s bytes
/// go through no such re-encoding. (2) `sh`/`bash`'s own `printf` builtin applies backslash-
/// escape processing to a `%s` argument in practice, beyond what POSIX narrowly specifies —
/// so it would "prove" a correctly-quoted backslash-bearing value was mis-quoted. Neither is a
/// quoting bug; both are quirks of the tools used to *observe* the result.</summary>
public class SshTransportShellQuoteTests
{
    /// <summary>Runs <paramref name="scriptBody"/> as a real POSIX shell script from a file (never a
    /// command-line string) and returns exactly what it printed on stdout — <see langword="null"/> if no
    /// POSIX shell is available (the caller should skip, not fail).</summary>
    private static string? RunScript(string scriptBody)
    {
        var directory = Directory.CreateTempSubdirectory("netfx-libvirt-shellquote-");
        try
        {
            var scriptPath = Path.Combine(directory.FullName, "script.sh");
            File.WriteAllText(scriptPath, scriptBody, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (var shell in new[] { "sh", "bash" })
            {
                try
                {
                    var psi = new ProcessStartInfo(shell) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    psi.ArgumentList.Add(scriptPath); // a plain path: no Windows argv/quote re-encoding to worry about
                    using var process = Process.Start(psi)!;
                    var stdout = process.StandardOutput.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        return stdout;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            return null;
        }
        finally
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("qemu:///system")]
    [InlineData("qemu+ssh://user@host/system")]
    [InlineData("test:///default?no_verify=1&keyfile=/a/b")] // realistic: legitimate query-string punctuation, never rejected
    [InlineData("it's got a quote")]
    [InlineData("'")]
    [InlineData("''")]
    [InlineData("'''")]
    [InlineData("a'b'c'd")]
    [InlineData("; touch /tmp/pwned")]
    [InlineData("$(touch /tmp/pwned)")]
    [InlineData("`touch /tmp/pwned`")]
    [InlineData("a && touch /tmp/pwned")]
    [InlineData("a | rm -rf /")]
    [InlineData("a > /etc/passwd")]
    [InlineData("a < /etc/passwd")]
    [InlineData("$RANDOM$USER${PATH}")]
    [InlineData("a\nb")] // embedded newline: must stay part of ONE argument, not become a second command
    [InlineData("a\tb")]
    [InlineData("a\\b\\\\c")]
    [InlineData("!!")]
    [InlineData("*")]
    [InlineData("")]
    public void ShellQuote_RoundTripsExactlyThroughARealShell_AsOneLiteralWord(string value)
    {
        var quoted = SshTransport.ShellQuote(value);

        // The quoted literal is embedded directly in the script SOURCE (generated once, not re-parsed through
        // any argv layer) as a variable assignment; "expected" comes back through the shell only via `$(cat)`,
        // which — unlike printf/echo — does no escape processing (it only strips trailing newlines, which is
        // why the file is written with none).
        var scriptDirectory = Directory.CreateTempSubdirectory("netfx-libvirt-shellquote-expected-");
        try
        {
            var expectedPath = Path.Combine(scriptDirectory.FullName, "expected.txt");
            File.WriteAllText(expectedPath, value, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            var output = RunScript($"""
                #!/bin/sh
                VALUE={quoted}
                EXPECTED=$(cat {ShToolQuoteForTest(expectedPath)})
                [ "$VALUE" = "$EXPECTED" ] && echo MATCH || echo NOMATCH
                """);
            if (output is null)
            {
                Assert.Skip("No POSIX shell (sh/bash) found on PATH.");
            }

            Assert.Equal("MATCH\n", output);
        }
        finally
        {
            try
            {
                scriptDirectory.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void ShellQuote_InjectionPayload_NeverExecutes_ProvenAgainstARealShell()
    {
        // The actual attack this fix closes: a payload that, unquoted, would run a second command.
        var marker = $"pwned-{Guid.NewGuid():N}";
        var payload = $"innocuous; echo {marker} > /dev/null; touch /tmp/{marker}";
        var quoted = SshTransport.ShellQuote(payload);

        var output = RunScript($"""
            #!/bin/sh
            rm -f /tmp/{marker}
            virt_ssh_helper_arg={quoted}
            [ "$virt_ssh_helper_arg" = {ShToolQuoteForTest(payload)} ] && echo LITERAL || echo NOTLITERAL
            test -e /tmp/{marker} && echo EXPLOITED || echo SAFE
            """);
        if (output is null)
        {
            Assert.Skip("No POSIX shell (sh/bash) found on PATH.");
        }

        Assert.Equal("LITERAL\nSAFE\n", output);
    }

    [Theory]
    [InlineData("a\0b")]
    [InlineData("\0")]
    public void ShellQuote_RejectsEmbeddedNul(string value) =>
        Assert.Throws<ArgumentException>(() => SshTransport.ShellQuote(value));

    [Fact]
    public void ShellQuote_AlwaysWrapsInSingleQuotes() =>
        Assert.Matches(@"^'.*'$", SshTransport.ShellQuote("anything"));

    /// <summary>Quotes a path with no shell-special characters (a temp path never has a single quote) for
    /// embedding directly in a generated script's source — just avoids an unquoted path breaking on a stray
    /// space; not the thing under test.</summary>
    private static string ShToolQuoteForTest(string path) => "'" + path.Replace("'", "'\\''") + "'";
}
