namespace NetfxLibvirt.Transport.OpenSsh;

/// <summary>OpenSSH's pattern semantics (<c>match.c</c>): <c>*</c> matches
/// any run of characters, <c>?</c> any single character; a pattern
/// <em>list</em> is comma-separated with a leading <c>!</c> negating an item
/// — a negated item that matches vetoes the whole list, no matter where it
/// appears, and otherwise the list matches when some non-negated item does.
/// Used for <c>known_hosts</c> host fields, certificate principals, and
/// <c>ssh_config</c> <c>Host</c> patterns — **not with the same case
/// sensitivity, though**: <c>known_hosts</c>/principal matching is
/// case-insensitive (confirmed: <c>ssh-keygen -F</c>), but <c>ssh_config</c>
/// <c>Host</c> patterns are case-**sensitive** (confirmed directly: real
/// <c>ssh -F ... -G</c> against <c>Host LAB</c> matches a query of <c>LAB</c>
/// but not <c>lab</c> or <c>Lab</c> — surprising, since it's easy to assume
/// "OpenSSH pattern matching" is one uniform thing; it isn't). Callers pick
/// with <paramref name="ignoreCase"/>; both entry points default to
/// <see langword="true"/> (matching the more common, pre-existing
/// <c>known_hosts</c>/certificate callers).</summary>
public static class OpenSshPattern
{
    public static bool Matches(string pattern, string text, bool ignoreCase = true) =>
        ignoreCase ? Glob(pattern.ToLowerInvariant(), text.ToLowerInvariant()) : Glob(pattern, text);

    public static bool MatchesList(string patternList, string text, bool ignoreCase = true)
    {
        var subject = ignoreCase ? text.ToLowerInvariant() : text;
        var matched = false;

        foreach (var item in patternList.Split(','))
        {
            if (item.Length == 0)
            {
                continue;
            }

            var negated = item[0] == '!';
            var rawPattern = negated ? item[1..] : item;
            var pattern = ignoreCase ? rawPattern.ToLowerInvariant() : rawPattern;

            if (Glob(pattern, subject))
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
