namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary>One member of an <c>enum</c> body. <paramref name="Value"/> is
/// null when the member has no explicit <c>= N</c> (auto-numbered from the
/// previous member, XDR-legal but unused by libvirt's own <c>.x</c> files).
/// <paramref name="IsProcedure"/> and <paramref name="MetadataComment"/>
/// capture libvirt's own convention of naming RPC procedure numbers inside
/// an <c>enum</c> (<c>REMOTE_PROC_...</c>), each preceded by a
/// <c>/** @generate: ... */</c> annotation comment.</summary>
public sealed record XdlEnumValue(string Name, string? Value, bool IsProcedure, string? MetadataComment);

/// <summary><c>enum NAME { ... };</c>.</summary>
public sealed record XdlEnumDefinition(string Name, IReadOnlyList<XdlEnumValue> Values) : XdlDefinition;
