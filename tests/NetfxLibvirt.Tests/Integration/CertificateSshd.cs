using System.Text;
using DotNet.Testcontainers.Containers;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// Starts an extra, throwaway <c>sshd</c> inside the shared fixture container
/// whose host key is certified by a REAL <c>ssh-keygen</c> (OpenSSH 8.9 in the
/// container) — the same kind of server the real lab host is: it presents an
/// OpenSSH host certificate. Each scenario gets its own container-side port
/// (published by the fixture), host key and certificate, so scenarios don't
/// interfere; CAs are generated once and shared.
/// </summary>
internal sealed class CertificateSshd
{
    private static readonly SemaphoreSlim CaGate = new(1, 1);
    private static bool _casReady;

    private CertificateSshd(LibvirtdContainerFixture fixture, int containerPort, string directory)
    {
        Fixture = fixture;
        ContainerPort = containerPort;
        Directory = directory;
    }

    public LibvirtdContainerFixture Fixture { get; }

    public int ContainerPort { get; }

    public string Directory { get; }

    /// <summary>The host-side port to connect to.</summary>
    public int Port => Fixture.MappedPort(ContainerPort);

    /// <summary>The name to connect with — and the principal certificates are issued for.</summary>
    public string Host => Fixture.Host;

    /// <summary>The certified host public key, as "type base64".</summary>
    public async Task<(string Type, byte[] Blob)> ReadHostPublicKeyAsync() => ParsePub(await ReadTextAsync($"{Directory}/hk.pub"));

    public async Task<(string Type, byte[] Blob)> ReadCertificateAsync() => ParsePub(await ReadTextAsync($"{Directory}/hk-cert.pub"));

    public static async Task<(string Type, byte[] Blob)> ReadCaAsync(LibvirtdContainerFixture fixture, string ca)
    {
        await EnsureCasAsync(fixture);
        return ParsePub(Encoding.UTF8.GetString(await fixture.Container.ReadFileAsync($"/tmp/ca/ca_{ca}.pub")));
    }

    /// <param name="hostKeyType">ssh-keygen -t value for the host key: ed25519, rsa, ecdsa.</param>
    /// <param name="ca">Which shared CA signs: ed25519, rsa, ecdsa, other.</param>
    /// <param name="principals">Comma-separated -n value; null = the fixture host name; "" = none.</param>
    /// <param name="signArgs">Extra ssh-keygen -s arguments (e.g. "-V 20200101:20200102", "-t ssh-rsa").</param>
    /// <param name="userCertificate">Issue a USER certificate (no -h) — nothing but a host certificate should ever be accepted for a host.</param>
    public static async Task<CertificateSshd> StartAsync(
        LibvirtdContainerFixture fixture,
        string hostKeyType = "ed25519",
        string ca = "ed25519",
        string? principals = null,
        string signArgs = "",
        bool userCertificate = false)
    {
        await EnsureCasAsync(fixture);

        var port = fixture.AllocateExtraSshdPort();
        var directory = $"/tmp/s{port}";
        var bits = hostKeyType == "rsa" ? "-b 3072" : hostKeyType == "ecdsa" ? "-b 256" : string.Empty;
        var names = principals ?? fixture.Host;
        var nameArgs = names.Length == 0 ? string.Empty : $"-n '{names}'";
        var hostFlag = userCertificate ? string.Empty : "-h";

        await ExecAsync(fixture, $"""
            set -e
            mkdir -p {directory} && cd {directory}
            ssh-keygen -q -t {hostKeyType} {bits} -N '' -f hk
            ssh-keygen -q -s /tmp/ca/ca_{ca} -I it-{port} {hostFlag} {nameArgs} {signArgs} hk.pub
            """);

        var scenario = new CertificateSshd(fixture, port, directory);

        await ExecAsync(fixture, $"""
            set -e
            cd {directory}
            cat > sshd.conf <<'EOF'
            Port {port}
            ListenAddress 0.0.0.0
            HostKey {directory}/hk
            HostCertificate {directory}/hk-cert.pub
            PidFile {directory}/sshd.pid
            PermitRootLogin yes
            PubkeyAuthentication yes
            AuthorizedKeysFile /root/.ssh/authorized_keys
            LogLevel ERROR
            EOF
            /usr/sbin/sshd -f {directory}/sshd.conf
            """);

        await scenario.WaitUntilServingAsync();
        return scenario;
    }

    /// <summary>The host-side published port can lag the daemon by a moment (Docker's proxy), so wait until the
    /// mapped port actually answers with an SSH banner before a test connects.</summary>
    private async Task WaitUntilServingAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await client.ConnectAsync(Host, Port, timeout.Token);
                var buffer = new byte[4];
                var read = await client.GetStream().ReadAsync(buffer, timeout.Token);
                if (read == 4 && Encoding.ASCII.GetString(buffer) == "SSH-")
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or OperationCanceledException)
            {
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"sshd on {Host}:{Port} never produced an SSH banner.");
            }

            await Task.Delay(100);
        }
    }

    private static async Task EnsureCasAsync(LibvirtdContainerFixture fixture)
    {
        if (_casReady)
        {
            return;
        }

        await CaGate.WaitAsync();
        try
        {
            if (_casReady)
            {
                return;
            }

            await ExecAsync(fixture, """
                set -e
                mkdir -p /tmp/ca && cd /tmp/ca
                ssh-keygen -q -t ed25519 -N '' -f ca_ed25519
                ssh-keygen -q -t rsa -b 3072 -N '' -f ca_rsa
                ssh-keygen -q -t ecdsa -b 384 -N '' -f ca_ecdsa
                ssh-keygen -q -t ed25519 -N '' -f ca_other
                """);
            _casReady = true;
        }
        finally
        {
            CaGate.Release();
        }
    }

    private static async Task ExecAsync(LibvirtdContainerFixture fixture, string script)
    {
        // ReplaceLineEndings first: the C# source's own raw-string literals are CRLF whenever this file is
        // checked out with core.autocrlf=true (the common Windows default — .gitattributes only pins .sh/
        // Dockerfile/workflow-yml to LF, not .cs), and a bare Split('\n') would leave a trailing '\r' on every
        // line, which corrupts the container's `sh -c` script (observed: "sh: 1: set: Illegal option -").
        var script2 = string.Join('\n', script.ReplaceLineEndings("\n").Split('\n').Select(l => l.TrimStart()));
        var result = await fixture.Container.ExecAsync(["sh", "-c", script2]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Container command failed ({result.ExitCode}): {result.Stderr}\n{script2}");
        }
    }

    private async Task<string> ReadTextAsync(string path) => Encoding.UTF8.GetString(await Fixture.Container.ReadFileAsync(path));

    private static (string Type, byte[] Blob) ParsePub(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts[0], Convert.FromBase64String(parts[1]));
    }
}
