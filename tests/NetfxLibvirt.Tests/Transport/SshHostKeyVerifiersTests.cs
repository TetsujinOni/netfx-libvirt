using NetfxLibvirt.Transport;

namespace NetfxLibvirt.Tests.Transport;

public class SshHostKeyVerifiersTests
{
    [Fact]
    public void DangerousAcceptAny_AlwaysReturnsTrue()
    {
        Assert.True(SshHostKeyVerifiers.DangerousAcceptAny(null!));
    }
}
