using DotNet.Testcontainers.Containers;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// Shares one running libvirtd+sshd fixture container across every
/// integration test via xUnit's collection fixture mechanism. Needs only a
/// working Docker daemon — no WSL, no host libvirt install, no manual
/// `sshd` setup, and it's the same for every contributor and for CI. See
/// <c>docs/plan.md</c>'s transport section for why this replaced the
/// earlier host-configuration approach.
///
/// How the container is actually obtained (registry pull vs. local
/// Dockerfile build) is <see cref="LibvirtdFixtureImageAcquisition"/>'s
/// concern, not this class's — this fixture only knows how to expose
/// connection details to tests and clean up afterward.
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

    // ECDSA: originally forced by Microsoft.DevTunnels.Ssh.Keys' importer
    // only supporting RSA/ECDSA (an ed25519 test key failed every one of
    // these tests with "No OpenSSH importer available for key algorithm:
    // ssh-ed25519"). SSH.NET (docs/plan.md story 11's later library swap)
    // supports ed25519 too, but there's no reason to regenerate a key
    // that already works — ECDSA is not itself the constraint anymore.
    public string PrivateKeyPath { get; } = Path.Combine(DockerDirectory, "id_ecdsa");

    // Copied here at build time (see this project's .csproj: <None Update="Integration\docker\**"
    // CopyToOutputDirectory="PreserveNewest" />), not located by walking up
    // from AppContext.BaseDirectory looking for the repo root — that breaks
    // whenever the source tree isn't checked out alongside the test
    // binaries (a published test payload, sharded CI, etc.). This path is
    // always correct because the build itself put the files here.
    private static string DockerDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Integration", "docker");

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = await LibvirtdFixtureImageAcquisition.StartAsync(DockerDirectory, SshPort).ConfigureAwait(false);
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
}

[CollectionDefinition(nameof(LibvirtdContainerCollection))]
public sealed class LibvirtdContainerCollection : ICollectionFixture<LibvirtdContainerFixture>;
