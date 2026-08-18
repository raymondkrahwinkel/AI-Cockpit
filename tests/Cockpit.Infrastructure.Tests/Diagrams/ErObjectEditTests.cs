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
    public void AddEntity_ThatIsAlreadyThere_IsRefused()
    {
        var edit = DiagramObjectEdit.AddEntity(Source, "ORDER");

        Assert.Null(edit.Text);
        Assert.Contains("already in this diagram", edit.Refusal);
    }

    [Fact]
    public void AddEntity_ForANameThatSoFarOnlyHadRelationships_GivesItABlock()
    {
        const string source = "erDiagram\n    CUSTOMER ||--o{ ORDER : \"places\"";

        var edit = DiagramObjectEdit.AddEntity(source, "ORDER");

        Assert.Null(edit.Refusal);
        Assert.Contains("a block of its own", edit.Summary);
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
    public void RenameEntity_OntoANameThatIsTaken_IsRefused_RatherThanMergingTheTwo()
    {
        var edit = DiagramObjectEdit.RenameEntity(Source, "CUSTOMER", "ORDER");

        Assert.Null(edit.Text);
        Assert.Contains("would merge the two", edit.Refusal);
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
    public void RemoveEntity_ThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.RemoveEntity(Source, "INVOICE");

        Assert.Null(edit.Text);
        Assert.Contains("no entity", edit.Refusal);
    }

    [Fact]
    public void SetAttribute_AddsItInsideItsOwnBlock_NotAtTheEndOfTheSource()
    {
        var edit = DiagramObjectEdit.SetAttribute(Source, "ORDER", "placedOn", "date", key: null);

        Assert.Null(edit.Refusal);
        Assert.Contains("    ORDER {\n        int id PK\n        date placedOn\n    }", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void SetAttribute_ForANameThatIsAlreadyThere_RewritesThatOneLine_AndKeepsItsComment()
    {
        const string source = "erDiagram\n    ORDER {\n        int id PK \"the key\"\n    }";

        var edit = DiagramObjectEdit.SetAttribute(source, "ORDER", "id", "bigint", "PK");

        Assert.Equal("erDiagram\n    ORDER {\n        bigint id PK \"the key\"\n    }", edit.Text);
        Assert.Contains("changed attribute", edit.Summary);
    }

    [Fact]
    public void SetAttribute_OnAnEntityThatHasNoBlockYet_GivesItOne()
    {
        const string source = "erDiagram\n    CUSTOMER ||--o{ ORDER : \"places\"";

        var edit = DiagramObjectEdit.SetAttribute(source, "ORDER", "id", "int", "pk");

        Assert.Null(edit.Refusal);
        Assert.EndsWith("\n    ORDER {\n        int id PK\n    }", edit.Text);
    }

    [Fact]
    public void SetAttribute_WithAKeyThatIsNotOne_IsRefused_RatherThanWrittenIntoTheSource()
    {
        var edit = DiagramObjectEdit.SetAttribute(Source, "ORDER", "total", "int", "PRIMARY");

        Assert.Null(edit.Text);
        Assert.Contains("PK, FK or UK", edit.Refusal);
    }

    [Fact]
    public void SetAttribute_WithATypeThatIsNotOneWord_IsRefused()
    {
        var edit = DiagramObjectEdit.SetAttribute(Source, "ORDER", "total", "decimal number", key: null);

        Assert.Null(edit.Text);
        Assert.Contains("one word", edit.Refusal);
    }

    [Fact]
    public void RemoveAttribute_TakesOnlyThatLine()
    {
        var edit = DiagramObjectEdit.RemoveAttribute(Source, "CUSTOMER", "name");

        Assert.Null(edit.Refusal);
        Assert.DoesNotContain("string name", edit.Text, StringComparison.Ordinal);
        Assert.Contains("int id PK", edit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveAttribute_ThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.RemoveAttribute(Source, "CUSTOMER", "total");

        Assert.Null(edit.Text);
        Assert.Contains("no attribute", edit.Refusal);
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
    public void Relate_WithoutALabel_IsRefused_BecauseMermaidDrawsThatVerb()
    {
        var edit = DiagramObjectEdit.Relate(Source, "ORDER", "CUSTOMER", DiagramErCardinality.One, DiagramErCardinality.One, "  ");

        Assert.Null(edit.Text);
        Assert.Contains("needs a label", edit.Refusal);
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
    public void Unrelate_ThatIsNotThere_IsRefused()
    {
        var edit = DiagramObjectEdit.Unrelate(Source, "ORDER", "CUSTOMER");

        Assert.Null(edit.Text);
        Assert.Contains("no ORDER -> CUSTOMER relationship", edit.Refusal);
    }

    [Fact]
    public void Attributes_ReadsTheBlockBackInSourceOrder()
    {
        var attributes = DiagramObjectEdit.Attributes(Source, "CUSTOMER");

        Assert.Collection(
            attributes,
            first => Assert.Equal(new DiagramErAttribute("string", "name", null), first),
            second => Assert.Equal(new DiagramErAttribute("int", "id", "PK"), second));
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

    [Fact]
    public void AnErCall_OnADiagramWithNeitherGrammar_PointsAtEditDiagram()
    {
        var edit = DiagramObjectEdit.AddEntity("sequenceDiagram\n    Alice->>Bob: Hello", "CUSTOMER");

        Assert.Null(edit.Text);
        Assert.Contains("edit_diagram", edit.Refusal);
    }
}
