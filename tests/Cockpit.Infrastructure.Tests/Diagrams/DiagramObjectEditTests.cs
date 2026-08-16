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

    [Fact]
    public void AddNode_OnAnEmptySurface_WritesTheHeaderTo()
    {
        var edit = DiagramObjectEdit.AddNode("", "A", "Start");

        Assert.Equal("flowchart TD\n    A[\"Start\"]", edit.Text);
    }

    [Fact]
    public void AddNode_WhenTheIdIsAlreadyThere_IsRefused()
    {
        var edit = DiagramObjectEdit.AddNode(Source, "B", "Other");

        Assert.Null(edit.Text);
        Assert.Contains("already in this diagram", edit.Refusal);
    }

    [Fact]
    public void AddNode_WithAnIdThatIsNotOne_IsRefused_RatherThanWrittenIntoTheSource()
    {
        var edit = DiagramObjectEdit.AddNode(Source, "C --> D", "Sneaky");

        Assert.Null(edit.Text);
        Assert.Contains("one word", edit.Refusal);
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
    public void RenameNode_FindsTheNodeWhereItIsDeclaredInsideAConnectionLine()
    {
        const string inline = "flowchart LR\n    Zip[Plugin zip] --> Host[Host copy]";

        var edit = DiagramObjectEdit.RenameNode(inline, "Zip", "Plugin package");

        Assert.Equal("flowchart LR\n    Zip[\"Plugin package\"] --> Host[Host copy]", edit.Text);
    }

    [Fact]
    public void RenameNode_LeavesAnotherNodesLabelAlone_EvenWhenItSpellsThisNodesId()
    {
        const string source = "flowchart LR\n    A[\"B is next\"]\n    B[\"Stop\"]";

        var edit = DiagramObjectEdit.RenameNode(source, "B", "Halt");

        Assert.Equal("flowchart LR\n    A[\"B is next\"]\n    B[\"Halt\"]", edit.Text);
    }

    [Fact]
    public void RenameNode_OfANodeThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.RenameNode(Source, "Z", "Ghost");

        Assert.Null(edit.Text);
        Assert.Contains("no node", edit.Refusal);
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
    public void RemoveNode_OfALoneDeclaration_ReportsNoConnections()
    {
        var edit = DiagramObjectEdit.RemoveNode("flowchart LR\n    A[\"Start\"]\n    B[\"Stop\"]", "A");

        Assert.Equal("flowchart LR\n    B[\"Stop\"]", edit.Text);
        Assert.Equal("removed node A", edit.Summary);
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
    public void Connect_WithALabel_WritesItQuoted()
    {
        var edit = DiagramObjectEdit.Connect(Source, "B", "A", "back \"home\"");

        Assert.EndsWith("\n    B -->|\"back 'home'\"| A", edit.Text);
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
    public void Disconnect_OfAConnectionThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.Disconnect(Source, "B", "A");

        Assert.Null(edit.Text);
        Assert.Contains("no B -> A connection", edit.Refusal);
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
