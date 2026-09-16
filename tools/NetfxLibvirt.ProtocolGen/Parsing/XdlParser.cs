using NetfxLibvirt.ProtocolGen.Ast;
using NetfxLibvirt.ProtocolGen.Lexing;

namespace NetfxLibvirt.ProtocolGen.Parsing;

/// <summary>
/// Recursive-descent parser for libvirt's XDR protocol definition language,
/// implementing the same grammar as go-libvirt's <c>internal/lvgen/sunrpc.y</c>
/// (a goyacc/LALR grammar) — ported here as hand-written recursive descent
/// rather than a generated table parser, since C# has no goyacc equivalent
/// and the grammar has no ambiguity that needs one.
///
/// Grammar rules not exercised by either <c>virnetprotocol.x</c> or
/// <c>remote_protocol.x</c> as of the vendored commits in
/// <c>reference/upstream-x/README.md</c> — nested enum/struct/union used
/// inline as a <c>type_specifier</c>, and a <c>void</c> union-case body —
/// are deliberately unsupported and throw <see cref="NotSupportedException"/>;
/// see that README before assuming they're needed for a newer upstream file.
/// </summary>
public sealed class XdlParser
{
    private readonly List<XdlToken> _tokens;
    private int _pos;

    private XdlParser(List<XdlToken> tokens)
    {
        _tokens = tokens;
    }

    public static XdlSpecification Parse(string source)
    {
        var tokens = new XdlLexer(source).Tokenize();
        return new XdlParser(tokens).ParseSpecification();
    }

    private XdlSpecification ParseSpecification()
    {
        var definitions = new List<XdlDefinition>();
        while (Current.Kind != XdlTokenKind.Eof)
        {
            var definition = ParseDefinition();
            Expect(XdlTokenKind.Semicolon);
            if (definition is not null)
            {
                definitions.Add(definition);
            }
        }

        return new XdlSpecification(definitions);
    }

    private XdlDefinition? ParseDefinition() => Current.Kind switch
    {
        XdlTokenKind.Enum => ParseEnumDefinition(),
        XdlTokenKind.Const => ParseConstDefinition(),
        XdlTokenKind.Typedef => ParseTypedefDefinition(),
        XdlTokenKind.Struct => ParseStructDefinition(),
        XdlTokenKind.Union => ParseUnionDefinition(),
        XdlTokenKind.Program => ParseProgramDefinition(),
        _ => throw new XdlParseException("expected a definition (const/enum/typedef/struct/union/program)", Current),
    };

    // enum_definition: ENUM enum_ident '{' enum_value_list '}'
    private XdlEnumDefinition ParseEnumDefinition()
    {
        Expect(XdlTokenKind.Enum);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.LBrace);

        var values = new List<XdlEnumValue>();
        values.Add(ParseEnumValue());
        while (TryConsume(XdlTokenKind.Comma))
        {
            values.Add(ParseEnumValue());
        }

