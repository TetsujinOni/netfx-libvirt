namespace NetfxLibvirt.ProtocolGen.Ast;

/// <summary>A typed field declaration, as used inside <c>struct</c>/<c>union</c>
/// bodies, <c>typedef</c>, and procedure argument/return types.</summary>
public abstract record XdlDeclaration(string Name, string TypeName);

/// <summary><c>type name;</c> — a single value of the named type.</summary>
public sealed record XdlSimpleDeclaration(string Name, string TypeName)
    : XdlDeclaration(Name, TypeName);

/// <summary><c>type name[N];</c> — a fixed-length array of exactly
/// <paramref name="Length"/> elements (a constant name or a numeric
/// literal — resolved later, against the enclosing file's <c>const</c>
/// table).</summary>
public sealed record XdlFixedArrayDeclaration(string Name, string TypeName, string Length)
    : XdlDeclaration(Name, TypeName);

/// <summary><c>type name&lt;N&gt;;</c> or <c>type name&lt;&gt;;</c> — a
/// variable-length array, bounded by <paramref name="MaxLength"/> when
/// present, otherwise unbounded (XDR's implicit max, <c>uint.MaxValue</c>).</summary>
public sealed record XdlVariableArrayDeclaration(string Name, string TypeName, string? MaxLength)
    : XdlDeclaration(Name, TypeName);

/// <summary><c>type *name;</c> — XDR's "optional-data" pointer syntax, which
/// RFC 4506 defines as sugar for a variable-length array of at most one
/// element, not an actual pointer on the wire.</summary>
public sealed record XdlOptionalDeclaration(string Name, string TypeName)
    : XdlDeclaration(Name, TypeName);
