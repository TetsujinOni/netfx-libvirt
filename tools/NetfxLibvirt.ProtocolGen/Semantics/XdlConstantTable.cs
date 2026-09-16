using System.Globalization;

namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>
/// Folds <c>const</c> definitions into actual integer values, so array
/// bounds (<c>type name&lt;SOME_CONST&gt;</c>) can be resolved to a number
/// instead of staying a free-text identifier.
/// </summary>
public sealed class XdlConstantTable
{
    /// <summary>
    /// Constants libvirt's <c>.x</c> files reference but never define
    /// themselves — they come from C headers outside what we vendor (see
    /// <c>reference/README.md</c>). <c>VIR_UUID_BUFLEN</c> is part of
    /// libvirt's public, stable ABI (<c>libvirt/libvirt.h</c>): a UUID is
    /// always 16 raw bytes. Anything else hitting this gap is a real parse
    /// failure, not silently guessed — see <see cref="Resolve"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, long> WellKnownExternalConstants = new Dictionary<string, long>
    {
        ["VIR_UUID_BUFLEN"] = 16,
    };

    private readonly Dictionary<string, long> _values = new();

    public IReadOnlyDictionary<string, long> Values => _values;

    public void Add(string name, string literalValue)
    {
        _values[name] = ParseLiteral(literalValue);
    }

    /// <summary>Resolves a <c>value</c> token (an identifier or a numeric
    /// literal) to an integer. Throws if an identifier names neither a
    /// <c>const</c> from this file nor a well-known external constant —
    /// silently returning 0 would corrupt every downstream array bound.</summary>
    public long Resolve(string valueToken)
    {
        if (valueToken.Length > 0 && (char.IsDigit(valueToken[0]) || valueToken[0] == '-'))
        {
            return ParseLiteral(valueToken);
        }

        if (_values.TryGetValue(valueToken, out var fromFile))
        {
            return fromFile;
        }

        if (WellKnownExternalConstants.TryGetValue(valueToken, out var wellKnown))
        {
            return wellKnown;
        }

        throw new InvalidOperationException(
            $"unresolved const '{valueToken}' — not defined in this file and not in the well-known external constant list");
    }

    private static long ParseLiteral(string literal) => literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToInt64(literal, 16)
        : long.Parse(literal, CultureInfo.InvariantCulture);
}
