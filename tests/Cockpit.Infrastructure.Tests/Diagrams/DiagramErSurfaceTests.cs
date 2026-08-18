using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-899 at the surface: an erDiagram gets the same guarantees a flowchart already had — the lock that keeps two
/// edits on different objects apart, the journal, the targeted revert, and controls that say why they are off.
/// </summary>
public class DiagramErSurfaceTests
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

    private static DiagramAccessRegistry Opened(string source = Source)
    {
        var registry = new DiagramAccessRegistry();
        registry.SurfaceOpened("surface-1", "Bestellingen", source);
        return registry;
    }

    private static string Reverted(DiagramAccessRegistry registry, DiagramHandEdit edit)
    {
        Assert.Null(registry.ApplyHandEdit("surface-1", edit));
        Assert.Null(registry.Revert("surface-1", registry.History("surface-1")[^1].Id));
        return registry.PeekText("surface-1")!;
    }

    [Fact]
    public void EditSupport_NamesTheDialect_SoThePanelKnowsWhichControlsBelongOnIt()
    {
        Assert.Equal(DiagramEditDialect.Er, Opened().EditSupport("surface-1").Dialect);
        Assert.Equal(DiagramEditDialect.Flowchart, Opened("flowchart LR\n    A[\"Start\"]").EditSupport("surface-1").Dialect);
    }

    [Fact]
    public void EditSupport_OnADialectWithNoGrammar_CarriesTheReasonToPutInTheTooltip()
    {
        var support = Opened("sequenceDiagram\n    Alice->>Bob: Hello").EditSupport("surface-1");

        Assert.Equal(DiagramEditDialect.Unsupported, support.Dialect);
        Assert.Contains("sequenceDiagram", support.Reason);
        Assert.Contains("agent", support.Reason);
    }

    [Fact]
    public void TwoEditsOnDifferentEntities_BothLand_NeitherOverwritingTheOther()
    {
        var registry = Opened();
        registry.Grant("agent-1", "surface-1", DiagramCapability.Edit);

        registry.EditCoupled("agent-1", "surface-1", DiagramHandEditKind.SetAttribute, "ORDER.total", source =>
        {
            var edit = DiagramObjectEdit.SetAttribute(source, "ORDER", "total", "int", key: null);
            return (edit.Text, edit.Summary);
        });
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "CUSTOMER") { Attribute = "email", AttributeType = "string" });

        var text = registry.PeekText("surface-1")!;
        Assert.Contains("int total", text, StringComparison.Ordinal);
        Assert.Contains("string email", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEditNamingAnEntityTheOperatorIsHolding_IsSeenAsHeld()
    {
        var registry = Opened();
        registry.HoldObject("surface-1", "CUSTOMER");

        Assert.True(registry.IsHeldByOperator("surface-1", "CUSTOMER"));
        Assert.False(registry.IsHeldByOperator("surface-1", "ORDER"));
    }

    [Fact]
    public void EveryErHandling_IsJournaledWithAKeyThatNamesItsObject()
    {
        var registry = Opened();

        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "ORDER") { Attribute = "total", AttributeType = "int" });
        registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.RenameEntity, "ORDER", Label: "PURCHASE"));

        Assert.Collection(
            registry.History("surface-1"),
            first => Assert.Equal("ORDER.total", first.ObjectKey),
            second => Assert.Equal("ORDER>PURCHASE", second.ObjectKey));
    }

    [Fact]
    public void Revert_OfARemovedEntity_PutsBackItsAttributesAndItsRelationship()
    {
        // An entity block is several lines, and two entities can hold the same line ("int id PK", "}") — this is
        // where a revert that puts back the wrong lines would show.
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.RemoveEntity, "CUSTOMER"));

        Assert.Contains("CUSTOMER ||--o{ ORDER : \"places\"", text, StringComparison.Ordinal);
        Assert.Contains("string name", text, StringComparison.Ordinal);
        Assert.Equal(2, DiagramObjectEdit.Attributes(text, "CUSTOMER").Count);
        Assert.Single(DiagramObjectEdit.Attributes(text, "ORDER"));
    }

    [Fact]
    public void Revert_OfARenamedEntity_PutsTheOldNameBackOnItsBlockAndItsRelationship()
    {
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.RenameEntity, "CUSTOMER", Label: "CLIENT"));

        Assert.Equal(Source.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Revert_OfAnAddedAttribute_TakesItOutAgain()
    {
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "ORDER") { Attribute = "total", AttributeType = "int" });

        Assert.Equal(Source.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Revert_OfAChangedAttribute_WritesTheOldTypeBack_NotADuplicateLine()
    {
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "CUSTOMER") { Attribute = "name", AttributeType = "varchar(50)" });

        Assert.Equal(Source.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Revert_OfARemovedAttribute_PutsItBackInsideItsOwnBlock()
    {
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.RemoveAttribute, "CUSTOMER") { Attribute = "id" });

        Assert.Collection(
            DiagramObjectEdit.Attributes(text, "CUSTOMER"),
            first => Assert.Equal(new DiagramErAttribute("string", "name", null), first),
            second => Assert.Equal(new DiagramErAttribute("int", "id", "PK"), second));
    }

    [Fact]
    public void Revert_OfAChangedRelationship_RestoresTheCardinalitiesAndTheLabel()
    {
        var edit = new DiagramHandEdit(DiagramHandEditKind.Relate, "CUSTOMER", "ORDER", "owns")
        {
            FromCardinality = DiagramErCardinality.OneOrMore,
            ToCardinality = DiagramErCardinality.One,
        };

        Assert.Equal(Source.ReplaceLineEndings("\n"), Reverted(Opened(), edit));
    }

    [Fact]
    public void Revert_OfANewRelationship_TakesTheLineOutAgain()
    {
        var edit = new DiagramHandEdit(DiagramHandEditKind.Relate, "ORDER", "CUSTOMER", "belongs to")
        {
            FromCardinality = DiagramErCardinality.ZeroOrMore,
            ToCardinality = DiagramErCardinality.One,
        };

        Assert.Equal(Source.ReplaceLineEndings("\n"), Reverted(Opened(), edit));
    }

    [Fact]
    public void Revert_OfARemovedRelationship_PutsItBack()
    {
        var text = Reverted(Opened(), new DiagramHandEdit(DiagramHandEditKind.Unrelate, "CUSTOMER", "ORDER"));

        Assert.Contains("CUSTOMER ||--o{ ORDER : \"places\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandEdit_OnADialectWithNoGrammar_IsRefused_WithTheSourceLeftAsItWas()
    {
        const string sequence = "sequenceDiagram\n    Alice->>Bob: Hello";
        var registry = Opened(sequence);

        var refusal = registry.ApplyHandEdit("surface-1", new DiagramHandEdit(DiagramHandEditKind.AddEntity, "CUSTOMER"));

        Assert.NotNull(refusal);
        Assert.Equal(sequence, registry.PeekText("surface-1"));
        Assert.Empty(registry.History("surface-1"));
    }
}
