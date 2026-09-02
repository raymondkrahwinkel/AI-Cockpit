using Cockpit.Core.Mentions;

namespace Cockpit.Core.Tests.Mentions;

/// <summary>
/// Fuzzy ranking for the AC-740 @-mention picker: filename beats directory, prefix beats mid-string, and a
/// tie goes to the shorter path — matching Claude Code's own file picker without pulling in a fuzzy-finder
/// dependency for it.
/// </summary>
public class MentionMatcherTests
{
    [Fact]
    public void Rank_EmptyQuery_ReturnsTheFirstMaxCandidatesUnranked() =>
        Assert.Equal(["a", "b"], MentionMatcher.Rank(["a", "b", "c"], "", 2));

    [Fact]
    public void Rank_MaxIsZero_ReturnsNothing() =>
        Assert.Empty(MentionMatcher.Rank(["a"], "foo", 0));

    // A candidate that does not carry the query as a subsequence is excluded — including when the query is
    // simply longer than it is.
    [Theory]
    [InlineData("src/Foo.cs", "zzz")]
    [InlineData("a", "abcdef")]
    public void Rank_ACandidateThatDoesNotCarryTheQuery_IsExcluded(string candidate, string query) =>
        Assert.Empty(MentionMatcher.Rank([candidate], query, 10));

    // Matching is case-insensitive, does not have to be contiguous, treats a root-level file's whole path as
    // its name, and lets a directory match on its own name.
    [Theory]
    [InlineData("src/SessionView.cs", "SESSIONVIEW")]
    [InlineData("src/SessionView.cs", "sv")]
    [InlineData("Program.cs", "prog")]
    [InlineData("src/", "src")]
    public void Rank_ACandidateThatCarriesTheQuery_IsKept(string candidate, string query) =>
        Assert.Equal([candidate], MentionMatcher.Rank([candidate], query, 10));

    [Fact]
    public void Rank_AFilenameMatch_OutranksAPathOnlyMatch()
    {
        var ranked = MentionMatcher.Rank(["view/x.cs", "src/View.cs"], "view", 10);

        Assert.Equal(["src/View.cs", "view/x.cs"], ranked);
    }

    [Fact]
    public void Rank_APrefixMatch_OutranksAMidSegmentMatch()
    {
        var ranked = MentionMatcher.Rank(["src/aSessionView.cs", "src/SessionView.cs"], "session", 10);

        Assert.Equal(["src/SessionView.cs", "src/aSessionView.cs"], ranked);
    }

    [Fact]
    public void Rank_EqualScore_TheShorterPathWins()
    {
        var ranked = MentionMatcher.Rank(["src/deep/nested/Foo.cs", "src/Foo.cs"], "foo", 10);

        Assert.Equal(["src/Foo.cs", "src/deep/nested/Foo.cs"], ranked);
    }

    [Fact]
    public void Rank_MoreMatchesThanMax_IsCappedAtMax() =>
        Assert.Equal(2, MentionMatcher.Rank(["a1", "a2", "a3"], "a", 2).Count);
}
