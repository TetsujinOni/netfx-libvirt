using NetfxLibvirt.ProtocolGen.Lexing;

namespace NetfxLibvirt.ProtocolGen.Tests.Lexing;

public class XdlLexerTests
{
    private static List<XdlToken> Tokenize(string source) => new XdlLexer(source).Tokenize();

    [Fact]
    public void Tokenize_EmptyInput_YieldsOnlyEof()
    {
        var tokens = Tokenize("");
        var token = Assert.Single(tokens);
        Assert.Equal(XdlTokenKind.Eof, token.Kind);
    }

    [Theory]
    [InlineData("hyper", XdlTokenKind.Hyper)]
    [InlineData("int", XdlTokenKind.Int)]
    [InlineData("short", XdlTokenKind.Short)]
    [InlineData("char", XdlTokenKind.Char)]
    [InlineData("bool", XdlTokenKind.Bool)]
    [InlineData("case", XdlTokenKind.Case)]
    [InlineData("const", XdlTokenKind.Const)]
    [InlineData("default", XdlTokenKind.Default)]
    [InlineData("double", XdlTokenKind.Double)]
    [InlineData("enum", XdlTokenKind.Enum)]
    [InlineData("float", XdlTokenKind.Float)]
    [InlineData("opaque", XdlTokenKind.Opaque)]
    [InlineData("string", XdlTokenKind.StringKeyword)]
    [InlineData("struct", XdlTokenKind.Struct)]
    [InlineData("switch", XdlTokenKind.Switch)]
    [InlineData("typedef", XdlTokenKind.Typedef)]
    [InlineData("union", XdlTokenKind.Union)]
    [InlineData("unsigned", XdlTokenKind.Unsigned)]
    [InlineData("void", XdlTokenKind.Void)]
    [InlineData("program", XdlTokenKind.Program)]
    [InlineData("version", XdlTokenKind.Version)]
    public void Tokenize_Keyword_ClassifiesCorrectly(string source, XdlTokenKind expected)
    {
        var tokens = Tokenize(source);
        Assert.Equal(expected, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_PlainIdentifier_IsIdentifier()
    {
        var tokens = Tokenize("remote_nonnull_domain");
        Assert.Equal(XdlTokenKind.Identifier, tokens[0].Kind);
        Assert.Equal("remote_nonnull_domain", tokens[0].Text);
    }

    [Theory]
    [InlineData("REMOTE_PROC_CONNECT_OPEN")]
    [InlineData("REMOTE_PROC_DOMAIN_GET_XML_DESC")]
    [InlineData("QEMU_PROC_MONITOR_COMMAND")]
    public void Tokenize_ProcShapedIdentifier_IsProcIdentifier(string source)
    {
        var tokens = Tokenize(source);
        Assert.Equal(XdlTokenKind.ProcIdentifier, tokens[0].Kind);
    }

    [Theory]
    [InlineData("remote_domain_PROC_thing")] // "_PROC_" not preceded by a single underscore-free word
    [InlineData("REMOTE_DOMAIN_PROC_OPEN")]
    public void Tokenize_UnderscoreBeforeProc_IsPlainIdentifier(string source)
    {
        var tokens = Tokenize(source);
        Assert.Equal(XdlTokenKind.Identifier, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_DecimalConstant_ReadsDigits()
    {
        var tokens = Tokenize("4194304");
        Assert.Equal(XdlTokenKind.Constant, tokens[0].Kind);
        Assert.Equal("4194304", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_NegativeDecimalConstant_IncludesSign()
    {
        var tokens = Tokenize("-1");
        Assert.Equal(XdlTokenKind.Constant, tokens[0].Kind);
        Assert.Equal("-1", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_HexConstant_ReadsFullLiteral()
    {
        var tokens = Tokenize("0x20008086");
        Assert.Equal(XdlTokenKind.Constant, tokens[0].Kind);
        Assert.Equal("0x20008086", tokens[0].Text);
    }

    [Theory]
    [InlineData("{", XdlTokenKind.LBrace)]
    [InlineData("}", XdlTokenKind.RBrace)]
    [InlineData("[", XdlTokenKind.LBracket)]
    [InlineData("]", XdlTokenKind.RBracket)]
    [InlineData("<", XdlTokenKind.LAngle)]
    [InlineData(">", XdlTokenKind.RAngle)]
    [InlineData("(", XdlTokenKind.LParen)]
    [InlineData(")", XdlTokenKind.RParen)]
    [InlineData(",", XdlTokenKind.Comma)]
    [InlineData("=", XdlTokenKind.Equals)]
    [InlineData(";", XdlTokenKind.Semicolon)]
    [InlineData(":", XdlTokenKind.Colon)]
    [InlineData("*", XdlTokenKind.Star)]
    public void Tokenize_Punctuation_ClassifiesCorrectly(string source, XdlTokenKind expected)
    {
        var tokens = Tokenize(source);
        Assert.Equal(expected, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_RegularBlockComment_IsSkipped()
    {
        var tokens = Tokenize("/* not metadata */ int");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal([XdlTokenKind.Int, XdlTokenKind.Eof], kinds);
    }

    [Fact]
    public void Tokenize_MetadataComment_IsEmittedWithTrimmedBody()
    {
        var tokens = Tokenize("/** @generate: both */ REMOTE_PROC_CONNECT_OPEN");
        Assert.Equal(XdlTokenKind.MetadataComment, tokens[0].Kind);
        Assert.Equal("@generate: both", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_PercentDirectiveLine_IsSkipped()
    {
        var tokens = Tokenize("%#include \"internal.h\"\nconst FOO = 1;");
        Assert.Equal(XdlTokenKind.Const, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_UnterminatedBlockComment_Throws()
    {
        Assert.Throws<XdlLexException>(() => Tokenize("/* never closed"));
    }

    [Fact]
    public void Tokenize_TracksLineAndColumn()
    {
        var tokens = Tokenize("const FOO\n  = 1;");
        var eq = tokens.Single(t => t.Kind == XdlTokenKind.Equals);
        Assert.Equal(2, eq.Line);
        Assert.Equal(3, eq.Column);
    }

    [Fact]
    public void Tokenize_TypicalStructDeclaration_ProducesExpectedSequence()
    {
        var tokens = Tokenize("struct remote_nonnull_domain {\n    remote_nonnull_string name;\n    int id;\n};");
        var kinds = tokens.Select(t => t.Kind).ToArray();
        Assert.Equal(
            [
                XdlTokenKind.Struct, XdlTokenKind.Identifier, XdlTokenKind.LBrace,
                XdlTokenKind.Identifier, XdlTokenKind.Identifier, XdlTokenKind.Semicolon,
                XdlTokenKind.Int, XdlTokenKind.Identifier, XdlTokenKind.Semicolon,
                XdlTokenKind.RBrace, XdlTokenKind.Semicolon, XdlTokenKind.Eof,
            ],
            kinds);
    }
}
