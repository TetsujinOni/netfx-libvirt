using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace NetfxLibvirt.Tests.Integration;

/// <summary>
/// Resolves and starts the throwaway libvirtd+sshd fixture container
/// (<c>Integration/docker/Dockerfile</c>), separated from
/// <see cref="LibvirtdContainerFixture"/>'s own job (xUnit lifecycle,
/// exposing connection details to tests) because the two change for
/// different reasons and at different rates: this class's job — deciding
/// *how* a running container is obtained — only changes when the
/// acquisition strategy changes, while the fixture's job changes whenever
/// tests need new connection details exposed.
///
/// Default behavior is to pull the published, content-hash-tagged image
/// from GHCR (see <c>.github/workflows/publish-test-fixture-image.yml</c>)
/// rather than build it locally: the image's content (Ubuntu 22.04 +
/// openssh-server + libvirt-daemon-system) changes only on the order of
/// "a contributor edits the Dockerfile" or "a backported CVE," so building
/// it once centrally and having every contributor/CI run pull a few
/// megabytes is strictly cheaper, in aggregate, than every one of them
/// independently paying a local `apt-get install` — and that gap widens,
/// not narrows, as the number of contributors grows. See
/// [[feedback_cache-when-real-thing-unneeded]] and
/// [[feedback_testcontainers-image-caching]].
///
/// Falls back to a local build (the original approach) when: the registry
/// pull fails for any reason (image not yet published for this exact
/// content hash — e.g. a PR that just edited the Dockerfile before CI
/// published it, no network access, a fork with no GHCR package visibility
/// yet), or when <see cref="ForceLocalBuildEnvVar"/> is set, for a
/// contributor actively iterating on the Dockerfile who wants to skip a
/// doomed pull attempt.
/// </summary>
internal static class LibvirtdFixtureImageAcquisition
{
    /// <summary>Content-hash-tagged package published by
    /// <c>.github/workflows/publish-test-fixture-image.yml</c>. Named to make
    /// clear on sight that it's a test fixture, not a product image.</summary>
    private const string RegistryImageName = "ghcr.io/tetsujinoni/netfx-libvirt/test-fixture-libvirtd-sshd";

    /// <summary>Set to force a local Dockerfile build, skipping the registry
    /// pull attempt entirely — for a contributor actively iterating on the
    /// Dockerfile itself, where a pull can only ever miss.</summary>
    private const string ForceLocalBuildEnvVar = "NETFX_LIBVIRT_TEST_FORCE_LOCAL_BUILD";

    /// <summary>Files that actually affect the built image's content — not
    /// <c>README.md</c> (documentation only) or the private half of the key
    /// pair (never copied into the image; see the Dockerfile).</summary>
    private static readonly string[] ImageInputFileNames = ["Dockerfile", "entrypoint.sh", "id_ecdsa.pub"];

    public static async Task<IContainer> StartAsync(string dockerDirectory, int sshPort, IEnumerable<int>? extraPorts = null)
    {
        var tag = ComputeImageTagFromInputFiles(dockerDirectory);

        if (!IsLocalBuildForced())
        {
            var fromRegistry = await TryStartFromRegistryAsync(tag, sshPort, extraPorts).ConfigureAwait(false);
            if (fromRegistry is not null)
            {
                return fromRegistry;
            }
        }

        return await BuildAndStartLocallyAsync(dockerDirectory, tag, sshPort, extraPorts).ConfigureAwait(false);
    }

    /// <summary>Extra published ports (random host ports) — used by tests that start additional sshd instances inside the container.</summary>
    private static ContainerBuilder WithExtraPorts(ContainerBuilder builder, IEnumerable<int>? extraPorts)
    {
        foreach (var port in extraPorts ?? [])
        {
            builder = builder.WithPortBinding(port, assignRandomHostPort: true);
        }

        return builder;
    }

    private static bool IsLocalBuildForced() =>
        Environment.GetEnvironmentVariable(ForceLocalBuildEnvVar) is "1" or "true";

    private static async Task<IContainer?> TryStartFromRegistryAsync(string tag, int sshPort, IEnumerable<int>? extraPorts)
    {
        try
        {
            var container = WithExtraPorts(new ContainerBuilder($"{RegistryImageName}:{tag}")
                .WithImagePullPolicy(PullPolicy.Missing)
                .WithPortBinding(sshPort, assignRandomHostPort: true), extraPorts)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(sshPort))
                .Build();
            await container.StartAsync().ConfigureAwait(false);
            return container;
        }
        catch
        {
            // Any failure here (image not published yet for this hash, no
            // network, registry auth) just means falling back to a local
            // build — the real failure, if any, will surface from there.
            return null;
        }
    }

    private static async Task<IContainer> BuildAndStartLocallyAsync(string dockerDirectory, string tag, int sshPort, IEnumerable<int>? extraPorts)
    {
        var image = new ImageFromDockerfileBuilder()
            .WithName($"netfx-libvirt-integration-test-libvirtd:{tag}")
            .WithDockerfileDirectory(dockerDirectory)
            .WithDockerfile("Dockerfile")
            .WithImageBuildPolicy(PullPolicy.Missing)
            .Build();
        await image.CreateAsync().ConfigureAwait(false);

        var container = WithExtraPorts(new ContainerBuilder(image)
            .WithPortBinding(sshPort, assignRandomHostPort: true), extraPorts)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(sshPort))
            .Build();
        await container.StartAsync().ConfigureAwait(false);
        return container;
    }

    /// <summary>Must match <c>.github/workflows/publish-test-fixture-image.yml</c>'s
    /// <c>cat Dockerfile entrypoint.sh id_ecdsa.pub | sha256sum</c> exactly — same
    /// files, same order, no separators — so a locally-computed tag always
    /// finds the image CI published for identical content.</summary>
    private static string ComputeImageTagFromInputFiles(string dockerDirectory)
    {
        using var sha256 = SHA256.Create();
        foreach (var fileName in ImageInputFileNames)
        {
            var bytes = File.ReadAllBytes(Path.Combine(dockerDirectory, fileName));
            sha256.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!)[..12].ToLowerInvariant();
    }
}
