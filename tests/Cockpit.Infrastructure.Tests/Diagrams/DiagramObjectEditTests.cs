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

    [Fact]
    public void AddNode_OnAnEmptySurface_WritesTheHeaderTo()
    {
        var edit = DiagramObjectEdit.AddNode("", "A", "Start");

        Assert.Equal("flowchart TD\n    A[\"Start\"]", edit.Text);
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
    public void RelabelConnection_SetsALabelOnAConnectionThatHadNone()
    {
        var edit = DiagramObjectEdit.RelabelConnection(Source, "A", "B", "go");

        Assert.Equal("""
            flowchart LR
                A["Start"]
                B{Choose}
                A -->|"go"| B
            """.ReplaceLineEndings("\n"), edit.Text);
        Assert.Contains("labeled connection", edit.Summary);
    }

    [Fact]
    public void RelabelConnection_ChangesAnExistingLabel_LeavingTheConnectorAlone()
    {
        var withLabel = DiagramObjectEdit.Connect(Source, "B", "A", "back home").Text!;

        var edit = DiagramObjectEdit.RelabelConnection(withLabel, "B", "A", "return");

        Assert.EndsWith("\n    B -->|\"return\"| A", edit.Text);
    }

    [Fact]
    public void RelabelConnection_WithNoLabel_RemovesTheExistingOne()
    {
        var withLabel = DiagramObjectEdit.Connect(Source, "B", "A", "back home").Text!;

        var edit = DiagramObjectEdit.RelabelConnection(withLabel, "B", "A", null);

        Assert.EndsWith("\n    B --> A", edit.Text);
        Assert.Contains("cleared the label", edit.Summary);
    }

    [Fact]
    public void RelabelConnection_OfAConnectionThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.RelabelConnection(Source, "B", "A", "go");

        Assert.Null(edit.Text);
        Assert.Contains("no B -> A connection", edit.Refusal);
    }

    [Fact]
    public void RelabelConnection_OnAChainLine_IsRefused()
    {
        const string chain = "flowchart LR\n    A --> B --> C";

        Assert.Contains("chain", DiagramObjectEdit.RelabelConnection(chain, "A", "C", "skip").Refusal);
    }

    [Fact]
    public void SetNodeShape_ChangesTheShape_KeepingTheLabelAndId()
    {
        var edit = DiagramObjectEdit.SetNodeShape(Source, "A", DiagramNodeShape.Rounded);

        Assert.Contains("A(\"Start\")", edit.Text, StringComparison.Ordinal);
        Assert.Contains("changed the shape of node A to rounded", edit.Summary);
    }

    [Fact]
    public void SetNodeShape_OnAnImplicitNode_MaterializesItWithItsOwnIdAsTheLabel()
    {
        const string implicitSource = "flowchart LR\n    A --> B";

        var edit = DiagramObjectEdit.SetNodeShape(implicitSource, "B", DiagramNodeShape.Diamond);

        Assert.Equal("flowchart LR\n    A --> B{\"B\"}", edit.Text);
    }

    [Fact]
    public void SetNodeShape_OfANodeThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.SetNodeShape(Source, "Z", DiagramNodeShape.Diamond);

        Assert.Null(edit.Text);
        Assert.Contains("no node \"Z\"", edit.Refusal);
    }

    [Fact]
    public void RestoreNodeShape_PutsBackWhateverDelimitersTheOldLineHad_EvenAHandWrittenOneOutsideTheFiveNamedShapes()
    {
        const string hexagon = "flowchart LR\n    A{{\"Odd shape\"}}";
        var reshaped = DiagramObjectEdit.SetNodeShape(hexagon, "A", DiagramNodeShape.Rectangle).Text!;

        var restored = DiagramObjectEdit.RestoreNodeShape(reshaped, "A", "    A{{\"Odd shape\"}}");

        Assert.Equal(hexagon, restored.Text);
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
