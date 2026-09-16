using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NetfxLibvirt.ProtocolGen.Ast;
using NetfxLibvirt.ProtocolGen.Semantics;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace NetfxLibvirt.ProtocolGen.Emission;

/// <summary>
/// Emits one complete C# source file per <c>struct</c>/<c>enum</c>
/// definition: a sealed class with required properties plus
/// <c>Encode(XdrWriter)</c>/<c>Decode(XdrReader)</c> for structs, a plain
/// enum with explicit wire values for enums. Built as a real Roslyn syntax
/// tree, not string concatenation: the class/property/method/namespace
/// skeleton is assembled from typed <see cref="SyntaxFactory"/> nodes (so it
/// can't be structurally malformed — no missing brace or mismatched
/// indentation is even expressible), and every leaf fragment
/// <see cref="XdlFieldEmitter"/> produces (an encode statement, a decode
/// expression, a property's type name) is parsed and checked for
/// diagnostics before being spliced in, so a bad fragment fails loudly right
/// here with a Roslyn diagnostic instead of surfacing later as a mystery
/// `dotnet build` error against a generated file nobody's looking at.
///
/// This is a deliberate trade: <c>Microsoft.CodeAnalysis.CSharp</c> is a
/// real dependency (see this tool's csproj), taken on because this
/// generator is expected to grow well past its current MVP-procedure scope
/// toward go-libvirt-level (~200+ procedure) coverage — at that scale,
/// catching malformed generated code at generation time instead of at
/// final-build time is worth the dependency weight.
/// </summary>
public static class CSharpTypeEmitter
{
    public const string Namespace = "NetfxLibvirt.Generated.Remote";

    public static string EmitStruct(XdlStructDefinition definition, XdlModule module)
    {
        var className = CSharpNaming.ToPascalCase(definition.Name);
        var fields = definition.Fields
            .Select(f => (PropertyName: CSharpNaming.ToPascalCase(f.Name), Emitted: XdlFieldEmitter.Describe(XdlTypeResolver.ResolveDeclaration(f, module))))
            .ToList();

        var properties = fields.Select(f => BuildProperty(f.PropertyName, f.Emitted.CSharpType));

        var encodeBody = Block(fields.Select(f => ParseValidatedStatement(f.Emitted.EncodeStatement(f.PropertyName))));
        var encodeMethod = MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Encode")
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddParameterListParameters(Parameter(Identifier("writer")).WithType(IdentifierName("XdrWriter")))
            .WithBody(encodeBody);

        var initializer = InitializerExpression(
            SyntaxKind.ObjectInitializerExpression,
            SeparatedList<ExpressionSyntax>(fields.Select(f => (ExpressionSyntax)AssignmentExpression(
                SyntaxKind.SimpleAssignmentExpression,
                IdentifierName(f.PropertyName),
                ParseValidatedExpression(f.Emitted.DecodeExpression)))));
        var decodeMethod = MethodDeclaration(IdentifierName(className), "Decode")
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
            .AddParameterListParameters(Parameter(Identifier("reader")).WithType(IdentifierName("XdrReader")))
            .WithExpressionBody(ArrowExpressionClause(ImplicitObjectCreationExpression().WithInitializer(initializer)))
            .WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        var classDeclaration = ClassDeclaration(className)
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.SealedKeyword))
            .AddMembers(properties.Cast<MemberDeclarationSyntax>().ToArray())
            .AddMembers(encodeMethod, decodeMethod);

        return RenderFile(classDeclaration, $"libvirt's `struct {definition.Name}` (remote_protocol.x)");
    }

    public static string EmitEnum(XdlEnumDefinition definition, XdlModule module)
    {
        var className = CSharpNaming.ToPascalCase(definition.Name);

        var members = new List<EnumMemberDeclarationSyntax>();
        var next = 0L;
        foreach (var value in definition.Values)
        {
            var resolved = value.Value is null ? next : module.Constants.Resolve(value.Value);
            // Literal(int), not Literal(long): an enum's underlying type
            // defaults to int, and a `long` constant (`0L`) does not
            // implicitly convert to it — only the bare literal `0` gets the
            // zero-to-any-enum special case. Emitting a long literal here
            // compiles for value 0 and fails for every other member; caught
            // by actually building the generated output against the real
            // library (see docs/status.md's validation discipline), not by
            // inspection.
            members.Add(EnumMemberDeclaration(CSharpNaming.ToPascalCase(value.Name))
                .WithEqualsValue(EqualsValueClause(LiteralExpression(SyntaxKind.NumericLiteralExpression, Literal(checked((int)resolved))))));
            next = resolved + 1;
        }

        var enumDeclaration = EnumDeclaration(className)
            .AddModifiers(Token(SyntaxKind.PublicKeyword))
            .AddMembers(members.ToArray());

        return RenderFile(enumDeclaration, $"libvirt's `enum {definition.Name}` (remote_protocol.x)");
    }

    private static PropertyDeclarationSyntax BuildProperty(string propertyName, string typeText)
    {
        var getAccessor = AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));
        var initAccessor = AccessorDeclaration(SyntaxKind.InitAccessorDeclaration).WithSemicolonToken(Token(SyntaxKind.SemicolonToken));

        return PropertyDeclaration(ParseValidatedType(typeText), propertyName)
            .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.RequiredKeyword))
            .AddAccessorListAccessors(getAccessor, initAccessor);
    }

    private static string RenderFile(MemberDeclarationSyntax typeDeclaration, string origin)
    {
        var namespaceDeclaration = FileScopedNamespaceDeclaration(ParseName(Namespace))
            .AddMembers(typeDeclaration);

        var usingXdr = UsingDirective(ParseName("NetfxLibvirt.Xdr"));

        var compilationUnit = CompilationUnit()
            .AddUsings(usingXdr)
            .AddMembers(namespaceDeclaration);

        // The "<auto-generated>" comment makes the compiler treat this file
        // as outside the project's nullable context by default (even with
        // <Nullable>enable</Nullable> csproj-wide) — the #nullable directive
        // is required, not decorative, or "string?" etc. above would warn
        // CS8669 instead of meaning what they say.
        var header = TriviaList(
            Comment("// <auto-generated>"),
            EndOfLine("\n"),
            Comment($"// Generated by NetfxLibvirt.ProtocolGen from {origin}. Do not edit by hand."),
            EndOfLine("\n"),
            Comment("// </auto-generated>"),
            EndOfLine("\n"),
            EndOfLine("\n"),
            Trivia(NullableDirectiveTrivia(Token(SyntaxKind.EnableKeyword), isActive: true)),
            EndOfLine("\n"));

        var withHeader = compilationUnit.WithLeadingTrivia(header);

        return withHeader.NormalizeWhitespace().ToFullString();
    }

    private static StatementSyntax ParseValidatedStatement(string text)
    {
        var statement = ParseStatement(text);
        ThrowIfInvalid(statement.GetDiagnostics(), text, "statement");
        return statement;
    }

    private static ExpressionSyntax ParseValidatedExpression(string text)
    {
        var expression = ParseExpression(text);
        ThrowIfInvalid(expression.GetDiagnostics(), text, "expression");
        return expression;
    }

    private static TypeSyntax ParseValidatedType(string text)
    {
        var type = ParseTypeName(text);
        ThrowIfInvalid(type.GetDiagnostics(), text, "type");
        return type;
    }

    private static void ThrowIfInvalid(IEnumerable<Diagnostic> diagnostics, string sourceText, string kind)
    {
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"generator produced an invalid C# {kind} fragment: {sourceText!}\n{string.Join('\n', errors)}");
        }
    }
}
