using Cockpit.Core.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-911 criterion 4: every wireframe type picker template parses without error and produces at least one screen
// — Parse itself resolves every goto:, so a dangling one shows up here as a parse error, not a broken preview.
// `SurfaceTemplate` is internal (CS0051), so the rows carry the fields read here — one case per template.
public class WireframeTemplatesTests
{
    public static IEnumerable<object[]> Templates() =>
        WireframeTemplates.All.Select(template => new object[] { template.Name, template.Source });

    [Theory]
    [MemberData(nameof(Templates))]
    public void EveryTemplate_ParsesWithNoErrorsAndAtLeastOneScreen(string name, string source)
    {
        var result = WireframeParser.Parse(source);

        Assert.True(result.Errors.Count == 0, $"{name}: {string.Join(", ", result.Errors)}");
        Assert.True(result.HasScreens, $"{name} produced no screens");
    }

    [Fact]
    public void Blank_IsExactlyWireframeDocumentEmpty()
    {
        Assert.Equal(WireframeDocument.Empty, WireframeTemplates.Blank.Source);
    }
}
