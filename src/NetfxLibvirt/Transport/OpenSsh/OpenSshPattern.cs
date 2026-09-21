namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>OpenSSH's pattern semantics (<c>match.c</c>): <c>*</c> matches
/// any run of characters, <c>?</c> any single character, case-insensitive;
/// a pattern <em>list</em> is comma-separated with a leading <c>!</c>
/// negating an item — a negated item that matches vetoes the whole list, no
/// matter where it appears, and otherwise the list matches when some
/// non-negated item does. Used for <c>known_hosts</c> host fields and for
/// certificate principals.</summary>
public static class OpenSshPattern
{
    public static bool Matches(string pattern, string text) =>
        Glob(pattern.ToLowerInvariant(), text.ToLowerInvariant());

    public static bool MatchesList(string patternList, string text)
    {
        var lowered = text.ToLowerInvariant();
        var matched = false;

        foreach (var item in patternList.Split(','))
        {
            if (item.Length == 0)
            {
                continue;
            }

            var negated = item[0] == '!';
            var pattern = (negated ? item[1..] : item).ToLowerInvariant();

            if (Glob(pattern, lowered))
            {
                if (negated)
                {
                    return false;
                }

                matched = true;
            }
        }

        return matched;
    }

    private static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || (pattern[p] != '*' && pattern[p] == text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
