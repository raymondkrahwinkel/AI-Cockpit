using Cockpit.Core.Abstractions.Diagrams;
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

    [Fact]
    public void AddNode_AppendsTheNode_AndLeavesEveryOtherLineAsItWas()
    {
        var edit = DiagramObjectEdit.AddNode(Source, "C", "Done");

        Assert.Null(edit.Refusal);
        Assert.Equal(Source.ReplaceLineEndings("\n") + "\n    C[\"Done\"]", edit.Text);
        Assert.Contains("added node C", edit.Summary);
    }

    // One refusal, two reasons an id can be unusable: it is already in the diagram, or it is not one word and would
    // otherwise be written into the source as its own arrow. Neither may produce text.
    [Theory]
    [InlineData("B", "already in this diagram")]
    [InlineData("C --> D", "one word")]
    public void AddNode_WithAnIdThatCannotBeUsed_IsRefused_RatherThanWrittenIntoTheSource(string id, string expected)
    {
        var edit = DiagramObjectEdit.AddNode(Source, id, "Other");

        Assert.Null(edit.Text);
        Assert.Contains(expected, edit.Refusal);
    }

    [Fact]
    public void RenameNode_ChangesOnlyTheLabel_KeepingTheShapeAndTheId()
    {
        var edit = DiagramObjectEdit.RenameNode(Source, "B", "Pick one");

        Assert.Equal("""
            flowchart LR
                A["Start"]
                B{"Pick one"}
                A --> B
            """.ReplaceLineEndings("\n"), edit.Text);
    }

    [Fact]
    public void RenameNode_LeavesAnotherNodesLabelAlone_EvenWhenItSpellsThisNodesId()
    {
        const string source = "flowchart LR\n    A[\"B is next\"]\n    B[\"Stop\"]";

        var edit = DiagramObjectEdit.RenameNode(source, "B", "Halt");

        Assert.Equal("flowchart LR\n    A[\"B is next\"]\n    B[\"Halt\"]", edit.Text);
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
    public void Connect_AppendsTheConnection_AndRefusesTheSameOneTwice()
    {
        var first = DiagramObjectEdit.Connect(Source, "B", "A", label: null);
        Assert.EndsWith("\n    B --> A", first.Text);

        var again = DiagramObjectEdit.Connect(first.Text!, "B", "A", label: null);
        Assert.Null(again.Text);
        Assert.Contains("already connected", again.Refusal);
    }

    [Fact]
    public void Disconnect_RemovesOnlyThatConnection()
    {
        var edit = DiagramObjectEdit.Disconnect(Source, "A", "B");

        Assert.Equal("""
            flowchart LR
                A["Start"]
                B{Choose}
            """.ReplaceLineEndings("\n"), edit.Text);
    }

    [Fact]
    public void RelabelConnection_ChangesAnExistingLabel_LeavingTheConnectorAlone()
    {
        var withLabel = DiagramObjectEdit.Connect(Source, "B", "A", "back home").Text!;

        var edit = DiagramObjectEdit.RelabelConnection(withLabel, "B", "A", "return");

        Assert.EndsWith("\n    B -->|\"return\"| A", edit.Text);
    }

    [Fact]
    public void SetNodeShape_ChangesTheShape_KeepingTheLabelAndId()
    {
        var edit = DiagramObjectEdit.SetNodeShape(Source, "A", DiagramNodeShape.Rounded);

        Assert.Contains("A(\"Start\")", edit.Text, StringComparison.Ordinal);
        Assert.Contains("changed the shape of node A to rounded", edit.Summary);
    }

    [Fact]
    public void AChainLine_IsRefused_RatherThanSplitInHalf()
    {
        const string chain = "flowchart LR\n    A --> B --> C";

        Assert.Contains("chain", DiagramObjectEdit.Disconnect(chain, "A", "C").Refusal);
        Assert.Contains("chain", DiagramObjectEdit.RemoveNode(chain, "B").Refusal);
    }

    [Fact]
    public void ADiagramThatIsNotAFlowchart_IsRefused_SoItsOwnGrammarIsNeverGuessedAt()
    {
        const string sequence = "sequenceDiagram\n    Alice->>Bob: Hello";

        var edit = DiagramObjectEdit.AddNode(sequence, "C", "Carol");

        Assert.Null(edit.Text);
        Assert.Contains("edit_diagram", edit.Refusal);
    }
}
