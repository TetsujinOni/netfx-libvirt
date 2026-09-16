using Microsoft.DevTunnels.Ssh.Algorithms;

namespace NetfxLibvirt.Transport;

public static class SshHostKeyVerifiers
{
    /// <summary>Accepts any host key with no verification at all. Named
    /// deliberately loudly, same pattern as .NET's own
    /// <c>HttpClientHandler.DangerousAcceptAnyServerCertificateValidator</c>.
    /// Never point this at a host you haven't already trusted some other
    /// way (e.g. a disposable lab environment you provisioned yourself) —
    /// it defeats SSH's protection against a machine-in-the-middle
    /// impersonating the real libvirt host.</summary>
    public static bool DangerousAcceptAny(IKeyPair hostKey) => true;
}
