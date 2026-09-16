namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary><c>const NAME = VALUE;</c>. <paramref name="Value"/> is the raw
/// numeric literal text (decimal or <c>0x</c> hex) — parsing is deferred to
/// the codegen stage, which needs the original radix for readable output.</summary>
public sealed record XdlConstDefinition(string Name, string Value) : XdlDefinition;
