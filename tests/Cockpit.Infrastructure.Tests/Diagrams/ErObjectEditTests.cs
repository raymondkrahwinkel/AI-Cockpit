using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-899: the erDiagram half of the per-object grammar. An entity is a block over several lines, so these check
/// that a call finds and rewrites its own block and leaves every other line — and every other entity — alone.
/// </summary>
public class ErObjectEditTests
{
    private const string Source = """
        erDiagram
            CUSTOMER ||--o{ ORDER : "places"
            CUSTOMER {
                string name
                int id PK
            }
            ORDER {
                int id PK
            }
        """;

    private static string Rendered(string source) =>
        MermaidRenderPipeline.Render(source, MermaidTheme.Neutral).Svg.Markup;

    [Fact]
    public void AddEntity_WritesAnEmptyBlock_BecauseABareNameDrawsNothing()
    {
        var edit = DiagramObjectEdit.AddEntity(Source, "INVOICE");

        Assert.Null(edit.Refusal);
        Assert.EndsWith("\n    INVOICE {\n    }", edit.Text);
        Assert.Contains("data-id=\"INVOICE\"", Rendered(edit.Text!), StringComparison.Ordinal);
    }

    [Fact]
    public void RenameEntity_RewritesTheBlockAndItsRelationships_AndNothingElse()
    {
        var edit = DiagramObjectEdit.RenameEntity(Source, "CUSTOMER", "CLIENT");

        Assert.Null(edit.Refusal);
        Assert.Equal("""
            erDiagram
                CLIENT ||--o{ ORDER : "places"
                CLIENT {
                    string name
                    int id PK
                }
                ORDER {
                    int id PK
                }
            """.ReplaceLineEndings("\n"), edit.Text);
    }

    [Fact]
    public void RemoveEntity_TakesItsAttributesAndItsRelationshipsWithIt_AndNothingElse()
    {
        var edit = DiagramObjectEdit.RemoveEntity(Source, "CUSTOMER");

        Assert.Null(edit.Refusal);
        Assert.Equal("""
            erDiagram
                ORDER {
                    int id PK
                }
            """.ReplaceLineEndings("\n"), edit.Text);
        Assert.Contains("1 relationship", edit.Summary);
    }

    [Fact]
    public void SetAttribute_AddsItInsideItsOwnBlock_NotAtTheEndOfTheSource()
    {
        var edit = DiagramObjectEdit.SetAttribute(Source, "ORDER", "placedOn", "date", key: null);

        Assert.Null(edit.Refusal);
        Assert.Contains("    ORDER {\n        int id PK\n        date placedOn\n    }", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SetAttribute_WithAKeyThatIsNotOne_IsRefused_RatherThanWrittenIntoTheSource()
    {
        var edit = DiagramObjectEdit.SetAttribute(Source, "ORDER", "total", "int", "PRIMARY");

        Assert.Null(edit.Text);
        Assert.Contains("PK, FK or UK", edit.Refusal);
    }

    [Theory]
    [InlineData(DiagramErCardinality.One, DiagramErCardinality.One, "||--||")]
    [InlineData(DiagramErCardinality.ZeroOrOne, DiagramErCardinality.ZeroOrOne, "|o--o|")]
    [InlineData(DiagramErCardinality.OneOrMore, DiagramErCardinality.OneOrMore, "}|--|{")]
    [InlineData(DiagramErCardinality.ZeroOrMore, DiagramErCardinality.ZeroOrMore, "}o--o{")]
    public void Relate_WritesTheCardinalityPairTheOperatorChose(DiagramErCardinality from, DiagramErCardinality to, string connector)
    {
        var edit = DiagramObjectEdit.Relate(Source, "ORDER", "CUSTOMER", from, to, "belongs to");

        Assert.Null(edit.Refusal);
        Assert.EndsWith($"\n    ORDER {connector} CUSTOMER : \"belongs to\"", edit.Text);
        Assert.Contains("data-entity1=\"ORDER\"", Rendered(edit.Text!), StringComparison.Ordinal);
    }

    [Fact]
    public void Relate_OverAnExistingRelationship_RewritesIt_AndKeepsItsNonIdentifyingLineStyle()
    {
        const string source = "erDiagram\n    CUSTOMER ||..o{ ORDER : \"places\"";

        var edit = DiagramObjectEdit.Relate(source, "CUSTOMER", "ORDER", DiagramErCardinality.One, DiagramErCardinality.OneOrMore, "owns");

        Assert.Equal("erDiagram\n    CUSTOMER ||..|{ ORDER : \"owns\"", edit.Text);
        Assert.Contains("changed relationship", edit.Summary);
    }

    [Fact]
    public void Unrelate_TakesTheLine_AndLeavesBothEntitiesStanding()
    {
        var edit = DiagramObjectEdit.Unrelate(Source, "CUSTOMER", "ORDER");

        Assert.Null(edit.Refusal);
        Assert.DoesNotContain("||--o{", edit.Text, StringComparison.Ordinal);
        Assert.Contains("CUSTOMER {", edit.Text, StringComparison.Ordinal);
        Assert.Contains("ORDER {", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowchartCall_OnAnErDiagram_IsRefused_NamingTheCallsThatDoWork()
    {
        var edit = DiagramObjectEdit.AddNode(Source, "INVOICE", "Invoice");

        Assert.Null(edit.Text);
        Assert.Contains("add_entity", edit.Refusal);
    }

    [Fact]
    public void AnErCall_OnAFlowchart_IsRefused_NamingTheCallsThatDoWork()
    {
        var edit = DiagramObjectEdit.AddEntity("flowchart LR\n    A[\"Start\"]", "CUSTOMER");

        Assert.Null(edit.Text);
        Assert.Contains("add_node", edit.Refusal);
    }

}
