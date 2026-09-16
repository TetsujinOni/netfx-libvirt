namespace NetfxLibvirt;

/// <summary>
/// Mirrors libvirt's public <c>virConnectListAllDomainsFlags</c> C enum —
/// not defined anywhere in <c>remote_protocol.x</c> (it's a public libvirt.h
/// API constant, not a wire-protocol const, so the codegen pipeline can't
/// produce it; see <c>XdlConstantTable</c>'s doc for the same situation with
/// <c>VIR_UUID_BUFLEN</c>). Values confirmed against go-libvirt's own
/// generated <c>const.gen.go</c> as a second independent reference. Only
/// the two flags this project actually uses are declared — extend as
/// needed, not preemptively.
/// </summary>
[Flags]
public enum ConnectListAllDomainsFlags : uint
{
    Active = 1,
    Inactive = 2,
}
