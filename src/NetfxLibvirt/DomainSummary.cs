namespace NetfxLibvirt;

/// <summary>The dashboard-list shape <c>LibvirtConnection.ListDomainsAsync</c>
/// returns — deliberately the same fields <c>virt-desktop</c>'s own
/// <c>VMInfo</c> carries (name, uuid, id, state). <c>HasVNC</c> isn't
/// included: <c>virt-desktop</c> derives that itself by sniffing the
/// domain's XML client-side, not via a separate libvirt call — that's
/// application-level logic, out of scope for this library (see
/// <c>docs/plan.md</c>'s parity-target section).</summary>
public sealed record DomainSummary(string Name, byte[] Uuid, int Id, DomainState State);
