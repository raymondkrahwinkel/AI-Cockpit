using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// The line surgery behind the per-object diagram tools (AC-852): a call rewrites the lines naming its object and
/// nothing else, refuses what it cannot do safely instead of guessing, and always leaves the source parseable.
/// </summary>
public class DiagramObjectEditTests
{
    private const string Source = """
        flowchart LR
            A["Start"]
            B{Choose}
            A --> B
        """;

    // One refusal, two reasons an id can be unusable: it is already in the diagram, or it is not one word and would
    // otherwise be written into the source as its own arrow. Neither may produce text.
    [Fact]
    public void AddNode_WithAnIdThatCannotBeUsed_IsRefused_RatherThanWrittenIntoTheSource()
    {
        {
            var scenario = new DiagramObjectEditTests();
            scenario._AssertAddNode_WithAnIdThatCannotBeUsed_IsRefused_RatherThanWrittenIntoTheSource("B", "already in this diagram");
        }
        {
            var scenario = new DiagramObjectEditTests();
            scenario._AssertAddNode_WithAnIdThatCannotBeUsed_IsRefused_RatherThanWrittenIntoTheSource("C --> D", "one word");
        }
    }

    private void _AssertAddNode_WithAnIdThatCannotBeUsed_IsRefused_RatherThanWrittenIntoTheSource(string id, string expected)
    {
        var edit = DiagramObjectEdit.AddNode(Source, id, "Other");

        Assert.Null(edit.Text);
        Assert.Contains(expected, edit.Refusal);
    }

    [Fact]
    public void RemoveNode_TakesItsOwnConnectionsWithIt_AndNothingElse()
    {
        const string source = "flowchart LR\n    A --> B\n    B --> C\n    A --> C";

        var edit = DiagramObjectEdit.RemoveNode(source, "B");

        Assert.Equal("flowchart LR\n    A --> C", edit.Text);
        Assert.Contains("2 connections", edit.Summary);
    }

    [Fact]
    public void AChainLine_IsRefused_RatherThanSplitInHalf()
    {
        const string chain = "flowchart LR\n    A --> B --> C";

        Assert.Contains("chain", DiagramObjectEdit.Disconnect(chain, "A", "C").Refusal);
        Assert.Contains("chain", DiagramObjectEdit.RemoveNode(chain, "B").Refusal);
    }
}
