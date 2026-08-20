using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-911 criterion 4: every wireframe type picker template parses without error and produces at least one screen
// — Parse itself resolves every goto:, so a dangling one shows up here as a parse error, not a broken preview.
// SurfaceTemplate is internal, so one [Fact] loops the list instead of a [Theory] taking it as a parameter.
public class WireframeTemplatesTests
{
    [Fact]
    public void EveryTemplate_ParsesWithNoErrorsAndAtLeastOneScreen()
    {
        foreach (var template in WireframeTemplates.All)
        {
            var result = WireframeParser.Parse(template.Source);

            Assert.True(result.Errors.Count == 0, $"{template.Name}: {string.Join(", ", result.Errors)}");
            Assert.True(result.HasScreens, $"{template.Name} produced no screens");
        }
    }

    [Fact]
    public void Blank_IsExactlyWireframeDocumentEmpty()
    {
        Assert.Equal(WireframeDocument.Empty, WireframeTemplates.Blank.Source);
    }
}
