namespace NetfxLibvirt;

/// <summary>
/// Mirrors libvirt's public <c>virDomainState</c> C enum — like
/// <see cref="ConnectListAllDomainsFlags"/>, a libvirt.h API constant with
/// no entry in <c>remote_protocol.x</c>, so not codegen-produced. Values
/// match <c>REMOTE_PROC_DOMAIN_GET_STATE</c>'s <c>state</c> field exactly
/// (confirmed against go-libvirt's <c>const.gen.go</c>) — the same mapping
/// <c>virt-desktop</c>'s own <c>hypervisor.go</c> (<c>MapState</c>) applies,
/// which is why this parity milestone includes it rather than leaving
/// callers to interpret a bare <c>int</c>.
/// </summary>
public enum DomainState
{
    NoState = 0,
    Running = 1,
    Blocked = 2,
    Paused = 3,
    ShuttingDown = 4,
    ShutOff = 5,
    Crashed = 6,
    PmSuspended = 7,

    /// <summary>Not a real wire value — used when a state code isn't one of
    /// the values above, mirroring <c>hypervisor.go</c>'s own
    /// <c>default: StateUnknown</c> fallback rather than throwing.</summary>
    Unknown = -1,
}
