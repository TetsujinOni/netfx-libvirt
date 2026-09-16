using Microsoft.DevTunnels.Ssh.Algorithms;

namespace NetfxLibvirt.Transport;

/// <summary>Verifies a remote SSH server's host key before
/// <see cref="SshTransport.ConnectAsync"/> completes the handshake. Return
/// <see langword="true"/> to trust the key, <see langword="false"/> to
/// reject the connection.
///
/// Required, not optional — this library never silently accepts an
/// unverified host key. (Contrast <c>virt-desktop</c>'s own
/// <c>connection.go</c>, which sets <c>HostKeyCallback: ssh.InsecureIgnoreHostKey()</c>
/// with a "configure this properly in production" comment left unresolved —
/// exactly the shortcut this API is designed to make impossible to take by
/// accident.) Check against a known_hosts file, a pinned fingerprint, or a
/// trust-on-first-use store; see <see cref="SshHostKeyVerifiers.DangerousAcceptAny"/>
/// only for throwaway lab use.</summary>
public delegate bool SshHostKeyVerifier(IKeyPair hostKey);
