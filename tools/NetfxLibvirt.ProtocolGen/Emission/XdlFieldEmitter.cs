using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Emission;

/// <summary>One field's C# projection: the property type, how to encode a
/// value expression, and the expression that decodes one.</summary>
public sealed record XdlEmittedField(string CSharpType, Func<string, string> EncodeStatement, string DecodeExpression);

/// <summary>
/// Turns a resolved <see cref="XdlTypeShape"/> into C# — the property type
/// plus the exact <see cref="Xdr.XdrWriter"/>/<see cref="Xdr.XdrReader"/>
/// calls to (de)serialize it. Recursive for array/optional shapes: each
/// nesting level gets its own <c>writer</c>/<c>reader</c> lambda-parameter
/// name (<c>w1</c>/<c>r1</c>, <c>w2</c>/<c>r2</c>, ...) so a shape like
/// "bounded list of optional of struct" doesn't shadow an outer lambda's
/// parameter — none of the MVP procedure set actually nests that deep, but
/// the generator doesn't assume it won't.
/// </summary>
public static class XdlFieldEmitter
{
    public static XdlEmittedField Describe(XdlTypeShape shape) => Describe(shape, depth: 0);

    private static string WriterVar(int depth) => depth == 0 ? "writer" : $"w{depth}";

    private static string ReaderVar(int depth) => depth == 0 ? "reader" : $"r{depth}";

    private static string WireCSharpType(string wireMethodSuffix) => wireMethodSuffix switch
    {
        "Int" => "int",
        "UInt" => "uint",
        "Hyper" => "long",
        "UHyper" => "ulong",
        "Bool" => "bool",
        "Float" => "float",
        "Double" => "double",
        _ => throw new NotSupportedException($"unknown wire method suffix '{wireMethodSuffix}'"),
    };

    private static XdlEmittedField Describe(XdlTypeShape shape, int depth)
    {
        var writerVar = WriterVar(depth);
        var readerVar = ReaderVar(depth);

        switch (shape)
        {
            case XdlPrimitiveShape p:
            {
                var wireType = WireCSharpType(p.WireMethodSuffix);
                var needsCast = p.CSharpType != wireType;
                string Encode(string v) => needsCast
                    ? $"{writerVar}.Write{p.WireMethodSuffix}(({wireType}){v});"
                    : $"{writerVar}.Write{p.WireMethodSuffix}({v});";
                var decode = needsCast
                    ? $"({p.CSharpType}){readerVar}.Read{p.WireMethodSuffix}()"
                    : $"{readerVar}.Read{p.WireMethodSuffix}()";
                return new XdlEmittedField(p.CSharpType, Encode, decode);
            }

            case XdlBoundedStringShape s:
                return new XdlEmittedField(
                    "string",
                    v => $"{writerVar}.WriteString({v});",
                    s.MaxLength is { } maxLen ? $"{readerVar}.ReadString({maxLen})" : $"{readerVar}.ReadString()");

            case XdlFixedOpaqueShape f:
                return new XdlEmittedField(
                    "byte[]",
                    v => $"{writerVar}.WriteFixedOpaque({v});",
                    $"{readerVar}.ReadFixedOpaque({f.Length})");

            case XdlBoundedOpaqueShape o:
                return new XdlEmittedField(
                    "byte[]",
                    v => $"{writerVar}.WriteVarOpaque({v});",
                    o.MaxLength is { } maxOpaque ? $"{readerVar}.ReadVarOpaque({maxOpaque})" : $"{readerVar}.ReadVarOpaque()");

            case XdlStructShape st:
            {
                var name = CSharpNaming.ToPascalCase(st.Definition.Name);
                return new XdlEmittedField(name, v => $"{v}.Encode({writerVar});", $"{name}.Decode({readerVar})");
            }

            case XdlEnumShape en:
            {
                var name = CSharpNaming.ToPascalCase(en.Definition.Name);
                return new XdlEmittedField(name, v => $"{writerVar}.WriteEnum({v});", $"{readerVar}.ReadEnum<{name}>()");
            }

            case XdlOptionalShape opt:
            {
                var inner = Describe(opt.Inner, depth + 1);
                var innerWriter = WriterVar(depth + 1);
                var innerReader = ReaderVar(depth + 1);
                return new XdlEmittedField(
                    $"{inner.CSharpType}?",
                    v => $"{writerVar}.WriteOptional({v}, ({innerWriter}, value) => {{ {inner.EncodeStatement("value")} }});",
                    $"{readerVar}.ReadOptional({innerReader} => {inner.DecodeExpression})");
            }

            case XdlFixedListShape fl:
            {
                var inner = Describe(fl.Element, depth + 1);
                var innerWriter = WriterVar(depth + 1);
                var innerReader = ReaderVar(depth + 1);
                return new XdlEmittedField(
                    $"List<{inner.CSharpType}>",
                    v => $"{writerVar}.WriteFixedArray({v}, ({innerWriter}, item) => {{ {inner.EncodeStatement("item")} }});",
                    $"{readerVar}.ReadFixedArray({fl.Length}, {innerReader} => {inner.DecodeExpression})");
            }

            case XdlBoundedListShape bl:
            {
                var inner = Describe(bl.Element, depth + 1);
                var innerWriter = WriterVar(depth + 1);
                var innerReader = ReaderVar(depth + 1);
                var decode = bl.MaxLength is { } maxCount
                    ? $"{readerVar}.ReadArray({innerReader} => {inner.DecodeExpression}, {maxCount})"
                    : $"{readerVar}.ReadArray({innerReader} => {inner.DecodeExpression})";
                return new XdlEmittedField(
                    $"List<{inner.CSharpType}>",
                    v => $"{writerVar}.WriteArray({v}, ({innerWriter}, item) => {{ {inner.EncodeStatement("item")} }});",
                    decode);
            }

            default:
                throw new NotSupportedException($"emission not implemented for shape {shape.GetType().Name}");
        }
    }
}
