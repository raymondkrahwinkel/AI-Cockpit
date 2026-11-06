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
}
