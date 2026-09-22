using NetfxLibvirt.Tests.OpenSsh;
using NetfxLibvirt.Transport;
using Renci.SshNet;

namespace NetfxLibvirt.Tests.Transport;

/// <summary><see cref="SshTransport.BuildAuthenticationMethod"/> — in particular
/// <see cref="SshTransportOptions.PrivateKeyPaths"/>, which offers several candidate keys within one
/// SSH session (one <c>PrivateKeyAuthenticationMethod</c>) rather than one connection attempt per
/// candidate. Real, checked-in key files (<c>Fixtures/OpenSsh/</c>), not fakes — <c>PrivateKeyFile</c>
/// parses the actual key format.</summary>
public class SshTransportAuthenticationTests
{
    private static SshTransportOptions Options() => new() { Host = "h", Username = "u", RemoteUri = "x" };

    [Fact]
    public void PrivateKeyPath_Single_StillBuildsOneKeyPrivateKeyAuthentication()
    {
        var method = SshTransport.BuildAuthenticationMethod(Options() with { PrivateKeyPath = OpenSshFixtures.Path("host_ed25519") });

        var privateKeyMethod = Assert.IsType<PrivateKeyAuthenticationMethod>(method);
        Assert.Single(privateKeyMethod.KeyFiles);
    }

    [Fact]
    public void PrivateKeyPaths_Multiple_OffersAllOfThemWithinOneAuthenticationMethod()
    {
        var options = Options() with { PrivateKeyPaths = [OpenSshFixtures.Path("host_ed25519"), OpenSshFixtures.Path("attacker_ed25519")] };

        var method = SshTransport.BuildAuthenticationMethod(options);

        var privateKeyMethod = Assert.IsType<PrivateKeyAuthenticationMethod>(method);
        Assert.Equal(2, privateKeyMethod.KeyFiles.Count);
    }

    [Fact]
    public void PrivateKeyPaths_SinglePathInTheList_StillWorks()
    {
        var options = Options() with { PrivateKeyPaths = [OpenSshFixtures.Path("host_ed25519")] };

        var method = SshTransport.BuildAuthenticationMethod(options);

        Assert.Single(Assert.IsType<PrivateKeyAuthenticationMethod>(method).KeyFiles);
    }

    [Fact]
    public void PrivateKeyPaths_Empty_FallsBackToPassword_NotAnEmptyKeyMethod()
    {
        var options = Options() with { PrivateKeyPaths = [], Password = "pw" };

        var method = SshTransport.BuildAuthenticationMethod(options);

        Assert.IsType<PasswordAuthenticationMethod>(method);
    }

    [Fact]
    public void BothPrivateKeyPathAndPrivateKeyPaths_Set_Throws_WhichOneShouldDecide()
    {
        var options = Options() with { PrivateKeyPath = OpenSshFixtures.Path("host_ed25519"), PrivateKeyPaths = [OpenSshFixtures.Path("attacker_ed25519")] };

        Assert.Throws<ArgumentException>(() => SshTransport.BuildAuthenticationMethod(options));
    }

    [Fact]
    public void NeitherPrivateKeyOption_FallsBackToPassword()
    {
        var method = SshTransport.BuildAuthenticationMethod(Options() with { Password = "pw" });

        var passwordMethod = Assert.IsType<PasswordAuthenticationMethod>(method);
    }

    [Fact]
    public void MissingCandidateInPrivateKeyPaths_ThrowsRatherThanSilentlySkipping()
    {
        // Unlike OpenSshConfig's own default-candidate fallback (which filters), PrivateKeyPaths is taken
        // as given — a caller who wants missing candidates dropped must filter before setting it.
        var options = Options() with { PrivateKeyPaths = [OpenSshFixtures.Path("host_ed25519"), "does-not-exist-anywhere.key"] };

        Assert.ThrowsAny<Exception>(() => SshTransport.BuildAuthenticationMethod(options));
    }
}
