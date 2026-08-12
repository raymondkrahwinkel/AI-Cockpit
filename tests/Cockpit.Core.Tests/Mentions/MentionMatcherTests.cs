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

    [Fact]
    public void Rank_NoCandidateContainsTheQueryAsASubsequence_ExcludesAll() =>
        Assert.Empty(MentionMatcher.Rank(["src/Foo.cs"], "zzz", 10));

    [Fact]
    public void Rank_QueryLongerThanTheCandidate_IsExcluded() =>
        Assert.Empty(MentionMatcher.Rank(["a"], "abcdef", 10));

    [Fact]
    public void Rank_IsCaseInsensitive() =>
        Assert.Equal(["src/SessionView.cs"], MentionMatcher.Rank(["src/SessionView.cs"], "SESSIONVIEW", 10));

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

    [Fact]
    public void Rank_ARootLevelFile_TreatsTheWholePathAsTheFileName() =>
        Assert.Equal(["Program.cs"], MentionMatcher.Rank(["Program.cs"], "prog", 10));

    [Fact]
    public void Rank_ADirectoryPath_CanStillMatchOnItsOwnName() =>
        Assert.Equal(["src/"], MentionMatcher.Rank(["src/"], "src", 10));

    [Fact]
    public void Rank_NonContiguousSubsequence_StillMatches() =>
        Assert.Equal(["src/SessionView.cs"], MentionMatcher.Rank(["src/SessionView.cs"], "sv", 10));
}
