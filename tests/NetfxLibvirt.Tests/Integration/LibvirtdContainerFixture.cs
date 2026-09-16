using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

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
/// Tagged by a hash of the image's own input files and built with
/// <see cref="PullPolicy.Missing"/> rather than
/// <c>ImageFromDockerfileBuilder</c>'s own default (a fresh random name
/// plus <see cref="PullPolicy.Always"/> — confirmed by reading its source:
/// that combination guarantees a full rebuild, from a cold `apt-get
/// install` and all, on <em>every single test run</em>, which measured
/// ~24 seconds here even back-to-back on the same machine with a warm
/// Docker daemon — <c>docker system df</c> showed 0B of build cache in
/// use throughout, i.e. no layer reuse was happening at all). Content
/// hashing keeps the speed of a stable image name (instant reuse across
/// repeated local runs) without its correctness risk (a stale image
/// silently outliving an edited Dockerfile): change any input file and
/// the tag changes with it, so a rebuild happens automatically instead of
/// requiring a contributor to remember `docker image rm`.
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

    /// <summary>Files that actually affect the built image's content —
    /// not <c>README.md</c> (documentation only) or the private half of
    /// the key pair (never copied into the image; see the Dockerfile).</summary>
    private static readonly string[] ImageInputFileNames = ["Dockerfile", "entrypoint.sh", "id_ecdsa.pub"];

    private IContainer? _container;

    public string? StartupFailure { get; private set; }

    public string Host => _container!.Hostname;

    public ushort SshHostPort => _container!.GetMappedPublicPort(SshPort);

    // ECDSA, not ed25519: Microsoft.DevTunnels.Ssh.Keys' OpenSSH key
    // importer only supports RSA/ECDSA — confirmed the hard way, an
    // ed25519 test key failed every one of these tests with
    // "No OpenSSH importer available for key algorithm: ssh-ed25519"
    // before this was caught and fixed.
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
            var image = new ImageFromDockerfileBuilder()
                .WithName($"netfx-libvirt-integration-test-libvirtd:{ComputeImageTagFromInputFiles()}")
                .WithDockerfileDirectory(DockerDirectory)
                .WithDockerfile("Dockerfile")
                .WithImageBuildPolicy(PullPolicy.Missing)
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

    private static string ComputeImageTagFromInputFiles()
    {
        using var sha256 = SHA256.Create();
        foreach (var fileName in ImageInputFileNames)
        {
            var bytes = File.ReadAllBytes(Path.Combine(DockerDirectory, fileName));
            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!)[..12].ToLowerInvariant();
    }
}

[CollectionDefinition(nameof(LibvirtdContainerCollection))]
public sealed class LibvirtdContainerCollection : ICollectionFixture<LibvirtdContainerFixture>;
