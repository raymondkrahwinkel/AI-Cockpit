using Cockpit.Core.Abstractions.Diagrams;

namespace Cockpit.Core.Tests.Diagrams;

/// <summary>
/// The diff-poort's algorithm (AC-825): turning two Mermaid sources into blocks the operator accepts or rejects,
/// and turning a per-block decision back into text. <see cref="DiagramDiff.Apply"/> must round-trip both ways —
/// accept everything reconstructs `after`, accept nothing reconstructs `before` — since those are exactly what the
/// registry falls back to.
/// </summary>
public class DiagramDiffTests
{
    [Fact]
    public void Compute_IdenticalText_IsOneContextBlock_WithNoChanges()
    {
        var blocks = DiagramDiff.Compute("A\nB\nC", "A\nB\nC");

        var block = Assert.Single(blocks);
        Assert.False(block.IsChange);
    }

    [Fact]
    public void Apply_AcceptingOrRejectingEverything_ReconstructsAfterOrBefore()
    {
        const string before = "flowchart LR\nA-->B\nB-->C\nC-->D";
        const string after = "flowchart LR\nA-->B\nB-->E";

        var blocks = DiagramDiff.Compute(before, after);

        Assert.Equal(after, DiagramDiff.Apply(blocks, Enumerable.Range(0, blocks.Count).ToHashSet()));
        Assert.Equal(before, DiagramDiff.Apply(blocks, new HashSet<int>()));
    }

    [Fact]
    public void Apply_AcceptingOnlyOneOfTwoChangeBlocks_KeepsTheOtherAsItWas()
    {
        const string before = "A\nB\nC\nD\nE";
        const string after = "A\nX\nC\nY\nE";

        var blocks = DiagramDiff.Compute(before, after);
        var changeIndexes = blocks.Select((b, i) => (b, i)).Where(x => x.b.IsChange).Select(x => x.i).ToList();
        Assert.Equal(2, changeIndexes.Count);

        var result = DiagramDiff.Apply(blocks, new HashSet<int> { changeIndexes[0] });

        Assert.Equal("A\nX\nC\nD\nE", result);
    }

    [Fact]
    public void Compute_ReorderedLines_AreNotDescribedAsAWholesaleRewrite()
    {
        // Same requirement as SourceChangeSummary: a change must never look bigger than it is.
        var blocks = DiagramDiff.Compute("A\nB\nC", "B\nA\nC");

        Assert.Contains(blocks, b => !b.IsChange && b.ContextLines.Any(l => l.Text == "C"));
    }
}
