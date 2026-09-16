using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// Builds and starts the throwaway libvirtd+sshd container
/// (<c>Integration/docker/Dockerfile</c>) once per test run, shared by
/// every integration test via xUnit's collection fixture mechanism. Needs
/// only a working Docker daemon — no WSL, no host libvirt install, no
/// manual `sshd` setup, and it's the same for every contributor and for CI.
/// See <c>docs/plan.md</c>'s transport section for why this replaced the
/// earlier host-configuration approach.
///
/// If Docker itself isn't available, <see cref="StartupFailure"/> is set
/// instead of throwing out of <see cref="InitializeAsync"/> — tests read
/// it and call <c>Assert.Skip</c>, the same clean-skip UX the
/// env-var-gated design had, just gated on a much lower, more standard
/// bar (Docker, not a hand-configured libvirtd).
/// </summary>
public sealed class LibvirtdContainerFixture : IAsyncLifetime
{
    public const int SshPort = 22;

    private IContainer? _container;

    public string? StartupFailure { get; private set; }

    public string Host => _container!.Hostname;

    public ushort SshHostPort => _container!.GetMappedPublicPort(SshPort);

    // ECDSA, not ed25519: Microsoft.DevTunnels.Ssh.Keys' OpenSSH key
    // importer only supports RSA/ECDSA — confirmed the hard way, an
    // ed25519 test key failed every one of these tests with
    // "No OpenSSH importer available for key algorithm: ssh-ed25519"
    // before this was caught and fixed.
    public string PrivateKeyPath { get; } = Path.Combine(FindDockerDirectory(), "id_ecdsa");

    public async ValueTask InitializeAsync()
    {
        try
        {
            var dockerDirectory = FindDockerDirectory();

            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(dockerDirectory)
                .WithDockerfile("Dockerfile")
                .Build();
            await image.CreateAsync().ConfigureAwait(false);

            _container = new ContainerBuilder(image)
                .WithPortBinding(SshPort, assignRandomHostPort: true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(SshPort))
                .Build();
            await _container.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StartupFailure = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string FindDockerDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "netfx-libvirt.slnx")))
        {
            dir = dir.Parent;
        }

        var repoRoot = dir?.FullName
            ?? throw new InvalidOperationException("could not locate repo root (netfx-libvirt.slnx) above " + AppContext.BaseDirectory);
        return Path.Combine(repoRoot, "tests", "NetfxLibvirt.Tests", "Integration", "docker");
    }
}

[CollectionDefinition(nameof(LibvirtdContainerCollection))]
public sealed class LibvirtdContainerCollection : ICollectionFixture<LibvirtdContainerFixture>;
