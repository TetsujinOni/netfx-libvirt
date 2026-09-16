namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>
/// One RPC procedure extracted from a <c>*_PROC_*</c> enum (e.g.
/// <c>remote_procedure</c>). <see cref="ArgsStructName"/>/<see cref="RetStructName"/>
/// are resolved by libvirt's own naming convention — <c>&lt;program&gt;_&lt;rest&gt;_args</c>
/// / <c>_ret</c> — not by anything in the grammar itself: the RPCL
/// <c>program</c>/<c>version</c>/<c>procedure</c> productions exist for
/// grammar completeness (see <c>XdlParser</c>'s class doc) but libvirt's own
/// <c>remote_protocol.x</c> doesn't use them, so there's no structural link
/// between a procedure enum value and its argument/return struct. Either
/// name is null when no such struct exists (a procedure can take no
/// arguments, or reply with nothing beyond the RPC status).
/// </summary>
public sealed record XdlProcedure(string Name, long Number, string? MetadataComment, string? ArgsStructName, string? RetStructName);
