using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NetfxLibvirt.ProtocolGen.Emission;
using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Tests.Emission;

/// <summary>Every emitted file is re-parsed with Roslyn and asserted
/// diagnostic-free — the regression guard for the emitter itself: a future
/// change that produces subtly malformed text (wrong brace nesting, a bad
/// cast, whatever) fails here immediately instead of surfacing later as an
/// unexplained `dotnet build` error against a generated file nobody's
/// looking at. That's the whole reason this emitter is built on Roslyn
/// syntax trees instead of string templates — see CSharpTypeEmitter's class doc.</summary>
public class CSharpTypeEmitterTests
{
    private static XdlModule BuildModule(string source) => XdlModuleBuilder.Build(XdlParser.Parse(source));

    private static void AssertNoDiagnostics(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, $"emitted source has parse errors:\n{string.Join('\n', errors)}\n---\n{source}");
    }

    [Fact]
    public void EmitStruct_SimplePrimitiveFields_ProducesParseableCSharp()
    {
        const string source = "struct s { int a; unsigned int b; hyper c; };";
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitStruct(module.Structs["s"], module);

        AssertNoDiagnostics(emitted);
        Assert.Contains("public sealed class S", emitted);
        Assert.Contains("public required int A { get; init; }", emitted);
    }

    [Fact]
    public void EmitStruct_WithNarrowingPrimitive_ProducesParseableCSharp()
    {
        const string source = "struct s { unsigned char flag; };";
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitStruct(module.Structs["s"], module);

        AssertNoDiagnostics(emitted);
        Assert.Contains("(byte)reader.ReadUInt()", emitted);
    }

    [Fact]
    public void EmitStruct_WithOptionalField_ProducesParseableCSharp()
    {
        const string source = """
            const MAX = 10;
            typedef string bounded<MAX>;
            typedef bounded *optbounded;
            struct s { optbounded name; };
            """;
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitStruct(module.Structs["s"], module);

        AssertNoDiagnostics(emitted);
        Assert.Contains("string? Name", emitted);
    }

    [Fact]
    public void EmitStruct_WithBoundedListOfStruct_ProducesParseableCSharp()
    {
        const string source = """
            const MAX = 10;
            struct inner { int x; };
            struct outer { inner items<MAX>; };
            """;
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitStruct(module.Structs["outer"], module);

        AssertNoDiagnostics(emitted);
        Assert.Contains("List<Inner>", emitted);
    }

    [Fact]
    public void EmitEnum_ExplicitAndAutoValues_ProducesParseableCSharp_WithIntLiterals()
    {
        const string source = "enum kind { A = 0, B, C = 5 };";
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitEnum(module.Enums["kind"], module);

        AssertNoDiagnostics(emitted);
        Assert.Contains("A = 0,", emitted);
        Assert.Contains("B = 1,", emitted);
        Assert.Contains("C = 5", emitted);
        Assert.DoesNotContain("L,", emitted); // guards the long-literal-in-int-enum bug this emitter once had
    }

    [Fact]
    public void EmitStruct_HasNullableEnableDirective_SoOptionalFieldsAreMeaningful()
    {
        const string source = """
            const MAX = 10;
            typedef string bounded<MAX>;
            typedef bounded *optbounded;
            struct s { optbounded name; };
            """;
        var module = BuildModule(source);
        var emitted = CSharpTypeEmitter.EmitStruct(module.Structs["s"], module);
        Assert.Contains("#nullable enable", emitted);
    }
}
