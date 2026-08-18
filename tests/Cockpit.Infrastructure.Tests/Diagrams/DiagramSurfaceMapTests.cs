using Cockpit.Core.Diagrams;
using Cockpit.Infrastructure.Diagrams;

namespace Cockpit.Infrastructure.Tests.Diagrams;

/// <summary>
/// AC-841's first acceptance criterion, kept measured rather than remembered: a click on the render leads back to a
/// place in the source. Everything here goes through the real Mermaider 0.12.2 render, so the day its markers change
/// these fail instead of the surface quietly selecting nothing.
/// </summary>
public class DiagramSurfaceMapTests
{
    private const string Source = """
        flowchart TD
            Start["Begin hier"] --> Check{"Klaar?"}
            Check -->|ja| Done["Af"]
            Check -->|nee| Work["Werk door"]
        """;

    [Fact]
    public void ClickInsideANode_NamesTheIdTheSourceUses()
    {
        var objects = DiagramSurfaceMap.Read(_Render(Source));

        foreach (var id in new[] { "Start", "Check", "Done", "Work" })
        {
            var node = objects.Single(o => o.Kind == DiagramObjectAt.Node && o.Id == id);
            Assert.Equal(node, DiagramSurfaceMap.At(objects, node.Bounds.Center));
        }
    }

    [Fact]
    public void ClickInsideAnErEntity_NamesTheEntity_BecauseItIsDrawnUnderItsOwnMarkers()
    {
        // AC-899: Mermaider tags an ER box class="entity" and an ER line class="er-relationship" with
        // data-entity1/2 — different markers for the same two roles, so the map has to know both spellings.
        const string source = """
            erDiagram
                CUSTOMER ||--o{ ORDER : "places"
                CUSTOMER {
                    string name
                }
            """;

        var objects = DiagramSurfaceMap.Read(_Render(source));

        var entity = objects.Single(o => o.Kind == DiagramObjectAt.Node && o.Id == "CUSTOMER");
        Assert.Equal(entity, DiagramSurfaceMap.At(objects, entity.Bounds.Center));
        var relationship = objects.Single(o => o.Kind == DiagramObjectAt.Edge);
        Assert.Equal("CUSTOMER->ORDER", relationship.HoldKey);
    }

    [Fact]
    public void ClickOnAConnection_NamesBothItsEnds()
    {
        var objects = DiagramSurfaceMap.Read(_Render(Source));
        var edge = objects.Single(o => o.Kind == DiagramObjectAt.Edge && o.Id == "Start" && o.To == "Check");

        // Halfway along the line it actually draws — not its bounding box, which an L-shaped connection shares with
        // half the diagram.
        var start = edge.Line[0];
        var end = edge.Line[1];
        var hit = DiagramSurfaceMap.At(objects, new DiagramPoint((start.X + end.X) / 2, (start.Y + end.Y) / 2));

        Assert.Equal("Start->Check", hit!.HoldKey);
    }

    [Fact]
    public void ClickOnEmptySpace_SelectsNothing()
    {
        var svg = _Render(Source);
        var objects = DiagramSurfaceMap.Read(svg);

        Assert.Null(DiagramSurfaceMap.At(objects, new DiagramPoint(DiagramSurfaceMap.Width(svg) - 2, 2)));
    }

    [Fact]
    public void EveryShapeMermaidDraws_StillHasBoundsToClickOn()
    {
        const string shapes = """
            flowchart TD
                A["rect"] --> B(["stadium"])
                B --> C(("circle"))
                C --> D{{"hexagon"}}
                D --> E[("cylinder")]
                E --> F{"diamond"}
            """;

        var objects = DiagramSurfaceMap.Read(_Render(shapes));

        Assert.Equal(6, objects.Count(o => o.Kind == DiagramObjectAt.Node));
        Assert.All(objects.Where(o => o.Kind == DiagramObjectAt.Node), node =>
        {
            Assert.True(node.Bounds.Width > 0 && node.Bounds.Height > 0);
            Assert.Equal(node.Id, DiagramSurfaceMap.At(objects, node.Bounds.Center)?.Id);
        });
    }

    [Fact]
    public void MarkupThatIsNotSvg_ReadsAsNothingRatherThanThrowing()
    {
        Assert.Empty(DiagramSurfaceMap.Read("not svg at all"));
        Assert.Equal(0, DiagramSurfaceMap.Width("not svg at all"));
    }

    private static string _Render(string source) => MermaidRenderPipeline.Render(source, MermaidTheme.Neutral).Svg.Markup;
}
