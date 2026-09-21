using NetfxLibvirt.Transport.OpenSsh;

namespace NetfxLibvirt.Tests.OpenSsh;

public class OpenSshPatternTests
{
    [Theory]
    [InlineData("*.example.com", "host.example.com", true)]
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("host?.example", "host1.example", true)]
    [InlineData("host?.example", "host12.example", false)]
    [InlineData("*", "anything", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("LAB.Example", "lab.example", true)] // case-insensitive
    [InlineData("lab.example", "LAB.EXAMPLE", true)]
    [InlineData("[lab.example]:2222", "[lab.example]:2222", true)]
    [InlineData("", "", true)]
    [InlineData("", "x", false)]
    public void Matches_FollowsOpenSshGlobSemantics(string pattern, string text, bool expected) =>
        Assert.Equal(expected, OpenSshPattern.Matches(pattern, text));

    [Theory]
    [InlineData("a.example,b.example", "b.example", true)]
    [InlineData("a.example,b.example", "c.example", false)]
    [InlineData("*.example,!bad.example", "good.example", true)]
    [InlineData("*.example,!bad.example", "bad.example", false)]
    [InlineData("!bad.example,*.example", "bad.example", false)] // a negated match vetoes wherever it appears
    [InlineData("!bad.example", "good.example", false)]          // only negations never match on their own
    [InlineData("a.example,,b.example", "b.example", true)]
    public void MatchesList_AppliesNegationAsAVeto(string list, string text, bool expected) =>
        Assert.Equal(expected, OpenSshPattern.MatchesList(list, text));
}
