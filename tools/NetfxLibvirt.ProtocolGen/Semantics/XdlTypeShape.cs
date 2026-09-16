using NetfxLibvirt.ProtocolGen.Ast;

namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>
/// The wire shape a declaration's type ultimately resolves to, after
/// following any typedef chain and applying the declaration's own
/// array/optional wrapper — see <see cref="XdlTypeResolver"/>. This is a
/// closed algebra by design: the emitter switches on it exhaustively rather
/// than walking <see cref="XdlDeclaration"/>/<see cref="XdlDefinition"/>
/// AST shapes directly, because a field's own declaration and a typedef it
/// names can each independently be simple/array/optional, and the two need
/// to compose (e.g. a variable array of a typedef that is itself a bounded
/// string).
/// </summary>
public abstract record XdlTypeShape;

/// <summary>One of XDR's built-in scalar types (never <c>string</c> or
/// <c>opaque</c> — those get their own shapes because their C# projection
/// depends on how they're declared, not just their keyword).</summary>
public sealed record XdlPrimitiveShape(string CSharpType, string WireMethodSuffix) : XdlTypeShape;

/// <summary>A field whose declaration resolves to a real <c>struct</c> definition.</summary>
public sealed record XdlStructShape(XdlStructDefinition Definition) : XdlTypeShape;

/// <summary>A field whose declaration resolves to a real <c>enum</c> definition.</summary>
public sealed record XdlEnumShape(XdlEnumDefinition Definition) : XdlTypeShape;

/// <summary>A field whose declaration resolves to a real <c>union</c> definition.</summary>
public sealed record XdlUnionShape(XdlUnionDefinition Definition) : XdlTypeShape;

/// <summary><c>opaque name[N];</c> (or a typedef of that shape) — exactly
/// <paramref name="Length"/> raw bytes, no length prefix on the wire.</summary>
public sealed record XdlFixedOpaqueShape(long Length) : XdlTypeShape;

/// <summary><c>opaque name&lt;N?&gt;;</c> (or a typedef of that shape) — a
/// length-prefixed byte blob, bounded by <paramref name="MaxLength"/> when
/// present.</summary>
public sealed record XdlBoundedOpaqueShape(long? MaxLength) : XdlTypeShape;

/// <summary><c>string name&lt;N?&gt;;</c> (or a typedef of that shape) — XDR
/// has no string type distinct from bounded opaque data; see
/// <see cref="Xdr.XdrWriter.WriteString"/>.</summary>
public sealed record XdlBoundedStringShape(long? MaxLength) : XdlTypeShape;

/// <summary><c>type name[N];</c> where <c>type</c> isn't opaque — exactly
/// <paramref name="Length"/> elements of <paramref name="Element"/>, no
/// count prefix.</summary>
public sealed record XdlFixedListShape(long Length, XdlTypeShape Element) : XdlTypeShape;

/// <summary><c>type name&lt;N?&gt;;</c> where <c>type</c> isn't opaque/string —
/// a count-prefixed list of <paramref name="Element"/>, bounded by
/// <paramref name="MaxLength"/> when present.</summary>
public sealed record XdlBoundedListShape(long? MaxLength, XdlTypeShape Element) : XdlTypeShape;

/// <summary><c>type *name;</c> (or a typedef of that shape) — XDR
/// "optional-data": present/absent, wrapping <paramref name="Inner"/> when present.</summary>
public sealed record XdlOptionalShape(XdlTypeShape Inner) : XdlTypeShape;
