using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

// AC-1755 (layer 2, unit): a three-way merge per line keeps their new lines and yours; a block both changed stays twice, yours first.
public class LineMergeTests
{
    [Theory]
    [InlineData("a|b", "a|b|d", "a|B", "a|B|d", false)]
    [InlineData("a|b|c", "a|X|c", "a|Y|c", "a|Y|X|c", true)]
    public void Merge_KeepsBothSidesAndPutsYoursFirstWhereBothChangedTheSameBlock(string baseText, string theirs, string mine, string expected, bool expectedOverlap)
    {
        var merged = LineMerge.Merge(baseText.Split('|'), theirs.Split('|'), mine.Split('|'));

        Assert.Equal(expected, string.Join('|', merged));
        Assert.Equal(merged, LineMerge.Merge(baseText.Split('|'), theirs.Split('|'), mine.Split('|'), out var overlap));
        Assert.Equal(expectedOverlap, overlap);
        Assert.Equal(new[] { "A", "B" }, LineMerge.Merge(["a", "b"], ["a", "B"], ["A", "b"], out var adjacentOverlap));
        Assert.False(adjacentOverlap);
        Assert.Equal(new[] { "B" }, LineMerge.Merge(["a", "b"], ["a", "B"], ["b"], out var deletionOverlap));
        Assert.False(deletionOverlap);
    }
}
