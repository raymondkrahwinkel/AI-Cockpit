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

    // Every in-place ER handling, reverted on its own, against the whole source rather than the one line it touched:
    // an entity block is several lines and two entities can hold the same one, so a revert writing the right lines in
    // the wrong order is what this catches and a Contains does not. Removals append (see below), so they sit apart.
    public static TheoryData<DiagramHandEdit> RevertedErHandEdits() =>
    [
        new DiagramHandEdit(DiagramHandEditKind.RenameEntity, "CUSTOMER", Label: "CLIENT"),
        new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "ORDER") { Attribute = "total", AttributeType = "int" },
        new DiagramHandEdit(DiagramHandEditKind.SetAttribute, "CUSTOMER") { Attribute = "name", AttributeType = "varchar(50)" },
        new DiagramHandEdit(DiagramHandEditKind.RemoveAttribute, "CUSTOMER") { Attribute = "id" },
        new DiagramHandEdit(DiagramHandEditKind.Relate, "CUSTOMER", "ORDER", "owns")
        {
            FromCardinality = DiagramErCardinality.OneOrMore,
            ToCardinality = DiagramErCardinality.One,
        },
        new DiagramHandEdit(DiagramHandEditKind.Relate, "ORDER", "CUSTOMER", "belongs to")
        {
            FromCardinality = DiagramErCardinality.ZeroOrMore,
            ToCardinality = DiagramErCardinality.One,
        },
    ];

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
