using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// Source → tree → controls → source has to come back unchanged (AC-871): the source text is what the operator
// sees in the read-only box, so a trip through the renderer may not quietly reword it.
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
        Assert.NotNull(result.Root);
        Assert.Equal(source, WireframeWriter.Write(result.Root));
    }

    [Theory]
    [MemberData(nameof(Screens))]
    public void Source_SurvivesTheControlsUnchanged(string screen)
    {
        var source = WireframeScreens.Source(screen);
        var root = WireframeParser.Parse(source).Root;
        Assert.NotNull(root);

        var carried = WireframeSource.GetNode(WireframeRenderer.Render(root));

        Assert.NotNull(carried);
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

        var root = WireframeParser.Parse(source).Root;

        Assert.NotNull(root);
        Assert.Equal(source, WireframeWriter.Write(root));
    }
}
