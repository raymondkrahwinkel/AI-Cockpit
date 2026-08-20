using Cockpit.Core.Wireframe;
using Cockpit.Core.Wireframe.Model;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// Source → tree → controls → source has to come back unchanged (AC-871): the source text is what the operator
// sees in the read-only box, so a trip through the renderer may not quietly reword it. AC-901 adds the multi-screen
// document to the same measure — one blank line between screens is the canonical form.
[Collection("avalonia")]
public class WireframeRoundTripTests
{
    public static TheoryData<string> Screens => WireframeScreens.Names;

    [Theory]
    [MemberData(nameof(Screens))]
    public void Source_SurvivesTheTreeUnchanged(string screen)
    {
        var source = WireframeScreens.Source(screen);
        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Screens);
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Source_SurvivesTheControlsUnchanged(string screen)
    {
        var source = WireframeScreens.Source(screen);
        var carried = new List<WireframeNode>();

        foreach (var parsed in WireframeParser.Parse(source).Screens)
        {
            var node = WireframeSource.GetNode(WireframeRenderer.Render(parsed));
            Assert.NotNull(node);
            carried.Add(node);
        }

        Assert.Equal(source, WireframeWriter.Write(carried));
    }

    [Fact]
    public void AQuoteInsideALabel_SurvivesBothDirections()
    {
        const string source = """
            screen "X"
              label "Zeg \"hallo\""
              input "Naam" value:"Raymond"
            """;

        var root = WireframeParser.Parse(source).Screens.SingleOrDefault();

        Assert.NotNull(root);
        Assert.Equal(source, WireframeWriter.Write(root));
    }

    // AC-914 criterion 5: a screen carrying states round-trips character for character, same as any other document
    // — kept out of the shared gallery above because RendererTests.EveryNode_IsCarriedByExactlyOneControl assumes
    // every model node gets a control, which a state deliberately never does (see WireframeRendererTests).
    [Fact]
    public void AScreenWithStates_SurvivesTheTreeUnchanged()
    {
        const string source = """
            screen "Search results"
              main w:4
                list #results
                  item "Result 1"
              state "Empty" replaces:#results
                label "No results found"
                button "Clear filters" primary
              state "Loading" replaces:#results
                space h:3
            """;

        var result = WireframeParser.Parse(source);

        Assert.Empty(result.Errors);
        Assert.Equal(source, WireframeWriter.Write(result.Screens));
    }
}