        Expect(XdlTokenKind.RBrace);
        return new XdlEnumDefinition(name, values);
    }

    private XdlEnumValue ParseEnumValue()
    {
        string? metadata = null;
        if (Current.Kind == XdlTokenKind.MetadataComment)
        {
            metadata = Current.Text;
            Advance();
        }

        if (Current.Kind == XdlTokenKind.ProcIdentifier)
        {
            var procName = Advance().Text;
            Expect(XdlTokenKind.Equals);
            var procValue = ParseValue();
            return new XdlEnumValue(procName, procValue, IsProcedure: true, metadata);
        }

        if (metadata is not null)
        {
            throw new XdlParseException("metadata comment must precede a *_PROC_* enum value", Current);
        }

        var name = Expect(XdlTokenKind.Identifier).Text;
        if (TryConsume(XdlTokenKind.Equals))
        {
            return new XdlEnumValue(name, ParseValue(), IsProcedure: false, MetadataComment: null);
        }

        return new XdlEnumValue(name, Value: null, IsProcedure: false, MetadataComment: null);
    }

    // const_definition: CONST const_ident '=' (IDENTIFIER | CONSTANT)
    //
    // libvirt runs the C preprocessor over its .x files before feeding them to
    // rpcgen, so a handful of consts are defined in terms of #define'd names
    // from headers we don't have (e.g. VIR_SECURITY_MODEL_BUFLEN) rather than
    // a literal. Parse the form so it doesn't break the file, but drop it —
    // same precedent as go-libvirt's AddConst callsite in sunrpc.y.
    private XdlConstDefinition? ParseConstDefinition()
    {
        Expect(XdlTokenKind.Const);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.Equals);

        if (Current.Kind == XdlTokenKind.Identifier)
        {
            Advance();
            return null;
        }

        var value = Expect(XdlTokenKind.Constant).Text;
        return new XdlConstDefinition(name, value);
    }

    // typedef_definition: TYPEDEF declaration
    private XdlTypedefDefinition ParseTypedefDefinition()
    {
        Expect(XdlTokenKind.Typedef);
        return new XdlTypedefDefinition(ParseDeclaration());
    }

    // struct_definition: STRUCT struct_ident '{' declaration_list '}'
    private XdlStructDefinition ParseStructDefinition()
    {
        Expect(XdlTokenKind.Struct);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.LBrace);

        var fields = new List<XdlDeclaration>();
        do
        {
            fields.Add(ParseDeclaration());
            Expect(XdlTokenKind.Semicolon);
        }
        while (Current.Kind != XdlTokenKind.RBrace);

        Expect(XdlTokenKind.RBrace);
        return new XdlStructDefinition(name, fields);
    }

    // union_definition: UNION union_ident SWITCH '(' simple_declaration ')' '{' case_list '}'
    private XdlUnionDefinition ParseUnionDefinition()
    {
        Expect(XdlTokenKind.Union);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.Switch);
        Expect(XdlTokenKind.LParen);
        var discriminantType = ParseTypeSpecifier();
        var discriminantName = Expect(XdlTokenKind.Identifier).Text;
        var discriminant = new XdlSimpleDeclaration(discriminantName, discriminantType);
        Expect(XdlTokenKind.RParen);
        Expect(XdlTokenKind.LBrace);

        var cases = new List<XdlUnionCase>();
        do
        {
            cases.Add(ParseUnionCase());
            Expect(XdlTokenKind.Semicolon);
        }
        while (Current.Kind != XdlTokenKind.RBrace);

        Expect(XdlTokenKind.RBrace);
        return new XdlUnionDefinition(name, discriminant, cases);
    }

    // case: CASE value ':' declaration | DEFAULT ':' declaration
    private XdlUnionCase ParseUnionCase()
    {
        if (TryConsume(XdlTokenKind.Default))
        {
            Expect(XdlTokenKind.Colon);
            return new XdlUnionCase(Value: null, IsDefault: true, ParseDeclaration());
        }

        Expect(XdlTokenKind.Case);
        var value = ParseValue();
        Expect(XdlTokenKind.Colon);
        if (Current.Kind == XdlTokenKind.Void)
        {
            throw new NotSupportedException(
                "void union-case bodies aren't used by libvirt's .x files and aren't supported here — see XdlParser's class doc.");
        }

        return new XdlUnionCase(value, IsDefault: false, ParseDeclaration());
    }

    // program_definition: PROGRAM program_ident '{' version_list '}' '=' value
    private XdlProgramDefinition ParseProgramDefinition()
    {
        Expect(XdlTokenKind.Program);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.LBrace);

        var versions = new List<XdlVersionDefinition>();
        do
        {
            versions.Add(ParseVersion());
            Expect(XdlTokenKind.Semicolon);
        }
        while (Current.Kind != XdlTokenKind.RBrace);

        Expect(XdlTokenKind.RBrace);
        Expect(XdlTokenKind.Equals);
        var value = ParseValue();
        return new XdlProgramDefinition(name, versions, value);
    }

    // version: VERSION version_ident '{' procedure_list '}' '=' value
    private XdlVersionDefinition ParseVersion()
    {
        Expect(XdlTokenKind.Version);
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.LBrace);

        var procedures = new List<XdlProcedureDefinition>();
        do
        {
            procedures.Add(ParseProcedure());
            Expect(XdlTokenKind.Semicolon);
        }
        while (Current.Kind != XdlTokenKind.RBrace);

        Expect(XdlTokenKind.RBrace);
        Expect(XdlTokenKind.Equals);
        var value = ParseValue();
        return new XdlVersionDefinition(name, procedures, value);
    }

    // procedure: type_specifier procedure_ident '(' type_specifier ')' '=' value
    private XdlProcedureDefinition ParseProcedure()
    {
        var returnType = ParseTypeSpecifier();
        var name = Expect(XdlTokenKind.Identifier).Text;
        Expect(XdlTokenKind.LParen);
        var argType = ParseTypeSpecifier();
        Expect(XdlTokenKind.RParen);
        Expect(XdlTokenKind.Equals);
        var value = ParseValue();
        return new XdlProcedureDefinition(returnType, name, argType, value);
    }

    // declaration: simple_declaration | fixed_array_declaration
    //            | variable_array_declaration | pointer_declaration
    private XdlDeclaration ParseDeclaration()
    {
        if (Current.Kind == XdlTokenKind.Star)
        {
            throw new XdlParseException("expected a type before '*'", Current);
        }

        var typeName = ParseTypeSpecifier();

        if (TryConsume(XdlTokenKind.Star))
        {
            var pointerName = Expect(XdlTokenKind.Identifier).Text;
            return new XdlOptionalDeclaration(pointerName, typeName);
        }

        var name = Expect(XdlTokenKind.Identifier).Text;

        if (TryConsume(XdlTokenKind.LBracket))
        {
            var length = ParseValue();
            Expect(XdlTokenKind.RBracket);
            return new XdlFixedArrayDeclaration(name, typeName, length);
        }

        if (TryConsume(XdlTokenKind.LAngle))
        {
            if (TryConsume(XdlTokenKind.RAngle))
            {
                return new XdlVariableArrayDeclaration(name, typeName, MaxLength: null);
            }

            var maxLength = ParseValue();
            Expect(XdlTokenKind.RAngle);
            return new XdlVariableArrayDeclaration(name, typeName, maxLength);
        }

        return new XdlSimpleDeclaration(name, typeName);
    }

    // type_specifier: int_spec | UNSIGNED int_spec | FLOAT | DOUBLE | BOOL
    //               | STRING | OPAQUE | enum_definition | struct_definition
    //               | union_definition | IDENTIFIER
    private string ParseTypeSpecifier()
    {
        switch (Current.Kind)
        {
            case XdlTokenKind.Unsigned:
                Advance();
                // Bare "unsigned" (no following hyper/int/short/char) is valid XDR
                // shorthand for "unsigned int" — same rule as C. Real usage: the
                // hand-ported virNetMessageHeader in virnetprotocol.x ("unsigned
                // prog;" etc.) relies on exactly this; remote_protocol.x always
                // spells it out. Caught by parsing the real upstream file, not a
                // synthetic fixture — see RealProtocolFileTests.
                return Current.Kind is XdlTokenKind.Hyper or XdlTokenKind.Int or XdlTokenKind.Short or XdlTokenKind.Char
                    ? "unsigned " + ParseIntSpec()
                    : "unsigned int";
            case XdlTokenKind.Hyper:
            case XdlTokenKind.Int:
            case XdlTokenKind.Short:
            case XdlTokenKind.Char:
                return ParseIntSpec();
            case XdlTokenKind.Float:
                Advance();
                return "float";
            case XdlTokenKind.Double:
                Advance();
                return "double";
            case XdlTokenKind.Bool:
                Advance();
                return "bool";
            case XdlTokenKind.StringKeyword:
                Advance();
                return "string";
            case XdlTokenKind.Opaque:
                Advance();
                return "opaque";
            case XdlTokenKind.Identifier:
                return Advance().Text;
            case XdlTokenKind.Enum:
            case XdlTokenKind.Struct:
            case XdlTokenKind.Union:
                throw new NotSupportedException(
                    $"a nested/anonymous {Current.Kind} as a type_specifier isn't used by libvirt's .x files and isn't supported here — see XdlParser's class doc.");
            default:
                throw new XdlParseException("expected a type specifier", Current);
        }
    }

    private string ParseIntSpec() => Current.Kind switch
    {
        XdlTokenKind.Hyper => Advance().Text,
        XdlTokenKind.Int => Advance().Text,
        XdlTokenKind.Short => Advance().Text,
        XdlTokenKind.Char => Advance().Text,
        _ => throw new XdlParseException("expected hyper/int/short/char", Current),
    };

    // value: IDENTIFIER | CONSTANT
    private string ParseValue()
    {
        if (Current.Kind is XdlTokenKind.Identifier or XdlTokenKind.Constant)
        {
            return Advance().Text;
        }

        throw new XdlParseException("expected an identifier or numeric constant", Current);
    }

    private XdlToken Current => _tokens[_pos];

    private XdlToken Advance() => _tokens[_pos++];

    private XdlToken Expect(XdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            throw new XdlParseException($"expected {kind}", Current);
        }

        return Advance();
    }

    private bool TryConsume(XdlTokenKind kind)
    {
        if (Current.Kind != kind)
        {
            return false;
        }

        Advance();
        return true;
    }
}
