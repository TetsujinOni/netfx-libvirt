namespace NetfxLibvirt.ProtocolGen.Emission;

/// <summary>Converts libvirt's <c>.x</c> identifiers (snake_case type/field
/// names, SCREAMING_SNAKE_CASE enum members, and a few already-camelCase
/// field names such as <c>nrVirtCpu</c>) into C# PascalCase.</summary>
public static class CSharpNaming
{
    /// <summary>Splits on <c>_</c>; a segment that's entirely uppercase is
    /// title-cased (<c>NONE</c> -&gt; <c>None</c>), otherwise only its first
    /// character is upper-cased, which leaves an already-camelCase segment
    /// (<c>Mem</c> in <c>maxMem</c>) alone. This one rule handles all three
    /// naming styles the real files mix: <c>remote_nonnull_domain</c>,
    /// <c>REMOTE_AUTH_NONE</c>, and <c>nrVirtCpu</c>.</summary>
    public static string ToPascalCase(string identifier)
    {
        var segments = identifier.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var result = new System.Text.StringBuilder();
        foreach (var segment in segments)
        {
            var isAllUpper = segment.All(c => !char.IsLower(c));
            result.Append(char.ToUpperInvariant(segment[0]));
            var rest = segment[1..];
            result.Append(isAllUpper ? rest.ToLowerInvariant() : rest);
        }

        return result.ToString();
    }
}
