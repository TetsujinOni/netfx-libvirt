namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>ReturnType Name(ArgType) = Value;</c> inside a <c>version</c>
/// block. Not used by libvirt's own <c>.x</c> files today (procedure numbers
/// there are declared via <c>enum remote_procedure</c> instead), but part of
/// the RPCL grammar go-libvirt's parser also supports — kept for parity and
/// in case upstream libvirt ever adopts it.</summary>
public sealed record XdlProcedureDefinition(string ReturnType, string Name, string ArgType, string Value);
